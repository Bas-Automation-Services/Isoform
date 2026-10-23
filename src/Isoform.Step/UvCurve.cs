namespace Isoform.Step;

/// <summary>
/// An edge's image in a face's (u, v) parameters: a clamped 2D B-spline parametrised like the edge's 3D curve, its
/// knots running from the edge's T0 to T1. Degree 1 with two poles is exact (a circle round a cylinder, a generator,
/// a meridian); higher degrees approximate the image piece by piece.
/// </summary>
public sealed record UvCurve(int Degree, IReadOnlyList<(double U, double V)> Poles, IReadOnlyList<double> Knots, IReadOnlyList<int> Multiplicities)
{
    public (double U, double V) Start => Poles[0];
    public (double U, double V) End => Poles[^1];

    public UvCurve Shifted(double du, double dv) => this with { Poles = Poles.Select(p => (p.U + du, p.V + dv)).ToList() };
}
