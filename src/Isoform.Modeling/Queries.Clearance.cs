using BasAutomation.Geometry;
using Isoform.Geometry;

namespace Isoform.Modeling;

/// <summary>
/// Clearance between two solids, as a certified interval [<see cref="Lower"/>, <see cref="Upper"/>]. Apart or touching:
/// the distance between them, with <see cref="OnA"/> and <see cref="OnB"/> boundary points <see cref="Upper"/> apart.
/// Interfering: both ends are negative, twice the lowest value of the intersection field max(fA, fB), and
/// OnA = OnB is the deepest point found; −Upper is then a lower bound on the penetration depth (a ball of that
/// diameter lies inside both solids).
/// </summary>
public readonly record struct ClearanceResult(double Lower, double Upper, Point3D OnA, Point3D OnB, bool Interferes);

public static partial class Queries
{
    /// <summary>
    /// Clearance between two solids within their boxes, to <paramref name="tolerance"/> where the search can tell
    /// within <paramref name="budget"/> boxes (the interval is honest either way).
    /// <list type="number">
    /// <item>Interference: branch-and-bound for the lowest value of g = max(fA, fB) over the hull of both boxes. A point
    /// with g &lt; 0 lies inside both. Since |f| never exceeds the distance, 2·min g never exceeds the distance between
    /// the solids, so this also gives the first lower bound.</item>
    /// <item>Distance: branch-and-bound over pairs of boxes holding boundary of A and of B. A pair is no closer than the
    /// boxes, nor than the field of one simplified for its box evaluated over the other box (1-Lipschitz again), nor than
    /// the gap along the direction of the best pair found, bounded by duality over the faces that hold each solid.
    /// The upper end comes from alternating Newton projections between the solids.</item>
    /// </list>
    /// Both use the faces each solid lies inside: two whose fields add up to a constant k everywhere (facing planes, a pin
    /// in a coaxial bore) keep the solids k apart and give g ≥ k/2, as do two convex ones (ball, rod, half-space) k apart.
    /// That makes face-to-face gaps and contacts exact at once.
    /// </summary>
    public static ClearanceResult Clearance(Field a, Box3D boxA, Field b, Box3D boxB, double tolerance = 1e-6, int budget = 100_000)
    {
        // 1. Interference.
        var hull = new Box3D(
            new Point3D(Math.Min(boxA.Min.X, boxB.Min.X), Math.Min(boxA.Min.Y, boxB.Min.Y), Math.Min(boxA.Min.Z, boxB.Min.Z)),
            new Point3D(Math.Max(boxA.Max.X, boxB.Max.X), Math.Max(boxA.Max.Y, boxB.Max.Y), Math.Max(boxA.Max.Z, boxB.Max.Z)));
        var boxes = new PriorityQueue<(Box3D Box, Field A, Field B), double>();
        var (ha, hb) = (a.Simplify(hull), b.Simplify(hull));
        boxes.Enqueue((hull, ha, hb), IntersectionLow(hull, ha, hb));
        var deepest = double.PositiveInfinity;
        var at = hull.Center;
        var pruned = double.PositiveInfinity; // lowest bound of a box set aside
        double floor; // no point has max(fA, fB) below this
        while (true)
        {
            // Overlap shallower than half the tolerance counts as touching.
            var limit = Math.Min(deepest, 0) - tolerance / 2;
            if (!boxes.TryDequeue(out var item, out var low))
            {
                floor = pruned;
                break;
            }
            if (low >= limit || budget-- <= 0 || HalfDiagonal(item.Box) < tolerance / 4)
            {
                floor = Math.Min(low, pruned);
                break;
            }
            var x = Equalize(a, b, item.Box.Center);
            var g = Math.Max(a.Value(x), b.Value(x));
            if (g < deepest) (deepest, at) = (g, x);
            foreach (var child in Split(item.Box))
            {
                var (ca, cb) = (item.A.Simplify(child), item.B.Simplify(child));
                var lc = IntersectionLow(child, ca, cb);
                if (lc < limit) boxes.Enqueue((child, ca, cb), lc);
                else pruned = Math.Min(pruned, lc);
            }
        }
        if (deepest < -tolerance / 2) return new(2 * Math.Min(floor, deepest), 2 * deepest, at, at, true);
        var uncertain = budget <= 0; // phase 1 ran out: only 2·floor is known to bound the clearance from below

        // 2. Distance between the boundaries.
        var upper = double.PositiveInfinity;
        Point3D onA = at, onB = at;
        void Polish(Point3D from)
        {
            // Alternate projections between the solids: each pair of boundary points bounds the distance from above.
            if (Boundary(a, from) is not { } pa) return;
            for (var round = 0; round < 3; round++)
            {
                if (Boundary(b, pa) is not { } pb) return;
                if ((pa - pb).Length < upper) (upper, onA, onB) = ((pa - pb).Length, pa, pb);
                if (Boundary(a, pb) is not { } next) return;
                pa = next;
            }
        }
        Polish(at);
        var pairs = new PriorityQueue<(Box3D BoxA, Field A, Box3D BoxB, Field B), double>();
        var (sa, sb) = (a.Simplify(boxA), b.Simplify(boxB));
        pairs.Enqueue((boxA, sa, boxB, sb), Math.Max(2 * floor, PairLow(boxA, sa, boxB, sb, null)));
        var lower = double.PositiveInfinity; // stays so if every pair was ruled out
        while (pairs.TryDequeue(out var pair, out var low))
        {
            if (low >= upper - tolerance || budget-- <= 0 || Math.Max(HalfDiagonal(pair.BoxA), HalfDiagonal(pair.BoxB)) < tolerance / 4)
            {
                lower = low;
                break;
            }
            Polish(pair.BoxA.Center);
            Polish(pair.BoxB.Center);
            // Split the larger box; keep children that can hold boundary.
            Vector3D? across = upper > 0 && !double.IsPositiveInfinity(upper) ? (onB - onA) / upper : null;
            var splitA = HalfDiagonal(pair.BoxA) >= HalfDiagonal(pair.BoxB);
            foreach (var child in Split(splitA ? pair.BoxA : pair.BoxB))
            {
                var parent = splitA ? pair.A : pair.B;
                if (!MayHoldBoundary(parent.Bounds(child))) continue;
                var h = parent.Simplify(child);
                var next = splitA ? (child, h, pair.BoxB, pair.B) : (pair.BoxA, pair.A, child, h);
                var lc = Math.Max(low, PairLow(next.Item1, next.Item2, next.Item3, next.Item4, across));
                if (lc < upper) pairs.Enqueue(next, lc);
            }
        }
        lower = Math.Max(0, Math.Min(lower, upper));
        if (uncertain) lower = Math.Min(lower, 2 * floor);
        return new(lower, upper, onA, onB, false);
    }

    /// <summary>Clearance between two compiled parts.</summary>
    public static ClearanceResult Clearance(CompiledPart a, CompiledPart b, double tolerance = 1e-6) =>
        Clearance(a.Field!, Box(a), b.Field!, Box(b), tolerance);

    /// <summary>A lower bound of max(fA, fB) over the box.</summary>
    static double IntersectionLow(Box3D box, Field a, Field b) =>
        Math.Max(Interval.Max(a.Bounds(box), b.Bounds(box)).Lo, Separation(Conjuncts(a, box), Conjuncts(b, box)) / 2);

    /// <summary>
    /// A lower bound of the distance between the boundary of A in one box and that of B in the other: the boxes' own
    /// distance, either field over the other box, the separation of their conjuncts, and the gap along <paramref name="u"/>.
    /// </summary>
    static double PairLow(Box3D boxA, Field a, Box3D boxB, Field b, Vector3D? u)
    {
        var (ca, cb) = (Conjuncts(a, boxA), Conjuncts(b, boxB));
        var low = Math.Max(Math.Max(Distance(boxA, boxB), Math.Max(a.Bounds(boxB).Lo, b.Bounds(boxA).Lo)), Separation(ca, cb));
        return u is { } d ? Math.Max(low, -Highest(boxB, cb, -d) - Highest(boxA, ca, d)) : low;
    }

    /// <summary>
    /// An upper bound of u·x over the part of the solid in the box, for a unit u. By weak duality, for any λ ≥ 0 on
    /// half-spaces nᵢ·x ≤ cᵢ that hold the solid, u·x ≤ Σ λᵢ cᵢ + (u − Σ λᵢ nᵢ)·x, and the last term is at most its
    /// maximum over the box; λ is fitted to the three planes most aligned with u. A ball gives u·c + r; a rod gives its
    /// axis' share of u over the box plus the rest over the rod. Taken along the best pair found (B's bound with −u),
    /// the gap u·(b − a) closes on the distance as the boxes close on that pair, edges and corners included.
    /// </summary>
    static double Highest(Box3D box, List<LeafField> conjuncts, Vector3D u)
    {
        var best = Support(box, u);
        var planes = new List<(Vector3D N, double C)>();
        foreach (var l in conjuncts)
        {
            var s = l.Surface;
            switch (s.Kind)
            {
                case SurfaceKind.Plane:
                    var n = l.Complement ? -s.Axis : s.Axis;
                    planes.Add((n, n.Dot(s.Origin.AsVector())));
                    break;
                case SurfaceKind.Sphere when !l.Complement:
                    best = Math.Min(best, u.Dot(s.Origin.AsVector()) + s.Radius);
                    break;
                case SurfaceKind.Cylinder when !l.Complement:
                    var along = s.Axis * u.Dot(s.Axis);
                    var across = u - along;
                    best = Math.Min(best, across.Dot(s.Origin.AsVector()) + across.Length * s.Radius + Support(box, along));
                    break;
            }
        }
        var aligned = planes.OrderByDescending(h => h.N.Dot(u)).Take(3).ToArray();
        for (var set = 1; set < 1 << aligned.Length; set++)
        {
            var idx = Enumerable.Range(0, aligned.Length).Where(i => (set >> i & 1) == 1).ToArray();
            var g = new double[idx.Length, idx.Length];
            var r = new double[idx.Length];
            for (var i = 0; i < idx.Length; i++)
            {
                r[i] = aligned[idx[i]].N.Dot(u);
                for (var j = 0; j < idx.Length; j++) g[i, j] = aligned[idx[i]].N.Dot(aligned[idx[j]].N);
            }
            if (Solve(g, r) is not { } lambda || lambda.Any(l => l < 0)) continue;
            var (rest, bound) = (u, 0.0);
            for (var i = 0; i < idx.Length; i++)
            {
                rest -= aligned[idx[i]].N * lambda[i];
                bound += aligned[idx[i]].C * lambda[i];
            }
            best = Math.Min(best, bound + Support(box, rest));
        }
        return best;
    }

    static double Support(Box3D b, Vector3D w) =>
        (w.X > 0 ? w.X * b.Max.X : w.X * b.Min.X) + (w.Y > 0 ? w.Y * b.Max.Y : w.Y * b.Min.Y) + (w.Z > 0 ? w.Z * b.Max.Z : w.Z * b.Min.Z);

    /// <summary>
    /// Moves x to where fA = fB along the difference of their gradients: on facing faces that is the mid-plane, where
    /// max(fA, fB) is lowest. Only a better sample point; nothing relies on it.
    /// </summary>
    static Point3D Equalize(Field a, Field b, Point3D x)
    {
        for (var it = 0; it < 8; it++)
        {
            var (sa, sb) = (a.Sample(x), b.Sample(x));
            var dg = sa.Gradient - sb.Gradient;
            if (dg.LengthSquared < 1e-12) break;
            x -= dg * ((sa.Value - sb.Value) / dg.LengthSquared);
        }
        return x;
    }

    /// <summary>
    /// A lower bound k of the distance between two solids from convex shapes their conjuncts hold them in: facing planes,
    /// a pin in a coaxial bore or a ball in a concentric socket (the two fields add up to k everywhere), or two of ball,
    /// rod (solid cylinder) and half-space apart. Both kinds are exact distances outside their shapes, so they also give
    /// max(fA, fB) ≥ k/2 everywhere.
    /// </summary>
    static double Separation(List<LeafField> a, List<LeafField> b)
    {
        var low = double.NegativeInfinity;
        foreach (var x in a)
        foreach (var y in b)
            if ((ConstantSum(x, y) ?? Apart(x, y)) is { } k) low = Math.Max(low, k);
        return low;
    }

    /// <summary>Facing planes, or a cylinder or sphere and a complemented one on the same axis or centre.</summary>
    static double? ConstantSum(LeafField x, LeafField y)
    {
        var (s, t) = (x.Surface, y.Surface);
        if (s.Kind != t.Kind) return null;
        var offset = t.Origin - s.Origin;
        var constant = s.Kind switch
        {
            SurfaceKind.Plane => ((x.Complement ? -s.Axis : s.Axis) + (y.Complement ? -t.Axis : t.Axis)).Length < 1e-12,
            SurfaceKind.Cylinder => x.Complement != y.Complement && s.Axis.Cross(t.Axis).Length < 1e-12
                && (offset - s.Axis * offset.Dot(s.Axis)).Length < 1e-12,
            SurfaceKind.Sphere => x.Complement != y.Complement && offset.Length < 1e-12,
            _ => false,
        };
        return constant ? x.Value(s.Origin) + y.Value(s.Origin) : null;
    }

    /// <summary>The gap between two convex shapes, each a ball, a rod or a half-space; null if they may meet.</summary>
    static double? Apart(LeafField x, LeafField y)
    {
        if (x.Surface.Kind == SurfaceKind.Plane) (x, y) = (y, x); // a plane, if any, second
        var (s, t) = (x.Surface, y.Surface);
        if (x.Complement || y.Complement && t.Kind != SurfaceKind.Plane) return null;
        double? gap = (s.Kind, t.Kind) switch
        {
            (SurfaceKind.Sphere, SurfaceKind.Sphere) => (s.Origin - t.Origin).Length - s.Radius - t.Radius,
            (SurfaceKind.Sphere, SurfaceKind.Cylinder) => FromAxis(s.Origin, t) - s.Radius - t.Radius,
            (SurfaceKind.Cylinder, SurfaceKind.Sphere) => FromAxis(t.Origin, s) - s.Radius - t.Radius,
            (SurfaceKind.Cylinder, SurfaceKind.Cylinder) => BetweenAxes(s, t) - s.Radius - t.Radius,
            (SurfaceKind.Sphere, SurfaceKind.Plane) => y.Value(s.Origin) - s.Radius,
            (SurfaceKind.Cylinder, SurfaceKind.Plane) when Math.Abs(s.Axis.Dot(t.Axis)) < 1e-12 => y.Value(s.Origin) - s.Radius,
            _ => null,
        };
        return gap > 0 ? gap : null;
    }

    static double FromAxis(Point3D p, Surface cylinder)
    {
        var v = p - cylinder.Origin;
        return (v - cylinder.Axis * v.Dot(cylinder.Axis)).Length;
    }

    static double BetweenAxes(Surface s, Surface t)
    {
        var n = s.Axis.Cross(t.Axis);
        return n.Length < 1e-9 ? FromAxis(t.Origin, s) : Math.Abs((t.Origin - s.Origin).Dot(n)) / n.Length;
    }
}
