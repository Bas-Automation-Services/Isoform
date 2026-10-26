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

    [Fact]
    public void A_bore_through_a_plate_leaves_a_hole()
    {
        var r = One(Booleans.Subtract(Plate, Prism(Circle(50, 30, 8), -1, 11)));
        MassProperties.Volume(r.Body).Should().BeApproximately(60000 - Math.PI * 640, 1e-9);
        r.Body.Faces.Should().HaveCount(7);
        BodyCheck.Genus(r.Body, new ShellId(0)).Should().Be(1);
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
    public void Ellipses_are_refused()
    {
        var tilted = RigidTransform3D.FromFrame(new Point3D(50, 30, -5), new Vector3D(0, 1, 1), new Vector3D(1, 0, 0));
        var r = Booleans.Subtract(Plate, Prism(Circle(0, 0, 4), 0, 30, tilted));
        r.Bodies.Should().BeNull();
        r.Problem.Should().Contain("ellipse");
    }
}
