using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Geometry;

namespace Isoform.Modeling;

/// <summary>Document profiles as 2D edges, and the checks a profile must pass.</summary>
public static class Profiles
{
    /// <summary>The edges with their ids, turned counter-clockwise if the profile runs clockwise.</summary>
    public static List<(string Id, ProfileEdge2D Edge)> CounterClockwise(Profile p)
    {
        var edges = Edges(p);
        if (edges.Sum(e => e.Edge.Area) >= 0) return edges;
        edges.Reverse();
        return edges.Select(e => (e.Id, e.Edge.Reversed())).ToList();
    }

    public static Frame FrameOf(SketchPlane plane) =>
        Frame.FromXAndNormal(new Point3D(plane.Origin[0], plane.Origin[1], plane.Origin[2]),
            new Vector3D(plane.X[0], plane.X[1], plane.X[2]), new Vector3D(plane.Normal[0], plane.Normal[1], plane.Normal[2]));

    /// <summary>A 2D box round the profile; arcs count with their whole circle, which is always enough.</summary>
    public static (double U0, double V0, double U1, double V1) Bounds(Profile p)
    {
        double u0 = double.MaxValue, v0 = double.MaxValue, u1 = double.MinValue, v1 = double.MinValue;
        foreach (var (_, e) in Edges(p))
        {
            var (lo, hi) = e.IsArc ? ((e.Cu - e.R, e.Cv - e.R), (e.Cu + e.R, e.Cv + e.R)) : ((Math.Min(e.U0, e.U1), Math.Min(e.V0, e.V1)), (Math.Max(e.U0, e.U1), Math.Max(e.V0, e.V1)));
            (u0, v0, u1, v1) = (Math.Min(u0, lo.Item1), Math.Min(v0, lo.Item2), Math.Max(u1, hi.Item1), Math.Max(v1, hi.Item2));
        }
        return (u0, v0, u1, v1);
    }

    public static string? Problem(SketchPlane plane, Profile p, double tolerance, bool revolve)
    {
        var normal = new Vector3D(plane.Normal[0], plane.Normal[1], plane.Normal[2]);
        var x = new Vector3D(plane.X[0], plane.X[1], plane.X[2]);
        if (normal.Length <= tolerance || x.Cross(normal).Length <= tolerance * Math.Max(1, x.Length))
            return "the sketch plane needs a normal and an x direction that is not parallel to it";
        if (p.Edges.Count < 2) return "a profile needs at least two edges";
        if (p.Edges.Select(e => e.Id).Distinct().Count() != p.Edges.Count) return "profile edge ids must be unique";
        if (p.Edges.Any(e => e.Id is "start" or "end" || e.Id.Contains('/') || e.Id.Length == 0))
            return "profile edge ids may not be empty, contain '/', or be 'start' or 'end'";
        var last = p.Edges[^1].To;
        if (Math.Abs(last[0] - p.Start[0]) > tolerance || Math.Abs(last[1] - p.Start[1]) > tolerance)
            return "the profile is not closed: its last edge must end at its start";
        if (p.Edges.Any(e => !double.IsFinite(e.Bulge) || Math.Abs(e.Bulge) > 1e3)) return "an arc bulge must be finite and below 1000";

        var edges = Edges(p);
        if (edges.Any(e => Math.Sqrt(Sq(e.Edge.U1 - e.Edge.U0) + Sq(e.Edge.V1 - e.Edge.V0)) <= tolerance))
            return "a profile edge has zero length";
        if (Math.Abs(edges.Sum(e => e.Edge.Area)) <= tolerance) return "the profile encloses no area";
        if (revolve && edges.Any(e => e.Edge.Points(32).Any(q => q.U < -tolerance)))
            return "a revolve profile must stay on one side of the axis (x ≥ 0)";
        if (SelfIntersection(edges, tolerance) is { } pair) return $"profile edges {pair.A} and {pair.B} cross";
        return null;
    }

    static List<(string Id, ProfileEdge2D Edge)> Edges(Profile p)
    {
        var result = new List<(string, ProfileEdge2D)>();
        var (u, v) = (p.Start[0], p.Start[1]);
        foreach (var e in p.Edges)
        {
            result.Add((e.Id, new ProfileEdge2D(u, v, e.To[0], e.To[1], e.Bulge)));
            (u, v) = (e.To[0], e.To[1]);
        }
        return result;
    }

    /// <summary>Two edges that cross or touch away from the corner they share, tested on arcs cut into 32 pieces.</summary>
    static (string A, string B)? SelfIntersection(List<(string Id, ProfileEdge2D Edge)> edges, double tolerance)
    {
        var pieces = edges.Select(e => e.Edge.Points(32).ToList()).ToList();
        var n = edges.Count;
        for (var i = 0; i < n; i++)
        for (var j = i + 1; j < n; j++)
        {
            // Neighbouring edges meet at a shared corner (two of them when the loop has only two edges); only the
            // pieces touching that corner may share its point.
            var (jFollows, iFollows) = (j == (i + 1) % n, i == (j + 1) % n);
            var (lastA, lastB) = (pieces[i].Count - 2, pieces[j].Count - 2);
            for (var a = 0; a <= lastA; a++)
            for (var b = 0; b <= lastB; b++)
            {
                if (jFollows && a == lastA && b == 0 || iFollows && a == 0 && b == lastB) continue;
                if (Cross(pieces[i][a], pieces[i][a + 1], pieces[j][b], pieces[j][b + 1], tolerance)) return (edges[i].Id, edges[j].Id);
            }
        }
        return null;
    }

    static bool Cross((double U, double V) p, (double U, double V) q, (double U, double V) r, (double U, double V) s, double tol)
    {
        double Orient((double U, double V) a, (double U, double V) b, (double U, double V) c) => (b.U - a.U) * (c.V - a.V) - (b.V - a.V) * (c.U - a.U);
        var (d1, d2, d3, d4) = (Orient(r, s, p), Orient(r, s, q), Orient(p, q, r), Orient(p, q, s));
        if ((d1 > tol && d2 < -tol || d1 < -tol && d2 > tol) && (d3 > tol && d4 < -tol || d3 < -tol && d4 > tol)) return true;
        bool On((double U, double V) a, (double U, double V) b, (double U, double V) c, double o) =>
            Math.Abs(o) <= tol && Math.Min(a.U, b.U) - tol <= c.U && c.U <= Math.Max(a.U, b.U) + tol && Math.Min(a.V, b.V) - tol <= c.V && c.V <= Math.Max(a.V, b.V) + tol;
        return On(r, s, p, d1) || On(r, s, q, d2) || On(p, q, r, d3) || On(p, q, s, d4);
    }

    static double Sq(double x) => x * x;
}
