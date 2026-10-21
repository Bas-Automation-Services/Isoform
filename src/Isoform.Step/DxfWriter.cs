using System.Globalization;
using System.Text;
using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Geometry;
using Isoform.Modeling;
using Isoform.Sketching;

namespace Isoform.Step;

/// <summary>A 2D entity of a drawing, in the drawing's (u, v) coordinates (mm).</summary>
public abstract record DxfEntity;

public sealed record DxfLine(double U0, double V0, double U1, double V1) : DxfEntity;

/// <summary>An arc counter-clockwise from <see cref="Start"/> to <see cref="End"/>, in degrees from the u axis, as DXF has it.</summary>
public sealed record DxfArc(double U, double V, double Radius, double Start, double End) : DxfEntity;

public sealed record DxfCircle(double U, double V, double Radius) : DxfEntity;

/// <summary>
/// Writes 2D drawings as DXF R12 ASCII (AC1009): LINE, ARC and CIRCLE on layer 0, in millimetres (<c>$INSUNITS</c> 4,
/// which R12 readers ignore). Two sources: a sketch's solved geometry in its (u, v) coordinates, without its
/// construction geometry, which a cutter would otherwise cut; and a flat face's exact outline in the face's plane.
/// </summary>
public static class DxfWriter
{
    public static string Write(IEnumerable<DxfEntity> entities)
    {
        var w = new StringBuilder();
        void Pair(int code, string value) => w.Append(code).Append("\r\n").Append(value).Append("\r\n");
        void Number(int code, double value) => Pair(code, value.ToString("0.##########", CultureInfo.InvariantCulture));
        void Point(int code, double u, double v)
        {
            Number(code, u);
            Number(code + 10, v);
            Number(code + 20, 0);
        }
        Pair(0, "SECTION");
        Pair(2, "HEADER");
        Pair(9, "$ACADVER");
        Pair(1, "AC1009");
        Pair(9, "$INSUNITS");
        Pair(70, "4");
        Pair(0, "ENDSEC");
        Pair(0, "SECTION");
        Pair(2, "ENTITIES");
        foreach (var e in entities)
        {
            Pair(0, e switch { DxfLine => "LINE", DxfArc => "ARC", _ => "CIRCLE" });
            Pair(8, "0");
            switch (e)
            {
                case DxfLine l:
                    Point(10, l.U0, l.V0);
                    Point(11, l.U1, l.V1);
                    break;
                case DxfArc a:
                    Point(10, a.U, a.V);
                    Number(40, a.Radius);
                    Number(50, a.Start);
                    Number(51, a.End);
                    break;
                case DxfCircle c:
                    Point(10, c.U, c.V);
                    Number(40, c.Radius);
                    break;
            }
        }
        Pair(0, "ENDSEC");
        Pair(0, "EOF");
        return w.ToString();
    }

    /// <summary>A sketch's lines, arcs and circles at its solution; construction geometry is left out.</summary>
    public static List<DxfEntity> Sketch(Sketch sketch, SketchSolution solution)
    {
        var p = solution.Points;
        return [.. sketch.Entities.Where(e => !e.Construction).Select(e => e switch
        {
            SketchLine l => (DxfEntity)new DxfLine(p[l.From].X, p[l.From].Y, p[l.To].X, p[l.To].Y),
            SketchArc a => new DxfArc(p[a.Center].X, p[a.Center].Y, Math.Sqrt(Sq(p[a.From].X - p[a.Center].X) + Sq(p[a.From].Y - p[a.Center].Y)),
                Angle(p[a.From].X - p[a.Center].X, p[a.From].Y - p[a.Center].Y), Angle(p[a.To].X - p[a.Center].X, p[a.To].Y - p[a.Center].Y)),
            SketchCircle c => new DxfCircle(p[c.Center].X, p[c.Center].Y, solution.Radii[c.Id]),
            _ => throw new NotSupportedException(e.GetType().Name),
        })];
    }

    /// <summary>
    /// The outline of a part's flat face <paramref name="face"/> (all of its pieces, when a cut split it), in the plane a
    /// sketch on it takes (<see cref="SketchPlanes.OfFace"/>), so seen from outside the part. Null when it is not a flat face.
    /// </summary>
    public static (List<DxfEntity> Entities, int Approximated)? Face(FrepResult result, Brep brep, string face, double chordTolerance) =>
        SketchPlanes.OfFace(face, result.Part.Surfaces, result.Part.Field) is { } plane
            ? Outline(brep, brep.Solids.SelectMany(s => s.Faces).Where(f => f.Sources[0] == face), Profiles.FrameOf(plane), chordTolerance)
            : null;

    /// <summary>
    /// The outline of flat faces, every loop, in <paramref name="frame"/>'s (x, y): lines and circular arcs exactly,
    /// any other edge (an ellipse, where a cylinder meets a sloping face) as lines within
    /// <paramref name="chordTolerance"/>. Approximated: how many edges were.
    /// </summary>
    public static (List<DxfEntity> Entities, int Approximated) Outline(Brep brep, IEnumerable<BrepFace> faces, Frame frame, double chordTolerance)
    {
        var entities = new List<DxfEntity>();
        var approximated = 0;
        var edges = faces.SelectMany(f => f.Loops).SelectMany(l => l.Coedges).Select(c => c.Edge).Distinct().Select(i => brep.Edges[i]);
        foreach (var edge in edges)
        {
            var (a, b) = (frame.ToLocal(edge.Curve.At(edge.T0)), frame.ToLocal(edge.Curve.At(edge.T1)));
            switch (edge.Curve)
            {
                case Line:
                    entities.Add(new DxfLine(a.X, a.Y, b.X, b.Y));
                    break;
                case Circle c when edge.T1 - edge.T0 > 2 * Math.PI - 1e-9:
                    var centre = frame.ToLocal(c.Center);
                    entities.Add(new DxfCircle(centre.X, centre.Y, c.Radius));
                    break;
                case Circle c:
                    // Counter-clockwise round the frame's normal from start to end: the curve's way round, or the other.
                    var o = frame.ToLocal(c.Center);
                    var (from, to) = (Angle(a.X - o.X, a.Y - o.Y), Angle(b.X - o.X, b.Y - o.Y));
                    entities.Add(c.Normal.Dot(frame.Z) > 0 ? new DxfArc(o.X, o.Y, c.Radius, from, to) : new DxfArc(o.X, o.Y, c.Radius, to, from));
                    break;
                default:
                    approximated++;
                    var points = Polyline(edge, chordTolerance).Select(p => frame.ToLocal(p)).ToList();
                    for (var i = 1; i < points.Count; i++) entities.Add(new DxfLine(points[i - 1].X, points[i - 1].Y, points[i].X, points[i].Y));
                    break;
            }
        }
        return (entities, approximated);
    }

    /// <summary>Points along an edge, evenly in its parameter, doubled until every chord stays within the tolerance at its middle.</summary>
    static List<Point3D> Polyline(BrepEdge edge, double tolerance)
    {
        for (var n = 4; ; n *= 2)
        {
            var t = Enumerable.Range(0, n + 1).Select(i => edge.T0 + (edge.T1 - edge.T0) * i / n).ToList();
            var points = t.Select(edge.Curve.At).ToList();
            var off = Enumerable.Range(0, n).Max(i =>
            {
                var (p, q, mid) = (points[i], points[i + 1], edge.Curve.At((t[i] + t[i + 1]) / 2));
                return (mid - new Point3D((p.X + q.X) / 2, (p.Y + q.Y) / 2, (p.Z + q.Z) / 2)).Length;
            });
            if (off <= tolerance || n >= 4096) return points;
        }
    }

    /// <summary>The direction's angle in degrees, in [0, 360).</summary>
    static double Angle(double u, double v)
    {
        var deg = Math.Atan2(v, u) * 180 / Math.PI;
        return deg < -1e-9 ? deg + 360 : Math.Max(deg, 0);
    }

    static double Sq(double x) => x * x;
}
