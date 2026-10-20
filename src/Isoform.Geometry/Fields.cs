using BasAutomation.Geometry;

namespace Isoform.Geometry;

/// <summary>
/// Field value at a point, the surface that "owns" it (the one deciding the min/max), and the exact gradient
/// of that owner. On the zero set, <see cref="Owner"/> says which source surface the point lies on.
/// </summary>
public readonly record struct FieldSample(double Value, int Owner, Vector3D Gradient);

/// <summary>A guaranteed enclosure of the field over a region: Lo ≤ f(p) ≤ Hi for every p in it.</summary>
public readonly record struct Interval(double Lo, double Hi)
{
    public static Interval Max(Interval a, Interval b) => new(Math.Max(a.Lo, b.Lo), Math.Max(a.Hi, b.Hi));
    public static Interval Min(Interval a, Interval b) => new(Math.Min(a.Lo, b.Lo), Math.Min(a.Hi, b.Hi));
    public Interval Negate() => new(-Hi, -Lo);
    public Interval Intersect(Interval other) => new(Math.Max(Lo, other.Lo), Math.Min(Hi, other.Hi));
}

/// <summary>
/// An implicit solid: negative inside, positive outside, zero on the boundary. Every field is 1-Lipschitz
/// (|f(p) − f(q)| ≤ |p − q|), so |f(p)| never exceeds the distance from p to the boundary.
/// </summary>
public abstract class Field
{
    public abstract FieldSample Sample(Point3D p);

    /// <summary>The field value alone, bit-identical to <c>Sample(p).Value</c> but without owner and gradient.</summary>
    public virtual double Value(Point3D p) => Sample(p).Value;

    /// <summary>
    /// An equivalent field for points inside <paramref name="box"/>: children that can never decide a min/max
    /// there are dropped. Value, owner and gradient are unchanged at every point of the box.
    /// </summary>
    public virtual Field Simplify(Box3D box) => this;

    /// <summary>The field of one owner surface alone, ignoring the rest of the tree; null if it is not in this tree.</summary>
    public abstract FieldSample? SampleSurface(int owner, Point3D p);

    /// <summary>Safe bounds of the field over a box.</summary>
    public abstract Interval Bounds(Box3D box);

    /// <summary>
    /// If the zero set inside <paramref name="box"/> can only be part of one analytic surface, the smallest radius of
    /// curvature it has there (∞ for a plane); otherwise null. Ask it of a field simplified for the same box.
    /// </summary>
    public virtual double? SinglePatch(Box3D box) => null;

    /// <summary>The bound every 1-Lipschitz field satisfies: f(centre) ± half the box diagonal.</summary>
    protected Interval LipschitzBounds(Box3D box)
    {
        var v = Value(box.Center);
        var h = HalfDiagonal(box);
        return new(v - h, v + h);
    }

    internal static double HalfDiagonal(Box3D box) =>
        Math.Sqrt(Sq(box.Max.X - box.Min.X) + Sq(box.Max.Y - box.Min.Y) + Sq(box.Max.Z - box.Min.Z)) / 2;

    static double Sq(double x) => x * x;
}

public sealed class LeafField(Surface surface, bool complement = false) : Field
{
    public Surface Surface { get; } = surface;

    /// <summary>True for the material outside the surface, e.g. the wall of a hole.</summary>
    public bool Complement { get; } = complement;

    public override FieldSample Sample(Point3D p)
    {
        var d = Surface.Distance(p);
        var g = Surface.Gradient(p);
        return Complement ? new(-d, Surface.Id, -g) : new(d, Surface.Id, g);
    }

    public override double Value(Point3D p)
    {
        var d = Surface.Distance(p);
        return Complement ? -d : d;
    }

    public override FieldSample? SampleSurface(int owner, Point3D p) => owner == Surface.Id ? Sample(p) : null;

    public override double? SinglePatch(Box3D box)
    {
        var s = Surface;
        switch (s.Kind)
        {
            case SurfaceKind.Plane:
                return double.PositiveInfinity;
            case SurfaceKind.Torus:
                var r = Math.Min(s.Minor, Math.Abs(s.Radius - s.Minor));
                return r > 1e-9 ? r : null;
            case SurfaceKind.Cone:
                // Curvature radius ρ / cos α across the cone: unbounded towards the apex.
                var axial = s.Axis.Dot(box.Center - s.Origin);
                var rho = ((box.Center - s.Origin) - s.Axis * axial).Length - HalfDiagonal(box);
                return rho > 1e-9 ? rho / Math.Cos(s.Angle) : null;
            default:
                return s.Radius;
        }
    }

    public override Interval Bounds(Box3D box)
    {
        if (Surface.Kind == SurfaceKind.Cylinder && AxisIndex(Surface.Axis) is { } axis)
        {
            // Axis-aligned cylinder: the nearest and farthest distance from the axis over the box are exact.
            var (u, v) = ((axis + 1) % 3, (axis + 2) % 3);
            double Gap(int a) => Math.Max(0, Math.Max(At(box.Min, a) - At(Surface.Origin, a), At(Surface.Origin, a) - At(box.Max, a)));
            double Far(int a) => Math.Max(Math.Abs(At(box.Min, a) - At(Surface.Origin, a)), Math.Abs(At(box.Max, a) - At(Surface.Origin, a)));
            var radial = new Interval(Math.Sqrt(Gap(u) * Gap(u) + Gap(v) * Gap(v)) - Surface.Radius, Math.Sqrt(Far(u) * Far(u) + Far(v) * Far(v)) - Surface.Radius);
            return Complement ? radial.Negate() : radial;
        }
        if (Surface.Kind == SurfaceKind.Sphere)
        {
            // Nearest and farthest distance from the centre over the box are exact.
            double Gap(int a) => Math.Max(0, Math.Max(At(box.Min, a) - At(Surface.Origin, a), At(Surface.Origin, a) - At(box.Max, a)));
            double Far(int a) => Math.Max(Math.Abs(At(box.Min, a) - At(Surface.Origin, a)), Math.Abs(At(box.Max, a) - At(Surface.Origin, a)));
            var ball = new Interval(Math.Sqrt(Gap(0) * Gap(0) + Gap(1) * Gap(1) + Gap(2) * Gap(2)) - Surface.Radius,
                Math.Sqrt(Far(0) * Far(0) + Far(1) * Far(1) + Far(2) * Far(2)) - Surface.Radius);
            return Complement ? ball.Negate() : ball;
        }
        if (Surface.Kind != SurfaceKind.Plane) return LipschitzBounds(box);
        // Linear: exact over the box.
        var n = Surface.Axis;
        var centre = n.Dot(box.Center - Surface.Origin);
        var spread = (Math.Abs(n.X) * (box.Max.X - box.Min.X) + Math.Abs(n.Y) * (box.Max.Y - box.Min.Y) + Math.Abs(n.Z) * (box.Max.Z - box.Min.Z)) / 2;
        var plane = new Interval(centre - spread, centre + spread);
        return Complement ? plane.Negate() : plane;
    }

    static int? AxisIndex(Vector3D a) =>
        a.Y == 0 && a.Z == 0 ? 0 : a.X == 0 && a.Z == 0 ? 1 : a.X == 0 && a.Y == 0 ? 2 : null;

    static double At(Point3D p, int axis) => axis switch { 0 => p.X, 1 => p.Y, _ => p.Z };
}

/// <summary>Intersection.</summary>
public sealed class MaxField(IReadOnlyList<Field> children) : Field
{
    public IReadOnlyList<Field> Children { get; } = children;

    public override FieldSample Sample(Point3D p)
    {
        var best = Children[0].Sample(p);
        for (var i = 1; i < Children.Count; i++)
        {
            var s = Children[i].Sample(p);
            if (s.Value > best.Value) best = s;
        }
        return best;
    }

    public override FieldSample? SampleSurface(int owner, Point3D p)
    {
        foreach (var c in Children)
            if (c.SampleSurface(owner, p) is { } s) return s;
        return null;
    }

    public override double Value(Point3D p)
    {
        var best = Children[0].Value(p);
        for (var i = 1; i < Children.Count; i++)
        {
            var v = Children[i].Value(p);
            if (v > best) best = v;
        }
        return best;
    }

    public override Interval Bounds(Box3D box)
    {
        var result = Children[0].Bounds(box);
        for (var i = 1; i < Children.Count; i++) result = Interval.Max(result, Children[i].Bounds(box));
        return result;
    }

    // A child whose upper bound is below another child's lower bound is never the maximum in the box.
    public override Field Simplify(Box3D box) => FieldAlgebra.Keep(this, Children, box, intersection: true);
}

/// <summary>Union.</summary>
public sealed class MinField(IReadOnlyList<Field> children) : Field
{
    public IReadOnlyList<Field> Children { get; } = children;

    public override FieldSample Sample(Point3D p)
    {
        var best = Children[0].Sample(p);
        for (var i = 1; i < Children.Count; i++)
        {
            var s = Children[i].Sample(p);
            if (s.Value < best.Value) best = s;
        }
        return best;
    }

    public override FieldSample? SampleSurface(int owner, Point3D p)
    {
        foreach (var c in Children)
            if (c.SampleSurface(owner, p) is { } s) return s;
        return null;
    }

    public override double Value(Point3D p)
    {
        var best = Children[0].Value(p);
        for (var i = 1; i < Children.Count; i++)
        {
            var v = Children[i].Value(p);
            if (v < best) best = v;
        }
        return best;
    }

    public override Interval Bounds(Box3D box)
    {
        var result = Children[0].Bounds(box);
        for (var i = 1; i < Children.Count; i++) result = Interval.Min(result, Children[i].Bounds(box));
        return result;
    }

    public override Field Simplify(Box3D box) => FieldAlgebra.Keep(this, Children, box, intersection: false);
}

/// <summary>Complement. <see cref="FieldAlgebra.Normalize"/> pushes it down to the leaves where it can.</summary>
public sealed class NegateField(Field child) : Field
{
    public Field Child { get; } = child;

    public override FieldSample Sample(Point3D p)
    {
        var s = Child.Sample(p);
        return new(-s.Value, s.Owner, -s.Gradient);
    }

    public override FieldSample? SampleSurface(int owner, Point3D p) =>
        Child.SampleSurface(owner, p) is { } s ? new(-s.Value, s.Owner, -s.Gradient) : null;

    public override double Value(Point3D p) => -Child.Value(p);

    public override Interval Bounds(Box3D box) => Child.Bounds(box).Negate();

    public override double? SinglePatch(Box3D box) => Child.SinglePatch(box);

    public override Field Simplify(Box3D box) => Child.Simplify(box) is var c && ReferenceEquals(c, Child) ? this : new NegateField(c);
}

/// <summary>
/// Exact constant-radius rolling-ball fillet on the convex edge between two leaves of the same intersection.
/// The ball centre runs along {dA = -r, dB = -r}. Writing p minus its centre as w = α·gA + β·gB, p lies in the
/// fillet sector when α, β ≥ 0, and the field there is |w| − r, the distance to the centre line or circle.
/// That is exact for plane–plane edges at any angle and for a plane perpendicular to a cylinder; in both cases
/// gA·gB is the same constant everywhere. Other pairs are rejected at compile time.
/// </summary>
public sealed class RoundedEdgeField : Field
{
    readonly double _c;

    readonly MaxField _sharp;

    public RoundedEdgeField(LeafField a, LeafField b, double radius, Surface fillet)
    {
        (A, B, Radius, Fillet) = (a, b, radius, fillet);
        _sharp = new MaxField([a, b]);
        var origin = new Point3D(0, 0, 0);
        _c = a.Sample(origin).Gradient.Dot(b.Sample(origin).Gradient);
    }

    public LeafField A { get; }
    public LeafField B { get; }
    public double Radius { get; }
    public Surface Fillet { get; }

    public override FieldSample Sample(Point3D p)
    {
        var sa = A.Sample(p);
        var sb = B.Sample(p);
        var sharp = sa.Value >= sb.Value ? sa : sb;

        var c = sa.Gradient.Dot(sb.Gradient);
        var denom = 1 - c * c;
        if (denom < 1e-12) return sharp;

        var a = sa.Value + Radius;
        var b = sb.Value + Radius;
        var alpha = (a - b * c) / denom;
        var beta = (b - a * c) / denom;
        if (alpha < 0 || beta < 0) return sharp;

        return Rounded(sa, sb, alpha, beta);
    }

    public override FieldSample? SampleSurface(int owner, Point3D p)
    {
        if (owner != Fillet.Id) return A.SampleSurface(owner, p) ?? B.SampleSurface(owner, p);
        var sa = A.Sample(p);
        var sb = B.Sample(p);
        var c = sa.Gradient.Dot(sb.Gradient);
        var denom = 1 - c * c;
        var a = sa.Value + Radius;
        var b = sb.Value + Radius;
        return Rounded(sa, sb, (a - b * c) / denom, (b - a * c) / denom);
    }

    /// <summary>
    /// Rounding only removes material, so the sharp bounds give the lower end. In the sector the field is |w| − r,
    /// and α, β are linear in the leaf values, so their intervals bound |w| from above.
    /// </summary>
    public override Interval Bounds(Box3D box)
    {
        var ia = A.Bounds(box);
        var ib = B.Bounds(box);
        var sharp = Interval.Max(ia, ib);
        var denom = 1 - _c * _c;
        if (denom < 1e-12) return sharp;

        var (alphaHi, betaHi) = SectorReach(ia, ib, denom);
        if (alphaHi < 0 || betaHi < 0) return sharp;

        var (am, bm) = (Math.Max(alphaHi, 0), Math.Max(betaHi, 0));
        var wMax = Math.Sqrt(am * am + bm * bm + 2 * am * bm * Math.Max(_c, 0));
        return new Interval(sharp.Lo, Math.Max(sharp.Hi, wMax - Radius)).Intersect(LipschitzBounds(box));
    }

    /// <summary>A box entirely inside the fillet sector holds only the fillet: a cylinder, or a torus round a cylinder.</summary>
    public override double? SinglePatch(Box3D box)
    {
        var denom = 1 - _c * _c;
        if (denom < 1e-12) return null;
        var (ia, ib) = (A.Bounds(box), B.Bounds(box));
        var (aLo, aHi, bLo, bHi) = (ia.Lo + Radius, ia.Hi + Radius, ib.Lo + Radius, ib.Hi + Radius);
        var alphaLo = (aLo - Math.Max(_c * bLo, _c * bHi)) / denom;
        var betaLo = (bLo - Math.Max(_c * aLo, _c * aHi)) / denom;
        if (alphaLo < 1e-9 || betaLo < 1e-9) return null;
        var cylinder = A.Surface.Kind == SurfaceKind.Cylinder ? A.Surface.Radius
            : B.Surface.Kind == SurfaceKind.Cylinder ? B.Surface.Radius : double.PositiveInfinity;
        return Math.Min(Radius, cylinder);
    }

    // Upper ends of α and β over the box: if either is negative, no point of the box is in the fillet sector.
    (double AlphaHi, double BetaHi) SectorReach(Interval ia, Interval ib, double denom)
    {
        var (aLo, aHi) = (ia.Lo + Radius, ia.Hi + Radius);
        var (bLo, bHi) = (ib.Lo + Radius, ib.Hi + Radius);
        return ((aHi - Math.Min(_c * bLo, _c * bHi)) / denom, (bHi - Math.Min(_c * aLo, _c * aHi)) / denom);
    }

    /// <summary>Outside the fillet sector the field is the sharp intersection; A wins ties, as in Sample.</summary>
    public override Field Simplify(Box3D box)
    {
        var denom = 1 - _c * _c;
        if (denom < 1e-12) return _sharp.Simplify(box);
        var (alphaHi, betaHi) = SectorReach(A.Bounds(box), B.Bounds(box), denom);
        return alphaHi < 0 || betaHi < 0 ? _sharp.Simplify(box) : this;
    }

    FieldSample Rounded(FieldSample sa, FieldSample sb, double alpha, double beta)
    {
        var w = sa.Gradient * alpha + sb.Gradient * beta;
        var len = w.Length;
        return len > 0 ? new(len - Radius, Fillet.Id, w / len) : new(-Radius, Fillet.Id, sa.Gradient);
    }

    /// <summary>The ball centre and its two contact points for a point <paramref name="q"/> on the sharp edge.</summary>
    public (Point3D Centre, Point3D OnA, Point3D OnB) Contacts(Point3D q)
    {
        var ga = A.Sample(q).Gradient;
        var gb = B.Sample(q).Gradient;
        var centre = q - (ga + gb) * (Radius / (1 + ga.Dot(gb)));
        return (centre, centre + ga * Radius, centre + gb * Radius);
    }
}

/// <summary>
/// Closed shell of wall t: max(f, −f − t). The inner surface {f = −t} is owned by an inner surface per outer
/// owner. Because f is 1-Lipschitz, every point of the cavity is more than t from the outer boundary, so the
/// wall is never thinner than t; at sharp edges it is thicker, like a CAD shell. Edge features after the shell
/// reshape only <see cref="Outer"/>: max(outer, −f − t) rounds the outside and leaves the cavity as it was.
/// </summary>
public sealed class ShellField : Field
{
    readonly IReadOnlyDictionary<int, int> _innerOf;
    readonly IReadOnlyDictionary<int, int> _outerOf;

    public ShellField(Field child, double thickness, IReadOnlyDictionary<int, int> innerOf)
        : this(child, thickness, innerOf, innerOf.ToDictionary(kv => kv.Value, kv => kv.Key), child) { }

    ShellField(Field child, double thickness, IReadOnlyDictionary<int, int> innerOf, IReadOnlyDictionary<int, int> outerOf, Field outer)
    {
        (Child, Thickness, _innerOf, _outerOf, Outer) = (child, thickness, innerOf, outerOf, outer);
    }

    /// <summary>The solid whose offset is the cavity.</summary>
    public Field Child { get; }

    /// <summary>The outside: the child itself, unless a later edge feature reshaped it.</summary>
    public Field Outer { get; }

    public double Thickness { get; }

    bool Plain => ReferenceEquals(Outer, Child);

    public ShellField WithOuter(Field outer) => new(Child, Thickness, _innerOf, _outerOf, outer);

    public override FieldSample Sample(Point3D p)
    {
        var c = Child.Sample(p);
        var inner = -c.Value - Thickness;
        var o = Plain ? c : Outer.Sample(p);
        return o.Value >= inner ? o : new(inner, _innerOf[c.Owner], -c.Gradient);
    }

    public override double Value(Point3D p)
    {
        var v = Child.Value(p);
        return Math.Max(Plain ? v : Outer.Value(p), -v - Thickness);
    }

    public override FieldSample? SampleSurface(int owner, Point3D p)
    {
        if (!_outerOf.TryGetValue(owner, out var outer)) return Outer.SampleSurface(owner, p);
        return Child.SampleSurface(outer, p) is { } s ? new(-s.Value - Thickness, owner, -s.Gradient) : null;
    }

    public override Interval Bounds(Box3D box)
    {
        var c = Child.Bounds(box);
        return Interval.Max(Plain ? c : Outer.Bounds(box), new Interval(-c.Hi - Thickness, -c.Lo - Thickness));
    }

    /// <summary>Where only the inner surface can win, it is the offset of a single child surface or unknown.</summary>
    public override double? SinglePatch(Box3D box)
    {
        var c = Child.Bounds(box);
        var o = Plain ? c : Outer.Bounds(box);
        if (-c.Hi - Thickness <= o.Hi + 1e-9 || Child.Simplify(box) is not LeafField leaf) return null;
        if (leaf.Surface.Kind == SurfaceKind.Plane) return double.PositiveInfinity;
        var r = leaf.Complement ? leaf.Surface.Radius + Thickness : leaf.Surface.Radius - Thickness;
        return r > 0 ? r : null;
    }

    // Where the cavity cannot reach, the shell is just its outside.
    public override Field Simplify(Box3D box)
    {
        var c = Child.Bounds(box);
        var o = Plain ? c : Outer.Bounds(box);
        if (-c.Lo - Thickness < o.Lo) return Outer.Simplify(box);
        var child = Child.Simplify(box);
        var outer = Plain ? child : Outer.Simplify(box);
        return ReferenceEquals(child, Child) && ReferenceEquals(outer, Outer) ? this : new ShellField(child, Thickness, _innerOf, _outerOf, outer);
    }
}

public static class FieldAlgebra
{
    /// <summary>
    /// Simplify for an intersection or union: drops the children that can never decide it in the box and
    /// simplifies the rest. Returns <paramref name="self"/> when nothing changes, so a settled tree allocates nothing.
    /// </summary>
    internal static Field Keep(Field self, IReadOnlyList<Field> children, Box3D box, bool intersection)
    {
        var n = children.Count;
        Span<Interval> bounds = n <= 64 ? stackalloc Interval[n] : new Interval[n];
        // Intersection: a child whose upper bound is below another's lower bound never wins. Union: mirrored.
        var limit = intersection ? double.NegativeInfinity : double.PositiveInfinity;
        for (var i = 0; i < n; i++)
        {
            bounds[i] = children[i].Bounds(box);
            limit = intersection ? Math.Max(limit, bounds[i].Lo) : Math.Min(limit, bounds[i].Hi);
        }
        List<Field>? kept = null;
        for (var i = 0; i < n; i++)
        {
            var keep = intersection ? bounds[i].Hi >= limit : bounds[i].Lo <= limit;
            var simple = keep ? children[i].Simplify(box) : null;
            if (kept is null && (!keep || !ReferenceEquals(simple, children[i])))
            {
                kept = new List<Field>(n);
                for (var j = 0; j < i; j++) kept.Add(children[j]); // all kept and unchanged so far
            }
            if (keep) kept?.Add(simple!);
        }
        if (kept is null) return self;
        // A face that a rounded edge in the same intersection holds is never above it: the rounded edge is at least
        // the face's own value, and equal to it with the face's own sample. Fillets sharing a face leave such pairs.
        if (intersection)
            kept.RemoveAll(c => c is LeafField l && kept.Any(k => k is RoundedEdgeField r && (r.A == l || r.B == l)));
        return kept.Count == 1 ? kept[0] : intersection ? new MaxField(kept) : new MinField(kept);
    }

    /// <summary>
    /// Pushes complements down to the leaves (De Morgan) and flattens nested intersections and unions,
    /// so two leaves that meet in a convex edge end up as siblings of one <see cref="MaxField"/>.
    /// </summary>
    public static Field Normalize(Field field, bool negate = false) => field switch
    {
        LeafField l => negate ? new LeafField(l.Surface, !l.Complement) : l,
        NegateField n => Normalize(n.Child, !negate),
        MaxField m => Flatten(m.Children, negate, intersection: !negate),
        MinField m => Flatten(m.Children, negate, intersection: negate),
        _ => negate ? new NegateField(field) : field,
    };

    static Field Flatten(IEnumerable<Field> children, bool negate, bool intersection)
    {
        var list = new List<Field>();
        foreach (var child in children.Select(c => Normalize(c, negate)))
        {
            if (intersection && child is MaxField mx) list.AddRange(mx.Children);
            else if (!intersection && child is MinField mn) list.AddRange(mn.Children);
            else list.Add(child);
        }
        return list.Count == 1 ? list[0] : intersection ? new MaxField(list) : new MinField(list);
    }

    /// <summary>Nodes of the tree. Does not enter a shell: features after a shell see it as one solid.</summary>
    public static IEnumerable<Field> Descendants(Field field)
    {
        yield return field;
        var children = field switch
        {
            MaxField m => m.Children,
            MinField m => m.Children,
            NegateField n => [n.Child],
            RoundedEdgeField r => [r.A, r.B],
            ProfileField p => p.Leaves,
            _ => [],
        };
        foreach (var c in children)
        foreach (var d in Descendants(c))
            yield return d;
    }
}
