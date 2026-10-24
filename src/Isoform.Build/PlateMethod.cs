using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Geometry;
using Isoform.Modeling;

namespace Isoform.Build;

/// <summary>
/// Plates: the section of what is left of the design at the plate's mid-plane, each region a plate. On a named flat
/// face the plate's outer face lies on it, thickness into the material, and each copy of the face (a pattern's) makes
/// the plates under it; on a plane the plate runs along its normal and every region (or the one <c>near</c> picks) is a
/// plate. A plate's part is its section sketch in its own frame (x and y the section's, origin at the outline's lower
/// left corner on the plate's underside), extruded 0 to the thickness; lines, arcs and circles copied from analytic
/// design faces are exact, the rest fitted with tangent arcs within the build's tolerance, each fit carried on the part.
/// </summary>
/// <param name="cellSize">The section's cell (mm): a slab one cell either side of the mid-plane is meshed.</param>
public sealed class PlateMethod(double cellSize = 1) : IBuildMethod
{
    public SectionResult Build(BuildSection section, BuildContext context)
    {
        var plate = (PlateSection)section;
        var (design, t) = (context.Design, plate.Thickness);
        var parts = new List<BuiltPart>();
        var diagnostics = new List<Diagnostic>();
        var sections = new List<(Frame Plane, SectionOutline Outline)>();

        // One section per plane, shared by copies of a face that lie in it (the feet of a stand).
        SectionOutline Cut(Frame mid)
        {
            foreach (var (p, o) in sections)
                if ((p.Z - mid.Z).Length < 1e-9 && Math.Abs(p.Z.Dot(mid.Origin - p.Origin)) < 1e-9) return o;
            var cut = Sections.Of(context.Remaining, context.Surfaces, mid, design.Bounds, cellSize);
            diagnostics.AddRange(cut.Diagnostics.Where(d => !diagnostics.Contains(d)));
            var outline = SectionCurves.Of(cut, context.Surfaces, context.Build.Tolerance);
            sections.Add((mid, outline));
            return outline;
        }

        void Make(Frame mid, SectionOutline outline, SectionRegion region)
        {
            // The part's frame: the mid-plane's axes, its origin on the underside below the outline's lower left.
            var extremes = outline.Loops[region.Outer].Edges.SelectMany(e => e.Segments).SelectMany(Extremes).ToList();
            var corner = new Vector2D(extremes.Min(p => p.X), extremes.Min(p => p.Y));
            var loops = (int[])[region.Outer, .. region.Holes];
            var moved = outline with { Loops = [.. outline.Loops.Select(l => l with { Edges = [.. l.Edges.Select(e => e with { Segments = [.. e.Segments.Select(s => Moved(s, corner))] })] })] };
            var sketch = SectionSketch.Of("outline", new SketchPlane([0, 0, 0], [1, 0, 0], [0, 0, 1]), moved, loops);
            var document = new PartDocument([sketch, new Extrude("plate", BooleanOp.New, null, null, 0, t) { Sketch = sketch.Id }]);
            var origin = mid.ToWorld(new Point3D(corner.X, corner.Y, -t / 2));
            var placement = new Placement([origin.X, origin.Y, origin.Z], [mid.X.X, mid.X.Y, mid.X.Z], [mid.Z.X, mid.Z.Y, mid.Z.Z]);
            parts.Add(BuiltPart.Of(section.Id, document, placement) with
            {
                Fits = [.. loops.SelectMany(i => outline.Loops[i].Edges).Where(e => !e.Exact).Select(e => new FittedEdge(e.Source, e.Segments.Count, e.Error))],
            });
        }

        if (plate.Face is { } name)
        {
            foreach (var (copy, faces) in design.Copies(name))
            {
                var part = design.Result.Part;
                if (SketchPlanes.OfFace(copy, part.Surfaces, part.Field) is not { } on) continue;
                var face = Profiles.FrameOf(on);
                var mid = face with { Origin = face.Origin - face.Z * (t / 2) };
                var outline = Cut(mid);
                // The regions under the face: those its polygons stand over.
                var over = design.PointsOn(faces).Select(p => mid.ToLocal(p)).Select(p => new Point2D(p.X, p.Y)).ToList();
                var regions = outline.Regions.Where(r => over.Any(outline.Section.Loops[r.Outer].Contains)).ToList();
                if (regions.Count == 0)
                    diagnostics.Add(new(BuildDiagnostic.RegionEmpty, section.Id, $"nothing of the design is left under '{copy}' for section '{section.Id}'"));
                foreach (var region in regions) Make(mid, outline, region);
            }
        }
        else if (plate.Plane is { } on)
        {
            var face = Profiles.FrameOf(on);
            var mid = face with { Origin = face.Origin + face.Z * (t / 2) };
            var outline = Cut(mid);
            var regions = outline.Regions.ToList();
            if (plate.Near is [var x, var y, var z] && regions.Count > 0)
            {
                var near = mid.ToLocal(new Point3D(x, y, z));
                var at = new Point2D(near.X, near.Y);
                double Distance(SectionRegion r) => outline.Section.Loops[r.Outer] is var loop && loop.Contains(at) ? 0 : loop.Points.Min(p => (p.At - at).Length);
                regions = [regions.MinBy(Distance)!];
            }
            foreach (var region in regions) Make(mid, outline, region);
        }
        return new SectionResult(parts, diagnostics);
    }

    static IEnumerable<Point2D> Extremes(Segment2 s) => (s.Center is { } c
        ? new OutlineEdge(OutlineEdgeKind.Arc, s.Ccw ? s.Start : s.End, s.Ccw ? s.End : s.Start, c, s.Radius, Math.Abs(s.Sweep))
        : new OutlineEdge(OutlineEdgeKind.Line, s.Start, s.End, default, 0, 0)).Extremes();

    static Segment2 Moved(Segment2 s, Vector2D by) => new(s.Start - by, s.End - by, s.Center is { } c ? c - by : null, s.Ccw);
}
