using Isoform.Document;

namespace Isoform.Sketching;

/// <summary>
/// A sketch as the solver sees it: the unknowns (point coordinates, then circle radii, in name order), and for each
/// constraint its residuals, each a function of a few unknowns, zero where the constraint holds, in millimetres.
/// Built-in rows hold every arc's two ends at one radius and every projected entity on its carrier, or, without one,
/// where it was stored.
/// </summary>
sealed class Model
{
    readonly record struct Residual(int[] Vars, Func<double[], double> F);

    readonly Sketch _sketch;
    readonly List<Residual>[] _constraints;
    readonly List<Residual> _built = [];

    public Dictionary<string, int> PointIndex { get; } = [];
    public Dictionary<string, int> RadiusIndex { get; } = [];
    public int Unknowns { get; }

    public Model(Sketch sketch, IReadOnlyDictionary<string, SketchCarrier>? carriers = null)
    {
        _sketch = sketch;
        var n = 0;
        foreach (var name in sketch.Points.Keys.Order(StringComparer.Ordinal)) (PointIndex[name], n) = (n, n + 2);
        foreach (var c in sketch.Entities.OfType<SketchCircle>().OrderBy(c => c.Id, StringComparer.Ordinal)) (RadiusIndex[c.Id], n) = (n, n + 1);
        Unknowns = n;
        var start = Start();
        foreach (var a in sketch.Entities.OfType<SketchArc>())
        {
            var (c, f, t) = (PointIndex[a.Center], PointIndex[a.From], PointIndex[a.To]);
            _built.Add(new([c, c + 1, f, f + 1, t, t + 1], x => Dist(x, t, c) - Dist(x, f, c)));
        }
        foreach (var e in sketch.Entities.Where(e => e.Project is not null)) Hold(e, carriers?.GetValueOrDefault(e.Id), start);
        _constraints = sketch.Constraints.Select(c => Build(c, start)).ToArray();
    }

    /// <summary>The stored coordinates and radii as a vector of unknowns.</summary>
    public double[] Start()
    {
        var x = new double[Unknowns];
        foreach (var (name, i) in PointIndex) (x[i], x[i + 1]) = (_sketch.Points[name][0], _sketch.Points[name][1]);
        foreach (var c in _sketch.Entities.OfType<SketchCircle>()) x[RadiusIndex[c.Id]] = c.Radius;
        return x;
    }

    public IReadOnlyDictionary<string, (double X, double Y)> Points(double[] x) => PointIndex.ToDictionary(p => p.Key, p => (x[p.Value], x[p.Value + 1]));

    public IReadOnlyDictionary<string, double> Radii(double[] x) => RadiusIndex.ToDictionary(p => p.Key, p => x[p.Value]);

    /// <summary>Points and circles that the null space can move.</summary>
    public IReadOnlySet<string> Free(List<double[]> nullspace)
    {
        bool Moves(params int[] vars) => nullspace.Sum(v => vars.Sum(i => v[i] * v[i])) > 1e-12;
        var free = new HashSet<string>(PointIndex.Where(p => Moves(p.Value, p.Value + 1)).Select(p => p.Key));
        free.UnionWith(RadiusIndex.Where(r => Moves(r.Value)).Select(r => r.Key));
        return free;
    }

    public int Rows(int[] constraints) => _built.Count + constraints.Sum(c => _constraints[c].Count);

    public double[] Residuals(int[] constraints, double[] x) => [.. Selected(constraints).Select(r => r.F(x))];

    /// <summary>Central differences over each residual's own unknowns.</summary>
    public double[,] Jacobian(int[] constraints, double[] x)
    {
        var rows = Selected(constraints).ToList();
        var j = new double[rows.Count, Unknowns];
        var probe = (double[])x.Clone();
        for (var row = 0; row < rows.Count; row++)
            foreach (var v in rows[row].Vars)
            {
                var h = 1e-6 * Math.Max(1, Math.Abs(x[v]));
                probe[v] = x[v] + h;
                var up = rows[row].F(probe);
                probe[v] = x[v] - h;
                var down = rows[row].F(probe);
                probe[v] = x[v];
                j[row, v] += (up - down) / (2 * h);
            }
        return j;
    }

    IEnumerable<Residual> Selected(int[] constraints) => _built.Concat(constraints.SelectMany(c => _constraints[c])).Concat(_pull);

    readonly List<Residual> _pull = [];

    /// <summary>
    /// A weak pull of a point towards a target: weighted far below the constraints, so a solve keeps them and moves the
    /// point as close to the target as they allow. Null removes it.
    /// </summary>
    public void Pull((string Point, double X, double Y)? target)
    {
        _pull.Clear();
        if (target is not { } t || !PointIndex.TryGetValue(t.Point, out var p)) return;
        const double weight = 1e-4;
        _pull.Add(new([p], x => weight * (x[p] - t.X)));
        _pull.Add(new([p + 1], x => weight * (x[p + 1] - t.Y)));
    }

    /// <summary>
    /// A projected entity on its carrier: a line's ends on the line, an arc's or circle's centre on the circle's centre
    /// and its radius the circle's. Without a carrier of its kind (the reference did not resolve) its points and radius
    /// stay where they were stored.
    /// </summary>
    void Hold(SketchEntity e, SketchCarrier? carrier, double[] start)
    {
        void Stay(int v) => _built.Add(new([v], x => x[v] - start[v]));
        switch (e, carrier)
        {
            case (SketchLine l, CarrierLine k):
                foreach (var p in new[] { PointIndex[l.From], PointIndex[l.To] })
                    _built.Add(new([p, p + 1], x => (x[p + 1] - k.Y) * k.Dx - (x[p] - k.X) * k.Dy));
                break;
            case (SketchArc or SketchCircle, CarrierCircle k):
                var (c, radius, vars) = Round(e.Id);
                _built.Add(new([c], x => x[c] - k.X));
                _built.Add(new([c + 1], x => x[c + 1] - k.Y));
                _built.Add(new([.. vars, .. Of(c)], x => radius(x) - k.Radius));
                break;
            default:
                var points = e switch { SketchLine l => [l.From, l.To], SketchArc a => [a.Center, a.From, a.To], SketchCircle k => new[] { k.Center }, _ => [] };
                foreach (var p in points.Select(q => PointIndex[q]).Distinct())
                {
                    Stay(p);
                    Stay(p + 1);
                }
                if (e is SketchCircle) Stay(RadiusIndex[e.Id]);
                break;
        }
    }

    // ---- Geometry over the unknowns ----

    static double Dist(double[] x, int p, int q) => Math.Sqrt(Sq(x[p] - x[q]) + Sq(x[p + 1] - x[q + 1]));

    static double Sq(double v) => v * v;

    /// <summary>A line as its two point indices.</summary>
    (int A, int B) Line(string id) => _sketch.Entities.First(e => e.Id == id) is SketchLine l ? (PointIndex[l.From], PointIndex[l.To]) : throw new InvalidOperationException(id);

    /// <summary>An arc or circle: its centre index, its radius as a function, and the unknowns that radius reads.</summary>
    (int C, Func<double[], double> R, int[] Vars) Round(string id) => _sketch.Entities.First(e => e.Id == id) switch
    {
        SketchArc a => (PointIndex[a.Center], x => Dist(x, PointIndex[a.From], PointIndex[a.Center]), Of(PointIndex[a.From], PointIndex[a.Center])),
        SketchCircle c => (PointIndex[c.Center], x => x[RadiusIndex[c.Id]], [RadiusIndex[c.Id]]),
        _ => throw new InvalidOperationException(id),
    };

    static int[] Of(params int[] points) => [.. points.SelectMany(p => new[] { p, p + 1 }).Distinct()];

    // Signed distance from point p to the line through a and b: positive on its left.
    static double Side(double[] x, int p, int a, int b) =>
        ((x[b] - x[a]) * (x[p + 1] - x[a + 1]) - (x[b + 1] - x[a + 1]) * (x[p] - x[a])) / Dist(x, b, a);

    static double Sign(double v) => v < 0 ? -1 : 1;

    /// <summary>
    /// The end of an arc that is also an end of the other entity (the same point, or where it starts within 1e-6 mm),
    /// as the index of the arc's end point; null when they share none.
    /// </summary>
    int? Shared(string e1, string e2, double[] start)
    {
        string[] Ends(string id) => _sketch.Entities.First(e => e.Id == id) switch
        {
            SketchLine l => [l.From, l.To],
            SketchArc a => [a.From, a.To],
            _ => [],
        };
        var (arc, other) = _sketch.Entities.First(e => e.Id == e1) is SketchArc ? (e1, e2) : (e2, e1);
        if (_sketch.Entities.First(e => e.Id == arc) is not SketchArc) return null;
        foreach (var p in Ends(arc))
            foreach (var q in Ends(other))
                if (p == q || Math.Abs(start[PointIndex[p]] - start[PointIndex[q]]) <= 1e-6 && Math.Abs(start[PointIndex[p] + 1] - start[PointIndex[q] + 1]) <= 1e-6)
                    return PointIndex[p];
        return null;
    }

    bool IsLine(string r) => _sketch.Entities.Any(e => e.Id == r && e is SketchLine);

    bool IsRound(string r) => _sketch.Entities.Any(e => e.Id == r && e is SketchArc or SketchCircle);

    bool IsPoint(string r) => _sketch.Points.ContainsKey(r) && _sketch.Entities.All(e => e.Id != r);

    // ---- Constraints ----

    List<Residual> Build(SketchConstraint c, double[] start)
    {
        var r = c.Refs;
        var v = c.Value ?? 0;
        var rows = new List<Residual>();
        // Each unknown once: the Jacobian adds a column per listed unknown.
        void Add(int[] vars, Func<double[], double> f) => rows.Add(new([.. vars.Distinct()], f));

        switch (c.Kind)
        {
            case "coincident":
            {
                var (p, q) = (PointIndex[r[0]], PointIndex[r[1]]);
                Add(Of(p, q), x => x[q] - x[p]);
                Add(Of(p, q), x => x[q + 1] - x[p + 1]);
                break;
            }
            case "horizontal" or "vertical":
            {
                var (a, b) = r.Length == 1 ? Line(r[0]) : (PointIndex[r[0]], PointIndex[r[1]]);
                var axis = c.Kind == "horizontal" ? 1 : 0;
                Add(Of(a, b), x => x[b + axis] - x[a + axis]);
                break;
            }
            case "parallel" or "perpendicular":
            {
                var ((a1, b1), (a2, b2)) = (Line(r[0]), Line(r[1]));
                Add(Of(a1, b1, a2, b2), x =>
                {
                    var (ux, uy, wx, wy) = (x[b1] - x[a1], x[b1 + 1] - x[a1 + 1], x[b2] - x[a2], x[b2 + 1] - x[a2 + 1]);
                    var w = Math.Sqrt(wx * wx + wy * wy);
                    return (c.Kind == "parallel" ? ux * wy - uy * wx : ux * wx + uy * wy) / w;
                });
                break;
            }
            // Tangent where an arc ends on the other entity: the radius there is square to the line, or runs through
            // both centres. Distance-equals-radius would be a double root there, with no first-order information.
            case "tangent" when Shared(r[0], r[1], start) is { } p && IsLine(r[0]) != IsLine(r[1]):
            {
                var (line, round) = IsLine(r[0]) ? (r[0], r[1]) : (r[1], r[0]);
                var (a, b) = Line(line);
                var centre = Round(round).C;
                Add(Of(a, b, centre, p), x => ((x[b] - x[a]) * (x[p] - x[centre]) + (x[b + 1] - x[a + 1]) * (x[p + 1] - x[centre + 1])) / Dist(x, b, a));
                break;
            }
            case "tangent" when Shared(r[0], r[1], start) is { } p:
            {
                var (c1, c2) = (Round(r[0]).C, Round(r[1]).C);
                Add(Of(c1, c2, p), x => ((x[p] - x[c1]) * (x[p + 1] - x[c2 + 1]) - (x[p + 1] - x[c1 + 1]) * (x[p] - x[c2])) / Math.Max(Dist(x, p, c2), 1e-12));
                break;
            }
            case "tangent" when IsLine(r[0]) != IsLine(r[1]):
            {
                var (line, round) = IsLine(r[0]) ? (r[0], r[1]) : (r[1], r[0]);
                var (a, b) = Line(line);
                var (centre, radius, vars) = Round(round);
                var side = Sign(Side(start, centre, a, b));
                Add([.. Of(a, b, centre), .. vars], x => Side(x, centre, a, b) - side * radius(x));
                break;
            }
            case "tangent":
            {
                var ((c1, r1, v1), (c2, r2, v2)) = (Round(r[0]), Round(r[1]));
                var d0 = Dist(start, c1, c2);
                var external = Math.Abs(d0 - (r1(start) + r2(start))) <= Math.Abs(d0 - Math.Abs(r1(start) - r2(start)));
                var sign = Sign(r1(start) - r2(start));
                Add([.. Of(c1, c2), .. v1, .. v2], x => Dist(x, c1, c2) - (external ? r1(x) + r2(x) : sign * (r1(x) - r2(x))));
                break;
            }
            case "equal" when IsLine(r[0]):
            {
                var ((a1, b1), (a2, b2)) = (Line(r[0]), Line(r[1]));
                Add(Of(a1, b1, a2, b2), x => Dist(x, b1, a1) - Dist(x, b2, a2));
                break;
            }
            case "equal":
            {
                var ((_, r1, v1), (_, r2, v2)) = (Round(r[0]), Round(r[1]));
                Add([.. v1, .. v2, .. Of(Round(r[0]).C, Round(r[1]).C)], x => r1(x) - r2(x));
                break;
            }
            case "midpoint":
            {
                var p = PointIndex[r[0]];
                var (a, b) = Line(r[1]);
                Add(Of(p, a, b), x => x[p] - (x[a] + x[b]) / 2);
                Add(Of(p, a, b), x => x[p + 1] - (x[a + 1] + x[b + 1]) / 2);
                break;
            }
            case "symmetric":
            {
                var (p, q) = (PointIndex[r[0]], PointIndex[r[1]]);
                var (a, b) = Line(r[2]);
                // The middle of pq lies on the line, and pq is square to it.
                Add(Of(p, q, a, b), x =>
                {
                    var (mx, my) = ((x[p] + x[q]) / 2, (x[p + 1] + x[q + 1]) / 2);
                    return ((x[b] - x[a]) * (my - x[a + 1]) - (x[b + 1] - x[a + 1]) * (mx - x[a])) / Dist(x, b, a);
                });
                Add(Of(p, q, a, b), x => ((x[q] - x[p]) * (x[b] - x[a]) + (x[q + 1] - x[p + 1]) * (x[b + 1] - x[a + 1])) / Dist(x, b, a));
                break;
            }
            case "on" when IsLine(r[1]):
            {
                var p = PointIndex[r[0]];
                var (a, b) = Line(r[1]);
                Add(Of(p, a, b), x => Side(x, p, a, b));
                break;
            }
            case "on":
            {
                var p = PointIndex[r[0]];
                var (centre, radius, vars) = Round(r[1]);
                Add([.. Of(p, centre), .. vars], x => Dist(x, p, centre) - radius(x));
                break;
            }
            case "fix":
            {
                var p = PointIndex[r[0]];
                var (fx, fy) = (start[p], start[p + 1]);
                Add(Of(p), x => x[p] - fx);
                Add(Of(p), x => x[p + 1] - fy);
                break;
            }
            case "length":
            {
                var (a, b) = Line(r[0]);
                Add(Of(a, b), x => Dist(x, b, a) - v);
                break;
            }
            case "distance" when r.Length == 2 && IsPoint(r[0]) && IsPoint(r[1]):
            {
                var (p, q) = (PointIndex[r[0]], PointIndex[r[1]]);
                Add(Of(p, q), x => Dist(x, q, p) - v);
                break;
            }
            case "distance":
            {
                // A point and a line, or two lines (the second's start from the first): on the side it starts on.
                var (line, p) = IsLine(r[1]) && IsPoint(r[0]) ? (r[1], PointIndex[r[0]]) : IsLine(r[0]) && IsPoint(r[1]) ? (r[0], PointIndex[r[1]]) : (r[0], Line(r[1]).A);
                var (a, b) = Line(line);
                var side = Sign(Side(start, p, a, b));
                Add(Of(p, a, b), x => side * Side(x, p, a, b) - v);
                break;
            }
            case "horizontal-distance" or "vertical-distance":
            {
                var (p, q) = (PointIndex[r[0]], PointIndex[r[1]]);
                var axis = c.Kind == "horizontal-distance" ? 0 : 1;
                var sign = Sign(start[q + axis] - start[p + axis]);
                Add(Of(p, q), x => sign * (x[q + axis] - x[p + axis]) - v);
                break;
            }
            case "radius" or "diameter":
            {
                var (centre, radius, vars) = Round(r[0]);
                var k = c.Kind == "radius" ? 1 : 2;
                Add([.. vars, .. Of(centre)], x => k * radius(x) - v);
                break;
            }
            case "angle":
            {
                var ((a1, b1), (a2, b2)) = (Line(r[0]), Line(r[1]));
                var (cos, sin) = (Math.Cos(v * Math.PI / 180), Math.Sin(v * Math.PI / 180));
                // The first line turned by the angle runs along the second.
                Add(Of(a1, b1, a2, b2), x =>
                {
                    var (ux, uy) = (x[b1] - x[a1], x[b1 + 1] - x[a1 + 1]);
                    var (tx, ty) = (ux * cos - uy * sin, ux * sin + uy * cos);
                    var (wx, wy) = (x[b2] - x[a2], x[b2 + 1] - x[a2 + 1]);
                    return (tx * wy - ty * wx) / Math.Sqrt(wx * wx + wy * wy);
                });
                break;
            }
        }
        return rows;
    }

    // ---- Validation ----

    static readonly string[] Kinds = [.. SketchConstraint.Geometric, .. SketchConstraint.Dimensions];

    /// <summary>What is wrong with the sketch's structure, before any solving; null when it can be solved.</summary>
    public static string? Problem(Sketch s)
    {
        var entities = s.Entities.ToDictionary(e => e.Id, e => e, StringComparer.Ordinal);
        if (entities.Count != s.Entities.Count) return "entity ids must be unique";
        if (s.Points.Keys.FirstOrDefault(entities.ContainsKey) is { } both) return $"'{both}' is both a point and an entity";
        if (s.Points.Keys.Concat(entities.Keys).FirstOrDefault(n => n.Length == 0 || n.Contains('.') || n.Contains('/') || n is "start" or "end") is { } bad)
            return $"'{bad}' is not a valid name: names may not be empty, contain '.' or '/', or be 'start' or 'end'";
        foreach (var e in s.Entities)
        {
            var points = e switch { SketchLine l => [l.From, l.To], SketchArc a => [a.Center, a.From, a.To], SketchCircle c => new[] { c.Center }, _ => [] };
            if (points.FirstOrDefault(p => !s.Points.ContainsKey(p)) is { } missing) return $"entity '{e.Id}' uses '{missing}', which is not a point";
            if (e is SketchLine { From: var f, To: var t } && f == t) return $"line '{e.Id}' starts and ends at the same point";
            if (e is SketchCircle { Radius: <= 0 }) return $"circle '{e.Id}' needs a radius above 0";
            if (e.Project is { } faces && (faces.Length != 2 || faces.Any(f => f.Length == 0) || faces[0] == faces[1]))
                return $"entity '{e.Id}' projects an edge, named by its two faces: two different face names";
            if (e.Project is not null && !e.Construction) return $"entity '{e.Id}' projects an edge, so it is construction geometry";
        }

        bool Point(string r) => s.Points.ContainsKey(r) && !entities.ContainsKey(r);
        bool Line(string r) => entities.GetValueOrDefault(r) is SketchLine;
        bool Round(string r) => entities.GetValueOrDefault(r) is SketchArc or SketchCircle;
        for (var k = 0; k < s.Constraints.Count; k++)
        {
            var c = s.Constraints[k];
            var r = c.Refs;
            if (!Kinds.Contains(c.Kind)) return $"constraint #{k}: unknown kind '{c.Kind}'";
            if (r.FirstOrDefault(x => !Point(x) && !entities.ContainsKey(x)) is { } unknown) return $"constraint #{k} ({c.Kind}): '{unknown}' is not a point or entity";
            var dimension = SketchConstraint.Dimensions.Contains(c.Kind);
            if (dimension != c.Value.HasValue) return dimension ? $"constraint #{k} ({c.Kind}) needs a value" : $"constraint #{k} ({c.Kind}) takes no value";
            var ok = c.Kind switch
            {
                "coincident" or "horizontal-distance" or "vertical-distance" => r.Length == 2 && Point(r[0]) && Point(r[1]),
                "horizontal" or "vertical" => r.Length == 1 && Line(r[0]) || r.Length == 2 && Point(r[0]) && Point(r[1]),
                "parallel" or "perpendicular" or "angle" => r.Length == 2 && Line(r[0]) && Line(r[1]),
                "tangent" => r.Length == 2 && (Round(r[0]) || Round(r[1])) && (Round(r[0]) || Line(r[0])) && (Round(r[1]) || Line(r[1])),
                "equal" => r.Length == 2 && (Line(r[0]) && Line(r[1]) || Round(r[0]) && Round(r[1])),
                "midpoint" => r.Length == 2 && Point(r[0]) && Line(r[1]),
                "symmetric" => r.Length == 3 && Point(r[0]) && Point(r[1]) && Line(r[2]),
                "on" => r.Length == 2 && Point(r[0]) && (Line(r[1]) || Round(r[1])),
                "fix" => r.Length == 1 && Point(r[0]),
                "length" => r.Length == 1 && Line(r[0]),
                "distance" => r.Length == 2 && (Point(r[0]) && Point(r[1]) || Point(r[0]) && Line(r[1]) || Line(r[0]) && Point(r[1]) || Line(r[0]) && Line(r[1])),
                "radius" or "diameter" => r.Length == 1 && Round(r[0]),
                _ => false,
            };
            if (!ok) return $"constraint #{k} ({c.Kind} {string.Join(" ", r)}) does not fit its points and entities";
            if (c.Kind is "length" or "distance" or "radius" or "diameter" && c.Value <= 0 && !(c.Kind == "distance" && c.Value == 0))
                return $"constraint #{k} ({c.Kind}) needs a value above 0";
        }
        return null;
    }
}
