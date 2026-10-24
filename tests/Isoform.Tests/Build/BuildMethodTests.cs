using BasAutomation.Geometry;
using Isoform.Build;
using Isoform.Document;
using Isoform.Geometry;
using Isoform.Modeling;

namespace Isoform.Tests.Build;

/// <summary>
/// The plate and tube methods on small designs (a T of two plates, crossing bars, a bar into a disc's rim), same parts
/// by congruence, and the plate and tube shapes the outputs read.
/// </summary>
public class BuildMethodTests
{
    static readonly FrepModeler Modeler = new(new ModelingSettings { CellSize = 2 });

    static Profile Circle(double cx, double cy, double r) => new([cx - r, cy], [new ProfileEdge("a", [cx + r, cy], 1), new ProfileEdge("b", [cx - r, cy], 1)]);

    static BuildResult Run(PartDocument design, params BuildSection[] sections) =>
        Rationaliser.PlatesAndTubes().Run(new BuildDocument("design.json", sections), design, Modeler.Regenerate(design));

    static Field Placed(PartDocument document, Placement placement) =>
        new PlacedField(new Compiler(new ModelingSettings()).Compile(document).Field!, AssemblyModeler.FrameOf(placement));

    // ---- Plates ----

    [Fact]
    public void A_plate_after_a_plate_runs_exactly_along_its_face()
    {
        // A T: a base 100 × 40 × 10 and an upright 10 × 40 × 60 on it.
        var design = new PartDocument([new Block("base", BooleanOp.New, [0, 0, 0], [100, 40, 10]), new Block("upright", BooleanOp.Join, [45, 0, 10], [10, 40, 60])]);

        var result = Run(design, new PlateSection("base", 10, Face: "base/z-"), new PlateSection("upright", 10, Plane: new SketchPlane([45, 0, 0], [0, 1, 0], [1, 0, 0])));

        result.Diagnostics.Should().BeEmpty();
        result.Parts.Keys.Should().Equal("base.json", "upright.json");
        var upright = PlateShape.Of(result.Parts["upright.json"])!;
        // The base plate's frame has its underside up (its outer face is the design's bottom): its start cap is the joint.
        upright.Sketch.Entities.Select(e => e.Id).Should().BeEquivalentTo(["base:plate-start~1", "upright-y-~1", "upright-y+~1", "upright-z+~1"]);
        upright.Edges.Should().OnlyContain(e => e.Kind == OutlineEdgeKind.Line);
        upright.Area.Should().BeApproximately(40 * 60, 1e-9, "the upright stops on the base's top face, exactly");
        // In the world the upright stands on the base: from z = 10 up, x 45 to 55.
        var placed = Placed(result.Parts["upright.json"], result.Bundle.Assembly.Components[1].Placement);
        placed.Value(new Point3D(50, 20, 10.001)).Should().BeNegative();
        placed.Value(new Point3D(50, 20, 9.999)).Should().BePositive();
        placed.Value(new Point3D(54.999, 20, 69.999)).Should().BeNegative();
        placed.Value(new Point3D(55.001, 20, 40)).Should().BePositive();
        // The base's own placement: its frame on its underside, normal down; the base runs upwards into the material.
        var basePlate = Placed(result.Parts["base.json"], result.Bundle.Assembly.Components[0].Placement);
        basePlate.Value(new Point3D(1, 1, 9.999)).Should().BeNegative();
        basePlate.Value(new Point3D(1, 1, 10.001)).Should().BePositive();
    }

    [Fact]
    public void A_plane_makes_a_plate_of_every_region_and_near_picks_one()
    {
        var design = new PartDocument([new Block("a", BooleanOp.New, [0, 0, 0], [40, 20, 10]), new Block("b", BooleanOp.Join, [60, 0, 0], [30, 20, 10])]);
        var xy = new SketchPlane([0, 0, 0], [1, 0, 0], [0, 0, 1]);

        var all = Run(design, new PlateSection("plates", 10, Plane: xy));
        var near = Run(design, new PlateSection("plates", 10, Plane: xy, Near: [80, 5, 0]));

        all.Bundle.Assembly.Components.Select(c => c.Id).Should().Equal("plates-1", "plates-2");
        all.Parts.Values.Select(p => PlateShape.Of(p)!.Area).Should().BeEquivalentTo([800.0, 600.0]);
        PlateShape.Of(near.Parts.Values.Single())!.Area.Should().BeApproximately(600, 1e-9);
        var origin = near.Bundle.Assembly.Components.Single().Placement.Origin;
        (new Point3D(origin[0], origin[1], origin[2]) - new Point3D(60, 0, 0)).Length.Should().BeLessThan(1e-9);
    }

    // ---- Tubes ----

    // A bar Ø20 along x from −50 to 50 and a post Ø10 standing on its axis, up to z = 60.
    static readonly PartDocument Cross = new(
    [
        new Extrude("bar", BooleanOp.New, new SketchPlane([-50, 0, 0], [0, 1, 0], [1, 0, 0]), Circle(0, 0, 10), 0, 100),
        new Extrude("post", BooleanOp.Join, new SketchPlane([0, 0, 0], [1, 0, 0], [0, 0, 1]), Circle(0, 0, 5), 0, 60),
    ]);

    [Fact]
    public void A_tube_that_leaves_the_design_is_cut_square()
    {
        var result = Run(Cross, new TubeSection("bar", 20, 2, Along: "bar/a"));

        result.Diagnostics.Should().BeEmpty();
        var tube = TubeShape.Of(result.Parts["bar.json"])!;
        tube.Length.Should().BeApproximately(100, 1e-9);
        (tube.AngleA, tube.AngleB, tube.LongPoint, tube.ShortPoint).Should().Be((0, 0, tube.Length, tube.Length));
        var placement = result.Bundle.Assembly.Components.Single().Placement;
        (new Point3D(placement.Origin[0], placement.Origin[1], placement.Origin[2]) - new Point3D(-50, 0, 0)).Length.Should().BeLessThan(1e-9);
        (new Vector3D(placement.Normal[0], placement.Normal[1], placement.Normal[2]) - new Vector3D(1, 0, 0)).Length.Should().BeLessThan(1e-12);

        // Along a line through the bar instead of its face: the same tube.
        var line = Run(Cross, new TubeSection("bar", 20, 2, Line: [[-30, 0, 0], [40, 0, 0]]));
        TubeShape.Of(line.Parts["bar.json"])!.Length.Should().BeApproximately(100, 1e-9);
    }

    [Fact]
    public void A_tube_that_ends_on_another_tube_is_refused()
    {
        var result = Run(Cross, new TubeSection("bar", 20, 2, Along: "bar/a"), new TubeSection("post", 10, 1, Along: "post/a"));

        result.Diagnostics.Should().ContainSingle().Which.Should().Match<Diagnostic>(d => d.Code == BuildDiagnostic.TubeMeetsTube && d.FeatureId == "post");
        result.Diagnostics[0].Message.Should().Contain("'post/a'").And.Contain("'bar'");
        result.Bundle.Assembly.Components.Select(c => c.Id).Should().Equal("bar");
    }

    [Fact]
    public void A_tube_that_ends_on_a_curved_face_is_refused()
    {
        // A disc plate Ø100 × 10 and a bar Ø4 into its rim above the plate's mid-plane, so the plate does not take it.
        var design = new PartDocument(
        [
            new Extrude("disc", BooleanOp.New, new SketchPlane([0, 0, 0], [1, 0, 0], [0, 0, 1]), Circle(0, 0, 50), 0, 10),
            new Extrude("bar", BooleanOp.Join, new SketchPlane([40, 0, 7.5], [0, 1, 0], [1, 0, 0]), Circle(0, 0, 2), 0, 80),
        ]);

        var result = Run(design, new PlateSection("disc", 10, Face: "disc/start"), new TubeSection("bar", 4, 0.5, Along: "bar/a"));

        result.Diagnostics.Should().ContainSingle().Which.Code.Should().Be(BuildDiagnostic.OpenEnd);
        result.Diagnostics[0].Message.Should().Contain("cylinder").And.Contain("'disc'");
        result.Bundle.Assembly.Components.Select(c => c.Id).Should().Equal("disc");
    }

    [Fact]
    public void A_tube_between_two_plates_ends_on_their_faces()
    {
        // A post Ø20 from z = 0 to 100 between a floor and a ceiling 10 thick, the ceiling tilted 20° round x.
        var tilt = 20 * Math.PI / 180;
        var design = new PartDocument(
        [
            new Block("floor", BooleanOp.New, [-50, -50, -10], [100, 100, 10]),
            new Extrude("post", BooleanOp.Join, new SketchPlane([0, 0, -8], [1, 0, 0], [0, 0, 1]), Circle(0, 0, 10), 0, 123),
            new Extrude("ceiling", BooleanOp.Join, new SketchPlane([0, 0, 100], [1, 0, 0], [0, -Math.Sin(tilt), Math.Cos(tilt)]), new Profile([-50, -50],
                [new ProfileEdge("s", [50, -50]), new ProfileEdge("e", [50, 50]), new ProfileEdge("n", [-50, 50]), new ProfileEdge("w", [-50, -50])]), 0, 10),
        ]);

        var result = Run(design, new PlateSection("floor", 10, Face: "floor/z-"), new PlateSection("ceiling", 10, Face: "ceiling/end"),
            new TubeSection("post", 20, 2, Along: "post/a"));

        result.Diagnostics.Should().BeEmpty();
        var tube = TubeShape.Of(result.Parts["post.json"])!;
        // From the floor's top (z = 0) to the ceiling's underside, which the axis meets at z = 100.
        tube.Length.Should().BeApproximately(100, 1e-9);
        tube.AngleA.Should().BeApproximately(0, 1e-9);
        tube.AngleB.Should().BeApproximately(20, 1e-9);
        tube.LongPoint.Should().BeApproximately(100 + 10 * Math.Tan(tilt), 1e-9);
        var post = result.Bundle.Assembly.Components.Single(c => c.Id == "post").Placement;
        post.Origin.Select(Math.Abs).Max().Should().BeLessThan(1e-9);
    }

    // ---- Same parts ----

    // An L-shaped plate 60 × 40 with a Ø10 hole: no symmetry, so it matches its turned and mirrored copies one way only.
    static PartDocument Ell(bool mirrored, double t = 8)
    {
        double[] P(double x, double y) => [x, mirrored ? -y : y];
        var points = new Dictionary<string, double[]>
        {
            ["a"] = P(0, 0), ["b"] = P(60, 0), ["c"] = P(60, 15), ["d"] = P(15, 15), ["e"] = P(15, 40), ["f"] = P(0, 40), ["o"] = P(7, 7),
        };
        var lines = new[] { "ab", "bc", "cd", "de", "ef", "fa" }.Select(s => (SketchEntity)new SketchLine(s, s[..1], s[1..])).Append(new SketchCircle("hole", "o", 5));
        return new([new Sketch("outline", new SketchPlacement(new SketchPlane([0, 0, 0], [1, 0, 0], [0, 0, 1])), points, [.. lines], []),
            new Extrude("plate", BooleanOp.New, null, null, 0, t) { Sketch = "outline" }]);
    }

    static void SameSolid(Field a, Field b)
    {
        var rng = new Random(3);
        for (var i = 0; i < 3000; i++)
        {
            var p = new Point3D(rng.NextDouble() * 200 - 100, rng.NextDouble() * 200 - 100, rng.NextDouble() * 200 - 100);
            a.Value(p).Should().BeApproximately(b.Value(p), 1e-9);
        }
    }

    [Fact]
    public void A_plate_turned_over_is_the_same_part()
    {
        var at = new Placement([10, 20, 30], [0.6, 0.8, 0], [0, 0, 1]);
        var (plate, mirrored) = (Ell(false), Ell(true));

        var placement = SameParts.Match(mirrored, at, plate);

        placement.Should().NotBeNull();
        SameSolid(Placed(plate, placement!), Placed(mirrored, at));
        SameParts.Match(Ell(true, t: 10), at, plate).Should().BeNull("a thicker plate is another part");
        SameParts.Match(mirrored, at, mirrored).Should().BeSameAs(at, "the same document is the same part where it is");
    }

    [Fact]
    public void A_tube_end_for_end_is_the_same_part()
    {
        var (ten, twenty) = (10 * Math.PI / 180, 20 * Math.PI / 180);
        var endA = new Vector3D(Math.Sin(ten), 0, -Math.Cos(ten));
        var endB = new Vector3D(0, Math.Sin(twenty), Math.Cos(twenty));
        var tube = TubeMethod.Document(30, 3, 200, endA, endB);
        // The same tube from the other end, turned a little round its axis: end A 20° off square, end B 10°.
        var (c, s) = (Math.Cos(0.3), Math.Sin(0.3));
        Vector3D Turned(Vector3D v) => new(c * v.X - s * v.Y, s * v.X + c * v.Y, v.Z);
        Vector3D Reversed(Vector3D v) => new(v.X, -v.Y, -v.Z);
        var other = TubeMethod.Document(30, 3, 200, Turned(Reversed(endB)), Turned(Reversed(endA)));
        var at = new Placement([5, -5, 50], [0, 1, 0], [1, 0, 0]);

        var placement = SameParts.Match(other, at, tube);

        placement.Should().NotBeNull();
        SameSolid(Placed(tube, placement!), Placed(other, at));
        SameParts.Match(TubeMethod.Document(30, 3, 201, endA, endB), at, tube).Should().BeNull();
        SameParts.Match(TubeMethod.Document(30, 3, 200, endA, new Vector3D(0, 0, 1)), at, tube).Should().BeNull();
    }

    // ---- Shapes ----

    [Fact]
    public void A_plates_area_and_box_are_exact()
    {
        // A slot 100 long with semicircular ends of radius 10, its arcs written either way round.
        var points = new Dictionary<string, double[]> { ["a"] = [0, 0], ["b"] = [100, 0], ["c"] = [100, 20], ["d"] = [0, 20], ["r"] = [100, 10], ["l"] = [0, 10] };
        var slot = new PartDocument([new Sketch("outline", new SketchPlacement(new SketchPlane([0, 0, 0], [1, 0, 0], [0, 0, 1])), points,
            [new SketchLine("s", "a", "b"), new SketchArc("e", "r", "b", "c"), new SketchLine("n", "c", "d"), new SketchArc("w", "l", "d", "a")], []),
            new Extrude("plate", BooleanOp.New, null, null, 0, 5) { Sketch = "outline" }]);

        var shape = PlateShape.Of(slot)!;

        shape.Area.Should().BeApproximately(100 * 20 + Math.PI * 100, 1e-9);
        shape.Box.Should().Be((-10, 0, 110, 20));
        shape.Thickness.Should().Be(5);
    }

    [Fact]
    public void A_tubes_long_and_short_points_and_turn()
    {
        var tilt = 15 * Math.PI / 180;
        var (down, up) = (new Vector3D(Math.Sin(tilt), 0, -Math.Cos(tilt)), new Vector3D(Math.Sin(tilt), 0, Math.Cos(tilt)));
        // Parallel cuts lean the same way at both ends; a V leans them opposite ways.
        var parallel = TubeShape.Of(TubeMethod.Document(40, 2, 500, down, new Vector3D(-up.X, 0, up.Z)))!;
        var vee = TubeShape.Of(TubeMethod.Document(40, 2, 500, down, up))!;

        (parallel.Turn, vee.Turn).Should().Be((180, 0));
        parallel.LongPoint.Should().BeApproximately(500 + 40 * Math.Tan(tilt), 1e-9);
        vee.LongPoint.Should().BeApproximately(500 + 40 * Math.Tan(tilt), 1e-9);
        vee.ShortPoint.Should().BeApproximately(500 - 40 * Math.Tan(tilt), 1e-9);
        parallel.Volume.Should().BeApproximately(Math.PI * (400 - 324) * 500, 1e-6);
        (parallel.AngleA, parallel.AngleB).Should().Be((parallel.AngleA, parallel.AngleA));
        parallel.AngleA.Should().BeApproximately(15, 1e-9);
    }
}
