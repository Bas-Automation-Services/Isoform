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

        Surface Register(Surface template)
        {
            var s = template with { Id = surfaces.Count };
            surfaces.Add(s);
            return s;
        }

        Surface Add(string source, SurfaceKind kind, Point3D origin, Vector3D axis, double radius = 0) =>
            Register(new Surface(0, source, kind, origin, axis, radius));

        void Grow(IEnumerable<Point3D> points)
        {
            foreach (var p in points)
            {
                min = new(Math.Min(min.X, p.X), Math.Min(min.Y, p.Y), Math.Min(min.Z, p.Z));
                max = new(Math.Max(max.X, p.X), Math.Max(max.Y, p.Y), Math.Max(max.Z, p.Z));
            }
        }

        Field Combine(BooleanOp op, Field solid) => op switch
        {
            BooleanOp.New => solid,
            BooleanOp.Join => new MinField([tree!, solid]),
            _ => new MaxField([tree!, new NegateField(solid)]),
        };

        if (DocumentRules.FirstProblem(doc, _tol) is { } invalid)
            return new CompiledPart(null, surfaces, min, max, [invalid]);

        // A solid-making feature placed by a rigid motion. Pattern copies rename their surfaces, join where the
        // original starts the body, and report as the pattern.
        void Place(Feature feature, Frame m, Func<string, string> name, string reportAs)
        {
            Surface Named(Surface s) => Register(s with { Source = name(s.Source) });
            BooleanOp Op(BooleanOp op) => op == BooleanOp.New && tree is not null ? BooleanOp.Join : op;
            switch (feature)
            {
                case Block b:
                    var (block, blockBox) = BlockField(b, m, Named);
                    if (b.Op != BooleanOp.Cut) Grow(blockBox);
                    tree = Combine(Op(b.Op), block);
                    break;

                case Extrude e:
                    var (extruded, extrudedBox) = ExtrudeField(e, m, Named);
                    if (e.Op != BooleanOp.Cut) Grow(extrudedBox);
                    tree = Combine(Op(e.Op), extruded);
                    break;

                case Revolve r:
                    var (revolved, revolvedBox) = RevolveField(r, m, Named);
                    if (r.Op != BooleanOp.Cut) Grow(revolvedBox);
                    tree = Combine(Op(r.Op), revolved);
                    break;

                case Hole h:
                    var wall = Named(new Surface(0, $"{h.Id}/wall", SurfaceKind.Cylinder, m.ToWorld(new Point3D(h.Center[0], h.Center[1], 0)), m.ToWorld(new Vector3D(0, 0, 1)), h.Diameter / 2));
                    diagnostics.AddRange(TangentContacts(reportAs, wall, tree!, surfaces, min, max));
                    tree = new MaxField([tree!, new LeafField(wall, complement: true)]);
                    break;

                case PlaneCut c:
                    var plane = Named(new Surface(0, $"{c.Id}/face", SurfaceKind.Plane, m.ToWorld(P(c.Point)), m.ToWorld(V(c.Normal).Normalized())));
                    tree = new MaxField([tree!, new LeafField(plane)]);
                    break;
            }
        }

        foreach (var feature in doc.Features)
        {
            switch (feature)
            {
                case Pattern p:
                    var copies = p.Motion is MirrorMotion ? 1 : p.Motion switch { LinearMotion l => l.Count, CircularMotion c => c.Count, _ => 1 } - 1;
                    for (var k = 1; k <= copies; k++)
                    {
                        var motion = Motion(p.Motion, k);
                        var copy = k;
                        foreach (var id in p.Features) Place(doc[id], motion, s => $"{p.Id}/{copy}:{s}", p.Id);
                    }
                    break;

                case Shell sh:
                    tree = ShellOf(sh, FieldAlgebra.Normalize(tree!), Register);
                    break;

                case Fillet f:
                    var fillet = Add($"{f.Id}/face", SurfaceKind.Fillet, new Point3D(0, 0, 0), new Vector3D(0, 0, 1), f.Radius);
                    tree = ApplyFillet(f, fillet, FieldAlgebra.Normalize(tree!), surfaces, min, max, diagnostics);
                    break;

                default:
                    Place(feature, Frame.Identity, s => s, feature.Id);
                    break;
            }
        }

        return new CompiledPart(FieldAlgebra.Normalize(tree!), surfaces, min, max, diagnostics);
    }

    /// <summary>
    /// Inner surfaces get real geometry where it exists: an offset plane is a plane, an offset hole wall a larger
    /// cylinder. So coplanar outer faces give one merged inner face, as on the outside.
    /// </summary>
    static ShellField ShellOf(Shell sh, Field tree, Func<Surface, Surface> register)
    {
        var t = sh.Thickness;
        var innerOf = new Dictionary<int, int>();
        foreach (var node in FieldAlgebra.Descendants(tree))
        {
            var (s, complement) = node switch
            {
                LeafField leaf => (leaf.Surface, leaf.Complement),
                RoundedEdgeField r => (r.Fillet, false),
                _ => (null, false),
            };
            if (s is null || innerOf.ContainsKey(s.Id)) continue;
            // The material shrinks by t; a complemented surface has its material outside, so it grows the other way.
            var inner = s.Offset(0, $"{sh.Id}/inner:{s.Source}", complement ? t : -t);
            // A flat inner face looks back at its outer face: it keeps the outer face's sense only if complemented.
            if (s.Kind == SurfaceKind.Plane && !complement) inner = inner with { Axis = -inner.Axis };
            innerOf[s.Id] = register(inner).Id;
        }
        return new ShellField(tree, t, innerOf);
    }

    /// <summary>Copy k of a pattern: the motion applied k times.</summary>
    static Frame Motion(PatternMotion motion, int k)
    {
        switch (motion)
        {
            case LinearMotion l:
                var step = V(l.Direction).Normalized() * (l.Spacing * k);
                return Frame.Identity with { Origin = new Point3D(step.X, step.Y, step.Z) };
            case CircularMotion c:
                var angle = (c.Angle >= 360 ? c.Angle / c.Count : c.Angle / (c.Count - 1)) * k * Math.PI / 180;
                var (o, axis) = (P(c.Origin), V(c.Axis).Normalized());
                Vector3D Turn(Vector3D v) => v * Math.Cos(angle) + axis.Cross(v) * Math.Sin(angle) + axis * (axis.Dot(v) * (1 - Math.Cos(angle)));
                // p ↦ o + R(p − o), written as a frame: origin o − R·o, axes R·e.
                return new Frame(o - Turn(o.AsVector()), Turn(new(1, 0, 0)), Turn(new(0, 1, 0)), Turn(new(0, 0, 1)));
            case MirrorMotion mirror:
                var n = V(mirror.Normal).Normalized();
                var d = n.Dot(P(mirror.Point).AsVector());
                Vector3D Reflect(Vector3D v) => v - n * (2 * n.Dot(v));
                var shift = n * (2 * d);
                return new Frame(new Point3D(shift.X, shift.Y, shift.Z), Reflect(new(1, 0, 0)), Reflect(new(0, 1, 0)), Reflect(new(0, 0, 1)));
            default:
                throw new NotSupportedException(motion.GetType().Name);
        }
    }

    /// <summary>The profile swept between two caps, and the corners of a box round it.</summary>
    static (Field, Point3D[]) ExtrudeField(Extrude e, Frame placement, Func<Surface, Surface> register)
    {
        var frame = Profiles.FrameOf(e.Plane).Then(placement);
        var edges = Profiles.CounterClockwise(e.Profile);
        var leaves = edges.Select(x => ProfileField.EdgeLeaf(x.Edge, frame, revolve: false,
            (k, o, a, r, m, ang) => register(new Surface(0, $"{e.Id}/{x.Id}", k, o, a, r, m, ang)))).ToList();
        var start = register(new Surface(0, $"{e.Id}/start", SurfaceKind.Plane, frame.Origin + frame.Z * e.Start, -frame.Z));
        var end = register(new Surface(0, $"{e.Id}/end", SurfaceKind.Plane, frame.Origin + frame.Z * e.End, frame.Z));
        var field = new MaxField([new ProfileField(edges.Select(x => x.Edge).ToList(), leaves, frame, revolve: false), new LeafField(start), new LeafField(end)]);

        var (u0, v0, u1, v1) = Profiles.Bounds(e.Profile);
        var corners = from u in new[] { u0, u1 } from v in new[] { v0, v1 } from w in new[] { e.Start, e.End }
                      select frame.ToWorld(new Point3D(u, v, w));
        return (field, corners.ToArray());
    }

    /// <summary>The profile revolved round the sketch's y axis, cut to the swept angle by two half-planes below 360°.</summary>
    static (Field, Point3D[]) RevolveField(Revolve r, Frame placement, Func<Surface, Surface> register)
    {
        var frame = Profiles.FrameOf(r.Plane).Then(placement);
        var edges = Profiles.CounterClockwise(r.Profile);
        var leaves = edges.Select(x => ProfileField.EdgeLeaf(x.Edge, frame, revolve: true,
            (k, o, a, rad, m, ang) => register(new Surface(0, $"{r.Id}/{x.Id}", k, o, a, rad, m, ang)))).ToList();
        Field field = new ProfileField(edges.Select(x => x.Edge).ToList(), leaves, frame, revolve: true);
        if (r.Angle < 360)
        {
            // θ runs from the sketch's x (U) towards W = axis × U, which is −Z for a sketch frame. Taken from the frame
            // itself, it stays right under a mirror, where a cross product would flip.
            var (u, w) = (frame.X, -frame.Z);
            var phi = r.Angle * Math.PI / 180;
            var start = new LeafField(register(new Surface(0, $"{r.Id}/start", SurfaceKind.Plane, frame.Origin, -w)));
            var end = new LeafField(register(new Surface(0, $"{r.Id}/end", SurfaceKind.Plane, frame.Origin, u * -Math.Sin(phi) + w * Math.Cos(phi))));
            Field wedge = phi <= Math.PI ? new MaxField([start, end]) : new MinField([start, end]);
            field = new MaxField([field, wedge]);
        }

        // A box round the full revolution: discs of the largest radius at both ends of the height range.
        var (_, v0, u1, v1) = Profiles.Bounds(r.Profile);
        var a = frame.Y;
        var extent = new Vector3D(u1 * Math.Sqrt(Math.Max(0, 1 - a.X * a.X)), u1 * Math.Sqrt(Math.Max(0, 1 - a.Y * a.Y)), u1 * Math.Sqrt(Math.Max(0, 1 - a.Z * a.Z)));
        var ends = new[] { frame.Origin + a * v0, frame.Origin + a * v1 };
        return (field, ends.SelectMany(c => new[] { c - extent, c + extent }).ToArray());
    }

    static (Field, Point3D[]) BlockField(Block b, Frame m, Func<Surface, Surface> register)
    {
        var lo = P(b.Origin);
        var hi = new Point3D(lo.X + b.Size[0], lo.Y + b.Size[1], lo.Z + b.Size[2]);
        LeafField Face(string name, Point3D at, Vector3D normal) =>
            new(register(new Surface(0, $"{b.Id}/{name}", SurfaceKind.Plane, m.ToWorld(at), m.ToWorld(normal))));
        var field = new MaxField(
        [
            Face("x-", lo, new Vector3D(-1, 0, 0)),
            Face("x+", hi, new Vector3D(1, 0, 0)),
            Face("y-", lo, new Vector3D(0, -1, 0)),
            Face("y+", hi, new Vector3D(0, 1, 0)),
            Face("z-", lo, new Vector3D(0, 0, -1)),
            Face("z+", hi, new Vector3D(0, 0, 1)),
        ]);
        var corners = from x in new[] { lo.X, hi.X } from y in new[] { lo.Y, hi.Y } from z in new[] { lo.Z, hi.Z }
                      select m.ToWorld(new Point3D(x, y, z));
        return (field, corners.ToArray());
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
