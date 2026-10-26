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

    [Fact]
    public void A_line_at_an_angle_is_refused_as_a_cone()
    {
        var (r, problem) = Revolution.Revolve(new Region(new ProfileLoop([new LineSegment(P(5, 0), P(10, 0)), new LineSegment(P(10, 0), P(5, 4)), new LineSegment(P(5, 4), P(5, 0))])),
            RigidTransform3D.Identity);
        r.Should().BeNull();
        problem.Should().Contain("cone");
    }
}
