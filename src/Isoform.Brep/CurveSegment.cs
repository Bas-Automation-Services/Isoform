using BasAutomation.Geometry;

namespace Isoform.Brep;

/// <summary>
/// A piece of a p-curve that is neither a line nor a circle (an ellipse on a plane, a wave on a cylinder) in flat
/// (u, v): u scaled by <see cref="Scale"/>, from parameter <see cref="From"/> to <see cref="To"/>. Measured in short
/// sub-pieces, each a graph over its chord, so distances, crossings and the angle it subtends are exact to rounding.
/// </summary>
internal sealed record CurveSegment(Curve2 Curve, double From, double To, double Scale) : Segment
{
    const int Pieces = 32;

    public Point2D At(double t)
    {
        var p = Curve.At(t);
        return new Point2D(p.X * Scale, p.Y);
    }

    /// <summary>d/dt of <see cref="At"/>.</summary>
    public Vector2D Velocity(double t)
    {
        var d = Curve.Derivative(t);
        return new Vector2D(d.X * Scale, d.Y);
    }

    public override Point2D Start => At(From);
    public override Point2D End => At(To);
    public override Segment Reversed() => this with { From = To, To = From };

    double T(int i) => From + (To - From) * i / Pieces;

    public Point2D Mid => At((From + To) / 2);

    /// <summary>The direction of travel at t.</summary>
    public Vector2D Heading(double t) => Velocity(t) * Math.Sign(To - From);

    /// <summary>The curvature to the left of the direction of travel at t.</summary>
    public double Curvature(double t)
    {
        var h = 1e-5 * Math.Max(1, Math.Abs(To - From));
        var d1 = Velocity(t);
        var d2 = (Velocity(t + h) - Velocity(t - h)) / (2 * h);
        return d1.Cross(d2) / Math.Pow(d1.Length, 3) * Math.Sign(To - From);
    }

    /// <summary>½ ∫ (x dy − y dx): this piece's term in a loop's signed area.</summary>
    public double AreaTerm() => Quadrature.Integrate(t =>
    {
        var (p, d) = (At(t), Velocity(t));
        return (p.X * d.Y - p.Y * d.X) / 2;
    }, From, To, Quadrature.AngleStep);

    /// <summary>The distance from a point: the nearest point of each sub-piece by golden-section search.</summary>
    public double DistanceTo(Point2D p)
    {
        var best = Math.Min((Start - p).Length, (End - p).Length);
        for (var i = 0; i < Pieces; i++)
        {
            var (a, b) = (T(i), T(i + 1));
            double F(double t) => (At(t) - p).LengthSquared;
            const double g = 0.6180339887498949;
            var (x1, x2) = (b - g * (b - a), a + g * (b - a));
            var (f1, f2) = (F(x1), F(x2));
            for (var k = 0; k < 80; k++)
            {
                if (f1 < f2)
                {
                    (b, x2, f2) = (x2, x1, f1);
                    x1 = b - g * (b - a);
                    f1 = F(x1);
                }
                else
                {
                    (a, x1, f1) = (x1, x2, f2);
                    x2 = a + g * (b - a);
                    f2 = F(x2);
                }
            }
            best = Math.Min(best, Math.Sqrt(Math.Min(f1, f2)));
        }
        return best;
    }

    /// <summary>
    /// The angle the piece turns through seen from <paramref name="p"/> (away from it): each sub-piece's chord, and a
    /// whole turn where p lies between a sub-piece and its chord.
    /// </summary>
    public double Subtended(Point2D p)
    {
        var angle = 0.0;
        for (var i = 0; i < Pieces; i++)
        {
            var (ta, tb) = (T(i), T(i + 1));
            var (a, b) = (At(ta), At(tb));
            var (u, w) = (a - p, b - p);
            angle += Math.Atan2(u.Cross(w), u.Dot(w));
            angle += 2 * Math.PI * Lens(ta, tb, a, b, p);
        }
        return angle;
    }

    // The winding of (sub-piece a → b, then its chord back) round p: 0 outside the lens between them, −1 inside it
    // when the piece bulges to the left of its chord, +1 when to the right.
    int Lens(double ta, double tb, Point2D a, Point2D b, Point2D p)
    {
        var chord = b - a;
        var length = chord.Length;
        if (length == 0) return 0;
        var dir = chord / length;
        var s = (p - a).Dot(dir);
        if (s <= 0 || s >= length) return 0;
        var hp = dir.Cross(p - a);
        // The curve's height above the chord where it is s along it (the sub-piece is a graph over its chord).
        var (lo, hi) = (ta, tb);
        for (var k = 0; k < 80; k++)
        {
            var mid = (lo + hi) / 2;
            if ((At(mid) - a).Dot(dir) < s) lo = mid; else hi = mid;
        }
        var hc = dir.Cross(At((lo + hi) / 2) - a);
        if (hp * hc <= 0 || Math.Abs(hp) >= Math.Abs(hc)) return 0;
        return hc > 0 ? -1 : 1;
    }

    /// <summary>The x where the piece crosses the line y = <paramref name="y"/> (sub-pieces with an end either side).</summary>
    public IEnumerable<double> Crossings(double y)
    {
        for (var i = 0; i < Pieces; i++)
        {
            var (lo, hi) = (T(i), T(i + 1));
            var (ya, yb) = (At(lo).Y - y, At(hi).Y - y);
            if (ya * yb >= 0) continue;
            for (var k = 0; k < 80; k++)
            {
                var mid = (lo + hi) / 2;
                if ((At(mid).Y - y) * ya > 0) lo = mid; else hi = mid;
            }
            yield return At((lo + hi) / 2).X;
        }
    }

    /// <summary>Points along the piece, ends included.</summary>
    public IEnumerable<Point2D> Samples() => Enumerable.Range(0, Pieces + 1).Select(i => At(T(i)));
}
