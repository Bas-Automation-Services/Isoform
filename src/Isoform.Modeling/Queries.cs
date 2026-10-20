using BasAutomation.Geometry;
using Isoform.Geometry;

namespace Isoform.Modeling;

/// <summary>The first boundary point along a ray: its distance from the origin, the surface that owns it and its outward normal.</summary>
public readonly record struct RayHit(double Distance, Point3D Point, int Owner, Vector3D Normal);

/// <summary>
/// Distance from a point to the boundary of a solid, certified: the true distance lies in [<see cref="Lower"/>,
/// <see cref="Upper"/>]. <see cref="Closest"/> is a boundary point at distance <see cref="Upper"/>, owned by <see cref="Owner"/>.
/// </summary>
public readonly record struct DistanceResult(double Lower, double Upper, Point3D Closest, int Owner, bool Inside);

/// <summary>
/// Geometric queries on implicit solids. Each takes one <see cref="Field"/>, so a part of several bodies is queried per body.
/// They rest on two guarantees of every field: it is 1-Lipschitz (|f(p)| never exceeds the distance from p to the
/// boundary) and <see cref="Field.Bounds"/> encloses it over a box (a box whose bounds exclude zero holds no boundary).
/// </summary>
public static partial class Queries
{
    // Newton projections overshoot the surface by this much, so they end on the other side and give a bracket.
    const double Overshoot = 1e-11;

    /// <summary>
    /// First point where a ray meets the boundary: an entry from outside, an exit from inside. Sphere tracing steps by |f|,
    /// which never passes the boundary because the field is 1-Lipschitz; within <paramref name="tolerance"/> of it the
    /// crossing is bracketed and found by root finding, so the point lies on the boundary to ~1e-12. A ray that only grazes
    /// the boundary within the tolerance reports the grazing point. Null if nothing is hit within <paramref name="maxDistance"/>.
    /// <para>
    /// A ray running along a face just inside or outside it would crawl, |f| staying small without dropping. There it
    /// takes longer steps, each only once the segment is proven free of boundary (<see cref="Clear"/>), doubling them
    /// while that holds. Throws if the march still does not settle, rather than report a miss it has not shown.
    /// </para>
    /// </summary>
    public static RayHit? Raycast(Field field, Point3D origin, Vector3D direction, double maxDistance, double tolerance = 1e-7)
    {
        var d = direction.Normalized();
        var side = Math.Sign(field.Value(origin));
        var t = 0.0;
        var (last, jump) = (double.PositiveInfinity, 0.0);
        for (var it = 0; t <= maxDistance; it++)
        {
            if (it == 1_000_000) throw new InvalidOperationException($"the ray march from {origin} did not settle");
            var p = origin + d * t;
            var v = field.Value(p);
            if (v == 0) return Hit(field, t, p);
            var a = Math.Abs(v);
            // |f| neither halving towards a hit nor doubling away from the faces: try a step longer than |f|, proven clear.
            if (a > last / 2 && a < 2 * last && a >= tolerance)
            {
                var s = Math.Max(4 * a, 2 * jump);
                if (Clear(field, p, origin + d * (t + s), side))
                {
                    (t, last, jump) = (t + s, a, s);
                    continue;
                }
            }
            (last, jump) = (a, 0);
            if (a < tolerance)
            {
                // Predict the crossing from the slope along the ray and bracket it; overshoot twice the prediction.
                var slope = field.Sample(p).Gradient.Dot(d) * side;
                if (slope < 0)
                {
                    var t2 = t + Math.Min(2 * Math.Abs(v) / -slope, 1000 * tolerance);
                    var q = origin + d * t2;
                    var v2 = field.Value(q);
                    if (Math.Sign(v2) != side)
                    {
                        var root = v2 == 0 ? q : Root(field, p, q, v, v2);
                        return Hit(field, (root - origin).Length, root);
                    }
                }
                return Hit(field, t, p);
            }
            t += a;
        }
        return null;
    }

    /// <summary>A ray against a compiled part, searched as far as the ray can still meet the part's box.</summary>
    public static RayHit? Raycast(CompiledPart part, Point3D origin, Vector3D direction) =>
        Raycast(part.Field!, origin, direction, Reach(Box(part), origin));

    /// <summary>
    /// Certified distance from <paramref name="p"/> to the boundary, to within <paramref name="tolerance"/> where the
    /// search can tell. The upper end is the distance to a boundary point found as a root between points of opposite sign.
    /// The lower end starts at |f(p)|, which is already exact where the field is an exact distance (near a single face,
    /// inside convex regions). Elsewhere a branch-and-bound over boxes raises it: a box whose bounds exclude zero holds no
    /// boundary; any other box is no nearer than the box itself, nor than |h(p)| for h the field simplified for the box
    /// (h equals f in the box and is 1-Lipschitz everywhere, so no boundary point in the box is closer), nor than the
    /// convex or round shapes h's faces hold the box's solid in (the edges and corners of planes, the rim where a hole or
    /// boss meets a face, a fillet's rod); from inside, the shapes that hold the complement. Stops after
    /// <paramref name="budget"/> boxes; the interval stays honest.
    /// </summary>
    public static DistanceResult Distance(Field field, Point3D p, double tolerance = 1e-6, int budget = 20_000)
    {
        var v = field.Value(p);
        if (v == 0) return new(0, 0, p, field.Sample(p).Owner, false);
        var closest = Boundary(field, p);
        var upper = closest is { } c ? (c - p).Length : double.PositiveInfinity;
        // From inside, the boundary is that of the complement, whose faces are the field's negated.
        var inside = v < 0;
        var lower = Math.Max(Math.Abs(v), HullDistance(Conjuncts(inside ? FieldAlgebra.Normalize(field, negate: true) : field), p));
        // Search a cube round p that holds the ball of radius upper. Newton can circle between faces of a cut and find
        // nothing; then the cube grows until the boxes turn up boundary.
        var radius = closest is null ? 2 * Math.Abs(v) + tolerance : upper;
        while (upper - lower > tolerance && budget > 0)
        {
            var queue = new PriorityQueue<(Box3D Box, Field Field), double>();
            var root = Cube(p, radius);
            queue.Enqueue((root, field.Simplify(root)), lower);
            var settled = double.PositiveInfinity; // stays so if every box was ruled out
            while (queue.TryDequeue(out var item, out var low))
            {
                if (low >= upper - tolerance || HalfDiagonal(item.Box) < tolerance / 4 || budget-- <= 0)
                {
                    settled = low;
                    break;
                }
                if (Boundary(field, Clamp(p, item.Box), item.Field) is { } q && (q - p).Length < upper) (upper, closest) = ((q - p).Length, q);
                foreach (var child in Split(item.Box))
                {
                    if (!MayHoldBoundary(item.Field.Bounds(child))) continue;
                    var h = item.Field.Simplify(child);
                    var hull = HullDistance(Conjuncts(inside ? FieldAlgebra.Normalize(h, negate: true) : h, child), p);
                    var lc = Math.Max(Math.Max(lower, Distance(p, child)), Math.Max(Math.Abs(h.Value(p)), hull));
                    if (lc < upper) queue.Enqueue((child, h), lc);
                }
            }
            // Nothing in the cube is nearer than settled, nothing outside it nearer than its radius.
            lower = Math.Max(lower, Math.Min(settled, radius));
            if (upper <= radius) break;
            if (radius > 1e12) throw new InvalidOperationException("the field has no boundary near the point");
            radius = Math.Min(2 * radius, upper);
        }
        var point = closest ?? throw new InvalidOperationException("no boundary found near the point within the budget");
        return new(Math.Min(lower, upper), upper, point, field.Sample(point).Owner, inside);
    }

    /// <summary>
    /// True if the field provably keeps the sign <paramref name="side"/> all along the segment a→b: its bounds over the
    /// segment's box exclude zero, or, simplified for that box, its structure does. Planes are linear along a segment,
    /// a solid ball, rod or cone convex (below zero at both ends, below all along) and their complements concave; an
    /// intersection is negative where all its parts are, positive where one is, a union the other way round. The
    /// structure covers rays along a face that is not square to the axes, where the box bounds cannot tell.
    /// </summary>
    static bool Clear(Field field, Point3D a, Point3D b, int side)
    {
        var box = new Box3D(new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z)), new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z)));
        return Keeps(field.Bounds(box), side) || Keeps(field.Simplify(box), a, b, side, box);
    }

    static bool Keeps(Interval i, int side) => side < 0 ? i.Hi < 0 : i.Lo > 0;

    static bool Keeps(Field h, Point3D a, Point3D b, int side, Box3D box) => h switch
    {
        LeafField { Surface.Kind: SurfaceKind.Plane } l => side * l.Value(a) > 0 && side * l.Value(b) > 0,
        LeafField { Surface.Kind: SurfaceKind.Sphere or SurfaceKind.Cylinder or SurfaceKind.Cone } l when l.Complement == side > 0 =>
            side * l.Value(a) > 0 && side * l.Value(b) > 0,
        MaxField m => side < 0 ? m.Children.All(c => Keeps(c, a, b, side, box)) : m.Children.Any(c => Keeps(c, a, b, side, box)),
        MinField m => side < 0 ? m.Children.Any(c => Keeps(c, a, b, side, box)) : m.Children.All(c => Keeps(c, a, b, side, box)),
        NegateField n => Keeps(n.Child, a, b, -side, box),
        // Rounding only removes material: the field is never below either face.
        RoundedEdgeField r when side > 0 => Keeps(r.A, a, b, side, box) || Keeps(r.B, a, b, side, box),
        PlacedField p => Keeps(p.Child, p.Placement.ToLocal(a), p.Placement.ToLocal(b), side, p.LocalBox(box)),
        _ => Keeps(h.Bounds(box), side),
    };

    static RayHit Hit(Field field, double t, Point3D p)
    {
        var s = field.Sample(p);
        return new(t, p, s.Owner, s.Gradient.Normalized());
    }

    /// <summary>A boundary point on the segment a→b, whose ends have opposite signs: Illinois, as the mesher uses.</summary>
    static Point3D Root(Field field, Point3D a, Point3D b, double fa, double fb)
    {
        var dir = b - a;
        var length = dir.Length;
        double t0 = 0, t1 = 1;
        var side = 0;
        for (var it = 0; it < 100 && (t1 - t0) * length > 1e-14 && t1 - t0 > 1e-16; it++)
        {
            var t = (t0 * fb - t1 * fa) / (fb - fa);
            if (!(t > t0 && t < t1)) t = (t0 + t1) / 2;
            var ft = field.Value(a + dir * t);
            if (ft == 0) return a + dir * t;
            if (ft < 0 == fa < 0)
            {
                (t0, fa) = (t, ft);
                if (side == -1) fb /= 2;
                side = -1;
            }
            else
            {
                (t1, fb) = (t, ft);
                if (side == 1) fa /= 2;
                side = 1;
            }
        }
        return a + dir * ((t0 + t1) / 2);
    }

    /// <summary>
    /// A boundary point near <paramref name="start"/>: Newton steps along the gradient, each overshooting slightly, until
    /// the sign flips; then the root on the segment from the start. A root between opposite signs is certainly on the
    /// boundary, so its distance from the start is a valid upper bound. Null if the sign never flips. The steps may follow
    /// a smaller <paramref name="guide"/> that equals the field near the start; the signs are always the field's own.
    /// </summary>
    static Point3D? Boundary(Field field, Point3D start, Field? guide = null)
    {
        var v0 = field.Value(start);
        if (v0 == 0) return start;
        var side = Math.Sign(v0);
        var x = start;
        for (var it = 0; it < 64; it++)
        {
            var s = (guide ?? field).Sample(x);
            if (Math.Sign(s.Value) != side && guide is not null) s = s with { Value = field.Value(x) };
            if (Math.Sign(s.Value) != side) return s.Value == 0 ? x : Root(field, start, x, v0, s.Value);
            var g2 = s.Gradient.LengthSquared;
            if (g2 == 0) return null;
            x -= s.Gradient * ((s.Value + side * Overshoot) / g2);
        }
        return null;
    }

    // ---- Convex shapes a solid lies in ----

    /// <summary>
    /// Leaves the field is never below, so the solid lies inside each of them. Given the box the field was simplified
    /// for, a fillet between two planes whose sector holds the whole box adds its rod: there the solid is inside it.
    /// </summary>
    static List<LeafField> Conjuncts(Field f, Box3D? box = null)
    {
        var list = new List<LeafField>();
        void Collect(Field g)
        {
            switch (g)
            {
                case LeafField l: list.Add(l); break;
                case MaxField m: foreach (var c in m.Children) Collect(c); break;
                case RoundedEdgeField r:
                    list.Add(r.A);
                    list.Add(r.B);
                    if (box is { } b && Rod(r, b) is { } rod) list.Add(rod);
                    break;
                case ShellField s: Collect(s.Outer); break;
                // A placed part holds the same faces, moved: a rigid motion keeps planes, cylinders and spheres what they are.
                case PlacedField p:
                    foreach (var l in Conjuncts(p.Child, box is { } world ? p.LocalBox(world) : null))
                        list.Add(new LeafField(l.Surface with { Origin = p.Placement.ToWorld(l.Surface.Origin), Axis = p.Placement.ToWorld(l.Surface.Axis) }, l.Complement));
                    break;
                case MinField m: list.AddRange(SharedPlanes(m, box)); break;
            }
        }
        Collect(f);
        return list;
    }

    /// <summary>The solid cylinder a plane–plane fillet rounds the edge with, if the box lies inside the fillet's sector.</summary>
    static LeafField? Rod(RoundedEdgeField r, Box3D box)
    {
        if (r.A.Surface.Kind != SurfaceKind.Plane || r.B.Surface.Kind != SurfaceKind.Plane || r.SinglePatch(box) is null) return null;
        var (na, nb) = (r.A.Sample(box.Center).Gradient, r.B.Sample(box.Center).Gradient);
        var axis = na.Cross(nb).Normalized();
        // The point of the sharp edge nearest the box centre, then the ball centre that rolls along it.
        var lambda = Solve(new[,] { { 1, na.Dot(nb) }, { na.Dot(nb), 1 } }, [r.A.Value(box.Center), r.B.Value(box.Center)]);
        if (lambda is null) return null;
        var onEdge = box.Center - na * lambda[0] - nb * lambda[1];
        var centre = onEdge - (na + nb) * (r.Radius / (1 + na.Dot(nb)));
        return new LeafField(new Surface(-1, "", SurfaceKind.Cylinder, centre, axis, r.Radius));
    }

    /// <summary>
    /// Half-spaces that hold every part of a union, so the union too: for a normal each part has a plane with, the
    /// loosest of the parts' tightest offsets. A tab joined flush with a plate keeps their common top face this way.
    /// </summary>
    static IEnumerable<LeafField> SharedPlanes(MinField union, Box3D? box)
    {
        List<(Vector3D N, double C)>? shared = null;
        foreach (var child in union.Children)
        {
            // The part's tightest offset per normal: n·x ≤ c.
            var tightest = new List<(Vector3D N, double C)>();
            foreach (var l in Conjuncts(child, box).Where(l => l.Surface.Kind == SurfaceKind.Plane))
            {
                var n = l.Complement ? -l.Surface.Axis : l.Surface.Axis;
                var c = n.Dot(l.Surface.Origin.AsVector());
                var i = tightest.FindIndex(h => (h.N - n).Length < 1e-12);
                if (i < 0) tightest.Add((n, c));
                else tightest[i] = (n, Math.Min(tightest[i].C, c));
            }
            if (shared is null) shared = tightest;
            else
            {
                var next = new List<(Vector3D N, double C)>();
                foreach (var h in shared)
                    if (tightest.FindIndex(t => (t.N - h.N).Length < 1e-12) is var i and >= 0) next.Add((h.N, Math.Max(h.C, tightest[i].C)));
                shared = next;
            }
            if (shared.Count == 0) return [];
        }
        return shared!.Select(h => new LeafField(new Surface(-1, "", SurfaceKind.Plane, new Point3D(h.N.X * h.C, h.N.Y * h.C, h.N.Z * h.C), h.N)));
    }

    /// <summary>A lower bound of the distance from p to a solid that lies in each of the conjuncts.</summary>
    static double HullDistance(List<LeafField> conjuncts, Point3D p) => Math.Max(PolyhedronDistance(conjuncts, p), RimDistance(conjuncts, p));

    /// <summary>
    /// A cylinder and the planes square to its axis hold the solid in a shape round that axis, so its distance from p is
    /// the 2D distance in p's half-plane through the axis: √(radial² + axial²) of p's distances outside each.
    /// </summary>
    static double RimDistance(List<LeafField> conjuncts, Point3D p)
    {
        var best = 0.0;
        foreach (var c in conjuncts.Where(l => l.Surface.Kind == SurfaceKind.Cylinder))
        {
            var radial = c.Value(p);
            if (radial <= 0) continue;
            var axial = conjuncts.Where(l => l.Surface.Kind == SurfaceKind.Plane && l.Surface.Axis.Cross(c.Surface.Axis).Length < 1e-12)
                .Select(l => l.Value(p)).DefaultIfEmpty(0).Max();
            if (axial > 0) best = Math.Max(best, Math.Sqrt(radial * radial + axial * axial));
        }
        return best;
    }

    /// <summary>
    /// Lower bound of the distance from p to the intersection of the plane conjuncts. Up to three are used, the ones p
    /// lies farthest outside; fewer half-spaces hold more, so the bound stays valid.
    /// </summary>
    static double PolyhedronDistance(List<LeafField> conjuncts, Point3D p)
    {
        var planes = conjuncts.Where(l => l.Surface.Kind == SurfaceKind.Plane)
            .Select(l => (N: l.Complement ? -l.Surface.Axis : l.Surface.Axis, Value: l.Value(p)))
            .Where(h => h.Value > 0).OrderByDescending(h => h.Value).Take(3).ToArray();
        return planes.Length == 0 ? 0 : ConvexDistance(planes);
    }

    /// <summary>
    /// Distance from p to the intersection of half-spaces with unit normals nᵢ, given p's height vᵢ &gt; 0 above each:
    /// the projection onto the face, edge or corner whose multipliers are all non-negative and that lies inside the rest
    /// (the KKT point, unique). Falls back to the largest height, the distance to one half-space.
    /// </summary>
    static double ConvexDistance((Vector3D N, double Value)[] planes)
    {
        var fallback = planes.Max(h => h.Value);
        var m = planes.Length;
        for (var set = 1; set < 1 << m; set++)
        {
            var idx = Enumerable.Range(0, m).Where(i => (set >> i & 1) == 1).ToArray();
            var k = idx.Length;
            // x = p − Σ λⱼ nⱼ on every plane of the set: Σ λⱼ (nᵢ·nⱼ) = vᵢ.
            var g = new double[k, k];
            var r = new double[k];
            for (var i = 0; i < k; i++)
            {
                r[i] = planes[idx[i]].Value;
                for (var j = 0; j < k; j++) g[i, j] = planes[idx[i]].N.Dot(planes[idx[j]].N);
            }
            if (Solve(g, r) is not { } lambda || lambda.Any(l => l < -1e-12)) continue;
            var step = new Vector3D(0, 0, 0);
            for (var i = 0; i < k; i++) step += planes[idx[i]].N * lambda[i];
            // Height of x above the others: vᵢ − nᵢ·step.
            if (planes.Where((_, i) => (set >> i & 1) == 0).All(h => h.Value - h.N.Dot(step) <= 1e-12)) return Math.Max(step.Length, fallback);
        }
        return fallback;
    }

    // Cramer's rule for up to three unknowns; null when the planes are (nearly) parallel.
    static double[]? Solve(double[,] a, double[] b)
    {
        var n = b.Length;
        double Det(double[,] m) => n switch
        {
            1 => m[0, 0],
            2 => m[0, 0] * m[1, 1] - m[0, 1] * m[1, 0],
            _ => m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1]) - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0]) + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]),
        };
        var d = Det(a);
        if (Math.Abs(d) < 1e-12) return null;
        var x = new double[n];
        for (var c = 0; c < n; c++)
        {
            var m = (double[,])a.Clone();
            for (var r = 0; r < n; r++) m[r, c] = b[r];
            x[c] = Det(m) / d;
        }
        return x;
    }

    // ---- Boxes ----

    static Box3D Box(CompiledPart part)
    {
        // Grown a little, so faces on the box are strictly inside it.
        var m = 1e-6 * (1 + (part.Max - part.Min).Length);
        return new(part.Min - new Vector3D(m, m, m), part.Max + new Vector3D(m, m, m));
    }

    // Beyond the farthest corner of the box a ray can no longer meet the solid.
    static double Reach(Box3D box, Point3D from)
    {
        var far = 0.0;
        foreach (var x in new[] { box.Min.X, box.Max.X })
        foreach (var y in new[] { box.Min.Y, box.Max.Y })
        foreach (var z in new[] { box.Min.Z, box.Max.Z })
            far = Math.Max(far, (new Point3D(x, y, z) - from).Length);
        return far;
    }

    static Box3D Cube(Point3D c, double r) => new(new Point3D(c.X - r, c.Y - r, c.Z - r), new Point3D(c.X + r, c.Y + r, c.Z + r));

    static double HalfDiagonal(Box3D b) => (b.Max - b.Min).Length / 2;

    static bool MayHoldBoundary(Interval i) => i.Lo <= 0 && i.Hi >= 0;

    static Point3D Clamp(Point3D p, Box3D b) =>
        new(Math.Clamp(p.X, b.Min.X, b.Max.X), Math.Clamp(p.Y, b.Min.Y, b.Max.Y), Math.Clamp(p.Z, b.Min.Z, b.Max.Z));

    static double Distance(Point3D p, Box3D b) => (Clamp(p, b) - p).Length;

    static double Distance(Box3D a, Box3D b)
    {
        static double Gap(double aMin, double aMax, double bMin, double bMax) => Math.Max(0, Math.Max(aMin - bMax, bMin - aMax));
        var (x, y, z) = (Gap(a.Min.X, a.Max.X, b.Min.X, b.Max.X), Gap(a.Min.Y, a.Max.Y, b.Min.Y, b.Max.Y), Gap(a.Min.Z, a.Max.Z, b.Min.Z, b.Max.Z));
        return Math.Sqrt(x * x + y * y + z * z);
    }

    internal static Box3D[] Split(Box3D b)
    {
        var c = b.Center;
        var children = new Box3D[8];
        for (var i = 0; i < 8; i++)
            children[i] = new Box3D(
                new Point3D((i & 1) == 0 ? b.Min.X : c.X, (i & 2) == 0 ? b.Min.Y : c.Y, (i & 4) == 0 ? b.Min.Z : c.Z),
                new Point3D((i & 1) == 0 ? c.X : b.Max.X, (i & 2) == 0 ? c.Y : b.Max.Y, (i & 4) == 0 ? c.Z : b.Max.Z));
        return children;
    }
}
