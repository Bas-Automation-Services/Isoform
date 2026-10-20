using BasAutomation.Geometry;

namespace Isoform.Geometry;

/// <summary>
/// One edge of a closed 2D profile from (U0, V0) to (U1, V1): a segment, or an arc when <see cref="Bulge"/> is not
/// zero. Bulge = tan(θ/4) with θ the arc's included angle, positive when the arc turns left (counter-clockwise).
/// </summary>
public sealed class ProfileEdge2D
{
    public ProfileEdge2D(double u0, double v0, double u1, double v1, double bulge)
    {
        (U0, V0, U1, V1, Bulge) = (u0, v0, u1, v1, bulge);
        if (bulge == 0) return;
        var (du, dv) = (u1 - u0, v1 - v0);
        var chord = Math.Sqrt(du * du + dv * dv);
        // The centre sits left of the chord by (chord/2)·cot(θ/2), and cot(θ/2) = (1 − b²)/2b.
        var offset = (1 - bulge * bulge) / (4 * bulge);
        (Cu, Cv) = ((u0 + u1) / 2 - dv * offset, (v0 + v1) / 2 + du * offset);
        R = chord * (1 + bulge * bulge) / (4 * Math.Abs(bulge));
        A0 = Math.Atan2(v0 - Cv, u0 - Cu);
        Sweep = 4 * Math.Atan(bulge);
    }

    public double U0 { get; }
    public double V0 { get; }
    public double U1 { get; }
    public double V1 { get; }
    public double Bulge { get; }
    public bool IsArc => Bulge != 0;
    public double Cu { get; }
    public double Cv { get; }
    public double R { get; }

    /// <summary>Start angle of the arc round its centre.</summary>
    public double A0 { get; }

    /// <summary>Included angle θ, signed: positive counter-clockwise.</summary>
    public double Sweep { get; }

    public ProfileEdge2D Reversed() => new(U1, V1, U0, V0, -Bulge);

    /// <summary>Squared distance from (u, v) and where the nearest point is: −1 inside the edge, 0 its start, 1 its end.</summary>
    public (double Squared, int Where) Closest(double u, double v)
    {
        if (IsArc)
        {
            if (WithinSweep(u, v))
            {
                var radial = Math.Sqrt((u - Cu) * (u - Cu) + (v - Cv) * (v - Cv)) - R;
                return (radial * radial, -1);
            }
        }
        else
        {
            var (du, dv) = (U1 - U0, V1 - V0);
            var t = ((u - U0) * du + (v - V0) * dv) / (du * du + dv * dv);
            if (t > 0 && t < 1)
            {
                var (eu, ev) = (u - U0 - t * du, v - V0 - t * dv);
                return (eu * eu + ev * ev, -1);
            }
        }
        var s0 = (u - U0) * (u - U0) + (v - V0) * (v - V0);
        var s1 = (u - U1) * (u - U1) + (v - V1) * (v - V1);
        return s0 <= s1 ? (s0, 0) : (s1, 1);
    }

    /// <summary>True when every point of the box is nearest to the inside of this edge (not to an end).</summary>
    public bool InteriorCovers(double u0, double v0, double u1, double v1)
    {
        Span<(double U, double V)> corners = [(u0, v0), (u1, v0), (u0, v1), (u1, v1)];
        if (IsArc)
        {
            // The region nearest to the arc's inside is a wedge, convex up to a half turn.
            if (Math.Abs(Sweep) > Math.PI) return false;
            foreach (var (u, v) in corners)
                if (!WithinSweep(u, v)) return false;
            return true;
        }
        var (du, dv) = (U1 - U0, V1 - V0);
        var len2 = du * du + dv * dv;
        foreach (var (u, v) in corners)
        {
            var t = ((u - U0) * du + (v - V0) * dv) / len2;
            if (!(t > 1e-9 && t < 1 - 1e-9)) return false;
        }
        return true;
    }

    public (double U, double V) StartTangent => Tangent(A0, U1 - U0, V1 - V0);
    public (double U, double V) EndTangent => Tangent(A0 + Sweep, U1 - U0, V1 - V0);

    /// <summary>This edge's share of the profile's signed area: the chord's shoelace term plus the arc's segment.</summary>
    public double Area => (U0 * V1 - U1 * V0) / 2 + (IsArc ? R * R / 2 * (Sweep - Math.Sin(Sweep)) : 0);

    /// <summary>Points along the edge, ends included: the chord for a segment, <paramref name="n"/> pieces for an arc.</summary>
    public IEnumerable<(double U, double V)> Points(int n)
    {
        if (!IsArc)
        {
            yield return (U0, V0);
            yield return (U1, V1);
            yield break;
        }
        for (var i = 0; i <= n; i++)
        {
            var a = A0 + Sweep * i / n;
            yield return i == 0 ? (U0, V0) : i == n ? (U1, V1) : (Cu + R * Math.Cos(a), Cv + R * Math.Sin(a));
        }
    }

    bool WithinSweep(double u, double v)
    {
        var a = Math.Atan2(v - Cv, u - Cu);
        var rel = Sweep > 0 ? a - A0 : A0 - a;
        rel %= 2 * Math.PI;
        if (rel < 0) rel += 2 * Math.PI;
        return rel < Math.Abs(Sweep);
    }

    (double, double) Tangent(double angle, double du, double dv)
    {
        if (!IsArc)
        {
            var len = Math.Sqrt(du * du + dv * dv);
            return (du / len, dv / len);
        }
        return Sweep > 0 ? (-Math.Sin(angle), Math.Cos(angle)) : (Math.Sin(angle), -Math.Cos(angle));
    }
}

/// <summary>
/// A solid swept from a closed counter-clockwise profile: extruded along the frame's Z (the profile in its X, Y),
/// or revolved round the frame's Y axis (the profile in (distance from the axis, height along it)). The value is
/// the exact signed distance to the profile in those 2D coordinates, which for a full revolution is also the exact
/// 3D distance. Where the nearest point lies inside an edge the value is that edge's own surface (plane, cylinder,
/// cone, sphere, torus), so a box nearest to one edge simplifies to that leaf bit for bit. Profile edges on the
/// revolve axis are no surface at all and are skipped.
/// </summary>
public sealed class ProfileField : Field
{
    readonly ProfileEdge2D[] _edges;
    readonly LeafField?[] _leaves;
    readonly int[] _vertexSign; // at the end of edge k: +1 convex, −1 reflex, 0 tangent
    readonly Frame _frame;
    readonly bool _revolve;
    readonly double _offset;
    readonly LeafField?[] _corners; // at the end of edge k: the round face an offset gave that corner

    /// <param name="offset">
    /// Grows the solid by this distance: the leaves are then the offset faces, and nearest a corner the value is
    /// the distance to the corner less the offset, so a grown convex corner (or a shrunk reflex one) is round.
    /// </param>
    /// <param name="corners">The face of each such round corner, per corner at the end of edge k.</param>
    public ProfileField(IReadOnlyList<ProfileEdge2D> edges, IReadOnlyList<LeafField?> leaves, Frame frame, bool revolve,
        double offset = 0, IReadOnlyList<LeafField?>? corners = null)
    {
        (_edges, _leaves, _frame, _revolve, _offset) = (edges.ToArray(), leaves.ToArray(), frame, revolve, offset);
        _corners = corners?.ToArray() ?? new LeafField?[_edges.Length];
        _vertexSign = new int[_edges.Length];
        for (var k = 0; k < _edges.Length; k++)
        {
            var (a, b) = (_edges[k].EndTangent, _edges[(k + 1) % _edges.Length].StartTangent);
            var cross = a.U * b.V - a.V * b.U;
            _vertexSign[k] = cross > 1e-12 ? 1 : cross < -1e-12 ? -1 : 0;
        }
        Leaves = _leaves.OfType<LeafField>().Concat(_corners.OfType<LeafField>()).ToList();
    }

    /// <summary>The surface of every edge that has one, and of every round corner.</summary>
    public IReadOnlyList<LeafField> Leaves { get; }

    /// <summary>
    /// The same profile grown by <paramref name="d"/>: <paramref name="offsetLeaf"/> gives each face's offset, and a
    /// corner that turns round gets a face of its own, a cylinder round the corner line (extrude) or a torus or
    /// sphere round the corner circle (revolve), registered under <paramref name="cornerName"/> of its two edges.
    /// </summary>
    public ProfileField Offset(double d, Func<LeafField, LeafField> offsetLeaf, Func<Surface, Surface> register, Func<string, string, string> cornerName)
    {
        var total = _offset + d;
        var corners = new LeafField?[_edges.Length];
        for (var k = 0; k < _edges.Length; k++)
        {
            if (_corners[k] is { } existing) corners[k] = offsetLeaf(existing);
            else if (Math.Abs(total) > 1e-9 && _vertexSign[k] == Math.Sign(total))
            {
                var (u, v) = (_edges[k].U1, _edges[k].V1);
                var next = _leaves[(k + 1) % _edges.Length];
                var name = cornerName(_leaves[k]?.Surface.Source ?? "axis", next?.Surface.Source ?? "axis");
                var r = Math.Abs(total);
                var surface = !_revolve
                    ? new Surface(0, name, SurfaceKind.Cylinder, _frame.ToWorld(new Point3D(u, v, 0)), _frame.Z, r)
                    : Math.Abs(u) <= 1e-9
                        ? new Surface(0, name, SurfaceKind.Sphere, _frame.Origin + _frame.Y * v, _frame.Y, r)
                        : new Surface(0, name, SurfaceKind.Torus, _frame.Origin + _frame.Y * v, _frame.Y, u, r);
                // A grown convex corner holds material inside the round; a shrunk reflex corner outside it.
                corners[k] = new LeafField(register(surface), complement: total < 0);
            }
        }
        return new(_edges, _leaves.Select(l => l is null ? null : offsetLeaf(l)).ToList(), _frame, _revolve, total, corners);
    }

    /// <summary>
    /// The surface a profile edge sweeps, with the profile's inside (its left) as material; null for an edge on
    /// the revolve axis. <paramref name="add"/> registers a surface: kind, origin, axis, radius, minor, angle.
    /// </summary>
    public static LeafField? EdgeLeaf(ProfileEdge2D e, Frame f, bool revolve, Func<SurfaceKind, Point3D, Vector3D, double, double, double, Surface> add)
    {
        const double eps = 1e-9;
        Point3D At(double u, double v) => f.ToWorld(new Point3D(u, v, 0));
        if (!revolve)
        {
            if (e.IsArc) return new LeafField(add(SurfaceKind.Cylinder, At(e.Cu, e.Cv), f.Z, e.R, 0, 0), complement: e.Sweep < 0);
            var len = Math.Sqrt((e.U1 - e.U0) * (e.U1 - e.U0) + (e.V1 - e.V0) * (e.V1 - e.V0));
            var normal = f.X * ((e.V1 - e.V0) / len) - f.Y * ((e.U1 - e.U0) / len);
            return new LeafField(add(SurfaceKind.Plane, At(e.U0, e.V0), normal, 0, 0, 0));
        }

        var axis = f.Y;
        Point3D OnAxis(double v) => f.Origin + axis * v;
        if (e.IsArc)
        {
            return Math.Abs(e.Cu) <= eps
                ? new LeafField(add(SurfaceKind.Sphere, OnAxis(e.Cv), axis, e.R, 0, 0), complement: e.Sweep < 0)
                : new LeafField(add(SurfaceKind.Torus, OnAxis(e.Cv), axis, e.Cu, e.R, 0), complement: e.Sweep < 0);
        }
        var (du, dv) = (e.U1 - e.U0, e.V1 - e.V0);
        if (Math.Abs(e.U0) <= eps && Math.Abs(e.U1) <= eps) return null;
        var l = Math.Sqrt(du * du + dv * dv);
        // Outward normal in (ρ, z): to the right of travel.
        var (nRho, nZ) = (dv / l, -du / l);
        if (Math.Abs(du) <= eps * l)
            return new LeafField(add(SurfaceKind.Cylinder, f.Origin, axis, e.U0, 0, 0), complement: nRho < 0);
        if (Math.Abs(dv) <= eps * l)
            return new LeafField(add(SurfaceKind.Plane, OnAxis(e.V0), axis * Math.Sign(nZ), 0, 0, 0));
        var apex = e.V0 - e.U0 * dv / du;
        var opening = du / dv > 0 ? axis : -axis;
        return new LeafField(add(SurfaceKind.Cone, OnAxis(apex), opening, 0, 0, Math.Atan(Math.Abs(du / dv))), complement: nRho < 0);
    }

    /// <summary>
    /// True when the whole profile lies on the material side of this leaf's edge: then the edge's surface bounds
    /// the swept solid everywhere, and an edge feature can use it as if it were a face of an intersection.
    /// </summary>
    public bool Supports(LeafField leaf)
    {
        var i = Array.IndexOf(_leaves, leaf);
        if (i < 0) return false;
        var e = _edges[i];
        const double tol = 1e-9;
        foreach (var other in _edges)
        foreach (var (u, v) in other.Points(32))
        {
            var outside = e.IsArc
                ? (Math.Sqrt((u - e.Cu) * (u - e.Cu) + (v - e.Cv) * (v - e.Cv)) - e.R) * Math.Sign(e.Sweep)
                : ((e.U1 - e.U0) * (v - e.V0) - (e.V1 - e.V0) * (u - e.U0)) / -Math.Sqrt((e.U1 - e.U0) * (e.U1 - e.U0) + (e.V1 - e.V0) * (e.V1 - e.V0));
            if (outside > tol) return false;
        }
        return true;
    }

    public override FieldSample Sample(Point3D p)
    {
        var (u, v, radial) = Map(p);
        var (i, where, squared) = Nearest(u, v);
        if (where < 0) return _leaves[i]!.Sample(p);
        var (vu, vv, sign, k) = Vertex(i, where, p);
        var (du, dv) = (u - vu, v - vv);
        var d = Math.Sqrt(du * du + dv * dv);
        var g = d > 0 ? Back(du / d, dv / d, radial) * sign : _leaves[i]!.Sample(p).Gradient;
        return new(sign * d - _offset, (_corners[k] ?? _leaves[i]!).Surface.Id, g);
    }

    public override double Value(Point3D p)
    {
        var (u, v, _) = Map(p);
        var (i, where, _) = Nearest(u, v);
        if (where < 0) return _leaves[i]!.Value(p);
        var (vu, vv, sign, _) = Vertex(i, where, p);
        var (du, dv) = (u - vu, v - vv);
        return sign * Math.Sqrt(du * du + dv * dv) - _offset;
    }

    public override FieldSample? SampleSurface(int owner, Point3D p)
    {
        foreach (var l in Leaves)
            if (l.Surface.Id == owner) return l.Sample(p);
        return null;
    }

    public override Interval Bounds(Box3D box) =>
        Single(box) is { } leaf ? leaf.Bounds(box) : LipschitzBounds(box);

    /// <summary>The leaf of the one edge certainly nearest to every point of the box, from its inside.</summary>
    public override Field Simplify(Box3D box) => (Field?)Single(box) ?? this;

    LeafField? Single(Box3D box)
    {
        var (u0, v0, u1, v1) = Region(box);
        var (cu, cv) = ((u0 + u1) / 2, (v0 + v1) / 2);
        var reach = Math.Sqrt((u1 - u0) * (u1 - u0) + (v1 - v0) * (v1 - v0)) / 2;
        // Distance is 1-Lipschitz: over the region it lies within ±reach of its value at the centre.
        Span<double> centre = _edges.Length <= 64 ? stackalloc double[_edges.Length] : new double[_edges.Length];
        var ceiling = double.PositiveInfinity;
        for (var i = 0; i < _edges.Length; i++)
        {
            centre[i] = _leaves[i] is null ? double.PositiveInfinity : Math.Sqrt(_edges[i].Closest(cu, cv).Squared);
            ceiling = Math.Min(ceiling, centre[i] + reach);
        }
        var only = -1;
        for (var i = 0; i < _edges.Length; i++)
        {
            if (centre[i] - reach > ceiling + 1e-12) continue;
            if (only >= 0) return null;
            only = i;
        }
        return only >= 0 && _edges[only].InteriorCovers(u0, v0, u1, v1) ? _leaves[only] : null;
    }

    /// <summary>A 2D box holding every profile coordinate of the 3D box.</summary>
    (double U0, double V0, double U1, double V1) Region(Box3D box)
    {
        double u0 = double.MaxValue, v0 = double.MaxValue, u1 = double.MinValue, v1 = double.MinValue;
        foreach (var x in new[] { box.Min.X, box.Max.X })
        foreach (var y in new[] { box.Min.Y, box.Max.Y })
        foreach (var z in new[] { box.Min.Z, box.Max.Z })
        {
            var d = new Point3D(x, y, z) - _frame.Origin;
            var (u, v) = (d.Dot(_frame.X), d.Dot(_frame.Y));
            (u0, u1, v0, v1) = (Math.Min(u0, u), Math.Max(u1, u), Math.Min(v0, v), Math.Max(v1, v));
        }
        if (!_revolve) return (u0, v0, u1, v1);
        // Distance from the axis is 1-Lipschitz too.
        var rho = Map(box.Center).U;
        var h = HalfDiagonal(box);
        return (Math.Max(0, rho - h), v0, rho + h, v1);
    }

    (double U, double V, Vector3D Radial) Map(Point3D p)
    {
        var d = p - _frame.Origin;
        if (!_revolve) return (d.Dot(_frame.X), d.Dot(_frame.Y), _frame.X);
        var v = d.Dot(_frame.Y);
        var r = d - _frame.Y * v;
        var u = r.Length;
        return (u, v, u > 0 ? r / u : _frame.X);
    }

    Vector3D Back(double gu, double gv, Vector3D radial) => radial * gu + _frame.Y * gv;

    (int Edge, int Where, double Squared) Nearest(double u, double v)
    {
        var (best, where, squared) = (-1, 0, double.PositiveInfinity);
        for (var i = 0; i < _edges.Length; i++)
        {
            if (_leaves[i] is null) continue;
            var (s, w) = _edges[i].Closest(u, v);
            if (s < squared) (best, where, squared) = (i, w, s);
        }
        return (best, where, squared);
    }

    /// <summary>
    /// The nearest end of edge i and the sign there: a point nearest to a convex corner is outside, to a reflex
    /// corner inside; at a tangent junction the edge's own side decides.
    /// </summary>
    (double U, double V, double Sign, int Corner) Vertex(int i, int where, Point3D p)
    {
        var e = _edges[i];
        var k = where == 1 ? i : (i - 1 + _edges.Length) % _edges.Length;
        var sign = _vertexSign[k] != 0 ? _vertexSign[k] : _leaves[i]!.Value(p) + _offset < 0 ? -1 : 1;
        return where == 1 ? (e.U1, e.V1, sign, k) : (e.U0, e.V0, sign, k);
    }
}
