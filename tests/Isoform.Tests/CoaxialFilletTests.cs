using System.Text.Json.Nodes;
using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Meshing;
using Isoform.Modeling;
using Xunit.Abstractions;

namespace Isoform.Tests;

/// <summary>
/// K4: fillets on edges between two surfaces round one axis (planes across it, cylinders, cones, spheres, tori). In the
/// (ρ, z) half-plane both faces are lines or circles, so the rolling ball's centre runs round a circle and the fillet
/// is a torus: vertices on it, and the volume by Pappus from the corner the ball leaves out.
/// </summary>
public class CoaxialFilletTests(ITestOutputHelper output)
{
    static readonly FrepModeler Modeler = new(new ModelingSettings { CellSize = 0.25 });
    static readonly SketchPlane Upright = new([30, 20, 0], [1, 0, 0], [0, -1, 0]);
    static readonly SketchPlane AtOrigin = new([0, 0, 0], [1, 0, 0], [0, -1, 0]);
    const double VolumeTolerance = 1e-6;

    static Profile Loop(double[] start, params (string Id, double U, double V, double Bulge)[] edges) =>
        new(start, edges.Select(e => new ProfileEdge(e.Id, [e.U, e.V], e.Bulge)).ToList());

    // A 60 × 40 × 10 plate with a 90° countersunk hole at (30, 20): a 3 mm bore, the cone reaching ρ = 6 at the top.
    static PartDocument Countersink(double r, Feature? edgeFeature = null) => new(
    [
        new Block("plate", BooleanOp.New, [0, 0, 0], [60, 40, 10]),
        new Revolve("sink", BooleanOp.Cut, Upright, Loop([0, -1], ("bottom", 3, -1, 0), ("wall", 3, 7, 0), ("cone", 7, 11, 0), ("top", 0, 11, 0), ("axis", 0, -1, 0))),
        edgeFeature ?? new Fillet("round", ["plate/z+", "sink/cone"], r),
    ]);

    // A cone 10 across the base and 20 high, bored through its axis with a 2 mm radius: the bore leaves the cone at z = 16.
    static PartDocument BoredCone(double r) => new(
    [
        new Revolve("cone", BooleanOp.New, AtOrigin, Loop([0, 0], ("base", 10, 0, 0), ("side", 0, 20, 0), ("axis", 0, 0, 0))),
        new Hole("bore", [0, 0], 4),
        new Fillet("round", ["cone/side", "bore/wall"], r),
    ]);

    [Theory]
    [InlineData(0.5)]
    [InlineData(1)]
    public void Countersink_rim_fillet_is_a_torus(double r)
    {
        var result = Modeler.Regenerate(Countersink(r));

        result.Diagnostics.Should().BeEmpty();
        result.Mesh!.IsWatertight().Should().BeTrue();
        // In (ρ, z): the top z = 10 meets the cone ρ = z − 4 at E = (6, 10) at 135°; the ball touches both t from E.
        var t = r * (Math.Sqrt(2) - 1);
        var (e, centre) = ((6.0, 10.0), (Rho: 6 + t, Z: 10 - r));
        var (onTop, onCone) = ((6 + t, 10.0), (6 - t / Math.Sqrt(2), 10 - t / Math.Sqrt(2)));
        TorusDeviation(result, 30, 20, centre, r).Should().BeLessThan(1e-7);
        var corner = Segment(e, onTop) + Arc(centre, r, Math.PI / 2, 3 * Math.PI / 4) + Segment(onCone, e);
        var exact = 60 * 40 * 10 - 126 * Math.PI - 2 * Math.PI * Math.Abs(corner);
        Volume(result).Should().BeApproximately(exact, exact * VolumeTolerance);
        result.Topology!.Faces.SelectMany(f => f.Sources).Should().BeEquivalentTo(
            ["plate/x-", "plate/x+", "plate/y-", "plate/y+", "plate/z-", "plate/z+", "sink/wall", "sink/cone", "round/face"]);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(1)]
    public void Cone_and_bore_fillet_is_a_torus(double r)
    {
        var result = Modeler.Regenerate(BoredCone(r));

        result.Diagnostics.Should().BeEmpty();
        result.Mesh!.IsWatertight().Should().BeTrue();
        // In (ρ, z): the bore ρ = 2 meets the cone z = 20 − 2ρ at E = (2, 16); the ball touches both t from E.
        var psi = Math.Acos(2 / Math.Sqrt(5));
        var t = r / Math.Tan(psi / 2);
        var (e, centre) = ((2.0, 16.0), (Rho: 2 + r, Z: 16 - t));
        var (onBore, onCone) = ((2.0, 16 - t), (2 + t / Math.Sqrt(5), 16 - 2 * t / Math.Sqrt(5)));
        TorusDeviation(result, 0, 0, centre, r).Should().BeLessThan(1e-7);
        var corner = Segment(e, onCone) + Arc(centre, r, Math.Atan2(1, 2), Math.PI) + Segment(onBore, e);
        var exact = 1792 * Math.PI / 3 - 2 * Math.PI * Math.Abs(corner);
        Volume(result).Should().BeApproximately(exact, exact * VolumeTolerance);
        result.Topology!.Faces.SelectMany(f => f.Sources).Should().BeEquivalentTo(["cone/base", "cone/side", "bore/wall", "round/face"]);
    }

    // A hemisphere of radius 8 joined on the plate's top at (30, 20), the inside corner where they meet rounded.
    static PartDocument Dome(double r) => new(
    [
        new Block("plate", BooleanOp.New, [0, 0, 0], [60, 40, 10]),
        new Revolve("dome", BooleanOp.Join, new SketchPlane([30, 20, 10], [1, 0, 0], [0, -1, 0]),
            Loop([0, 0], ("base", 8, 0, 0), ("cap", 0, 8, Math.Tan(Math.PI / 8)), ("axis", 0, 0, 0))),
        new Fillet("round", ["plate/z+", "dome/cap"], r),
    ]);

    // A puck 14 in radius and 6 high whose top rim is a quarter torus (tube 6 round ρ = 8), bored 11 in radius: the
    // bore leaves the torus at z = √27.
    static PartDocument Knob(double r) => new(
    [
        new Revolve("knob", BooleanOp.New, AtOrigin, Loop([0, 0], ("base", 14, 0, 0), ("rim", 8, 6, Math.Tan(Math.PI / 8)), ("top", 0, 6, 0), ("axis", 0, 0, 0))),
        new Hole("bore", [0, 0], 22),
        new Fillet("round", ["knob/rim", "bore/wall"], r),
    ]);

    [Theory]
    [InlineData(0.5)]
    [InlineData(1.5)]
    public void Fillet_where_a_dome_meets_a_plate_is_a_torus(double r)
    {
        var result = Modeler.Regenerate(Dome(r));

        result.Diagnostics.Should().BeEmpty();
        result.Mesh!.IsWatertight().Should().BeTrue();
        // In (ρ, z): the ball sits on the top z = 10 and against the sphere round (0, 10), outside both.
        var centre = (Rho: Math.Sqrt(Sq(8 + r) - r * r), Z: 10 + r);
        var onSphere = (Rho: centre.Rho * 8 / (8 + r), Z: 10 + r * 8 / (8 + r));
        TorusDeviation(result, 30, 20, centre, r).Should().BeLessThan(1e-7);
        var filler = Segment((8, 10), (centre.Rho, 10)) + Arc(centre, r, -Math.PI / 2, Math.Atan2(-r, -centre.Rho))
            + Arc((0, 10), 8, Math.Atan2(onSphere.Z - 10, onSphere.Rho), 0);
        var exact = 60 * 40 * 10 + 2 * Math.PI * 512 / 3 + 2 * Math.PI * Math.Abs(filler);
        Volume(result).Should().BeApproximately(exact, exact * VolumeTolerance);
        result.Topology!.Faces.SelectMany(f => f.Sources).Should().BeEquivalentTo(
            ["plate/x-", "plate/x+", "plate/y-", "plate/y+", "plate/z-", "plate/z+", "dome/cap", "round/face"]);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(1)]
    public void Fillet_where_a_bore_meets_a_torus_is_a_torus(double r)
    {
        var result = Modeler.Regenerate(Knob(r));

        result.Diagnostics.Should().BeEmpty();
        result.Mesh!.IsWatertight().Should().BeTrue();
        // In (ρ, z): the ball sits inside the tube round (8, 0) and against the bore ρ = 11 from outside it.
        var centre = (Rho: 11 + r, Z: Math.Sqrt(Sq(6 - r) - Sq(3 + r)));
        var onTorus = (Rho: 8 + (3 + r) * 6 / (6 - r), Z: centre.Z * 6 / (6 - r));
        var edge = (11.0, Math.Sqrt(27));
        TorusDeviation(result, 0, 0, centre, r).Should().BeLessThan(1e-7);
        var ring = Segment((11, 0), (14, 0)) + Arc((8, 0), 6, 0, Math.PI / 3) + Segment(edge, (11, 0));
        var corner = Segment(edge, (11, centre.Z)) + Arc(centre, r, Math.PI, Math.Atan2(onTorus.Z - centre.Z, onTorus.Rho - centre.Rho))
            + Arc((8, 0), 6, Math.Atan2(onTorus.Z, onTorus.Rho - 8), Math.PI / 3);
        var exact = 2 * Math.PI * (Math.Abs(ring) - Math.Abs(corner));
        // A small part with long thin cusps where the fillet runs into its faces: 2e-6 off at 0.25 mm cells, 1e-7 at 0.125.
        Volume(result).Should().BeApproximately(exact, exact * 5 * VolumeTolerance);
        result.Topology!.Faces.SelectMany(f => f.Sources).Should().BeEquivalentTo(["knob/base", "knob/rim", "bore/wall", "round/face"]);
    }

    [Fact]
    public void Fillet_on_a_cone_edge_off_its_axis_is_refused()
    {
        var result = Modeler.Regenerate(new PartDocument([.. Countersink(1).Features.SkipLast(1), new Fillet("round", ["plate/x-", "sink/cone"], 1)]));

        result.Diagnostics.Should().ContainSingle().Which.Message.Should().Contain("no exact fillet for Plane–Cone");
    }

    [Fact]
    public void Fillet_next_to_a_hollow_torus_is_refused()
    {
        // A groove of round section, 2 in radius round ρ = 10 on the top face.
        var result = Modeler.Regenerate(new PartDocument(
        [
            new Block("plate", BooleanOp.New, [0, 0, 0], [60, 40, 10]),
            new Revolve("groove", BooleanOp.Cut, Upright, Loop([8, 10], ("near", 12, 10, 1), ("far", 8, 10, 1))),
            new Fillet("round", ["plate/z+", "groove/near"], 0.5),
        ]));

        var problem = result.Diagnostics.Should().ContainSingle().Which;
        problem.Code.Should().Be(Diagnostic.UnsupportedFillet);
        problem.Message.Should().Contain("hollow torus");
    }

    [Fact]
    public void Chamfer_on_a_cone_edge_is_refused()
    {
        var result = Modeler.Regenerate(Countersink(0, new Chamfer("round", ["plate/z+", "sink/cone"], 1)));

        result.Diagnostics.Should().ContainSingle().Which.Code.Should().Be(Diagnostic.UnsupportedChamfer);
    }

    public static IEnumerable<object[]> Parts => [["countersink"], ["bored cone"], ["dome"], ["knob"]];

    internal static PartDocument Part(string name) => name switch
    {
        "countersink" => Countersink(1),
        "bored cone" => BoredCone(0.5),
        "dome" => Dome(1.5),
        _ => Knob(1),
    };

    [Theory]
    [MemberData(nameof(Parts))]
    public void Field_is_1_lipschitz_bounded_and_simplifies_exactly(string name) =>
        FieldProperties.Check(new Compiler(new ModelingSettings()).Compile(Part(name)));

    [Theory]
    [MemberData(nameof(Parts))]
    public void Faces_keep_their_names_when_the_radius_changes(string name)
    {
        var doc = Part(name);
        var edit = JsonNode.Parse("""{ "set": "round.radius", "value": 0.6 }""")!;
        var before = Modeler.Regenerate(doc).Topology!;
        var after = Modeler.Regenerate(DocumentEdits.Apply(doc, [edit]));
        after.Diagnostics.Should().BeEmpty();
        foreach (var source in before.Faces.SelectMany(f => f.Sources))
        {
            var reference = new FaceReference(source, source);
            ReferenceResolver.Resolve(reference, after.Topology!).Status.Should().Be(ReferenceResolver.Resolve(reference, before).Status, source);
        }
    }

    /// <summary>Largest distance of the fillet's own vertices from the torus round the axis through (x, y) along z.</summary>
    double TorusDeviation(FrepResult result, double x, double y, (double Rho, double Z) centre, double r)
    {
        var fillet = result.Part.Surfaces.Single(s => s.Source == "round/face").Id;
        var owners = new Dictionary<int, HashSet<int>>();
        foreach (var q in result.Mesh!.Quads)
            foreach (var v in new[] { q.A, q.B, q.C, q.D })
                (owners.TryGetValue(v, out var s) ? s : owners[v] = []).Add(q.Owner);
        var vertices = owners.Where(kv => kv.Value.All(o => o == fillet)).Select(kv => result.Mesh.Vertices[kv.Key]).ToList();
        vertices.Should().NotBeEmpty();
        var deviation = vertices.Max(p => Math.Abs(Math.Sqrt(Sq(Math.Sqrt(Sq(p.X - x) + Sq(p.Y - y)) - centre.Rho) + Sq(p.Z - centre.Z)) - r));
        output.WriteLine($"torus deviation {deviation:E2} over {vertices.Count} vertices");
        return deviation;
    }

    double Volume(FrepResult result)
    {
        var volume = MassProperties.Of(result.Part.Field!, result.Mesh!).Volume;
        output.WriteLine($"volume {volume:F6}");
        return volume;
    }

    // ∫∫ ρ dA over a region of the (ρ, z) half-plane is ∮ ρ²/2 dz round it (Green); Pappus makes 2π times it a volume.
    internal static double Segment((double Rho, double Z) p, (double Rho, double Z) q)
    {
        var (dr, dz) = (q.Rho - p.Rho, q.Z - p.Z);
        return dz / 2 * (p.Rho * p.Rho + p.Rho * dr + dr * dr / 3);
    }

    internal static double Arc((double Rho, double Z) c, double r, double from, double to)
    {
        double F(double a) => r / 2 * (c.Rho * c.Rho * Math.Sin(a) + 2 * c.Rho * r * (a / 2 + Math.Sin(2 * a) / 4) + r * r * (Math.Sin(a) - Math.Pow(Math.Sin(a), 3) / 3));
        return F(to) - F(from);
    }

    static double Sq(double x) => x * x;
}
