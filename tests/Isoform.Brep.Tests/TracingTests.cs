using BasAutomation.Geometry;
using Xunit.Abstractions;

namespace Isoform.Brep.Tests;

/// <summary>
/// Curves without a closed form, traced (experiment 24). The parts that need them are among <see cref="Documents.All"/>,
/// which the backend and OCCT tests run.
/// </summary>
public class TracingTests(ITestOutputHelper output)
{
    // A shaft Ø20 along z and a bore Ø8 along x through its axis at z = 0.
    static readonly Cylinder Shaft = new(RigidTransform3D.FromFrame(new Point3D(0, 0, 0), new Vector3D(0, 0, 1), new Vector3D(1, 0, 0)), 10);
    static readonly Cylinder Bore = new(RigidTransform3D.FromFrame(new Point3D(0, 0, 0), new Vector3D(1, 0, 0), new Vector3D(0, 1, 0)), 4);

    static SurfaceMeeting Traced(Surface a, Surface b, double reach = 20)
    {
        var box = (new Point3D(-reach, -reach, -reach), new Point3D(reach, reach, reach));
        return Tracing.Trace(a, b, box, box, []);
    }

    [Fact]
    public void A_bore_across_a_shaft_meets_it_in_two_closed_loops()
    {
        var meeting = Traced(Shaft, Bore);
        meeting.Kind.Should().Be(Meeting.Curves);
        meeting.Curves.Should().HaveCount(2).And.AllSatisfy(c => ((Trace3)c).Closed.Should().BeTrue());
        // One each side of the shaft, round the bore's axis: x = ±√(100 − y²).
        meeting.Curves.Select(c => Math.Sign(c.At(0).X)).Should().BeEquivalentTo([1, -1]);
    }

    [Fact]
    public void Every_point_of_the_spline_lies_on_both_surfaces()
    {
        foreach (var (a, b) in new (Surface, Surface)[]
                 {
                     (Shaft, Bore),
                     // A torus (R 10, r 3) and the plane x = 8 along its axis; a cone and a cylinder beside its axis.
                     (new Torus(RigidTransform3D.Identity, 10, 3), new Plane(RigidTransform3D.FromFrame(new Point3D(8, 0, 0), new Vector3D(1, 0, 0), new Vector3D(0, 1, 0)))),
                     (new Cone(RigidTransform3D.Identity, 10, -0.5), new Cylinder(RigidTransform3D.FromFrame(new Point3D(5, 0, 0), new Vector3D(0, 0, 1), new Vector3D(1, 0, 0)), 2)),
                 })
        foreach (var c in Traced(a, b).Curves.Cast<Trace3>())
        {
            var worst = Enumerable.Range(0, 10_000).Select(i => c.Gap(c.At(c.End * i / 10_000))).Max();
            output.WriteLine($"{a.GetType().Name} x {b.GetType().Name}: {c.Points.Length} points, worst {worst:E2}");
            worst.Should().BeLessThan(2 * Tracing.Accuracy);
            // Its points themselves on both, to rounding.
            c.Points.Max(c.Gap).Should().BeLessThan(1e-12);
        }
    }

    [Fact]
    public void A_point_on_the_curve_is_found_on_it()
    {
        var c = (Trace3)Traced(Shaft, Bore).Curves[0];
        foreach (var t in new[] { 0.1, 1.7, 3.3, 6.2 })
        {
            var exact = c.Exact(t);
            var (found, distance) = c.Nearest(exact);
            distance.Should().BeLessThan(2 * Tracing.Accuracy);
            (c.At(found) - c.At(t)).Length.Should().BeLessThan(1e-6);
        }
    }

    [Fact]
    public void Its_p_curve_follows_it_on_each_surface()
    {
        var c = (Trace3)Traced(Shaft, Bore).Curves[0];
        foreach (var s in new Surface[] { Shaft, Bore })
        {
            var pc = Uv.Pcurve(s, c);
            for (var i = 0; i < 200; i++)
            {
                var t = 4 * Math.PI * i / 200 - Math.PI;
                var uv = pc.At(t);
                (s.At(uv.X, uv.Y) - c.At(t)).Length.Should().BeLessThan(2 * Tracing.Accuracy);
                // The derivative is the p-curve's own (central differences).
                var d = (pc.At(t + 1e-6) - pc.At(t - 1e-6)) / 2e-6;
                (pc.Derivative(t) - d).Length.Should().BeLessThan(1e-5);
            }
        }
    }
}
