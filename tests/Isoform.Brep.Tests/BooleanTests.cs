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
    public void Cylinders_at_an_angle_meet_in_traced_curves()
    {
        // A plate with a Ø8 hole, drilled Ø4 along x through the hole's axis: the bore's volume in the plate less what
        // it shares with the hole (two cylinders crossing square, radii 2 and 4: 8 ∫ √(4 − y²) √(16 − y²) dy over [0, 2]).
        var across = RigidTransform3D.FromFrame(new Point3D(-1, 30, 5), new Vector3D(1, 0, 0), new Vector3D(0, 1, 0));
        var holed = Prism(Rectangle(0, 0, 100, 60), 0, 10, null, Circle(50, 30, 4));
        var r = Booleans.Subtract(holed, Prism(Circle(0, 0, 2), 0, 102, across));
        r.Problem.Should().BeNull();
        var shared = 8 * Documents.Smooth(t => 4 * Math.Cos(t) * Math.Cos(t) * Math.Sqrt(16 - 4 * Math.Sin(t) * Math.Sin(t)), 0, Math.PI / 2);
        var volume = 60_000 - 160 * Math.PI - (400 * Math.PI - shared);
        MassProperties.Volume(r.Bodies![0].Body).Should().BeApproximately(volume, 1e-10 * volume);
        r.Bodies[0].Body.Edges.Should().Contain(e => e.Curve is Trace3);
    }

    [Theory]
    [InlineData(1, -2, 1)]
    [InlineData(0, -1, 1)]
    public void A_ball_round_a_cube_corner_takes_its_octant(double ax, double ay, double az)
    {
        // Planes through the ball's centre at an angle to its axis cut it in great circles; the cube keeps 27 − π·4/3.
        var centre = new Point3D(38, 28, 8);
        var cube = Extrusion.Extrude(new Region(Rectangle(0, 0, 3, 3)), RigidTransform3D.FromFrame(centre, new Vector3D(0, 0, 1), new Vector3D(1, 0, 0)), 0, 3).Result!.Body;
        var diagonal = new Vector3D(1, 1, 1).Normalized();
        var axis = new Vector3D(ax, ay, az).Normalized();
        var half = new ProfileLoop([new ArcSegment(new Point2D(0, 0), 2, -Math.PI / 2, Math.PI), new LineSegment(new Point2D(0, 2), new Point2D(0, -2))]);
        var ball = Revolution.Revolve(new Region(half), RigidTransform3D.FromFrame(centre, axis, (diagonal * -1 - axis * axis.Dot(diagonal * -1)).Normalized())).Result!.Body;
        var (left, problem) = Booleans.Subtract(cube, ball);
        problem.Should().BeNull();
        MassProperties.Volume(left!.Single().Body).Should().BeApproximately(27 - Math.PI * 4 / 3, 1e-12);
        left.Single().Body.Faces.Should().ContainSingle(f => f.Surface is Sphere);
    }

    [Fact]
    public void Holes_drilled_one_by_one_are_carried_and_one_drilled_round_another_takes_it()
    {
        // Each boolean edits the plate: the walls copied, the earlier holes carried whole into the top and bottom.
        var body = Plate;
        for (var i = 0; i < 5; i++)
        for (var j = 0; j < 3; j++)
            body = One(Booleans.Subtract(body, Prism(Circle(10 + 20 * i, 10 + 20 * j, 2), -1, 11))).Body;
        MassProperties.Volume(body).Should().BeApproximately(60_000 - 15 * Math.PI * 4 * 10, 1e-9);
        BodyCheck.Problems(body).Should().BeEmpty();

        // A hole of radius 8 round the one at (30, 30): that one's loop lies in the disc it takes out.
        var wider = One(Booleans.Subtract(body, Prism(Circle(30, 30, 8), -1, 11))).Body;
        MassProperties.Volume(wider).Should().BeApproximately(60_000 - 14 * Math.PI * 4 * 10 - Math.PI * 64 * 10, 1e-9);
        BodyCheck.Problems(wider).Should().BeEmpty();
        wider.Faces.Count(f => f.Surface is Cylinder).Should().Be(15);
    }

    // The face of a body on the plane y = 0 (the front, seen from -y).
    static Face Front(Body body) => body.Faces.Single(f => f.Surface is Plane p && Math.Abs(p.Frame.ZAxis.Y) > 0.99 && Math.Abs(p.Frame.Origin.Y) < 1e-9);

    [Fact]
    public void Ribs_joined_one_by_one_grow_the_front_and_back_by_edits()
    {
        // Each rib's front and back merge with the base's: one face each side that gains a tooth per join, its loop
        // spliced, not traced again; everything else the earlier joins made is copied.
        var body = Prism(Rectangle(0, 0, 125, 20), 0, 5);
        for (var k = 0; k < 12; k++)
        {
            body = One(Booleans.Unite(body, Prism(Rectangle(5 + 10 * k, 0, 10 + 10 * k, 20), 5, 15))).Body;
            Solid.Edited(body).Should().BeTrue($"rib {k} is joined by an edit");
        }
        MassProperties.Volume(body).Should().BeApproximately(125 * 20 * 5 + 12 * 5 * 20 * 10, 1e-9);
        BodyCheck.Problems(body).Should().BeEmpty();
        body.Faces.Should().HaveCount(4 * 12 + 6);
        var front = Front(body);
        front.Loops.Should().ContainSingle();
        body.CoedgesOf(front.Loops[0]).Should().HaveCount(4 * 12 + 4);
    }

    [Fact]
    public void A_block_filling_the_gap_between_two_posts_closes_the_front()
    {
        // The front of a U, and a block in its gap flush with the posts: the faces merge into one rectangle, and the top
        // edges either side of the block's join through the corners they no longer share with anything.
        var u = One(Booleans.Unite(Prism(Rectangle(0, 0, 30, 20), 0, 10), Prism(Rectangle(0, 0, 10, 20), 10, 20))).Body;
        u = One(Booleans.Unite(u, Prism(Rectangle(20, 0, 30, 20), 10, 20))).Body;
        var r = One(Booleans.Unite(u, Prism(Rectangle(10, 0, 20, 20), 10, 20))).Body;
        MassProperties.Volume(r).Should().BeApproximately(30 * 20 * 20, 1e-9);
        BodyCheck.Problems(r).Should().BeEmpty();
        r.Faces.Should().HaveCount(6);
        r.Edges.Should().HaveCount(12);
    }

    [Fact]
    public void A_block_closing_a_c_makes_a_hole_in_the_front()
    {
        // The block meets the C's front along two stretches apart: the front gains a hole, which a splice cannot make, so
        // the faces are traced again.
        var c = One(Booleans.Unite(Prism(Rectangle(0, 0, 30, 20), 0, 10), Prism(Rectangle(0, 0, 10, 20), 10, 20))).Body;
        c = One(Booleans.Unite(c, Prism(Rectangle(0, 0, 25, 20), 20, 30))).Body;
        var r = One(Booleans.Unite(c, Prism(Rectangle(15, 0, 25, 20), 10, 20))).Body;
        MassProperties.Volume(r).Should().BeApproximately((30 * 10 + 10 * 10 + 25 * 10 + 10 * 10) * 20, 1e-9);
        BodyCheck.Problems(r).Should().BeEmpty();
        BodyCheck.Genus(r, new ShellId(0)).Should().Be(1);
        Front(r).Loops.Should().HaveCount(2);
    }

    [Fact]
    public void A_rib_flush_with_the_end_merges_with_it()
    {
        var body = Prism(Rectangle(0, 0, 40, 20), 0, 5);
        body = One(Booleans.Unite(body, Prism(Rectangle(5, 0, 10, 20), 5, 15))).Body;
        var r = One(Booleans.Unite(body, Prism(Rectangle(35, 0, 40, 20), 5, 15))).Body;
        MassProperties.Volume(r).Should().BeApproximately(40 * 20 * 5 + 2 * 5 * 20 * 10, 1e-9);
        BodyCheck.Problems(r).Should().BeEmpty();
        // The base's bottom, front, back and ends (the right one and rib 2's right are one face), two strips of its top,
        // rib 1's left, top and right, rib 2's left and top.
        r.Faces.Should().HaveCount(5 + 2 + 3 + 2);
        body.CoedgesOf(Front(body).Loops[0]).Should().HaveCount(8);
        r.CoedgesOf(Front(r).Loops[0]).Should().HaveCount(10);
    }
}
