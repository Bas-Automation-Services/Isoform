using BasAutomation.Geometry;
using Isoform.Geometry;

namespace Isoform.Modeling;

/// <summary>
/// A body that touches itself in a point: one of its corners on one of its edges or on the inside of one of its faces,
/// or two of its edges that cross. On a manifold solid the material round any point of its boundary is one piece and
/// so is the space outside it; at such a contact one of them is two pieces that meet only in the point, a contact of
/// zero thickness no face can be bounded at. The points tried are the corners and where two edges come within reach
/// of each other, each put exactly onto its faces' surfaces by Gauss-Newton steps; at one that lies on them within the
/// tolerance, the field round it says how many pieces meet there. Pieces that touch each other are
/// <see cref="PieceContacts"/>'.
/// </summary>
static class SelfContacts
{
    /// <summary>
    /// Where, the faces round each of the two pieces of material (or of space outside) that meet there, named as the
    /// topology names them (a surface with no face there by its own name), and their surfaces.
    /// </summary>
    public readonly record struct Contact(Point3D At, string[] One, string[] Other, int[] Owners);

    /// <param name="reach">How far the mesh may put a corner or an edge's chain from the exact one: two cells.</param>
    public static Contact? Find(TopologyBuilder.Result built, IReadOnlyList<Surface> surfaces, Field field, double tolerance, double reach)
    {
        var t = built.Topology;
        // Analytic surfaces have their own distance, a fillet's comes exactly from its field; blends and lattices have none.
        FieldSample? Eval(int o, Point3D p) => surfaces[o].Analytic ? new(surfaces[o].Distance(p), o, surfaces[o].Gradient(p))
            : surfaces[o].Kind == SurfaceKind.Fillet ? field.SampleSurface(o, p) : null;
        var tried = new List<Point3D>();
        Contact? Try(IReadOnlyCollection<int> faces, Point3D near)
        {
            if (faces.Any(f => built.FaceOwners[f].All(o => Eval(o, near) is null))) return null;
            var on = faces.Select(f => built.FaceOwners[f].Where(o => Eval(o, near) is not null).MinBy(o => Math.Abs(Eval(o, near)!.Value.Value))).Distinct().ToArray();
            if (Meet(on, Eval, near, tolerance, reach) is not { } c || tried.Any(x => (x - c).Length <= 10 * tolerance)) return null;
            tried.Add(c);
            if (Pieces(field, c, surfaces, Eval, tolerance) is not [var one, var other, ..]) return null;
            // Each piece's surfaces as faces: those of the faces tried first, then of the faces whose edges pass by, then any.
            var local = faces.Concat(Enumerable.Range(0, t.Edges.Count).Where(e => built.EdgePoints[e].Any(x => (x - c).Length <= reach))
                .SelectMany(e => new[] { t.Edges[e].FaceA, t.Edges[e].FaceB })).Concat(Enumerable.Range(0, t.Faces.Count)).Distinct().ToList();
            // A surface with no face (a cut that took nothing away, or a face lost below the cells) only where a piece has no other.
            int Face(int o) => local.FirstOrDefault(f => built.FaceOwners[f].Contains(o), -1);
            List<int> Shown(List<int> owners) => owners.Any(o => Face(o) >= 0) ? [.. owners.Where(o => Face(o) >= 0)] : owners;
            string[] Names(List<int> owners) => [.. Shown(owners).OrderBy(o => Face(o) < 0 ? int.MaxValue : Face(o))
                .Select(o => Face(o) >= 0 ? string.Join("+", t.Faces[Face(o)].Sources) : surfaces[o].Source).Distinct()];
            return new Contact(c, Names(one), Names(other), [.. Shown(one), .. Shown(other)]);
        }

        for (var v = 0; v < t.Vertices.Count; v++)
            if (Try(t.Vertices[v].Faces, built.VertexPoints[v][0]) is { } contact) return contact;

        // Edges of one piece whose chains come within reach: from their nearest points.
        var cells = new Dictionary<(long, long, long), List<(int Edge, Point3D At)>>();
        (long, long, long) Cell(Point3D p) => ((long)Math.Floor(p.X / reach), (long)Math.Floor(p.Y / reach), (long)Math.Floor(p.Z / reach));
        for (var e = 0; e < t.Edges.Count; e++)
        {
            // Points half the reach apart are close enough to start from.
            var (chain, kept) = (built.EdgePoints[e], new Point3D());
            for (var i = 0; i < chain.Length; i++)
            {
                if (i > 0 && i < chain.Length - 1 && (chain[i] - kept).Length < reach / 2) continue;
                kept = chain[i];
                if (!cells.TryGetValue(Cell(kept), out var list)) cells[Cell(kept)] = list = [];
                list.Add((e, kept));
            }
        }
        var nearest = new Dictionary<(int, int), (double Gap, Point3D Mid)>();
        void Near((int Edge, Point3D At) x, (int Edge, Point3D At) y)
        {
            if (x.Edge == y.Edge) return;
            var (e, f, p, q) = x.Edge < y.Edge ? (x.Edge, y.Edge, x.At, y.At) : (y.Edge, x.Edge, y.At, x.At);
            if ((q - p).Length is var gap && gap <= reach && (!nearest.TryGetValue((e, f), out var best) || gap < best.Gap)) nearest[(e, f)] = (gap, p + (q - p) * 0.5);
        }
        // Each cell against itself and the half of its neighbours that come after it.
        foreach (var ((i, j, k), list) in cells)
        {
            for (var a = 0; a < list.Count; a++)
            for (var b = a + 1; b < list.Count; b++) Near(list[a], list[b]);
            foreach (var (di, dj, dk) in After)
                if (cells.TryGetValue((i + di, j + dj, k + dk), out var other))
                    foreach (var x in list)
                    foreach (var y in other) Near(x, y);
        }
        foreach (var ((e, f), (_, mid)) in nearest)
        {
            var (a, b) = (t.Edges[e], t.Edges[f]);
            if (t.Faces[a.FaceA].Body != t.Faces[b.FaceA].Body) continue;
            if (Try(new HashSet<int> { a.FaceA, a.FaceB, b.FaceA, b.FaceB }, mid) is { } contact) return contact;
        }
        return null;
    }

    static readonly (int, int, int)[] After =
        [.. from i in new[] { -1, 0, 1 } from j in new[] { -1, 0, 1 } from k in new[] { -1, 0, 1 } where (i, j, k).CompareTo((0, 0, 0)) > 0 select (i, j, k)];

    /// <summary>
    /// The point near <paramref name="p"/> on every surface in <paramref name="on"/>, by Gauss-Newton steps; null where
    /// their normals are (nearly) dependent, or it does not converge onto all of them within the tolerance and reach.
    /// </summary>
    static Point3D? Meet(int[] on, Func<int, Point3D, FieldSample?> eval, Point3D p, double tolerance, double reach)
    {
        var x = p;
        for (var it = 0; it < 30; it++)
        {
            // The normal equations JᵀJ·step = Jᵀr, solved by Cramer's rule.
            var (m, r) = (new double[3, 3], new double[3]);
            foreach (var o in on)
            {
                if (eval(o, x) is not { } s) return null;
                var g = new[] { s.Gradient.X, s.Gradient.Y, s.Gradient.Z };
                for (var i = 0; i < 3; i++)
                {
                    r[i] += g[i] * s.Value;
                    for (var j = 0; j < 3; j++) m[i, j] += g[i] * g[j];
                }
            }
            var (c0, c1, c2) = (new Vector3D(m[0, 0], m[1, 0], m[2, 0]), new Vector3D(m[0, 1], m[1, 1], m[2, 1]), new Vector3D(m[0, 2], m[1, 2], m[2, 2]));
            var rv = new Vector3D(r[0], r[1], r[2]);
            var det = c0.Dot(c1.Cross(c2));
            if (det < 1e-6) return null;
            var step = new Vector3D(rv.Dot(c1.Cross(c2)), c0.Dot(rv.Cross(c2)), c0.Dot(c1.Cross(rv))) / det;
            x -= step;
            if (step.Length <= tolerance / 100) break;
        }
        return (x - p).Length <= reach && on.All(o => eval(o, x) is { } s && Math.Abs(s.Value) <= tolerance) ? x : null;
    }

    /// <summary>
    /// The pieces of material, or else of space outside, that meet at <paramref name="c"/>, each as the surfaces round
    /// it; fewer than two where the solid is manifold there, or where it cannot be told. Close by, every surface through
    /// c is its tangent plane and they cut the sphere of directions round c into cells, each wholly inside or outside
    /// the solid, which a probe of the field a short way along one of its directions tells. Every cell has a corner
    /// where two of the planes' great circles cross, and is found from one beside it; cells whose signs differ by one
    /// plane share an arc of its circle. Null (cannot tell) where a surface passes close by but not through c, two
    /// planes are too close to tell apart, or a probe would come too near a plane.
    /// </summary>
    static List<int>[]? Pieces(Field field, Point3D c, IReadOnlyList<Surface> surfaces, Func<int, Point3D, FieldSample?> eval, double tolerance)
    {
        // Probes this far from c, at least ten tolerances from every plane: the planes stand for their surfaces there
        // for any radius of curvature above 0.005 mm.
        const double Probe = 1e-4;
        var round = new Vector3D(2 * Probe, 2 * Probe, 2 * Probe);
        field = field.Simplify(new Box3D(c - round, c + round));
        var planes = new List<(Vector3D Normal, List<(int Owner, bool Along)> Owners)>();
        for (var o = 0; o < surfaces.Count; o++)
        {
            var s = surfaces[o].Analytic ? eval(o, c) : field.SampleSurface(o, c);
            if (s is null || Math.Abs(s.Value.Value) > 2 * Probe) continue;
            if (Math.Abs(s.Value.Value) > tolerance || surfaces[o].Kind is SurfaceKind.Blend or SurfaceKind.Lattice) return null;
            var n = s.Value.Gradient.Normalized();
            if (planes.FindIndex(x => x.Normal.Cross(n).Length <= 1e-9) is var same and >= 0) planes[same].Owners.Add((o, planes[same].Normal.Dot(n) > 0));
            else if (planes.Any(x => x.Normal.Cross(n).Length < 0.02) || planes.Count == 12) return null;
            else planes.Add((n, [(o, true)]));
        }
        if (planes.Count < 2) return null;

        var inside = new Dictionary<int, bool>();
        int Signs(Vector3D d) => Enumerable.Range(0, planes.Count).Sum(i => planes[i].Normal.Dot(d) > 0 ? 1 << i : 0);
        for (var i = 0; i < planes.Count; i++)
        for (var j = i + 1; j < planes.Count; j++)
        foreach (var v in new[] { planes[i].Normal.Cross(planes[j].Normal).Normalized(), planes[j].Normal.Cross(planes[i].Normal).Normalized() })
        {
            // The circles through this corner, as rays out of it in its tangent plane; a probe between each two.
            var through = planes.Where(x => Math.Abs(x.Normal.Dot(v)) <= 1e-9).Select(x => v.Cross(x.Normal).Normalized()).ToList();
            var clear = planes.Select(x => Math.Abs(x.Normal.Dot(v))).Where(d => d > 1e-9).Append(1).Min();
            var (e1, e2) = (through[0], v.Cross(through[0]));
            var rays = through.SelectMany(r => new[] { Math.Atan2(r.Dot(e2), r.Dot(e1)), Math.Atan2(-r.Dot(e2), -r.Dot(e1)) }).Order().ToList();
            var away = Math.Min(0.3, Math.Asin(clear) / 2);
            for (var k = 0; k < rays.Count; k++)
            {
                var mid = (rays[k] + (k + 1 < rays.Count ? rays[k + 1] : rays[0] + 2 * Math.PI)) / 2;
                var d = (v * Math.Cos(away) + (e1 * Math.Cos(mid) + e2 * Math.Sin(mid)) * Math.Sin(away)).Normalized();
                if (planes.Any(x => Math.Abs(x.Normal.Dot(d)) * Probe < 10 * tolerance)) return null;
                var signs = Signs(d);
                var material = field.Value(c + d * Probe) < 0;
                if (inside.TryGetValue(signs, out var seen) && seen != material) return null;
                inside[signs] = material;
            }
        }

        // Cells of one kind that share an arc are one piece.
        var cells = inside.Keys.ToList();
        var pieces = new UnionFind(cells.Count);
        for (var a = 0; a < cells.Count; a++)
        for (var b = a + 1; b < cells.Count; b++)
            if (inside[cells[a]] == inside[cells[b]] && int.PopCount(cells[a] ^ cells[b]) == 1) pieces.Union(a, b);
        foreach (var material in new[] { true, false })
        {
            var groups = Enumerable.Range(0, cells.Count).Where(a => inside[cells[a]] == material).GroupBy(pieces.Find).ToList();
            if (groups.Count < 2) continue;
            // A piece's surfaces: those across which one of its cells borders the other kind, of the surfaces there
            // (coincident ones bound opposite sides) the ones with the piece on their own side.
            IEnumerable<int> Round(int a, int i)
            {
                var own = planes[i].Owners.Where(x => x.Along == ((cells[a] >> i & 1) == 0) == material).Select(x => x.Owner).ToList();
                return own.Count > 0 ? own : planes[i].Owners.Select(x => x.Owner);
            }
            return [.. groups.Select(g => g.SelectMany(a => Enumerable.Range(0, planes.Count)
                .Where(i => inside.TryGetValue(cells[a] ^ (1 << i), out var m) && m != material).SelectMany(i => Round(a, i))).Distinct().ToList())];
        }
        return null;
    }
}
