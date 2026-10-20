using System.Text.Json.Nodes;
using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Geometry;
using Isoform.Meshing;
using Isoform.Modeling;
using Xunit.Abstractions;

namespace Isoform.Tests;

/// <summary>K4: extrude and revolve from line-and-arc profiles, with plane, cylinder, cone, sphere and torus faces.</summary>
public class SketchFeatureTests(ITestOutputHelper output)
{
    static readonly SketchPlane Xy = new([0, 0, 0], [1, 0, 0], [0, 0, 1]);

    // Profile x = distance from the world Z axis, profile y = height: the sketch's y axis is world Z.
    static readonly SketchPlane Xz = new([0, 0, 0], [1, 0, 0], [0, -1, 0]);

    static readonly double QuarterBulge = Math.Tan(Math.PI / 8);

    static Profile Loop(double[] start, params (string Id, double U, double V, double Bulge)[] edges) =>
        new(start, edges.Select(e => new ProfileEdge(e.Id, [e.U, e.V], e.Bulge)).ToList());

    public sealed record Part(string Name, PartDocument Doc, double Volume, (int Faces, int Edges, int Vertices) Topology);

    public static IEnumerable<Part> Parts()
    {
        yield return new("L bracket", new([new Extrude("bracket", BooleanOp.New, Xy, Loop([0, 0],
                ("bottom", 40, 0, 0), ("tip", 40, 5, 0), ("inner", 5, 5, 0), ("riser", 5, 30, 0), ("top", 0, 30, 0), ("back", 0, 0, 0)), 0, 20)]),
            (40 * 5 + 5 * 25) * 20, (8, 18, 12));
        yield return new("stadium", new([new Extrude("slot", BooleanOp.New, Xy, Loop([0, 0],
                ("a", 20, 0, 0), ("right", 20, 10, 1), ("b", 0, 10, 0), ("left", 0, 0, 1)), 0, 5)]),
            (20 * 10 + Math.PI * 25) * 5, (6, 12, 8));
        yield return new("stepped shaft", new([new Revolve("shaft", BooleanOp.New, Xz, Loop([0, 0],
                ("bottom", 10, 0, 0), ("big", 10, 20, 0), ("step", 6, 20, 0), ("small", 6, 40, 0), ("top", 0, 40, 0), ("axis", 0, 0, 0)))]),
            Math.PI * (100 * 20 + 36 * 20), (5, 4, 0));
        yield return new("cone with a dome", new([new Revolve("nose", BooleanOp.New, Xz, Loop([0, 0],
                ("base", 10, 0, 0), ("side", 10, 10, 0), ("cone", 5, 20, 0), ("dome", 0, 25, QuarterBulge), ("axis", 0, 0, 0)))]),
            Math.PI * (1000 + 10.0 / 3 * (100 + 50 + 25) + 2.0 / 3 * 125), (4, 3, 0));
        yield return new("torus", new([new Revolve("ring", BooleanOp.New, Xz, Loop([20, -3], ("outer", 20, 3, 1), ("inner", 20, -3, 1)))]),
            2 * Math.PI * Math.PI * 20 * 9, (1, 0, 0));
        yield return new("quarter turn", new([new Revolve("elbow", BooleanOp.New, Xz, Loop([5, 0],
                ("low", 10, 0, 0), ("outer", 10, 4, 0), ("high", 5, 4, 0), ("inner", 5, 0, 0)), 90)]),
            0.25 * 2 * Math.PI * 7.5 * 20, (6, 12, 8));
        var hex = Enumerable.Range(1, 6).Select(k => ($"s{k}", 30 + 5 * Math.Cos(k * Math.PI / 3), 25 + 5 * Math.Sin(k * Math.PI / 3), 0.0)).ToArray();
        yield return new("hex hole", new(
            [
                new Block("plate", BooleanOp.New, [0, 0, 0], [60, 50, 10]),
                new Extrude("hex", BooleanOp.Cut, Xy, Loop([35, 25], hex), -1, 11),
            ]),
            60 * 50 * 10 - 1.5 * Math.Sqrt(3) * 25 * 10, (12, 30, 20));
    }

    public static IEnumerable<object[]> PartNames => Parts().Select(p => new object[] { p.Name });

    static Part Get(string name) => Parts().Single(p => p.Name == name);

    [Theory]
    [MemberData(nameof(PartNames))]
    public void Part_has_the_exact_volume_and_topology(string name)
    {
        var part = Get(name);
        var result = new FrepModeler(new ModelingSettings { CellSize = 0.25 }).Regenerate(part.Doc);
        var t = result.Topology!;
        var volume = SignedVolume(result.Mesh!);
        output.WriteLine($"{name}: {t.Faces.Count} faces, {t.Edges.Count} edges, {t.Vertices.Count} vertices; volume {volume:F3} vs {part.Volume:F3}");
        foreach (var f in t.Faces) output.WriteLine($"  face {string.Join(", ", f.Sources)}");

        result.Diagnostics.Should().BeEmpty();
        result.Mesh!.IsWatertight().Should().BeTrue();
        (t.Faces.Count, t.Edges.Count, t.Vertices.Count).Should().Be(part.Topology);
        t.BodyCount.Should().Be(1);
        volume.Should().BeApproximately(part.Volume, part.Volume * 1e-3);
    }

    [Theory]
    [MemberData(nameof(PartNames))]
    public void Field_is_1_lipschitz_bounded_and_simplifies_exactly(string name) =>
        FieldProperties.Check(new Compiler(new ModelingSettings()).Compile(Get(name).Doc));

    [Fact]
    public void Faces_keep_their_names_when_the_profile_changes()
    {
        var modeler = new FrepModeler(new ModelingSettings { CellSize = 0.25 });
        (PartDocument Doc, JsonNode Edit)[] cases =
        [
            (Get("L bracket").Doc, JsonNode.Parse("""{ "set": "bracket.profile.edges.2.to", "value": [8, 5] }""")!),
            (Get("stadium").Doc, JsonNode.Parse("""{ "set": "slot.profile.edges.1.bulge", "value": 0.8 }""")!),
            (Get("cone with a dome").Doc, JsonNode.Parse("""{ "set": "nose.profile.edges.2.to", "value": [4, 18] }""")!),
            (Get("quarter turn").Doc, JsonNode.Parse("""{ "set": "elbow.angle", "value": 200 }""")!),
        ];
        foreach (var (doc, edit) in cases)
        {
            var before = modeler.Regenerate(doc).Topology!;
            var after = modeler.Regenerate(DocumentEdits.Apply(doc, [edit])).Topology!;
            foreach (var source in before.Faces.SelectMany(f => f.Sources))
                ReferenceResolver.Resolve(new FaceReference(source, source), after).Status
                    .Should().Be(ReferenceStatus.Resolved, $"'{source}' after {edit.ToJsonString()}");
            (after.Faces.Count, after.Edges.Count).Should().Be((before.Faces.Count, before.Edges.Count));
        }
    }

    [Fact]
    public void Clockwise_profiles_give_the_same_solid()
    {
        var ccw = Get("L bracket").Doc;
        var e = (Extrude)ccw.Features[0];
        var points = new[] { e.Profile.Start }.Concat(e.Profile.Edges.Select(x => x.To)).ToList();
        // Walk the same corners backwards: each edge keeps its id and its own two corners.
        var reversed = new Profile(points[0], Enumerable.Range(0, e.Profile.Edges.Count).Reverse()
            .Select(i => new ProfileEdge(e.Profile.Edges[i].Id, points[i])).ToList());
        var cw = new PartDocument([e with { Profile = reversed }]);
        var settings = new ModelingSettings { CellSize = 0.5 };

        var (a, b) = (new FrepModeler(settings).Regenerate(ccw), new FrepModeler(settings).Regenerate(cw));

        b.Diagnostics.Should().BeEmpty();
        b.Topology!.Faces.Select(f => string.Join(",", f.Sources)).Order().Should().Equal(a.Topology!.Faces.Select(f => string.Join(",", f.Sources)).Order());
        SignedVolume(b.Mesh!).Should().BeApproximately(SignedVolume(a.Mesh!), 1e-6);
    }

    [Fact]
    public void Sketch_features_round_trip_through_the_canonical_format()
    {
        foreach (var part in Parts())
        {
            var text = CanonicalFormat.Write(part.Doc);
            CanonicalFormat.Write(CanonicalFormat.Read(text)).Should().Be(text);
        }
        CanonicalFormat.Write(Get("L bracket").Doc).Should().NotContain("bulge", "lines carry no bulge key");
    }

    [Theory]
    [InlineData("open", "not closed")]
    [InlineData("crossing", "cross")]
    [InlineData("across the axis", "one side of the axis")]
    [InlineData("duplicate ids", "unique")]
    public void Bad_profiles_are_rejected(string kind, string message)
    {
        Feature feature = kind switch
        {
            "open" => new Extrude("x", BooleanOp.New, Xy, Loop([0, 0], ("a", 10, 0, 0), ("b", 10, 10, 0), ("c", 0, 9, 0)), 0, 5),
            "crossing" => new Extrude("x", BooleanOp.New, Xy, Loop([0, 0], ("a", 10, 10, 0), ("b", 10, 0, 0), ("c", 0, 6, 0), ("d", 0, 0, 0)), 0, 5),
            "across the axis" => new Revolve("x", BooleanOp.New, Xz, Loop([-2, 0], ("a", 10, 0, 0), ("b", 10, 5, 0), ("c", -2, 0, 0))),
            _ => new Extrude("x", BooleanOp.New, Xy, Loop([0, 0], ("a", 10, 0, 0), ("a", 10, 10, 0), ("c", 0, 0, 0)), 0, 5),
        };

        var result = new FrepModeler(new ModelingSettings()).Regenerate(new PartDocument([feature]));

        result.Topology.Should().BeNull();
        result.Diagnostics.Should().ContainSingle().Which.Message.Should().Contain(message);
    }

    static double SignedVolume(Mesh mesh)
    {
        var v = mesh.Vertices;
        return mesh.Triangles().Sum(t => v[t.A].AsVector().Dot(v[t.B].AsVector().Cross(v[t.C].AsVector()))) / 6;
    }
}
