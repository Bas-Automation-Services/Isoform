using BasAutomation.Geometry;
using static Isoform.Brep.Tests.Shapes;

namespace Isoform.Brep.Tests;

public class ExtrusionTests
{
    static readonly RigidTransform3D World = RigidTransform3D.Identity;

    static ExtrudeResult Extrude(Region region, double start, double end, RigidTransform3D? frame = null)
    {
        var (result, problem) = Extrusion.Extrude(region, frame ?? World, start, end);
        problem.Should().BeNull();
        BodyCheck.Problems(result!.Body).Should().BeEmpty();
        return result;
    }

    static void Counts(Body body, int vertices, int edges, int faces, int genus)
    {
        body.Vertices.Length.Should().Be(vertices);
        body.Edges.Length.Should().Be(edges);
        body.Faces.Length.Should().Be(faces);
        BodyCheck.Genus(body, new ShellId(0)).Should().Be(genus);
    }

    static void Close(double actual, double expected) => actual.Should().BeApproximately(expected, 1e-12 * Math.Max(1, Math.Abs(expected)));

    [Fact]
    public void A_block_is_a_valid_solid_with_exact_volume()
    {
        var r = Extrude(new Region(Rectangle(0, 0, 100, 50)), 0, 10);

        Counts(r.Body, 8, 12, 6, genus: 0);
        Close(MassProperties.Volume(r.Body), 50_000);
        Close(MassProperties.Area(r.Body, r.EndCap), 5_000);
        Close(MassProperties.Area(r.Body, r.Sides[0][0]), 1_000);
        Close(MassProperties.Area(r.Body, r.Sides[0][1]), 500);
    }

    [Fact]
    public void A_plate_with_a_hole_has_a_seam_and_genus_one()
    {
        var r = Extrude(new Region(Rectangle(0, 0, 100, 50), [Circle(30, 25, 10)]), 0, 10);

        // 4 + 4 corners and one seam vertex top and bottom; 12 block edges and the hole's two circles and seam.
        Counts(r.Body, 10, 15, 7, genus: 1);
        Close(MassProperties.Volume(r.Body), (5_000 - Math.PI * 100) * 10);
        var wall = r.Body[r.Sides[1][0]];
        wall.Surface.Should().BeOfType<Cylinder>();
        wall.SameSense.Should().BeFalse("the hole's wall faces its axis");
        Close(MassProperties.Area(r.Body, r.Sides[1][0]), 2 * Math.PI * 10 * 10);
        r.Body[r.EndCap].Loops.Length.Should().Be(2);

        var seam = r.Body.Edges.Select((e, i) => new EdgeId(i)).Single(e => r.Body.UsesOf(e).All(c => r.Body.FaceOf(c) == r.Sides[1][0]));
        var us = r.Body.UsesOf(seam).Select(c => r.Body[c].Pcurve.At(0).X).ToList();
        Math.Abs(us[0] - us[1]).Should().BeApproximately(2 * Math.PI, 1e-15, "the seam's two p-curves are a period apart");
    }

    [Fact]
    public void A_circle_extrudes_to_a_cylinder()
    {
        var r = Extrude(new Region(Circle(0, 0, 5)), -2, 3);

        Counts(r.Body, 2, 3, 3, genus: 0);
        Close(MassProperties.Volume(r.Body), Math.PI * 25 * 5);
        r.Body[r.Sides[0][0]].SameSense.Should().BeTrue();
    }

    [Fact]
    public void A_slot_has_tangent_planar_and_cylindrical_sides()
    {
        var r = Extrude(new Region(Slot(40, 6)), 0, 8);

        Counts(r.Body, 8, 12, 6, genus: 0);
        Close(MassProperties.Volume(r.Body), (40 * 12 + Math.PI * 36) * 8);
        r.Sides[0].Select(f => r.Body[f].Surface.GetType()).Should().Equal(typeof(Plane), typeof(Cylinder), typeof(Plane), typeof(Cylinder));
    }

    [Fact]
    public void A_concave_arc_uses_its_cylinder_against_its_sense()
    {
        var r = Extrude(new Region(Bitten()), 0, 5);

        Close(MassProperties.Volume(r.Body), BittenArea * 5);
        r.Body[r.Sides[0][1]].SameSense.Should().BeTrue("the rounded corner is convex");
        r.Body[r.Sides[0][4]].SameSense.Should().BeFalse("the bite is concave");
    }

    [Fact]
    public void Loops_given_the_other_way_round_are_turned_and_keep_their_segment_order()
    {
        var outer = Rectangle(0, 0, 100, 50).Reversed(); // clockwise
        var hole = Circle(30, 25, 10);                    // counter-clockwise
        var r = Extrude(new Region(outer, [hole]), 0, 10);

        Close(MassProperties.Volume(r.Body), (5_000 - Math.PI * 100) * 10);
        // Segment 0 of the reversed rectangle runs (0,0)→(0,50): its face is the x = 0 side, normal −X.
        var side = (Plane)r.Body[r.Sides[0][0]].Surface;
        side.Frame.ZAxis.X.Should().BeApproximately(-1, 1e-15);
        side.Frame.Origin.X.Should().BeApproximately(0, 1e-15);
    }

    [Fact]
    public void Several_holes_and_a_two_segment_hole()
    {
        var r = Extrude(new Region(Rectangle(-50, -30, 50, 30), [Circle(-30, 0, 8), Circle(30, 0, 8), D(0, 0, 10).Reversed()]), 0, 4);

        BodyCheck.Genus(r.Body, new ShellId(0)).Should().Be(3);
        Close(MassProperties.Volume(r.Body), (100 * 60 - 2 * Math.PI * 64 - Math.PI * 100 / 2) * 4);
    }

    [Fact]
    public void Placement_moves_the_solid_but_not_its_volume()
    {
        var frame = RigidTransform3D.FromFrame(new Point3D(1000, -250, 40), new Vector3D(1, 2, 3), new Vector3D(-2, 1, 0));
        var r = Extrude(new Region(Slot(40, 6), [Circle(20, 0, 3)]), -5, 5, frame);

        // Exact up to rounding of coordinates around 1000.
        MassProperties.Volume(r.Body).Should().BeApproximately((40 * 12 + Math.PI * 36 - Math.PI * 9) * 10, 1e-9);
        var bottom = r.Body.Vertices[0].Point;
        frame.ApplyInverse(bottom).Z.Should().BeApproximately(-5, 1e-12);
    }

    [Theory]
    [InlineData(0, 0, "not above the tolerance")]
    [InlineData(5, 5, "not above the tolerance")]
    [InlineData(5, 1, "not above the tolerance")]
    public void An_empty_range_is_refused(double start, double end, string reason)
    {
        var (result, problem) = Extrusion.Extrude(new Region(Rectangle(0, 0, 1, 1)), World, start, end);
        result.Should().BeNull();
        problem.Should().Contain(reason);
    }
}
