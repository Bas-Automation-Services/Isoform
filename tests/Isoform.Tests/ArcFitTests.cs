using BasAutomation.Geometry;
using Isoform.Geometry;

namespace Isoform.Tests;

/// <summary>S3: tangent arcs through smooth curves, with end tangents kept and the error measured both ways.</summary>
public class ArcFitTests
{
    static Point2D P(double x, double y) => new(x, y);

    static (List<Point2D> Points, List<Vector2D> Tangents) Sample(Func<double, Point2D> at, Func<double, Vector2D> tangent, double t0, double t1, int n)
    {
        var ts = Enumerable.Range(0, n + 1).Select(i => t0 + (t1 - t0) * i / n).ToList();
        return (ts.Select(at).ToList(), ts.Select(t => tangent(t).Normalized()).ToList());
    }

    // The largest distance from the chain to the true curve, by dense sampling of both.
    static double Deviation(IReadOnlyList<Segment2> arcs, Func<double, Point2D> at, double t0, double t1)
    {
        var curve = Enumerable.Range(0, 20001).Select(i => at(t0 + (t1 - t0) * i / 20000)).ToList();
        var e = curve.Max(p => arcs.Min(a => a.Distance(p)));
        foreach (var a in arcs)
            for (var j = 0; j <= 64; j++)
            {
                var q = a.At(j / 64.0);
                e = Math.Max(e, curve.Min(p => (p - q).Length));
            }
        return e;
    }

    static void Chained(ArcFitResult fit, Point2D start, Vector2D t0, Point2D end, Vector2D t1)
    {
        var s = fit.Segments;
        (s[0].Start - start).Length.Should().BeLessThan(1e-12);
        (s[^1].End - end).Length.Should().BeLessThan(1e-12);
        (s[0].Tangent(0) - t0).Length.Should().BeLessThan(1e-9);
        (s[^1].Tangent(1) - t1).Length.Should().BeLessThan(1e-9);
        for (var i = 1; i < s.Count; i++)
        {
            (s[i].Start - s[i - 1].End).Length.Should().BeLessThan(1e-12);
            (s[i].Tangent(0) - s[i - 1].Tangent(1)).Length.Should().BeLessThan(1e-9);
        }
    }

    [Fact]
    public void A_circular_arc_comes_back_as_one_arc()
    {
        Point2D At(double t) => P(3 + 20 * Math.Cos(t), -1 + 20 * Math.Sin(t));
        Vector2D Tangent(double t) => new(-Math.Sin(t), Math.Cos(t));
        var (points, tangents) = Sample(At, Tangent, 0.2, 2.1, 200);

        var fit = ArcFit.Fit(points, tangents, 0.05);

        fit.Segments.Should().ContainSingle();
        var arc = fit.Segments[0];
        (arc.Center!.Value - P(3, -1)).Length.Should().BeLessThan(1e-9);
        arc.Radius.Should().BeApproximately(20, 1e-9);
        arc.Ccw.Should().BeTrue();
        fit.Error.Should().BeLessThan(1e-9);
    }

    [Fact]
    public void A_parabola_is_fitted_within_the_tolerance_with_its_end_tangents()
    {
        // The section of a quadratic blend between two flat faces is a parabola.
        Point2D At(double t) => P(t, t * t / 20);
        Vector2D Tangent(double t) => new(1, t / 10);
        var (points, tangents) = Sample(At, Tangent, -10, 10, 400);

        foreach (var tolerance in new[] { 0.05, 0.01 })
        {
            var fit = ArcFit.Fit(points, tangents, tolerance);

            Chained(fit, At(-10), Tangent(-10).Normalized(), At(10), Tangent(10).Normalized());
            fit.Error.Should().BeLessThanOrEqualTo(tolerance);
            Deviation(fit.Segments, At, -10, 10).Should().BeLessThanOrEqualTo(tolerance * 1.01);
            fit.Segments.Count.Should().BeLessThanOrEqualTo(tolerance == 0.05 ? 4 : 8);
        }
    }

    [Fact]
    public void A_closed_ellipse_is_split_and_fitted_round()
    {
        Point2D At(double t) => P(40 * Math.Cos(t), 25 * Math.Sin(t));
        Vector2D Tangent(double t) => new(-40 * Math.Sin(t), 25 * Math.Cos(t));
        var (points, tangents) = Sample(At, Tangent, 0, 2 * Math.PI, 2000);
        points[^1] = points[0];

        var fit = ArcFit.Fit(points, tangents, 0.05);

        Chained(fit, At(0), Tangent(0).Normalized(), At(0), Tangent(0).Normalized());
        fit.Error.Should().BeLessThanOrEqualTo(0.05);
        Deviation(fit.Segments, At, 0, 2 * Math.PI).Should().BeLessThanOrEqualTo(0.0505);
        fit.Segments.Should().HaveCountLessThanOrEqualTo(32);
    }

    [Fact]
    public void An_s_curve_is_fitted_through_its_inflection()
    {
        Point2D At(double t) => P(t, 5 * Math.Sin(t / 5));
        Vector2D Tangent(double t) => new(1, Math.Cos(t / 5));
        var (points, tangents) = Sample(At, Tangent, -15, 15, 600);

        var fit = ArcFit.Fit(points, tangents, 0.02);

        Chained(fit, At(-15), Tangent(-15).Normalized(), At(15), Tangent(15).Normalized());
        Deviation(fit.Segments, At, -15, 15).Should().BeLessThanOrEqualTo(0.0202);
        fit.Segments.Should().Contain(s => s.Ccw).And.Contain(s => s.IsArc && !s.Ccw);
    }

    [Fact]
    public void Segments_measure_distance_sweep_and_tangent()
    {
        var quarter = new Segment2(P(10, 0), P(0, 10), P(0, 0), Ccw: true);
        quarter.Sweep.Should().BeApproximately(Math.PI / 2, 1e-15);
        quarter.Length.Should().BeApproximately(5 * Math.PI, 1e-12);
        quarter.Distance(P(20, 20)).Should().BeApproximately(Math.Sqrt(800) - 10, 1e-12);
        quarter.Distance(P(-5, -5)).Should().BeApproximately(Math.Sqrt(250), 1e-12); // beyond its ends
        quarter.Distance(P(10, -3)).Should().BeApproximately(3, 1e-12);
        (quarter.Tangent(0) - new Vector2D(0, 1)).Length.Should().BeLessThan(1e-15);

        var backwards = quarter with { Start = P(0, 10), End = P(10, 0), Ccw = false };
        backwards.Sweep.Should().BeApproximately(-Math.PI / 2, 1e-15);
        (backwards.At(0.5) - P(Math.Sqrt(50), Math.Sqrt(50))).Length.Should().BeLessThan(1e-12);

        var circle = new Segment2(P(5, 0), P(5, 0), P(0, 0), Ccw: false);
        circle.Sweep.Should().BeApproximately(-2 * Math.PI, 1e-15);
        circle.Distance(P(0, 2)).Should().BeApproximately(3, 1e-12);
    }
}
