using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Geometry;

namespace Isoform.Modeling;

/// <summary>
/// A section outline as a sketch: its points, and a line, arc or circle per segment, without constraints (the points
/// carry the geometry). Coordinates are the section plane's; the sketch lies on <c>on</c>, so a plate's sketch can sit
/// in the plate's own frame. Entities are named after the design face they follow and a running number,
/// <c>lobes-2:lobe-blend~3</c> for the third segment along <c>lobes/2:lobe/blend</c> (a sketch name holds no '/'), so a
/// plate's faces say where they came from and keep their names when the design changes but its faces do not. Numbers
/// run over the loops ordered by their first face name, each loop from its first edge on that face. Outer loops and
/// holes need nothing more: <c>SketchRegions</c> nests the loops.
/// </summary>
public static class SectionSketch
{
    /// <param name="loops">The loops to take (by index), e.g. one region's; null for all.</param>
    public static Sketch Of(string id, SketchPlane on, SectionOutline outline, IEnumerable<int>? loops = null)
    {
        var points = new Dictionary<string, double[]>();
        var entities = new List<SketchEntity>();
        var count = new Dictionary<string, int>();

        string Point(Point2D p)
        {
            var name = $"p{points.Count + 1}";
            points[name] = [p.X, p.Y];
            return name;
        }

        string Name(string source)
        {
            var n = count[source] = count.GetValueOrDefault(source) + 1;
            return $"{source.Replace('/', '-')}~{n}";
        }

        // In an order that holds while the design's faces do: loops by their first face name, each from its first edge
        // on that face counter-clockwise from +x round the loop's middle.
        static string Least(OutlineLoop l) => l.Edges.Select(e => e.Source).Min(StringComparer.Ordinal)!;
        static Point2D Middle(OutlineLoop l) => new(l.Edges.Average(e => e.Start.X), l.Edges.Average(e => e.Start.Y));
        foreach (var index in (loops ?? Enumerable.Range(0, outline.Loops.Count))
                     .OrderBy(i => Least(outline.Loops[i]), StringComparer.Ordinal).ThenBy(i => Middle(outline.Loops[i]).X).ThenBy(i => Middle(outline.Loops[i]).Y))
        {
            var loop = outline.Loops[index];
            var edges = loop.Edges;
            var (least, middle) = (Least(loop), Middle(loop));
            double Turn(Point2D p) => Math.Atan2(p.Y - middle.Y, p.X - middle.X) is var a && a < 0 ? a + 2 * Math.PI : a;
            var first = Enumerable.Range(0, edges.Count).Where(i => edges[i].Source == least).MinBy(i => Turn(edges[i].Start));
            var segments = Enumerable.Range(0, edges.Count).Select(i => edges[(first + i) % edges.Count])
                .SelectMany(e => e.Segments.Select(s => (e.Source, Segment: s))).ToList();
            if (segments is [(var only, { IsArc: true } circle)] && circle.Start == circle.End)
            {
                entities.Add(new SketchCircle(Name(only), Point(circle.Center!.Value), circle.Radius));
                continue;
            }
            var start = Point(segments[0].Segment.Start);
            var from = start;
            for (var k = 0; k < segments.Count; k++)
            {
                var (source, s) = segments[k];
                var to = k == segments.Count - 1 ? start : Point(s.End);
                entities.Add(s.Center is { } c
                    ? s.Ccw ? new SketchArc(Name(source), Point(c), from, to) : new SketchArc(Name(source), Point(c), to, from)
                    : new SketchLine(Name(source), from, to));
                from = to;
            }
        }
        return new Sketch(id, new SketchPlacement(on), points, entities, []);
    }
}
