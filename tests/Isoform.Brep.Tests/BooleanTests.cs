using BasAutomation.Geometry;
using Xunit.Abstractions;
using static Isoform.Brep.Tests.Shapes;

namespace Isoform.Brep.Tests;

/// <summary>
/// Booleans of extruded solids, checked by their exact volume and the shape of the result (faces, shells, genus);
/// every result also passes <see cref="BodyCheck"/>, or the boolean fails.
/// </summary>
public class BooleanTests(ITestOutputHelper output)
{
    static readonly RigidTransform3D Xy = RigidTransform3D.Identity;

    static Body Prism(ProfileLoop loop, double z0, double z1, RigidTransform3D? frame = null, params ProfileLoop[] holes) =>
        Extrusion.Extrude(new Region(loop, holes), frame ?? Xy, z0, z1).Result!.Body;

    static readonly Body Plate = Prism(Rectangle(0, 0, 100, 60), 0, 10);

    Shaped One((IReadOnlyList<Shaped>? Bodies, string? Problem) result)
    {
        result.Problem.Should().BeNull();
        result.Bodies.Should().ContainSingle();
        var body = result.Bodies![0].Body;
        output.WriteLine($"{body.Faces.Length}F {body.Edges.Length}E {body.Vertices.Length}V {body.Shells.Length} shell(s), genus {BodyCheck.Genus(body, new ShellId(0))}");
        return result.Bodies[0];
    }

    // Experiment 8: solids turned round one axis meet in circles, so what is left is the difference of their profiles
    // turned round it (Pappus). The cone's profile is the triangle (5, 0), (10, 0), (5, 4), area 10, centroid 20/3 out.
    static Body Turned(ProfileLoop loop) => Revolution.Revolve(new Region(loop), Xy).Result!.Body;

    static readonly ProfileLoop Wedge = new([new LineSegment(P(5, 0), P(10, 0)), new LineSegment(P(10, 0), P(5, 4)), new LineSegment(P(5, 4), P(5, 0))]);

    [Fact]
    public void A_ring_bitten_out_of_a_cone()
    {
        // A tube of radius 1 round (7.5, 2), the slope's midpoint: the half disc inside the triangle goes, its
        // centroid 4/(3π) in from the slope along the inward normal (−4, −5)/√41.
        var r = One(Booleans.Subtract(Turned(Wedge), Turned(Circle(7.5, 2, 1))));
        var x = 7.5 - 4 / (3 * Math.PI) * 4 / Math.Sqrt(41);
        MassProperties.Volume(r.Body).Should().BeApproximately(2 * Math.PI * (10 * 20.0 / 3 - Math.PI / 2 * x), 1e-9);
        r.Body.Faces.Select(f => f.Surface.GetType().Name).Order().Should().Equal("Cone", "Cone", "Cylinder", "Plane", "Torus");
    }

    [Fact]
    public void Two_cones_round_one_axis()
    {
        // The other's slope ρ = 5.75 + 1.25 z crosses this one's ρ = 10 − 1.25 z at (7.875, 1.7); above z = 1 the
        // triangle (7, 1), (8.75, 1), (7.875, 1.7) goes, area 0.6125, centroid 7.875 out.
        var other = new ProfileLoop([new LineSegment(P(7, 1), P(12, 1)), new LineSegment(P(12, 1), P(12, 5)), new LineSegment(P(12, 5), P(7, 1))]);
        var r = One(Booleans.Subtract(Turned(Wedge), Turned(other)));
        MassProperties.Volume(r.Body).Should().BeApproximately(2 * Math.PI * (10 * 20.0 / 3 - 0.6125 * 7.875), 1e-9);
        // The first cone in two bands either side of the bite, the second's piece between them.
        r.Body.Faces.Count(f => f.Surface is Cone).Should().Be(3);
    }

    [Fact]
    public void A_bore_through_a_plate_leaves_a_hole()
    {
        var r = One(Booleans.Subtract(Plate, Prism(Circle(50, 30, 8), -1, 11)));
        MassProperties.Volume(r.Body).Should().BeApproximately(60000 - Math.PI * 640, 1e-9);
        r.Body.Faces.Should().HaveCount(7);
        BodyCheck.Genus(r.Body, new ShellId(0)).Should().Be(1);
    }

    [Fact]
    public void Bores_that_do_not_touch_go_through_in_one_boolean()
    {
        var bores = (from i in Enumerable.Range(0, 9) from j in Enumerable.Range(0, 5) select Prism(Circle(10 + 10 * i, 10 + 10 * j, 3), -1, 11)).ToList();
        Booleans.Apart(bores).Should().ContainSingle();
        var (tool, offsets) = Booleans.Lumps(bores);
        offsets.Should().Equal(Enumerable.Range(0, 45).Select(k => 3 * k));
        var r = One(Booleans.Subtract(Plate, tool));
        MassProperties.Volume(r.Body).Should().BeApproximately(60000 - 45 * Math.PI * 90, 1e-9);
        BodyCheck.Genus(r.Body, new ShellId(0)).Should().Be(45);
    }

    [Fact]
    public void Bores_that_overlap_go_in_separate_booleans()
    {
        var bores = new[] { Prism(Circle(30, 30, 8), -1, 11), Prism(Circle(40, 30, 8), -1, 11), Prism(Circle(70, 30, 8), -1, 11) };
        Booleans.Apart(bores).Should().BeEquivalentTo(new[] { new[] { 0, 2 }, new[] { 1 } }, o => o.WithStrictOrdering());
    }

    [Fact]
    public void A_pocket_from_the_top()
    {
        var r = One(Booleans.Subtract(Plate, Prism(Rectangle(20, 10, 60, 40), 4, 11)));
        MassProperties.Volume(r.Body).Should().BeApproximately(60000 - 7200, 1e-9);
        r.Body.Faces.Should().HaveCount(11);
    }

    [Fact]
    public void A_pocket_flush_with_the_top()
    {
        var r = One(Booleans.Subtract(Plate, Prism(Rectangle(20, 10, 60, 40), 4, 10)));
        MassProperties.Volume(r.Body).Should().BeApproximately(60000 - 7200, 1e-9);
        r.Body.Faces.Should().HaveCount(11);
    }

    [Fact]
    public void A_boss_standing_on_the_top()
    {
        var r = One(Booleans.Unite(Plate, Prism(Circle(50, 30, 10), 10, 25)));
        MassProperties.Volume(r.Body).Should().BeApproximately(60000 + Math.PI * 1500, 1e-9);
        r.Body.Faces.Should().HaveCount(8);
    }

    [Fact]
    public void A_block_overlapping_the_side()
    {
        var r = One(Booleans.Unite(Plate, Prism(Rectangle(80, 20, 120, 40), 0, 10)));
        MassProperties.Volume(r.Body).Should().BeApproximately(64000, 1e-9);
        r.Body.Faces.Should().HaveCount(10);
    }

    [Fact]
    public void A_boss_flush_with_two_sides()
    {
        var r = One(Booleans.Unite(Plate, Prism(Rectangle(0, 0, 30, 20), 10, 15)));
        MassProperties.Volume(r.Body).Should().BeApproximately(63000, 1e-9);
        r.Body.Faces.Should().HaveCount(9);
    }

    [Fact]
    public void A_bore_along_the_plate()
    {
        var alongX = RigidTransform3D.FromFrame(new Point3D(-1, 0, 0), new Vector3D(1, 0, 0), new Vector3D(0, 1, 0));
        var r = One(Booleans.Subtract(Plate, Prism(Circle(30, 5, 3), 0, 102, alongX)));
        MassProperties.Volume(r.Body).Should().BeApproximately(60000 - Math.PI * 900, 1e-9);
        r.Body.Faces.Should().HaveCount(7);
    }

    [Fact]
    public void A_cut_right_across_parts_the_plate()
    {
        var r = Booleans.Subtract(Plate, Prism(Rectangle(40, -1, 50, 61), -1, 11));
        r.Problem.Should().BeNull();
        r.Bodies.Should().HaveCount(2);
        r.Bodies!.Sum(b => MassProperties.Volume(b.Body)).Should().BeApproximately(54000, 1e-9);
    }

    [Fact]
    public void A_cavity_inside_is_a_void()
    {
        var r = One(Booleans.Subtract(Plate, Prism(Rectangle(20, 20, 40, 40), 2, 8)));
        r.Body.Shells.Should().HaveCount(2);
        MassProperties.Volume(r.Body).Should().BeApproximately(60000 - 2400, 1e-9);
    }

    [Fact]
    public void A_slot_through_a_bore_cuts_its_cylinder()
    {
        var holed = Prism(Rectangle(0, 0, 100, 60), 0, 10, null, Circle(50, 30, 8));
        var r = One(Booleans.Subtract(holed, Prism(Rectangle(46, -1, 54, 30), -1, 11)));
        // The slot takes 8 × 30 from the plate, less the part of the bore it overlaps: the half below y = 30 of the
        // disc's strip between x = 46 and 54, a·√(R² − a²) + R²·asin(a/R) for a = 4, R = 8.
        var halfStrip = 4 * Math.Sqrt(64 - 16) + 64 * Math.Asin(4.0 / 8);
        MassProperties.Volume(r.Body).Should().BeApproximately((6000 - Math.PI * 64 - (8 * 30 - halfStrip)) * 10, 1e-9);
    }

    [Fact]
    public void Disjoint_solids_stay_apart()
    {
        var r = Booleans.Unite(Plate, Prism(Rectangle(200, 0, 210, 10), 0, 10));
        r.Bodies.Should().HaveCount(2);
    }

    [Fact]
    public void A_circle_of_two_arcs_merges_into_one_cylinder()
    {
        var loop = new ProfileLoop([new ArcSegment(P(0, 0), 5, 0, Math.PI), new ArcSegment(P(0, 0), 5, Math.PI, Math.PI)]);
        var merged = Booleans.Merge(Prism(loop, 0, 3));
        merged.Body.Faces.Should().HaveCount(3);
        merged.Origins.Should().Contain(o => o.Count == 2);
        MassProperties.Volume(merged.Body).Should().BeApproximately(Math.PI * 75, 1e-9);
    }

    [Fact]
    public void A_bore_at_45_degrees_meets_the_faces_in_ellipses()
    {
        var tilted = RigidTransform3D.FromFrame(new Point3D(50, 30, -5), new Vector3D(0, 1, 1).Normalized(), new Vector3D(1, 0, 0));
        var r = One(Booleans.Subtract(Plate, Prism(Circle(0, 0, 4), 0, 30, tilted)));
        // Horizontal slices are ellipses of area π r² / cos 45°.
        MassProperties.Volume(r.Body).Should().BeApproximately(60000 - Math.PI * 160 * Math.Sqrt(2), 1e-9);
        r.Body.Faces.Should().HaveCount(7);
        r.Body.Edges.Select(e => e.Curve).OfType<Ellipse3>().Should().HaveCount(2);
        BodyCheck.Genus(r.Body, new ShellId(0)).Should().Be(1);
    }

    [Fact]
    public void A_cut_at_30_degrees_across_a_bore()
    {
        var plate = Prism(Rectangle(0, 0, 100, 50), 0, 10, null, Circle(30, 25, 5));
        var slope = Math.Tan(Math.PI / 6);
        var plane = RigidTransform3D.FromFrame(new Point3D(20, 25, 10), new Vector3D(slope, 0, 1).Normalized(), new Vector3D(0, 1, 0));
        var r = One(Booleans.Subtract(plate, Prism(Rectangle(-500, -500, 500, 500), 0, 500, plane)));
        // The plate under z = 10 − (x − 20)·tan 30°, less the bore's column under it (its mean height at x = 30).
        var zeroAt = 20 + 10 / slope;
        var prism = 50 * (20 * 10 + 0.5 * (zeroAt - 20) * 10);
        MassProperties.Volume(r.Body).Should().BeApproximately(prism - Math.PI * 25 * (10 - 10 * slope), 1e-9);
        r.Body.Edges.Select(e => e.Curve).OfType<Ellipse3>().Should().ContainSingle();
    }

    [Fact]
    public void Cylinders_at_an_angle_are_refused()
    {
        var across = RigidTransform3D.FromFrame(new Point3D(-1, 30, 5), new Vector3D(1, 0, 0), new Vector3D(0, 1, 0));
        var holed = Prism(Rectangle(0, 0, 100, 60), 0, 10, null, Circle(50, 30, 4));
        var r = Booleans.Subtract(holed, Prism(Circle(0, 0, 2), 0, 102, across));
        r.Bodies.Should().BeNull();
        r.Problem.Should().Contain("not parallel");
    }
}
