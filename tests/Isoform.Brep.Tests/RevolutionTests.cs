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
