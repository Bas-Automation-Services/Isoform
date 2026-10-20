using BasAutomation.Geometry;

namespace Isoform.Geometry;

/// <summary>
/// Field value at a point, the surface that "owns" it (the one deciding the min/max), and the exact gradient
/// of that owner. On the zero set, <see cref="Owner"/> says which source surface the point lies on.
/// </summary>
public readonly record struct FieldSample(double Value, int Owner, Vector3D Gradient);

/// <summary>An implicit solid: negative inside, positive outside, zero on the boundary.</summary>
public abstract class Field
{
    public abstract FieldSample Sample(Point3D p);

    public double Value(Point3D p) => Sample(p).Value;

    /// <summary>The field of one owner surface alone, ignoring the rest of the tree; null if it is not in this tree.</summary>
    public abstract FieldSample? SampleSurface(int owner, Point3D p);
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
}

/// <summary>Complement. Removed by <see cref="FieldAlgebra.Normalize"/>, which pushes it down to the leaves.</summary>
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
}

/// <summary>
/// Exact constant-radius rolling-ball fillet on the convex edge between two leaves of the same intersection.
/// The ball centre runs along {dA = -r, dB = -r}. Writing p minus its centre as w = α·gA + β·gB, p lies in the
/// fillet sector when α, β ≥ 0, and the field there is |w| − r. That is exact for plane–plane edges at any
/// angle and for a plane perpendicular to a cylinder (the gradients are constant within each cross-section);
/// other pairs are rejected at compile time.
/// </summary>
public sealed class RoundedEdgeField(LeafField a, LeafField b, double radius, Surface fillet) : Field
{
    public LeafField A { get; } = a;
    public LeafField B { get; } = b;
    public double Radius { get; } = radius;
    public Surface Fillet { get; } = fillet;

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
        RoundedEdgeField when negate => throw new NotSupportedException("Cannot complement a rounded edge."),
        _ => field,
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
