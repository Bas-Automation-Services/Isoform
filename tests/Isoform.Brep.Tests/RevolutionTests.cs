using BasAutomation.Geometry;
using Xunit.Abstractions;
using static Isoform.Brep.Tests.Shapes;

namespace Isoform.Brep.Tests;

/// <summary>Solids of revolution, checked by their exact volume (Pappus) and their shape.</summary>
public class RevolutionTests(ITestOutputHelper output)
{
    Body Revolve(ProfileLoop loop)
    {
        var (r, problem) = Revolution.Revolve(new Region(loop), RigidTransform3D.Identity);
        output.WriteLine(problem ?? "");
        problem.Should().BeNull();
        return r!.Body;
    }

    [Fact]
    public void A_washer()
    {
        var body = Revolve(Rectangle(5, 0, 10, 4));
        MassProperties.Volume(body).Should().BeApproximately(Math.PI * 75 * 4, 1e-9);
        body.Faces.Should().HaveCount(4);
        BodyCheck.Genus(body, new ShellId(0)).Should().Be(1);
    }

    [Fact]
    public void A_rectangle_on_the_axis_is_a_solid_cylinder_with_disc_ends()
    {
        // The edge on the axis sweeps nothing; the ends are discs, each bounded by one circle.
        var body = Revolve(Rectangle(0, 0, 5, 10));
        MassProperties.Volume(body).Should().BeApproximately(Math.PI * 25 * 10, 1e-9);
        body.Faces.Select(f => f.Surface.GetType().Name).Order().Should().Equal("Cylinder", "Plane", "Plane");
        body.Faces.Where(f => f.Surface is Plane).Should().OnlyContain(f => f.Loops.Length == 1);
        BodyCheck.Genus(body, new ShellId(0)).Should().Be(0);
    }

    // Experiment 12: an arc round a centre on the axis turns into a sphere, closed at a pole where it reaches the axis.
    static ProfileLoop DomedPost => new([new LineSegment(P(0, 0), P(5, 0)), new LineSegment(P(5, 0), P(5, 10)),
        new ArcSegment(P(0, 10), 5, 0, Math.PI / 2), new LineSegment(P(0, 15), P(0, 0))]);

    [Fact]
    public void A_half_circle_on_the_axis_turns_into_a_ball()
    {
        var body = Revolve(new ProfileLoop([new ArcSegment(P(0, 0), 5, -Math.PI / 2, Math.PI), new LineSegment(P(0, 5), P(0, -5))]));
        MassProperties.Volume(body).Should().BeApproximately(4.0 / 3 * Math.PI * 125, 1e-9);
        body.Faces.Should().ContainSingle().Which.Surface.Should().BeOfType<Sphere>();
        MassProperties.Area(body, new FaceId(0)).Should().BeApproximately(4 * Math.PI * 25, 1e-9);
        BodyCheck.Genus(body, new ShellId(0)).Should().Be(0);
    }

    [Fact]
    public void A_post_with_a_dome()
    {
        var body = Revolve(DomedPost);
        MassProperties.Volume(body).Should().BeApproximately(Math.PI * 25 * 10 + 2.0 / 3 * Math.PI * 125, 1e-9);
        body.Faces.Select(f => f.Surface.GetType().Name).Order().Should().Equal("Cylinder", "Plane", "Sphere");
    }

    [Fact]
    public void Half_a_domed_post_closes_its_pole_with_the_ends()
    {
        var (r, problem) = Revolution.Revolve(new Region(DomedPost), RigidTransform3D.Identity, angle: Math.PI);
        problem.Should().BeNull();
        MassProperties.Volume(r!.Body).Should().BeApproximately((Math.PI * 25 * 10 + 2.0 / 3 * Math.PI * 125) / 2, 1e-9);
        BodyCheck.Problems(r.Body).Should().BeEmpty();
    }

    [Fact]
    public void A_cone_coming_to_a_point_on_the_axis_closes_at_its_apex()
    {
        var triangle = new ProfileLoop([new LineSegment(P(0, 0), P(5, 0)), new LineSegment(P(5, 0), P(0, 8)), new LineSegment(P(0, 8), P(0, 0))]);
        var (result, problem) = Revolution.Revolve(new Region(triangle), RigidTransform3D.Identity);
        problem.Should().BeNull();
        MassProperties.Volume(result!.Body).Should().BeApproximately(Math.PI * 25 * 8 / 3, 1e-12);
        result.Body.Edges.Should().ContainSingle(e => e.Curve is Pole);
        BodyCheck.Problems(result.Body).Should().BeEmpty();
    }

    [Fact]
    public void An_arc_off_the_axis_reaching_it_is_refused()
    {
        var bite = new ProfileLoop([new LineSegment(P(0, 0), P(5, 0)), new ArcSegment(P(0, 0), 5, 0, Math.PI / 2), new LineSegment(P(0, 5), P(0, 0))]);
        var lens = new ProfileLoop([new LineSegment(P(0, 0), P(4, 0)), new ArcSegment(P(-1, 0), 5, 0, Math.Acos(0.2)), new LineSegment(P(0, Math.Sqrt(24)), P(0, 0))]);
        Revolution.Revolve(new Region(bite), RigidTransform3D.Identity).Problem.Should().BeNull("an arc round a centre on the axis is a sphere");
        Revolution.Revolve(new Region(lens), RigidTransform3D.Identity).Problem.Should().Contain("torus with no hole");
    }

    [Fact]
    public void A_ring_of_round_section_is_a_torus()
    {
        var body = Revolve(Circle(20, 0, 3));
        MassProperties.Volume(body).Should().BeApproximately(2 * Math.PI * Math.PI * 20 * 9, 1e-9);
        body.Faces.Should().ContainSingle().Which.Surface.Should().BeOfType<Torus>();
        // Pappus again: the circle's length turned round at its centre's distance.
        MassProperties.Area(body, new FaceId(0)).Should().BeApproximately(2 * Math.PI * 3 * 2 * Math.PI * 20, 1e-9);
    }

    [Fact]
    public void A_cone_face_has_the_area_of_its_frustum()
    {
        // From (10, 0) up to (5, 4): π (r1 + r2) times the slant height.
        var body = Revolve(new ProfileLoop([new LineSegment(P(5, 0), P(10, 0)), new LineSegment(P(10, 0), P(5, 4)), new LineSegment(P(5, 4), P(5, 0))]));
        var cone = new FaceId(body.Faces.ToList().FindIndex(f => f.Surface is Cone));
        MassProperties.Area(body, cone).Should().BeApproximately(Math.PI * 15 * Math.Sqrt(41), 1e-9);
    }

    [Fact]
    public void A_fillets_spandrel_round_a_hole_rim()
    {
        // The corner between a hole of radius 5 and a top face at z = 10, less the ball of radius 2 rolled round it.
        var loop = new ProfileLoop([new LineSegment(P(5, 10), P(7, 10)), new ArcSegment(P(7, 8), 2, Math.PI / 2, Math.PI / 2), new LineSegment(P(5, 8), P(5, 10))]);
        var body = Revolve(loop);
        var spandrel = 4 * (1 - Math.PI / 4);
        var centroid = 2 * (5.0 / 6 - Math.PI / 4) / (1 - Math.PI / 4);
        MassProperties.Volume(body).Should().BeApproximately(2 * Math.PI * (5 + centroid) * spandrel, 1e-9);
        body.Faces.Select(f => f.Surface.GetType().Name).Order().Should().Equal("Cylinder", "Plane", "Torus");
    }

    // A triangle on the axis side of a slope from (10, 0) up to (5, 4): area 10, centroid 20/3 from the axis.
    static ProfileLoop Wedge() => new([new LineSegment(P(5, 0), P(10, 0)), new LineSegment(P(10, 0), P(5, 4)), new LineSegment(P(5, 4), P(5, 0))]);

    [Fact]
    public void A_line_at_an_angle_turns_into_a_cone()
    {
        var body = Revolve(Wedge());
        MassProperties.Volume(body).Should().BeApproximately(2 * Math.PI * 20.0 / 3 * 10, 1e-9);
        body.Faces.Select(f => f.Surface.GetType().Name).Order().Should().Equal("Cone", "Cylinder", "Plane");
        BodyCheck.Problems(body).Should().BeEmpty();
    }

    [Fact]
    public void A_frustum_widening_upwards()
    {
        // The slope runs from (8, 4) down to (10, 0) on the right of a ring 5..10: area 5·4 − 4, centroid by parts.
        var loop = new ProfileLoop([new LineSegment(P(5, 0), P(10, 0)), new LineSegment(P(10, 0), P(12, 4)), new LineSegment(P(12, 4), P(5, 4)), new LineSegment(P(5, 4), P(5, 0))]);
        var body = Revolve(loop);
        // Rectangle 5..10 × 4 plus the triangle (10, 0), (12, 4), (10, 4) of area 4, centroid x = 32/3.
        MassProperties.Volume(body).Should().BeApproximately(2 * Math.PI * (20 * 7.5 + 4 * 32.0 / 3), 1e-9);
        BodyCheck.Problems(body).Should().BeEmpty();
    }

    [Fact]
    public void A_quarter_turn_of_a_cone_has_its_ends_closed()
    {
        // The flat ends hold the cone's straight lines through its apex.
        var (r, problem) = Revolution.Revolve(new Region(Wedge()), RigidTransform3D.Identity, angle: Math.PI / 2);
        output.WriteLine(problem ?? "");
        problem.Should().BeNull();
        MassProperties.Volume(r!.Body).Should().BeApproximately(Math.PI / 2 * 20.0 / 3 * 10, 1e-9);
        r.Body.Faces.Should().HaveCount(5);
        BodyCheck.Problems(r.Body).Should().BeEmpty();
    }

    [Fact]
    public void A_quarter_washer_has_its_ends_closed()
    {
        var (r, problem) = Revolution.Revolve(new Region(Rectangle(5, 0, 10, 4)), RigidTransform3D.Identity, angle: Math.PI / 2);
        problem.Should().BeNull();
        MassProperties.Volume(r!.Body).Should().BeApproximately(Math.PI * 75, 1e-9);
        r.Body.Faces.Should().HaveCount(6);
    }

    [Fact]
    public void Half_a_ring_of_round_section()
    {
        var (r, problem) = Revolution.Revolve(new Region(Circle(20, 0, 3)), RigidTransform3D.Identity, angle: Math.PI);
        problem.Should().BeNull();
        MassProperties.Volume(r!.Body).Should().BeApproximately(Math.PI * Math.PI * 20 * 9, 1e-9);
        r.Body.Faces.Should().HaveCount(3);
    }
}
