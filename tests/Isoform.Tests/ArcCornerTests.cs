using System.Text.Json.Nodes;
using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Geometry;
using Isoform.Meshing;
using Isoform.Modeling;
using Xunit.Abstractions;

namespace Isoform.Tests;

/// <summary>
/// K4: profile corners next to arcs rounded in the profile, by an arc tangent to both edges: a line meeting an arc
/// (convex and reflex, extruded) and two arcs meeting (revolved, between two spheres). The round's centre is where
/// both edges moved by r meet, so the new face is a cylinder (extrude) or a torus (revolve), exact.
/// </summary>
public class ArcCornerTests(ITestOutputHelper output)
{
    static readonly FrepModeler Modeler = new(new ModelingSettings { CellSize = 0.25 });
    static readonly SketchPlane Xy = new([0, 0, 0], [1, 0, 0], [0, 0, 1]);
    static readonly SketchPlane Xz = new([0, 0, 0], [1, 0, 0], [0, -1, 0]);
    const double VolumeTolerance = 1e-6;

    static Profile Loop(double[] start, params (string Id, double U, double V, double Bulge)[] edges) =>
        new(start, edges.Select(e => new ProfileEdge(e.Id, [e.U, e.V], e.Bulge)).ToList());

    // A D, 10 thick: a flat edge from (−20, 0) to (20, 0) and a half circle of radius 20 back over the top. Its corner
    // at (20, 0) is convex, a right angle between the line and the arc.
    public static PartDocument D(double r) => new(
    [
        new Extrude("d", BooleanOp.New, Xy, Loop([-20, 0], ("flat", 20, 0, 0), ("bow", -20, 0, 1)), 0, 10),
        new Fillet("round", ["d/flat", "d/bow"], r),
    ]);

    // A 60 × 30 plate, 10 thick, with a half-round tab of radius 10 on its top edge: reflex right angles where the top
    // meets the tab. The corner at (40, 30) is rounded, which adds material.
    public static PartDocument Tab(double r) => new(
    [
        new Extrude("plate", BooleanOp.New, Xy, Loop([0, 0], ("s", 60, 0, 0), ("e", 60, 30, 0), ("n1", 40, 30, 0), ("tab", 20, 30, 1),
            ("n2", 0, 30, 0), ("w", 0, 0, 0)), 0, 10),
        new Fillet("round", ["plate/n1", "plate/tab"], r),
    ]);

    // A lens revolved round z: two arcs of radius 10 round (0, ±6) on the axis, meeting at the rim (8, 0); caps of 53°.
    static readonly double LensAngle = Math.Atan2(6, 8);
    public static PartDocument Lens(double r) => new(
    [
        new Revolve("lens", BooleanOp.New, Xz, Loop([0, -4], ("low", 8, 0, Math.Tan((Math.PI / 2 - LensAngle) / 4)),
            ("high", 0, 4, Math.Tan((Math.PI / 2 - LensAngle) / 4)), ("axis", 0, -4, 0))),
        new Fillet("round", ["lens/low", "lens/high"], r),
    ]);

    /// <summary>The D less the corner the round takes off at (20, 0).</summary>
    public static double DVolume(double r)
    {
        // The flat moved up by r meets the circle shrunk by r.
        var (cu, cv) = (Math.Sqrt(Sq(20 - r) - r * r), r);
        var onArc = (U: cu * 20 / (20 - r), V: cv * 20 / (20 - r));
        var spandrel = Area(
            new ProfileEdge2D(cu, 0, 20, 0, 0),
            new ProfileEdge2D(20, 0, onArc.U, onArc.V, Math.Tan(Math.Atan2(onArc.V, onArc.U) / 4)),
            Round(onArc, (cu, 0), (cu, cv)));
        return (200 * Math.PI - Math.Abs(spandrel)) * 10;
    }

    /// <summary>The plate and tab plus the corner the round fills at (40, 30).</summary>
    public static double TabVolume(double r)
    {
        // The top moved up by r meets the tab's circle grown by r.
        var (cu, cv) = (30 + Math.Sqrt(Sq(10 + r) - r * r), 30 + r);
        var onTab = (U: 30 + (cu - 30) * 10 / (10 + r), V: 30 + (cv - 30) * 10 / (10 + r));
        var filler = Area(
            new ProfileEdge2D(40, 30, cu, 30, 0),
            Round((cu, 30), onTab, (cu, cv)),
            new ProfileEdge2D(onTab.U, onTab.V, 40, 30, -Math.Tan(Math.Atan2(onTab.V - 30, onTab.U - 30) / 4)));
        return (60 * 30 + 50 * Math.PI + Math.Abs(filler)) * 10;
    }

    /// <summary>Pappus round the lens's section: the two spherical arcs and the round between them.</summary>
    public static double LensVolume(double r)
    {
        var big = 10.0;
        var (low, high) = ((Rho: 0.0, Z: 6.0), (Rho: 0.0, Z: -6.0));
        var centre = (Rho: Math.Sqrt(Sq(big - r) - 36), Z: 0.0);
        var (a1, a2) = (Math.Atan2(centre.Z - low.Z, centre.Rho), Math.Atan2(centre.Z - high.Z, centre.Rho));
        return 2 * Math.PI * Math.Abs(CoaxialFilletTests.Arc(low, big, -Math.PI / 2, a1) + CoaxialFilletTests.Arc(centre, r, a1, a2)
            + CoaxialFilletTests.Arc(high, big, a2, Math.PI / 2));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    public void Line_into_arc_corner_rounds_to_a_tangent_cylinder(double r)
    {
        var result = Modeler.Regenerate(D(r));

        result.Diagnostics.Should().BeEmpty();
        result.Mesh!.IsWatertight().Should().BeTrue();
        var (cu, cv) = (Math.Sqrt(Sq(20 - r) - r * r), r);
        Deviation(result, p => Math.Sqrt(Sq(p.X - cu) + Sq(p.Y - cv)) - r).Should().BeLessThan(1e-7);
        var exact = DVolume(r);
        Volume(result).Should().BeApproximately(exact, exact * VolumeTolerance);
        result.Topology!.Faces.SelectMany(f => f.Sources).Should().BeEquivalentTo(["d/flat", "d/bow", "d/start", "d/end", "round/face"]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Reflex_line_into_arc_corner_rounds_with_material_added(double r)
    {
        var result = Modeler.Regenerate(Tab(r));

        result.Diagnostics.Should().BeEmpty();
        result.Mesh!.IsWatertight().Should().BeTrue();
        var (cu, cv) = (30 + Math.Sqrt(Sq(10 + r) - r * r), 30 + r);
        Deviation(result, p => Math.Sqrt(Sq(p.X - cu) + Sq(p.Y - cv)) - r).Should().BeLessThan(1e-7);
        var exact = TabVolume(r);
        Volume(result).Should().BeApproximately(exact, exact * VolumeTolerance);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Arc_into_arc_corner_of_a_revolve_rounds_to_a_torus(double r)
    {
        var result = Modeler.Regenerate(Lens(r));

        result.Diagnostics.Should().BeEmpty();
        result.Mesh!.IsWatertight().Should().BeTrue();
        var rho = Math.Sqrt(Sq(10 - r) - 36);
        Deviation(result, p => Math.Sqrt(Sq(Math.Sqrt(p.X * p.X + p.Y * p.Y) - rho) + p.Z * p.Z) - r).Should().BeLessThan(1e-7);
        var exact = LensVolume(r);
        // A small part curved all over: the mesh's volume is 2e-6 off at 0.25 mm cells, 4e-7 at 0.125 (the exact STEP
        // has it to 1e-11).
        Volume(result).Should().BeApproximately(exact, exact * 5 * VolumeTolerance);
        result.Topology!.Faces.SelectMany(f => f.Sources).Should().BeEquivalentTo(["lens/low", "lens/high", "round/face"]);
    }

    [Fact]
    public void Round_too_big_for_the_arc_is_refused()
    {
        // The flat moved up by 15 misses the circle shrunk to 5: no round of 15 fits the corner.
        var result = Modeler.Regenerate(D(15));

        result.Diagnostics.Should().ContainSingle().Which.Code.Should().Be(Diagnostic.FilletInvalid);
    }

    [Fact]
    public void Chamfer_next_to_an_arc_is_refused()
    {
        var result = Modeler.Regenerate(new PartDocument([D(1).Features[0], new Chamfer("round", ["d/flat", "d/bow"], 1)]));

        result.Diagnostics.Should().ContainSingle().Which.Code.Should().Be(Diagnostic.UnsupportedChamfer);
    }

    public static IEnumerable<object[]> Parts => [["d"], ["tab"], ["lens"]];

    public static PartDocument Part(string name) => name switch
    {
        "d" => D(3),
        "tab" => Tab(2),
        _ => Lens(1.5),
    };

    [Theory]
    [MemberData(nameof(Parts))]
    public void Field_is_1_lipschitz_bounded_and_simplifies_exactly(string name) =>
        FieldProperties.Check(new Compiler(new ModelingSettings()).Compile(Part(name)));

    [Theory]
    [MemberData(nameof(Parts))]
    public void Faces_keep_their_names_when_sizes_change(string name)
    {
        var doc = Part(name);
        foreach (var edit in new[] { """{ "set": "round.radius", "value": 1.25 }""", """{ "set": "round.radius", "value": 2.5 }""" })
        {
            var before = Modeler.Regenerate(doc).Topology!;
            var after = Modeler.Regenerate(DocumentEdits.Apply(doc, [JsonNode.Parse(edit)!]));
            after.Diagnostics.Should().BeEmpty();
            foreach (var source in before.Faces.SelectMany(f => f.Sources))
            {
                var reference = new FaceReference(source, source);
                ReferenceResolver.Resolve(reference, after.Topology!).Status.Should().Be(ReferenceResolver.Resolve(reference, before).Status, $"{source} after {edit}");
            }
        }
    }

    /// <summary>The tangent arc of the round from one touching point to the other, round its centre.</summary>
    static ProfileEdge2D Round((double U, double V) from, (double U, double V) to, (double U, double V) c)
    {
        var (au, av, bu, bv) = (from.U - c.U, from.V - c.V, to.U - c.U, to.V - c.V);
        return new ProfileEdge2D(from.U, from.V, to.U, to.V, Math.Tan(Math.Atan2(au * bv - av * bu, au * bu + av * bv) / 4));
    }

    static double Area(params ProfileEdge2D[] loop) => loop.Sum(e => e.Area);

    /// <summary>Largest |distance| of the round's own mesh vertices from its exact surface.</summary>
    double Deviation(FrepResult result, Func<Point3D, double> distance)
    {
        var owner = result.Part.Surfaces.Single(s => s.Source == "round/face").Id;
        var mesh = result.Mesh!;
        var points = mesh.Quads.Where(q => q.Owner == owner).SelectMany(q => new[] { q.A, q.B, q.C, q.D }).Distinct()
            .Select(v => mesh.Vertices[v])
            .Where(p => result.Part.Field!.Sample(p) is var s && Math.Abs(s.Value) < 1e-9 && s.Owner == owner).ToList();
        points.Should().NotBeEmpty();
        var deviation = points.Max(p => Math.Abs(distance(p)));
        output.WriteLine($"deviation {deviation:E2} over {points.Count} vertices");
        return deviation;
    }

    double Volume(FrepResult result)
    {
        var volume = MassProperties.Of(result.Part.Field!, result.Mesh!).Volume;
        output.WriteLine($"volume {volume:F6}");
        return volume;
    }

    static double Sq(double x) => x * x;
}
