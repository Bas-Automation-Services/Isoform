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

    public double Value(Point3D p) => Sample(p).Value;

    /// <summary>The field of one owner surface alone, ignoring the rest of the tree; null if it is not in this tree.</summary>
    public abstract FieldSample? SampleSurface(int owner, Point3D p);

    /// <summary>Safe bounds of the field over a box.</summary>
    public abstract Interval Bounds(Box3D box);

    /// <summary>The bound every 1-Lipschitz field satisfies: f(centre) ± half the box diagonal.</summary>
    protected Interval LipschitzBounds(Box3D box)
    {
        var v = Value(box.Center);
        var h = HalfDiagonal(box);
        return new(v - h, v + h);
    }

    protected static double HalfDiagonal(Box3D box) =>
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

    public override FieldSample? SampleSurface(int owner, Point3D p) => owner == Surface.Id ? Sample(p) : null;

    public override Interval Bounds(Box3D box)
    {
        if (Surface.Kind != SurfaceKind.Plane) return LipschitzBounds(box);
        // Linear: exact over the box.
        var n = Surface.Axis;
        var centre = n.Dot(box.Center - Surface.Origin);
        var spread = (Math.Abs(n.X) * (box.Max.X - box.Min.X) + Math.Abs(n.Y) * (box.Max.Y - box.Min.Y) + Math.Abs(n.Z) * (box.Max.Z - box.Min.Z)) / 2;
        var plane = new Interval(centre - spread, centre + spread);
        return Complement ? plane.Negate() : plane;
    }
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

    public override Interval Bounds(Box3D box)
    {
        var result = Children[0].Bounds(box);
        for (var i = 1; i < Children.Count; i++) result = Interval.Max(result, Children[i].Bounds(box));
        return result;
    }
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

    public override Interval Bounds(Box3D box)
    {
        var result = Children[0].Bounds(box);
        for (var i = 1; i < Children.Count; i++) result = Interval.Min(result, Children[i].Bounds(box));
        return result;
    }
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

    public override Interval Bounds(Box3D box) => Child.Bounds(box).Negate();
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

    public RoundedEdgeField(LeafField a, LeafField b, double radius, Surface fillet)
    {
        (A, B, Radius, Fillet) = (a, b, radius, fillet);
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

        var (aLo, aHi) = (ia.Lo + Radius, ia.Hi + Radius);
        var (bLo, bHi) = (ib.Lo + Radius, ib.Hi + Radius);
        var alphaHi = (aHi - Math.Min(_c * bLo, _c * bHi)) / denom;
        var betaHi = (bHi - Math.Min(_c * aLo, _c * aHi)) / denom;
        if (alphaHi < 0 || betaHi < 0) return sharp;

        var (am, bm) = (Math.Max(alphaHi, 0), Math.Max(betaHi, 0));
        var wMax = Math.Sqrt(am * am + bm * bm + 2 * am * bm * Math.Max(_c, 0));
        return new Interval(sharp.Lo, Math.Max(sharp.Hi, wMax - Radius)).Intersect(LipschitzBounds(box));
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
/// wall is never thinner than t; at sharp edges it is thicker, like a CAD shell.
/// </summary>
public sealed class ShellField(Field child, double thickness, IReadOnlyDictionary<int, int> innerOf) : Field
{
    readonly Dictionary<int, int> _outerOf = innerOf.ToDictionary(kv => kv.Value, kv => kv.Key);

    public Field Child { get; } = child;
    public double Thickness { get; } = thickness;

    public override FieldSample Sample(Point3D p)
    {
        var s = Child.Sample(p);
        var inner = -s.Value - Thickness;
        return s.Value >= inner ? s : new(inner, innerOf[s.Owner], -s.Gradient);
    }

    public override FieldSample? SampleSurface(int owner, Point3D p)
    {
        if (!_outerOf.TryGetValue(owner, out var outer)) return Child.SampleSurface(owner, p);
        return Child.SampleSurface(outer, p) is { } s ? new(-s.Value - Thickness, owner, -s.Gradient) : null;
    }

    public override Interval Bounds(Box3D box)
    {
        var c = Child.Bounds(box);
        return Interval.Max(c, new Interval(-c.Hi - Thickness, -c.Lo - Thickness));
    }
}

public static class FieldAlgebra
{
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
            _ => [],
        };
        foreach (var c in children)
        foreach (var d in Descendants(c))
            yield return d;
    }
}
