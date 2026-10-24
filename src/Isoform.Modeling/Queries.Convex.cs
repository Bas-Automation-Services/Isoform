using BasAutomation.Geometry;
using Isoform.Geometry;

namespace Isoform.Modeling;

public static partial class Queries
{
    /// <summary>
    /// The corners of a convex polytope holding the points of the box inside every one of the holds: the box cut by a
    /// half-space per hold that cuts it. A plane gives its own; a solid cylinder or ball the plane touching it square to
    /// the way to the box centre (u); outside a cylinder of radius r, with the box no wider than w across u and the
    /// axis, |x⊥|² = (u·x⊥)² + (v·x⊥)² ≥ r² keeps u·x⊥ ≥ √(r² − w²) on the box's side of the axis: the plane through
    /// the chord, a sag of w²/2r from the surface. Every corner is where three of the planes meet inside the rest.
    /// A smooth field the region keeps at or below zero (a blend the solid lies in), or on zero (a blend face), cuts by
    /// its tangent plane at the box centre moved out by M·R²/2: with its gradient M-Lipschitz over the box (its
    /// curvature, <see cref="Bend"/>) it is within that of the plane at any point R across the box from the centre,
    /// counted square to the axes it does not change along. A blend face lies in a slab round its tangent plane,
    /// second order in the box's size. Empty when one of the cuts misses the box; the box's own corners when no
    /// corner of the cut box is found.
    /// </summary>
    static Point3D[] Corners(Box3D box, List<LeafField> holds, List<(Field Field, bool Zero)> smooth)
    {
        var planes = new List<(Vector3D N, double C)>
        {
            (new(1, 0, 0), box.Max.X), (new(-1, 0, 0), -box.Min.X), (new(0, 1, 0), box.Max.Y),
            (new(0, -1, 0), -box.Min.Y), (new(0, 0, 1), box.Max.Z), (new(0, 0, -1), -box.Min.Z),
        };
        bool Cut(Vector3D n, double c)
        {
            if (Support(box, n) <= c) return true; // the box lies inside it
            if (-Support(box, -n) > c) return false; // the box lies outside it
            planes.Add((n, c));
            return true;
        }
        foreach (var (z, zero) in smooth)
        {
            if (Bend(z, box) is not { } m) continue;
            var s = z.Sample(box.Center);
            var length = s.Gradient.Length;
            if (!(length > 1e-6)) continue;
            Vector3D[] axes = [new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)];
            var size = box.Max - box.Min;
            double[] extent = [size.X, size.Y, size.Z];
            var r2 = Enumerable.Range(0, 3).Where(a => !Invariant(z, axes[a])).Sum(a => extent[a] * extent[a] / 4);
            var (n, at, slack) = (s.Gradient / length, s.Gradient.Dot(box.Center.AsVector()) / length, m * r2 / 2 / length);
            if (!Cut(n, at - s.Value / length + slack) || zero && !Cut(-n, -at + s.Value / length + slack)) return [];
        }
        foreach (var h in holds)
        {
            var s = h.Surface;
            (Vector3D N, double C)? cut = null;
            if (s.Kind == SurfaceKind.Plane)
            {
                var n = h.Complement ? -s.Axis : s.Axis;
                cut = (n, n.Dot(s.Origin.AsVector()));
            }
            else if (s.Kind is SurfaceKind.Cylinder or SurfaceKind.Sphere)
            {
                var to = box.Center - s.Origin;
                if (s.Kind == SurfaceKind.Cylinder) to -= s.Axis * to.Dot(s.Axis);
                if (to.Length < 1e-9) continue;
                var u = to / to.Length;
                var o = u.Dot(s.Origin.AsVector());
                if (!h.Complement) cut = (u, o + s.Radius);
                else if (s.Kind == SurfaceKind.Cylinder)
                {
                    var v = s.Axis.Cross(u);
                    var across = v.Dot(s.Origin.AsVector());
                    var w = Math.Max(Support(box, v) - across, Support(box, -v) + across);
                    if (w >= s.Radius) continue;
                    var d = Math.Sqrt(s.Radius * s.Radius - w * w);
                    if (-Support(box, -u) - o <= -d) continue; // the box reaches the far side
                    cut = (-u, -(o + d));
                }
            }
            if (cut is { } c && !Cut(c.N, c.C)) return [];
        }
        Point3D[] own = [.. Enumerable.Range(0, 8).Select(i => new Point3D((i & 1) == 0 ? box.Min.X : box.Max.X, (i & 2) == 0 ? box.Min.Y : box.Max.Y, (i & 4) == 0 ? box.Min.Z : box.Max.Z))];
        if (planes.Count == 6) return own;
        // A corner may stand outside a plane by rounding (a face's two sides meet in one plane).
        var scale = 1e-9 * (1 + own.Max(p => Math.Max(Math.Abs(p.X), Math.Max(Math.Abs(p.Y), Math.Abs(p.Z)))));
        var corners = new List<Point3D>();
        var a = new double[3, 3];
        for (var i = 0; i < planes.Count; i++)
        for (var j = i + 1; j < planes.Count; j++)
        for (var k = j + 1; k < planes.Count; k++)
        {
            (Vector3D N, double C)[] three = [planes[i], planes[j], planes[k]];
            for (var r = 0; r < 3; r++) (a[r, 0], a[r, 1], a[r, 2]) = (three[r].N.X, three[r].N.Y, three[r].N.Z);
            if (Solve(a, [three[0].C, three[1].C, three[2].C]) is not { } x) continue;
            var p = new Point3D(x[0], x[1], x[2]);
            if (planes.All(q => q.N.Dot(p.AsVector()) <= q.C + scale)) corners.Add(p);
        }
        return corners.Count > 0 ? [.. corners] : own;
    }

    /// <summary>The blends the solid lies in (parts of its intersection), for <see cref="Corners"/>: no point of it is above them.</summary>
    static List<(Field Field, bool Zero)> Smooth(Field f, List<(Field Field, bool Zero)> list)
    {
        switch (f)
        {
            case BlendField b: list.Add((b, false)); break;
            case MaxField m: foreach (var c in m.Children) Smooth(c, list); break;
            case PlacedField p: list.AddRange(Smooth(p.Child, []).Select(z => ((Field)new PlacedField(z.Field, p.Placement), z.Zero))); break;
        }
        return list;
    }

    /// <summary>
    /// A bound on how fast the field's gradient turns over the box (its Hessian's norm, where it has one, and the
    /// gradient continuous everywhere): 0 for a plane, 1/ρ for a cylinder or sphere no nearer than ρ to its axis or
    /// centre. The quadratic smooth min or max of a and b is S(a, b) = (a + b)/2 ∓ k/4 ∓ (a − b)²/4k where they are
    /// within k, its weights summing to 1, so it bends no more than its sides do plus |∇a − ∇b|²/2k ≤ 2/k. Null for
    /// anything with a crease.
    /// </summary>
    static double? Bend(Field f, Box3D box)
    {
        switch (f)
        {
            case LeafField { Surface.Kind: SurfaceKind.Plane }:
                return 0;
            case LeafField { Surface.Kind: SurfaceKind.Cylinder or SurfaceKind.Sphere } l:
            {
                var s = l.Surface;
                var centre = s.Kind == SurfaceKind.Cylinder ? FromAxis(box.Center, s) : (box.Center - s.Origin).Length;
                var near = centre - HalfDiagonal(box);
                return near > 0 ? 1 / near : null;
            }
            case ProfileField p when Disc(p) is { } wall:
                return Bend(wall, box);
            case BlendField b:
                return Bend(b.A, box) is { } a && Bend(b.B, box) is { } c ? Math.Max(a, c) + 2 / b.K : null;
            case PlacedField p:
                return Bend(p.Child, p.LocalBox(box));
            default:
                return null;
        }
    }

    /// <summary>
    /// An upper bound of f over the polytope with these corners (within the box, the holds those of
    /// <see cref="High"/>), from the field's shape. A convex field is highest at a corner: exact. A concave one lies
    /// below its tangent plane anywhere, which is highest at a corner: second order in the polytope's size. The
    /// smooth union of two convex sides is a concave rising function S(a, b) of convex ones, so below its tangent
    /// plane in (a, b), Sa·a + Sb·b with Sa, Sb ≥ 0, a convex field highest at a corner: second order too. Where
    /// boxes alone close only cell by cell along a face, a polytope cut close to the face (<see cref="Corners"/>)
    /// closes them on a curve: a fitted plate outline against the design's blend. Never above <see cref="High"/>
    /// (an intersection or union of parts that are not, a placed field and a blend as <see cref="High"/> takes them).
    /// </summary>
    static double Upper(Field f, Box3D box, Point3D[] corners, List<LeafField> holds)
    {
        if (corners.Length == 0) return double.NegativeInfinity;
        if (Convex(f)) return Math.Min(High(f, box, holds), corners.Max(f.Value));
        if (Concave(f)) return Math.Min(High(f, box, holds), Tangent(f, corners));
        switch (f)
        {
            case MaxField m: return m.Children.Max(c => Upper(c, box, corners, holds));
            case MinField { Children: [var a, var b] } when Convex(a) && Convex(b):
                return Math.Min(Upper(a, box, corners, holds), Math.Min(Upper(b, box, corners, holds),
                    Mixed(corners.Select(a.Value).ToArray(), corners.Select(b.Value).ToArray())));
            case MinField m: return m.Children.Min(c => Upper(c, box, corners, holds));
            case BlendField { Union: true } b:
            {
                var x0 = Centroid(corners);
                var (a0, b0) = (b.A.Value(x0), b.B.Value(x0));
                var sa = Math.Abs(a0 - b0) >= b.K ? (a0 <= b0 ? 1 : 0) : 0.5 - (a0 - b0) / (2 * b.K);
                var s0 = b.Value(x0) - sa * a0 - (1 - sa) * b0;
                var (ua, ub) = (Upper(b.A, box, corners, holds), Upper(b.B, box, corners, holds));
                var tangent = Convex(b.A) && Convex(b.B)
                    ? s0 + corners.Max(c => sa * b.A.Value(c) + (1 - sa) * b.B.Value(c))
                    : s0 + (sa == 0 ? 0 : sa * ua) + (sa == 1 ? 0 : (1 - sa) * ub);
                return Math.Min(tangent, Math.Min(ua, ub) - Sink(b, box));
            }
            case PlacedField p:
                return Upper(p.Child, p.LocalBox(box), [.. corners.Select(p.Placement.ToLocal)], holds.Select(l => Moved(l, p.Placement, toWorld: false)).ToList());
            default:
                return High(f, box, holds);
        }
    }

    /// <summary>
    /// The union of two convex fields over a polytope, given their values at its corners: min(a, b) ≤ λa + (1 − λ)b
    /// for every λ in [0, 1], a convex field highest at a corner; the best λ is searched for (any is a bound). Exact
    /// on two planes, where the corner check alone would read the higher of the two maxima.
    /// </summary>
    static double Mixed(double[] a, double[] b)
    {
        double At(double t)
        {
            var m = double.NegativeInfinity;
            for (var i = 0; i < a.Length; i++) m = Math.Max(m, t * a[i] + (1 - t) * b[i]);
            return m;
        }
        var (lo, hi) = (0.0, 1.0);
        for (var it = 0; it < 40; it++)
        {
            var (t1, t2) = (lo + (hi - lo) / 3, hi - (hi - lo) / 3);
            if (At(t1) <= At(t2)) hi = t2;
            else lo = t1;
        }
        return Math.Min(Math.Min(At(0), At(1)), At((lo + hi) / 2));
    }

    /// <summary>A concave field's tangent plane at the corners' centroid, at its highest corner (+∞ without a gradient).</summary>
    static double Tangent(Field f, Point3D[] corners)
    {
        var x0 = Centroid(corners);
        var s = f.Sample(x0);
        var g = s.Gradient;
        if (!double.IsFinite(g.X) || !double.IsFinite(g.Y) || !double.IsFinite(g.Z)) return double.PositiveInfinity;
        return s.Value + corners.Max(c => g.Dot(c - x0));
    }

    static Point3D Centroid(Point3D[] p) => new(p.Average(q => q.X), p.Average(q => q.Y), p.Average(q => q.Z));

    /// <summary>
    /// True if the field is a convex function of the point: a plane, a solid cylinder or ball, the intersection or
    /// smooth intersection (convex and rising in both) of convex fields, an extruded convex profile (the signed
    /// distance of a convex region), a complement of a concave one. Its sampled gradient is then a subgradient.
    /// </summary>
    static bool Convex(Field f) => f switch
    {
        LeafField l => l.Surface.Kind == SurfaceKind.Plane || !l.Complement && l.Surface.Kind is SurfaceKind.Cylinder or SurfaceKind.Sphere,
        MaxField m => m.Children.All(Convex),
        BlendField { Union: false } b => Convex(b.A) && Convex(b.B),
        ProfileField p => !p.Revolved && Enumerable.Range(0, p.Edges.Count).All(k => p.Turn(k) >= 0 && p.Edges[k].Bulge >= 0),
        NegateField n => Concave(n.Child),
        PlacedField p => Convex(p.Child),
        _ => false,
    };

    /// <summary>True if the field is a concave function of the point (the mirror of <see cref="Convex"/>); its sampled gradient is a supergradient.</summary>
    static bool Concave(Field f) => f switch
    {
        LeafField l => l.Surface.Kind == SurfaceKind.Plane || l.Complement && l.Surface.Kind is SurfaceKind.Cylinder or SurfaceKind.Sphere,
        MinField m => m.Children.All(Concave),
        BlendField { Union: true } b => Concave(b.A) && Concave(b.B),
        NegateField n => Convex(n.Child),
        PlacedField p => Concave(p.Child),
        _ => false,
    };

    /// <summary>
    /// True if splitting a box along <paramref name="axis"/> cannot tighten the bounds of the field over it, nor tell
    /// more of the solid: the field does not change along it (planes, cylinders and extruded profiles parallel to it,
    /// and what is built of those), or is the intersection of such fields and planes, whose highest over a box is its
    /// parts' highest and whose solid is a prism cut by the planes. A union of such parts is neither (a post on a slab
    /// has the slab's faces only below the post's foot). A plate against a design extruded the same way then splits in
    /// its plane only.
    /// </summary>
    static bool Separable(Field f, Vector3D axis) => f switch
    {
        LeafField { Surface.Kind: SurfaceKind.Plane } => true,
        MaxField m => m.Children.All(c => Separable(c, axis)),
        PlacedField p => Separable(p.Child, p.Placement.ToLocal(axis)),
        _ => Invariant(f, axis),
    };

    static bool Invariant(Field f, Vector3D axis) => f switch
    {
        LeafField { Surface.Kind: SurfaceKind.Plane } l => Math.Abs(l.Surface.Axis.Dot(axis)) < 1e-12,
        LeafField { Surface.Kind: SurfaceKind.Cylinder } l => l.Surface.Axis.Cross(axis).Length < 1e-12,
        ProfileField p => !p.Revolved && p.Frame.Z.Cross(axis).Length < 1e-12,
        MaxField m => m.Children.All(c => Invariant(c, axis)),
        MinField m => m.Children.All(c => Invariant(c, axis)),
        BlendField b => Invariant(b.A, axis) && Invariant(b.B, axis),
        NegateField n => Invariant(n.Child, axis),
        PlacedField p => Invariant(p.Child, p.Placement.ToLocal(axis)),
        _ => false,
    };

    /// <summary>The world axes (0 to 2) not both fields are <see cref="Separable"/> along; all three when both are along every one.</summary>
    static int[] Across(Field f, Field s)
    {
        Vector3D[] axes = [new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)];
        var across = Enumerable.Range(0, 3).Where(a => !(Separable(f, axes[a]) && Separable(s, axes[a]))).ToArray();
        return across.Length == 0 ? [0, 1, 2] : across;
    }

    static double Extent(Box3D b, int axis) => axis switch { 0 => b.Max.X - b.Min.X, 1 => b.Max.Y - b.Min.Y, _ => b.Max.Z - b.Min.Z };

    /// <summary>Half the box's diagonal over these axes.</summary>
    static double Reach(Box3D b, int[] axes) => Math.Sqrt(axes.Sum(a => Extent(b, a) * Extent(b, a))) / 2;

    /// <summary>The box halved along those of the axes at least half as long as the longest of them: a plate splits in its plane.</summary>
    static Box3D[] Split(Box3D b, int[] axes)
    {
        var longest = axes.Max(a => Extent(b, a));
        var mask = axes.Where(a => Extent(b, a) >= longest / 2).Aggregate(0, (m, a) => m | 1 << a);
        if (mask == 7) return Split(b);
        var c = b.Center;
        var children = new List<Box3D>();
        for (var i = 0; i < 8; i++)
        {
            if ((i & ~mask) != 0) continue;
            children.Add(new Box3D(
                new Point3D((mask & 1) == 0 ? b.Min.X : (i & 1) == 0 ? b.Min.X : c.X, (mask & 2) == 0 ? b.Min.Y : (i & 2) == 0 ? b.Min.Y : c.Y, (mask & 4) == 0 ? b.Min.Z : (i & 4) == 0 ? b.Min.Z : c.Z),
                new Point3D((mask & 1) == 0 ? b.Max.X : (i & 1) == 0 ? c.X : b.Max.X, (mask & 2) == 0 ? b.Max.Y : (i & 2) == 0 ? c.Y : b.Max.Y, (mask & 4) == 0 ? b.Max.Z : (i & 4) == 0 ? c.Z : b.Max.Z)));
        }
        return [.. children];
    }
}
