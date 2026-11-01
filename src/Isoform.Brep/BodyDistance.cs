using BasAutomation.Geometry;

namespace Isoform.Brep;

/// <summary>
/// The signed distance to a body's boundary (negative inside), the face nearest and the way away from it: what a field
/// needs to take a solid on. A face's nearest point is its surface's nearest point where that lies on the face, else
/// the nearest point of its edges; faces whose box is farther than the best so far are skipped. The sign is the side
/// of the nearest face where the nearest point lies inside it, else (nearest an edge or corner) the solid's own
/// inside test; on the boundary, to rounding, the distance is exactly 0.
/// </summary>
public sealed class BodyDistance(Body body)
{
    readonly Solid solid = new(body);

    public Body Body { get; } = body;

    /// <summary>A box round the body.</summary>
    public (Point3D Min, Point3D Max) Box => solid.Box;

    public (double Value, FaceId Face, Vector3D Gradient) Sample(Point3D p)
    {
        var (best, face, nearest, inside) = (double.MaxValue, -1, p, false);
        foreach (var f in Enumerable.Range(0, solid.Faces.Length).OrderBy(f => Away(solid.Faces[f].Box, p)))
        {
            if (Away(solid.Faces[f].Box, p) >= best) break;
            var (q, onFace) = Nearest(f, p);
            var d = (p - q).Length;
            if (d < best) (best, face, nearest, inside) = (d, f, q, onFace);
        }
        var normal = solid.Faces[face].Normal(nearest);
        // On the boundary (to rounding): exactly 0, as a plane through the point gives, not a sign the rounding picks.
        if (best <= 1e-12) return (0, new FaceId(face), normal);
        var sign = inside ? ((p - nearest).Dot(normal) >= 0 ? 1 : -1) : solid.Inside(p) ? -1 : 1;
        var gradient = best > 1e-12 ? (p - nearest) * (sign / best) : normal;
        return (sign * best, new FaceId(face), gradient);
    }

    // How far a point is from a box (0 inside it).
    static double Away((Point3D Min, Point3D Max) box, Point3D p)
    {
        static double Gap(double x, double lo, double hi) => x < lo ? lo - x : x > hi ? x - hi : 0;
        var (dx, dy, dz) = (Gap(p.X, box.Min.X, box.Max.X), Gap(p.Y, box.Min.Y, box.Max.Y), Gap(p.Z, box.Min.Z, box.Max.Z));
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    // The face's nearest point, and whether it lies inside the face (not on its edges).
    (Point3D Point, bool Inside) Nearest(int f, Point3D p)
    {
        var face = solid.Faces[f];
        var foot = Foot(face.Surface, p);
        if (face.Locate(foot) != Place.Outside) return (foot, true);
        var (best, nearest) = (double.MaxValue, p);
        foreach (var e in face.Edges)
        {
            var q = OnEdge(Body[e], p);
            var d = (p - q).Length;
            if (d < best) (best, nearest) = (d, q);
        }
        return (nearest, false);
    }

    // The nearest point of a surface (any, where several are: on a cylinder's axis, at a sphere's centre).
    static Point3D Foot(Surface s, Point3D p)
    {
        switch (s)
        {
            case Plane q:
                return p - q.Frame.ZAxis * (p - q.Frame.Origin).Dot(q.Frame.ZAxis);
            case Sphere o:
            {
                var w = p - o.Frame.Origin;
                return o.Frame.Origin + (w.Length > 1e-12 ? w.Normalized() : o.Frame.XAxis) * o.Radius;
            }
            default:
            {
                // Round an axis: in the half-plane through p, (distance from the axis, height along it).
                var (frame, z) = s switch
                {
                    Cylinder c => (c.Frame, c.Frame.ZAxis),
                    Cone k => (k.Frame, k.Frame.ZAxis),
                    Torus t => (t.Frame, t.Frame.ZAxis),
                    _ => throw new NotSupportedException(s.GetType().Name),
                };
                var w = p - frame.Origin;
                var h = w.Dot(z);
                var radial = w - z * h;
                var away = radial.Length > 1e-12 ? radial.Normalized() : frame.XAxis;
                var (rho, height) = s switch
                {
                    Cylinder c => (c.Radius, h),
                    Cone k => OnLine(k, radial.Length, h),
                    Torus t => OnTube(t, radial.Length, h),
                    _ => throw new NotSupportedException(s.GetType().Name),
                };
                return frame.Origin + away * rho + z * height;
            }
        }
    }

    // The cone's generator ρ = R + s·h nearest (ρ, h), no nearer the axis than its apex.
    static (double, double) OnLine(Cone k, double rho, double h)
    {
        var len = Math.Sqrt(1 + k.Slope * k.Slope);
        var (dr, dh) = (k.Slope / len, 1 / len);
        var t = (rho - k.Radius) * dr + h * dh;
        var (r, z) = (k.Radius + dr * t, dh * t);
        return r >= 0 ? (r, z) : (0, -k.Radius / k.Slope);
    }

    // The tube's circle in the half-plane nearest (ρ, h).
    static (double, double) OnTube(Torus t, double rho, double h)
    {
        var (dr, dh) = (rho - t.Major, h);
        var len = Math.Sqrt(dr * dr + dh * dh);
        return len > 1e-12 ? (t.Major + dr / len * t.Minor, dh / len * t.Minor) : (t.Major + t.Minor, 0);
    }

    // The nearest point of an edge.
    static Point3D OnEdge(Edge e, Point3D p)
    {
        switch (e.Curve)
        {
            case Pole q:
                return q.Point;
            case Line3 l:
                return l.At(Math.Clamp((p - l.Origin).Dot(l.Direction), e.T0, e.T1));
            case Circle3 c:
            {
                var w = p - c.Frame.Origin;
                var a = Math.Atan2(w.Dot(c.Frame.YAxis), w.Dot(c.Frame.XAxis));
                var t = e.T0 + Intersections.Wrap(a - e.T0);
                if (t <= e.T1) return c.At(t);
                return (c.At(e.T0) - p).Length <= (c.At(e.T1) - p).Length ? c.At(e.T0) : c.At(e.T1);
            }
            default:
            {
                // A sample of the edge, then a golden-section search round the nearest.
                const int n = 64;
                double D(double t) => (e.Curve.At(t) - p).Length;
                var step = (e.T1 - e.T0) / n;
                var k = Enumerable.Range(0, n + 1).MinBy(i => D(e.T0 + step * i));
                var (lo, hi) = (Math.Max(e.T0, e.T0 + step * (k - 1)), Math.Min(e.T1, e.T0 + step * (k + 1)));
                var g = (Math.Sqrt(5) - 1) / 2;
                for (var i = 0; i < 60; i++)
                {
                    var (x1, x2) = (hi - g * (hi - lo), lo + g * (hi - lo));
                    if (D(x1) < D(x2)) hi = x2; else lo = x1;
                }
                return e.Curve.At((lo + hi) / 2);
            }
        }
    }
}
