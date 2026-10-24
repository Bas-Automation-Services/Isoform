using BasAutomation.Geometry;
using Isoform.Geometry;

namespace Isoform.Modeling;

/// <summary>
/// The largest value of a field over a region (a solid, or the faces of a solid's boundary), certified: the maximum
/// lies in [<see cref="Lower"/>, <see cref="Upper"/>]. <see cref="At"/> is a point of the region where the field is
/// <see cref="Lower"/>; <see cref="RegionOwner"/> is the region's face there (the solid's owner), <see cref="FieldOwner"/>
/// the field's. <see cref="Settled"/> when Upper − Lower is within the tolerance asked for; false when the budget ran out
/// first, and the interval is honest either way. An empty region has −∞ at both ends.
/// </summary>
public readonly record struct ExcessResult(double Lower, double Upper, Point3D At, int RegionOwner, int FieldOwner, bool Settled);

public static partial class Queries
{
    /// <summary>
    /// The largest value of <paramref name="field"/> over the solid of <paramref name="solid"/> within
    /// <paramref name="box"/>: with the parts as the solid and the design as the field, how far the parts stray outside
    /// the design (0 or less when they lie inside it, the depth of the shallowest part point then).
    /// <para>
    /// Branch and bound over boxes, highest first. A box holds none of the solid when the solid's bounds there are above
    /// zero. Otherwise the field is no higher there than its bounds over the box, nor than its structure allows inside
    /// the convex shapes the solid's faces hold it in (<see cref="High"/>): a face of the field parallel to a face of
    /// the solid, a cylinder coaxial with a rod of it, a sphere round a ball of it. Those make a face lying a constant
    /// distance from the other (a tube in a larger bore, a plate on a face) exact in one box, where boxes alone could
    /// only close on it cell by cell. The lower end is the field at points of the solid, each climbed from a box
    /// centre up the field's gradient and back into the solid. Stops after <paramref name="budget"/> boxes.
    /// </para>
    /// </summary>
    public static ExcessResult Excess(Field field, Field solid, Box3D box, double tolerance = 1e-3, int budget = 100_000) =>
        Search(field, solid, box, tolerance, budget,
            (b, s) => Enclose(s, b).Lo > 0 ? null : Conjuncts(s, b),
            p => Into(solid, p));

    /// <summary>
    /// The largest value of <paramref name="field"/> over the boundary points of <paramref name="solid"/> owned by one
    /// of <paramref name="faces"/>: with the design as the solid and the parts as the field, how far that design face
    /// is from the parts where they leave it uncovered (negative: covered that deep). It is the parts' field, so a
    /// lower bound of the distance to them, exact near a single face. As <see cref="Excess(Field, Field, Box3D, double, int)"/>,
    /// over boxes where the face may own a boundary point (<see cref="Owned"/>: which child of an intersection, union or
    /// blend can decide there, ties going to the first). The face's points lie inside the solid's convex shapes, on
    /// the face's own surface where it has one, and on a smooth union's face both sides are between 0 and k: a leg's
    /// blend never runs farther than k from the leg. Witnesses are boundary points the face owns, found by Newton onto
    /// its surface (on the boundary to 1e-9).
    /// </summary>
    public static ExcessResult BoundaryExcess(Field field, Field solid, Box3D box, IReadOnlySet<int> faces, double tolerance = 1e-3, int budget = 100_000) =>
        Search(field, solid, box, tolerance, budget,
            (b, s) =>
            {
                if (Owned(s, b, faces) is not { } owned || !MayHoldBoundary(owned)) return null;
                var holds = Conjuncts(s, b);
                FaceLeaves(s, faces, holds);
                return holds;
            },
            p => OnFace(solid, faces, p));

    /// <summary>
    /// <see cref="BoundaryExcess"/> per face name of <paramref name="surfaces"/> (pattern copies are faces of their own),
    /// faces searched in parallel; a face with no boundary in the box is left out.
    /// </summary>
    public static IReadOnlyList<(string Face, ExcessResult Excess)> ExcessByFace(Field field, Field solid, Box3D box, IReadOnlyList<Surface> surfaces,
        double tolerance = 1e-3, int budget = 100_000)
    {
        var faces = surfaces.GroupBy(s => s.Source).Select(g => (Face: g.Key, Owners: (IReadOnlySet<int>)g.Select(s => s.Id).ToHashSet())).ToArray();
        var results = new ExcessResult[faces.Length];
        Parallel.For(0, faces.Length, i => results[i] = BoundaryExcess(field, solid, box, faces[i].Owners, tolerance, budget));
        return faces.Select((f, i) => (f.Face, results[i])).Where(r => !double.IsNegativeInfinity(r.Item2.Upper)).OrderBy(r => r.Face, StringComparer.Ordinal).ToList();
    }

    /// <summary>How far <paramref name="parts"/> stray outside <paramref name="design"/>.</summary>
    public static ExcessResult Excess(CompiledPart design, CompiledPart parts, double tolerance = 1e-3) =>
        Excess(design.Field!, parts.Field!, Box(parts), tolerance);

    /// <summary>How far each face of <paramref name="design"/> is from <paramref name="parts"/>, where they leave it uncovered.</summary>
    public static IReadOnlyList<(string Face, ExcessResult Excess)> ExcessByFace(CompiledPart parts, CompiledPart design, double tolerance = 1e-3) =>
        ExcessByFace(parts.Field!, design.Field!, Box(design), design.Surfaces, tolerance);

    static ExcessResult Search(Field field, Field solid, Box3D box, double tolerance, int budget,
        Func<Box3D, Field, List<LeafField>?> region, Func<Point3D, Point3D?> onto)
    {
        var (best, at) = (double.NegativeInfinity, box.Center);
        var pruned = double.NegativeInfinity; // highest bound of a box set aside
        var top = double.NegativeInfinity; // highest bound of the boxes left when the search stopped
        var queue = new PriorityQueue<(Box3D Box, Field F, Field S), double>();
        void Push(Box3D b, Field f, Field s)
        {
            if (region(b, s) is not { } holds) return;
            var hi = High(f, b, holds);
            if (hi <= best + tolerance) pruned = Math.Max(pruned, hi);
            else queue.Enqueue((b, f, s), -hi);
        }
        Push(box, field.Simplify(box), solid.Simplify(box));
        while (queue.TryDequeue(out var item, out var priority))
        {
            var hi = -priority;
            if (hi <= best + tolerance)
            {
                pruned = Math.Max(pruned, hi); // the rest are no higher
                break;
            }
            if (budget-- <= 0 || HalfDiagonal(item.Box) < tolerance / 4)
            {
                top = hi;
                break;
            }
            // A sample that beats the best is climbed from; most do not, and cost one projection.
            if (onto(item.Box.Center) is { } start && field.Value(start) > best)
            {
                var (x, v) = Climb(field, onto, start, HalfDiagonal(item.Box), tolerance);
                (best, at) = (v, x);
            }
            foreach (var child in Split(item.Box)) Push(child, item.F.Simplify(child), item.S.Simplify(child));
        }
        var upper = Math.Max(best, Math.Max(pruned, top));
        var settled = upper == best || upper - best <= tolerance;
        return new(best, upper, at, solid.Sample(at).Owner, field.Sample(at).Owner, settled);
    }

    /// <summary>
    /// Projected gradient ascent: a step up the field's gradient, then back into the region; kept if the field rose, else
    /// halved, until the step is below the tolerance. Only a better witness; the bound never rests on it.
    /// </summary>
    static (Point3D X, double V) Climb(Field field, Func<Point3D, Point3D?> onto, Point3D x, double step, double tolerance)
    {
        var v = field.Value(x);
        for (var it = 0; it < 48 && step > tolerance / 8; it++)
        {
            var g = field.Sample(x).Gradient;
            if (g.LengthSquared > 0 && onto(x + g.Normalized() * step) is { } y && field.Value(y) is var w && w > v) (x, v) = (y, w);
            else step /= 2;
        }
        return (x, v);
    }

    /// <summary>A point of the solid near p: p itself, or the boundary point Newton finds, nudged in until the field says it is inside.</summary>
    static Point3D? Into(Field solid, Point3D p)
    {
        if (solid.Value(p) <= 0) return p;
        if (Boundary(solid, p) is not { } q) return null;
        for (var i = 0; i < 4; i++)
        {
            var s = solid.Sample(q);
            if (s.Value <= 0) return q;
            q -= s.Gradient * (2 * s.Value + 1e-12);
        }
        return null;
    }

    /// <summary>A boundary point near p owned by one of the faces: Newton onto each face's own surface, kept if the solid agrees.</summary>
    static Point3D? OnFace(Field solid, IReadOnlySet<int> faces, Point3D p)
    {
        foreach (var o in faces)
        {
            if (solid.SampleSurface(o, p) is null) continue;
            var q = Onto(solid, o, p);
            var s = solid.Sample(q);
            if (Math.Abs(s.Value) <= 1e-9 && faces.Contains(s.Owner)) return q;
        }
        return null;
    }

    /// <summary>
    /// An enclosure of f over the points of the box that one of the faces owns; null if no point there can be theirs.
    /// A child of an intersection owns only where every other child is at or below it, of a union at or above it; a
    /// side of a smooth union only where it is k below the other (elsewhere the blend face owns), and the blend face
    /// only where the two are within k, which sinks it by h²k/4 at least.
    /// </summary>
    static Interval? Owned(Field f, Box3D box, IReadOnlySet<int> faces)
    {
        static Interval? Hull(Interval? a, Interval b) => a is { } x ? new(Math.Min(x.Lo, b.Lo), Math.Max(x.Hi, b.Hi)) : b;
        static Interval? Valid(double lo, double hi) => lo <= hi ? new Interval(lo, hi) : null;
        switch (f)
        {
            case LeafField l:
                return faces.Contains(l.Surface.Id) ? l.Bounds(box) : null;
            case MaxField or MinField:
            {
                var children = f is MaxField max ? max.Children : ((MinField)f).Children;
                var bounds = children.Select(c => Enclose(c, box)).ToArray();
                Interval? result = null;
                for (var i = 0; i < children.Count; i++)
                {
                    // Ties go to the first child: one never above (below) an earlier one owns nothing.
                    if (Enumerable.Range(0, i).Any(j => f is MaxField ? Dominates(children[j], children[i]) : Dominates(children[i], children[j]))) continue;
                    if (Owned(children[i], box, Unsunk(f is MaxField, children, i, faces)) is not { } o) continue;
                    var (lo, hi) = (o.Lo, o.Hi);
                    for (var j = 0; j < children.Count; j++)
                        if (j != i)
                            (lo, hi) = f is MaxField ? (Math.Max(lo, bounds[j].Lo), hi) : (lo, Math.Min(hi, bounds[j].Hi));
                    if (Valid(lo, hi) is { } v) result = Hull(result, v);
                }
                return result;
            }
            case BlendField b:
            {
                var (ia, ib) = (Enclose(b.A, box), Enclose(b.B, box));
                Interval? result = null;
                if (faces.Contains(b.Blend.Id) && Math.Max(ia.Lo - ib.Hi, ib.Lo - ia.Hi) < b.K) result = Enclose(b, box);
                foreach (var (side, other) in new[] { (b.A, ib), (b.B, ia) })
                    if (Owned(side, box, faces) is { } o
                        && (b.Union ? Valid(o.Lo, Math.Min(o.Hi, other.Hi - b.K)) : Valid(Math.Max(o.Lo, other.Lo + b.K), o.Hi)) is { } v)
                        result = Hull(result, v);
                return result;
            }
            case NegateField n:
                return Owned(n.Child, box, faces)?.Negate();
            case PlacedField p:
                return Owned(p.Child, p.LocalBox(box), faces);
            default:
                return faces.Any(o => f.SampleSurface(o, box.Center) is not null) ? f.Bounds(box) : null;
        }
    }

    /// <summary>
    /// The leaves of the faces in the field: a boundary point a leaf owns lies on its surface, so both the leaf and its
    /// complement hold it. Only through nodes that hand on a child's sample unchanged where it owns the point.
    /// </summary>
    static void FaceLeaves(Field f, IReadOnlySet<int> faces, List<LeafField> holds)
    {
        switch (f)
        {
            case LeafField l when faces.Contains(l.Surface.Id):
                holds.Add(l);
                holds.Add(new LeafField(l.Surface, !l.Complement));
                break;
            case MaxField m: foreach (var c in m.Children) FaceLeaves(c, faces, holds); break;
            case MinField m: foreach (var c in m.Children) FaceLeaves(c, faces, holds); break;
            case NegateField n: FaceLeaves(n.Child, faces, holds); break;
            case BlendField b:
                if (faces.Contains(b.Blend.Id))
                    foreach (var side in new[] { b.A, b.B })
                    {
                        // On a smooth union's face both sides lie between 0 and k (the min sinks by h²k/4 where they
                        // differ by less than k), on a smooth intersection's between −k and 0.
                        foreach (var l in Conjuncts(side))
                            if (!b.Union) holds.Add(l);
                            else if (!l.Complement || l.Surface.Kind == SurfaceKind.Plane) // a hole's wall shrunk by k may vanish
                                holds.Add(new LeafField(l.Surface.Offset(l.Surface.Id, l.Surface.Source, l.Complement ? -b.K : b.K), l.Complement));
                        if (b.Union && side is LeafField leaf) holds.Add(new LeafField(leaf.Surface, !leaf.Complement));
                    }
                FaceLeaves(b.A, faces, holds);
                FaceLeaves(b.B, faces, holds);
                break;
            case ProfileField p: foreach (var l in p.Leaves) FaceLeaves(l, faces, holds); break;
            case PlacedField p:
                var local = new List<LeafField>();
                FaceLeaves(p.Child, faces, local);
                holds.AddRange(local.Select(l => Moved(l, p.Placement, toWorld: true)));
                break;
        }
    }

    /// <summary>
    /// An upper bound of f over the points of the box inside every one of <paramref name="holds"/>. An intersection
    /// is no higher than its highest part, a union (smooth or not: the smooth min never exceeds the min) than its lowest,
    /// a smooth intersection than its highest part plus k/4. An extruded profile without convex corners is never above
    /// its highest edge's surface (inside, the nearest edge is no nearer than its surface; outside, the nearest point is
    /// inside an edge or at a tangent junction, where the surface is exact). A placed field takes the holds along.
    /// </summary>
    static double High(Field f, Box3D box, List<LeafField> holds) => f switch
    {
        LeafField l => Math.Min(l.Bounds(box).Hi, LeafHigh(l, box, holds)),
        MaxField m => m.Children.Max(c => High(c, box, holds)),
        MinField m => m.Children.Min(c => High(c, box, holds)),
        BlendField { Union: true } b => Math.Min(High(b.A, box, holds), High(b.B, box, holds)) - Sink(b, box),
        BlendField b => Math.Min(Enclose(b, box).Hi, Math.Max(High(b.A, box, holds), High(b.B, box, holds)) + b.K / 4),
        ProfileField p when !p.Revolved && p.Grown == 0 && Enumerable.Range(0, p.Edges.Count).All(k => p.Turn(k) <= 0) =>
            Math.Min(p.Bounds(box).Hi, p.Leaves.Max(l => High(l, box, holds))),
        PlacedField p => High(p.Child, p.LocalBox(box), holds.Select(l => Moved(l, p.Placement, toWorld: false)).ToList()),
        _ => f.Bounds(box).Hi,
    };

    /// <summary>
    /// Bounds of the field over the box, as <see cref="Field.Bounds"/> but closer round a smooth blend: the quadratic
    /// smooth min is min(a, b) − h²k/4 with h = (k − |a − b|)/k, so it sinks by no more than |a − b| at its least
    /// allows and by no less than at its most. Near the blend's face that is first-order tight, where the plain
    /// bounds stay k/4 wide however small the box.
    /// </summary>
    static Interval Enclose(Field f, Box3D box)
    {
        switch (f)
        {
            case MaxField m:
            {
                var r = Enclose(m.Children[0], box);
                for (var i = 1; i < m.Children.Count; i++) r = Interval.Max(r, Enclose(m.Children[i], box));
                return r;
            }
            case MinField m:
            {
                var r = Enclose(m.Children[0], box);
                for (var i = 1; i < m.Children.Count; i++) r = Interval.Min(r, Enclose(m.Children[i], box));
                return r;
            }
            case NegateField n:
                return Enclose(n.Child, box).Negate();
            case PlacedField p:
                return Enclose(p.Child, p.LocalBox(box));
            case BlendField b:
            {
                var (ia, ib) = (Enclose(b.A, box), Enclose(b.B, box));
                var plain = b.Union ? Interval.Min(ia, ib) : Interval.Max(ia, ib);
                var near = Math.Max(0, Math.Max(ia.Lo - ib.Hi, ib.Lo - ia.Hi)); // |a − b| at its least
                var most = Math.Max(0, b.K - near) / b.K;
                var sink = (most * most * b.K / 4, Sink(b, box));
                return b.Union ? new(plain.Lo - sink.Item1, plain.Hi - sink.Item2) : new(plain.Lo + sink.Item2, plain.Hi + sink.Item1);
            }
            default:
                return f.Bounds(box);
        }
    }

    /// <summary>The least a smooth blend sinks below the plain min (rises above the max) anywhere in the box: h²k/4 at |a − b|'s most.</summary>
    static double Sink(BlendField b, Box3D box)
    {
        var (ia, ib) = (Enclose(b.A, box), Enclose(b.B, box));
        var least = Math.Max(0, b.K - Math.Max(ia.Hi - ib.Lo, ib.Hi - ia.Lo)) / b.K;
        return least * least * b.K / 4;
    }

    /// <summary>
    /// The faces less a blend face that child i (a smooth union in an intersection, or the other way round) can never
    /// show: where the blend face owns it lies strictly below both its sides, so a sibling never below one side is
    /// above it there. A plane cut flush with a face that a blend runs into: the blend's rim only touches the cut.
    /// </summary>
    static IReadOnlySet<int> Unsunk(bool intersection, IReadOnlyList<Field> children, int i, IReadOnlySet<int> faces)
    {
        if (children[i] is not BlendField b || b.Union != intersection || !faces.Contains(b.Blend.Id)) return faces;
        var shown = Enumerable.Range(0, children.Count).Where(j => j != i).Any(j => intersection
            ? Dominates(children[j], b.A) || Dominates(children[j], b.B)
            : Dominates(b.A, children[j]) || Dominates(b.B, children[j]));
        return shown ? faces.Where(o => o != b.Blend.Id).ToHashSet() : faces;
    }

    /// <summary>
    /// True if c is never below d: the same leaf, or an intersection with a part that is never below d. Settles ties
    /// between coplanar faces, a plane cut flush with a face.
    /// </summary>
    static bool Dominates(Field c, Field d) => ReferenceEquals(c, d) || c switch
    {
        LeafField l => d is LeafField e && l.Complement == e.Complement && l.Surface.SameGeometry(e.Surface, 0),
        MaxField m => m.Children.Any(x => Dominates(x, d)),
        MinField m => m.Children.All(x => Dominates(x, d)),
        _ => false,
    };

    /// <summary>
    /// An upper bound of one surface's field over the box inside the holds. Any 1-Lipschitz field is within r of its
    /// value at a ball's centre. A plane is a linear function: its highest point over the holds' planes, balls and rods
    /// (<see cref="Highest"/>). A cylinder is no farther from its axis than a rod of radius R round another axis, plus
    /// the farthest the other axis comes from it along the box: R − r when coaxial; a hole's wall, the other way round.
    /// A spherical hole likewise round a spherical hole of the holds.
    /// </summary>
    static double LeafHigh(LeafField l, Box3D box, List<LeafField> holds)
    {
        var s = l.Surface;
        var best = double.PositiveInfinity;
        foreach (var h in holds)
        {
            var t = h.Surface;
            if (t.Kind == SurfaceKind.Sphere && !h.Complement) best = Math.Min(best, l.Value(t.Origin) + t.Radius);
            if (t.Kind != s.Kind || h.Complement != l.Complement) continue;
            if (s.Kind == SurfaceKind.Cylinder)
                best = Math.Min(best, l.Complement ? s.Radius - t.Radius + AxisGap(box, s, t) : t.Radius - s.Radius + AxisGap(box, t, s));
            else if (s.Kind == SurfaceKind.Sphere && l.Complement)
                best = Math.Min(best, s.Radius - t.Radius + (s.Origin - t.Origin).Length);
        }
        if (s.Kind == SurfaceKind.Plane)
        {
            var n = l.Complement ? -s.Axis : s.Axis;
            best = Math.Min(best, Highest(box, holds, n) - n.Dot(s.Origin.AsVector()));
        }
        return best;
    }

    /// <summary>
    /// The farthest the axis of <paramref name="along"/>, over the stretch the box projects onto, comes from the axis of
    /// <paramref name="from"/>: the distance between two lines is convex along either, so its ends decide.
    /// </summary>
    static double AxisGap(Box3D box, Surface along, Surface from)
    {
        var (a, o) = (along.Axis, along.Origin.AsVector());
        var (t0, t1) = (-Support(box, -a) - a.Dot(o), Support(box, a) - a.Dot(o));
        return Math.Max(FromAxis(along.Origin + a * t0, from), FromAxis(along.Origin + a * t1, from));
    }

    static LeafField Moved(LeafField l, Frame f, bool toWorld) => new(l.Surface with
    {
        Origin = toWorld ? f.ToWorld(l.Surface.Origin) : f.ToLocal(l.Surface.Origin),
        Axis = toWorld ? f.ToWorld(l.Surface.Axis) : f.ToLocal(l.Surface.Axis),
    }, l.Complement);
}
