using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Geometry;

namespace Isoform.Modeling;

/// <summary>A document compiled to one implicit solid, plus the source surface behind every owner id.</summary>
public sealed class CompiledPart(Field? field, IReadOnlyList<Surface> surfaces, Point3D min, Point3D max, IReadOnlyList<Diagnostic> diagnostics)
{
    /// <summary>Null when a diagnostic made the document unbuildable.</summary>
    public Field? Field { get; } = field;
    public IReadOnlyList<Surface> Surfaces { get; } = surfaces;
    public Point3D Min { get; } = min;
    public Point3D Max { get; } = max;
    public IReadOnlyList<Diagnostic> Diagnostics { get; } = diagnostics;
}

/// <summary>Feature graph → CSG field. Every surface is named after its feature, e.g. <c>plate/z+</c>.</summary>
public sealed class Compiler(ModelingSettings settings)
{
    readonly double _tol = settings.Tolerance;

    public CompiledPart Compile(PartDocument doc)
    {
        var surfaces = new List<Surface>();
        var diagnostics = new List<Diagnostic>();
        Field? tree = null;
        var min = new Point3D(double.MaxValue, double.MaxValue, double.MaxValue);
        var max = new Point3D(double.MinValue, double.MinValue, double.MinValue);

        Surface Add(string source, SurfaceKind kind, Point3D origin, Vector3D axis, double radius = 0)
        {
            var s = new Surface(surfaces.Count, source, kind, origin, axis, radius);
            surfaces.Add(s);
            return s;
        }

        foreach (var feature in doc.Features)
        {
            if (Validate(feature, tree) is { } invalid)
            {
                diagnostics.Add(invalid);
                return new CompiledPart(null, surfaces, min, max, diagnostics);
            }

            switch (feature)
            {
                case Block b:
                    var block = BlockField(b, Add);
                    if (b.Op != BooleanOp.Cut)
                    {
                        min = new(Math.Min(min.X, b.Origin[0]), Math.Min(min.Y, b.Origin[1]), Math.Min(min.Z, b.Origin[2]));
                        max = new(Math.Max(max.X, b.Origin[0] + b.Size[0]), Math.Max(max.Y, b.Origin[1] + b.Size[1]), Math.Max(max.Z, b.Origin[2] + b.Size[2]));
                    }
                    tree = b.Op switch
                    {
                        BooleanOp.New => block,
                        BooleanOp.Join => new MinField([tree!, block]),
                        _ => new MaxField([tree!, new NegateField(block)]),
                    };
                    break;

                case Hole h:
                    var wall = Add($"{h.Id}/wall", SurfaceKind.Cylinder, new Point3D(h.Center[0], h.Center[1], 0), new Vector3D(0, 0, 1), h.Diameter / 2);
                    diagnostics.AddRange(TangentContacts(h.Id, wall, tree!, surfaces, min, max));
                    tree = new MaxField([tree!, new LeafField(wall, complement: true)]);
                    break;

                case PlaneCut c:
                    var plane = Add($"{c.Id}/face", SurfaceKind.Plane, P(c.Point), V(c.Normal).Normalized());
                    tree = new MaxField([tree!, new LeafField(plane)]);
                    break;

                case Fillet f:
                    var fillet = Add($"{f.Id}/face", SurfaceKind.Fillet, new Point3D(0, 0, 0), new Vector3D(0, 0, 1), f.Radius);
                    tree = ApplyFillet(f, fillet, FieldAlgebra.Normalize(tree!), surfaces, min, max, diagnostics);
                    break;
            }
        }

        return new CompiledPart(FieldAlgebra.Normalize(tree!), surfaces, min, max, diagnostics);
    }

    Diagnostic? Validate(Feature feature, Field? tree)
    {
        string? problem = feature switch
        {
            Block { Op: BooleanOp.New } when tree is not null => "only the first feature may start a new body",
            Block { Op: not BooleanOp.New } when tree is null => "the first feature must be a new block",
            Block b when b.Size.Any(s => s <= _tol) => $"block size must be positive, got [{string.Join(", ", b.Size)}]",
            Hole h when h.Diameter <= _tol => $"hole diameter must be positive, got {h.Diameter}",
            PlaneCut c when V(c.Normal).Length <= _tol => "plane-cut normal must not be zero",
            Fillet f when f.Radius <= _tol => $"fillet radius must be positive, got {f.Radius}",
            Fillet f when f.Edge.Length != 2 => "a fillet edge names exactly two source faces",
            not Block when tree is null => "the first feature must be a new block",
            _ => null,
        };
        return problem is null ? null : new Diagnostic(Diagnostic.InvalidParameter, feature.Id, problem);
    }

    static Field BlockField(Block b, Func<string, SurfaceKind, Point3D, Vector3D, double, Surface> add)
    {
        var lo = P(b.Origin);
        var hi = new Point3D(lo.X + b.Size[0], lo.Y + b.Size[1], lo.Z + b.Size[2]);
        return new MaxField(
        [
            new LeafField(add($"{b.Id}/x-", SurfaceKind.Plane, lo, new Vector3D(-1, 0, 0), 0)),
            new LeafField(add($"{b.Id}/x+", SurfaceKind.Plane, hi, new Vector3D(1, 0, 0), 0)),
            new LeafField(add($"{b.Id}/y-", SurfaceKind.Plane, lo, new Vector3D(0, -1, 0), 0)),
            new LeafField(add($"{b.Id}/y+", SurfaceKind.Plane, hi, new Vector3D(0, 1, 0), 0)),
            new LeafField(add($"{b.Id}/z-", SurfaceKind.Plane, lo, new Vector3D(0, 0, -1), 0)),
            new LeafField(add($"{b.Id}/z+", SurfaceKind.Plane, hi, new Vector3D(0, 0, 1), 0)),
        ]);
    }

    /// <summary>A hole wall tangent to an existing planar face along a line the part actually touches.</summary>
    IEnumerable<Diagnostic> TangentContacts(string featureId, Surface wall, Field before, List<Surface> surfaces, Point3D min, Point3D max)
    {
        foreach (var plane in surfaces.Where(s => s.Kind == SurfaceKind.Plane))
        {
            if (Math.Abs(plane.Axis.Dot(wall.Axis)) > _tol) continue;
            var offset = plane.Distance(wall.Origin);
            if (Math.Abs(Math.Abs(offset) - wall.Radius) > _tol) continue;
            var line = wall.Origin - plane.Axis * offset;
            if (SampleLine(line, wall.Axis, min, max).Any(q => Math.Abs(before.Value(q)) <= 10 * _tol))
                yield return new Diagnostic(Diagnostic.TangentContact, featureId,
                    $"{wall.Source} touches {plane.Source} tangentially: zero-thickness contact");
        }
    }

    Field ApplyFillet(Fillet f, Surface fillet, Field tree, List<Surface> surfaces, Point3D min, Point3D max, List<Diagnostic> diagnostics)
    {
        void Fail(string code, string message) => diagnostics.Add(new Diagnostic(code, f.Id, message));

        var host = FieldAlgebra.Descendants(tree).OfType<MaxField>().FirstOrDefault(m =>
            f.Edge.All(src => m.Children.OfType<LeafField>().Any(l => l.Surface.Source == src)));
        if (host is null)
        {
            Fail(Diagnostic.UnsupportedFillet,
                $"edge [{string.Join(", ", f.Edge)}] is not a convex edge between two faces of one intersection");
            return tree;
        }

        var a = host.Children.OfType<LeafField>().First(l => l.Surface.Source == f.Edge[0]);
        var b = host.Children.OfType<LeafField>().First(l => l.Surface.Source == f.Edge[1]);
        if (EdgeSamples(a.Surface, b.Surface, min, max) is not { } samples)
        {
            Fail(Diagnostic.UnsupportedFillet, $"no exact rolling-ball fillet for {a.Surface.Kind}–{b.Surface.Kind} in this orientation");
            return tree;
        }

        var rounded = new RoundedEdgeField(a, b, f.Radius, fillet);
        var onEdge = samples.Where(q => Math.Abs(tree.Value(q)) <= 10 * _tol).ToList();
        if (onEdge.Count == 0)
        {
            Fail(Diagnostic.FilletInvalid, $"edge [{string.Join(", ", f.Edge)}] does not exist on the part");
            return tree;
        }
        // On a face's own plane the solid field is ≥ 0, and 0 only on the face itself: a contact point with
        // a positive value means the ball does not fit on that face.
        if (onEdge.Select(rounded.Contacts).Any(c => tree.Value(c.OnA) > 10 * _tol || tree.Value(c.OnB) > 10 * _tol))
        {
            Fail(Diagnostic.FilletInvalid, $"radius {f.Radius} does not fit between the faces next to the edge");
            return tree;
        }

        return Replace(tree, host, new MaxField(host.Children.Where(c => c != a && c != b).Append(rounded).ToList()));
    }

    /// <summary>Points on the sharp edge between two leaf surfaces, or null when the pair is not supported exactly.</summary>
    static List<Point3D>? EdgeSamples(Surface a, Surface b, Point3D min, Point3D max)
    {
        if (a.Kind == SurfaceKind.Plane && b.Kind == SurfaceKind.Plane)
        {
            var c = a.Axis.Dot(b.Axis);
            if (1 - c * c < 1e-12) return null;
            var u = a.Axis.Cross(b.Axis).Normalized();
            var ha = a.Axis.Dot(a.Origin.AsVector());
            var hb = b.Axis.Dot(b.Origin.AsVector());
            var x0 = (a.Axis * (ha - hb * c) + b.Axis * (hb - ha * c)) / (1 - c * c);
            return SampleLine(new Point3D(x0.X, x0.Y, x0.Z), u, min, max).ToList();
        }
        var (plane, cyl) = a.Kind == SurfaceKind.Plane ? (a, b) : (b, a);
        if (plane.Kind != SurfaceKind.Plane || cyl.Kind != SurfaceKind.Cylinder) return null;
        if (plane.Axis.Cross(cyl.Axis).Length > 1e-9) return null;

        var centre = cyl.Origin - cyl.Axis * (plane.Distance(cyl.Origin) / plane.Axis.Dot(cyl.Axis));
        var e1 = (Math.Abs(cyl.Axis.X) < 0.9 ? new Vector3D(1, 0, 0) : new Vector3D(0, 1, 0)).Cross(cyl.Axis).Normalized();
        var e2 = cyl.Axis.Cross(e1);
        const int n = 720;
        return Enumerable.Range(0, n)
            .Select(i => 2 * Math.PI * i / n)
            .Select(t => centre + (e1 * Math.Cos(t) + e2 * Math.Sin(t)) * cyl.Radius)
            .ToList();
    }

    static IEnumerable<Point3D> SampleLine(Point3D p0, Vector3D u, Point3D min, Point3D max)
    {
        var ts = Corners(min, max).Select(c => (c - p0).Dot(u)).ToList();
        var (t0, t1) = (ts.Min(), ts.Max());
        const int n = 800;
        for (var i = 0; i <= n; i++) yield return p0 + u * (t0 + (t1 - t0) * i / n);
    }

    static IEnumerable<Point3D> Corners(Point3D lo, Point3D hi)
    {
        foreach (var x in new[] { lo.X, hi.X })
        foreach (var y in new[] { lo.Y, hi.Y })
        foreach (var z in new[] { lo.Z, hi.Z })
            yield return new Point3D(x, y, z);
    }

    static Field Replace(Field field, Field target, Field replacement) => field == target ? replacement : field switch
    {
        MaxField m => new MaxField(m.Children.Select(c => Replace(c, target, replacement)).ToList()),
        MinField m => new MinField(m.Children.Select(c => Replace(c, target, replacement)).ToList()),
        _ => field,
    };

    static Point3D P(double[] v) => new(v[0], v[1], v[2]);
    static Vector3D V(double[] v) => new(v[0], v[1], v[2]);
}
