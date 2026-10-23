using Isoform.Document;

namespace Isoform.Sketching;

/// <summary>A region of a sketch: an outer loop and the loops cut out of it, as profiles; edges keep their entity ids.</summary>
public sealed record SketchRegion(Profile Outer, IReadOnlyList<Profile> Holes)
{
    /// <summary>The entity ids round the outer loop.</summary>
    public IEnumerable<string> Ids => Outer.Edges.Select(e => e.Id).Distinct();
}

/// <summary>
/// The regions a solved sketch encloses. Lines and arcs that are not construction geometry join where their ends meet
/// (within <c>1e-6</c> mm); chains that do not close are left out, a point where three or more meet is a problem.
/// Circles are loops of their own. Loops must not cross; a loop inside an odd number of others is a hole in the
/// innermost, inside an even number the outer loop of a region. <see cref="Chain"/> gives an open or closed chain as a
/// sweep's path.
/// </summary>
public static class SketchRegions
{
    const double Join = 1e-6;

    public static (IReadOnlyList<SketchRegion> Regions, string? Problem) Of(Sketch sketch, SketchSolution solution)
    {
        var at = solution.Points;
        var edges = sketch.Entities.Where(e => !e.Construction && e is SketchLine or SketchArc).ToList();
        var node = Nodes(edges, at);

        // Prune open chains: an end no other edge meets takes its edge with it, until none is left.
        var live = edges.ToList();
        while (true)
        {
            var degree = live.SelectMany(e => new[] { node[Ends(e).From], node[Ends(e).To] }).GroupBy(n => n).ToDictionary(g => g.Key, g => g.Count());
            if (degree.FirstOrDefault(d => d.Value > 2) is { Key: { } branch, Value: var count })
                return ([], $"point '{branch}' joins {count} edges; a profile point joins two");
            var open = live.Where(e => degree[node[Ends(e).From]] == 1 || degree[node[Ends(e).To]] == 1).ToList();
            if (open.Count == 0) break;
            live = live.Except(open).ToList();
        }

        // Trace the loops: from an unused edge to the other edge at its far node, until back.
        var loops = new List<Profile>();
        var used = new HashSet<string>();
        foreach (var first in live)
        {
            if (!used.Add(first.Id)) continue;
            var (start, cursor) = (node[Ends(first).From], node[Ends(first).To]);
            var steps = new List<ProfileEdge> { Step(first, forward: true, at) };
            var previous = first;
            while (cursor != start)
            {
                var next = live.First(e => e != previous && !used.Contains(e.Id) && (node[Ends(e).From] == cursor || node[Ends(e).To] == cursor));
                used.Add(next.Id);
                var forward = node[Ends(next).From] == cursor;
                steps.Add(Step(next, forward, at));
                cursor = forward ? node[Ends(next).To] : node[Ends(next).From];
                previous = next;
            }
            var p = at[Ends(first).From];
            loops.Add(new Profile([p.X, p.Y], steps));
        }
        foreach (var c in sketch.Entities.OfType<SketchCircle>().Where(c => !c.Construction))
        {
            var (centre, r) = (at[c.Center], solution.Radii[c.Id]);
            loops.Add(new Profile([centre.X + r, centre.Y], [new ProfileEdge(c.Id, [centre.X - r, centre.Y], 1), new ProfileEdge(c.Id, [centre.X + r, centre.Y], 1)]));
        }

        // Crossings, then nesting.
        var outlines = loops.Select(Outline).ToList();
        for (var i = 0; i < loops.Count; i++)
        {
            if (SelfCrossing(outlines[i])) return ([], $"the loop through {Names(loops[i])} crosses itself");
            for (var j = 0; j < i; j++)
                if (Crossing(outlines[i], outlines[j])) return ([], $"the loops through {Names(loops[i])} and {Names(loops[j])} cross");
        }
        var inside = loops.Select((_, i) => Enumerable.Range(0, loops.Count).Where(j => j != i && Inside(outlines[j], outlines[i][0])).ToList()).ToList();
        var regions = new List<SketchRegion>();
        for (var i = 0; i < loops.Count; i++)
        {
            if (inside[i].Count % 2 == 1) continue;
            // The holes: loops one level deeper whose innermost container is this one.
            var holes = Enumerable.Range(0, loops.Count).Where(j => inside[j].Count == inside[i].Count + 1 && inside[j].Contains(i)).Select(j => loops[j]).ToList();
            regions.Add(new SketchRegion(loops[i], holes));
        }
        return (regions, null);
    }

    /// <summary>
    /// The path a sweep follows: the chain of <paramref name="entities"/> (in any order), or the whole chain through
    /// <paramref name="from"/> among the lines and arcs that are construction geometry as it is. Ends join as in a
    /// region. An open chain runs from one free end, a closed one round from its first entity; a circle is a closed path
    /// on its own, two half arcs from its point at angle 0.
    /// </summary>
    public static (Profile? Path, bool Closed, string? Problem) Chain(Sketch sketch, SketchSolution solution, IReadOnlyList<string>? entities, string? from)
    {
        var at = solution.Points;
        var byId = sketch.Entities.ToDictionary(e => e.Id);
        var named = (entities ?? [from!]).Select(id => byId[id]).ToList();
        if (named.OfType<SketchCircle>().FirstOrDefault() is { } circle)
        {
            if (named.Count > 1) return (null, false, $"circle {circle.Id} is a closed path of its own and joins no other entity");
            var (centre, r) = (at[circle.Center], solution.Radii[circle.Id]);
            return (new Profile([centre.X + r, centre.Y], [new ProfileEdge(circle.Id, [centre.X - r, centre.Y], 1), new ProfileEdge(circle.Id, [centre.X + r, centre.Y], 1)]), true, null);
        }
        var chain = entities is not null ? named
            : sketch.Entities.Where(e => e is SketchLine or SketchArc && e.Construction == named[0].Construction).ToList();
        var node = Nodes(chain, at);
        var degree = chain.SelectMany(e => new[] { node[Ends(e).From], node[Ends(e).To] }).GroupBy(n => n).ToDictionary(g => g.Key, g => g.Count());
        if (entities is null)
        {
            // Only what is joined to the one entity named.
            var reached = new List<SketchEntity> { named[0] };
            for (var grew = true; grew;)
            {
                var touched = reached.SelectMany(e => new[] { node[Ends(e).From], node[Ends(e).To] }).ToHashSet();
                var more = chain.Where(e => !reached.Contains(e) && (touched.Contains(node[Ends(e).From]) || touched.Contains(node[Ends(e).To]))).ToList();
                reached.AddRange(more);
                grew = more.Count > 0;
            }
            chain = reached;
        }
        var ends = chain.SelectMany(e => new[] { node[Ends(e).From], node[Ends(e).To] }).ToHashSet();
        if (degree.FirstOrDefault(d => d.Value > 2 && ends.Contains(d.Key)) is { Key: { } branch, Value: var count })
            return (null, false, $"point '{branch}' joins {count} entities; a path does not branch");

        // From a free end, or round from the first entity; at each point on to the other entity there.
        var free = chain.FirstOrDefault(e => degree[node[Ends(e).From]] == 1 || degree[node[Ends(e).To]] == 1);
        var first = free ?? chain[0];
        var forward = free is null || degree[node[Ends(first).From]] == 1;
        var start = forward ? Ends(first).From : Ends(first).To;
        var cursor = node[forward ? Ends(first).To : Ends(first).From];
        var steps = new List<ProfileEdge> { Step(first, forward, at) };
        var used = new HashSet<SketchEntity> { first };
        while (chain.FirstOrDefault(e => !used.Contains(e) && (node[Ends(e).From] == cursor || node[Ends(e).To] == cursor)) is { } next)
        {
            used.Add(next);
            forward = node[Ends(next).From] == cursor;
            steps.Add(Step(next, forward, at));
            cursor = node[forward ? Ends(next).To : Ends(next).From];
        }
        if (used.Count < chain.Count) return (null, false, $"the path entities {string.Join(", ", chain.Select(e => e.Id))} do not form one chain");
        var p = at[start];
        return (new Profile([p.X, p.Y], steps), free is null, null);
    }

    static (string From, string To) Ends(SketchEntity e) => e is SketchLine l ? (l.From, l.To) : (((SketchArc)e).From, ((SketchArc)e).To);

    /// <summary>Nodes: the edges' end points by name, those within Join of each other one node.</summary>
    static Dictionary<string, string> Nodes(IEnumerable<SketchEntity> edges, IReadOnlyDictionary<string, (double X, double Y)> at)
    {
        var names = edges.SelectMany(e => new[] { Ends(e).From, Ends(e).To }).Distinct().Order(StringComparer.Ordinal).ToList();
        var node = names.ToDictionary(n => n, n => n);
        for (var i = 0; i < names.Count; i++)
        for (var j = 0; j < i; j++)
            if (Math.Abs(at[names[i]].X - at[names[j]].X) <= Join && Math.Abs(at[names[i]].Y - at[names[j]].Y) <= Join && node[names[i]] == names[i])
                node[names[i]] = node[names[j]];
        return node;
    }

    /// <summary>A line or arc as a profile edge to its far end, run forward (from → to) or backward.</summary>
    static ProfileEdge Step(SketchEntity e, bool forward, IReadOnlyDictionary<string, (double X, double Y)> at)
    {
        var (from, to) = Ends(e);
        var end = at[forward ? to : from];
        if (e is not SketchArc a) return new ProfileEdge(e.Id, [end.X, end.Y]);
        var (c, f, t) = (at[a.Center], at[a.From], at[a.To]);
        var sweep = Math.Atan2(t.Y - c.Y, t.X - c.X) - Math.Atan2(f.Y - c.Y, f.X - c.X);
        if (sweep <= 0) sweep += 2 * Math.PI;
        var bulge = Math.Tan(sweep / 4);
        return new ProfileEdge(e.Id, [end.X, end.Y], forward ? bulge : -bulge);
    }

    /// <summary>The regions asked for, each by entity ids of its outer loop; all of them when none are named.</summary>
    public static (IReadOnlyList<SketchRegion> Regions, string? Problem) Select(IReadOnlyList<SketchRegion> regions, IReadOnlyList<string[]>? wanted)
    {
        if (wanted is null) return regions.Count > 0 ? (regions, null) : ([], "the sketch encloses no region");
        var chosen = new List<SketchRegion>();
        foreach (var ids in wanted)
        {
            if (regions.FirstOrDefault(r => ids.All(r.Ids.Contains)) is not { } region) return ([], $"no region of the sketch has {string.Join(", ", ids)} round its outside");
            if (!chosen.Contains(region)) chosen.Add(region);
        }
        return (chosen, null);
    }

    static string Names(Profile loop) => string.Join(", ", loop.Edges.Select(e => e.Id).Distinct());

    /// <summary>The loop as a closed polyline, arcs in 64 pieces.</summary>
    static List<(double X, double Y)> Outline(Profile p)
    {
        var points = new List<(double, double)>();
        var (x, y) = (p.Start[0], p.Start[1]);
        foreach (var e in p.Edges)
        {
            var (tx, ty) = (e.To[0], e.To[1]);
            if (e.Bulge == 0) points.Add((x, y));
            else
            {
                // Centre from chord and bulge; the sweep is 4·atan(bulge), positive counter-clockwise.
                var sweep = 4 * Math.Atan(e.Bulge);
                var (mx, my, dx, dy) = ((x + tx) / 2, (y + ty) / 2, tx - x, ty - y);
                var offset = (1 - e.Bulge * e.Bulge) / (4 * e.Bulge);
                var (cx, cy) = (mx - dy * offset, my + dx * offset);
                var a0 = Math.Atan2(y - cy, x - cx);
                var r = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                for (var k = 0; k < 64; k++) points.Add((cx + r * Math.Cos(a0 + sweep * k / 64), cy + r * Math.Sin(a0 + sweep * k / 64)));
            }
            (x, y) = (tx, ty);
        }
        return points;
    }

    static bool Inside(List<(double X, double Y)> polygon, (double X, double Y) p)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var (a, b) = (polygon[i], polygon[j]);
            if (a.Y > p.Y != b.Y > p.Y && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    static bool Crossing(List<(double X, double Y)> a, List<(double X, double Y)> b)
    {
        for (var i = 0; i < a.Count; i++)
        for (var j = 0; j < b.Count; j++)
            if (Cross(a[i], a[(i + 1) % a.Count], b[j], b[(j + 1) % b.Count])) return true;
        return false;
    }

    static bool SelfCrossing(List<(double X, double Y)> a)
    {
        var n = a.Count;
        for (var i = 0; i < n; i++)
        for (var j = i + 2; j < n; j++)
            if ((j + 1) % n != i && Cross(a[i], a[(i + 1) % n], a[j], a[(j + 1) % n])) return true;
        return false;
    }

    // Two segments that cross or touch: separate loops must not meet at all (touching is a contact of zero width),
    // and a vertex of one landing exactly on the other must not hide a crossing.
    static bool Cross((double X, double Y) p, (double X, double Y) q, (double X, double Y) r, (double X, double Y) s)
    {
        static double Orient((double X, double Y) a, (double X, double Y) b, (double X, double Y) c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        static int Sign(double v) => v > Join ? 1 : v < -Join ? -1 : 0;
        var (d1, d2, d3, d4) = (Sign(Orient(r, s, p)), Sign(Orient(r, s, q)), Sign(Orient(p, q, r)), Sign(Orient(p, q, s)));
        if (d1 * d2 > 0 || d3 * d4 > 0) return false;
        if (d1 != 0 || d2 != 0 || d3 != 0 || d4 != 0) return true;
        // All on one line: they meet if their extents overlap.
        return Math.Max(Math.Min(p.X, q.X), Math.Min(r.X, s.X)) <= Math.Min(Math.Max(p.X, q.X), Math.Max(r.X, s.X)) + Join
            && Math.Max(Math.Min(p.Y, q.Y), Math.Min(r.Y, s.Y)) <= Math.Min(Math.Max(p.Y, q.Y), Math.Max(r.Y, s.Y)) + Join;
    }
}
