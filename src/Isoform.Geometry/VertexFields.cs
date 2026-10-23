using BasAutomation.Geometry;

namespace Isoform.Geometry;

/// <summary>
/// The rolling ball at a convex corner where three plane–plane fillets of one radius r meet. Its centre C lies r inside
/// all three planes. Writing p − C = α·gA + β·gB + γ·gC, p lies in the corner sector when α, β, γ ≥ 0, and the field
/// there is |p − C| − r, a sphere; elsewhere it is the three fillets' max (their mitre).
/// </summary>
/// <remarks>
/// That is d(p, K) − r for K the corner moved inward by r (the planes' max inside it): the exact rounding of the
/// corner. In the sector p's nearest point of K is its vertex C; anywhere else it lies on a face or edge of K, where K
/// is the wedge of one fillet, whose field is that wedge's distance less r. So the field is ≥ every fillet, equal to
/// them on the sector's faces, and 1-Lipschitz. It goes in the fillets' intersection after them: outside the sector it
/// adds nothing there, and simplifies to their max, which the intersection drops.
/// </remarks>
public sealed class VertexSphereField : Field
{
    readonly LeafField _ball;
    readonly MaxField _mitre;
    readonly Vector3D _ua, _ub, _uc;

    /// <param name="sphere">The corner face: a sphere round C of the fillets' radius.</param>
    public VertexSphereField(IReadOnlyList<RoundedEdgeField> edges, Surface sphere)
    {
        (Edges, Sphere) = (edges, sphere);
        (_ball, _mitre) = (new LeafField(sphere), new MaxField(edges));
        var leaves = Leaves(edges)!;
        var origin = new Point3D(0, 0, 0);
        var (ga, gb, gc) = (leaves[0].Sample(origin).Gradient, leaves[1].Sample(origin).Gradient, leaves[2].Sample(origin).Gradient);
        // The dual basis: u_i · g_j = δ_ij, so α = u_a · (p − C).
        var det = ga.Dot(gb.Cross(gc));
        (_ua, _ub, _uc) = (gb.Cross(gc) / det, gc.Cross(ga) / det, ga.Cross(gb) / det);
    }

    /// <summary>The three fillets, in the order the intersection holds them.</summary>
    public IReadOnlyList<RoundedEdgeField> Edges { get; }

    public Surface Sphere { get; }

    /// <summary>
    /// The corner of three plane–plane fillets of radius r that share their faces pairwise: the sphere round the point r
    /// inside all three planes. Null when they are not three such fillets of one radius.
    /// </summary>
    public static Surface? Ball(IReadOnlyList<RoundedEdgeField> edges, int id, string source) =>
        edges.All(e => Math.Abs(e.Radius - edges[0].Radius) <= 1e-12) && Meet(edges, edges[0].Radius) is { } c
            ? new Surface(id, source, SurfaceKind.Sphere, c, new Vector3D(0, 0, 1), edges[0].Radius)
            : null;

    /// <summary>
    /// Where the three faces of three plane–plane fillets that share them pairwise meet, each moved inward by
    /// <paramref name="inset"/>; null when the fillets are not such, or the planes have no single point in common.
    /// </summary>
    public static Point3D? Meet(IReadOnlyList<RoundedEdgeField> edges, double inset)
    {
        if (edges.Count != 3 || edges.Any(e => e is MeridianEdgeField || e.A.Surface.Kind != SurfaceKind.Plane || e.B.Surface.Kind != SurfaceKind.Plane)
            || Leaves(edges) is not { } leaves)
            return null;
        var origin = new Point3D(0, 0, 0);
        var g = leaves.Select(l => l.Sample(origin).Gradient).ToArray();
        var det = g[0].Dot(g[1].Cross(g[2]));
        if (Math.Abs(det) < 1e-9) return null;
        // g_i · p + value_i(0) = −inset for each plane.
        var h = leaves.Select(l => -inset - l.Value(origin)).ToArray();
        var p = (g[1].Cross(g[2]) * h[0] + g[2].Cross(g[0]) * h[1] + g[0].Cross(g[1]) * h[2]) / det;
        return new Point3D(p.X, p.Y, p.Z);
    }

    /// <summary>The three distinct faces of three fillets that share them pairwise, or null.</summary>
    static List<LeafField>? Leaves(IReadOnlyList<RoundedEdgeField> edges)
    {
        var leaves = edges.SelectMany(e => new[] { e.A, e.B }).DistinctBy(l => l.Surface.Id).ToList();
        return leaves.Count == 3 && edges.All(e => e.A.Surface.Id != e.B.Surface.Id) ? leaves : null;
    }

    /// <summary>The same corner on other fillets, as an offset of the part has it.</summary>
    public VertexSphereField With(IReadOnlyList<RoundedEdgeField> edges, Func<Surface, Surface> register) =>
        new(edges, register(Ball(edges, 0, Sphere.Source)!));

    bool InSector(Point3D p)
    {
        var w = p - Sphere.Origin;
        return _ua.Dot(w) >= 0 && _ub.Dot(w) >= 0 && _uc.Dot(w) >= 0;
    }

    public override FieldSample Sample(Point3D p) => InSector(p) ? _ball.Sample(p) : _mitre.Sample(p);

    public override double Value(Point3D p) => InSector(p) ? _ball.Value(p) : _mitre.Value(p);

    public override FieldSample? SampleSurface(int owner, Point3D p)
    {
        if (owner == Sphere.Id) return _ball.Sample(p);
        foreach (var e in Edges)
            if (e.SampleSurface(owner, p) is { } s) return s;
        return null;
    }

    // Lowest and highest of u · (p − C) over the box: linear, so exact.
    (double Lo, double Hi) Reach(Vector3D u, Box3D box)
    {
        var c = u.Dot(box.Center - Sphere.Origin);
        var h = (Math.Abs(u.X) * (box.Max.X - box.Min.X) + Math.Abs(u.Y) * (box.Max.Y - box.Min.Y) + Math.Abs(u.Z) * (box.Max.Z - box.Min.Z)) / 2;
        return (c - h, c + h);
    }

    bool Outside(Box3D box) => Reach(_ua, box).Hi < -1e-9 || Reach(_ub, box).Hi < -1e-9 || Reach(_uc, box).Hi < -1e-9;

    bool Inside(Box3D box) => Reach(_ua, box).Lo > 1e-9 && Reach(_ub, box).Lo > 1e-9 && Reach(_uc, box).Lo > 1e-9;

    /// <summary>At least the fillets' max; in the sector at most the farthest corner of the box from C, less r.</summary>
    public override Interval Bounds(Box3D box)
    {
        var edges = _mitre.Bounds(box);
        if (Outside(box)) return edges;
        var c = Sphere.Origin;
        static double Far(double lo, double hi, double at) => Math.Max(Math.Abs(lo - at), Math.Abs(hi - at));
        var (fx, fy, fz) = (Far(box.Min.X, box.Max.X, c.X), Far(box.Min.Y, box.Max.Y, c.Y), Far(box.Min.Z, box.Max.Z, c.Z));
        var far = Math.Sqrt(fx * fx + fy * fy + fz * fz) - Sphere.Radius;
        return new Interval(edges.Lo, Math.Max(edges.Hi, far)).Intersect(LipschitzBounds(box));
    }

    /// <summary>Outside the sector the fillets themselves; wholly inside it the sphere.</summary>
    public override Field Simplify(Box3D box) => Outside(box) ? _mitre : Inside(box) ? _ball : this;

    public override double? SinglePatch(Box3D box) => Inside(box) ? Sphere.Radius : null;
}
