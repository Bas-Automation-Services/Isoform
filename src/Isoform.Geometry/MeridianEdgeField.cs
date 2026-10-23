using BasAutomation.Geometry;

namespace Isoform.Geometry;

/// <summary>
/// Exact rolling-ball fillet on the convex edge between two surfaces of revolution round one axis, one of them a
/// sphere or torus. In the (ρ, z) half-plane each is a line or a circle and its distance the 2D distance there; the
/// ball centre C is where both, moved inward by r, meet. In the sector, the wedge at C between the two normals there,
/// the field is |(ρ, z) − C| − r, the distance to the ball centre circle less r; elsewhere it is the sharp max(A, B).
/// </summary>
/// <remarks>
/// C lies r inside both surfaces, so |(ρ, z) − C| − r ≥ max(A, B) everywhere (for a circle by the triangle
/// inequality), and on each ray of the wedge it equals the surface the ray is normal to. The field is therefore
/// continuous, and made of 1-Lipschitz pieces, 1-Lipschitz. That needs each ray to stay on its surface's normal line
/// across the half-plane: true for a line, a circle holding its material inside, and a hollow sphere (whose centre
/// is on the axis). A hollow torus is refused: past its tube's centre the ray would leave the normal line.
/// </remarks>
public sealed class MeridianEdgeField : RoundedEdgeField
{
    readonly Point3D _origin;
    readonly Vector3D _axis;
    readonly Vector3D _x;
    readonly double _rho;
    readonly (double R, double Z) _ua;
    readonly (double R, double Z) _ub;
    readonly double _det;
    readonly Point3D _edge;

    MeridianEdgeField(LeafField a, LeafField b, double radius, Surface fillet, Circle path, (double R, double Z) ua, (double R, double Z) ub, Point3D edge)
        : base(a, b, radius, fillet)
    {
        (_origin, _axis, _x, _rho, _ua, _ub, _edge) = (path.Center, path.Normal, path.X, path.Radius, ua, ub, edge);
        _det = ua.R * ub.Z - ua.Z * ub.R;
    }

    /// <summary>The ball centre circle: round the axis, in the plane of C.</summary>
    public Circle Path => new(_origin, _axis, _x, _rho);

    /// <summary>The fillet through the edge point <paramref name="onEdge"/>, or null when the ball does not fit or the pair is refused.</summary>
    public static new MeridianEdgeField? Of(LeafField a, LeafField b, double radius, Surface fillet, Point3D onEdge)
    {
        static bool Fits(LeafField l, double r) => l.Surface.Kind switch
        {
            SurfaceKind.Torus => !l.Complement && l.Surface.Minor - r > 1e-9,
            SurfaceKind.Sphere => l.Complement || l.Surface.Radius - r > 1e-9,
            _ => true,
        };
        if (!Fits(a, radius) || !Fits(b, radius)) return null;
        if (SurfaceIntersection.Curve(Inward(a, radius), Inward(b, radius), onEdge) is not Circle path) return null;
        var centre = path.Center + path.X * path.Radius;
        (double R, double Z) Normal(LeafField l)
        {
            var g = l.Sample(centre).Gradient;
            return (g.Dot(path.X), g.Dot(path.Normal));
        }
        var field = new MeridianEdgeField(a, b, radius, fillet, path, Normal(a), Normal(b), onEdge);
        if (Math.Abs(field._det) < 1e-9) return null;
        // The edge lies in the sector: the edge is convex.
        var (rho, z, _) = field.Map(onEdge);
        var (alpha, beta) = field.Sector(rho - field._rho, z);
        return alpha > 0 && beta > 0 ? field : null;
    }

    public override RoundedEdgeField With(LeafField a, LeafField b, double radius, Surface fillet) => Of(a, b, radius, fillet, _edge)!;

    public override FieldSample Sample(Point3D p)
    {
        var sa = A.Sample(p);
        var sb = B.Sample(p);
        var (rho, z, unit) = Map(p);
        var (alpha, beta) = Sector(rho - _rho, z);
        if (alpha < 0 || beta < 0) return sa.Value >= sb.Value ? sa : sb;
        return Round(rho - _rho, z, unit, sa.Gradient);
    }

    public override FieldSample? SampleSurface(int owner, Point3D p)
    {
        if (owner != Fillet.Id) return A.SampleSurface(owner, p) ?? B.SampleSurface(owner, p);
        var (rho, z, unit) = Map(p);
        return Round(rho - _rho, z, unit, A.Sample(p).Gradient);
    }

    /// <summary>The sharp bounds below (rounding only removes material); in reach of the sector, the distance to C above.</summary>
    public override Interval Bounds(Box3D box)
    {
        var sharp = Interval.Max(A.Bounds(box), B.Bounds(box));
        var (alpha, beta) = Reach(box);
        if (alpha.Hi < 0 || beta.Hi < 0) return sharp;
        var (rho, z, _) = Map(box.Center);
        var far = Math.Sqrt((rho - _rho) * (rho - _rho) + z * z) + HalfDiagonal(box) - Radius;
        return new Interval(sharp.Lo, Math.Max(sharp.Hi, far)).Intersect(LipschitzBounds(box));
    }

    public override Field Simplify(Box3D box)
    {
        var (alpha, beta) = Reach(box);
        return alpha.Hi < 0 || beta.Hi < 0 ? Sharp.Simplify(box) : this;
    }

    /// <summary>Inside the sector only the fillet: a torus round a circle of radius R with tube r curves no tighter than min(r, R − r).</summary>
    public override double? SinglePatch(Box3D box)
    {
        var (alpha, beta) = Reach(box);
        if (alpha.Lo < 1e-9 || beta.Lo < 1e-9 || _rho - Radius <= 1e-9) return null;
        return Math.Min(Radius, _rho - Radius);
    }

    public override (Point3D Centre, Point3D OnA, Point3D OnB) Contacts(Point3D q)
    {
        var (_, _, unit) = Map(q);
        var centre = _origin + unit * _rho;
        return (centre, centre + (unit * _ua.R + _axis * _ua.Z) * Radius, centre + (unit * _ub.R + _axis * _ub.Z) * Radius);
    }

    FieldSample Round(double wr, double wz, Vector3D unit, Vector3D fallback)
    {
        var len = Math.Sqrt(wr * wr + wz * wz);
        return len > 0 ? new(len - Radius, Fillet.Id, (unit * wr + _axis * wz) / len) : new(-Radius, Fillet.Id, fallback);
    }

    /// <summary>(ρ, z) − C = α·uA + β·uB.</summary>
    (double Alpha, double Beta) Sector(double wr, double wz) =>
        ((wr * _ub.Z - wz * _ub.R) / _det, (_ua.R * wz - _ua.Z * wr) / _det);

    /// <summary>Enclosures of α and β over a box, from enclosures of ρ (1-Lipschitz) and z (linear).</summary>
    (Interval Alpha, Interval Beta) Reach(Box3D box)
    {
        var (rho, z, _) = Map(box.Center);
        var h = HalfDiagonal(box);
        var dz = (Math.Abs(_axis.X) * (box.Max.X - box.Min.X) + Math.Abs(_axis.Y) * (box.Max.Y - box.Min.Y) + Math.Abs(_axis.Z) * (box.Max.Z - box.Min.Z)) / 2;
        var wr = new Interval(Math.Max(0, rho - h) - _rho, rho + h - _rho);
        var wz = new Interval(z - dz, z + dz);
        return (Linear(_ub.Z / _det, wr, -_ub.R / _det, wz), Linear(-_ua.Z / _det, wr, _ua.R / _det, wz));
    }

    static Interval Linear(double k1, Interval x, double k2, Interval y) =>
        new(Math.Min(k1 * x.Lo, k1 * x.Hi) + Math.Min(k2 * y.Lo, k2 * y.Hi), Math.Max(k1 * x.Lo, k1 * x.Hi) + Math.Max(k2 * y.Lo, k2 * y.Hi));

    (double Rho, double Z, Vector3D Unit) Map(Point3D p)
    {
        var v = p - _origin;
        var z = v.Dot(_axis);
        var radial = v - _axis * z;
        var rho = radial.Length;
        return (rho, z, rho > 0 ? radial / rho : _x);
    }
}
