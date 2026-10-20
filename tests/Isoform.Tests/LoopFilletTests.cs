using System.Text.Json.Nodes;
using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Meshing;
using Isoform.Modeling;
using Xunit.Abstractions;

namespace Isoform.Tests;

/// <summary>K4: fillets and chamfers on every edge round one face, exact along straight edges and arcs alike.</summary>
public class LoopFilletTests(ITestOutputHelper output)
{
    static readonly Block Plate = new("plate", BooleanOp.New, [0, 0, 0], [100, 50, 10]);
    static readonly SketchPlane Xy = new([0, 0, 0], [1, 0, 0], [0, 0, 1]);
    static readonly SketchPlane Top = new([0, 0, 10], [1, 0, 0], [0, 0, 1]);
    static readonly FrepModeler Modeler = new(new ModelingSettings { CellSize = 0.25 });
    const double VolumeTolerance = 1e-4;

    // A stadium 20 long between two R5 ends: (40, 20) → (60, 20), round to (60, 30), back to (40, 30), round home.
    static Profile Stadium(double length = 20) => new([40, 20],
    [
        new ProfileEdge("a", [40 + length, 20]), new ProfileEdge("right", [40 + length, 30], 1),
        new ProfileEdge("b", [40, 30]), new ProfileEdge("left", [40, 20], 1),
    ]);

    static Profile Rectangle(double x0, double y0, double x1, double y1) => new([x0, y0],
        [new ProfileEdge("s", [x1, y0]), new ProfileEdge("e", [x1, y1]), new ProfileEdge("n", [x0, y1]), new ProfileEdge("w", [x0, y0])]);

    static PartDocument Boss(Feature edge, double length = 20) => new(
        [Plate, new Extrude("boss", BooleanOp.Join, Top, Stadium(length), 0, 8), edge]);

    static PartDocument Outline() => new(
        [new Extrude("outline", BooleanOp.New, Xy, Rectangle(0, 0, 60, 40), 0, 10), new Fillet("round", [], 2, Loop: "outline/end")]);

    static PartDocument Pocket() => new(
        [Plate, new Extrude("pocket", BooleanOp.Cut, Xy, Rectangle(20, 10, 60, 40), 4, 11), new Fillet("floor", [], 2, Loop: "pocket/start")]);

    static PartDocument CutOut() => new(
        [Plate, new Extrude("slot", BooleanOp.Cut, Xy, Stadium(), -1, 11), new Fillet("round", [], 1, Loop: "plate/z+")]);

    /// <summary>The corner a ball of radius r leaves out, in (s into the material from the side, t down from the cap).</summary>
    static bool Spandrel(double r, double s, double t) => Sq(r - s) + Sq(r - t) > r * r;

    static double Spandrel(double r) => r * r * (1 - Math.PI / 4);

    [Fact]
    public void Loop_round_a_boss_top_follows_lines_and_arcs()
    {
        const double r = 1;
        var result = Modeler.Regenerate(Boss(new Fillet("round", [], r, Loop: "boss/end")));

        result.Diagnostics.Should().BeEmpty();
        result.Mesh!.IsWatertight().Should().BeTrue();
        // Straight edges lose the spandrel per mm; each R5 half circle sweeps it round at radius 5 − s.
        var removed = 2 * 20 * Spandrel(r) + 2 * Math.PI * Integrate2(r, (s, t) => Spandrel(r, s, t) ? 5 - s : 0);
        Volume(result, 100 * 50 * 10 + (20 * 10 + Math.PI * 25) * 8 - removed);
        result.Topology!.Faces.SelectMany(f => f.Sources).Should().Contain(["round/boss/a", "round/boss/right", "round/boss/b", "round/boss/left"]);
        Deviation(result, "round/boss/a", p => Math.Sqrt(Sq(p.Y - 21) + Sq(p.Z - 17)) - r).Should().BeLessThan(1e-7);
        Deviation(result, "round/boss/right", p => Math.Sqrt(Sq(Math.Sqrt(Sq(p.X - 60) + Sq(p.Y - 25)) - 4) + Sq(p.Z - 17)) - r).Should().BeLessThan(1e-7);
    }

    [Fact]
    public void Loop_round_an_outline_mitres_its_corners()
    {
        const double r = 2;
        var result = Modeler.Regenerate(Outline());

        result.Diagnostics.Should().BeEmpty();
        var overlap = Integrate(h => Math.Pow(r - Math.Sqrt(r * r - h * h), 2), 0, r);
        Volume(result, 60 * 40 * 10 - Spandrel(r) * 200 + 4 * overlap);
        Deviation(result, "round/outline/s", p => Math.Sqrt(Sq(p.Y - 2) + Sq(p.Z - 8)) - r).Should().BeLessThan(1e-7);
    }

    [Fact]
    public void Loop_round_a_pocket_floor_adds_material_all_round()
    {
        const double r = 2;
        var result = Modeler.Regenerate(Pocket());

        result.Diagnostics.Should().BeEmpty();
        result.Mesh!.IsWatertight().Should().BeTrue();
        var overlap = Integrate(h => Math.Pow(r - Math.Sqrt(r * r - h * h), 2), 0, r);
        Volume(result, 100 * 50 * 10 - 40 * 30 * 6 + Spandrel(r) * 140 - 4 * overlap);
        Deviation(result, "floor/pocket/s", p => Math.Sqrt(Sq(p.Y - 12) + Sq(p.Z - 6)) - r).Should().BeLessThan(1e-7);
    }

    [Fact]
    public void Loop_round_the_floor_of_a_block_pocket_rounds_each_inside_edge()
    {
        const double r = 2;
        var result = Modeler.Regenerate(new PartDocument(
            [Plate, new Block("pocket", BooleanOp.Cut, [30, 15, 5], [40, 20, 10]), new Fillet("floor", [], r, Loop: "pocket/z-")]));

        result.Diagnostics.Should().BeEmpty();
        var overlap = Integrate(h => Math.Pow(r - Math.Sqrt(r * r - h * h), 2), 0, r);
        Volume(result, 100 * 50 * 10 - 40 * 20 * 5 + Spandrel(r) * 120 - 4 * overlap);
        result.Topology!.Faces.SelectMany(f => f.Sources).Should().Contain(["floor/pocket/x-", "floor/pocket/y+"]);
    }

    [Fact]
    public void Loop_round_a_face_takes_its_outer_edges_and_the_rim_of_a_cut_out()
    {
        const double r = 1;
        var result = Modeler.Regenerate(CutOut());

        result.Diagnostics.Should().BeEmpty();
        result.Mesh!.IsWatertight().Should().BeTrue();
        // Outer: four mitred edges. Rim: the slot's lines, and its ends swept at radius 5 + s outside the hole.
        var overlap = Integrate(h => Math.Pow(r - Math.Sqrt(r * r - h * h), 2), 0, r);
        var outer = Spandrel(r) * 300 - 4 * overlap;
        var rim = 2 * 20 * Spandrel(r) + 2 * Math.PI * Integrate2(r, (s, t) => Spandrel(r, s, t) ? 5 + s : 0);
        Volume(result, 100 * 50 * 10 - (20 * 10 + Math.PI * 25) * 10 - outer - rim);
        Deviation(result, "round/slot/right", p => Math.Sqrt(Sq(Math.Sqrt(Sq(p.X - 60) + Sq(p.Y - 25)) - 6) + Sq(p.Z - 9)) - r).Should().BeLessThan(1e-7);
        Deviation(result, "round/plate/x+", p => Math.Sqrt(Sq(p.X - 99) + Sq(p.Z - 9)) - r).Should().BeLessThan(1e-7);
    }

    [Fact]
    public void Loop_chamfer_is_flat_on_lines_and_conical_on_arcs()
    {
        const double d = 1;
        var result = Modeler.Regenerate(Boss(new Chamfer("cut", [], d, Loop: "boss/end")));

        result.Diagnostics.Should().BeEmpty();
        // A right triangle of legs d per mm of line; round each R5 end at radius 5 − s.
        var removed = 2 * 20 * d * d / 2 + 2 * Math.PI * (5 * d * d / 2 - d * d * d / 6);
        Volume(result, 100 * 50 * 10 + (20 * 10 + Math.PI * 25) * 8 - removed);
        Deviation(result, "cut/boss/a", p => ((p.Y - 20) + (18 - p.Z) - d) / Math.Sqrt(2)).Should().BeLessThan(1e-7);
        Deviation(result, "cut/boss/right", p => ((5 - Math.Sqrt(Sq(p.X - 60) + Sq(p.Y - 25))) + (18 - p.Z) - d) / Math.Sqrt(2)).Should().BeLessThan(1e-7);
    }

    [Fact]
    public void A_loop_needs_a_flat_face()
    {
        var result = Modeler.Regenerate(new PartDocument([Plate, new Hole("hole", [30, 25], 10), new Fillet("round", [], 1, Loop: "hole/wall")]));

        result.Diagnostics.Should().ContainSingle().Which.Code.Should().Be(Diagnostic.UnsupportedFillet);
    }

    public static IEnumerable<object[]> Parts => [["boss"], ["outline"], ["pocket"], ["cut-out"], ["chamfer"]];

    internal static PartDocument Part(string name) => name switch
    {
        "boss" => Boss(new Fillet("round", [], 1, Loop: "boss/end")),
        "outline" => Outline(),
        "pocket" => Pocket(),
        "cut-out" => CutOut(),
        _ => Boss(new Chamfer("cut", [], 1, Loop: "boss/end")),
    };

    [Theory]
    [MemberData(nameof(Parts))]
    public void Field_is_1_lipschitz_bounded_and_simplifies_exactly(string name) =>
        FieldProperties.Check(new Compiler(new ModelingSettings()).Compile(Part(name)));

    [Fact]
    public void Loop_faces_keep_their_names_when_the_profile_and_radius_change()
    {
        var doc = Boss(new Fillet("round", [], 1, Loop: "boss/end"));
        var before = Modeler.Regenerate(doc).Topology!;
        var after = Modeler.Regenerate(DocumentEdits.Apply(doc,
        [
            JsonNode.Parse("""{ "set": "round.radius", "value": 1.5 }""")!,
            JsonNode.Parse("""{ "set": "boss.profile.edges.0.to", "value": [70, 20] }""")!,
            JsonNode.Parse("""{ "set": "boss.profile.edges.1.to", "value": [70, 30] }""")!,
        ]));

        after.Diagnostics.Should().BeEmpty();
        foreach (var source in before.Faces.SelectMany(f => f.Sources))
        {
            var reference = new FaceReference(source, source);
            ReferenceResolver.Resolve(reference, after.Topology!).Status.Should().Be(ReferenceResolver.Resolve(reference, before).Status, source);
        }
    }

    [Fact]
    public void Loops_round_trip_through_the_canonical_format()
    {
        var text = CanonicalFormat.Write(CutOut());
        text.Should().Contain("\"loop\": \"plate/z+\"").And.NotContain("\"edge\"");
        CanonicalFormat.Write(CanonicalFormat.Read(text)).Should().Be(text);
    }

    void Volume(FrepResult r, double exact)
    {
        var volume = SignedVolume(r.Mesh!);
        output.WriteLine($"volume {volume:F4} vs {exact:F4}");
        volume.Should().BeApproximately(exact, exact * VolumeTolerance);
    }

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
