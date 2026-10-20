using System.Text.Json.Nodes;
using Isoform.Document;
using Isoform.Meshing;
using Isoform.Modeling;
using Xunit.Abstractions;

namespace Isoform.Tests;

/// <summary>K4: smooth blends on join and cut, and offsets that move every face and keep its name.</summary>
public class BlendOffsetTests(ITestOutputHelper output)
{
    static readonly Block Plate = new("plate", BooleanOp.New, [0, 0, 0], [100, 50, 10]);
    static readonly SketchPlane Top = new([0, 0, 10], [1, 0, 0], [0, 0, 1]);
    static readonly FrepModeler Modeler = new(new ModelingSettings { CellSize = 0.25 });
    const double VolumeTolerance = 1e-4;

    static PartDocument BlendedBoss(double k) => new(
    [
        Plate,
        new Extrude("boss", BooleanOp.Join, Top, new Profile([45, 25], [new ProfileEdge("a", [55, 25], 1), new ProfileEdge("b", [45, 25], 1)]), 0, 8, k),
    ]);

    static PartDocument BlendedPocket(double k) => new([Plate, new Block("pocket", BooleanOp.Cut, [30, 15, 5], [40, 20, 10], k)]);

    static PartDocument OffsetPlate(double d) => new([Plate, new Hole("hole", [30, 25], 10), new Offset("grow", d)]);

    static PartDocument OffsetHexagon(double d) => new(
    [
        new Extrude("hex", BooleanOp.New, new SketchPlane([0, 0, 0], [1, 0, 0], [0, 0, 1]),
            new Profile([10, 0], Enumerable.Range(1, 6).Select(k => new ProfileEdge($"s{k}", [10 * Math.Cos(k * Math.PI / 3), 10 * Math.Sin(k * Math.PI / 3)])).ToList()), 0, 20),
        new Offset("grow", d),
    ]);

    [Fact]
    public void Smooth_join_adds_the_blend_band_and_nothing_else()
    {
        const double k = 2, r = 5;
        var result = Modeler.Regenerate(BlendedBoss(k));

        result.Diagnostics.Should().BeEmpty();
        result.Mesh!.IsWatertight().Should().BeTrue();
        result.Topology!.Faces.SelectMany(f => f.Sources).Should().Contain("boss/blend");
        // Round the boss foot, u above the plate and v out from the wall, the smooth min is below zero where
        // min(u, v) < (k − |u − v|)²/4k: that band, swept round the circle at radius r + v, is the extra material.
        var band = Integrate2(k, (u, v) => Math.Min(u, v) < Math.Pow(Math.Max(k - Math.Abs(u - v), 0), 2) / (4 * k) ? 2 * Math.PI * (r + v) : 0);
        var exact = 100 * 50 * 10 + Math.PI * r * r * 8 + band;
        var volume = SignedVolume(result.Mesh);
        output.WriteLine($"volume {volume:F3} vs {exact:F3} (band {band:F3})");
        volume.Should().BeApproximately(exact, exact * VolumeTolerance);
        MaxVertexError(result).Should().BeLessThan(1e-7);
    }

    [Fact]
    public void Smooth_cut_rounds_the_rim_and_removes_more_than_the_sharp_cut()
    {
        var sharp = SignedVolume(Modeler.Regenerate(BlendedPocket(0)).Mesh!);
        var result = Modeler.Regenerate(BlendedPocket(2));

        result.Diagnostics.Should().BeEmpty();
        result.Mesh!.IsWatertight().Should().BeTrue();
        result.Topology!.Faces.SelectMany(f => f.Sources).Should().Contain("pocket/blend");
        var removed = sharp - SignedVolume(result.Mesh);
        output.WriteLine($"extra material removed {removed:F3}");
        // The band round the 120 mm rim is at most k²/4 wide in each direction.
        removed.Should().BeInRange(1, 120 * 2 * 2 / 4.0 * 2);
        MaxVertexError(result).Should().BeLessThan(1e-7);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void Offset_moves_every_face_and_keeps_its_name(double d)
    {
        var result = Modeler.Regenerate(OffsetPlate(d));

        result.Diagnostics.Should().BeEmpty();
        var exact = (100 + 2 * d) * (50 + 2 * d) * (10 + 2 * d) - Math.PI * Math.Pow(5 - d, 2) * (10 + 2 * d);
        SignedVolume(result.Mesh!).Should().BeApproximately(exact, exact * VolumeTolerance);
        result.Topology!.Faces.SelectMany(f => f.Sources).Should().BeEquivalentTo(
            ["hole/wall", "plate/x+", "plate/x-", "plate/y+", "plate/y-", "plate/z+", "plate/z-"]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void Offset_grows_or_shrinks_fillet_radii_with_the_faces(double d)
    {
        var result = Modeler.Regenerate(new PartDocument([.. EditSuite.Base.Features, new Offset("grow", d)]));

        result.Diagnostics.Should().BeEmpty();
        var r = 2 + d;
        var exact = (100 + 2 * d) * (50 + 2 * d) * (10 + 2 * d) - Math.PI * Math.Pow(5 - d, 2) * (10 + 2 * d) - r * r * (1 - Math.PI / 4) * (50 + 2 * d);
        SignedVolume(result.Mesh!).Should().BeApproximately(exact, exact * VolumeTolerance);
        result.Part.Surfaces.Last(s => s.Source == "fillet/face").Radius.Should().Be(r);
    }

    [Fact]
    public void Grown_profile_corners_are_round()
    {
        const double d = 1;
        var result = Modeler.Regenerate(OffsetHexagon(d));

        result.Diagnostics.Should().BeEmpty();
        // Grown convex polygon: area A + P·d + πd², between caps moved out by d.
        var (area, perimeter) = (1.5 * Math.Sqrt(3) * 100, 60.0);
        var exact = (area + perimeter * d + Math.PI * d * d) * (20 + 2 * d);
        SignedVolume(result.Mesh!).Should().BeApproximately(exact, exact * VolumeTolerance);
        MaxVertexError(result).Should().BeLessThan(1e-7);
        // Each corner is a face of its own, tangent to its two sides: 6 sides, 6 corners and 2 caps.
        (result.Topology!.Faces.Count, result.Topology.Edges.Count, result.Topology.Vertices.Count).Should().Be((14, 36, 24));
        result.Topology.Faces.SelectMany(f => f.Sources).Should().Contain("grow/corner:hex/s1+hex/s2");
    }

    [Fact]
    public void An_offset_part_can_be_shelled_but_a_shell_cannot_be_offset()
    {
        var shelled = Modeler.Regenerate(new PartDocument([Plate, new Offset("grow", 1), new Shell("shell", 1.5)]));
        shelled.Diagnostics.Should().BeEmpty();
        shelled.Mesh!.IsWatertight().Should().BeTrue();

        var rejected = Modeler.Regenerate(new PartDocument([Plate, new Shell("shell", 1.5), new Offset("grow", 1)]));
        rejected.Diagnostics.Should().ContainSingle().Which.Message.Should().Contain("offset before the shell");
    }

    public static IEnumerable<object[]> Parts => [["blended boss"], ["blended pocket"], ["offset plate"], ["offset fillet"], ["offset hexagon"]];

    static PartDocument Part(string name) => name switch
    {
        "blended boss" => BlendedBoss(2),
        "blended pocket" => BlendedPocket(2),
        "offset plate" => OffsetPlate(-1),
        "offset fillet" => new([.. EditSuite.Base.Features, new Offset("grow", 1)]),
        _ => OffsetHexagon(1),
    };

    [Theory]
    [MemberData(nameof(Parts))]
    public void Field_is_1_lipschitz_bounded_and_simplifies_exactly(string name) =>
        FieldProperties.Check(new Compiler(new ModelingSettings()).Compile(Part(name)));

    [Fact]
    public void Faces_keep_their_names_when_blend_and_offset_change()
    {
        (PartDocument Doc, JsonNode Edit)[] cases =
        [
            (BlendedBoss(2), JsonNode.Parse("""{ "set": "boss.blend", "value": 3 }""")!),
            (OffsetPlate(1), JsonNode.Parse("""{ "set": "grow.distance", "value": 2 }""")!),
        ];
        foreach (var (doc, edit) in cases)
        {
            var before = Modeler.Regenerate(doc).Topology!;
            var after = Modeler.Regenerate(DocumentEdits.Apply(doc, [edit]));
            after.Diagnostics.Should().BeEmpty();
            foreach (var source in before.Faces.SelectMany(f => f.Sources))
            {
                var reference = new FaceReference(source, source);
                ReferenceResolver.Resolve(reference, after.Topology!).Status.Should().Be(ReferenceResolver.Resolve(reference, before).Status, source);
            }
        }
    }

    [Fact]
    public void Blends_and_offsets_round_trip_through_the_canonical_format()
    {
        foreach (var doc in new[] { BlendedBoss(2), BlendedPocket(1.5), OffsetPlate(-0.5) })
        {
            var text = CanonicalFormat.Write(doc);
            CanonicalFormat.Write(CanonicalFormat.Read(text)).Should().Be(text);
        }
        CanonicalFormat.Write(BlendedPocket(0)).Should().NotContain("blend");
    }

    static double MaxVertexError(FrepResult r) => r.Mesh!.Vertices.Max(p => Math.Abs(r.Part.Field!.Value(p)));

    /// <summary>Simpson's rule over [0, k]².</summary>
    static double Integrate2(double k, Func<double, double, double> f, int n = 1000)
    {
        var h = k / n;
        double W(int i) => i == 0 || i == n ? 1 : i % 2 == 1 ? 4 : 2;
        var sum = 0.0;
        for (var i = 0; i <= n; i++)
        for (var j = 0; j <= n; j++)
            sum += W(i) * W(j) * f(i * h, j * h);
        return sum * h * h / 9;
    }

    static double SignedVolume(Mesh mesh)
    {
        var v = mesh.Vertices;
        return mesh.Triangles().Sum(t => v[t.A].AsVector().Dot(v[t.B].AsVector().Cross(v[t.C].AsVector()))) / 6;
    }
}
