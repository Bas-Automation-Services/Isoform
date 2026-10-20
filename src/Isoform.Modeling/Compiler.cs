using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Geometry;

namespace Isoform.Modeling;

/// <summary>One solid body of a part: its field and a box round it.</summary>
public sealed record CompiledBody(string Name, Field Field, Point3D Min, Point3D Max);

/// <summary>A document compiled to implicit solids, one per body, plus the source surface behind every owner id.</summary>
public sealed class CompiledPart(Field? field, IReadOnlyList<Surface> surfaces, Point3D min, Point3D max, IReadOnlyList<Diagnostic> diagnostics,
    IReadOnlyList<CompiledBody>? bodies = null)
{
    /// <summary>The whole part: the one body, or the union of all bodies. Null when a diagnostic made the document unbuildable.</summary>
    public Field? Field { get; } = field;

    /// <summary>Every body, in the order the document started them. Bodies may overlap: each is a solid of its own.</summary>
    public IReadOnlyList<CompiledBody> Bodies { get; } = bodies ?? [];
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

        // Bodies: the current one lives in tree, min and max; the others wait here.
        var bodies = new Dictionary<string, (Field? Tree, Point3D Min, Point3D Max)>();
        var order = new List<string>();
        var bodyOfSource = new Dictionary<string, string>();
        var bodyOfFeature = new Dictionary<string, string>();
        string? current = null;

        void Switch(string name)
        {
            if (current == name) return;
            if (current is not null) bodies[current] = (tree, min, max);
            (tree, min, max) = bodies[name];
            current = name;
        }

        void Start(string name)
        {
            if (current is not null) bodies[current] = (tree, min, max);
            (tree, min, max) = (null, new Point3D(double.MaxValue, double.MaxValue, double.MaxValue), new Point3D(double.MinValue, double.MinValue, double.MinValue));
            bodies[name] = (tree, min, max);
            order.Add(name);
            current = name;
        }

        Surface Register(Surface template)
        {
            var s = template with { Id = surfaces.Count };
            surfaces.Add(s);
            bodyOfSource[s.Source] = current!;
            return s;
        }

        void Grow(IEnumerable<Point3D> points)
        {
            foreach (var p in points)
            {
                min = new(Math.Min(min.X, p.X), Math.Min(min.Y, p.Y), Math.Min(min.Z, p.Z));
                max = new(Math.Max(max.X, p.X), Math.Max(max.Y, p.Y), Math.Max(max.Z, p.Z));
            }
        }

        // A blend above 0 joins or cuts smoothly through a new face named after the feature.
        Field Combine(BooleanOp op, Field solid, double blend = 0, Func<Surface, Surface>? named = null, string id = "")
        {
            if (op == BooleanOp.New) return solid;
            var tool = op == BooleanOp.Join ? solid : new NegateField(solid);
            if (blend <= 0) return op == BooleanOp.Join ? new MinField([tree!, tool]) : new MaxField([tree!, tool]);
            var face = named!(new Surface(0, $"{id}/blend", SurfaceKind.Blend, new Point3D(0, 0, 0), new Vector3D(0, 0, 1), blend));
            return new BlendField(FieldAlgebra.Normalize(tree!), FieldAlgebra.Normalize(tool), blend, face, union: op == BooleanOp.Join);
        }

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
                    tree = Combine(Op(b.Op), block, b.Blend, Named, b.Id);
                    break;

                case Extrude e:
                    var (extruded, extrudedBox) = ExtrudeField(e, m, Named);
                    if (e.Op != BooleanOp.Cut) Grow(extrudedBox);
                    tree = Combine(Op(e.Op), extruded, e.Blend, Named, e.Id);
                    break;

                case Revolve r:
                    var (revolved, revolvedBox) = RevolveField(r, m, Named);
                    if (r.Op != BooleanOp.Cut) Grow(revolvedBox);
                    tree = Combine(Op(r.Op), revolved, r.Blend, Named, r.Id);
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
            // Pick the body: a new one, the one the feature names, or for an edge the body that has its faces.
            if (DocumentRules.StartsBody(feature)) Start(feature.Body ?? feature.Id);
            else if (feature.Body is { } named) Switch(named);
            else if (EdgeFace(feature) is { } face && bodyOfSource.TryGetValue(face, out var holder)) Switch(holder);
            bodyOfFeature[feature.Id] = current!;
            // Edge features and patterns work where their faces or copies are, then hand the current body back.
            var resume = current;

            switch (feature)
            {
                case Pattern p:
                    // Each copy goes into the body of the feature it copies.
                    var copies = p.Motion is MirrorMotion ? 1 : p.Motion switch { LinearMotion l => l.Count, CircularMotion c => c.Count, _ => 1 } - 1;
                    for (var k = 1; k <= copies; k++)
                    {
                        var motion = Motion(p.Motion, k);
                        var copy = k;
                        foreach (var id in p.Features)
                        {
                            Switch(bodyOfFeature[id]);
                            Place(doc[id], motion, s => $"{p.Id}/{copy}:{s}", p.Id);
                        }
                    }
                    break;

                case CombineBodies c:
                    if (c.Body is { } target) Switch(target);
                    var tool = bodies[c.Tool];
                    var (into, toolTree) = (current!, FieldAlgebra.Normalize(tool.Tree!));
                    tree = c.Op == BooleanOp.Join ? new MinField([tree!, toolTree]) : new MaxField([tree!, new NegateField(toolTree)]);
                    if (c.Op == BooleanOp.Join) Grow([tool.Min, tool.Max]);
                    if (!c.Keep)
                    {
                        bodies.Remove(c.Tool);
                        order.Remove(c.Tool);
                        foreach (var source in bodyOfSource.Where(kv => kv.Value == c.Tool).Select(kv => kv.Key).ToList()) bodyOfSource[source] = into;
                        foreach (var id in bodyOfFeature.Where(kv => kv.Value == c.Tool).Select(kv => kv.Key).ToList()) bodyOfFeature[id] = into;
                    }
                    break;

                case Shell sh:
                    var solid = FieldAlgebra.Normalize(tree!);
                    // The cavity of a second shell would need inner faces of inner faces; shell once.
                    if (FieldAlgebra.Descendants(solid).Any(n => n is ShellField))
                        diagnostics.Add(new Diagnostic(Diagnostic.InvalidParameter, sh.Id, "this body is already shelled; a body can be shelled once"));
                    else
                        tree = ShellOf(sh, solid, Register);
                    break;

                case Offset o:
                    if (OffsetOf(FieldAlgebra.Normalize(tree!), o.Distance, Register, new Dictionary<(LeafField, double), LeafField>(), o.Id) is { } grown)
                    {
                        tree = grown;
                        // Growing moves every face out by at most the distance, and the box with them.
                        if (o.Distance > 0) Grow([min - new Vector3D(1, 1, 1) * o.Distance, max + new Vector3D(1, 1, 1) * o.Distance]);
                    }
                    else
                        diagnostics.Add(new Diagnostic(Diagnostic.InvalidParameter, o.Id, "a shelled part cannot be offset; offset before the shell"));
                    break;

                case Fillet { Loop: { } loop } f:
                    tree = ApplyLoop(f, loop, f.Radius, round: true, FieldAlgebra.Normalize(tree!), Register, min, max, diagnostics);
                    break;

                case Chamfer { Loop: { } loop } c:
                    tree = ApplyLoop(c, loop, c.Distance, round: false, FieldAlgebra.Normalize(tree!), Register, min, max, diagnostics);
                    break;

                case Fillet or Chamfer:
                    tree = ApplyEdge(feature, FieldAlgebra.Normalize(tree!), Register, min, max, diagnostics);
                    break;

                default:
                    Place(feature, Frame.Identity, s => s, feature.Id);
                    break;
            }
            if (feature is Fillet or Chamfer or Pattern && resume is not null && bodies.ContainsKey(resume)) Switch(resume);
        }

        bodies[current!] = (tree, min, max);
        var compiled = order.Select(n => new CompiledBody(n, FieldAlgebra.Normalize(bodies[n].Tree!), bodies[n].Min, bodies[n].Max)).ToList();
        var whole = compiled.Count == 1 ? compiled[0].Field : new MinField(compiled.Select(b => b.Field).ToList());
        var (lo, hi) = (compiled[0].Min, compiled[0].Max);
        foreach (var b in compiled)
            (lo, hi) = (new Point3D(Math.Min(lo.X, b.Min.X), Math.Min(lo.Y, b.Min.Y), Math.Min(lo.Z, b.Min.Z)), new Point3D(Math.Max(hi.X, b.Max.X), Math.Max(hi.Y, b.Max.Y), Math.Max(hi.Z, b.Max.Z)));
        return new CompiledPart(whole, surfaces, lo, hi, diagnostics, compiled);
    }

    /// <summary>
    /// Inner surfaces get real geometry where it exists: an offset plane is a plane, an offset hole wall a larger
    /// cylinder. So coplanar outer faces give one merged inner face, as on the outside.
    /// </summary>
    static ShellField ShellOf(Shell sh, Field tree, Func<Surface, Surface> register)
    {
        var t = sh.Thickness;
        var innerOf = new Dictionary<int, int>();
        void Add(Surface s, bool complement)
        {
            if (innerOf.ContainsKey(s.Id)) return;
            // The material shrinks by t; a complemented surface has its material outside, so it grows the other way.
            var inner = s.Offset(0, $"{sh.Id}/inner:{s.Source}", complement ? t : -t);
            // A flat inner face looks back at its outer face: it keeps the outer face's sense only if complemented.
            if (s.Kind == SurfaceKind.Plane && !complement) inner = inner with { Axis = -inner.Axis };
            innerOf[s.Id] = register(inner).Id;
        }
        // Walk with the number of complements above each face, so a face of a cut, or a fillet that adds material,
        // has its material on the right side.
        void Walk(Field f, bool negated)
        {
            switch (f)
            {
                case LeafField l:
                    Add(l.Surface, l.Complement ^ negated);
                    break;
                case NegateField n:
                    Walk(n.Child, !negated);
                    break;
                case MaxField m:
                    foreach (var c in m.Children) Walk(c, negated);
                    break;
                case MinField m:
                    foreach (var c in m.Children) Walk(c, negated);
                    break;
                case RoundedEdgeField r:
                    Add(r.Fillet, negated);
                    Walk(r.A, negated);
                    Walk(r.B, negated);
                    break;
                case LoopEdgeField l:
                    foreach (var face in l.FaceOf.Values) Add(face, negated);
                    Walk(l.Cap, negated);
                    Walk(l.Side, negated);
                    break;
                case BlendField b:
                    Add(b.Blend, negated);
                    Walk(b.A, negated);
                    Walk(b.B, negated);
                    break;
                case ProfileField p:
                    foreach (var l in p.Leaves) Walk(l, negated);
                    break;
            }
        }
        Walk(tree, false);
        return new ShellField(tree, t, innerOf);
    }

    /// <summary>
    /// The tree with every face moved by d (material grows for d > 0). Faces get real offset surfaces under their
    /// own names, fillet radii grow with them (a fillet shrunk to nothing leaves a sharp edge), and later features
    /// see true geometry. Each leaf is offset once, so faces shared by fillets stay shared. Null for a shell.
    /// </summary>
    static Field? OffsetOf(Field f, double d, Func<Surface, Surface> register, Dictionary<(LeafField, double), LeafField> done, string id)
    {
        LeafField Leaf(LeafField l, double by)
        {
            if (done.TryGetValue((l, by), out var known)) return known;
            return done[(l, by)] = new LeafField(register(l.Surface.Offset(0, l.Surface.Source, l.Complement ? -by : by)), l.Complement);
        }
        Field? Each(IReadOnlyList<Field> children, Func<List<Field>, Field> make)
        {
            var moved = children.Select(c => OffsetOf(c, d, register, done, id)).ToList();
            return moved.Any(c => c is null) ? null : make(moved!);
        }
        switch (f)
        {
            case LeafField l:
                return Leaf(l, d);
            case MaxField m:
                return Each(m.Children, c => new MaxField(c));
            case MinField m when m.Children.OfType<LoopEdgeField>().FirstOrDefault(l => l.Side is NegateField) is { Side: NegateField { Child: var tool } } rim
                                 && m.Children.Any(c => c is LeafField l && l.Surface.Id == rim.Cap.Surface.Id && l.Complement != rim.Cap.Complement):
                // What a cut-out's rim round takes away, normalized to min(−cap, tool, rim): the cap and the outside of
                // the cut are faces of the part, so as the part grows the cap rises and the tool shrinks, while the
                // round itself grows with the part.
                var parts = m.Children.Select(c => c switch
                {
                    LeafField l when l.Surface.Id == rim.Cap.Surface.Id => Leaf(l, -d),
                    LoopEdgeField l when l == rim => OffsetOf(rim, d, register, done, id),
                    _ when c == tool => OffsetOf(tool, -d, register, done, id),
                    _ => OffsetOf(c, d, register, done, id),
                }).ToList();
                return parts.Any(c => c is null) ? null : new MinField(parts!);
            case MinField m:
                return Each(m.Children, c => new MinField(c));
            case NegateField n:
                // −g − d = −(g + d): the complement's own faces move the other way.
                return OffsetOf(n.Child, -d, register, done, id) is { } child ? new NegateField(child) : null;
            case RoundedEdgeField r:
                var (a, b) = (Leaf(r.A, d), Leaf(r.B, d));
                return r.Radius + d > 1e-9
                    ? new RoundedEdgeField(a, b, r.Radius + d, register(r.Fillet with { Radius = r.Radius + d }))
                    : new MaxField([a, b]);
            case BlendField bl:
                // The smooth min shifts with its arguments, so the blend face keeps its width.
                return Each([bl.A, bl.B], c => new BlendField(c[0], c[1], bl.K, bl.Blend, bl.Union));
            case ProfileField p:
                return p.Offset(d, l => Leaf(l, d), register, (a, b) => $"{id}/corner:{a}+{b}");
            case LoopEdgeField lp:
                // A rounded loop grows with the faces; a chamfer plane (a + b + s)/√2 moves by d when s grows by (2 − √2)d.
                var size = lp.Round ? lp.Size + d : lp.Size + (2 - Math.Sqrt(2)) * d;
                if (OffsetOf(lp.Side, d, register, done, id) is not { } side) return null;
                if (size <= 1e-9) return new MaxField([Leaf(lp.Cap, d), side]);
                var faces = lp.FaceOf.ToDictionary(kv => kv.Key, kv => register(kv.Value with { Radius = size }));
                // The side's owners are offset leaves now (moved by ±d: a cut's side is a complement): map the new ids
                // to the new faces, and give any new side face (a round corner of a grown profile) a face of its own.
                var remapped = new Dictionary<int, Surface>();
                var present = FieldAlgebra.Descendants(side).OfType<LeafField>().Select(l => l.Surface.Id).ToHashSet();
                foreach (var (oldOwner, face) in faces)
                    foreach (var ((leaf, by), moved) in done)
                        if (leaf.Surface.Id == oldOwner && Math.Abs(by) == Math.Abs(d) && present.Contains(moved.Surface.Id)) remapped[moved.Surface.Id] = face;
                var prefix = lp.FaceOf.Values.First().Source.Split('/')[0];
                foreach (var l in FieldAlgebra.Descendants(side).OfType<ProfileField>().SelectMany(pf => pf.Leaves).Where(l => !remapped.ContainsKey(l.Surface.Id)))
                    remapped[l.Surface.Id] = register(new Surface(0, $"{prefix}/{l.Surface.Source}", SurfaceKind.Fillet, new Point3D(0, 0, 0), new Vector3D(0, 0, 1), size));
                return new LoopEdgeField(Leaf(lp.Cap, d), side, size, lp.Round, remapped);
            default:
                return null;
        }
    }

    static string? EdgeFace(Feature f) => f switch
    {
        Fillet x => x.Loop ?? x.Edge.FirstOrDefault(),
        Chamfer x => x.Loop ?? x.Edge.FirstOrDefault(),
        _ => null,
    };

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

    /// <summary>
    /// A fillet or chamfer on an edge, in the first of these forms that fits:
    /// <list type="number">
    /// <item>two neighbouring edges of one swept profile: the profile corner itself is rounded or cut, convex or reflex;</item>
    /// <item>a convex edge between two faces of one intersection. A face may sit there directly, inside an earlier
    /// fillet (fillets sharing a face), or as the side of a swept profile that lies wholly behind it;</item>
    /// <item>for fillets, a concave edge: inside a cut, or where two joined solids meet (<see cref="ConcaveFillet"/>).</item>
    /// </list>
    /// Inside a shell the feature reshapes only the shell's outside.
    /// </summary>
    /// <param name="faceName">The new face's name; <c>id/face</c> unless a loop names it after the other face.</param>
    Field ApplyEdge(Feature feature, Field tree, Func<Surface, Surface> register, Point3D min, Point3D max, List<Diagnostic> diagnostics, string? faceName = null)
    {
        var name0 = faceName ?? $"{feature.Id}/face";
        var (edge, size, fillet) = feature switch
        {
            Fillet x => (x.Edge, x.Radius, true),
            Chamfer x => (x.Edge, x.Distance, false),
            _ => throw new NotSupportedException(feature.GetType().Name),
        };
        var (unsupported, invalid) = fillet ? (Diagnostic.UnsupportedFillet, Diagnostic.FilletInvalid) : (Diagnostic.UnsupportedChamfer, Diagnostic.ChamferInvalid);
        Field Fail(string code, string message)
        {
            diagnostics.Add(new Diagnostic(code, feature.Id, message));
            return tree;
        }
        var name = $"[{string.Join(", ", edge)}]";

        foreach (var p in FieldAlgebra.Descendants(tree).Concat(Intersections(tree, null).SelectMany(x => FieldAlgebra.Descendants(x.Host))).OfType<ProfileField>().Distinct())
        {
            var (la, lb) = (p.Leaves.FirstOrDefault(l => l.Surface.Source == edge[0]), p.Leaves.FirstOrDefault(l => l.Surface.Source == edge[1]));
            if (la is null || lb is null || p.CornerBetween(la, lb) is not { } corner) continue;
            // A loop fillet already run round this profile names its faces per side and would not know the new one.
            if (Intersections(tree, null).SelectMany(x => FieldAlgebra.Descendants(x.Host)).Concat(FieldAlgebra.Descendants(tree))
                .OfType<LoopEdgeField>().Any(l => FieldAlgebra.Descendants(l.Side).Contains(p)))
                return Fail(unsupported, $"round the corner between {edge[0]} and {edge[1]} before the loop fillet round this profile");
            var rebuilt = p.WithCorner(corner, size, fillet, (e, offset) =>
            {
                var leaf = ProfileField.EdgeLeaf(e, p.Frame, p.Revolved, (k, o, a, r, m, ang) => register(new Surface(0, name0, k, o, a, r, m, ang)));
                return leaf is null || offset == 0 ? leaf : new LeafField(register(leaf.Surface.Offset(0, leaf.Surface.Source, leaf.Complement ? -offset : offset)), leaf.Complement);
            });
            if (rebuilt is null)
                return Fail(invalid, $"{(fillet ? "radius" : "distance")} {size} does not fit on the straight profile edges next to the corner");
            var next = Replace(tree, p, rebuilt);
            // In a shell, the new face must stay clear of the cavity: probe its middle line on the new outside.
            // Project onto the new outside alone: where the wall is cut through, the shelled field is not zero there.
            var around = Intersections(next, null).FirstOrDefault(x => x.Shell is not null && FieldAlgebra.Descendants(x.Host).Contains(rebuilt)).Shell;
            if (around is not null && rebuilt.Leaves.Except(p.Leaves).SelectMany(l => rebuilt.MiddleOf(l, (max - min).Length))
                .Select(q => Project(around.Outer, q)).Any(q => Math.Abs(around.Outer.Value(q)) <= 1e-6 && Math.Abs(next.Value(q)) > 1e-9 || CutsShell(around, q) && Math.Abs(around.Outer.Value(q)) <= 1e-6))
                return Fail(invalid, $"radius {size} cuts through the shell wall");
            return next;
        }

        (MaxField Host, ShellField? Shell, LeafField? A, LeafField? B) Find() => Intersections(tree, null)
            .Select(x => (x.Host, x.Shell, A: Held(x.Host, edge[0]), B: Held(x.Host, edge[1])))
            .FirstOrDefault(x => x.A is not null && x.B is not null);
        var found = Find();
        // A face cut after a join (a hole drilled through a joined boss) meets the boss in no single intersection
        // until it is pushed into the union: max(min(X, Y), b) = min(max(X, b), max(Y, b)), exactly.
        while (found.Host is null && Distribute(tree, edge) is { } spread)
        {
            tree = spread;
            found = Find();
        }
        if (found.Host is null)
            return fillet
                ? ConcaveFillet(feature.Id, edge, size, tree, register, min, max, Fail, faceName)
                : Fail(unsupported, $"edge {name} is not a convex edge between two faces of one intersection");
        var (host, shell, a, b) = (found.Host, found.Shell, found.A!, found.B!);

        if (EdgeSamples(a.Surface, b.Surface, min, max) is not { } samples)
            return Fail(unsupported, $"no exact {feature.GetType().Name.ToLowerInvariant()} for {a.Surface.Kind}–{b.Surface.Kind} in this orientation");
        var onEdge = samples.Where(q => Math.Abs(tree.Value(q)) <= 10 * _tol).ToList();
        if (onEdge.Count == 0) return Fail(invalid, $"edge {name} does not exist on the part");

        Field addition;
        if (fillet)
        {
            var rounded = new RoundedEdgeField(a, b, size, register(new Surface(0, name0, SurfaceKind.Fillet, new Point3D(0, 0, 0), new Vector3D(0, 0, 1), size)));
            // On a face's own plane the solid field is ≥ 0, and 0 only on the face itself: a contact point with
            // a positive value means the ball does not fit on that face.
            if (onEdge.Select(rounded.Contacts).Any(c => tree.Value(c.OnA) > 10 * _tol || tree.Value(c.OnB) > 10 * _tol))
                return Fail(invalid, $"radius {size} does not fit between the faces next to the edge");
            // The arc's middle must stay outside the cavity, or the round cuts through the wall.
            if (shell is not null && onEdge.Select(rounded.Contacts).Any(c => -shell.Child.Value(Middle(c, size)) - shell.Thickness > -10 * _tol))
                return Fail(invalid, $"radius {size} cuts through the shell wall");
            addition = rounded;
        }
        else
        {
            if (onEdge.Select(q => Setback(a, b, q, size)).Any(c => tree.Value(c.OnA) > 10 * _tol || tree.Value(c.OnB) > 10 * _tol))
                return Fail(invalid, $"distance {size} does not fit between the faces next to the edge");
            addition = ChamferLeaf(feature.Id, a, b, onEdge[0], size, s => register(s with { Source = name0 }));
        }

        // A face held directly is now inside the fillet; a chamfer only adds its own face.
        var children = host.Children.Where(c => !fillet || (c != a && c != b)).Append(addition).ToList();
        return Replace(tree, host, new MaxField(children));
    }

    /// <summary>
    /// A fillet that adds material in an inside corner. The empty corner between the faces is convex, so the ball
    /// rolls there exactly as on a convex edge, and the material it leaves behind is added:
    /// <list type="bullet">
    /// <item>inside a cut the two faces belong to the tool, which the normalized tree holds as a union of complements:
    /// rounding the tool's convex edge is the whole fillet;</item>
    /// <item>where two joined solids meet, a filler (corner gap minus rounded gap) joins them, cut by each solid's other
    /// faces so it ends flush where the edge ends, as a CAD fillet on that one edge does.</item>
    /// </list>
    /// </summary>
    /// <param name="face">The fillet face's name; <c>id/face</c> unless a loop names it after the other face.</param>
    Field ConcaveFillet(string id, string[] edge, double r, Field tree, Func<Surface, Surface> register, Point3D min, Point3D max, Func<string, string, Field> fail, string? face = null)
    {
        var name = $"[{string.Join(", ", edge)}]";
        LeafField Flip(LeafField l) => new(l.Surface, !l.Complement);

        // Checks the edge is there and the ball touches both faces on the part; returns null when it does.
        string? Problem(RoundedEdgeField rounded, Func<Point3D, bool> onBoth, out string code)
        {
            code = Diagnostic.FilletInvalid;
            if (EdgeSamples(rounded.A.Surface, rounded.B.Surface, min, max) is not { } samples)
            {
                code = Diagnostic.UnsupportedFillet;
                return $"no exact fillet for {rounded.A.Surface.Kind}–{rounded.B.Surface.Kind} in this orientation";
            }
            var onEdge = samples.Where(q => Math.Abs(tree.Value(q)) <= 10 * _tol && onBoth(q)).ToList();
            if (onEdge.Count == 0) return $"edge {name} does not exist on the part";
            return onEdge.Select(rounded.Contacts).Any(c => Math.Abs(tree.Value(c.OnA)) > 10 * _tol || Math.Abs(tree.Value(c.OnB)) > 10 * _tol)
                ? $"radius {r} does not fit between the faces next to the edge" : null;
        }

        var unions = Unions(tree).ToList();
        foreach (var union in unions)
        {
            if (FlippedIn(union, edge[0]) is not { } a || FlippedIn(union, edge[1]) is not { } b || a.Flipped == b.Flipped) continue;
            var rounded = new RoundedEdgeField(a.Flipped, b.Flipped, r, register(FilletSurface(id, r, face)));
            if (Problem(rounded, _ => true, out var code) is { } problem) return fail(code, problem);
            var kept = union.Children.Where(c => c != a.Direct && c != b.Direct).Append(new NegateField(rounded)).ToList();
            return Replace(tree, union, new MinField(kept));
        }

        foreach (var union in unions)
        for (var i = 0; i < union.Children.Count; i++)
        for (var j = 0; j < union.Children.Count; j++)
        {
            if (i == j || HostIn(union.Children[i], edge[0]) is not { } one || HostIn(union.Children[j], edge[1]) is not { } two) continue;
            var rounded = new RoundedEdgeField(Flip(one.Leaf), Flip(two.Leaf), r, register(FilletSurface(id, r, face)));
            // The edge is where the two solids touch: on the boundary of both, not just on one face's line.
            var (first, second) = (union.Children[i], union.Children[j]);
            if (Problem(rounded, q => Math.Abs(first.Value(q)) <= 10 * _tol && Math.Abs(second.Value(q)) <= 10 * _tol, out var code) is { } problem)
                return fail(code, problem);
            // Gap corner minus the rounded gap, kept within the extent of both faces.
            var filler = new MaxField([rounded.A, rounded.B, new NegateField(rounded), .. Clips(one), .. Clips(two)]);
            return Replace(tree, union, new MinField([.. union.Children, filler]));
        }
        return fail(Diagnostic.UnsupportedFillet, $"edge {name} is neither a convex edge of one intersection nor an inside corner of a cut or a join");
    }

    /// <summary>
    /// One step of pushing a face that an intersection holds directly into a union beside it whose solids hold the
    /// edge's other face. Null when there is no such pair.
    /// </summary>
    static Field? Distribute(Field tree, string[] edge)
    {
        foreach (var (m, _) in Intersections(tree, null))
        for (var s = 0; s < 2; s++)
        {
            if (m.Children.OfType<LeafField>().FirstOrDefault(l => l.Surface.Source == edge[s]) is not { } leaf) continue;
            var other = edge[1 - s];
            // The union holds the other face in one of its solids, or as a wall of its own (a block-shaped cut).
            if (m.Children.OfType<MinField>().FirstOrDefault(u => u.Children.Any(c => c is LeafField l && l.Surface.Source == other)
                    || Intersections(u, null).Any(x => Held(x.Host, other) is not null)) is not { } union) continue;
            Field Push(Field c) => c is MaxField mx ? new MaxField([.. mx.Children, leaf]) : new MaxField([c, leaf]);
            var pushed = new MinField(union.Children.Select(Push).ToList());
            return Replace(tree, m, new MaxField([.. m.Children.Where(c => c != leaf && c != union), pushed]));
        }
        return null;
    }

    /// <summary>
    /// A fillet or chamfer on every edge round one face:
    /// <list type="bullet">
    /// <item>a swept profile the face caps in its intersection (the end of a boss, rib or outline): the whole loop at
    /// once, exact (<see cref="LoopEdgeField"/>);</item>
    /// <item>a cut whose profile runs through the face (the rim of a cut-out): the corner material all round the rim
    /// is taken away;</item>
    /// <item>where the face is the floor of a cut (a pocket): the cut's own loop is rounded, which adds material;</item>
    /// <item>every other face it meets in its intersection (a block's sides, a hole): one edge each.</item>
    /// </list>
    /// Loop faces are named after the face on the other side of their edge: <c>round/rib/a</c>.
    /// </summary>
    Field ApplyLoop(Feature feature, string face, double size, bool round, Field tree, Func<Surface, Surface> register, Point3D min, Point3D max, List<Diagnostic> diagnostics)
    {
        var (unsupported, invalid) = round ? (Diagnostic.UnsupportedFillet, Diagnostic.FilletInvalid) : (Diagnostic.UnsupportedChamfer, Diagnostic.ChamferInvalid);
        Field Fail(string code, string message)
        {
            diagnostics.Add(new Diagnostic(code, feature.Id, message));
            return tree;
        }
        Surface Named(string other) => register(new Surface(0, $"{feature.Id}/{other}", SurfaceKind.Fillet, new Point3D(0, 0, 0), new Vector3D(0, 0, 1), size));
        Dictionary<int, Surface> FacesOf(ProfileField p) => p.Leaves.ToDictionary(l => l.Surface.Id, l => Named(l.Surface.Source));
        // The swept profile under a field: itself, or under loops already run round it (an extrude looped at both ends).
        ProfileField? Across(Field f, LeafField cap) => f switch
        {
            ProfileField p when !p.Revolved && Math.Abs(p.Frame.Z.Dot(cap.Surface.Axis)) > 1 - 1e-9 => p,
            LoopEdgeField l => Across(l.Side, cap),
            _ => null,
        };
        bool OnPart(Point3D q) => Math.Abs(tree.Value(q)) <= 10 * _tol;

        // Where the cap meets the profile on the part (and, for a rim, where the cut really reaches the cap), and
        // whether the ball (or setback) lands on both faces there. Null samples: the loop does not exist there.
        (List<Point3D>? Samples, string? Problem) Check(LoopEdgeField loop, ProfileField p, bool rim, ShellField? shell)
        {
            var onEdge = p.Leaves
                .SelectMany(e => EdgeSamples(loop.Cap.Surface, e.Surface, min, max) ?? [])
                .Where(q => OnPart(q) && Math.Abs(p.Value(q)) <= 10 * _tol)
                .Where(q => !rim || tree.Value(q + (loop.Side.Sample(q).Gradient - loop.Cap.Sample(q).Gradient) * 1e-4) > 0)
                .ToList();
            if (onEdge.Count == 0) return (null, null);
            foreach (var q in onEdge)
            {
                var (ga, gb) = (loop.Cap.Sample(q).Gradient, loop.Side.Sample(q).Gradient);
                if (!OnPart(q - gb * size) || !OnPart(q - ga * size))
                    return (onEdge, $"{(round ? "radius" : "distance")} {size} does not fit round {face}");
                if (shell is not null && CutsShell(shell, MidEdge(q, ga, gb, size, round)))
                    return (onEdge, $"{(round ? "radius" : "distance")} {size} cuts through the shell wall");
            }
            return (onEdge, null);
        }

        var found = Intersections(tree, null).FirstOrDefault(x => x.Host.Children.Any(c => c is LeafField l && l.Surface.Source == face));
        if (found.Host is { } host)
        {
            var cap = host.Children.OfType<LeafField>().First(l => l.Surface.Source == face);
            if (cap.Surface.Kind != SurfaceKind.Plane) return Fail(unsupported, $"a loop runs round a flat face; {face} is a {cap.Surface.Kind.ToString().ToLowerInvariant()}");
            var children = new List<Field>();
            var added = new List<Field>();
            var walls = new List<string>();
            var unhandled = new List<string>();
            foreach (var c in host.Children)
            {
                switch (c)
                {
                    case ProfileField or LoopEdgeField when Across(c, cap) is { } p:
                        var loop = new LoopEdgeField(cap, c, size, round, FacesOf(p));
                        var (samples, problem) = Check(loop, p, rim: false, found.Shell);
                        if (problem is not null) return Fail(invalid, problem);
                        children.Add(samples is null ? c : loop);
                        break;

                    case LeafField l when l != cap && EdgeSamples(cap.Surface, l.Surface, min, max) is { } lineOrCircle:
                        var onEdge = lineOrCircle.Where(OnPart).ToList();
                        children.Add(l);
                        if (onEdge.Count == 0) break;
                        if (round)
                        {
                            var rounded = new RoundedEdgeField(cap, l, size, Named(l.Surface.Source));
                            if (onEdge.Select(rounded.Contacts).Any(x => tree.Value(x.OnA) > 10 * _tol || tree.Value(x.OnB) > 10 * _tol))
                                return Fail(invalid, $"radius {size} does not fit between {face} and {l.Surface.Source}");
                            if (found.Shell is { } shell && onEdge.Select(rounded.Contacts).Any(x => CutsShell(shell, Middle(x, size))))
                                return Fail(invalid, $"radius {size} cuts through the shell wall");
                            children[^1] = rounded;
                        }
                        else
                        {
                            if (onEdge.Select(q => Setback(cap, l, q, size)).Any(x => tree.Value(x.OnA) > 10 * _tol || tree.Value(x.OnB) > 10 * _tol))
                                return Fail(invalid, $"distance {size} does not fit between {face} and {l.Surface.Source}");
                            added.Add(ChamferLeafNamed(feature.Id, l.Surface.Source, cap, l, onEdge[0], size, register));
                        }
                        break;

                    case MinField cut when cut.Children.OfType<NegateField>().Select(n => Across(n.Child, cap)).FirstOrDefault(t => t is not null) is { } tool:
                        // The rim of a cut-out: round the cap against the outside of the tool, and take away only the
                        // corner material that rounding leaves out, so the cut's floor below stays where it is.
                        children.Add(c);
                        var rim = new LoopEdgeField(cap, new NegateField(tool), size, round, FacesOf(tool));
                        var (rimSamples, rimProblem) = Check(rim, tool, rim: true, found.Shell);
                        if (rimProblem is not null) return Fail(invalid, rimProblem);
                        if (rimSamples is not null) added.Add(new NegateField(new MaxField([cap, new NegateField(tool), new NegateField(rim)])));
                        break;

                    case MinField cut:
                        // A block-shaped cut-out: each wall that reaches the cap is one edge, rounded on its own below.
                        children.Add(c);
                        walls.AddRange(cut.Children.OfType<LeafField>()
                            .Where(w => EdgeSamples(cap.Surface, w.Surface, min, max) is { } ws && ws.Any(q => OnPart(q) && Math.Abs(cut.Value(q)) <= 10 * _tol))
                            .Select(w => w.Surface.Source));
                        break;

                    default:
                        children.Add(c);
                        // Anything else that meets the cap on the part is an edge this loop cannot round: say so.
                        foreach (var l in FieldAlgebra.Descendants(c).OfType<LeafField>())
                            if (l.Surface.Source != face && EdgeSamples(cap.Surface, l.Surface, min, max) is { } es && es.Any(q => OnPart(q) && Math.Abs(c.Value(q)) <= 10 * _tol))
                                unhandled.Add(l.Surface.Source);
                        break;
                }
            }
            if (unhandled.Count > 0)
                return Fail(unsupported, $"the loop round {face} cannot {(round ? "round" : "chamfer")} its edge with {string.Join(", ", unhandled.Distinct())}");
            if (children.SequenceEqual(host.Children) && added.Count == 0 && walls.Count == 0)
                return Fail(invalid, $"{face} has no edges to {(round ? "round" : "chamfer")}");
            tree = Replace(tree, host, new MaxField([.. children, .. added]));
            foreach (var w in walls)
            {
                Feature single = round ? new Fillet(feature.Id, [face, w], size) : new Chamfer(feature.Id, [face, w], size);
                tree = ApplyEdge(single, tree, register, min, max, diagnostics, $"{feature.Id}/{w}");
            }
            return tree;
        }

        // The floor of a cut: the tool is a union of complements; rounding its own loop adds material in the pocket.
        foreach (var union in Unions(tree))
        {
            if (union.Children.OfType<LeafField>().FirstOrDefault(l => l.Surface.Source == face) is not { } floor) continue;
            var flipped = new LeafField(floor.Surface, !floor.Complement);
            var wall = union.Children.OfType<NegateField>().FirstOrDefault(n => Across(n.Child, flipped) is not null);
            if (wall is null)
            {
                // A block-shaped cut: its floor meets each wall in one inside edge.
                if (!round) return Fail(unsupported, $"a chamfer loop round the floor of a cut needs a swept cut");
                var blockWalls = union.Children.OfType<LeafField>().Where(l => l != floor && Math.Abs(l.Surface.Axis.Dot(floor.Surface.Axis)) < 1e-9).Select(l => l.Surface.Source).ToList();
                if (blockWalls.Count == 0) return Fail(invalid, $"{face} has no edges to round");
                foreach (var w in blockWalls)
                    tree = ConcaveFillet(feature.Id, [face, w], size, tree, register, min, max, Fail, $"{feature.Id}/{w}");
                return tree;
            }
            var tool = Across(wall.Child, flipped)!;
            var loop = new LoopEdgeField(flipped, wall.Child, size, round, FacesOf(tool));
            var (samples, problem) = Check(loop, tool, rim: false, null);
            if (problem is not null) return Fail(invalid, problem);
            if (samples is null) return Fail(invalid, $"{face} meets no swept side there");
            return Replace(tree, union, new MinField([.. union.Children.Where(c => c != floor && c != wall), new NegateField(loop)]));
        }
        return Fail(unsupported, $"{face} is neither a face of one intersection nor the floor of a swept cut");
    }

    /// <summary>The middle of the fillet arc (or chamfer face) over an edge point q whose faces meet at right angles.</summary>
    static Point3D MidEdge(Point3D q, Vector3D ga, Vector3D gb, double size, bool round)
    {
        if (!round) return q - (ga + gb) * (size / 2);
        var centre = q - (ga + gb) * size;
        return centre + (ga + gb).Normalized() * size;
    }

    static Point3D Project(Field f, Point3D q)
    {
        for (var i = 0; i < 4; i++)
        {
            var s = f.Sample(q);
            q -= s.Gradient * (s.Value / Math.Max(s.Gradient.LengthSquared, 1e-12));
        }
        return q;
    }

    /// <summary>A point of a new outer surface inside the shell's cavity: the round has cut through the wall.</summary>
    static bool CutsShell(ShellField shell, Point3D onOuter) => -shell.Child.Value(onOuter) - shell.Thickness > -1e-9;

    static LeafField ChamferLeafNamed(string id, string other, LeafField a, LeafField b, Point3D q, double d, Func<Surface, Surface> register) =>
        ChamferLeaf(id, a, b, q, d, s => register(s with { Source = $"{id}/{other}" }));

    static Surface FilletSurface(string id, double r, string? face = null) => new(0, face ?? $"{id}/face", SurfaceKind.Fillet, new Point3D(0, 0, 0), new Vector3D(0, 0, 1), r);

    /// <summary>A face of a union of complements as its flipped leaf: directly, or inside an earlier inside fillet.</summary>
    static (LeafField Flipped, Field? Direct)? FlippedIn(MinField union, string source)
    {
        foreach (var c in union.Children)
        {
            if (c is LeafField l && l.Surface.Source == source) return (new LeafField(l.Surface, !l.Complement), l);
            if (c is NegateField { Child: RoundedEdgeField r })
            {
                if (r.A.Surface.Source == source) return (r.A, null);
                if (r.B.Surface.Source == source) return (r.B, null);
            }
        }
        return null;
    }

    /// <summary>The intersection inside a joined solid that holds a face, the face, and the child holding it.</summary>
    static (MaxField Host, LeafField Leaf, Field Holder)? HostIn(Field solid, string source)
    {
        foreach (var (host, _) in Intersections(solid, null))
            if (Held(host, source) is { } leaf)
                return (host, leaf, host.Children.First(c => c == leaf || c is RoundedEdgeField r && (r.A == leaf || r.B == leaf) || c is ProfileField p && p.Leaves.Contains(leaf)));
        return null;
    }

    /// <summary>
    /// What bounds a face along its edge: the host's other children, the other face of a fillet that holds it, or
    /// the faces next to it in a swept profile.
    /// </summary>
    static IEnumerable<Field> Clips((MaxField Host, LeafField Leaf, Field Holder) h)
    {
        foreach (var c in h.Host.Children.Where(c => c != h.Holder)) yield return c;
        switch (h.Holder)
        {
            case RoundedEdgeField r:
                yield return r.A == h.Leaf ? r.B : r.A;
                break;
            case ProfileField p:
                foreach (var n in p.Neighbours(h.Leaf)) yield return n;
                break;
        }
    }

    /// <summary>Every union node, also inside a shell's outside.</summary>
    static IEnumerable<MinField> Unions(Field tree)
    {
        foreach (var node in FieldAlgebra.Descendants(tree))
        {
            if (node is MinField m) yield return m;
            if (node is ShellField s)
                foreach (var inner in Unions(s.Outer)) yield return inner;
        }
    }

    static Point3D Middle((Point3D Centre, Point3D OnA, Point3D OnB) c, double r)
    {
        var bisector = (c.OnA - c.Centre) + (c.OnB - c.Centre);
        return c.Centre + bisector.Normalized() * r;
    }

    /// <summary>The points at distance d from the edge point q, along each face away from the edge.</summary>
    static (Point3D OnA, Point3D OnB) Setback(LeafField a, LeafField b, Point3D q, double d)
    {
        var (ga, gb) = (a.Sample(q).Gradient, b.Sample(q).Gradient);
        var c = ga.Dot(gb);
        return (q + (ga * c - gb).Normalized() * d, q + (gb * c - ga).Normalized() * d);
    }

    /// <summary>
    /// The equal-distance chamfer face: a plane between two planes, a 45° cone on the edge of a hole or boss.
    /// Its material side is the one that leaves the sharp edge outside.
    /// </summary>
    static LeafField ChamferLeaf(string id, LeafField a, LeafField b, Point3D q, double d, Func<Surface, Surface> register)
    {
        var (onA, onB) = Setback(a, b, q, d);
        Surface template;
        if (a.Surface.Kind == SurfaceKind.Plane && b.Surface.Kind == SurfaceKind.Plane)
        {
            var along = a.Sample(q).Gradient.Cross(b.Sample(q).Gradient).Normalized();
            template = new Surface(0, $"{id}/face", SurfaceKind.Plane, onA, (onB - onA).Cross(along).Normalized());
        }
        else
        {
            var (plane, wall, onPlane) = a.Surface.Kind == SurfaceKind.Plane ? (a, b, onA) : (b, a, onB);
            var up = plane.Sample(q).Gradient;
            var axis = wall.Surface.Axis;
            var foot = wall.Surface.Origin + axis * axis.Dot(q - wall.Surface.Origin);
            // In (ρ, z) round the axis, z out of the face: the chamfer runs from (R, −d) to (ρ on the face, 0).
            var r = wall.Surface.Radius;
            var slope = (onPlane - (foot + axis * axis.Dot(onPlane - foot))).Length - r;
            slope /= d;
            template = new Surface(0, $"{id}/face", SurfaceKind.Cone, foot + up * (-d - r / slope), slope > 0 ? up : -up, Angle: Math.Atan(Math.Abs(slope)));
        }
        var s = register(template);
        var leaf = new LeafField(s);
        return leaf.Value(q) > 0 ? leaf : new LeafField(s, complement: true);
    }

    /// <summary>Every intersection node, also inside a shell's outside, with the shell it sits in.</summary>
    static IEnumerable<(MaxField Host, ShellField? Shell)> Intersections(Field tree, ShellField? shell)
    {
        foreach (var node in FieldAlgebra.Descendants(tree))
        {
            // An inside fillet's filler is no face-holding intersection of the part.
            if (node is MaxField m && !m.Children.Any(c => c is NegateField { Child: RoundedEdgeField or LoopEdgeField })) yield return (m, shell);
            if (node is ShellField s)
                foreach (var inner in Intersections(s.Outer, s)) yield return inner;
        }
    }

    /// <summary>The leaf of a source face as the intersection holds it, or null.</summary>
    static LeafField? Held(MaxField m, string source)
    {
        foreach (var c in m.Children)
        {
            switch (c)
            {
                case LeafField l when l.Surface.Source == source:
                    return l;
                case RoundedEdgeField r when r.A.Surface.Source == source:
                    return r.A;
                case RoundedEdgeField r when r.B.Surface.Source == source:
                    return r.B;
                case ProfileField p when p.Leaves.FirstOrDefault(l => l.Surface.Source == source) is { } side && p.Supports(side):
                    return side;
            }
        }
        return null;
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

    /// <summary>The tree with one node swapped; untouched branches stay the same objects.</summary>
    static Field Replace(Field field, Field target, Field replacement)
    {
        if (field == target) return replacement;
        switch (field)
        {
            case MaxField m:
                var max = m.Children.Select(c => Replace(c, target, replacement)).ToList();
                return max.SequenceEqual(m.Children) ? m : new MaxField(max);
            case MinField m:
                var min = m.Children.Select(c => Replace(c, target, replacement)).ToList();
                return min.SequenceEqual(m.Children) ? m : new MinField(min);
            case ShellField s:
                var outer = Replace(s.Outer, target, replacement);
                return outer == s.Outer ? s : s.WithOuter(outer);
            case BlendField b:
                var (ra, rb) = (Replace(b.A, target, replacement), Replace(b.B, target, replacement));
                return ra == b.A && rb == b.B ? b : new BlendField(ra, rb, b.K, b.Blend, b.Union);
            case NegateField n:
                var child = Replace(n.Child, target, replacement);
                return child == n.Child ? n : new NegateField(child);
            case LoopEdgeField l:
                var side = Replace(l.Side, target, replacement);
                return side == l.Side ? l : l.WithSide(side);
            default:
                return field;
        }
    }

    static Point3D P(double[] v) => new(v[0], v[1], v[2]);
    static Vector3D V(double[] v) => new(v[0], v[1], v[2]);
}
