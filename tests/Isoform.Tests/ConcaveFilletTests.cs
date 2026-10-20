using System.Text.Json.Nodes;
using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Meshing;
using Isoform.Modeling;
using Xunit.Abstractions;

namespace Isoform.Tests;

/// <summary>K4: fillets that add material (inside corners of cuts, joins and profiles) and corner rounds of profiles.</summary>
public class ConcaveFilletTests(ITestOutputHelper output)
{
    static readonly Block Plate = new("plate", BooleanOp.New, [0, 0, 0], [100, 50, 10]);
    static readonly SketchPlane Xy = new([0, 0, 0], [1, 0, 0], [0, 0, 1]);
    static readonly SketchPlane Xz = new([0, 0, 0], [1, 0, 0], [0, -1, 0]);
    static readonly FrepModeler Modeler = new(new ModelingSettings { CellSize = 0.25 });
    const double VolumeTolerance = 1e-4;
    const double R = 2;
    static readonly double Corner = R * R * (1 - Math.PI / 4);

    static PartDocument Pocket(params string[] walls) => new(
    [
        Plate,
        new Block("pocket", BooleanOp.Cut, [30, 15, 5], [40, 20, 10]),
        .. walls.Select((w, i) => new Fillet($"f{i}", ["pocket/z-", $"pocket/{w}"], R)),
    ]);

    static PartDocument BoxBoss(double height = 8) => new(
    [
        Plate,
        new Block("boss", BooleanOp.Join, [40, 20, 10], [20, 10, height]),
        new Fillet("foot", ["plate/z+", "boss/x+"], R),
    ]);

    static PartDocument RoundBoss() => new(
    [
        Plate,
        new Extrude("boss", BooleanOp.Join, new SketchPlane([0, 0, 10], [1, 0, 0], [0, 0, 1]),
            new Profile([45, 25], [new ProfileEdge("a", [55, 25], 1), new ProfileEdge("b", [45, 25], 1)]), 0, 8),
        new Fillet("foot", ["plate/z+", "boss/a"], R),
    ]);

    static Profile Rectangle => new([0, 0], [new ProfileEdge("a", [60, 0]), new ProfileEdge("b", [60, 40]), new ProfileEdge("c", [0, 40]), new ProfileEdge("d", [0, 0])]);

    static PartDocument Outline(Feature corner) => new([new Extrude("outline", BooleanOp.New, Xy, Rectangle, 0, 10), corner]);

    static PartDocument Bracket() => new(
    [
        new Extrude("bracket", BooleanOp.New, Xy, new Profile([0, 0],
        [
            new ProfileEdge("bottom", [40, 0]), new ProfileEdge("tip", [40, 5]), new ProfileEdge("inner", [5, 5]),
            new ProfileEdge("riser", [5, 30]), new ProfileEdge("top", [0, 30]), new ProfileEdge("back", [0, 0]),
        ]), 0, 20),
        new Fillet("inside", ["bracket/inner", "bracket/riser"], R),
    ]);

    static PartDocument Shaft() => new(
    [
        new Revolve("shaft", BooleanOp.New, Xz, new Profile([0, 0],
        [
            new ProfileEdge("bottom", [10, 0]), new ProfileEdge("big", [10, 20]), new ProfileEdge("step", [6, 20]),
            new ProfileEdge("small", [6, 40]), new ProfileEdge("top", [0, 40]), new ProfileEdge("axis", [0, 0]),
        ])),
        new Fillet("undercut", ["shaft/step", "shaft/small"], 1),
    ]);

    [Fact]
    public void Fillet_inside_a_pocket_adds_the_corner_material()
    {
        var result = Modeler.Regenerate(Pocket("x-"));

        result.Diagnostics.Should().BeEmpty();
        Volume(result, 100 * 50 * 10 - 40 * 20 * 5 + Corner * 20);
        Deviation(result, "f0/face", p => Math.Sqrt(Sq(p.X - 32) + Sq(p.Z - 7)) - R).Should().BeLessThan(1e-7);
    }

    [Fact]
    public void Fillets_round_the_whole_pocket_floor_and_mitre_at_its_corners()
    {
        var result = Modeler.Regenerate(Pocket("x-", "y-", "x+", "y+"));

        result.Diagnostics.Should().BeEmpty();
        result.Mesh!.IsWatertight().Should().BeTrue();
        // As on the outside, the corner regions of two fillets overlap by ∫ w(h)² dh.
        var overlap = Integrate(h => Math.Pow(R - Math.Sqrt(R * R - h * h), 2), 0, R);
        Volume(result, 100 * 50 * 10 - 40 * 20 * 5 + Corner * 120 - 4 * overlap);
        MaxVertexError(result).Should().BeLessThan(1e-7);
    }

    [Fact]
    public void Fillet_at_the_foot_of_a_joined_boss_ends_flush_with_its_sides()
    {
        var result = Modeler.Regenerate(BoxBoss());

        result.Diagnostics.Should().BeEmpty();
        result.Mesh!.IsWatertight().Should().BeTrue();
        Volume(result, 100 * 50 * 10 + 20 * 10 * 8 + Corner * 10);
        Deviation(result, "foot/face", p => Math.Sqrt(Sq(p.X - 62) + Sq(p.Z - 12)) - R).Should().BeLessThan(1e-7);
        foreach (var side in new[] { "boss/y-", "boss/y+", "plate/z+", "boss/x+" })
            ReferenceResolver.Resolve(new EdgeReference(side, "foot/face", side), result.Topology!).Status.Should().Be(ReferenceStatus.Resolved, side);
    }

    [Fact]
    public void Fillet_round_a_cylindrical_boss_is_a_torus()
    {
        const double r = 5;
        var result = Modeler.Regenerate(RoundBoss());

        result.Diagnostics.Should().BeEmpty();
        // Cross-section: the square of side R at the foot less the quarter disc, swept round at radius r + v.
        var ring = Integrate2(R, (u, v) => Sq(v - R) + Sq(u - R) > R * R ? 2 * Math.PI * (r + v) : 0);
        Volume(result, 100 * 50 * 10 + Math.PI * r * r * 8 + ring);
        Deviation(result, "foot/face", p => Math.Sqrt(Sq(Math.Sqrt(Sq(p.X - 50) + Sq(p.Y - 25)) - (r + R)) + Sq(p.Z - 12)) - R).Should().BeLessThan(1e-7);
    }

    [Fact]
    public void Profile_corners_round_convex_and_reflex_alike()
    {
        var convex = Modeler.Regenerate(Outline(new Fillet("round", ["outline/a", "outline/b"], 5)));
        convex.Diagnostics.Should().BeEmpty();
        Volume(convex, 60 * 40 * 10 - 25 * (1 - Math.PI / 4) * 10);
        Deviation(convex, "round/face", p => Math.Sqrt(Sq(p.X - 55) + Sq(p.Y - 5)) - 5).Should().BeLessThan(1e-7);

        var reflex = Modeler.Regenerate(Bracket());
        reflex.Diagnostics.Should().BeEmpty();
        Volume(reflex, (40 * 5 + 5 * 25) * 20 + Corner * 20);
        Deviation(reflex, "inside/face", p => Math.Sqrt(Sq(p.X - 7) + Sq(p.Y - 7)) - R).Should().BeLessThan(1e-7);

        var chamfered = Modeler.Regenerate(Outline(new Chamfer("cut", ["outline/a", "outline/b"], 3)));
        chamfered.Diagnostics.Should().BeEmpty();
        Volume(chamfered, 60 * 40 * 10 - 4.5 * 10);
    }

    [Fact]
    public void Revolved_step_gets_a_toroidal_undercut()
    {
        var result = Modeler.Regenerate(Shaft());
        var plain = Modeler.Regenerate(new PartDocument([Shaft().Features[0]]));

        result.Diagnostics.Should().BeEmpty();
        // Corner square of side 1 at (ρ, z) = (6, 20) less the quarter disc round (7, 21), swept round the axis.
        // The ring is small next to the shaft's own chord error, so compare what the fillet adds.
        var ring = Integrate2(1, (u, v) => Sq(v - 1) + Sq(u - 1) > 1 ? 2 * Math.PI * (6 + v) : 0);
        var added = SignedVolume(result.Mesh!) - SignedVolume(plain.Mesh!);
        output.WriteLine($"added {added:F4} vs ring {ring:F4}");
        added.Should().BeApproximately(ring, ring * 0.05);
        Deviation(result, "undercut/face", p => Math.Sqrt(Sq(Math.Sqrt(p.X * p.X + p.Y * p.Y) - 7) + Sq(p.Z - 21)) - 1).Should().BeLessThan(1e-7);
    }

    [Fact]
    public void A_ball_taller_than_the_boss_does_not_fit()
    {
        var result = Modeler.Regenerate(BoxBoss(height: 1));

        result.Diagnostics.Should().ContainSingle().Which.Code.Should().Be(Diagnostic.FilletInvalid);
    }

    public static IEnumerable<object[]> Parts => [["pocket"], ["box boss"], ["round boss"], ["outline"], ["bracket"], ["shaft"]];

    internal static PartDocument Part(string name) => name switch
    {
        "pocket" => Pocket("x-", "y-", "x+", "y+"),
        "box boss" => BoxBoss(),
        "round boss" => RoundBoss(),
        "outline" => Outline(new Fillet("round", ["outline/a", "outline/b"], 5)),
        "bracket" => Bracket(),
        _ => Shaft(),
    };

    [Theory]
    [MemberData(nameof(Parts))]
    public void Field_is_1_lipschitz_bounded_and_simplifies_exactly(string name) =>
        FieldProperties.Check(new Compiler(new ModelingSettings()).Compile(Part(name)));

    [Fact]
    public void Inside_fillets_keep_their_names_when_the_radius_changes()
    {
        foreach (var (doc, id) in new[] { (BoxBoss(), "foot"), (Bracket(), "inside"), (Pocket("x-", "y-"), "f1") })
        {
            var before = Modeler.Regenerate(doc).Topology!;
            var after = Modeler.Regenerate(DocumentEdits.Apply(doc, [JsonNode.Parse($$"""{ "set": "{{id}}.radius", "value": 1.5 }""")!]));
            after.Diagnostics.Should().BeEmpty();
            foreach (var source in before.Faces.SelectMany(f => f.Sources))
            {
                var reference = new FaceReference(source, source);
                ReferenceResolver.Resolve(reference, after.Topology!).Status.Should().Be(ReferenceResolver.Resolve(reference, before).Status, source);
            }
        }
    }

    void Volume(FrepResult r, double exact)
    {
        var volume = SignedVolume(r.Mesh!);
        output.WriteLine($"volume {volume:F4} vs {exact:F4}");
        volume.Should().BeApproximately(exact, exact * VolumeTolerance);
    }

    static double MaxVertexError(FrepResult r) => r.Mesh!.Vertices.Max(p => Math.Abs(r.Part.Field!.Value(p)));

    /// <summary>Largest |distance| to its exact surface over the mesh vertices on the solid that one face owns.</summary>
    static double Deviation(FrepResult r, string source, Func<Point3D, double> distance)
    {
        var owner = r.Part.Surfaces.Single(s => s.Source == source).Id;
        var mesh = r.Mesh!;
        var points = mesh.Quads.Where(q => q.Owner == owner).SelectMany(q => new[] { q.A, q.B, q.C, q.D }).Distinct()
            .Select(v => mesh.Vertices[v])
            .Where(p => r.Part.Field!.Sample(p) is var s && Math.Abs(s.Value) < 1e-9 && s.Owner == owner).ToList();
        points.Should().NotBeEmpty();
        return points.Max(p => Math.Abs(distance(p)));
    }

    static double Integrate(Func<double, double> f, double a, double b, int n = 2000)
    {
        var h = (b - a) / n;
        var sum = f(a) + f(b);
        for (var i = 1; i < n; i++) sum += f(a + i * h) * (i % 2 == 1 ? 4 : 2);
        return sum * h / 3;
    }

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

    static double Sq(double x) => x * x;

    static double SignedVolume(Mesh mesh)
    {
        var v = mesh.Vertices;
        return mesh.Triangles().Sum(t => v[t.A].AsVector().Dot(v[t.B].AsVector().Cross(v[t.C].AsVector()))) / 6;
    }
}
