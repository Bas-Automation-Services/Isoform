using Isoform.Document;

namespace Isoform.Sketching;

public static partial class SketchEdits
{
    /// <summary>
    /// Deletes the piece of <paramref name="entity"/> between its intersections with the sketch's other entities that
    /// lies nearest (<paramref name="x"/>, <paramref name="y"/>), in sketch coordinates. A trimmed circle becomes an arc;
    /// the piece kept keeps the entity's id.
    /// </summary>
    /// <remarks>
    /// Only geometry that is not construction cuts or is cut, as for regions. A line or arc keeps its id on the piece at
    /// its From end, a piece beyond the deleted one is new (<c>id-2</c>), held on the kept one's line or circle. A cut
    /// at an end of the entity that made it is that point; elsewhere it is a new point (<c>id-other</c>) held on each
    /// entity that cut there. Dropped, and listed: a line's length, equal and midpoint constraints (its ends moved),
    /// constraints on end points nothing uses any more, and <c>on</c> constraints whose point became an end of the
    /// entity. An entity that crosses nothing is not trimmed (that is a delete), nor a circle crossed only once.
    /// </remarks>
    public static SketchEdit Trim(Sketch sketch, string entity, double x, double y)
    {
        var e = sketch.Entities.FirstOrDefault(k => k.Id == entity) ?? throw new ArgumentException($"sketch '{sketch.Id}' has no entity '{entity}'");
        if (e.Construction) throw new ArgumentException($"'{entity}' is construction geometry, which trim does not cut");
        var curve = Curve.Of(sketch, e);
        var cuts = Trimming.Cuts(curve, SketchIntersections.Of(sketch, entity), e is SketchCircle);
        if (cuts.Count == 0) throw new ArgumentException($"'{entity}' crosses nothing else in the sketch, so there is nothing to trim it to; delete it instead");
        if (e is SketchCircle && cuts.Count == 1) throw new ArgumentException($"circle '{entity}' meets the rest of the sketch in one point only; trimming a circle takes two");

        // Piece k runs from bounds[k] to bounds[k + 1]: along a line or arc from its start, round a circle from its first cut.
        double[] bounds = e is SketchCircle ? [.. cuts.Select(c => c.At), cuts[0].At + 2 * Math.PI] : [0, .. cuts.Select(c => c.At), curve.End];
        var k = Enumerable.Range(0, bounds.Length - 1).MinBy(i => curve.Distance((x, y), bounds[i], bounds[i + 1]));

        var taken = new HashSet<string>(sketch.Points.Keys.Concat(sketch.Entities.Select(n => n.Id)), StringComparer.Ordinal);
        string Fresh(string name)
        {
            var fresh = name;
            for (var i = 2; !taken.Add(fresh); i++) fresh = FormattableString.Invariant($"{name}-{i}");
            return fresh;
        }
        var points = sketch.Points.ToDictionary(p => p.Key, p => p.Value);
        var added = new List<string>();
        var extra = new List<SketchConstraint>();

        // An end of an entity that cut here is the cut point; otherwise a new one, held on every entity that cut here.
        string PointAt(Cut c)
        {
            var name = c.Others.SelectMany(o => Trimming.Ends(sketch, o)).FirstOrDefault(p => Math.Abs(points[p][0] - c.X) <= SketchIntersections.Touch && Math.Abs(points[p][1] - c.Y) <= SketchIntersections.Touch);
            if (name is null)
            {
                name = Fresh($"{entity}-{c.Others[0]}");
                points[name] = [c.X, c.Y];
                added.Add(name);
            }
            foreach (var o in c.Others.Where(o => !Trimming.Ends(sketch, o).Contains(name))) extra.Add(new("on", [name, o]));
            return name;
        }

        var pieces = new List<SketchEntity>();
        var gone = new List<string>();
        if (e is SketchCircle circle)
        {
            var (to, from) = (PointAt(cuts[k]), PointAt(cuts[(k + 1) % cuts.Count]));
            pieces.Add(new SketchArc(entity, circle.Center, from, to));
        }
        else
        {
            var (from, to) = e is SketchLine l ? (l.From, l.To) : (((SketchArc)e).From, ((SketchArc)e).To);
            SketchEntity Piece(string id, string f, string t) => e is SketchArc a ? new SketchArc(id, a.Center, f, t) : new SketchLine(id, f, t);
            var before = k > 0 ? PointAt(cuts[k - 1]) : null;
            var after = k < cuts.Count ? PointAt(cuts[k]) : null;
            if (before is not null) pieces.Add(Piece(entity, from, before));
            else gone.Add(from);
            if (after is null) gone.Add(to);
            else if (before is null) pieces.Add(Piece(entity, after, to));
            else
            {
                // Both sides stay: the far one is new, on the kept one's line or circle.
                var id = Fresh(entity);
                pieces.Add(Piece(id, after, to));
                added.Add(id);
                extra.Add(new("on", [after, entity]));
                if (e is SketchLine) extra.Add(new("on", [to, entity]));
            }
        }

        var entities = sketch.Entities.SelectMany(n => n.Id == entity ? pieces : [n]).ToList();
        var removed = gone.Where(p => !entities.Any(n => Trimming.Uses(n).Contains(p))).ToHashSet(StringComparer.Ordinal);
        bool EndOf(IEnumerable<SketchEntity> list, string p, string id) => list.Any(n => n.Id == id && Trimming.Ends(n).Contains(p));
        var kept = new List<SketchConstraint>();
        var dropped = new List<string>();
        for (var i = 0; i < sketch.Constraints.Count; i++)
        {
            var c = sketch.Constraints[i];
            var drop = c.Refs.Any(removed.Contains)
                || e is SketchLine && c.Kind is "length" or "equal" or "midpoint" && c.Refs.Contains(entity)
                || c.Kind == "on" && EndOf(entities, c.Refs[0], c.Refs[1]) && !EndOf(sketch.Entities, c.Refs[0], c.Refs[1]);
            if (drop) dropped.Add(SketchSolver.Describe(sketch, i));
            else kept.Add(c);
        }
        foreach (var c in extra)
            if (!kept.Any(o => o.Kind == c.Kind && o.Refs.SequenceEqual(c.Refs))) kept.Add(c);

        var trimmed = sketch with
        {
            Points = points.Where(p => !removed.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value),
            Entities = entities,
            Constraints = kept,
        };
        var solution = SketchSolver.Solve(trimmed);
        if (!solution.Solved) throw new ArgumentException($"the trimmed sketch does not solve: {string.Join("; ", solution.Problems.Select(p => p.Message))}");
        return new SketchEdit(SketchSolver.Settle(trimmed, solution), added, dropped);
    }
}

file sealed record Cut(double At, double X, double Y, List<string> Others);

file static class Trimming
{
    /// <summary>The hits inside the entity (not at its own ends), those at one place merged into one cut.</summary>
    public static List<Cut> Cuts(Curve curve, IReadOnlyList<SketchHit> hits, bool closed)
    {
        var cuts = new List<Cut>();
        foreach (var h in hits)
        {
            if (!closed && (h.At * curve.Scale <= SketchIntersections.Touch || (curve.End - h.At) * curve.Scale <= SketchIntersections.Touch)) continue;
            if (cuts.Count > 0 && (h.At - cuts[^1].At) * curve.Scale <= SketchIntersections.Touch) cuts[^1].Others.Add(h.Other);
            else cuts.Add(new Cut(h.At, h.X, h.Y, [h.Other]));
        }
        // Round a circle the last cut can be the first again.
        if (closed && cuts.Count > 1 && (cuts[0].At + 2 * Math.PI - cuts[^1].At) * curve.Scale <= SketchIntersections.Touch)
        {
            cuts[0].Others.AddRange(cuts[^1].Others);
            cuts.RemoveAt(cuts.Count - 1);
        }
        return cuts;
    }

    public static string[] Ends(SketchEntity e) => e switch
    {
        SketchLine l => [l.From, l.To],
        SketchArc a => [a.From, a.To],
        _ => [],
    };

    public static string[] Ends(Sketch sketch, string id) => Ends(sketch.Entities.First(n => n.Id == id));

    public static string[] Uses(SketchEntity e) => e switch
    {
        SketchLine l => [l.From, l.To],
        SketchArc a => [a.Center, a.From, a.To],
        SketchCircle c => [c.Center],
        _ => [],
    };
}
