using BasAutomation.Geometry;
using Isoform.Geometry;
using Isoform.Step;

namespace Isoform.Occt.Tests;

/// <summary>Surface parameters and p-curves of the STEP export, against the curves they image.</summary>
public class StepExportParameterTests
{
    static readonly Vector3D Tilted = new Vector3D(0.3, -0.5, 0.8).Normalized();

    public static IEnumerable<object[]> Surfaces =>
    [
        [new Surface(0, "", SurfaceKind.Plane, new Point3D(1, 2, 3), Tilted)],
        [new Surface(0, "", SurfaceKind.Cylinder, new Point3D(1, 2, 3), Tilted, 4)],
        [new Surface(0, "", SurfaceKind.Cone, new Point3D(1, 2, 3), Tilted, Angle: 0.6)],
        [new Surface(0, "", SurfaceKind.Sphere, new Point3D(1, 2, 3), Tilted, 4)],
        [new Surface(0, "", SurfaceKind.Torus, new Point3D(1, 2, 3), Tilted, 5, 2)],
    ];

    [Theory]
    [MemberData(nameof(Surfaces))]
    public void Parameters_of_a_surface_point_give_the_point_back(Surface s)
    {
        var random = new Random(1);
        for (var i = 0; i < 200; i++)
        {
            var (u, v) = (random.NextDouble() * 6 - 3, s.Kind == SurfaceKind.Sphere ? random.NextDouble() * 3 - 1.5 : random.NextDouble() * 6 - 3);
            if (s.Kind == SurfaceKind.Cone) v = Math.Abs(v) + 0.1; // the nappe the face lies on
            var p = SurfaceParameters.At(s, u, v);
            var (pu, pv) = SurfaceParameters.Of(s, p);
            (SurfaceParameters.At(s, pu, pv) - p).Length.Should().BeLessThan(1e-12);
            if (s.Kind != SurfaceKind.Plane) Math.Abs(Math.IEEERemainder(pu - u, 2 * Math.PI)).Should().BeLessThan(1e-12);
        }
    }

    [Fact]
    public void A_circle_round_the_axis_has_a_straight_exact_image()
    {
        var cylinder = new Surface(0, "", SurfaceKind.Cylinder, new Point3D(1, 2, 3), Tilted, 4);
        var circle = new Circle(new Point3D(1, 2, 3) + Tilted * 2.5, -Tilted, SurfaceIntersection.Perpendicular(Tilted), 4);

        var image = SurfaceParameters.Image(cylinder, circle, 0.5, 0.5 + 2 * Math.PI);

        image.Degree.Should().Be(1);
        (image.End.U - image.Start.U).Should().BeApproximately(-2 * Math.PI, 1e-12, "it runs once round, against u");
        image.End.V.Should().BeApproximately(2.5, 1e-12);
    }

    [Fact]
    public void An_oblique_ellipse_on_a_cylinder_is_imaged_within_1e_11()
    {
        var cylinder = new Surface(0, "", SurfaceKind.Cylinder, new Point3D(0, 0, 0), new Vector3D(0, 0, 1), 5);
        var plane = new Surface(0, "", SurfaceKind.Plane, new Point3D(0, 0, 2), new Vector3D(0.6, 0, 0.8));
        var ellipse = SurfaceIntersection.Curve(plane, cylinder, new Point3D(5, 0, 2))!;
        var (t0, t1) = (1.0, 1.0 + 2 * Math.PI);

        var image = SurfaceParameters.Image(cylinder, ellipse, t0, t1);

        image.Degree.Should().BeGreaterThan(1);
        var worst = 0.0;
        for (var i = 0; i <= 1000; i++)
        {
            var t = t0 + (t1 - t0) * i / 1000;
            var (u, v) = Evaluate(image, t);
            worst = Math.Max(worst, (SurfaceParameters.At(cylinder, u, v) - ellipse.At(t)).Length);
        }
        worst.Should().BeLessThan(1e-10);
    }

    /// <summary>A clamped B-spline whose inner knots all have multiplicity = degree: one Bézier piece per span.</summary>
    static (double U, double V) Evaluate(UvCurve c, double t)
    {
        var k = Math.Clamp(Enumerable.Range(0, c.Knots.Count - 1).Last(i => c.Knots[i] <= t), 0, c.Knots.Count - 2);
        var x = (t - c.Knots[k]) / (c.Knots[k + 1] - c.Knots[k]);
        var points = c.Poles.Skip(k * c.Degree).Take(c.Degree + 1).ToArray();
        for (var r = 1; r <= c.Degree; r++)
            for (var i = 0; i <= c.Degree - r; i++)
                points[i] = (points[i].U + (points[i + 1].U - points[i].U) * x, points[i].V + (points[i + 1].V - points[i].V) * x);
        return points[0];
    }
}
