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

        var points = Edges.SelectMany(e => Sample(body[e])).ToList();
        Box = (new Point3D(points.Min(p => p.X), points.Min(p => p.Y), points.Min(p => p.Z)),
               new Point3D(points.Max(p => p.X), points.Max(p => p.Y), points.Max(p => p.Z)));
    }

    public Surface Surface { get; }
    public bool SameSense { get; }
    public IReadOnlyList<EdgeId> Edges { get; }
    public IReadOnlyList<ProfileLoop> Loops { get; }
    public double UMin { get; }
    public double VMin { get; }
    public (Point3D Min, Point3D Max) Box { get; }

    static IEnumerable<Point3D> Sample(Edge e)
    {
        var n = e.Curve.Period is null ? 1 : 32;
        for (var i = 0; i <= n; i++) yield return e.Curve.At(e.T0 + (e.T1 - e.T0) * i / n);
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

    /// <summary>The outward normal of the solid at a point of the face.</summary>
    public Vector3D Normal(Point3D p)
    {
        var uv = Uv.Of(Surface, p);
        return Surface.Normal(uv.X, uv.Y) * (SameSense ? 1 : -1);
    }

    public bool Overlaps((Point3D Min, Point3D Max) b) =>
        Box.Min.X <= b.Max.X + Intersections.Snap && b.Min.X <= Box.Max.X + Intersections.Snap &&
        Box.Min.Y <= b.Max.Y + Intersections.Snap && b.Min.Y <= Box.Max.Y + Intersections.Snap &&
        Box.Min.Z <= b.Max.Z + Intersections.Snap && b.Min.Z <= Box.Max.Z + Intersections.Snap;
}

/// <summary>A body's faces in flat form, for locating points against it.</summary>
internal sealed class Solid(Body body)
{
    public Body Body { get; } = body;
    public FlatFace[] Faces { get; } = [.. Enumerable.Range(0, body.Faces.Length).Select(f => new FlatFace(body, new FaceId(f)))];

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
            foreach (var t in Intersections.Hits(p, d, f.Surface))
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
