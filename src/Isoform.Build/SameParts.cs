using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Geometry;
using Isoform.Modeling;

namespace Isoform.Build;

/// <summary>
/// Same parts: a part is the same as one already written when the documents are the same, or when it is congruent to
/// it: plates of one thickness whose outlines map onto each other by a turn and a shift in the plane, or by turning the
/// plate over (a flat plate is its own mirror image); tubes of one size and length whose end planes match up to a turn
/// about the axis, or end for end. The match gives the placement that puts the written part where this one goes.
/// </summary>
public static class SameParts
{
    /// <summary>How far apart congruent parts may be, anywhere, in mm (and in the unit normals of tube ends).</summary>
    public const double Tolerance = 1e-6;

    /// <summary>
    /// The placement of <paramref name="written"/> that makes it the part <paramref name="document"/> placed at
    /// <paramref name="placement"/>, or null when they are not the same part.
    /// </summary>
    public static Placement? Match(PartDocument document, Placement placement, PartDocument written)
    {
        if (CanonicalFormat.Hash(document) == CanonicalFormat.Hash(written)) return placement;
        // A rigid map m from this part's own coordinates into the written part's: the written part placed at
        // (m⁻¹ then this placement) is this part.
        var map = PlateShape.Of(document) is { } a && PlateShape.Of(written) is { } b ? Plates(a, b)
            : TubeShape.Of(document) is { } c && TubeShape.Of(written) is { } d ? Tubes(c, d)
            : null;
        if (map is not { } m) return null;
        var inverse = Inverse(m);
        var at = inverse.Then(AssemblyModeler.FrameOf(placement));
        return new Placement([at.Origin.X, at.Origin.Y, at.Origin.Z], [at.X.X, at.X.Y, at.X.Z], [at.Z.X, at.Z.Y, at.Z.Z]);
    }

    static Frame Inverse(Frame f)
    {
        var o = f.ToLocal(new Point3D(0, 0, 0));
        return new Frame(o, f.ToLocal(new Vector3D(1, 0, 0)), f.ToLocal(new Vector3D(0, 1, 0)), f.ToLocal(new Vector3D(0, 0, 1)));
    }

    /// <summary>
    /// The map that lays plate <paramref name="a"/> onto <paramref name="b"/>: a turn and shift in the plane, after
    /// turning it over (y and z reversed, z from the far face) if need be.
    /// </summary>
    static Frame? Plates(PlateShape a, PlateShape b)
    {
        if (Math.Abs(a.Thickness - b.Thickness) > Tolerance) return null;
        var (ea, eb) = (a.Edges, b.Edges);
        if (ea.Count != eb.Count) return null;
        if (eb.Where(e => e.Kind != OutlineEdgeKind.Circle).Take(1).ToList() is not [var anchor]) return null;
        foreach (var e in ea.Where(e => e.Kind == anchor.Kind && Math.Abs(e.Length - anchor.Length) <= Tolerance && Math.Abs(e.Radius - anchor.Radius) <= Tolerance))
        foreach (var over in new[] { false, true })
        foreach (var (p, q) in new[] { (e.From, e.To), (e.To, e.From) })
        {
            // The turn and shift that take p, q (turned over first) onto the anchor's ends.
            Point2D Flip(Point2D v) => over ? new(v.X, -v.Y) : v;
            var (fp, fq) = (Flip(p), Flip(q));
            var turn = Angle(anchor.To - anchor.From) - Angle(fq - fp);
            var (cos, sin) = (Math.Cos(turn), Math.Sin(turn));
            Point2D Map(Point2D v)
            {
                var f = Flip(v);
                var x = cos * (f.X - fp.X) - sin * (f.Y - fp.Y);
                var y = sin * (f.X - fp.X) + cos * (f.Y - fp.Y);
                return new(anchor.From.X + x, anchor.From.Y + y);
            }
            if (!ea.All(x => eb.Any(y => Same(x, y, Map)))) continue;
            // In 3D: (x, y, z) → (Map(x, y), z), or (Map(x, y), t − z) turned over.
            var origin = Map(new Point2D(0, 0));
            var (ux, uy) = (Map(new Point2D(1, 0)) - origin, Map(new Point2D(0, 1)) - origin);
            return new Frame(new Point3D(origin.X, origin.Y, over ? a.Thickness : 0), new Vector3D(ux.X, ux.Y, 0), new Vector3D(uy.X, uy.Y, 0), new Vector3D(0, 0, over ? -1 : 1));
        }
        return null;
    }

    static double Angle(Vector2D v) => Math.Atan2(v.Y, v.X);

    // Two edges are the same when one maps onto the other: kind, radius, both ends either way round, and the middle.
    static bool Same(OutlineEdge x, OutlineEdge y, Func<Point2D, Point2D> map)
    {
        if (x.Kind != y.Kind || Math.Abs(x.Radius - y.Radius) > Tolerance) return false;
        bool Near(Point2D p, Point2D q) => (map(p) - q).Length <= Tolerance;
        return Near(x.Middle, y.Middle) && (Near(x.From, y.From) && Near(x.To, y.To) || Near(x.From, y.To) && Near(x.To, y.From));
    }

    /// <summary>
    /// The map that lays tube <paramref name="a"/> onto <paramref name="b"/>: a turn about the axis, after turning it
    /// end for end (y and z reversed, z from the far end) if need be.
    /// </summary>
    static Frame? Tubes(TubeShape a, TubeShape b)
    {
        if (Math.Abs(a.Outside - b.Outside) > Tolerance || Math.Abs(a.Wall - b.Wall) > Tolerance || Math.Abs(a.Length - b.Length) > Tolerance) return null;
        foreach (var flipped in new[] { false, true })
        {
            static Vector3D Flip(Vector3D v) => new(v.X, -v.Y, -v.Z);
            var (na, nb) = flipped ? (Flip(a.EndB), Flip(a.EndA)) : (a.EndA, a.EndB);
            // The turn that takes the first leaning end's lean onto b's.
            var lean = Math.Sqrt(na.X * na.X + na.Y * na.Y) > 1e-9 ? (na, b.EndA) : (nb, b.EndB);
            var turn = Math.Atan2(lean.Item2.Y, lean.Item2.X) - Math.Atan2(lean.Item1.Y, lean.Item1.X);
            var (cos, sin) = (Math.Cos(turn), Math.Sin(turn));
            Vector3D Turn(Vector3D v) => new(cos * v.X - sin * v.Y, sin * v.X + cos * v.Y, v.Z);
            if ((Turn(na) - b.EndA).Length > Tolerance || (Turn(nb) - b.EndB).Length > Tolerance) continue;
            var (x, y) = (new Vector3D(cos, sin, 0), new Vector3D(-sin, cos, 0));
            return flipped
                ? new Frame(new Point3D(0, 0, a.Length), x, y * -1, new Vector3D(0, 0, -1))
                : new Frame(new Point3D(0, 0, 0), x, y, new Vector3D(0, 0, 1));
        }
        return null;
    }
}
