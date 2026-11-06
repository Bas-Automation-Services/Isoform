using BasAutomation.Geometry;

namespace Isoform.Brep;

public enum BooleanKind { Union, Difference }

/// <summary>A result body with where each of its faces came from (several origins for faces merged on one surface).</summary>
public sealed record Shaped(Body Body, IReadOnlyList<IReadOnlyList<Origin>> Origins);

/// <summary>
/// Booleans of two solids bounded by planes and cylinders. Each face of one solid is intersected with each face of
/// the other: curves where their surfaces cross (in closed form), and where they lie on one surface, each other's
/// edges. Edges are split where the pieces of those curves end, and each face is cut along them in its own
/// (u, v). Every piece is located against the other solid at a point well inside it (out, in, or on its boundary
/// pointing the same way or the other), the pieces the operation keeps are merged where they lie on one surface,
/// and sewn into solids by their edges' geometry. Anything outside that (an ellipse, a tangency, two cylinders at an
/// angle) is refused with the reason.
/// </summary>
public static class Booleans
{
    /// <summary>The union of two solids: one body, or two when they do not meet.</summary>
    public static (IReadOnlyList<Shaped>? Bodies, string? Problem) Unite(Body a, Body b) => Apply(a, b, BooleanKind.Union);

    /// <summary><paramref name="a"/> less <paramref name="b"/>: no bodies, one, or several where the cut parts it.</summary>
    public static (IReadOnlyList<Shaped>? Bodies, string? Problem) Subtract(Body a, Body b) => Apply(a, b, BooleanKind.Difference);

    /// <summary>
    /// The body with adjacent faces on one surface merged into one face and edges that continue each other through a
    /// vertex of only those two edges joined: the form Isoform's topology names.
    /// </summary>
    public static Shaped Merge(Body body)
    {
        var patches = Enumerable.Range(0, body.Faces.Length).Select(f => PatchOf(body, new FaceId(f), 0)).ToList();
        return Sewing.Sew(Sewing.MergeSurfaces(patches), body.Tolerance) switch
        {
            [var one] => one,
            var many => throw new BooleanFailure($"merging faces made {many.Count} bodies of one"),
        };
    }

    /// <summary>
    /// The tools in groups whose boxes do not touch (each tool in the first group it fits), so that each group can be
    /// applied in one boolean as <see cref="Lumps"/>: taking away or adding solids that do not touch is the same
    /// in any order.
    /// </summary>
    public static List<List<int>> Apart(IReadOnlyList<Body> tools)
    {
        var boxes = tools.Select(t => new Solid(t).Box).ToList();
        static bool Touch((Point3D Min, Point3D Max) a, (Point3D Min, Point3D Max) b) =>
            a.Min.X <= b.Max.X && b.Min.X <= a.Max.X && a.Min.Y <= b.Max.Y && b.Min.Y <= a.Max.Y && a.Min.Z <= b.Max.Z && b.Min.Z <= a.Max.Z;
        var groups = new List<List<int>>();
        for (var i = 0; i < tools.Count; i++)
        {
            if (groups.FirstOrDefault(g => g.All(j => !Touch(boxes[i], boxes[j]))) is { } fits) fits.Add(i);
            else groups.Add([i]);
        }
        return groups;
    }

    /// <summary>
    /// Solids that do not touch as one body, the second operand of a boolean that applies them all at once. Not a
    /// valid body (each shell bounds a solid, none a void), but its faces tell inside from outside as the solids
    /// together do. Face k of the i-th solid is face <c>Offsets[i] + k</c>.
    /// </summary>
    public static (Body Body, int[] Offsets) Lumps(IReadOnlyList<Body> solids)
    {
        var builder = new BodyBuilder(solids.Max(s => s.Tolerance));
        var offsets = new int[solids.Count];
        var faces = 0;
        for (var i = 0; i < solids.Count; i++)
        {
            var body = solids[i];
            offsets[i] = faces;
            var vertices = body.Vertices.Select(v => builder.AddVertex(v.Point)).ToList();
            var edges = body.Edges.Select(e => builder.AddEdge(e.Curve, e.T0, e.T1, vertices[e.Start.Index], vertices[e.End.Index])).ToList();
            var shells = body.Shells.Select(_ => builder.AddShell()).ToList();
            foreach (var f in body.Faces)
            {
                var face = builder.AddFace(shells[f.Shell.Index], f.Surface, f.SameSense);
                foreach (var loop in f.Loops)
                    builder.AddLoop(face, [.. body.CoedgesOf(loop).Select(c => new BodyBuilder.Use(edges[body[c].Edge.Index], body[c].Forward, body[c].Pcurve))]);
            }
            faces += body.Faces.Length;
        }
        return (builder.Build(), offsets);
    }

    public static (IReadOnlyList<Shaped>? Bodies, string? Problem) Apply(Body a, Body b, BooleanKind kind)
    {
        try
        {
            var ops = new[] { new Operand(a, 0), new Operand(b, 1) };
            for (var fa = 0; fa < a.Faces.Length; fa++)
            for (var fb = 0; fb < b.Faces.Length; fb++)
            {
                var (x, y) = (ops[0].Solid.Faces[fa], ops[1].Solid.Faces[fb]);
                if (!x.Overlaps(y.Box)) continue;
                Intersect(ops, fa, fb);
            }

            // The first operand's faces that come through whole and miss the other solid's box are kept as they are: the
            // result is an edit of that operand where it can be (Sewing.Edit), else everything is sewn.
            var kept = new List<Patch>();
            var whole = new HashSet<int>();
            for (var f = 0; f < a.Faces.Length; f++)
                if (ops[0].Whole(f) && !ops[0].Solid.Faces[f].Overlaps(ops[1].Solid.Box)) whole.Add(f);
            for (var o = 0; o < 2; o++)
            {
                var other = ops[1 - o].Solid;
                foreach (var (patch, face, entire) in ops[o].Patches(o == 0 ? whole : []))
                {
                    // A piece of a face whose box misses the other solid's lies outside it.
                    var flat = ops[o].Solid.Faces[face];
                    var where = flat.Overlaps(other.Box) ? Locate(patch, other, entire ? flat : null) : Containment.Out;
                    var keep = (kind, o, where) switch
                    {
                        (BooleanKind.Union, 0, Containment.Out or Containment.OnSame) => patch,
                        (BooleanKind.Union, 1, Containment.Out) => patch,
                        (BooleanKind.Difference, 0, Containment.Out or Containment.OnOpposite) => patch,
                        (BooleanKind.Difference, 1, Containment.In) => patch.Flipped(),
                        _ => null,
                    };
                    if (keep is not null) kept.Add(keep);
                }
            }
            var tolerance = Math.Max(a.Tolerance, b.Tolerance);
            if (whole.Count > 0 && Sewing.Edit(ops[0].Solid, whole, kept, tolerance) is { } edited) return ([edited], null);
            kept = [.. kept.Select(p => p.Expanded()), .. whole.Select(f => PatchOf(a, new FaceId(f), 0) with { Unchanged = true })];
            return (Sewing.Sew(Sewing.MergeSurfaces(kept), tolerance), null);
        }
        catch (BooleanFailure failure)
        {
            return (null, failure.Message);
        }
    }

    // Where a piece lies against the other solid, at a point well inside it. A piece that is a whole face (split at
    // most) is that face's points: no cut crosses it, so it lies wholly outside the other solid where a corner of it
    // lies outside that solid's box, and is otherwise found inside its flat loops as they were.
    static Containment Locate(Patch patch, Solid other, FlatFace? entire)
    {
        if (entire is not null)
        {
            var (lo, hi) = other.Box;
            if (patch.Loops.SelectMany(l => l).Any(u => u.Start is var p && (p.X < lo.X || p.Y < lo.Y || p.Z < lo.Z || p.X > hi.X || p.Y > hi.Y || p.Z > hi.Z)))
                return Containment.Out;
            var inside = Arrangement.Inside(patch.Surface, [.. entire.Loops.SelectMany(l => l.Segments)]);
            return other.Classify(patch.Surface.At(inside.X, inside.Y), entire.Normal(patch.Surface.At(inside.X, inside.Y)));
        }
        var uv = Arrangement.Inside(patch.Surface, patch.UvLoops);
        // Away from the carried holes too (a flat face's: (u, v) is flat there); else found among them all.
        var flat = new Point2D(uv.X * Uv.Scale(patch.Surface), uv.Y * Uv.ScaleV(patch.Surface));
        if (patch.Carried.Any(c => FlatFace.Within(c.Box, flat) && c.Flat.Winding(flat) != 0))
            uv = Arrangement.Inside(patch.Surface, patch.Expanded().UvLoops);
        var p = patch.Surface.At(uv.X, uv.Y);
        return other.Classify(p, patch.Surface.Normal(uv.X, uv.Y) * (patch.SameSense ? 1 : -1));
    }

    internal static Patch PatchOf(Body body, FaceId f, int operand)
    {
        var face = body[f];
        var loops = face.Loops.Select(l => (IReadOnlyList<Use>)[.. body.CoedgesOf(l).Select(c =>
        {
            var (from, to) = body.ParametersOf(c);
            return new Use(body[body[c].Edge].Curve, from, to, body[c].Pcurve) { Of = operand == 0 ? c : null };
        })]).ToList();
        return new Patch(face.Surface, face.SameSense, loops, new HashSet<Origin> { new(operand, f) });
    }

    // One face of each solid: the curves their surfaces share, cut down to where they lie on both faces, or, on one
    // surface, each face's edges where they lie on the other face.
    static void Intersect(Operand[] ops, int fa, int fb)
    {
        var (x, y) = (ops[0].Solid.Faces[fa], ops[1].Solid.Faces[fb]);
        var meeting = Intersections.Of(x.Surface, y.Surface);
        switch (meeting.Kind)
        {
            case Meeting.Unsupported:
                // Surfaces the kernel cannot intersect, but faces that only touch (a fillet's cylinder running on into
                // the next one's torus): where neither face crosses the other's surface inside that face, no cut.
                if (!x.Crosses(y) && !y.Crosses(x)) break;
                throw new BooleanFailure($"two faces meet as {meeting.Why}, which the managed kernel does not handle yet");
            case Meeting.Curves:
                foreach (var c in meeting.Curves) Along(ops, fa, fb, c);
                break;
            case Meeting.Coincident:
                Imprint(ops, 0, fa, ops[1], fb);
                Imprint(ops, 1, fb, ops[0], fa);
                break;
        }
    }

    static void Along(Operand[] ops, int fa, int fb, Curve3 c)
    {
        var (a, b) = (ops[0], ops[1]);
        // The face with fewer edges first: most pieces of a curve along a large face lie outside the small one.
        var (x, y) = (a.Solid.Faces[fa], b.Solid.Faces[fb]);
        var smallFirst = x.Edges.Count <= y.Edges.Count;
        var small = smallFirst ? x : y;
        var ts = new List<double>();
        foreach (var (op, face) in new[] { (a, fa), (b, fb) })
        foreach (var e in op.Solid.Faces[face].Edges)
        {
            var edge = op.Body[e];
            // The large face's edges only near the small face: away from it the curve's pieces lie outside the small
            // face whatever splits them (its own edges' points bound every piece inside it).
            if (op.Solid.Faces[face] != small && edge.Curve is not Pole && !small.Overlaps(op.BoxOf(e))) continue;
            var (points, overlap) = Intersections.Meet(c, edge.Curve);
            if (overlap) points = [edge.Curve.At(edge.T0), edge.Curve.At(edge.T1)];
            foreach (var p in points)
            {
                if (Intersections.On(edge.Curve, edge.T0, edge.T1, p) is null) continue;
                ts.Add(Intersections.Param(c, p));
                if (edge.Curve is Pole) op.SplitPole(face, e, c, Intersections.Param(c, p));
            }
        }

        List<(double From, double To)> intervals = [];
        if (c.Period is not null)
        {
            var sorted = Dedupe(ts.Select(Intersections.Wrap), c, periodic: true);
            if (sorted.Count == 0) sorted.Add(0);
            for (var i = 0; i < sorted.Count; i++)
                intervals.Add((sorted[i], i + 1 < sorted.Count ? sorted[i + 1] : sorted[0] + 2 * Math.PI));
        }
        else
        {
            var sorted = Dedupe(ts, c, periodic: false);
            for (var i = 0; i + 1 < sorted.Count; i++) intervals.Add((sorted[i], sorted[i + 1]));
        }

        foreach (var (t0, t1) in intervals)
        {
            var m = c.At((t0 + t1) / 2);
            var first = (smallFirst ? x : y).Locate(m);
            if (first == Place.Outside) continue;
            var second = (smallFirst ? y : x).Locate(m);
            if (second == Place.Outside) continue;
            var (pa, pb) = smallFirst ? (first, second) : (second, first);
            if (pa == Place.Inside) a.Cut(fa, c, t0, t1);
            if (pb == Place.Inside) b.Cut(fb, c, t0, t1);
            foreach (var p in new[] { c.At(t0), c.At(t1) })
            {
                a.SplitAt(fa, p);
                b.SplitAt(fb, p);
            }
        }
    }

    // The edges of `from`'s face on the surface `onto`'s face shares: pieces inside it cut it; every piece on
    // either face splits the edges at its ends.
    static void Imprint(Operand[] ops, int fromIndex, int fromFace, Operand onto, int ontoFace)
    {
        var from = ops[fromIndex];
        var target = onto.Solid.Faces[ontoFace];
        foreach (var e in from.Solid.Faces[fromFace].Edges)
        {
            var edge = from.Body[e];
            // An edge whose box misses the face lies outside it, and meets only the edges its box meets.
            var box = from.BoxOf(e);
            if (!target.Overlaps(box)) continue;
            var (start, end) = (edge.Curve.At(edge.T0), edge.Curve.At(edge.T1));
            var ts = new List<double>();
            foreach (var g in target.Edges)
            {
                var other = onto.Body[g];
                if (!FlatFace.Touch(box, onto.BoxOf(g))) continue;
                var (points, overlap) = Intersections.Meet(edge.Curve, other.Curve);
                if (overlap) points = [other.Curve.At(other.T0), other.Curve.At(other.T1)];
                foreach (var p in points)
                    if (Intersections.On(other.Curve, other.T0, other.T1, p) is not null &&
                        Intersections.On(edge.Curve, edge.T0, edge.T1, p) is { } t &&
                        (p - start).Length > Intersections.Snap && (p - end).Length > Intersections.Snap) ts.Add(t);
            }
            // A closed edge starts and ends at one point: its ends are kept apart by parameter.
            List<double> sorted = [edge.T0, .. Dedupe(ts, edge.Curve, periodic: false), edge.T1];
            for (var i = 0; i + 1 < sorted.Count; i++)
            {
                var (t0, t1) = (sorted[i], sorted[i + 1]);
                var place = target.Locate(edge.Curve.At((t0 + t1) / 2));
                if (place == Place.Outside) continue;
                if (place == Place.Inside) onto.Cut(ontoFace, edge.Curve, t0, t1);
                from.Split(e, t0);
                from.Split(e, t1);
                onto.SplitAt(ontoFace, edge.Curve.At(t0));
                onto.SplitAt(ontoFace, edge.Curve.At(t1));
            }
        }
    }

    // Parameters sorted, those whose points lie within Snap of each other (round the period on a circle) taken once.
    static List<double> Dedupe(IEnumerable<double> ts, Curve3 c, bool periodic)
    {
        var sorted = ts.Order().ToList();
        var result = new List<double>();
        // On a pole every t is one point: there t itself tells them apart.
        bool Apart(double t, double s) => c is Pole ? Math.Abs(t - s) > 1e-9 : (c.At(t) - c.At(s)).Length > Intersections.Snap;
        foreach (var t in sorted)
            if (result.Count == 0 || Apart(t, result[^1])) result.Add(t);
        if (periodic && result.Count > 1 && !Apart(result[0], result[^1])) result.RemoveAt(result.Count - 1);
        return result;
    }

    /// <summary>One solid of a boolean: where its edges split and the cuts on its faces.</summary>
    sealed class Operand(Body body, int index)
    {
        public Body Body { get; } = body;
        public Solid Solid { get; } = new(body);
        // Where each edge splits and the cuts on each face: none until there are some.
        readonly List<double>?[] splits = new List<double>?[body.Edges.Length];
        readonly List<(Curve3 Curve, double T0, double T1)>?[] cuts = new List<(Curve3, double, double)>?[body.Faces.Length];
        readonly (Point3D Min, Point3D Max)?[] boxes = new (Point3D, Point3D)?[body.Edges.Length];

        /// <summary>A box round an edge (see <see cref="FlatFace.BoxOf(Edge)"/>), made once.</summary>
        public (Point3D Min, Point3D Max) BoxOf(EdgeId e) => boxes[e.Index] ??= FlatFace.BoxOf(Body[e]);

        public void Cut(int face, Curve3 c, double t0, double t1) => (cuts[face] ??= []).Add((c, t0, t1));

        /// <summary>Splits the edge at t, unless t is at one of its ends (a pole's, where t is the u it runs over).</summary>
        public void Split(EdgeId e, double t)
        {
            var edge = Body[e];
            var p = edge.Curve.At(t);
            if (edge.Curve is Pole ? Math.Min(Math.Abs(t - edge.T0), Math.Abs(t - edge.T1)) <= 1e-9
                    : (p - edge.Curve.At(edge.T0)).Length <= Intersections.Snap || (p - edge.Curve.At(edge.T1)).Length <= Intersections.Snap)
                return;
            (splits[e.Index] ??= []).Add(t);
        }

        /// <summary>
        /// A curve through a pole (a meridian, a cone's generator) at t: the pole's edge split at the u where the
        /// curve's pieces either side reach it.
        /// </summary>
        public void SplitPole(int face, EdgeId e, Curve3 c, double t)
        {
            var surface = Body.Faces[face].Surface;
            var pc = Uv.Pcurve(surface, c);
            var coedge = Body.Coedges.First(co => co.Edge == e).Pcurve;
            foreach (var side in new[] { -1e-4, 1e-4 })
            {
                var u = Uv.Within(surface, pc, t + side).At(t).X;
                var along = (u - coedge.At(0).X) / coedge.Derivative(0).X;
                Split(e, Body[e].T0 + Intersections.Wrap(along - Body[e].T0));
            }
        }

        /// <summary>Splits whichever edge of the face the point lies on.</summary>
        public void SplitAt(int face, Point3D p)
        {
            foreach (var e in Solid.Faces[face].Edges)
            {
                var edge = Body[e];
                if (Intersections.On(edge.Curve, edge.T0, edge.T1, p) is { } t) Split(e, t);
            }
        }

        // An edge's pieces from T0 to T1.
        List<(double From, double To)> Pieces(EdgeId e)
        {
            var edge = Body[e];
            if (splits[e.Index] is null) return [(edge.T0, edge.T1)];
            var ts = Dedupe(splits[e.Index]!, edge.Curve, periodic: false);
            var bounds = new List<double> { edge.T0 };
            bounds.AddRange(ts.Where(t => t > edge.T0 && t < edge.T1));
            bounds.Add(edge.T1);
            return [.. bounds.Zip(bounds.Skip(1))];
        }

        /// <summary>A face neither cut nor split comes through whole.</summary>
        public bool Whole(int f) => cuts[f] is null && Solid.Faces[f].Edges.All(e => splits[e.Index] is null);

        /// <summary>
        /// Every face cut into the faces its cuts bound, with the face each comes from, but for those in
        /// <paramref name="skip"/> (kept as they are). Of the first operand's flat faces, a hole whose every edge runs
        /// along a face kept as it is stays out of the arrangement and is carried whole into the piece round it
        /// (<see cref="Carried"/>).
        /// </summary>
        public IEnumerable<(Patch Patch, int Face, bool Entire)> Patches(IReadOnlySet<int> skip)
        {
            for (var f = 0; f < Body.Faces.Length; f++)
            {
                if (skip.Contains(f)) continue;
                var face = Body.Faces[f];
                var flat = Solid.Faces[f];
                if (Whole(f))
                {
                    yield return (PatchOf(Body, new FaceId(f), index) with { Unchanged = true }, f, true);
                    continue;
                }
                var carry = index == 0 && face.Surface is Plane
                    ? face.Loops.Skip(1).Where(l => Body.CoedgesOf(l).All(c => Body.UsesOf(Body[c].Edge).All(u => Body.FaceOf(u).Index == f || skip.Contains(Body.FaceOf(u).Index)))).ToHashSet()
                    : [];
                var own = new List<IReadOnlyList<Use>>();
                foreach (var loop in face.Loops.Where(l => !carry.Contains(l)))
                {
                    var loopUses = new List<Use>();
                    foreach (var c in Body.CoedgesOf(loop))
                    {
                        var coedge = Body[c];
                        var pieces = Pieces(coedge.Edge);
                        if (pieces.Count == 1)
                        {
                            var (from, to) = Body.ParametersOf(c);
                            loopUses.Add(new Use(Body[coedge.Edge].Curve, from, to, coedge.Pcurve) { Of = index == 0 ? c : null });
                            continue;
                        }
                        if (!coedge.Forward) pieces = [.. pieces.AsEnumerable().Reverse().Select(p => (p.To, p.From))];
                        foreach (var (from, to) in pieces) loopUses.Add(new Use(Body[coedge.Edge].Curve, from, to, coedge.Pcurve));
                    }
                    own.Add(loopUses);
                }
                var origin = new HashSet<Origin> { new(index, new FaceId(f)) };
                // Only split, not cut: the face keeps its loops, with more uses.
                if (cuts[f] is not { } faceCuts)
                {
                    yield return (new Patch(face.Surface, face.SameSense, own, origin) { Carried = [.. carry.Select(l => CarriedOf(l, face.Loops.IndexOf(l)))] }, f, true);
                    continue;
                }
                // Into (u, v) with the face on the left, then the cuts both ways.
                var uses = own.SelectMany(l => l).ToList();
                if (!face.SameSense) uses = [.. uses.Select(u => u.Reversed())];
                foreach (var (curve, t0, t1) in Distinct(faceCuts))
                {
                    var pc = Uv.Within(face.Surface, Uv.Pcurve(face.Surface, curve), (t0 + t1) / 2);
                    var (du, dv) = flat.ShiftInto(pc.At((t0 + t1) / 2));
                    pc = Uv.Shift(pc, du, dv);
                    var use = new Use(curve, t0, t1, pc);
                    uses.Add(use);
                    uses.Add(use.Reversed());
                }
                List<List<IReadOnlyList<Use>>> faces;
                try
                {
                    faces = Arrangement.Faces(face.Surface, uses);
                }
                catch (BooleanFailure failure)
                {
                    throw new BooleanFailure($"{failure.Message} (face {f} of the {(index == 0 ? "first" : "second")} solid, a {face.Surface.GetType().Name.ToLowerInvariant()}, {faceCuts.Count} cuts)");
                }
                var made = faces.Select(loops => new Patch(face.Surface, face.SameSense, [.. loops.Select(l => Arrangement.Sensed(l, face.SameSense))], origin)).ToList();
                // Each carried hole into the piece round its first corner (in flat (u, v), where the face's loops are).
                var flats = faces.Select(loops => loops.Select(l => new ProfileLoop([.. l.Select(u => Uv.Flat(face.Surface, u.Pcurve, u.From, u.To))])).ToList()).ToList();
                var into = made.Select(_ => new List<Carried>()).ToList();
                // Pieces by the size of their outer loop's box: a hole in none of the smaller ones lies in the largest.
                static double Size(ProfileLoop l) => l.Segments.SelectMany(s => new[] { s.Start, s.End }).Aggregate((Lo: (X: double.MaxValue, Y: double.MaxValue), Hi: (X: double.MinValue, Y: double.MinValue)),
                    (b, p) => ((Math.Min(b.Lo.X, p.X), Math.Min(b.Lo.Y, p.Y)), (Math.Max(b.Hi.X, p.X), Math.Max(b.Hi.Y, p.Y))), b => (b.Hi.X - b.Lo.X) * (b.Hi.Y - b.Lo.Y));
                var order = Enumerable.Range(0, flats.Count).OrderBy(k => Size(flats[k][0])).ToList();
                foreach (var loop in carry)
                {
                    var corner = flat.FlatOf(Body[Body.StartOf(Body[loop].First)].Point);
                    var piece = order.Take(order.Count - 1).FirstOrDefault(p => flats[p].Sum(l => l.Winding(corner)) != 0, order[^1]);
                    into[piece].Add(CarriedOf(loop, face.Loops.IndexOf(loop)));
                }
                for (var k = 0; k < made.Count; k++)
                    yield return (into[k].Count == 0 ? made[k] : made[k] with { Carried = into[k] }, f, false);

                Carried CarriedOf(LoopId loop, int k)
                {
                    var body = Body;
                    return new Carried(loop, flat.Loops[k], flat.BoxOfLoop(k), () => [.. body.CoedgesOf(loop).Select(c =>
                    {
                        var (from, to) = body.ParametersOf(c);
                        return new Use(body[body[c].Edge].Curve, from, to, body[c].Pcurve) { Of = c };
                    })]);
                }
            }
        }

        // Cuts with the same ends and middle taken once.
        static IEnumerable<(Curve3, double, double)> Distinct(List<(Curve3 Curve, double T0, double T1)> list)
        {
            var kept = new List<(Curve3 Curve, double T0, double T1)>();
            foreach (var c in list)
            {
                var (s, e, m) = (c.Curve.At(c.T0), c.Curve.At(c.T1), c.Curve.At((c.T0 + c.T1) / 2));
                bool Near(Point3D p, Point3D q) => (p - q).Length <= Intersections.Snap;
                if (kept.Any(k => Near(k.Curve.At((k.T0 + k.T1) / 2), m) &&
                                  ((Near(k.Curve.At(k.T0), s) && Near(k.Curve.At(k.T1), e)) || (Near(k.Curve.At(k.T0), e) && Near(k.Curve.At(k.T1), s)))))
                    continue;
                kept.Add(c);
            }
            return kept;
        }
    }
}
