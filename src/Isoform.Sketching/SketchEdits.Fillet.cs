using System.Globalization;
using Isoform.Document;

namespace Isoform.Sketching;

public static partial class SketchEdits
{
    /// <summary>
    /// Rounds the corner at point <paramref name="corner"/>, where two lines or arcs meet, with a tangent arc of
    /// <paramref name="radius"/>. The corner stays as a construction point on both, so dimensions to it still hold.
    /// The two keep their ids and now end at the tangent points; the new arc is <c>&lt;corner&gt;-fillet</c>, round
    /// <c>&lt;corner&gt;-fillet-centre</c>, between <c>&lt;corner&gt;-fillet-&lt;entity&gt;</c> on each. A length,
    /// equal or midpoint on a line that is cut short keeps measuring to the corner: a length becomes a distance from
    /// the corner, an equal or midpoint moves to a construction line <c>&lt;line&gt;-sharp</c> along the old line.
    /// </summary>
    public static SketchEdit Fillet(Sketch sketch, string corner, double radius) => Rounding.Fillet(sketch, corner, radius);
}

file readonly record struct Vec(double X, double Y)
{
    public static Vec operator +(Vec a, Vec b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec operator -(Vec a, Vec b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec operator *(double k, Vec a) => new(k * a.X, k * a.Y);
    public double Dot(Vec b) => X * b.X + Y * b.Y;
    public double Cross(Vec b) => X * b.Y - Y * b.X;
    public double Length => Math.Sqrt(X * X + Y * Y);
    public Vec Unit => 1 / Length * this;

    /// <summary>Turned a quarter counter-clockwise.</summary>
    public Vec Left => new(-Y, X);
}

/// <summary>
/// One of the two entities at the corner, as the fillet sees it: the corner, the far end and, for an arc, its centre
/// and radius. Its offset by the fillet radius towards the other entity is a line (n·X = k) or a circle.
/// </summary>
file sealed class Leg
{
    public SketchEntity Entity { get; }
    public bool AtFrom { get; }
    public string Far { get; }
    public bool Arc => Entity is SketchArc;
    readonly Vec _p, _far, _c;
    readonly double _r;

    public Leg(SketchEntity e, string corner, Func<string, Vec> at)
    {
        Entity = e;
        var (from, to) = e is SketchLine l ? (l.From, l.To) : (((SketchArc)e).From, ((SketchArc)e).To);
        (AtFrom, Far) = (from == corner, from == corner ? to : from);
        (_p, _far) = (at(corner), at(Far));
        if (e is SketchArc a) (_c, _r) = (at(a.Center), (at(a.From) - at(a.Center)).Length);
    }

    /// <summary>The direction along the entity at <paramref name="q"/>, from the corner's end towards the far end.</summary>
    public Vec Into(Vec q) => Arc ? (AtFrom ? 1 : -1) * (q - _c).Unit.Left : (_far - _p).Unit;

    // The offset: the line Normal·X = K, or the circle of radius Offset round Centre.
    public Vec Normal { get; private set; }
    public double K { get; private set; }
    public Vec Centre => _c;
    public double Offset { get; private set; }

    /// <summary>Offsets towards <paramref name="other"/>'s direction at the corner by <paramref name="r"/>; false when an arc is too tight for it.</summary>
    public bool OffsetTowards(Vec other, double r)
    {
        var t = Into(_p);
        Normal = Math.Sign(t.Cross(other)) * t.Left;
        if (!Arc)
        {
            K = Normal.Dot(_p) + r;
            return true;
        }
        Offset = _r + (Normal.Dot(_p - _c) > 0 ? r : -r);
        return Offset > 1e-9;
    }

    /// <summary>The foot of <paramref name="o"/> on the entity's line or circle.</summary>
    public Vec Foot(Vec o) => Arc ? _c + _r * (o - _c).Unit : _p + (o - _p).Dot(Into(_p)) * Into(_p);

    /// <summary>Whether <paramref name="q"/>, on the line or circle, lies inside the entity, clear of both its ends.</summary>
    public bool Within(Vec q)
    {
        const double clear = 1e-6;
        if (!Arc)
        {
            var s = (q - _p).Dot(Into(_p));
            return s > clear && s < (_far - _p).Length - clear;
        }
        var (from, to) = AtFrom ? (_p, _far) : (_far, _p);
        var sweep = Ccw(from - _c, to - _c);
        var along = AtFrom ? Ccw(_p - _c, q - _c) : Ccw(q - _c, _p - _c);
        return along * _r > clear && (sweep - along) * _r > clear;
    }

    // The angle counter-clockwise from a to b, in (0, 2π]: an arc from a point back to it is a full turn.
    static double Ccw(Vec a, Vec b)
    {
        var angle = Math.Atan2(a.Cross(b), a.Dot(b));
        return angle <= 0 ? angle + 2 * Math.PI : angle;
    }
}

file static class Rounding
{
    public static SketchEdit Fillet(Sketch sketch, string corner, double radius)
    {
        string Text(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
        if (!sketch.Points.ContainsKey(corner) || sketch.Entities.Any(e => e.Id == corner))
            throw new ArgumentException($"'{corner}' is not a point of sketch '{sketch.Id}'");
        if (radius <= 0) throw new ArgumentException($"a fillet needs a radius above 0, not {Text(radius)}");

        var ending = sketch.Entities.SelectMany(e => (e switch { SketchLine l => [l.From, l.To], SketchArc a => [a.From, a.To], _ => Array.Empty<string>() })
            .Where(p => p == corner).Select(_ => e)).ToList();
        var drawn = ending.Where(e => !e.Construction).ToList();
        if (drawn.Count != 2 || drawn[0] == drawn[1])
            throw new ArgumentException(
                ending.Count == 0 && sketch.Entities.OfType<SketchCircle>().Any(c => c.Center == corner) ? $"'{corner}' is the centre of a circle; a circle has no corner to fillet"
                : drawn.Count < ending.Count ? $"construction geometry ends at '{corner}'; only a corner of two lines or arcs that are not construction is filleted"
                : $"a fillet rounds a corner where two lines or arcs end; {drawn.Count} end at '{corner}'");

        Vec At(string p) => new(sketch.Points[p][0], sketch.Points[p][1]);
        var p = At(corner);
        var (one, two) = (new Leg(drawn[0], corner, At), new Leg(drawn[1], corner, At));
        var (t1, t2) = (one.Into(p), two.Into(p));
        if (Math.Abs(t1.Cross(t2)) < 1e-9)
            throw new ArgumentException($"'{drawn[0].Id}' and '{drawn[1].Id}' meet at '{corner}' without a corner to round");
        if (!one.OffsetTowards(t2, radius) || !two.OffsetTowards(t1, radius))
            throw new ArgumentException($"a fillet of radius {Text(radius)} does not fit inside the arc at '{corner}'");

        // The fillet centre: nearest the corner of the offsets' crossings whose tangent points both lie on their entities.
        var centre = Crossings(one, two).Where(o => one.Within(one.Foot(o)) && two.Within(two.Foot(o)))
            .OrderBy(o => (o - p).Length).Select(o => (Vec?)o).FirstOrDefault()
            ?? throw new ArgumentException($"a fillet of radius {Text(radius)} does not fit at '{corner}': it would run past the end of '{drawn[0].Id}' or '{drawn[1].Id}'");
        var (f1, f2) = (one.Foot(centre), two.Foot(centre));

        var taken = new HashSet<string>(sketch.Points.Keys.Concat(sketch.Entities.Select(e => e.Id)), StringComparer.Ordinal);
        string Fresh(string name)
        {
            var n = name;
            for (var k = 2; !taken.Add(n); k++) n = FormattableString.Invariant($"{name}-{k}");
            return n;
        }
        var arc = Fresh($"{corner}-fillet");
        var middle = Fresh($"{arc}-centre");
        var (end1, end2) = (Fresh($"{arc}-{one.Entity.Id}"), Fresh($"{arc}-{two.Entity.Id}"));
        var added = new List<string> { arc, middle, end1, end2 };

        var points = sketch.Points.ToDictionary(q => q.Key, q => q.Value);
        points[middle] = [centre.X, centre.Y];
        points[end1] = [f1.X, f1.Y];
        points[end2] = [f2.X, f2.Y];

        // Counter-clockwise from the end where leaving the first entity turns left round the centre.
        var leaving = -1 * one.Into(f1);
        var fillet = (f1 - centre).Cross(leaving) > 0 ? new SketchArc(arc, middle, end1, end2) : new SketchArc(arc, middle, end2, end1);

        SketchEntity Cut(SketchEntity e, string end) => e switch
        {
            SketchLine l => l.From == corner ? l with { From = end } : l with { To = end },
            SketchArc a => a.From == corner ? a with { From = end } : a with { To = end },
            _ => e,
        };
        var entities = sketch.Entities.Select(e => e == one.Entity ? Cut(e, end1) : e == two.Entity ? Cut(e, end2) : e).ToList();

        // Lines cut short: what measured their length or middle measures the old line, corner to far end.
        var sharp = new Dictionary<string, string>();
        string Sharp(Leg leg)
        {
            if (sharp.TryGetValue(leg.Entity.Id, out var id)) return id;
            id = sharp[leg.Entity.Id] = Fresh($"{leg.Entity.Id}-sharp");
            entities.Add(new SketchLine(id, leg.AtFrom ? corner : leg.Far, leg.AtFrom ? leg.Far : corner) { Construction = true });
            added.Add(id);
            return id;
        }
        Leg? Cutting(string r) => new[] { one, two }.FirstOrDefault(l => !l.Arc && l.Entity.Id == r);
        SketchConstraint Kept(SketchConstraint c) => c.Kind switch
        {
            "length" when Cutting(c.Refs[0]) is { } leg => new SketchConstraint("distance", [corner, leg.Far], c.Value),
            "equal" or "midpoint" when c.Refs.Any(r => Cutting(r) is not null) => c with { Refs = [.. c.Refs.Select(r => Cutting(r) is { } leg ? Sharp(leg) : r)] },
            _ => c,
        };
        var constraints = sketch.Constraints.Select(Kept).ToList();
        entities.Add(fillet);
        constraints.AddRange([
            new SketchConstraint("on", [corner, one.Entity.Id]), new SketchConstraint("on", [corner, two.Entity.Id]),
            new SketchConstraint("tangent", [one.Entity.Id, arc]), new SketchConstraint("tangent", [two.Entity.Id, arc]),
            new SketchConstraint("radius", [arc], radius),
        ]);

        var edited = sketch with { Points = points, Entities = entities, Constraints = constraints };
        var solution = SketchSolver.Solve(edited);
        if (!solution.Solved) throw new ArgumentException($"the fillet at '{corner}' does not solve: {string.Join("; ", solution.Problems.Select(q => q.Message))}");
        return new SketchEdit(SketchSolver.Settle(edited, solution), added, []);
    }

    /// <summary>Where the two offsets cross: lines in one point, a line and a circle or two circles in up to two.</summary>
    static IEnumerable<Vec> Crossings(Leg a, Leg b)
    {
        if (a.Arc && !b.Arc) (a, b) = (b, a);
        if (!a.Arc && !b.Arc)
        {
            var (n, m) = (a.Normal, b.Normal);
            var det = n.Cross(m);
            yield return new Vec((a.K * m.Y - b.K * n.Y) / det, (n.X * b.K - m.X * a.K) / det);
        }
        else if (!a.Arc)
        {
            var h = a.K - a.Normal.Dot(b.Centre);
            if (Math.Abs(h) > b.Offset) yield break;
            var (foot, half) = (b.Centre + h * a.Normal, Math.Sqrt(b.Offset * b.Offset - h * h));
            yield return foot + half * a.Normal.Left;
            yield return foot - half * a.Normal.Left;
        }
        else
        {
            var join = b.Centre - a.Centre;
            var d = join.Length;
            if (d < 1e-12 || d > a.Offset + b.Offset || d < Math.Abs(a.Offset - b.Offset)) yield break;
            var along = (a.Offset * a.Offset - b.Offset * b.Offset + d * d) / (2 * d);
            var (u, h) = (join.Unit, Math.Sqrt(Math.Max(a.Offset * a.Offset - along * along, 0)));
            yield return a.Centre + along * u + h * u.Left;
            yield return a.Centre + along * u - h * u.Left;
        }
    }
}
