using BasAutomation.Geometry;

namespace Isoform.Brep;

internal enum Place { Outside, Boundary, Inside }

/// <summary>Where a point lies against a solid: outside, inside, or on its boundary with the normal pointing the same way or not.</summary>
internal enum Containment { Out, In, OnSame, OnOpposite }

/// <summary>
/// A face in flat (u, v) (see <see cref="Uv"/>): its loops as segments, and the window of u its points are taken in
/// on a cylinder, so that every point of the face has one flat position.
/// </summary>
internal sealed class FlatFace
{
    public FlatFace(Body body, FaceId id)
    {
        var face = body[id];
        Surface = face.Surface;
        SameSense = face.SameSense;
        Edges = [.. face.Loops.SelectMany(body.CoedgesOf).Select(c => body[c].Edge).Distinct()];
        Loops = [.. face.Loops.Select(l => new ProfileLoop([.. body.CoedgesOf(l).Select(c =>
        {
            var (from, to) = body.ParametersOf(c);
            return Uv.Flat(Surface, body[c].Pcurve, from, to);
        })]))];
        // The windows of a periodic u or v: from the lowest the face's p-curves reach.
        var ends = face.Loops.SelectMany(body.CoedgesOf).SelectMany(c =>
        {
            var (from, to) = body.ParametersOf(c);
            return new[] { body[c].Pcurve.At(from), body[c].Pcurve.At(to) };
        }).ToList();
        if (Surface.PeriodU is not null) UMin = ends.Min(p => p.X);
        if (Surface.PeriodV is not null) VMin = ends.Min(p => p.Y);

        // The edges' ends and the points where they turn back along an axis, grown to hold the whole face: a plane,
        // cylinder or cone face lies between its edges (on lines across it); a torus face reaches at most the tube's
        // diameter from them; a sphere face may bulge anywhere on the sphere (a ball's edges are only its seam and poles).
        var points = Edges.SelectMany(e => Extremes(body[e])).ToList();
        if (Surface is Sphere o)
        {
            var r = new Vector3D(o.Radius, o.Radius, o.Radius);
            points.AddRange([o.Frame.Origin - r, o.Frame.Origin + r]);
        }
        var pad = (Surface is Torus t ? 2 * t.Minor : 0) + Intersections.Snap;
        Box = (new Point3D(points.Min(p => p.X) - pad, points.Min(p => p.Y) - pad, points.Min(p => p.Z) - pad),
               new Point3D(points.Max(p => p.X) + pad, points.Max(p => p.Y) + pad, points.Max(p => p.Z) + pad));
    }

    public Surface Surface { get; }
    public bool SameSense { get; }
    public IReadOnlyList<EdgeId> Edges { get; }
    public IReadOnlyList<ProfileLoop> Loops { get; }
    public double UMin { get; }
    public double VMin { get; }
    public (Point3D Min, Point3D Max) Box { get; }

    // An edge's ends, and on a circle or ellipse (O + a cos t X + b sin t Y) the points within it where a coordinate
    // is least or greatest: tan t = b Y_i / (a X_i).
    static IEnumerable<Point3D> Extremes(Edge e)
    {
        yield return e.Curve.At(e.T0);
        yield return e.Curve.At(e.T1);
        var (frame, a, b) = e.Curve switch
        {
            Circle3 c => (c.Frame, c.Radius, c.Radius),
            Ellipse3 el => (el.Frame, el.Major, el.Minor),
            _ => (RigidTransform3D.Identity, 0.0, 0.0),
        };
        if (a == 0) yield break;
        foreach (var (x, y) in new[] { (frame.XAxis.X, frame.YAxis.X), (frame.XAxis.Y, frame.YAxis.Y), (frame.XAxis.Z, frame.YAxis.Z) })
        {
            var t = Math.Atan2(b * y, a * x);
            foreach (var s in new[] { t, t + Math.PI })
                if (e.T0 + Intersections.Wrap(s - e.T0) <= e.T1) yield return e.Curve.At(s);
        }
    }

    /// <summary>Whether the ray from p along d meets the face's box.</summary>
    public bool Ahead(Point3D p, Vector3D d)
    {
        var (near, far) = (0.0, double.MaxValue);
        foreach (var (o, v, lo, hi) in new[] { (p.X, d.X, Box.Min.X, Box.Max.X), (p.Y, d.Y, Box.Min.Y, Box.Max.Y), (p.Z, d.Z, Box.Min.Z, Box.Max.Z) })
        {
            if (v == 0)
            {
                if (o < lo || o > hi) return false;
                continue;
            }
            var (t0, t1) = ((lo - o) / v, (hi - o) / v);
            (near, far) = (Math.Max(near, Math.Min(t0, t1)), Math.Min(far, Math.Max(t0, t1)));
        }
        return near <= far;
    }

    /// <summary>A point's flat position on this face (u in the face's window).</summary>
    public Point2D FlatOf(Point3D p) => FlatOf(Uv.Of(Surface, p));

    public Point2D FlatOf(Point2D uv)
    {
        var (du, dv) = ShiftInto(uv);
        return new Point2D((uv.X + du) * Uv.Scale(Surface), (uv.Y + dv) * Uv.ScaleV(Surface));
    }

    /// <summary>The shifts (multiples of 2π) that take (u, v) into the face's windows.</summary>
    public (double U, double V) ShiftInto(Point2D uv) => (
        Surface.PeriodU is null ? 0 : UMin - 1e-9 + Intersections.Wrap(uv.X - (UMin - 1e-9)) - uv.X,
        Surface.PeriodV is null ? 0 : VMin - 1e-9 + Intersections.Wrap(uv.Y - (VMin - 1e-9)) - uv.Y);

    /// <summary>Where a point on (or very near) the face's surface lies against the face.</summary>
    public Place Locate(Point3D p) => Locate(FlatOf(p));

    public Place Locate(Point2D flat)
    {
        foreach (var loop in Loops)
            if (loop.Segments.Any(s => Distances.To(s, flat) <= Intersections.Snap)) return Place.Boundary;
        return Loops.Sum(l => l.Winding(flat)) != 0 ? Place.Inside : Place.Outside;
    }

    /// <summary>
    /// Whether this face crosses the other's surface at a point inside the other face: on a grid over this face (and
    /// along its loops), wherever the side of the other's surface changes between neighbours that are on this face,
    /// the crossing found by bisection and located on the other face. A crossing finer than the grid can be missed.
    /// </summary>
    public bool Crosses(FlatFace other)
    {
        var (ku, kv) = (Uv.Scale(Surface), Uv.ScaleV(Surface));
        Point3D On(Point2D q) => Surface.At(q.X / ku, q.Y / kv);
        double Side(Point2D q) => Uv.Side(other.Surface, On(q));
        bool Across(Point2D a, Point2D b)
        {
            var (sa, sb) = (Side(a), Side(b));
            if (!(sa * sb < 0) || Math.Abs(sa) <= Intersections.Snap || Math.Abs(sb) <= Intersections.Snap) return false;
            for (var k = 0; k < 60; k++)
            {
                var m = new Point2D((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                if (Side(m) * sa > 0) a = m; else b = m;
            }
            var p = On(new Point2D((a.X + b.X) / 2, (a.Y + b.Y) / 2));
            return other.Locate(p) == Place.Inside;
        }

        // Along the loops.
        foreach (var loop in Loops)
        {
            var points = loop.Segments.SelectMany(s => s is CurveSegment c ? c.Samples() : Enumerable.Range(0, 17).Select(i => Along(s, i / 16.0))).ToList();
            for (var i = 0; i + 1 < points.Count; i++)
                if (Across(points[i], points[i + 1])) return true;
        }

        // Across a grid, between neighbours on the face.
        var flat = Loops.SelectMany(l => l.Segments).SelectMany(s => s is CurveSegment c ? c.Samples() : [s.Start, s.End]).ToList();
        var (x0, x1, y0, y1) = (flat.Min(q => q.X), flat.Max(q => q.X), flat.Min(q => q.Y), flat.Max(q => q.Y));
        const int n = 24;
        var grid = new Point2D[n + 1, n + 1];
        var on = new bool[n + 1, n + 1];
        for (var i = 0; i <= n; i++)
        for (var j = 0; j <= n; j++)
        {
            grid[i, j] = new Point2D(x0 + (x1 - x0) * i / n, y0 + (y1 - y0) * j / n);
            on[i, j] = Locate(grid[i, j]) != Place.Outside;
        }
        for (var i = 0; i <= n; i++)
        for (var j = 0; j <= n; j++)
        {
            if (!on[i, j]) continue;
            if (i < n && on[i + 1, j] && Across(grid[i, j], grid[i + 1, j])) return true;
            if (j < n && on[i, j + 1] && Across(grid[i, j], grid[i, j + 1])) return true;
        }
        return false;
    }

    static Point2D Along(Segment s, double f) => s switch
    {
        LineSegment l => new Point2D(l.From.X + (l.To.X - l.From.X) * f, l.From.Y + (l.To.Y - l.From.Y) * f),
        ArcSegment a => a.PointAt(a.StartAngle + a.Sweep * f),
        _ => throw new NotSupportedException(s.GetType().Name),
    };

    /// <summary>The outward normal of the solid at a point of the face.</summary>
    public Vector3D Normal(Point3D p)
    {
        var uv = Uv.Of(Surface, p);
        return Surface.Normal(uv.X, uv.Y) * (SameSense ? 1 : -1);
    }

    public bool Overlaps((Point3D Min, Point3D Max) b) =>
        Box.Min.X <= b.Max.X && b.Min.X <= Box.Max.X && Box.Min.Y <= b.Max.Y && b.Min.Y <= Box.Max.Y && Box.Min.Z <= b.Max.Z && b.Min.Z <= Box.Max.Z;
}

/// <summary>A body's faces in flat form, for locating points against it.</summary>
internal sealed class Solid(Body body)
{
    public Body Body { get; } = body;
    public FlatFace[] Faces { get; } = [.. Enumerable.Range(0, body.Faces.Length).Select(f => new FlatFace(body, new FaceId(f)))];

    /// <summary>A box round the whole solid.</summary>
    public (Point3D Min, Point3D Max) Box => box ??= (
        new Point3D(Faces.Min(f => f.Box.Min.X), Faces.Min(f => f.Box.Min.Y), Faces.Min(f => f.Box.Min.Z)),
        new Point3D(Faces.Max(f => f.Box.Max.X), Faces.Max(f => f.Box.Max.Y), Faces.Max(f => f.Box.Max.Z)));

    (Point3D, Point3D)? box;

    // Ray directions in no special relation to the axes of any part.
    static readonly Vector3D[] Rays =
    [
        new Vector3D(0.5773, 0.6123, 0.5401).Normalized(),
        new Vector3D(-0.3412, 0.8231, -0.4532).Normalized(),
        new Vector3D(0.7071, -0.1829, 0.6833).Normalized(),
        new Vector3D(-0.6131, -0.5507, 0.5664).Normalized(),
        new Vector3D(0.2219, -0.9318, -0.2873).Normalized(),
    ];

    /// <summary>
    /// Where <paramref name="p"/> lies against the solid. On its boundary, <paramref name="normal"/> (the outward
    /// normal of the face p comes from) says whether the two faces point the same way.
    /// </summary>
    public Containment Classify(Point3D p, Vector3D normal)
    {
        foreach (var f in Faces)
        {
            if (Uv.Distance(f.Surface, p) > Intersections.Snap) continue;
            if (f.Locate(p) == Place.Outside) continue;
            return f.Normal(p).Dot(normal) > 0 ? Containment.OnSame : Containment.OnOpposite;
        }
        return Inside(p) ? Containment.In : Containment.Out;
    }

    /// <summary>Whether a point off the boundary is inside: the parity of a ray's crossings, retried when a ray grazes an edge.</summary>
    public bool Inside(Point3D p)
    {
        foreach (var d in Rays)
        {
            var count = 0;
            var clean = true;
            foreach (var f in Faces)
            foreach (var t in f.Ahead(p, d) ? Intersections.Hits(p, d, f.Surface) : [])
            {
                if (t <= Intersections.Snap) continue;
                var q = p + d * t;
                var place = f.Locate(q);
                if (place == Place.Boundary || (place == Place.Inside && Math.Abs(f.Normal(q).Dot(d)) < 1e-6)) clean = false;
                else if (place == Place.Inside) count++;
            }
            if (clean) return count % 2 == 1;
        }
        throw new BooleanFailure("every ray from a point grazes an edge of the other solid");
    }
}

/// <summary>A boolean that cannot be completed, with the reason.</summary>
public sealed class BooleanFailure(string message) : Exception(message);
