using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Geometry;
using Isoform.Sketching;
using static System.FormattableString;

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
        doc = Parametric.Resolve(doc);

        // Sketches, solved where they stand in the history: a face sketch takes the face as the part has it there.
        var sketches = new Dictionary<string, (SweptShape? Shape, SketchSolution? Solution, string? Problem)>();
        // Projected part edges are resolved here, from the surfaces the part has so far; one that is not stays put.
        void Prepare(Sketch s)
        {
            var plane = s.On.Plane ?? SketchPlanes.OfFace(s.On.Face!, surfaces, tree);
            IReadOnlyDictionary<string, SketchCarrier>? carriers = null;
            IReadOnlyList<(string Entity, string Problem)> lost = [];
            if (plane is not null) (carriers, lost) = SketchReferences.Resolve(s, plane, surfaces);
            var solution = SketchSolver.Solve(s, carriers: carriers);
            foreach (var p in solution.Problems)
                diagnostics.Add(new Diagnostic(p.Code, s.Id, $"sketch {s.Id}: {p.Message}"));
            foreach (var (entity, why) in lost)
                diagnostics.Add(new Diagnostic(Diagnostic.SketchReferenceLost, s.Id, $"sketch {s.Id}: {entity} cannot follow its edge, so it stays where it was: {why}"));
            if (!solution.Solved)
            {
                sketches[s.Id] = (null, null, "does not solve");
                return;
            }
            var (regions, problem) = SketchRegions.Of(s, solution);
            if (problem is not null) diagnostics.Add(new Diagnostic(SketchSolution.Invalid, s.Id, $"sketch {s.Id}: {problem}"));
            if (plane is null)
            {
                diagnostics.Add(new Diagnostic(Diagnostic.SketchPlaneLost, s.Id, $"sketch {s.Id} lies on {s.On.Face}, which is not a flat face of the part here"));
                sketches[s.Id] = (null, null, "has no plane");
                return;
            }
            // Points serve holes even where the loops do not make regions.
            sketches[s.Id] = (new SweptShape(plane, problem is null ? regions : []), solution, problem is null ? null : "has no regions");
        }

        // Where a hole goes: one centre on a face, or one per sketch point, with the drilling direction (into the part)
        // and the name of each wall; null with a diagnostic when its face or sketch is not there.
        List<(Point3D At, Vector3D Into, string Name)>? Spots(Hole h)
        {
            if (h.On is null && h.Sketch is null) return [(new Point3D(h.Center[0], h.Center[1], 0), new Vector3D(0, 0, -1), "")];
            var plane = h.On is { } face ? SketchPlanes.OfFace(face, surfaces, tree) : sketches[h.Sketch!].Shape?.Plane;
            if (plane is null)
            {
                diagnostics.Add(new Diagnostic(h.On is null ? SketchSolution.Invalid : Diagnostic.SketchPlaneLost, h.Id,
                    h.On is { } f ? $"{h.Id} is on {f}, which is not a flat face of the part here" : $"{h.Id} uses sketch {h.Sketch}, which {sketches[h.Sketch!].Problem}"));
                return null;
            }
            var frame = Profiles.FrameOf(plane);
            if (h.On is not null) return [(frame.ToWorld(new Point3D(h.Center[0], h.Center[1], 0)), -frame.Z, "")];
            var points = sketches[h.Sketch!].Solution!.Points;
            return [.. h.Points!.Select(p => (frame.ToWorld(new Point3D(points[p].X, points[p].Y, 0)), -frame.Z, p + ":"))];
        }

        // The plane and regions an extrude or revolve sweeps: its own profile, or its sketch's regions.
        SweptShape? Shape(Feature f, string reportAs)
        {
            var (plane, profile, sketch, wanted) = f switch
            {
                Extrude e => (e.Plane, e.Profile, e.Sketch, e.Regions),
                Revolve r => (r.Plane, r.Profile, r.Sketch, r.Regions),
                _ => throw new ArgumentException(f.Id),
            };
            if (sketch is null) return new SweptShape(plane!, [new SketchRegion(profile!, [])]);
            var (shape, _, why) = sketches[sketch];
            var (regions, problem) = shape is null || why is not null ? ([], $"its sketch {sketch} {why}") : SketchRegions.Select(shape.Regions, wanted);
            if (problem is null && f is Revolve { Axis: { } axis })
            {
                var line = doc.Features.OfType<Sketch>().First(k => k.Id == sketch).Entities.OfType<SketchLine>().First(l => l.Id == axis);
                var points = sketches[sketch].Solution!.Points;
                return AboutAxis(shape! with { Regions = regions }, points[line.From], points[line.To]);
            }
            if (problem is null) return shape! with { Regions = regions };
            if (diagnostics.All(d => d.FeatureId != reportAs || d.Code != SketchSolution.Invalid))
                diagnostics.Add(new Diagnostic(SketchSolution.Invalid, reportAs, $"{reportAs} cannot sweep sketch {sketch}: {problem}"));
            return null;
        }

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
                    if (Shape(e, reportAs) is not { } extrudeShape) break;
                    var (extruded, extrudedBox) = ExtrudeField(e, extrudeShape, m, Named);
                    if (e.Op != BooleanOp.Cut) Grow(extrudedBox);
                    tree = Combine(Op(e.Op), extruded, e.Blend, Named, e.Id);
                    break;

                case Revolve r:
                    if (Shape(r, reportAs) is not { } revolveShape) break;
                    var (revolved, revolvedBox) = RevolveField(r, revolveShape, m, Named);
                    if (r.Op != BooleanOp.Cut) Grow(revolvedBox);
                    tree = Combine(Op(r.Op), revolved, r.Blend, Named, r.Id);
                    break;

                case Hole h:
                    if (Spots(h) is not { } spots) break;
                    foreach (var (at, into, spot) in spots)
                    {
                        // The axis points out of the face, +Z for a plain hole as it always has (exact export reads it).
                        var wall = Named(new Surface(0, $"{h.Id}/{spot}wall", SurfaceKind.Cylinder, m.ToWorld(at), m.ToWorld(-into), h.Diameter / 2));
                        diagnostics.AddRange(TangentContacts(reportAs, wall, tree!, surfaces, min, max));
                        if (h.Depth is not { } depth)
                        {
                            tree = new MaxField([tree!, new LeafField(wall, complement: true)]);
                            continue;
                        }
                        // Blind: the drill is the cylinder down to a floor depth below the face, open above it.
                        var floor = Named(new Surface(0, $"{h.Id}/{spot}floor", SurfaceKind.Plane, m.ToWorld(at + into * depth), m.ToWorld(into)));
                        tree = new MaxField([tree!, new NegateField(new MaxField([new LeafField(wall), new LeafField(floor)]))]);
                    }
                    break;

                case PlaneCut c:
                    var plane = Named(new Surface(0, $"{c.Id}/face", SurfaceKind.Plane, m.ToWorld(P(c.Point)), m.ToWorld(V(c.Normal).Normalized())));
                    tree = new MaxField([tree!, new LeafField(plane)]);
                    break;
            }
        }

        foreach (var feature in doc.Features)
        {
            // A sketch makes no solid and belongs to no body.
            if (feature is Sketch sketch)
            {
                Prepare(sketch);
                continue;
            }

            // Pick the body: a new one, the one the feature names, or for an edge the body that has its faces.
            if (DocumentRules.StartsBody(feature)) Start(feature.Body ?? feature.Id);
            else if (feature.Body is { } named) Switch(named);
            else if (EdgeFace(feature) is { } face && bodyOfSource.TryGetValue(face, out var holder)) Switch(holder);
            bodyOfFeature[feature.Id] = current!;
            // Edge features and patterns work where their faces or copies are, then hand the current body back.
            var resume = current;

            // These reshape the faces of the solid they find; a lattice has no faces of that kind to reshape.
            if (feature is Fillet or Chamfer or Shell or Offset && tree is not null && HasLattice(tree))
            {
                diagnostics.Add(new Diagnostic(Diagnostic.InvalidParameter, feature.Id,
                    $"{feature.Id} comes after a lattice on this body; put fillets, chamfers, shells and offsets before the lattice (a lattice's skin is its shell)"));
                if (resume is not null && bodies.ContainsKey(resume)) Switch(resume);
                continue;
            }

            switch (feature)
            {
                case Lattice l:
                    tree = LatticeOf(l, FieldAlgebra.Normalize(tree!), doc, Register, min, max, diagnostics, f => Shape(f, l.Id));
                    break;

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
                    {
                        tree = ShellOf(sh, solid, Register);
                        if (ThinCavity(sh, solid, min, max) is { } thin) diagnostics.Add(thin);
                    }
                    break;

                case Offset o:
                    if (OffsetOf(FieldAlgebra.Normalize(tree!), o.Distance, Register, new Dictionary<(LeafField, double), LeafField>(), o.Id, surfaces) is { } grown)
                    {
                        tree = grown;
                        // Growing moves every face out by at most the distance, and the box with them.
                        if (o.Distance > 0) Grow([min - new Vector3D(1, 1, 1) * o.Distance, max + new Vector3D(1, 1, 1) * o.Distance]);
                        // A thicker wall leaves a smaller cavity, which may now be too thin to mesh.
                        foreach (var s in FieldAlgebra.Descendants(grown).OfType<ShellField>())
                            if (ThinCavity(new Shell(o.Id, s.Thickness), s.Child, min, max) is { } thin) diagnostics.Add(thin);
                    }
                    else
                        diagnostics.Add(new Diagnostic(Diagnostic.InvalidParameter, o.Id,
                            Invariant($"offset {o.Distance} cannot move this shell: its wall changes by twice the distance and must stay thicker than zero, and a shell rounded after shelling can only grow")));
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
    /// The body filled with a lattice f: max(s, f), or max(s, min(f, −r)) within a region R, so the body stays solid
    /// outside it. With a skin of wall t, max(s, min(−s − t, f)): the shell's wall and the lattice in its cavity, joined
    /// where they meet. That is a shell of the child max(s, −f − t) with the outside s (−max(s, −f − t) − t = min(−s −
    /// t, f)), the lattice its own inner face and the others named as a shell names them; near the outside, where the
    /// cavity cannot reach, it simplifies to s alone. A region then joins the solid outside it: min(that, max(s, −r)).
    /// </summary>
    Field LatticeOf(Lattice l, Field solid, PartDocument doc, Func<Surface, Surface> register, Point3D min, Point3D max, List<Diagnostic> diagnostics, Func<Feature, SweptShape?> shape)
    {
        var face = register(new Surface(0, l.Face, SurfaceKind.Lattice, new Point3D(0, 0, 0), new Vector3D(0, 0, 1), l.Cell));
        var lattice = new LatticeField(DocumentRules.LatticeKindOf(l.Kind)!.Value, l.Cell, l.Thickness, face);
        var outside = l.Region is { } region ? new NegateField(RegionOf(l.Id, region, doc, register, shape)) : null;
        var skinned = l.Skin > 0 && !FieldAlgebra.Descendants(solid).Any(n => n is ShellField);
        if (l.Skin > 0 && !skinned)
            diagnostics.Add(new Diagnostic(Diagnostic.InvalidParameter, l.Id, "this body is already shelled; give the lattice no skin, or let the skin be the shell"));
        if (!skinned) return FieldAlgebra.Normalize(new MaxField([solid, outside is null ? lattice : new MinField([lattice, outside])]));

        var skin = new Shell(l.Id, l.Skin);
        if (ThinCavity(skin, solid, min, max) is { } thin) diagnostics.Add(thin);
        var inner = new Dictionary<int, int>(ShellOf(skin, solid, register).InnerOf) { [face.Id] = face.Id };
        Field filled = new ShellField(FieldAlgebra.Normalize(new MaxField([solid, lattice.Outside(l.Skin)])), l.Skin, inner).WithOuter(solid);
        return outside is null ? filled : new MinField([filled, FieldAlgebra.Normalize(new MaxField([solid, outside]))]);
    }

    /// <summary>A lattice's region as a solid, its faces named after the lattice: id/region:x- for a box, id/region:&lt;face&gt; for a feature.</summary>
    static Field RegionOf(string id, LatticeRegion region, PartDocument doc, Func<Surface, Surface> register, Func<Feature, SweptShape?> shape)
    {
        Surface Named(Surface s) => register(s with { Source = $"{id}/region:{(region.Feature is null ? s.Source[(s.Source.IndexOf('/') + 1)..] : s.Source)}" });
        return (region.Feature is not { } feature
            ? BlockField(new Block("box", BooleanOp.New, region.Origin!, region.Size!), Frame.Identity, Named)
            : doc[feature] switch
            {
                Block b => BlockField(b, Frame.Identity, Named),
                Extrude e => ExtrudeField(e, shape(e)!, Frame.Identity, Named),
                var r => RevolveField((Revolve)r, shape(r)!, Frame.Identity, Named),
            }).Item1;
    }

    /// <summary>Whether a lattice is in the tree, also inside a shell (a lattice's skin is one).</summary>
    static bool HasLattice(Field tree) => FieldAlgebra.Descendants(tree).Any(n => n is LatticeField || n is ShellField s && HasLattice(s.Child));

    /// <summary>
    /// A cavity no grid edge may cross: the shell's inside lies where the solid is deeper than the thickness, and if
    /// it never gets a cell size deeper (a cavity under 2 cells thick) the mesher can miss it, so it is reported.
    /// Best-first over boxes by their lower bound until the deepest point is resolved, absent or proved shallow.
    /// </summary>
    Diagnostic? ThinCavity(Shell sh, Field solid, Point3D min, Point3D max)
    {
        var (cavity, resolved) = (-sh.Thickness - _tol, -sh.Thickness - settings.CellSize);
        var root = new Box3D(min, max);
        var best = solid.Value(root.Center);
        var queue = new PriorityQueue<(Box3D Box, Field Field), double>();
        queue.Enqueue((root, solid.Simplify(root)), solid.Bounds(root).Lo);
        for (var budget = 100_000; queue.TryPeek(out _, out var lo) && budget > 0; budget--)
        {
            if (best <= resolved || lo >= cavity) return null;
            if (lo > resolved && best < cavity) break;
            var (box, f) = queue.Dequeue();
            foreach (var child in Queries.Split(box))
            {
                var h = f.Simplify(child);
                best = Math.Min(best, h.Value(child.Center));
                if (h.Bounds(child).Lo is var low && low < cavity) queue.Enqueue((child, h), low);
            }
        }
        if (queue.Count == 0) return null;
        // Out of budget nothing is settled; the doubt is reported rather than dropped.
        var size = best < cavity ? Invariant($"a cavity about {2 * (-best - sh.Thickness):G3} mm thick") : "a cavity too thin to measure, if any";
        return new Diagnostic(Diagnostic.BelowResolution, sh.Id,
            Invariant($"{sh.Id} leaves {size}, below the resolved size of {2 * settings.CellSize:G4} mm; use a smaller CellSize or a thinner shell"));
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
    /// see true geometry. Each leaf is offset once, so faces shared by fillets stay shared. A shell's wall changes by
    /// twice the distance; null when that closes it.
    /// </summary>
    static Field? OffsetOf(Field f, double d, Func<Surface, Surface> register, Dictionary<(LeafField, double), LeafField> done, string id, IReadOnlyList<Surface> surfaces)
    {
        LeafField Leaf(LeafField l, double by)
        {
            if (done.TryGetValue((l, by), out var known)) return known;
            return done[(l, by)] = new LeafField(register(l.Surface.Offset(0, l.Surface.Source, l.Complement ? -by : by)), l.Complement);
        }
        Field? Each(IReadOnlyList<Field> children, Func<List<Field>, Field> make)
        {
            var moved = children.Select(c => OffsetOf(c, d, register, done, id, surfaces)).ToList();
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
                    LoopEdgeField l when l == rim => OffsetOf(rim, d, register, done, id, surfaces),
                    _ when c == tool => OffsetOf(tool, -d, register, done, id, surfaces),
                    _ => OffsetOf(c, d, register, done, id, surfaces),
                }).ToList();
                return parts.Any(c => c is null) ? null : new MinField(parts!);
            case MinField m:
                return Each(m.Children, c => new MinField(c));
            case NegateField n:
                // −g − d = −(g + d): the complement's own faces move the other way.
                return OffsetOf(n.Child, -d, register, done, id, surfaces) is { } child ? new NegateField(child) : null;
            case RoundedEdgeField r:
                var (a, b) = (Leaf(r.A, d), Leaf(r.B, d));
                return r.Radius + d > 1e-9
                    ? r.With(a, b, r.Radius + d, register(r.Fillet with { Radius = r.Radius + d }))
                    : new MaxField([a, b]);
            case BlendField bl:
                // The smooth min shifts with its arguments, so the blend face keeps its width.
                return Each([bl.A, bl.B], c => new BlendField(c[0], c[1], bl.K, bl.Blend, bl.Union));
            case ProfileField p:
                return p.Offset(d, l => Leaf(l, d), register, (a, b) => $"{id}/corner:{a}+{b}");
            case LoopEdgeField lp:
                // A rounded loop grows with the faces; a chamfer plane (a + b + s)/√2 moves by d when s grows by (2 − √2)d.
                var size = lp.Round ? lp.Size + d : lp.Size + (2 - Math.Sqrt(2)) * d;
                if (OffsetOf(lp.Side, d, register, done, id, surfaces) is not { } side) return null;
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
            case ShellField sh:
                // max(outer, −c − t) − d = max(outer − d, −(c − d) − (t + 2d)): the cavity is the moved child's, the
                // wall t + 2d thick, and its faces are named again as the shell named them. Null once the wall closes.
                // An outside rounded after the shell is thinner than t at the round, and thinner still once shrunk: it
                // may only grow.
                var wall = sh.Thickness + 2 * d;
                var plain = ReferenceEquals(sh.Outer, sh.Child);
                if (wall <= 1e-9 || !plain && d < 0 || OffsetOf(sh.Child, d, register, done, id, surfaces) is not { } solid) return null;
                var inner = surfaces[sh.InnerOf.Values.First()].Source;
                var shelled = ShellOf(new Shell(inner[..inner.IndexOf("/inner:", StringComparison.Ordinal)], wall), solid, register);
                if (plain) return shelled;
                return OffsetOf(sh.Outer, d, register, done, id, surfaces) is { } outside ? shelled.WithOuter(outside) : null;
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

    /// <summary>A plane and the regions to sweep in it: an inline profile, or a sketch's regions (outer loops with holes).</summary>
    sealed record SweptShape(SketchPlane Plane, IReadOnlyList<SketchRegion> Regions);

    /// <summary>
    /// The 2D solid of the regions in the sketch frame, swept straight or round: per region the outer loop less its
    /// holes, and the union of the regions. Faces are named after the profile edge (or sketch entity) that sweeps them.
    /// </summary>
    static Field Regions(string id, SweptShape shape, Frame frame, bool revolve, Func<Surface, Surface> register)
    {
        Field Loop(Profile p)
        {
            var edges = Profiles.CounterClockwise(p);
            var leaves = edges.Select(x => ProfileField.EdgeLeaf(x.Edge, frame, revolve,
                (k, o, a, r, m, ang) => register(new Surface(0, $"{id}/{x.Id}", k, o, a, r, m, ang)))).ToList();
            return new ProfileField(edges.Select(x => x.Edge).ToList(), leaves, frame, revolve);
        }
        var regions = shape.Regions.Select(r => r.Holes.Count == 0 ? Loop(r.Outer)
            : (Field)new MaxField([Loop(r.Outer), .. r.Holes.Select(h => (Field)new NegateField(Loop(h)))])).ToList();
        return regions.Count == 1 ? regions[0] : new MinField(regions);
    }

    /// <summary>
    /// The shape in a plane of its own whose y axis runs along the line from a to b, so a revolve turns round that
    /// line: the same plane through a, turned in itself, and the regions in its coordinates. When the regions lie on the
    /// line's other side the plane turns half round as well, as a revolve takes x as the distance from the axis.
    /// </summary>
    static SweptShape AboutAxis(SweptShape shape, (double X, double Y) a, (double X, double Y) b)
    {
        var length = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
        var (dx, dy) = ((b.X - a.X) / length, (b.Y - a.Y) / length);
        // The new x is the line's direction turned a quarter clockwise: (dy, -dx).
        double Across(double[] p) => (p[0] - a.X) * dy - (p[1] - a.Y) * dx;
        var side = shape.Regions.SelectMany(r => r.Outer.Edges).Sum(e => Across(e.To)) >= 0 ? 1 : -1;
        double[] To(double[] p) => [side * Across(p), side * ((p[0] - a.X) * dx + (p[1] - a.Y) * dy)];
        Profile Map(Profile p) => new(To(p.Start), [.. p.Edges.Select(e => e with { To = To(e.To) })]);
        var frame = Profiles.FrameOf(shape.Plane);
        var origin = frame.ToWorld(new Point3D(a.X, a.Y, 0));
        var x = frame.X * (side * dy) + frame.Y * (-side * dx);
        var plane = new SketchPlane([origin.X, origin.Y, origin.Z], [x.X, x.Y, x.Z], shape.Plane.Normal);
        return new SweptShape(plane, [.. shape.Regions.Select(r => new SketchRegion(Map(r.Outer), [.. r.Holes.Select(Map)]))]);
    }

    /// <summary>A 2D box round every region's outer loop.</summary>
    static (double U0, double V0, double U1, double V1) Bounds(SweptShape shape) => shape.Regions.Select(r => Profiles.Bounds(r.Outer))
        .Aggregate((a, b) => (Math.Min(a.U0, b.U0), Math.Min(a.V0, b.V0), Math.Max(a.U1, b.U1), Math.Max(a.V1, b.V1)));

    /// <summary>The regions swept between two caps, and the corners of a box round them.</summary>
    static (Field, Point3D[]) ExtrudeField(Extrude e, SweptShape shape, Frame placement, Func<Surface, Surface> register)
    {
        var frame = Profiles.FrameOf(shape.Plane).Then(placement);
        var profile = Regions(e.Id, shape, frame, revolve: false, register);
        var start = register(new Surface(0, $"{e.Id}/start", SurfaceKind.Plane, frame.Origin + frame.Z * e.Start, -frame.Z));
        var end = register(new Surface(0, $"{e.Id}/end", SurfaceKind.Plane, frame.Origin + frame.Z * e.End, frame.Z));
        var field = new MaxField([profile, new LeafField(start), new LeafField(end)]);

        var (u0, v0, u1, v1) = Bounds(shape);
        var corners = from u in new[] { u0, u1 } from v in new[] { v0, v1 } from w in new[] { e.Start, e.End }
                      select frame.ToWorld(new Point3D(u, v, w));
        return (field, corners.ToArray());
    }

    /// <summary>The regions revolved round the sketch's y axis, cut to the swept angle by two half-planes below 360°.</summary>
    static (Field, Point3D[]) RevolveField(Revolve r, SweptShape shape, Frame placement, Func<Surface, Surface> register)
    {
        var frame = Profiles.FrameOf(shape.Plane).Then(placement);
        var field = Regions(r.Id, shape, frame, revolve: true, register);
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
        var (_, v0, u1, v1) = Bounds(shape);
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
                return Fail(invalid, Invariant($"{(fillet ? "radius" : "distance")} {size} does not fit on the straight profile edges next to the corner"));
            var next = Replace(tree, p, rebuilt);
            // In a shell, the new face must stay clear of the cavity: probe its middle line on the new outside.
            // Project onto the new outside alone: where the wall is cut through, the shelled field is not zero there.
            var around = Intersections(next, null).FirstOrDefault(x => x.Shell is not null && FieldAlgebra.Descendants(x.Host).Contains(rebuilt)).Shell;
            if (around is not null && rebuilt.Leaves.Except(p.Leaves).SelectMany(l => rebuilt.MiddleOf(l, (max - min).Length))
                .Select(q => Project(around.Outer, q)).Any(q => Math.Abs(around.Outer.Value(q)) <= 1e-6 && Math.Abs(next.Value(q)) > 1e-9 || CutsShell(around, q) && Math.Abs(around.Outer.Value(q)) <= 1e-6))
                return Fail(invalid, Invariant($"radius {size} cuts through the shell wall"));
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
            return RevolvedRim(feature.Id, edge, size, fillet, tree, register, min, max, Fail, name0)
                ?? SweptEdge(feature.Id, edge, size, fillet, tree, register, min, max, Fail, name0)
                ?? (fillet
                    ? ConcaveFillet(feature.Id, edge, size, tree, register, min, max, Fail, faceName)
                    : Fail(unsupported, $"edge {name} is not a convex edge between two faces of one intersection"));
        var (host, shell, a, b) = (found.Host, found.Shell, found.A!, found.B!);

        Field addition;
        if (fillet)
        {
            var (rounded, onEdge, code, problem) = Rolling(a, b, size, FilletSurface(feature.Id, size, name0), q => Math.Abs(tree.Value(q)) <= 10 * _tol, register, min, max, name);
            if (rounded is null) return Fail(code, problem!);
            // On a face's own plane the solid field is ≥ 0, and 0 only on the face itself: a contact point with
            // a positive value means the ball does not fit on that face. A fillet on two other faces can have eaten
            // the end of a face near a corner; the rounds then meet in a mitre, as fillets sharing a face do, so the
            // ball must fit on the faces as they were before such rounds.
            var mitred = FieldAlgebra.Descendants(tree).OfType<RoundedEdgeField>()
                .Where(r => new[] { r.A, r.B }.All(l => l.Surface.Source != a.Surface.Source && l.Surface.Source != b.Surface.Source))
                .Aggregate(tree, (t, r) => Replace(t, r, new MaxField([r.A, r.B])));
            if (onEdge.Select(rounded.Contacts).Any(c => mitred.Value(c.OnA) > 10 * _tol || mitred.Value(c.OnB) > 10 * _tol))
                return Fail(invalid, Invariant($"radius {size} does not fit between the faces next to the edge"));
            // The arc's middle must stay outside the cavity, or the round cuts through the wall.
            if (shell is not null && onEdge.Select(rounded.Contacts).Any(c => -shell.Child.Value(Middle(c, size)) - shell.Thickness > -10 * _tol))
                return Fail(invalid, Invariant($"radius {size} cuts through the shell wall"));
            addition = rounded;
        }
        else
        {
            if (!Flat(a.Surface, b.Surface) || EdgeSamples(a.Surface, b.Surface, min, max) is not { } samples)
                return Fail(unsupported, $"no exact chamfer for {a.Surface.Kind}–{b.Surface.Kind} in this orientation");
            var onEdge = samples.Where(q => Math.Abs(tree.Value(q)) <= 10 * _tol).ToList();
            if (onEdge.Count == 0) return Fail(invalid, $"edge {name} does not exist on the part");
            if (onEdge.Select(q => Setback(a, b, q, size)).Any(c => tree.Value(c.OnA) > 10 * _tol || tree.Value(c.OnB) > 10 * _tol))
                return Fail(invalid, Invariant($"distance {size} does not fit between the faces next to the edge"));
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

        // The rounded edge where the edge is there and the ball touches both faces on the part; or why not.
        (RoundedEdgeField? Rounded, string Code, string? Problem) Round(LeafField a, LeafField b, Func<Point3D, bool> onBoth)
        {
            var (rounded, onEdge, code, problem) = Rolling(a, b, r, FilletSurface(id, r, face), q => Math.Abs(tree.Value(q)) <= 10 * _tol && onBoth(q), register, min, max, name);
            if (rounded is null) return (null, code, problem);
            return onEdge.Select(rounded.Contacts).Any(c => Math.Abs(tree.Value(c.OnA)) > 10 * _tol || Math.Abs(tree.Value(c.OnB)) > 10 * _tol)
                ? (null, Diagnostic.FilletInvalid, Invariant($"radius {r} does not fit between the faces next to the edge")) : (rounded, "", null);
        }

        var unions = Unions(tree).ToList();
        foreach (var union in unions)
        {
            if (FlippedIn(union, edge[0]) is not { } a || FlippedIn(union, edge[1]) is not { } b || a.Flipped == b.Flipped) continue;
            var (rounded, code, problem) = Round(a.Flipped, b.Flipped, _ => true);
            if (rounded is null) return fail(code, problem!);
            var kept = union.Children.Where(c => c != a.Direct && c != b.Direct).Append(new NegateField(rounded)).ToList();
            return Replace(tree, union, new MinField(kept));
        }

        foreach (var union in unions)
        for (var i = 0; i < union.Children.Count; i++)
        for (var j = 0; j < union.Children.Count; j++)
        {
            if (i == j || HostIn(union.Children[i], edge[0]) is not { } one || HostIn(union.Children[j], edge[1]) is not { } two) continue;
            // The edge is where the two solids touch: on the boundary of both, not just on one face's line.
            var (first, second) = (union.Children[i], union.Children[j]);
            var (rounded, code, problem) = Round(Flip(one.Leaf), Flip(two.Leaf), q => Math.Abs(first.Value(q)) <= 10 * _tol && Math.Abs(second.Value(q)) <= 10 * _tol);
            if (rounded is null) return fail(code, problem!);
            // Gap corner minus the rounded gap, kept within the extent of both faces.
            var filler = new MaxField([rounded.A, rounded.B, new NegateField(rounded), .. Clips(one), .. Clips(two)]);
            return Replace(tree, union, new MinField([.. union.Children, filler]));
        }
        return fail(Diagnostic.UnsupportedFillet, $"edge {name} is neither a convex edge of one intersection nor an inside corner of a cut or a join");
    }

    /// <summary>
    /// A fillet or chamfer on the edge between a flat cap and a side of a swept profile that does not lie behind the
    /// edge (the riser of an L), or between a face of the part and a side of a swept cut (the rim of a cut-out). The
    /// corner material along the edge is taken away, W = max(cap, side, −loop, clips), as the rim of a loop does, and
    /// the edge runs on round tangent neighbours. A closed run is the whole loop. An open one is a piece per edge,
    /// bounded where the run ends by the neighbour's face (a convex corner) or its complement (a reflex one), so there
    /// the neighbour's face gains a small piece under its own name. Caps across the sweep only. Null when the edge is
    /// not of this kind.
    /// </summary>
    Field? SweptEdge(string id, string[] edge, double size, bool round, Field tree, Func<Surface, Surface> register, Point3D min, Point3D max, Func<string, string, Field> fail, string faceName)
    {
        var (unsupported, invalid) = round ? (Diagnostic.UnsupportedFillet, Diagnostic.FilletInvalid) : (Diagnostic.UnsupportedChamfer, Diagnostic.ChamferInvalid);
        var (kind, what) = round ? ("fillet", "radius") : ("chamfer", "distance");
        foreach (var (host, shell) in Intersections(tree, null))
        for (var s = 0; s < 2; s++)
        {
            if (Held(host, edge[s]) is not { } cap) continue;
            foreach (var (p, cut) in SweptIn(host))
            {
                var i = Enumerable.Range(0, p.Edges.Count).FirstOrDefault(k => p.EdgeLeaves[k]?.Surface.Source == edge[1 - s], -1);
                if (i < 0) continue;
                if (cap.Surface.Kind != SurfaceKind.Plane || p.Revolved || cap.Surface.Axis.Cross(p.Frame.Z).Length > 1e-9)
                    return fail(unsupported, $"no exact {kind} between {edge[s]} and {edge[1 - s]}: the face must be flat and across the sweep");
                if (p.Grown != 0) return fail(unsupported, $"no exact {kind} on {edge[1 - s]}: its profile was offset");
                if (SweptEdgeField(id, p, i, cut, cap, size, round, register, faceName) is not { } w)
                    return fail(unsupported, $"no exact {kind} on {edge[1 - s]} where it ends at a corner: it would run on past the corner");
                var next = Replace(tree, host, new MaxField([.. host.Children, new NegateField(w.Field)]));

                // Where the cap meets the run on the part, with the side really a face there (just out of it, under the
                // cap, is outside), the ball or setback lands on both faces.
                bool OnPart(Point3D q) => Math.Abs(tree.Value(q)) <= 10 * _tol;
                var onEdge = w.Sides.SelectMany(l => (EdgeSamples(cap.Surface, l.Surface, min, max) ?? []).Select(q => (q, l)))
                    .Where(x => OnPart(x.q) && Math.Abs(p.Value(x.q)) <= 10 * _tol && p.Sample(x.q).Owner == x.l.Surface.Id
                        && tree.Value(x.q + (x.l.Sample(x.q).Gradient - cap.Sample(x.q).Gradient) * 1e-4) > 0)
                    .ToList();
                if (onEdge.Count == 0) return fail(invalid, $"edge [{string.Join(", ", edge)}] does not exist on the part");
                foreach (var (q, l) in onEdge)
                {
                    var (ga, gb) = (cap.Sample(q).Gradient, l.Sample(q).Gradient);
                    if (!OnPart(q - gb * size) || !OnPart(q - ga * size))
                        return fail(invalid, Invariant($"{what} {size} does not fit between {edge[0]} and {edge[1]}"));
                    if (shell is not null && CutsShell(shell, MidEdge(q, ga, gb, size, round)))
                        return fail(invalid, Invariant($"{what} {size} cuts through the shell wall"));
                }
                return next;
            }
        }
        return null;
    }

    /// <summary>
    /// A fillet or chamfer between a face of the part and a side of a revolved profile the intersection holds whole:
    /// the rim of a full revolved cut (a countersink, a stepped bore), or a side of a revolved solid that does not lie
    /// wholly behind the edge (a rounded rim met by a bore). The side's own surface is no face of the intersection (its
    /// solid reaches past the profile), so the corner the ball or chamfer leaves is taken away instead. Both faces are
    /// lines or circles in the (ρ, z) half-plane, and W is that one corner round the axis: past the round, between the
    /// faces' normal lines at the contacts, and within the faces grown by the size. Bounded by the faces themselves, W
    /// would share them with the part and leave a sheet of zero on each; grown, it reaches a little past them, into
    /// empty space. Null when the edge is not of this kind.
    /// </summary>
    Field? RevolvedRim(string id, string[] edge, double size, bool round, Field tree, Func<Surface, Surface> register, Point3D min, Point3D max, Func<string, string, Field> fail, string faceName)
    {
        static LeafField Flip(LeafField l) => new(l.Surface, !l.Complement);
        var name = $"[{string.Join(", ", edge)}]";
        foreach (var (host, shell) in Intersections(tree, null))
        for (var s = 0; s < 2; s++)
        {
            if (Held(host, edge[s]) is not { } cap) continue;
            foreach (var (p, cut) in host.Children.Select(c => c is NegateField { Child: ProfileField t } ? (t, true) : (c as ProfileField, false)))
            {
                if (p is not { Revolved: true } || p.EdgeLeaves.FirstOrDefault(l => l?.Surface.Source == edge[1 - s]) is not { } leaf) continue;
                var side = cut ? Flip(leaf) : leaf;
                // On the part, and on the side's own face of the profile.
                bool OnRim(Point3D q) => Math.Abs(tree.Value(q)) <= 10 * _tol && p.Sample(q).Owner == leaf.Surface.Id;
                bool OnPart(Point3D q) => Math.Abs(tree.Value(q)) <= 10 * _tol;
                Field away;
                List<(Point3D OnA, Point3D OnB)> contacts;
                if (round)
                {
                    var (rounded, onEdge, code, problem) = Rolling(cap, side, size, FilletSurface(id, size, faceName), OnRim, register, min, max, name);
                    if (rounded is null) return fail(code, problem!);
                    if (shell is not null && onEdge.Select(rounded.Contacts).Any(c => CutsShell(shell, Middle(c, size))))
                        return fail(Diagnostic.FilletInvalid, Invariant($"radius {size} cuts through the shell wall"));
                    contacts = onEdge.Select(rounded.Contacts).Select(c => (c.OnA, c.OnB)).ToList();
                    away = new NegateField(rounded);
                }
                else
                {
                    if (!Flat(cap.Surface, side.Surface) || EdgeSamples(cap.Surface, side.Surface, min, max) is not { } samples)
                        return fail(Diagnostic.UnsupportedChamfer, $"no exact chamfer for {cap.Surface.Kind}–{side.Surface.Kind} in this orientation");
                    var onEdge = samples.Where(OnRim).ToList();
                    if (onEdge.Count == 0) return fail(Diagnostic.ChamferInvalid, $"edge {name} does not exist on the part");
                    contacts = onEdge.Select(q => Setback(cap, side, q, size)).ToList();
                    away = new NegateField(ChamferLeaf(id, cap, side, onEdge[0], size, x => register(x with { Source = faceName })));
                }
                if (contacts.Any(c => !OnPart(c.OnA) || !OnPart(c.OnB)))
                    return fail(round ? Diagnostic.FilletInvalid : Diagnostic.ChamferInvalid,
                        Invariant($"{(round ? "radius" : "distance")} {size} does not fit between the faces next to the edge"));

                // Each face's normal line at its contact, in the meridian through them, keeping the other contact; and
                // both faces grown by the size, so W reaches past them.
                var axis = SurfaceIntersection.CommonAxis(cap.Surface, side.Surface)!.Value;
                var (onA, onB) = contacts[0];
                var radial = (onB - axis.Point - axis.Direction * axis.Direction.Dot(onB - axis.Point)).Normalized();
                var frame = new Frame(axis.Point, radial, axis.Direction, radial.Cross(axis.Direction));
                LeafField Normal(LeafField face, Point3D at, Point3D keep)
                {
                    var (b, a, n) = (frame.ToLocal(at), frame.ToLocal(keep), frame.ToLocal(face.Sample(at).Gradient));
                    // What may go is on the left of travel (a profile's inside).
                    var ray = n.X * (a.Y - b.Y) - n.Y * (a.X - b.X) > 0
                        ? new ProfileEdge2D(b.X, b.Y, b.X + n.X, b.Y + n.Y, 0)
                        : new ProfileEdge2D(b.X + n.X, b.Y + n.Y, b.X, b.Y, 0);
                    return ProfileField.EdgeLeaf(ray, frame, true, (k, o, d, r, m, angle) => register(new Surface(0, $"{id}/bound", k, o, d, r, m, angle)))!;
                }
                LeafField Grown(LeafField l) => new(register(l.Surface.Offset(0, $"{id}/bound", l.Complement ? -size : size)), l.Complement);
                var corner = new MaxField([Normal(cap, onA, onB), Normal(side, onB, onA), Grown(cap), Grown(side), away]);
                return Replace(tree, host, new MaxField([.. host.Children, new NegateField(corner)]));
            }
        }
        return null;
    }

    /// <summary>
    /// The corner material W along the run of tangent edges through edge <paramref name="i"/> of a profile (its
    /// complement when <paramref name="cut"/>), and the run's sides as the part has them. Null where the run ends at a
    /// corner that W cannot end at exactly.
    /// </summary>
    /// <remarks>
    /// Each piece is the corner of the cap and one side, LoopEdgeField(cap, side) taken away, kept to its edge by
    /// planes across it. Two pieces meet at a tangent junction, where the bigger solid's piece (the flatter side) runs
    /// a little past the junction: it takes nothing more there, as rounding a smaller solid keeps less, and a shared
    /// plane would leave a sheet of zero inside. At an end of the run the neighbour's face bounds the piece, or its
    /// complement at a reflex corner, which is right while that corner turns by at most a quarter (past it the piece
    /// would cut into the material round the corner). An arc there also needs its own end plane, against the far side
    /// of its circle; that holds for an arc bulging out of the part, at a convex corner turning by at least a quarter
    /// or a reflex one by at most a quarter.
    /// </remarks>
    static (Field Field, List<LeafField> Sides)? SweptEdgeField(string id, ProfileField p, int i, bool cut, LeafField cap, double size, bool round,
        Func<Surface, Surface> register, string faceName)
    {
        static LeafField Flip(LeafField l) => new(l.Surface, !l.Complement);
        var n = p.Edges.Count;
        LeafField Leaf(int k) => p.EdgeLeaves[k]!;
        LeafField Part(int k) => cut ? Flip(Leaf(k)) : Leaf(k);
        Surface Face(int k) => register(new Surface(0, k == i ? faceName : $"{id}/{Leaf(k).Surface.Source}", SurfaceKind.Fillet, new Point3D(0, 0, 0), new Vector3D(0, 0, 1), size));
        LoopEdgeField Loop(Field side, Dictionary<int, Surface> faces) => new(cap, side, size, round, faces);

        if (Enumerable.Range(0, n).All(k => p.Turn(k) == 0))
        {
            var whole = cut ? (Field)new NegateField(p) : p;
            var loop = Loop(whole, Enumerable.Range(0, n).ToDictionary(k => Leaf(k).Surface.Id, Face));
            return (new MaxField([cap, whole, new NegateField(loop)]), [.. Enumerable.Range(0, n).Select(Part)]);
        }

        var run = new List<int> { i };
        while (p.Turn((run[0] - 1 + n) % n) == 0) run.Insert(0, (run[0] - 1 + n) % n);
        while (p.Turn(run[^1]) == 0) run.Add((run[^1] + 1) % n);

        Point3D At(double u, double v) => p.Frame.ToWorld(new Point3D(u, v, 0));
        Vector3D Along((double U, double V) t) => p.Frame.ToWorld(new Vector3D(t.U, t.V, 0));
        LeafField Across(Point3D at, Vector3D normal, string name) => new(register(new Surface(0, $"{id}/{name}", SurfaceKind.Plane, at, normal)));
        double Length(ProfileEdge2D e) => e.IsArc ? e.R * Math.Abs(e.Sweep) : Math.Sqrt((e.U1 - e.U0) * (e.U1 - e.U0) + (e.V1 - e.V0) * (e.V1 - e.V0));
        // Signed curvature of the part's side: an arc bulging out of the part curves its solid in.
        double Curvature(int k) => p.Edges[k].IsArc ? (Part(k).Complement ? -1 : 1) / p.Edges[k].R : 0;
        double TurnAngle(int k)
        {
            var (a, b) = (p.Edges[k].EndTangent, p.Edges[(k + 1) % n].StartTangent);
            return Math.Abs(Math.Atan2(a.U * b.V - a.V * b.U, a.U * b.U + a.V * b.V));
        }

        // The planes across each piece at its two ends, and the corner clips at the ends of the run.
        var starts = new LeafField?[run.Count];
        var ends = new LeafField?[run.Count];
        var clips = run.Select(_ => new List<Field>()).ToList();
        for (var j = 0; j < run.Count; j++)
        {
            var (k, e) = (run[j], p.Edges[run[j]]);
            if (j < run.Count - 1)
            {
                var next = run[j + 1];
                var (at, t) = (At(e.U1, e.V1), Along(e.EndTangent));
                var reach = Math.Min(size, Math.Min(Length(e), Length(p.Edges[next])) / 4);
                var (pastEnd, pastStart) = Curvature(k) <= Curvature(next) ? (reach, 0.0) : (0.0, reach);
                ends[j] = Across(at + t * pastEnd, t, $"junction:{Leaf(k).Surface.Source}+{Leaf(next).Surface.Source}");
                starts[j + 1] = Across(at - t * pastStart, -t, $"junction:{Leaf(next).Surface.Source}+{Leaf(k).Surface.Source}");
            }
        }
        foreach (var (j, corner, neighbour) in new[] { (0, (run[0] - 1 + n) % n, (run[0] - 1 + n) % n), (run.Count - 1, run[^1], (run[^1] + 1) % n) })
        {
            var (k, e) = (run[j], p.Edges[run[j]]);
            var convex = (cut ? -1 : 1) * p.Turn(corner) > 0;
            var angle = TurnAngle(corner);
            if (!convex && angle > Math.PI / 2 + 1e-9) return null;
            clips[j].Add(p.Turn(corner) > 0 ? Leaf(neighbour) : Flip(Leaf(neighbour)));
            if (!e.IsArc) continue;
            if (Part(k).Complement || (convex ? angle < Math.PI / 2 - 1e-9 : angle > Math.PI / 2 + 1e-9)) return null;
            if (j == 0) starts[0] = Across(At(e.U0, e.V0), -Along(e.StartTangent), $"end:{Leaf(k).Surface.Source}");
            if (j == run.Count - 1) ends[j] = Across(At(e.U1, e.V1), Along(e.EndTangent), $"end:{Leaf(k).Surface.Source}");
        }

        var pieces = new List<Field>();
        for (var j = 0; j < run.Count; j++)
        {
            var k = run[j];
            var side = Part(k);
            List<Field> children = [cap, side, new NegateField(Loop(side, new() { [Leaf(k).Surface.Id] = Face(k) })), .. clips[j]];
            // An arc's two end planes keep the sector of its circle: both for up to a half turn, either beyond.
            if (p.Edges[k].IsArc && Math.Abs(p.Edges[k].Sweep) > Math.PI && starts[j] is { } a && ends[j] is { } b) children.Add(new MinField([a, b]));
            else children.AddRange(new[] { starts[j], ends[j] }.OfType<LeafField>());
            pieces.Add(new MaxField(children));
        }
        return (pieces.Count == 1 ? pieces[0] : new MinField(pieces), [.. run.Select(Part)]);
    }

    /// <summary>The swept profiles an intersection holds: as a child (under loops run round it), or as the tool of a cut.</summary>
    static IEnumerable<(ProfileField P, bool Cut)> SweptIn(MaxField host)
    {
        static ProfileField? Under(Field f) => f switch { ProfileField p => p, LoopEdgeField l => Under(l.Side), _ => null };
        foreach (var c in host.Children)
        {
            if (Under(c) is { } p) yield return (p, false);
            if (c is MinField union)
                foreach (var n in union.Children.OfType<NegateField>())
                    if (Under(n.Child) is { } tool) yield return (tool, true);
        }
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
                    return (onEdge, Invariant($"{(round ? "radius" : "distance")} {size} does not fit round {face}"));
                if (shell is not null && CutsShell(shell, MidEdge(q, ga, gb, size, round)))
                    return (onEdge, Invariant($"{(round ? "radius" : "distance")} {size} cuts through the shell wall"));
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
                                return Fail(invalid, Invariant($"radius {size} does not fit between {face} and {l.Surface.Source}"));
                            if (found.Shell is { } shell && onEdge.Select(rounded.Contacts).Any(x => CutsShell(shell, Middle(x, size))))
                                return Fail(invalid, Invariant($"radius {size} cuts through the shell wall"));
                            children[^1] = rounded;
                        }
                        else
                        {
                            if (onEdge.Select(q => Setback(cap, l, q, size)).Any(x => tree.Value(x.OnA) > 10 * _tol || tree.Value(x.OnB) > 10 * _tol))
                                return Fail(invalid, Invariant($"distance {size} does not fit between {face} and {l.Surface.Source}"));
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
        // A full revolve is its profile alone: an intersection of one.
        foreach (var (host, _) in Intersections(solid is ProfileField ? new MaxField([solid]) : solid, null))
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

    /// <summary>
    /// Points on the sharp edge between two leaf surfaces: a line between planes, the circles where two surfaces of
    /// revolution round one axis meet. Null when the pair has no closed-form edge.
    /// </summary>
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
        if (plane.Kind != SurfaceKind.Plane || cyl.Kind != SurfaceKind.Cylinder) return CoaxialSamples(a, b);
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

    static List<Point3D>? CoaxialSamples(Surface a, Surface b)
    {
        if (!a.Analytic || !b.Analytic || SurfaceIntersection.CommonAxis(a, b) is null) return null;
        const int n = 720;
        return SurfaceIntersection.Circles(a, b).SelectMany(c => Enumerable.Range(0, n).Select(i => c.At(2 * Math.PI * i / n))).ToList();
    }

    /// <summary>Two planes, or a plane across a cylinder: the pairs a chamfer face is known for.</summary>
    static bool Flat(Surface a, Surface b) =>
        a.Kind == SurfaceKind.Plane && b.Kind == SurfaceKind.Plane
        || a.Kind == SurfaceKind.Plane && b.Kind == SurfaceKind.Cylinder && a.Axis.Cross(b.Axis).Length <= 1e-9
        || b.Kind == SurfaceKind.Plane && a.Kind == SurfaceKind.Cylinder && b.Axis.Cross(a.Axis).Length <= 1e-9;

    /// <summary>
    /// The rolling-ball field on the sharp edge between two leaves (<see cref="RoundedEdgeField.Of"/>) with the edge's
    /// points on the part; or the diagnostic code and message saying why there is none.
    /// </summary>
    static (RoundedEdgeField? Field, List<Point3D> OnEdge, string Code, string? Problem) Rolling(LeafField a, LeafField b, double r, Surface face,
        Func<Point3D, bool> onPart, Func<Surface, Surface> register, Point3D min, Point3D max, string name)
    {
        var (sa, sb) = (a.Surface, b.Surface);
        if (EdgeSamples(sa, sb, min, max) is not { } samples)
            return (null, [], Diagnostic.UnsupportedFillet, $"no exact fillet for {sa.Kind}–{sb.Kind} in this orientation");
        var onEdge = samples.Where(onPart).ToList();
        if (onEdge.Count == 0) return (null, onEdge, Diagnostic.FilletInvalid, $"edge {name} does not exist on the part");
        foreach (var l in new[] { a, b })
        {
            if (l.Surface.Kind == SurfaceKind.Torus && l.Complement)
                return (null, onEdge, Diagnostic.UnsupportedFillet, $"no exact fillet next to the hollow torus face {l.Surface.Source}");
            if (!l.Complement && (l.Surface.Kind == SurfaceKind.Torus ? l.Surface.Minor : l.Surface.Kind == SurfaceKind.Sphere ? l.Surface.Radius : double.PositiveInfinity) <= r)
                return (null, onEdge, Diagnostic.FilletInvalid, Invariant($"radius {r} does not fit in the curve of {l.Surface.Source}"));
        }
        // Two surfaces of revolution can meet in two circles; one fillet rounds one edge.
        var circles = SurfaceIntersection.Circles(sa, sb).ToList();
        int Nearest(Point3D q) => circles.Count < 2 ? 0 : circles.IndexOf(circles.MinBy(c => (q - c.At(c.Parameter(q))).Length)!);
        if (onEdge.Select(Nearest).Distinct().Count() > 1)
            return (null, onEdge, Diagnostic.UnsupportedFillet, $"edge {name} is two circles on the part; a fillet rounds one edge");
        if (RoundedEdgeField.Of(a, b, r, face, onEdge[0]) is not { } rounded)
            return (null, onEdge, Diagnostic.UnsupportedFillet, $"no exact fillet for {sa.Kind}–{sb.Kind} in this orientation");
        return (rounded.With(a, b, r, register(face)), onEdge, "", null);
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
