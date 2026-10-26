using BasAutomation.Geometry;
using Isoform.Sketching;
using DocProfile = Isoform.Document.Profile;

namespace Isoform.Brep.Modeling;

/// <summary>Isoform profiles (lines and bulge arcs, edges named by entity) as kernel loops.</summary>
public static class ProfileConversion
{
    /// <summary>
    /// The region's loops (outer first) and each segment's entity id. Consecutive arcs of one entity on one circle are
    /// one segment, so a sketch circle (two half arcs) is a full circle: one face, as the F-rep has it.
    /// </summary>
    public static (List<ProfileLoop> Loops, List<List<string>> Names) Loops(SketchRegion region, double tolerance)
    {
        var loops = new List<ProfileLoop>();
        var names = new List<List<string>>();
        foreach (var profile in new[] { region.Outer }.Concat(region.Holes))
        {
            var (loop, ids) = Loop(profile, tolerance);
            loops.Add(loop);
            names.Add(ids);
        }
        return (loops, names);
    }

    static (ProfileLoop, List<string>) Loop(DocProfile p, double tolerance)
    {
        var segments = new List<(string Id, Segment Segment)>();
        var from = new Point2D(p.Start[0], p.Start[1]);
        foreach (var e in p.Edges)
        {
            var to = new Point2D(e.To[0], e.To[1]);
            segments.Add((e.Id, e.Bulge == 0 ? new LineSegment(from, to) : Arc(from, to, e.Bulge)));
            from = to;
        }

        // Merge arcs of one entity on one circle, also across the loop's start.
        bool Mergeable((string Id, Segment S) a, (string Id, Segment S) b) =>
            a.Id == b.Id && a.S is ArcSegment x && b.S is ArcSegment y && Math.Sign(x.Sweep) == Math.Sign(y.Sweep)
            && (x.Center - y.Center).Length <= tolerance && Math.Abs(x.Radius - y.Radius) <= tolerance;
        static (string, Segment) Join((string Id, Segment S) a, (string Id, Segment S) b)
        {
            var (x, y) = ((ArcSegment)a.S, (ArcSegment)b.S);
            return (a.Id, x with { Sweep = x.Sweep + y.Sweep });
        }
        var merged = new List<(string Id, Segment S)>();
        foreach (var s in segments)
        {
            if (merged.Count > 0 && Mergeable(merged[^1], s)) merged[^1] = Join(merged[^1], s);
            else merged.Add(s);
        }
        if (merged.Count > 1 && Mergeable(merged[^1], merged[0]))
        {
            merged[0] = Join(merged[^1], merged[0]);
            merged.RemoveAt(merged.Count - 1);
        }
        return (new ProfileLoop([.. merged.Select(m => m.S)]), [.. merged.Select(m => m.Id)]);
    }

    /// <summary>
    /// The arc from <paramref name="a"/> to <paramref name="b"/> with bulge tan(θ/4): centre on the chord's left
    /// normal at (1 − bulge²)/(4 bulge) chords from its middle, sweep θ.
    /// </summary>
    public static ArcSegment Arc(Point2D a, Point2D b, double bulge)
    {
        var chord = b - a;
        var left = new Vector2D(-chord.Y, chord.X);
        var center = new Point2D((a.X + b.X) / 2, (a.Y + b.Y) / 2) + left * ((1 - bulge * bulge) / (4 * bulge));
        var radius = (a - center).Length;
        return new ArcSegment(center, radius, Math.Atan2(a.Y - center.Y, a.X - center.X), 4 * Math.Atan(bulge));
    }
}
