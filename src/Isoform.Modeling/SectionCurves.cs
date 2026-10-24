using BasAutomation.Geometry;
using Isoform.Geometry;

namespace Isoform.Modeling;

public enum SectionEdgeKind { Line, Arc, Circle, Fitted }

/// <summary>
/// The stretch of a section loop on one surface of the solid. A line, arc or full circle is that surface's exact
/// intersection with the plane, between exact corners (one segment, <see cref="Error"/> 0); a fitted edge is a chain of
/// tangent arcs and its largest distance from the true curve, measured both ways.
/// </summary>
public sealed record SectionEdge(int Owner, string Source, SectionEdgeKind Kind, IReadOnlyList<Segment2> Segments, double Error)
{
    public bool Exact => Kind != SectionEdgeKind.Fitted;
    public Point2D Start => Segments[0].Start;
    public Point2D End => Segments[^1].End;
}

/// <summary>A loop of edges, each starting where the one before ends; the material on its left.</summary>
public sealed record OutlineLoop(IReadOnlyList<SectionEdge> Edges, bool Outer);

/// <summary>A section's loops as exact and fitted edges, one per loop of <see cref="Section"/>; its regions index both alike.</summary>
public sealed record SectionOutline(Section Section, IReadOnlyList<OutlineLoop> Loops, double Tolerance)
{
    public IReadOnlyList<SectionRegion> Regions => Section.Regions;
}

/// <summary>
/// A section's loops turned into curves. Each loop is cut into runs by the surface owning its points:
/// <list type="bullet">
/// <item>a run on a plane, or on a cylinder, cone, sphere or torus cut round its axis, is the exact line or circle of
/// <see cref="SurfaceIntersection.Curve"/>;</item>
/// <item>every other run (blends, fillets, lattices, an oblique cylinder's ellipse) is fitted with tangent arcs
/// (<see cref="ArcFit"/>) within the tolerance, from end tangents that run on into its neighbours.</item>
/// </list>
/// A corner is found first where the owner changes (bisection along the boundary), then exactly: two lines or circles
/// meet in closed form (a tangent junction too); two analytic surfaces by Newton on both within the plane; and a blend
/// ends where its own field says, on its neighbour's surface where the other side of the blend is k away
/// (|f_a − f_b| = k), not where the cells saw the owners change.
/// </summary>
public static class SectionCurves
{
    public static SectionOutline Of(Section section, IReadOnlyList<Surface> surfaces, double tolerance = 0.05)
    {
        var blends = new Dictionary<int, BlendField>();
        foreach (var b in FieldAlgebra.Descendants(section.Field).OfType<BlendField>()) blends.TryAdd(b.Blend.Id, b);
        var context = new Context(section, surfaces, blends, tolerance);
        return new SectionOutline(section, section.Loops.Select(l => new OutlineLoop(context.Loop(l), l.Outer)).ToList(), tolerance);
    }

    /// <summary>A line (through P along unit D) or circle (centre P, radius R) in the plane.</summary>
    readonly record struct Curve2(bool Round, Point2D P, Vector2D D, double R)
    {
        public double Distance(Point2D p) => Round ? (p - P).Length - R : D.Cross(p - P);

        public Vector2D Gradient(Point2D p) => Round ? (p - P).Normalized() : new Vector2D(-D.Y, D.X);

        public Point2D Foot(Point2D p) => Round ? P + (p - P).Normalized() * R : P + D * D.Dot(p - P);
    }

    sealed class Run(int owner, List<Point2D> points)
    {
        public int Owner = owner;

        /// <summary>The owner and any other surface of the same name and curve that the run took in (the halves of a circle).</summary>
        public HashSet<int> Owners = [owner];

        public List<Point2D> Points = points;
        public Curve2? Curve;
    }

    delegate (double Value, Vector2D Gradient) Equation(Point2D p);

    sealed class Context(Section section, IReadOnlyList<Surface> surfaces, Dictionary<int, BlendField> blends, double tolerance)
    {
        public List<SectionEdge> Loop(SectionLoop loop)
        {
            var runs = Runs(loop);
            foreach (var r in runs) r.Curve = Exact(r);
            // Surfaces of one name on one line or circle (a sketch circle's two halves) are one edge.
            for (var i = 0; runs.Count > 1 && i < runs.Count; )
            {
                var (r, next) = (runs[i], runs[(i + 1) % runs.Count]);
                if (r.Curve is { } a && next.Curve is { } b && Same(a, b) && surfaces[r.Owner].Source == surfaces[next.Owner].Source)
                {
                    r.Points.AddRange(next.Points);
                    r.Owners.UnionWith(next.Owners);
                    runs.Remove(next);
                }
                else i++;
            }
            if (runs.Count == 1) return [Closed(runs[0])];
            Point2D[] corners;
            while (true)
            {
                corners = runs.Select((_, i) => Corner(runs[(i + runs.Count - 1) % runs.Count], runs[i])).ToArray();
                // A run whose corners meet (or cross, on a line) is the cells' noise at a corner: gone, and its neighbours join.
                var gone = Enumerable.Range(0, runs.Count).FirstOrDefault(i => Empty(runs[i], corners[i], corners[(i + 1) % runs.Count]), -1);
                if (gone < 0) break;
                runs.RemoveAt(gone);
                var at = gone % runs.Count;
                var before = (at + runs.Count - 1) % runs.Count;
                if (runs.Count > 1 && runs[before].Owner == runs[at].Owner)
                {
                    runs[before].Points.AddRange(runs[at].Points);
                    runs[before].Owners.UnionWith(runs[at].Owners);
                    runs.RemoveAt(at);
                }
                if (runs.Count == 1) return [Closed(runs[0])];
            }
            return runs.Select((r, i) => Edge(r, corners[i], corners[(i + 1) % runs.Count])).ToList();
        }

        bool Empty(Run r, Point2D start, Point2D end)
        {
            var d = end - start;
            if (d.Length <= 1e-9 * (1 + Math.Abs(start.X) + Math.Abs(start.Y))) return true;
            return r.Curve is { Round: false } line && d.Dot(Travel(r, line)) < 0;
        }

        // The line's direction of travel, from the loop's own sense at the run's middle.
        Vector2D Travel(Run r, Curve2 line) => line.D * Math.Sign(line.D.Dot(section.Tangent(Middle(r), r.Owner)));

        static Point2D Middle(Run r) => r.Points[r.Points.Count / 2];

        /// <summary>The loop's points in runs of one owner; a single point of another owner inside a run is the cells' noise there, and dropped.</summary>
        static List<Run> Runs(SectionLoop loop)
        {
            var all = loop.Points;
            var p = all.Where((x, i) => x.Owner == all[(i + all.Count - 1) % all.Count].Owner || all[(i + all.Count - 1) % all.Count].Owner != all[(i + 1) % all.Count].Owner).ToList();
            var n = p.Count;
            var first = Enumerable.Range(0, n).FirstOrDefault(i => p[i].Owner != p[(i + n - 1) % n].Owner, -1);
            if (first < 0) return [new Run(p[0].Owner, p.Select(x => x.At).ToList())];
            var runs = new List<Run>();
            for (var k = 0; k < n; k++)
            {
                var i = (first + k) % n;
                if (k == 0 || p[i].Owner != runs[^1].Owner) runs.Add(new Run(p[i].Owner, []));
                runs[^1].Points.Add(p[i].At);
            }
            return runs;
        }

        /// <summary>The run's exact line or circle in the plane, when its surface has one there and the run lies on it.</summary>
        Curve2? Exact(Run r)
        {
            var s = surfaces[r.Owner];
            if (!s.Analytic) return null;
            var plane = new Surface(-1, "", SurfaceKind.Plane, section.Plane.Origin, section.Plane.Z);
            Curve2? c = SurfaceIntersection.Curve(plane, s, section.World(Middle(r))) switch
            {
                Line l => new Curve2(false, Local(l.Origin), Local(l.Direction).Normalized(), 0),
                Circle o when Math.Abs(Math.Abs(o.Normal.Dot(section.Plane.Z)) - 1) <= 1e-9 => new Curve2(true, Local(o.Center), default, o.Radius),
                _ => null,
            };
            return c is { } curve && r.Points.All(p => Math.Abs(curve.Distance(p)) <= 1e-7 * (1 + curve.R)) ? curve : null;
        }

        Point2D Local(Point3D p)
        {
            var q = section.Plane.ToLocal(p);
            return new Point2D(q.X, q.Y);
        }

        Vector2D Local(Vector3D v) => new(v.Dot(section.Plane.X), v.Dot(section.Plane.Y));

        /// <summary>Where run <paramref name="a"/> ends and <paramref name="b"/> starts.</summary>
        Point2D Corner(Run a, Run b)
        {
            // Bisection along the boundary between the last point of a and the first of b, by the owner there.
            var (pa, pb) = (a.Points[^1], b.Points[0]);
            for (var it = 0; it < 100 && (pb - pa).Length > 1e-13 * (1 + Math.Abs(pa.X) + Math.Abs(pa.Y)); it++)
            {
                if (section.Project(Mid(pa, pb)) is not { } m) break;
                if (a.Owners.Contains(section.Sample(m).Owner)) pa = m;
                else pb = m;
            }
            var seed = Mid(pa, pb);
            if (a.Curve is { } ca && b.Curve is { } cb) return Meet(ca, cb, seed);
            if (Equations(a, b) is var (fa, fb) && Newton(fa, fb, seed) is { } p && (p - seed).Length <= section.CellSize) return p;
            return a.Curve is { } exactA ? exactA.Foot(seed) : b.Curve is { } exactB ? exactB.Foot(seed) : seed;
        }

        static Point2D Mid(Point2D a, Point2D b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

        static bool Same(Curve2 a, Curve2 b) => a.Round == b.Round && (a.Round
            ? (a.P - b.P).Length <= 1e-9 * (1 + a.R) && Math.Abs(a.R - b.R) <= 1e-9 * (1 + a.R)
            : Math.Abs(a.D.Cross(b.D)) <= 1e-12 && Math.Abs(a.Distance(b.P)) <= 1e-9 * (1 + Math.Abs(a.P.X) + Math.Abs(a.P.Y)));

        /// <summary>The two equations a corner between the runs solves; null where a side has none.</summary>
        (Equation, Equation)? Equations(Run a, Run b) =>
            (Of(a, b), Of(b, a)) is ({ } fa, { } fb) ? (fa, fb) : null;

        /// <summary>
        /// The equation of run <paramref name="r"/>'s side of its corner with <paramref name="other"/>: its exact curve, or
        /// its analytic surface; for a blend, that the far side of the blend is k away (the blend ends there).
        /// </summary>
        Equation? Of(Run r, Run other)
        {
            if (r.Curve is { } c) return p => (c.Distance(p), c.Gradient(p));
            if (blends.TryGetValue(r.Owner, out var blend))
            {
                var probe = section.World(other.Points[^1]);
                var far = blend.A.SampleSurface(other.Owner, probe) is not null ? blend.B : blend.B.SampleSurface(other.Owner, probe) is not null ? blend.A : null;
                if (far is null) return null;
                var k = blend.Union ? blend.K : -blend.K;
                return p =>
                {
                    var s = far.Sample(section.World(p));
                    return (s.Value - k, Local(s.Gradient));
                };
            }
            var surface = surfaces[r.Owner];
            if (!surface.Analytic) return null;
            return p =>
            {
                var w = section.World(p);
                return (surface.Distance(w), Local(surface.Gradient(w)));
            };
        }

        static Point2D? Newton(Equation fa, Equation fb, Point2D p)
        {
            for (var it = 0; it < 50; it++)
            {
                var ((va, ga), (vb, gb)) = (fa(p), fb(p));
                var det = ga.Cross(gb);
                if (Math.Abs(det) <= 1e-10 * ga.Length * gb.Length) return null;
                var step = new Vector2D(va * gb.Y - vb * ga.Y, vb * ga.X - va * gb.X) / det;
                p -= step;
                if (step.Length <= 1e-15 * (1 + Math.Abs(p.X) + Math.Abs(p.Y))) return p;
            }
            return Math.Abs(fa(p).Value) + Math.Abs(fb(p).Value) <= 1e-12 ? p : null;
        }

        /// <summary>Where two lines or circles meet nearest the seed, in closed form; a tangent touch at its point of contact.</summary>
        static Point2D Meet(Curve2 a, Curve2 b, Point2D seed)
        {
            const double tangent = 1e-12;
            if (a.Round && !b.Round) (a, b) = (b, a);
            if (!a.Round && !b.Round)
            {
                var cross = a.D.Cross(b.D);
                if (Math.Abs(cross) <= 1e-15) return a.Foot(seed);
                return a.P + a.D * ((b.P - a.P).Cross(b.D) / cross);
            }
            Point2D foot;
            Vector2D along;
            double disc;
            if (!a.Round)
            {
                foot = a.Foot(b.P);
                along = a.D;
                disc = b.R * b.R - (b.P - foot).LengthSquared;
            }
            else
            {
                var d = b.P - a.P;
                var dist = d.Length;
                if (dist <= 1e-12 * (1 + a.R)) return a.Foot(seed);
                var x = (a.R * a.R - b.R * b.R + dist * dist) / (2 * dist);
                foot = a.P + d * (x / dist);
                along = new Vector2D(-d.Y, d.X) / dist;
                disc = a.R * a.R - x * x;
            }
            if (disc <= tangent * b.R * b.R) return foot;
            var s = Math.Sqrt(disc);
            var (p, q) = (foot + along * s, foot - along * s);
            return (p - seed).LengthSquared <= (q - seed).LengthSquared ? p : q;
        }

        SectionEdge Edge(Run r, Point2D start, Point2D end)
        {
            var source = surfaces[r.Owner].Source;
            if (r.Curve is { Round: false }) return new(r.Owner, source, SectionEdgeKind.Line, [new Segment2(start, end)], 0);
            if (r.Curve is { } c) return new(r.Owner, source, SectionEdgeKind.Arc, [new Segment2(start, end, c.P, Ccw(r, c))], 0);
            var inside = r.Points.Where(p => Ahead(r, start, p) && Ahead(r, p, end)).ToList();
            return Fitted(r, [start, .. inside, end]);
        }

        // Whether q lies ahead of p along the run.
        bool Ahead(Run r, Point2D p, Point2D q) => (q - p).Dot(section.Tangent(q, r.Owner)) > 1e-9;

        bool Ccw(Run r, Curve2 circle)
        {
            var m = Middle(r);
            return (m - circle.P).Cross(section.Tangent(m, r.Owner)) > 0;
        }

        /// <summary>A loop on one surface: a full circle, or fitted from its first point round to it again.</summary>
        SectionEdge Closed(Run r)
        {
            var source = surfaces[r.Owner].Source;
            if (r.Curve is { Round: true } c)
            {
                var p = c.P + new Vector2D(c.R, 0);
                return new(r.Owner, source, SectionEdgeKind.Circle, [new Segment2(p, p, c.P, Ccw(r, c))], 0);
            }
            var start = section.Project(r.Points[0], r.Owner) ?? r.Points[0];
            return Fitted(r, [start, .. r.Points.Skip(1), start]);
        }

        /// <summary>
        /// Tangent arcs through the run's curve: its points made dense by projecting between them onto the owner's own
        /// boundary, the fit's error both ways, and every arc measured against the true curve by projecting points along it.
        /// </summary>
        SectionEdge Fitted(Run r, List<Point2D> points)
        {
            var step = tolerance;
            var dense = new List<Point2D> { points[0] };
            for (var k = 1; k < points.Count; k++)
            {
                var (a, b) = (dense[^1], points[k]);
                var n = (int)Math.Ceiling((b - a).Length / step);
                for (var j = 1; j < n; j++)
                {
                    var q = a + (b - a) * ((double)j / n);
                    if (section.Project(q, r.Owner) is { } on && (on - dense[^1]).Length > step / 10) dense.Add(on);
                }
                if ((b - dense[^1]).Length > step / 10 || k == points.Count - 1) dense.Add(b);
            }
            if ((dense[^1] - dense[^2]).Length <= step / 10 && dense.Count > 2) dense.RemoveAt(dense.Count - 2);
            var tangents = dense.Select(p => section.Tangent(p, r.Owner)).ToList();
            // The fit's own measure is against the curve between its points; the true curve may say a little more: refit tighter.
            var aim = tolerance;
            var (fit, error) = Measured(aim);
            while (error > tolerance && aim > tolerance / 4) (fit, error) = Measured(aim *= 0.8);
            return new SectionEdge(r.Owner, surfaces[r.Owner].Source, SectionEdgeKind.Fitted, fit.Segments, error);

            (ArcFitResult, double) Measured(double within)
            {
                var fit = ArcFit.Fit(dense, tangents, within);
                var error = fit.Error;
                foreach (var arc in fit.Segments)
                {
                    var n = Math.Max(8, (int)Math.Ceiling(arc.Length / step));
                    for (var j = 1; j < n; j++)
                    {
                        var q = arc.At((double)j / n);
                        if (section.Project(q, r.Owner) is { } on) error = Math.Max(error, (on - q).Length);
                    }
                }
                return (fit, error);
            }
        }
    }
}
