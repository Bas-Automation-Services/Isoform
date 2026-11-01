using BasAutomation.Geometry;

namespace Isoform.Brep;

/// <summary>Faces on one surface merged, and faces sewn into solids by the geometry of their edges.</summary>
internal static class Sewing
{
    const double Snap = Intersections.Snap;

    static bool Near(Point3D a, Point3D b) => (a - b).Length <= Snap;

    // w runs back along u: the same piece of curve the other way.
    static bool Opposite(Use u, Use w) => Near(u.Start, w.End) && Near(u.End, w.Start) && Near(u.Mid, w.Mid) &&
                                          (!Near(u.Start, u.End) || Tangent(u).Dot(Tangent(w)) < 0);

    static Vector3D Tangent(Use u) => u.Curve.Derivative((u.From + u.To) / 2) * Math.Sign(u.To - u.From);

    /// <summary>
    /// Patches that lie on one surface, face the same way and share an edge, merged into one face (through every
    /// edge they share, so faces round a cylinder close into one with a seam). Their origins are pooled.
    /// </summary>
    public static List<Patch> MergeSurfaces(List<Patch> patches)
    {
        var parent = Enumerable.Range(0, patches.Count).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        // Only patches with a use between the same two points can share an edge.
        var points = new Welded();
        var between = new Dictionary<(int, int), List<int>>();
        for (var i = 0; i < patches.Count; i++)
        foreach (var u in patches[i].Loops.SelectMany(l => l))
        {
            var (a, b) = (points[u.Start], points[u.End]);
            var key = (Math.Min(a, b), Math.Max(a, b));
            if (!between.TryGetValue(key, out var list)) between[key] = list = [];
            if (list.Count == 0 || list[^1] != i) list.Add(i);
        }
        foreach (var list in between.Values)
        for (var x = 0; x < list.Count; x++)
        for (var y = x + 1; y < list.Count; y++)
            if (Find(list[x]) != Find(list[y]) && Mergeable(patches[list[x]], patches[list[y]])) parent[Find(list[x])] = Find(list[y]);

        var result = new List<Patch>();
        foreach (var group in Enumerable.Range(0, patches.Count).GroupBy(Find))
        {
            var members = group.Select(i => patches[i]).ToList();
            result.AddRange(members.Count == 1 ? members : Merge(members));
        }
        return result;
    }

    static bool Mergeable(Patch a, Patch b)
    {
        if (!Intersections.SameSurface(a.Surface, b.Surface)) return false;
        var p = a.Loops[0][0].Start;
        if (OutwardAt(a, p).Dot(OutwardAt(b, p)) <= 0) return false;
        var bUses = b.Loops.SelectMany(l => l).ToList();
        return a.Loops.SelectMany(l => l).Any(u => bUses.Any(w => Opposite(u, w)));
    }

    static Vector3D OutwardAt(Patch patch, Point3D p)
    {
        var uv = Uv.Of(patch.Surface, p);
        return patch.Surface.Normal(uv.X, uv.Y) * (patch.SameSense ? 1 : -1);
    }

    // The members' uses re-expressed on the first member's surface (on a cylinder each member placed in u next to
    // the one it was reached from), the uses they share dropped, and the rest traced again.
    static IEnumerable<Patch> Merge(List<Patch> members)
    {
        var surface = members[0].Surface;
        var sense = members[0].SameSense;
        var loops = members.Select(m => m.Loops.Select(l => Arrangement.Sensed(l, sense)).ToList()).ToList();
        var placed = new List<Use>?[members.Count];

        placed[0] = Place(surface, loops[0], 0, 0, null);
        var queue = new Queue<int>([0]);
        while (queue.TryDequeue(out var m))
        {
            for (var other = 0; other < members.Count; other++)
            {
                if (placed[other] is not null) continue;
                foreach (var u in placed[m]!)
                {
                    var match = FindUse(loops[other], w => Opposite(u, w));
                    if (match is not { } at) continue;
                    placed[other] = Place(surface, loops[other], at.Loop, at.Index, u.Pcurve.At((u.From + u.To) / 2));
                    queue.Enqueue(other);
                    break;
                }
            }
        }
        if (placed.Any(p => p is null)) throw new BooleanFailure("faces on one surface do not join up");

        var uses = placed.SelectMany(p => p!).ToList();
        var flat = uses.Select(u => Uv.Flat(surface, u.Pcurve, u.From, u.To)).ToList();
        var points = new Welded();
        var (starts, ends) = (uses.Select(u => points[u.Start]).ToArray(), uses.Select(u => points[u.End]).ToArray());
        var leaving = Enumerable.Range(0, uses.Count).ToLookup(i => starts[i]);
        var drop = new bool[uses.Count];
        for (var i = 0; i < uses.Count; i++)
        foreach (var j in leaving[ends[i]])
        {
            if (drop[i]) break;
            if (j <= i || drop[j] || ends[j] != starts[i] || !Opposite(uses[i], uses[j])) continue;
            // Shared in (u, v) too: not the two sides of a seam.
            var (a, b) = (flat[i], flat[j]);
            if ((a.Start - b.End).Length <= Snap && (a.End - b.Start).Length <= Snap) drop[i] = drop[j] = true;
        }
        var origins = members.SelectMany(m => m.Origins).ToHashSet();
        foreach (var face in Arrangement.Faces(surface, [.. uses.Where((_, i) => !drop[i])]))
            yield return new Patch(surface, sense, [.. face.Select(l => Arrangement.Sensed(l, sense))], origins);
    }

    static (int Loop, int Index)? FindUse(List<IReadOnlyList<Use>> loops, Func<Use, bool> match)
    {
        for (var l = 0; l < loops.Count; l++)
        for (var i = 0; i < loops[l].Count; i++)
            if (match(loops[l][i])) return (l, i);
        return null;
    }

    // A patch's loops (face on the left in (u, v)) with p-curves on `surface`, each loop continuous in u; the given
    // use's middle at `target` (anywhere when null), other loops in the period nearest it.
    static List<Use> Place(Surface surface, List<IReadOnlyList<Use>> loops, int loop, int index, Point2D? target)
    {
        var result = new List<Use>();
        var anchor = target;
        foreach (var l in new[] { loop }.Concat(Enumerable.Range(0, loops.Count).Where(i => i != loop)))
        {
            var uses = loops[l];
            var start = l == loop ? index : 0;
            Point2D? previousEnd = null;
            for (var k = 0; k < uses.Count; k++)
            {
                var u = uses[(start + k) % uses.Count];
                var pc = Uv.Pcurve(surface, u.Curve);
                // Each periodic parameter by whole turns: on from the use before, or to the anchor.
                var (mid, from) = (pc.At((u.From + u.To) / 2), pc.At(u.From));
                double Turn(double want, double at) => 2 * Math.PI * Math.Round((want - at) / (2 * Math.PI));
                var du = surface.PeriodU is null ? 0 : Turn(previousEnd is { } e ? e.X + (mid.X - from.X) : anchor?.X ?? Intersections.Wrap(mid.X), mid.X);
                var dv = surface.PeriodV is null ? 0 : Turn(previousEnd is { } f ? f.Y + (mid.Y - from.Y) : anchor?.Y ?? Intersections.Wrap(mid.Y), mid.Y);
                pc = Uv.Shift(pc, du, dv);
                previousEnd = pc.At(u.To);
                anchor ??= pc.At((u.From + u.To) / 2);
                result.Add(u with { Pcurve = pc });
            }
        }
        return result;
    }

    /// <summary>
    /// Patches sewn into solids: uses that continue each other on one curve through a vertex of only those two
    /// edges joined, uses paired with the use that runs back along them, each connected set of faces a shell, and
    /// voids placed in the solid around them. Every body passes <see cref="BodyCheck"/> or the sewing fails.
    /// </summary>
    public static List<Shaped> Sew(List<Patch> patches, double tolerance)
    {
        patches = JoinEdges(patches);
        var uses = patches.SelectMany((p, pi) => p.Loops.SelectMany((l, li) => l.Select((u, ui) => (Patch: pi, Loop: li, Index: ui, Use: u)))).ToList();
        var welded = new Welded();
        var points = welded.Points;
        var start = uses.Select(u => welded[u.Use.Start]).ToArray();
        var indexOf = Enumerable.Range(0, uses.Count).ToDictionary(i => (uses[i].Patch, uses[i].Loop, uses[i].Index));
        var end = uses.Select(u => welded[u.Use.End]).ToArray();

        var partner = Enumerable.Repeat(-1, uses.Count).ToArray();
        var byStart = Enumerable.Range(0, uses.Count).ToLookup(i => start[i]);
        for (var i = 0; i < uses.Count; i++)
        {
            if (partner[i] >= 0) continue;
            // A pole's use is the only one of its edge.
            if (uses[i].Use.Curve is Pole)
            {
                partner[i] = i;
                continue;
            }
            foreach (var j in byStart[end[i]])
            {
                if (j == i || partner[j] >= 0 || end[j] != start[i] || !Opposite(uses[i].Use, uses[j].Use)) continue;
                partner[i] = j;
                partner[j] = i;
                break;
            }
            if (partner[i] < 0)
            {
                var near = uses.Where(x => Near(x.Use.Mid, uses[i].Use.Mid))
                    .Select(x => $"{string.Join(",", patches[x.Patch].Origins.Select(o => $"{o.Operand}:{o.Face.Index}"))} {x.Use.Start}->{x.Use.End}");
                throw new BooleanFailure($"the faces do not close round the edge from {uses[i].Use.Start} to {uses[i].Use.End} (uses: {string.Join("; ", near)})");
            }
        }

        var parent = Enumerable.Range(0, patches.Count).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        for (var i = 0; i < uses.Count; i++) parent[Find(uses[i].Patch)] = Find(uses[partner[i]].Patch);
        var shells = Enumerable.Range(0, patches.Count).GroupBy(Find).Select(g => g.ToList()).ToList();

        // The body, and which of its faces came through unchanged.
        (Shaped Shaped, HashSet<int> Unchanged) Build(IReadOnlyList<List<int>> shellPatches)
        {
            var unchanged = new HashSet<int>();
            var builder = new BodyBuilder(tolerance);
            var vertexIds = new Dictionary<int, VertexId>();
            VertexId V(int v) => vertexIds.TryGetValue(v, out var id) ? id : vertexIds[v] = builder.AddVertex(points[v]);
            var edgeOf = new Dictionary<int, (EdgeId Edge, bool Forward, Curve2 Pcurve)>();
            var origins = new List<IReadOnlyList<Origin>>();
            foreach (var members in shellPatches)
            {
                var shell = builder.AddShell();
                var inShell = members.ToHashSet();
                foreach (var pi in members)
                {
                    var patch = patches[pi];
                    var face = builder.AddFace(shell, patch.Surface, patch.SameSense);
                    if (patch.Unchanged) unchanged.Add(face.Index);
                    origins.Add([.. patch.Origins.OrderBy(o => o.Operand).ThenBy(o => o.Face.Index)]);
                    for (var li = 0; li < patch.Loops.Count; li++)
                    {
                        var loopUses = new List<BodyBuilder.Use>();
                        for (var ui = 0; ui < patch.Loops[li].Count; ui++)
                        {
                            var i = indexOf[(pi, li, ui)];
                            if (!edgeOf.TryGetValue(i, out var e))
                            {
                                var (u, w) = (uses[i].Use, uses[partner[i]].Use);
                                var forward = u.From < u.To;
                                var (t0, t1) = forward ? (u.From, u.To) : (u.To, u.From);
                                var (s, f) = forward ? (start[i], end[i]) : (end[i], start[i]);
                                // A circle's range starts in [0, 2π), where a reader that takes it from the vertices puts it.
                                var d = u.Curve.Period is not null ? 2 * Math.PI * Math.Floor(t0 / (2 * Math.PI) + 1e-12) : 0;
                                var edge = builder.AddEdge(u.Curve, t0 - d, t1 - d, V(s), V(f));
                                // The partner's p-curve in this edge's parameter: its From is at u.To, its To at u.From.
                                var alpha = (w.From - w.To) / (u.To - u.From);
                                var beta = w.From - alpha * u.To;
                                edgeOf[i] = e = (edge, forward, Uv.Reparametrised(u.Pcurve, 1, d));
                                if (partner[i] != i) edgeOf[partner[i]] = (edge, !forward, Uv.Reparametrised(w.Pcurve, Math.Round(alpha), beta + alpha * d));
                            }
                            loopUses.Add(new BodyBuilder.Use(e.Edge, e.Forward, e.Pcurve));
                        }
                        builder.AddLoop(face, loopUses);
                    }
                }
            }
            return (new Shaped(builder.Build(), origins), unchanged);
        }

        // One shell is the solid; several are each built alone, to tell solids (positive volume) from voids.
        if (shells.Count == 1) return [Checked(Build(shells))];
        var alone = shells.Select(s => Build([s]).Shaped.Body).ToList();
        var volumes = alone.Select(b => MassProperties.Volume(b)).ToList();
        var solids = Enumerable.Range(0, shells.Count).Where(s => volumes[s] > 0).ToList();
        var voidsOf = solids.ToDictionary(s => s, _ => new List<int>());
        foreach (var v in Enumerable.Range(0, shells.Count).Where(s => volumes[s] <= 0))
        {
            var p = alone[v].Vertices[0].Point;
            var owner = solids.FirstOrDefault(s => new Solid(alone[s]).Inside(p), -1);
            if (owner < 0) throw new BooleanFailure("a void lies in no solid");
            voidsOf[owner].Add(v);
        }

        return [.. solids.Select(s => Checked(Build([shells[s], .. voidsOf[s].Select(v => shells[v])])))];
    }

    /// <summary>
    /// A boolean's result as an edit of its first operand <paramref name="a"/>: the faces in <paramref name="whole"/>
    /// come through untouched and are copied as they are, with their edges, vertices and p-curves, but for those next
    /// to the rest on the same surface (which merge with it). The rest (the other kept pieces) is sewn as
    /// <see cref="Sew"/> sews, onto the copied faces' own edges where it meets them, its edges joined through a corner
    /// only where the copied faces leave none there either. Null where that does not fit (a use nothing pairs, a vertex
    /// claimed twice, more than one shell, a copied corner left with two edges on one curve): the caller then sews
    /// everything.
    /// </summary>
    internal static Shaped? Edit(Solid solid, IReadOnlySet<int> whole, List<Patch> rest, double tolerance)
    {
        var a = solid.Body;
        // Untouched faces next to a face that is not, on a surface of the rest: they merge with it, so are sewn again.
        var ring = new HashSet<int>();
        for (var e = 0; e < a.Edges.Length; e++)
        {
            var faces = a.UsesOf(new EdgeId(e)).Select(u => a.FaceOf(u).Index).Distinct().ToList();
            if (faces.All(whole.Contains)) continue;
            foreach (var f in faces.Where(whole.Contains))
                if (rest.Any(p => Intersections.SameSurface(p.Surface, a.Faces[f].Surface))) ring.Add(f);
        }
        var copied = whole.Where(f => !ring.Contains(f)).Order().ToList();
        if (copied.Count == 0) return null;
        var isCopied = new HashSet<int>(copied);

        // Where the region meets the copied faces: a's edges between a copied face and one sewn again, by curve and range,
        // and how many copied coedges leave each of their ends (for joining).
        var boundary = new Dictionary<(Curve3, double, double), int>(new BoundaryKey());
        var ends = new HashSet<VertexId>();
        for (var e = 0; e < a.Edges.Length; e++)
        {
            var faces = a.UsesOf(new EdgeId(e)).Select(u => a.FaceOf(u).Index).ToList();
            if (!faces.Any(isCopied.Contains) || faces.All(isCopied.Contains)) continue;
            boundary[(a.Edges[e].Curve, a.Edges[e].T0, a.Edges[e].T1)] = e;
            ends.Add(a.Edges[e].Start);
            ends.Add(a.Edges[e].End);
        }
        // Carried holes stay whole where every edge of theirs runs along a copied face and their patch merges with
        // nothing; otherwise they are sewn with the rest.
        bool Keeps(Carried c) => a.CoedgesOf(c.Loop).All(co => a.UsesOf(a[co].Edge).Any(u => isCopied.Contains(a.FaceOf(u).Index)));
        var patches = rest.Concat(ring.Select(f => Booleans.PatchOf(a, new FaceId(f), 0) with { Unchanged = true })).ToList();
        patches = [.. patches.Select(p => p.Carried.Count == 0 ? p
            : p.Carried.All(Keeps) && !patches.Any(q => q != p && Mergeable(p, q)) ? p
            : p.Expanded())];
        var carried = patches.SelectMany(p => p.Carried).Select(c => c.Loop).ToHashSet();
        var touched = new Welded();
        foreach (var u in patches.SelectMany(p => p.Loops).SelectMany(l => l)) (_, _) = (touched[u.Start], touched[u.End]);
        var leavingCopied = new List<Point3D>();
        for (var c = 0; c < a.Coedges.Length; c++)
        {
            var co = new CoedgeId(c);
            var from = a.StartOf(co);
            if (ends.Contains(from) && (isCopied.Contains(a.FaceOf(co).Index) || carried.Contains(a[co].Loop)) && touched.Find(a[from].Point) is not null)
                leavingCopied.Add(a[from].Point);
        }
        var region = JoinEdges(MergeSurfaces(patches), leavingCopied);

        var uses = region.SelectMany((p, pi) => p.Loops.SelectMany((l, li) => l.Select((u, ui) => (Patch: pi, Loop: li, Index: ui, Use: u)))).ToList();
        var welded = new Welded();
        var start = uses.Select(u => welded[u.Use.Start]).ToArray();
        var end = uses.Select(u => welded[u.Use.End]).ToArray();
        var indexOf = Enumerable.Range(0, uses.Count).ToDictionary(i => (uses[i].Patch, uses[i].Loop, uses[i].Index));
        // Each use's partner: another use, itself (a pole), or ~e for a's edge e on the boundary.
        var partner = Enumerable.Repeat(int.MinValue, uses.Count).ToArray();
        // The welded points that are a's vertices.
        var vertexOfPoint = new Dictionary<int, VertexId>();
        bool Claim(int point, VertexId v) => vertexOfPoint.TryGetValue(point, out var had) ? had == v : (vertexOfPoint[point] = v) == v;
        for (var i = 0; i < uses.Count; i++)
        {
            var u = uses[i].Use;
            if (u.Curve is Pole) partner[i] = i;
            else if (boundary.TryGetValue((u.Curve, Math.Min(u.From, u.To), Math.Max(u.From, u.To)), out var e))
            {
                var edge = a.Edges[e];
                var (s0, s1) = u.From < u.To ? (edge.Start, edge.End) : (edge.End, edge.Start);
                if (!Claim(start[i], s0) || !Claim(end[i], s1)) return null;
                partner[i] = ~e;
            }
        }
        var byStart = Enumerable.Range(0, uses.Count).Where(i => partner[i] == int.MinValue).ToLookup(i => start[i]);
        for (var i = 0; i < uses.Count; i++)
        {
            if (partner[i] != int.MinValue) continue;
            foreach (var j in byStart[end[i]])
            {
                if (j == i || partner[j] != int.MinValue || end[j] != start[i] || !Opposite(uses[i].Use, uses[j].Use)) continue;
                (partner[i], partner[j]) = (j, i);
                break;
            }
            if (partner[i] == int.MinValue) return null;
        }

        // One shell, or the full sewing sorts solids from voids.
        var parent = Enumerable.Range(0, region.Count + a.Faces.Length).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        void Union(int x, int y) => parent[Find(x)] = Find(y);
        for (var i = 0; i < uses.Count; i++)
            if (partner[i] >= 0) Union(uses[i].Patch, uses[partner[i]].Patch);
            else Union(uses[i].Patch, region.Count + a.UsesOf(new EdgeId(~partner[i])).Select(c => a.FaceOf(c).Index).First(isCopied.Contains));
        for (var e = 0; e < a.Edges.Length; e++)
        {
            var faces = a.UsesOf(new EdgeId(e)).Select(c => a.FaceOf(c).Index).Where(isCopied.Contains).ToList();
            for (var k = 1; k < faces.Count; k++) Union(region.Count + faces[0], region.Count + faces[k]);
        }
        for (var pi = 0; pi < region.Count; pi++)
        foreach (var c in region[pi].Carried)
            Union(pi, region.Count + a.UsesOf(a[a[c.Loop].First].Edge).Select(u => a.FaceOf(u).Index).First(isCopied.Contains));
        var roots = Enumerable.Range(0, region.Count).Select(Find).Concat(copied.Select(f => Find(region.Count + f))).Distinct().Count();
        if (roots != 1) return null;

        var builder = new BodyBuilder(tolerance);
        var shell = builder.AddShell();
        var vertexOfA = new Dictionary<VertexId, VertexId>();
        VertexId VA(VertexId v) => vertexOfA.TryGetValue(v, out var id) ? id : vertexOfA[v] = builder.AddVertex(a[v].Point);
        var edgeOfA = new Dictionary<int, EdgeId>();
        EdgeId EA(int e) => edgeOfA.TryGetValue(e, out var id) ? id : edgeOfA[e] = builder.AddEdge(a.Edges[e].Curve, a.Edges[e].T0, a.Edges[e].T1, VA(a.Edges[e].Start), VA(a.Edges[e].End));
        var pointIds = new Dictionary<int, VertexId>();
        VertexId V(int point) => vertexOfPoint.TryGetValue(point, out var v) ? VA(v) : pointIds.TryGetValue(point, out var id) ? id : pointIds[point] = builder.AddVertex(welded.Points[point]);

        var origins = new List<IReadOnlyList<Origin>>();
        var unchanged = new HashSet<int>();
        foreach (var f in copied)
        {
            var face = builder.AddFace(shell, a.Faces[f].Surface, a.Faces[f].SameSense);
            unchanged.Add(face.Index);
            origins.Add([new Origin(0, new FaceId(f))]);
            foreach (var loop in a.Faces[f].Loops)
                builder.AddLoop(face, [.. a.CoedgesOf(loop).Select(c => new BodyBuilder.Use(EA(a[c].Edge.Index), a[c].Forward, a[c].Pcurve))]);
        }
        var edgeOf = new Dictionary<int, (EdgeId Edge, bool Forward, Curve2 Pcurve)>();
        var knownLoops = new HashSet<int>();
        var flatLoops = new Dictionary<int, Dictionary<int, (ProfileLoop, (Point2D, Point2D))>>();
        for (var pi = 0; pi < region.Count; pi++)
        {
            var patch = region[pi];
            var face = builder.AddFace(shell, patch.Surface, patch.SameSense);
            // Carried holes after the patch's own loops, with a's edges (shared with copied faces) and p-curves.
            var hints = new Dictionary<int, (ProfileLoop, (Point2D, Point2D))>();
            for (var k = 0; k < patch.Carried.Count; k++)
            {
                var c = patch.Carried[k];
                hints[patch.Loops.Count + k] = (c.Flat, c.Box);
            }
            if (hints.Count > 0) flatLoops[face.Index] = hints;
            if (patch.Unchanged) unchanged.Add(face.Index);
            origins.Add([.. patch.Origins.OrderBy(o => o.Operand).ThenBy(o => o.Face.Index)]);
            for (var li = 0; li < patch.Loops.Count; li++)
            {
                var loopUses = new List<BodyBuilder.Use>();
                for (var ui = 0; ui < patch.Loops[li].Count; ui++)
                {
                    var i = indexOf[(pi, li, ui)];
                    var u = uses[i].Use;
                    if (partner[i] < 0 && partner[i] != int.MinValue)
                    {
                        loopUses.Add(new BodyBuilder.Use(EA(~partner[i]), u.From < u.To, u.Pcurve));
                        continue;
                    }
                    if (!edgeOf.TryGetValue(i, out var e))
                    {
                        var w = uses[partner[i]].Use;
                        var forward = u.From < u.To;
                        var (t0, t1) = forward ? (u.From, u.To) : (u.To, u.From);
                        var (s0, s1) = forward ? (start[i], end[i]) : (end[i], start[i]);
                        var d = u.Curve.Period is not null ? 2 * Math.PI * Math.Floor(t0 / (2 * Math.PI) + 1e-12) : 0;
                        var edge = builder.AddEdge(u.Curve, t0 - d, t1 - d, V(s0), V(s1));
                        var alpha = (w.From - w.To) / (u.To - u.From);
                        var beta = w.From - alpha * u.To;
                        edgeOf[i] = e = (edge, forward, Uv.Reparametrised(u.Pcurve, 1, d));
                        if (partner[i] != i) edgeOf[partner[i]] = (edge, !forward, Uv.Reparametrised(w.Pcurve, Math.Round(alpha), beta + alpha * d));
                    }
                    loopUses.Add(new BodyBuilder.Use(e.Edge, e.Forward, e.Pcurve));
                }
                builder.AddLoop(face, loopUses);
            }
            foreach (var c in patch.Carried)
                knownLoops.Add(builder.AddLoop(face, [.. a.CoedgesOf(c.Loop).Select(co => new BodyBuilder.Use(EA(a[co].Edge.Index), a[co].Forward, a[co].Pcurve))]).Index);
        }
        var body = builder.Build();
        // A corner where the region meets the copied faces, left with two edges on one curve: the full sewing joins them.
        var corners = ends.Where(vertexOfA.ContainsKey).Select(v => vertexOfA[v]).ToHashSet();
        var at = corners.ToDictionary(v => v, _ => new List<int>());
        for (var e = 0; e < body.Edges.Length; e++)
            foreach (var v in new[] { body.Edges[e].Start, body.Edges[e].End }.Distinct())
                if (at.TryGetValue(v, out var list)) list.Add(e);
        if (at.Values.Any(l => l.Count == 2 && SameCarrier(body.Edges[l[0]].Curve, body.Edges[l[1]].Curve))) return null;
        // The copied faces come first; the next boolean on the body takes their flat faces as they were.
        var flat = new FlatFace?[body.Faces.Length];
        for (var k = 0; k < copied.Count; k++) flat[k] = solid.Faces[copied[k]].Renumbered(e => edgeOfA[e.Index]);
        Solid.Carry(body, flat, flatLoops);
        return Checked((new Shaped(body, origins), unchanged), knownLoops);
    }

    // A curve by reference and a range to the snap: a's edge as a use of it runs.
    sealed class BoundaryKey : IEqualityComparer<(Curve3 Curve, double T0, double T1)>
    {
        public bool Equals((Curve3 Curve, double T0, double T1) x, (Curve3 Curve, double T0, double T1) y) =>
            ReferenceEquals(x.Curve, y.Curve) && Math.Abs(x.T0 - y.T0) <= 1e-9 && Math.Abs(x.T1 - y.T1) <= 1e-9;

        public int GetHashCode((Curve3 Curve, double T0, double T1) x) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(x.Curve);
    }

    static Shaped Checked((Shaped Shaped, HashSet<int> Unchanged) built, IReadOnlySet<int>? loops = null) => BodyCheck.Problems(built.Shaped.Body, built.Unchanged, loops) is [var first, ..]
        ? throw new BooleanFailure($"the result is not a valid solid: {first.Kind} at {first.Where}: {first.Message}")
        : built.Shaped;

    // Consecutive uses on one curve through a vertex where only their two edges meet, joined into one use.
    // `more`: the starts of uses outside the patches that leave their corners too (faces an edit copies).
    static List<Patch> JoinEdges(List<Patch> patches, IEnumerable<Point3D>? more = null)
    {
        var points = new Welded();
        int Vertex(Point3D p) => points[p];
        var leaving = new Dictionary<int, int>();
        foreach (var start in patches.SelectMany(p => p.Loops).SelectMany(l => l).Select(u => u.Start).Concat(more ?? []))
        {
            var v = Vertex(start);
            leaving[v] = leaving.GetValueOrDefault(v) + 1;
        }

        return [.. patches.Select(p => p with { Loops = [.. p.Loops.Select(Join)] })];

        IReadOnlyList<Use> Join(IReadOnlyList<Use> loop)
        {
            var list = loop.ToList();
            for (var changed = true; changed && list.Count > 1;)
            {
                changed = false;
                for (var i = 0; i < list.Count && list.Count > 1; i++)
                {
                    var (x, y) = (list[i], list[(i + 1) % list.Count]);
                    if (leaving[Vertex(x.End)] != 2 || Opposite(x, y) || !SameCarrier(x.Curve, y.Curve)) continue;
                    list[i] = Joined(x, y);
                    list.RemoveAt((i + 1) % list.Count);
                    changed = true;
                    break;
                }
            }
            return list;
        }
    }

    static bool SameCarrier(Curve3 a, Curve3 b) => (a, b) switch
    {
        (Line3 p, Line3 q) => p.Direction.Cross(q.Direction).Length < 1e-9 &&
                              (q.Origin - p.Origin - p.Direction * (q.Origin - p.Origin).Dot(p.Direction)).Length <= Snap,
        (Circle3 p, Circle3 q) => p.Frame.ZAxis.Cross(q.Frame.ZAxis).Length < 1e-9 && Near(p.Frame.Origin, q.Frame.Origin) &&
                                  Math.Abs(p.Radius - q.Radius) <= Snap,
        (Ellipse3 p, Ellipse3 q) => p.Frame.ZAxis.Cross(q.Frame.ZAxis).Length < 1e-9 && p.Frame.XAxis.Cross(q.Frame.XAxis).Length < 1e-9 &&
                                    Near(p.Frame.Origin, q.Frame.Origin) && Math.Abs(p.Major - q.Major) <= Snap && Math.Abs(p.Minor - q.Minor) <= Snap,
        _ => false,
    };

    // x then y as one use on x's curve and p-curve.
    static Use Joined(Use x, Use y)
    {
        var t = Intersections.Param(x.Curve, y.End);
        if (x.Curve.Period is not null)
        {
            var direction = Math.Sign(x.To - x.From);
            var turn = Intersections.Wrap(direction * (t - x.To));
            if (turn <= Snap) turn = 2 * Math.PI;
            t = x.To + direction * turn;
        }
        return x with { To = t };
    }
}

/// <summary>
/// Points taken once where they lie within Snap of each other: each point's index is that of the first point near
/// it, found among the points in the cells of a grid 2 Snap wide that its Snap neighbourhood reaches (at most two
/// along each axis).
/// </summary>
internal sealed class Welded
{
    const double Cell = 2 * Intersections.Snap;
    readonly Dictionary<(long, long, long), List<int>> cells = [];

    public List<Point3D> Points { get; } = [];

    /// <summary>The point within the snap of p, if there is one (none is added).</summary>
    public int? Find(Point3D p)
    {
        static (long, long) Span(double x) => ((long)Math.Floor((x - Intersections.Snap) / Cell), (long)Math.Floor((x + Intersections.Snap) / Cell));
        var ((x0, x1), (y0, y1), (z0, z1)) = (Span(p.X), Span(p.Y), Span(p.Z));
        for (var i = x0; i <= x1; i++)
        for (var j = y0; j <= y1; j++)
        for (var k = z0; k <= z1; k++)
            if (cells.TryGetValue((i, j, k), out var list))
                foreach (var n in list)
                    if ((Points[n] - p).Length <= Intersections.Snap) return n;
        return null;
    }

    public int this[Point3D p]
    {
        get
        {
            static (long, long) Span(double x) => ((long)Math.Floor((x - Intersections.Snap) / Cell), (long)Math.Floor((x + Intersections.Snap) / Cell));
            var ((x0, x1), (y0, y1), (z0, z1)) = (Span(p.X), Span(p.Y), Span(p.Z));
            var found = -1;
            for (var i = x0; i <= x1; i++)
            for (var j = y0; j <= y1; j++)
            for (var k = z0; k <= z1; k++)
                if (cells.TryGetValue((i, j, k), out var list))
                    foreach (var n in list)
                        if ((found < 0 || n < found) && (Points[n] - p).Length <= Intersections.Snap) found = n;
            if (found >= 0) return found;
            Points.Add(p);
            var key = ((long)Math.Floor(p.X / Cell), (long)Math.Floor(p.Y / Cell), (long)Math.Floor(p.Z / Cell));
            if (!cells.TryGetValue(key, out var own)) cells[key] = own = [];
            own.Add(Points.Count - 1);
            return Points.Count - 1;
        }
    }
}
