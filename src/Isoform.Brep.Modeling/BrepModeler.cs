using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Modeling;
using Isoform.Sketching;

namespace Isoform.Brep.Modeling;

/// <summary>
/// Isoform documents on the managed B-rep kernel: blocks, plain extrudes (an inline profile, or the regions of a
/// sketch on a plane or on a flat face of the part), plain revolves (round the sketch's y axis or a line of it; edges
/// on the axis sweep nothing), sweeps along lines and arcs (sides <c>id/path:profile</c>), holes (through, or blind
/// with a floor; along Z, into a face, or at a sketch's points), plane cuts (face <c>id/face</c>), patterns of those
/// (linear, circular or mirrored; copy k of face s is <c>id/k:s</c>), bodies joined or cut into others, fillets and
/// chamfers on the edge between two flat faces or round a cylinder square to a flat
/// face (face <c>id/face</c>, a cone for a chamfer there; a ball <c>id/corner:a+b+c</c> where three fillets of one
/// radius meet at a square corner), and offsets of one body (the history rebuilt with every
/// face moved; round corners <c>id/corner:a+b</c>) and closed shells of one (inner faces <c>id/inner:face</c>). Each
/// starts a body, joins it or cuts it, working on the
/// current body as Isoform's <see cref="Feature.Body"/> says. Faces carry Isoform's names: sides after their profile
/// edge, caps <c>id/start</c> and <c>id/end</c>, blocks <c>id/x-</c> … <c>id/z+</c>, holes <c>id/wall</c> and
/// <c>id/floor</c>, faces merged on one surface named by all their sources. Anything else is refused with
/// <see cref="Diagnostic.BackendFailed"/>.
/// </summary>
public sealed class BrepModeler(ModelingSettings settings) : IModeler
{
    ModelResult IModeler.Regenerate(PartDocument document) => Regenerate(document);

    /// <summary>A sketch solved where it stands in the history, on its plane.</summary>
    sealed record Placed(Sketch Sketch, SketchPlane Plane, SketchSolution Solution);

    public BrepResult Regenerate(PartDocument document)
    {
        var (_, solids, _, problem) = Run(document, head: false);
        if (problem is not null) return Failed(problem);
        var bodies = solids.Select(s => s.Solid).ToList();
        return new BrepResult([], TopologyOf(bodies), bodies);
    }

    /// <summary>
    /// How far the kernel gets: the features before the first it cannot build, the solids they make (each with the
    /// name of its body) and the body of each of those features.
    /// </summary>
    public (int Count, IReadOnlyList<(NamedBody Solid, string Body)> Solids, IReadOnlyDictionary<string, string> BodyOf) Head(PartDocument document)
    {
        var (count, solids, bodyOf, _) = Run(document, head: true);
        return (count, solids, bodyOf);
    }

    // The features in turn; at the first that fails, the problem, and for a head the solids as they were before it.
    (int Count, List<(NamedBody Solid, string Body)> Solids, Dictionary<string, string> BodyOf, Diagnostic? Problem) Run(PartDocument document, bool head)
    {
        if (DocumentRules.FirstProblem(document, settings.Tolerance) is { } invalid) return (0, [], [], invalid);
        document = Parametric.Resolve(document);

        var solids = new List<(NamedBody Solid, string Body)>();
        var sketches = new Dictionary<string, Placed>();
        var bodyOf = new Dictionary<string, string>();
        string? current = null;
        // An offset rebuilds what came before it with every face moved (see Grown): by how much for each feature, the
        // offset whose round corners it gets, and what stays as it was first built (sketch planes, hole faces, which
        // way each fillet turns).
        var features = document.Features;
        var grow = new double[features.Count];
        var owner = new string?[features.Count];
        var facePlanes = new Dictionary<string, SketchPlane>();
        var convex = new Dictionary<string, bool>();
        // While a shell's cavity is built: every face moved in by the wall, and the shell when it asks for round corners.
        var hollow = 0.0;
        Isoform.Document.Shell? rounding = null;
        for (var i = 0; i < features.Count; i++)
        {
            var kept = head ? (Solids: solids.ToList(), BodyOf: new Dictionary<string, string>(bodyOf)) : default;
            if ((features[i] is Offset offset ? Grown(offset, i) : Build(i)) is { } problem)
                return head ? (i, kept.Solids, kept.BodyOf, problem) : (i, solids, bodyOf, problem);
        }
        return (features.Count, solids, bodyOf, null);

        // Feature i on the solids so far.
        Diagnostic? Build(int i)
        {
            var feature = features[i];
            if (feature is Sketch sketch) return sketches.ContainsKey(sketch.Id) ? null : Prepare(sketch);
            if (DocumentRules.StartsBody(feature)) current = feature.Body ?? feature.Id;
            else if (feature.Body is { } named) current = named;
            bodyOf[feature.Id] = current!;
            if (feature is Fillet or Chamfer) return Blend(feature, solids, grow[i] + hollow, convex);
            if (feature is Isoform.Document.Shell shell) return Hollowed(shell, i);
            if (feature is Pattern pattern) return Copies(pattern, i);
            if (feature is CombineBodies combine) return Combined(combine);
            var (tools, op, failed) = Tools(feature, current!, Motion.None, s => s, i);
            return failed ?? Apply(solids, current!, tools!, op, feature.Id);
        }

        // Every face so far moved by the offset's distance along its outward normal: the history rebuilt with each
        // feature's tool grown by it (a cut's shrunk), round corners where its profiles' convex corners part, fillets
        // rounder, as the F-rep core moves the faces of its tree. Sketches stay where they were placed.
        Diagnostic? Grown(Offset o, int at)
        {
            if (features.Take(at).Any(f => f is CombineBodies || f.Body is { } b && b != current) || solids.Any(s => s.Body != current))
                return Unsupported(o.Id, $"{o.Id}: an offset of a part with more than one body is not followed by the managed B-rep kernel yet");
            if (features.Take(at).Any(f => f is Isoform.Document.Shell))
                return Unsupported(o.Id, $"{o.Id}: an offset of a shelled part is not followed by the managed B-rep kernel yet");
            for (var j = 0; j < at; j++)
            {
                grow[j] += o.Distance;
                owner[j] ??= o.Id;
            }
            solids.Clear();
            bodyOf.Clear();
            current = null;
            for (var j = 0; j < at; j++)
                if (features[j] is not Offset && Build(j) is { } problem)
                    return problem with { FeatureId = o.Id, Message = $"{o.Id}: {problem.Message}" };
            return null;
        }

        // A closed skin: the solid less its cavity, which is the history before the shell rebuilt with every face moved
        // in by the wall (cut tools grown by it) and corners met sharp, so that each inner face is an outer face moved,
        // id/inner:face, as the F-rep core names them. Asked for round, the cavity's corners turn round where its faces
        // part (opposite an inside corner, round a grown cut's corner), faces id/corner:a+b, and the wall is the
        // thickness everywhere.
        Diagnostic? Hollowed(Isoform.Document.Shell sh, int at)
        {
            if (features.Take(at).Any(f => f is CombineBodies || f.Body is { } b && b != current) || solids.Any(s => s.Body != current))
                return Unsupported(sh.Id, $"{sh.Id}: a shell of a part with more than one body is not followed by the managed B-rep kernel yet");
            if (features.Take(at).Any(f => f is Offset or Isoform.Document.Shell))
                return Unsupported(sh.Id, $"{sh.Id}: a shell after an offset or another shell is not followed by the managed B-rep kernel yet");
            var (kept, keptBodies, keptCurrent) = (solids.ToList(), bodyOf.ToList(), current);
            solids.Clear();
            bodyOf.Clear();
            current = null;
            (hollow, rounding) = (-sh.Thickness, sh.Round ? sh : null);
            Diagnostic? problem = null;
            for (var j = 0; j < at && problem is null; j++) problem = Build(j);
            (hollow, rounding) = (0, null);
            var cavity = solids.Select(c => new NamedBody(c.Solid.Body, c.Solid.Names.ToDictionary(n => n.Key,
                n => (IReadOnlyList<string>)[.. n.Value.Select(name => name.StartsWith($"{sh.Id}/corner:", StringComparison.Ordinal) ? name : $"{sh.Id}/inner:{name}")]))).ToList();
            solids.Clear();
            solids.AddRange(kept);
            bodyOf.Clear();
            foreach (var (feature, body) in keptBodies) bodyOf[feature] = body;
            current = keptCurrent;
            if (problem is not null)
                return problem with { FeatureId = sh.Id, Message = $"{sh.Id}: its cavity cannot be built: {problem.Message}" };
            return Apply(solids, current!, cavity, BooleanOp.Cut, sh.Id);
        }

        // A solid-making feature's tools, placed by a motion and named by `name`, against the solids of the body it
        // works on, grown as the offsets after feature `at` (itself, or the pattern copying it) move its faces.
        (List<NamedBody>? Tools, BooleanOp Op, Diagnostic? Problem) Tools(Feature feature, string body, Motion motion, Func<string, string> name, int at)
        {
            List<NamedBody> Part() => [.. solids.Where(s => s.Body == body).Select(s => s.Solid)];
            var g = hollow == 0 ? new Growth(grow[at], owner[at], name) : new Growth(hollow, rounding?.Id, name, Round: rounding is not null);
            var op = feature switch { Block b => b.Op, Extrude e => e.Op, Revolve r => r.Op, Sweep w => w.Op, _ => BooleanOp.Cut };
            // A cut's tool shrinks as the part grows.
            if (op == BooleanOp.Cut) g = g with { By = -g.By };
            return feature switch
            {
                Block { Blend: 0 } b => Guard(b.Id, () => [BlockTool(b, motion, g)], b.Op),
                Extrude { Blend: 0 } e => Extruded(e, sketches, motion, g) is var (t, p) ? (t, e.Op, p) : default,
                Revolve { Blend: 0 } r => Revolved(r, sketches, motion, g) is var (t, p) ? (t, r.Op, p) : default,
                Sweep w => SweepTools(w, sketches, motion, g) is var (t, p) ? (t, w.Op, p) : default,
                Hole h => HoleTools(h, sketches, Part(), motion, g, facePlanes) is var (t, p) ? (t, BooleanOp.Cut, p) : default,
                PlaneCut c => Guard(c.Id, () => [PlaneTool(c, Part(), motion, g)], BooleanOp.Cut),
                _ => (null, BooleanOp.New, Unsupported(feature.Id, $"{feature.GetType().Name} is not supported by the managed B-rep kernel yet")),
            };
        }

        // A pattern's copies, each in the body of the feature it copies (joined to it where the original started it),
        // copy k of face s named id/k:s. Copies of cuts only are cut out together; others go copy by copy, feature by
        // feature, as the F-rep core applies them. Copies are made where the pattern is, and grow with what follows it.
        Diagnostic? Copies(Pattern p, int at)
        {
            var count = p.Motion is MirrorMotion ? 1 : p.Motion switch { LinearMotion l => l.Count, CircularMotion c => c.Count, _ => 1 } - 1;
            var copies = (from k in Enumerable.Range(1, count) from id in p.Features select (K: k, Source: document[id])).ToList();
            var cutsOnly = copies.All(c => c.Source is Hole or PlaneCut or Block { Op: BooleanOp.Cut } or Extrude { Op: BooleanOp.Cut } or Revolve { Op: BooleanOp.Cut } or Sweep { Op: BooleanOp.Cut })
                           && copies.Select(c => bodyOf[c.Source.Id]).Distinct().Count() == 1;
            var batch = new List<NamedBody>();
            foreach (var (k, source) in copies)
            {
                var body = bodyOf[source.Id];
                var (tools, op, failed) = Tools(source, body, Motion.Of(p.Motion, k), name => $"{p.Id}/{k}:{name}", at);
                if (failed is not null) return failed with { FeatureId = p.Id };
                if (cutsOnly) batch.AddRange(tools!);
                else if (Apply(solids, body, tools!, op == BooleanOp.New ? BooleanOp.Join : op, p.Id) is { } problem) return problem;
            }
            return cutsOnly && batch.Count > 0 ? Apply(solids, bodyOf[copies[0].Source.Id], batch, BooleanOp.Cut, p.Id) : null;
        }

        // Another body joined into the current one or cut out of it, used up unless kept (its features then belong
        // to the current body); its faces keep their names.
        Diagnostic? Combined(CombineBodies c)
        {
            var tools = solids.Where(s => s.Body == c.Tool).Select(s => s.Solid).ToList();
            if (!c.Keep)
            {
                solids.RemoveAll(s => s.Body == c.Tool);
                foreach (var id in bodyOf.Where(kv => kv.Value == c.Tool).Select(kv => kv.Key).ToList()) bodyOf[id] = current!;
            }
            return Apply(solids, current!, tools, c.Op, c.Id);
        }

        Diagnostic? Prepare(Sketch s)
        {
            if (s.Entities.FirstOrDefault(e => e.Project is not null) is { } projected)
                return Unsupported(s.Id, $"sketch {s.Id}: {projected.Id} projects a part edge, which the managed B-rep kernel does not follow yet");
            var plane = s.On.Plane ?? FacePlane(s.On.Face!, solids.Select(p => p.Solid));
            if (plane is null) return new Diagnostic(Diagnostic.SketchPlaneLost, s.Id, $"sketch {s.Id} lies on {s.On.Face}, which is not a flat face of the part here");
            var solution = SketchSolver.Solve(s);
            if (!solution.Solved) return new Diagnostic(SketchSolution.Invalid, s.Id, $"sketch {s.Id} does not solve");
            sketches[s.Id] = new Placed(s, plane, solution);
            return null;
        }
    }

    static BrepResult Failed(Diagnostic d) => new([d], null, []);

    static Diagnostic Unsupported(string? feature, string message) => new(Diagnostic.BackendFailed, feature, message);

    static (List<NamedBody>?, BooleanOp, Diagnostic?) Guard(string id, Func<List<NamedBody>> tools, BooleanOp op)
    {
        try
        {
            return (tools(), op, null);
        }
        catch (BooleanFailure failure)
        {
            return (null, op, Unsupported(id, $"{id}: {failure.Message}"));
        }
    }

    /// <summary>
    /// The plane of a flat face as Isoform's sketches and holes take it: the outward normal, the world origin put onto
    /// the plane as origin, the world x put onto it as x (world y where x runs along the normal). Null when no flat
    /// face of the part has that name.
    /// </summary>
    static SketchPlane? FacePlane(string name, IEnumerable<NamedBody> part)
    {
        foreach (var (body, names) in part)
        for (var f = 0; f < body.Faces.Length; f++)
        {
            if (body.Faces[f].Surface is not Plane plane || !names[new FaceId(f)].Contains(name)) continue;
            var n = plane.Frame.ZAxis * (body.Faces[f].SameSense ? 1 : -1);
            var o = n * n.Dot(plane.Frame.Origin.AsVector());
            var x = new Vector3D(1, 0, 0) - n * n.X;
            if (x.Length < 0.1) x = new Vector3D(0, 1, 0) - n * n.Y;
            x = x.Normalized();
            return new SketchPlane([o.X, o.Y, o.Z], [x.X, x.Y, x.Z], [n.X, n.Y, n.Z]);
        }
        return null;
    }

    static RigidTransform3D FrameOf(SketchPlane plane) => RigidTransform3D.FromFrame(P(plane.Origin), V(plane.Normal), V(plane.X));

    /// <summary>
    /// How a feature's tool is made: grown by <see cref="By"/> (the offsets after it, turned round for a cut's tool),
    /// its round corners named after <see cref="Owner"/>, the offset that first grew it, and its faces named by
    /// <see cref="Name"/> (a pattern's copy names them id/k:face). Without <see cref="Round"/> (a shell's cavity)
    /// corners always meet sharp.
    /// </summary>
    sealed record Growth(double By, string? Owner, Func<string, string> Name, bool Round = true)
    {
        // The round corner between two faces of a profile, named as they are.
        public string Corner(string a, string b) => $"{Owner}/corner:{a}+{b}";
    }

    // A block: its rectangle swept up Z, sides named by the side of the box they face. Grown, it stays a box.
    NamedBody BlockTool(Block b, Motion motion, Growth g)
    {
        var (x0, y0, z0) = (b.Origin[0] - g.By, b.Origin[1] - g.By, b.Origin[2] - g.By);
        var (x1, y1, z1) = (b.Origin[0] + b.Size[0] + g.By, b.Origin[1] + b.Size[1] + g.By, b.Origin[2] + b.Size[2] + g.By);
        if (x1 - x0 <= settings.Tolerance || y1 - y0 <= settings.Tolerance || z1 - z0 <= settings.Tolerance)
            throw new BooleanFailure($"{b.Id} shrinks to nothing");
        Point2D Q(double x, double y) => new(x, y);
        var loop = new ProfileLoop([new LineSegment(Q(x0, y0), Q(x1, y0)), new LineSegment(Q(x1, y0), Q(x1, y1)),
            new LineSegment(Q(x1, y1), Q(x0, y1)), new LineSegment(Q(x0, y1), Q(x0, y0))]);
        var (frame, region) = motion.Place(RigidTransform3D.Identity, new Region(loop));
        string N(string side) => g.Name($"{b.Id}/{side}");
        return Swept(b.Id, region, frame, z0, z1, [[N("y-"), N("x+"), N("y+"), N("x-")]], N("z-"), N("z+"));
    }

    // A region swept along the frame's Z, its faces named: sides by `sides`, caps `start` and `end`.
    NamedBody Swept(string id, Region region, RigidTransform3D frame, double from, double to, List<List<string>> sides, string start, string end)
    {
        var (result, why) = Extrusion.Extrude(region, frame, from, to, settings.Tolerance);
        if (result is null) throw new BooleanFailure($"{id} cannot be swept: {why}");
        var names = new Dictionary<FaceId, IReadOnlyList<string>> { [result.StartCap] = [start], [result.EndCap] = [end] };
        for (var l = 0; l < sides.Count; l++)
        for (var s = 0; s < sides[l].Count; s++)
            names[result.Sides[l][s]] = [sides[l][s]];
        return new NamedBody(result.Body, names);
    }

    // A region's loops and their segments' faces, id/<entity>, grown: each loop moved off its material (with the
    // F-rep core's ProfileField), a round corner where two faces part, named after both. On a revolve's axis
    // (x = 0) a segment stays and a corner stays sharp.
    (List<ProfileLoop> Loops, List<List<string>> Names) Loops(string id, SketchRegion region, Growth g, bool revolve)
    {
        var (loops, entities) = ProfileConversion.Loops(region, settings.Tolerance);
        var names = entities.Select(l => l.Select(e => g.Name($"{id}/{e}")).ToList()).ToList();
        if (g.By == 0) return (loops, names);
        bool OnAxis(Point2D p) => revolve && Math.Abs(p.X) <= settings.Tolerance;
        for (var l = 0; l < loops.Count; l++)
        {
            // Material on the left of an outer loop counter-clockwise and of a hole clockwise: grown, it moves right.
            var right = loops[l].SignedArea > 0 == (l == 0) ? g.By : -g.By;
            var by = loops[l].Segments.Select(s => s is LineSegment && OnAxis(s.Start) && OnAxis(s.End) ? 0 : right).ToList();
            var (grown, origins, problem) = ProfileOffset.Grow(loops[l], by, p => !g.Round || OnAxis(p), settings.Tolerance);
            if (problem is not null) throw new BooleanFailure($"{id} cannot grow by {g.By}: {problem}");
            var n = names[l].Count;
            names[l] = [.. origins.Select(o => o >= 0 ? names[l][o] : g.Corner(names[l][~o], names[l][(~o + 1) % n]))];
            loops[l] = grown!;
        }
        var why = new Region(loops[0], loops.Skip(1).ToList()).Problem(settings.Tolerance);
        if (why is not null) throw new BooleanFailure($"{id} cannot grow by {g.By}: {why}");
        return (loops, names);
    }

    // The extrude's tool bodies, one per region, named. Grown, its caps move out too.
    (List<NamedBody>? Tools, Diagnostic? Problem) Extruded(Extrude e, Dictionary<string, Placed> sketches, Motion motion, Growth g)
    {
        var (plane, regions, problem) = Shape(e, sketches);
        if (problem is not null) return (null, new Diagnostic(SketchSolution.Invalid, e.Id, $"{e.Id}: {problem}"));
        var frame = FrameOf(plane!);
        var outward = e.End >= e.Start ? g.By : -g.By;
        var (tools, _, failed) = Guard(e.Id, () => [.. regions!.Select(region =>
        {
            var (loops, names) = Loops(e.Id, region, g, revolve: false);
            var (placed, moved) = motion.Place(frame, new Region(loops[0], loops.Skip(1).ToList()));
            return Swept(e.Id, moved, placed, e.Start - outward, e.End + outward, names, g.Name($"{e.Id}/start"), g.Name($"{e.Id}/end"));
        })], e.Op);
        return (tools, failed);
    }

    // The revolve's tool bodies, one per region turned round the sketch's y axis (or the line it names), faces named
    // after their profile edges and, below a whole turn, the ends id/start and id/end.
    (List<NamedBody>? Tools, Diagnostic? Problem) Revolved(Revolve r, Dictionary<string, Placed> sketches, Motion motion, Growth g)
    {
        var (plane, regions, problem) = Shape(r, sketches);
        if (problem is not null) return (null, new Diagnostic(SketchSolution.Invalid, r.Id, $"{r.Id}: {problem}"));
        if (g.By != 0 && r.Angle < 360)
            return (null, Unsupported(r.Id, $"{r.Id}: an offset of a part turn moves its ends off the axis, which the managed B-rep kernel does not follow yet"));
        var (tools, _, failed) = Guard(r.Id, () => [.. regions!.Select(region =>
        {
            var (loops, names) = Loops(r.Id, region, g, revolve: true);
            var (sketch, moved) = motion.Place(FrameOf(plane!), new Region(loops[0], loops.Skip(1).ToList()));
            // The kernel turns (x, z) round its frame's Z from its X: the sketch's x round its y, towards −normal.
            var frame = RigidTransform3D.FromFrame(sketch.Origin, sketch.YAxis, sketch.XAxis);
            return Turned(r.Id, [.. moved.Loops], names, frame, Math.Min(r.Angle, 360) * Math.PI / 180, g.Name($"{r.Id}/start"), g.Name($"{r.Id}/end"));
        })], r.Op);
        return (tools, failed);
    }

    // Loops turned round the frame's Z (see Revolution), faces named after their segments and a part turn's ends
    // `start` and `end`.
    NamedBody Turned(string id, List<ProfileLoop> loops, List<List<string>> names, RigidTransform3D frame, double angle, string start, string end)
    {
        // Outer loop counter-clockwise, holes clockwise: the kernel would turn them so, and number their faces so.
        for (var l = 0; l < loops.Count; l++)
            if (loops[l].SignedArea > 0 != (l == 0)) (loops[l], names[l]) = (loops[l].Reversed(), [.. Enumerable.Reverse(names[l])]);
        var (result, why) = Revolution.Revolve(new Region(loops[0], loops.Skip(1).ToList()), frame, settings.Tolerance, angle);
        if (result is null) throw new BooleanFailure($"{id} cannot be revolved: {why}");
        List<string> source = [.. names.SelectMany(n => n), start, end];
        var faces = Enumerable.Range(0, result.Body.Faces.Length).ToDictionary(f => new FaceId(f),
            f => (IReadOnlyList<string>)[.. result.Origins[f].Select(o => source[o.Face.Index])]);
        return new NamedBody(result.Body, faces);
    }

    // A sweep, one tool per region: the profile extruded along each line of the path and turned round each arc's
    // axis (as Sweeps.Plan places them, with the F-rep core), the pieces joined where they meet across the path. Sides
    // id/<path entity>:<profile entity>, an open path's ends id/start and id/end.
    (List<NamedBody>? Tools, Diagnostic? Problem) SweepTools(Sweep sw, Dictionary<string, Placed> sketches, Motion motion, Growth g)
    {
        if (g.By != 0) return (null, Unsupported(sw.Id, $"{sw.Id}: an offset of a sweep is not followed by the managed B-rep kernel yet"));
        var (plane, regions, problem) = Shape(sw, sketches);
        if (problem is not null) return (null, new Diagnostic(SketchSolution.Invalid, sw.Id, $"{sw.Id}: {problem}"));
        var path = sketches[sw.Path.Sketch];
        var (chain, closed, lost) = SketchRegions.Chain(path.Sketch, path.Solution, sw.Path.Entities, sw.Path.From);
        if (lost is not null) return (null, new Diagnostic(SketchSolution.Invalid, sw.Id, $"{sw.Id} cannot follow its path: {lost}"));
        var (pieces, sections, refused) = Sweeps.Plan(plane!, regions!, path.Plane, chain!, closed, motion.Frame);
        if (refused is not null)
            return (null, new Diagnostic(Diagnostic.InvalidParameter, sw.Id, $"{sw.Id} cannot sweep sketch {sw.Sketch} along {sw.Path.Sketch}: {refused}"));
        var (tools, _, failed) = Guard(sw.Id, () => [.. sections!.Select(section =>
        {
            var parts = pieces!.Select((piece, k) => PieceOf(sw.Id, piece, section, g,
                g.Name($"{sw.Id}/{(k == 0 && !closed ? "start" : $"{piece.Id}:from")}"),
                g.Name($"{sw.Id}/{(k == pieces!.Count - 1 && !closed ? "end" : $"{piece.Id}:to")}"))).ToList();
            // Each piece joined on: they meet in their ends across the path, which the union drops.
            var joined = parts[0];
            foreach (var part in parts.Skip(1))
            {
                var (result, why) = Booleans.Unite(joined.Body, part.Body);
                if (why is not null) throw new BooleanFailure($"{sw.Id}: its pieces do not join: {why}");
                if (result is not [var one]) throw new BooleanFailure($"{sw.Id}: its pieces do not join into one solid");
                joined = Named(one, [joined, part]);
            }
            return joined;
        })], sw.Op);
        return (tools, failed);
    }

    // One piece of a sweep: the section (a left of travel, b along the path plane's normal) extruded along a line, or
    // in (distance from the axis, height) turned round an arc's axis, which mirrors it.
    NamedBody PieceOf(string id, Isoform.Geometry.SweepPiece piece, SketchRegion section, Growth g, string start, string end)
    {
        double[] At(double[] x)
        {
            var (u, v) = piece.Section(x[0], x[1]);
            return [u, v];
        }
        Profile Map(Profile p) => piece.Arc ? new(At(p.Start), [.. p.Edges.Select(e => e with { To = At(e.To), Bulge = -e.Bulge })]) : p;
        var (loops, names) = ProfileConversion.Loops(new SketchRegion(Map(section.Outer), [.. section.Holes.Select(Map)]), settings.Tolerance);
        var sides = names.Select(l => l.Select(n => g.Name($"{id}/{piece.Id}:{n}")).ToList()).ToList();
        var f = piece.Frame;
        return piece.Arc
            ? Turned(id, loops, sides, RigidTransform3D.FromFrame(f.Origin, f.Y, f.X), piece.Angle, start, end)
            : Swept(id, new Region(loops[0], loops.Skip(1).ToList()), RigidTransform3D.FromFrame(f.Origin, f.Z, f.X), 0, piece.Length, sides, start, end);
    }

    // A drill per spot: a cylinder along the outward normal where it goes in, from its floor (or from beyond the
    // part, for through all) out past the part. Grown (by a negative amount as the part grows), its radius and depth
    // change; a hole on a face stays on the plane that face had where it was first drilled.
    (List<NamedBody>? Tools, Diagnostic? Problem) HoleTools(Hole h, Dictionary<string, Placed> sketches, List<NamedBody> body, Motion motion, Growth g,
        Dictionary<string, SketchPlane> facePlanes)
    {
        var radius = h.Diameter / 2 + g.By;
        if (radius <= settings.Tolerance) return (null, Unsupported(h.Id, $"{h.Id} closes up when the part grows by {-g.By}"));
        List<(Point3D At, Vector3D Out, string Spot)> spots;
        if (h.On is null && h.Sketch is null) spots = [(new Point3D(h.Center[0], h.Center[1], 0), new Vector3D(0, 0, 1), "")];
        else if (h.On is { } face)
        {
            if (!facePlanes.TryGetValue(h.Id, out var plane))
            {
                if (FacePlane(face, body) is not { } found)
                    return (null, new Diagnostic(Diagnostic.SketchPlaneLost, h.Id, $"{h.Id} is on {face}, which is not a flat face of the part here"));
                facePlanes[h.Id] = plane = found;
            }
            var frame = FrameOf(plane);
            spots = [(frame.Apply(new Point3D(h.Center[0], h.Center[1], 0)), frame.ZAxis, "")];
        }
        else
        {
            var placed = sketches[h.Sketch!];
            var frame = FrameOf(placed.Plane);
            spots = [.. h.Points!.Select(p => (frame.Apply(new Point3D(placed.Solution.Points[p].X, placed.Solution.Points[p].Y, 0)), frame.ZAxis, p + ":"))];
        }

        spots = [.. spots.Select(s => (motion.Apply(s.At), motion.Apply(s.Out), s.Spot))];
        var corners = body.SelectMany(b => b.Body.Vertices.Select(v => v.Point)).ToList();
        var reach = body.SelectMany(b => b.Body.Faces).Select(f => Reach(f.Surface)).DefaultIfEmpty(0).Max() + h.Diameter + 1;
        var (tools, _, failed) = Guard(h.Id, () => [.. spots.Select(spot =>
        {
            var along = corners.Select(c => (c - spot.At).Dot(spot.Out)).DefaultIfEmpty(0).ToList();
            var x = Math.Abs(spot.Out.X) < 0.9 ? new Vector3D(1, 0, 0) : new Vector3D(0, 1, 0);
            var frame = RigidTransform3D.FromFrame(spot.At, spot.Out, (x - spot.Out * spot.Out.Dot(x)).Normalized());
            var circle = new Region(new ProfileLoop([ArcSegment.Circle(new Point2D(0, 0), radius)]));
            var from = h.Depth is { } depth ? -depth - g.By : along.Min() - reach;
            string N(string face) => g.Name($"{h.Id}/{spot.Spot}{face}");
            return Swept(h.Id, circle, frame, from, along.Max() + reach, [[N("wall")]], N("floor"), N("mouth"));
        })], BooleanOp.Cut);
        return (tools, failed);
    }

    // A plane cut: a box standing on the plane on the side its normal points to, reaching past the body; its base is
    // the cut face, id/face. Grown, the base moves back by the growth.
    NamedBody PlaneTool(PlaneCut c, List<NamedBody> body, Motion motion, Growth g)
    {
        var (at, n) = (motion.Apply(P(c.Point)), motion.Apply(V(c.Normal)).Normalized());
        var reach = body.SelectMany(b => b.Body.Vertices).Select(v => (v.Point - at).Length).DefaultIfEmpty(0).Max()
                    + body.SelectMany(b => b.Body.Faces).Select(f => Reach(f.Surface)).DefaultIfEmpty(0).Max() + 1;
        var x = Math.Abs(n.X) < 0.9 ? new Vector3D(1, 0, 0) : new Vector3D(0, 1, 0);
        var frame = RigidTransform3D.FromFrame(at, n, (x - n * n.Dot(x)).Normalized());
        Point2D Q(double u, double v) => new(u, v);
        var square = new ProfileLoop([new LineSegment(Q(-reach, -reach), Q(reach, -reach)), new LineSegment(Q(reach, -reach), Q(reach, reach)),
            new LineSegment(Q(reach, reach), Q(-reach, reach)), new LineSegment(Q(-reach, reach), Q(-reach, -reach))]);
        string N(string face) => g.Name($"{c.Id}/{face}");
        return Swept(c.Id, new Region(square), frame, -g.By, reach, [[N("beyond-1"), N("beyond-2"), N("beyond-3"), N("beyond-4")]], N("face"), N("beyond"));
    }

    // A fillet or chamfer on the edge between two named faces, or round the loops of one, on the solid that has them
    // (whichever body that is). Grown by an offset, a fillet's radius grows with the faces (shrinks, round a concave
    // edge, as `convex` noted when it was first made); one taken to nothing leaves the edge sharp.
    static Diagnostic? Blend(Feature feature, List<(NamedBody Solid, string Body)> solids, double grow, Dictionary<string, bool> convex)
    {
        if (grow != 0 && feature is Chamfer)
            return Unsupported(feature.Id, $"{feature.Id}: an offset of a chamfer is not followed by the managed B-rep kernel yet");
        if ((feature switch { Fillet f => f.Loop, Chamfer c => c.Loop, _ => null }) is { } loop) return LoopBlend(feature, loop, solids, grow);
        var (edge, size, kind) = feature switch
        {
            Fillet f => (f.Edge, f.Radius + (convex.GetValueOrDefault(f.Id, true) ? grow : -grow), BlendKind.Fillet),
            Chamfer c => (c.Edge, c.Distance, BlendKind.Chamfer),
            _ => throw new NotSupportedException(feature.GetType().Name),
        };
        if (size <= 1e-9) return null;
        var what = kind == BlendKind.Fillet ? "rounded" : "chamfered";
        for (var i = 0; i < solids.Count; i++)
        {
            var (solid, body) = solids[i];
            IEnumerable<FaceId> Called(string name) => Enumerable.Range(0, solid.Body.Faces.Length).Select(f => new FaceId(f)).Where(f => solid.Names[f].Contains(name));
            var pairs = (from a in Called(edge[0]) from b in Called(edge[1]) where Adjacent(solid.Body, a, b) select (a, b)).ToList();
            if (pairs.Count == 0) continue;
            if (pairs.Count > 1) return Unsupported(feature.Id, $"{feature.Id}: {edge[0]} and {edge[1]} meet in more than one edge; the managed B-rep kernel rounds one");
            var (result, blend, corners, problem) = Blends.Apply(solid.Body, pairs[0].a, pairs[0].b, size, kind);
            if (problem is not null) return Unsupported(feature.Id, $"the edge between {edge[0]} and {edge[1]} cannot be {what}: {problem}");
            // Only the blend's own face takes its name; the rest of its prism lies on faces of the body. Its surface's
            // normal points out of the round: kept, the edge was convex.
            var face = new Dictionary<FaceId, IReadOnlyList<string>>();
            foreach (var o in result!.Origins.SelectMany(o => o).Where(o => o.Operand == 1)) face[o.Face] = o.Face == blend ? [$"{feature.Id}/face"] : [];
            if (grow == 0)
                for (var f = 0; f < result.Body.Faces.Length; f++)
                    if (result.Origins[f].Any(o => o.Operand == 1 && o.Face == blend)) convex[feature.Id] = result.Body.Faces[f].SameSense;
            // A ball where it meets two earlier fillets: id/corner:a+b+c after the three fillets, as the F-rep core names it.
            var operands = new List<NamedBody> { solid, new NamedBody(result.Body, face) };
            foreach (var fillets in corners)
            {
                var ids = fillets.Select(f => solid.Names[f].FirstOrDefault(n => n.EndsWith("/face", StringComparison.Ordinal)) is { } n ? n[..^"/face".Length] : solid.Names[f][0])
                    .Append(feature.Id).Order(StringComparer.Ordinal);
                var ball = new Dictionary<FaceId, IReadOnlyList<string>>();
                foreach (var o in result.Origins.SelectMany(o => o).Where(o => o.Operand == operands.Count)) ball[o.Face] = [$"{feature.Id}/corner:{string.Join("+", ids)}"];
                operands.Add(new NamedBody(result.Body, ball));
            }
            solids[i] = (Named(result, [.. operands]), body);
            return null;
        }
        return Unsupported(feature.Id, $"{feature.Id}: no edge between {edge[0]} and {edge[1]}");
    }

    // Every edge round a named flat face: each piece's face named after the face on the other side, id/<side>.
    static Diagnostic? LoopBlend(Feature feature, string loop, List<(NamedBody Solid, string Body)> solids, double grow)
    {
        var (size, kind) = feature switch
        {
            Fillet f => (f.Radius + grow, BlendKind.Fillet),
            Chamfer c => (c.Distance, BlendKind.Chamfer),
            _ => throw new NotSupportedException(feature.GetType().Name),
        };
        for (var i = 0; i < solids.Count; i++)
        {
            var (solid, body) = solids[i];
            var faces = Enumerable.Range(0, solid.Body.Faces.Length).Select(f => new FaceId(f)).Where(f => solid.Names[f].Contains(loop)).ToList();
            if (faces.Count == 0) continue;
            if (faces.Count > 1) return Unsupported(feature.Id, $"{feature.Id}: {loop} is in more than one piece; the managed B-rep kernel rounds the loops of one");
            var (result, pieces, problem) = Blends.Loop(solid.Body, faces[0], size, kind);
            if (problem is not null) return Unsupported(feature.Id, $"the edges round {loop} cannot be {(kind == BlendKind.Fillet ? "rounded" : "chamfered")}: {problem}");
            var operands = new List<NamedBody> { solid };
            foreach (var (blend, side) in pieces!)
            {
                var names = new Dictionary<FaceId, IReadOnlyList<string>>();
                foreach (var o in result!.Origins.SelectMany(o => o).Where(o => o.Operand == operands.Count))
                    names[o.Face] = o.Face == blend ? [.. solid.Names[side].Select(n => $"{feature.Id}/{n}")] : [];
                operands.Add(new NamedBody(result.Body, names));
            }
            solids[i] = (Named(result!, [.. operands]), body);
            return null;
        }
        return Unsupported(feature.Id, $"{feature.Id}: no face {loop}");
    }

    static bool Adjacent(Body body, FaceId a, FaceId b) => Enumerable.Range(0, body.Edges.Length)
        .Any(e => body.UsesOf(new EdgeId(e)).Select(body.FaceOf).ToHashSet().SetEquals([a, b]));

    // The feature's tools applied to the current body's solids: new solids of it, joined to the solids they meet, or
    // cut out of each. Other bodies are left as they are.
    static Diagnostic? Apply(List<(NamedBody Solid, string Body)> solids, string current, List<NamedBody> tools, BooleanOp op, string id)
    {
        // A body too thin for the tolerance (a sliver) fails its own merge: refused as a failing boolean is.
        try
        {
            return Applied(solids, current, tools, op, id);
        }
        catch (BooleanFailure failure)
        {
            return Unsupported(id, $"{id}: {failure.Message}");
        }
    }

    static Diagnostic? Applied(List<(NamedBody Solid, string Body)> solids, string current, List<NamedBody> tools, BooleanOp op, string id)
    {
        switch (op)
        {
            case BooleanOp.New:
                // Faces on one surface merged, as a boolean leaves them.
                solids.AddRange(tools.Select(t => (Named(Booleans.Merge(t.Body), [t]), current)));
                return null;
            case BooleanOp.Join:
                foreach (var tool in tools)
                {
                    var joined = tool;
                    for (var i = 0; i < solids.Count; i++)
                    {
                        if (solids[i].Body != current) continue;
                        var (result, problem) = Booleans.Unite(solids[i].Solid.Body, joined.Body);
                        if (problem is not null) return Unsupported(id, $"{id} cannot be joined: {problem}");
                        if (result is not [var one]) continue;
                        joined = Named(one, [solids[i].Solid, joined]);
                        solids.RemoveAt(i--);
                    }
                    solids.Add((joined == tool ? Named(Booleans.Merge(tool.Body), [tool]) : joined, current));
                }
                return null;
            case BooleanOp.Cut:
                // Tools that do not touch are cut out in one boolean.
                foreach (var group in Booleans.Apart([.. tools.Select(t => t.Body)]))
                {
                    var tool = group is [var only] ? tools[only] : Together([.. group.Select(k => tools[k])]);
                    var next = new List<(NamedBody, string)>();
                    foreach (var (solid, body) in solids)
                    {
                        if (body != current)
                        {
                            next.Add((solid, body));
                            continue;
                        }
                        var (result, problem) = Booleans.Subtract(solid.Body, tool.Body);
                        if (problem is not null) return Unsupported(id, $"{id} cannot be cut: {problem}");
                        next.AddRange(result!.Select(r => (Named(r, [solid, tool]), body)));
                    }
                    solids.Clear();
                    solids.AddRange(next);
                }
                return null;
            default:
                throw new NotSupportedException(op.ToString());
        }
    }

    // Tools that do not touch as one, each face keeping its names.
    static NamedBody Together(IReadOnlyList<NamedBody> tools)
    {
        var (body, offsets) = Booleans.Lumps([.. tools.Select(t => t.Body)]);
        var names = new Dictionary<FaceId, IReadOnlyList<string>>();
        for (var i = 0; i < tools.Count; i++)
            foreach (var (face, n) in tools[i].Names)
                names[new FaceId(face.Index + offsets[i])] = n;
        return new NamedBody(body, names);
    }

    // A result body named from its operands: each face by every source of the faces it came from.
    static NamedBody Named(Shaped shaped, NamedBody[] operands) => new(shaped.Body,
        Enumerable.Range(0, shaped.Body.Faces.Length).ToDictionary(f => new FaceId(f), f =>
            // One origin whose names are in order already (as every boolean leaves them) keeps its list.
            shaped.Origins[f] is [var one] && operands[one.Operand].Names[one.Face] is var names && Ordered(names) ? names
            : (IReadOnlyList<string>)[.. shaped.Origins[f].SelectMany(o => operands[o.Operand].Names[o.Face]).Distinct().Order(StringComparer.Ordinal)]));

    static bool Ordered(IReadOnlyList<string> names)
    {
        for (var i = 1; i < names.Count; i++)
            if (string.CompareOrdinal(names[i - 1], names[i]) >= 0) return false;
        return true;
    }

    // The plane and regions an extrude or revolve sweeps: its inline profile, or the regions of its sketch where it
    // was solved (for a revolve round a line of the sketch, put round that line).
    static (SketchPlane? Plane, IReadOnlyList<SketchRegion>? Regions, string? Problem) Shape(Feature f, Dictionary<string, Placed> sketches)
    {
        var (plane, profile, sketch, wanted) = f switch
        {
            Extrude e => (e.Plane, e.Profile, e.Sketch, e.Regions),
            Revolve r => (r.Plane, r.Profile, r.Sketch, r.Regions),
            Sweep w => (null, null, w.Sketch, w.Regions),
            _ => throw new ArgumentException(f.Id),
        };
        if (sketch is null) return (plane, [new SketchRegion(profile!, [])], null);
        var placed = sketches[sketch];
        var (all, problem) = SketchRegions.Of(placed.Sketch, placed.Solution);
        if (problem is not null) return (null, null, $"its sketch {placed.Sketch.Id}: {problem}");
        var (regions, why) = SketchRegions.Select(all, wanted);
        if (why is not null) return (null, null, why);
        return f is Revolve { Axis: { } axis } ? AboutAxis(placed, regions!, axis) : (placed.Plane, regions, null);
    }

    // Regions to turn round a line of their sketch, as the F-rep core puts them: the plane moved to the line's first
    // point, its y along the line and its x square to it on the regions' side, the regions in those coordinates.
    static (SketchPlane, IReadOnlyList<SketchRegion>, string?) AboutAxis(Placed placed, IReadOnlyList<SketchRegion> regions, string axis)
    {
        var line = placed.Sketch.Entities.OfType<SketchLine>().First(l => l.Id == axis);
        var (a, b) = (placed.Solution.Points[line.From], placed.Solution.Points[line.To]);
        var length = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
        var (dx, dy) = ((b.X - a.X) / length, (b.Y - a.Y) / length);
        double Across(double[] p) => (p[0] - a.X) * dy - (p[1] - a.Y) * dx;
        var side = regions.SelectMany(r => r.Outer.Edges).Sum(e => Across(e.To)) >= 0 ? 1 : -1;
        double[] To(double[] p) => [side * Across(p), side * ((p[0] - a.X) * dx + (p[1] - a.Y) * dy)];
        Profile Map(Profile p) => new(To(p.Start), [.. p.Edges.Select(e => e with { To = To(e.To) })]);
        var frame = FrameOf(placed.Plane);
        var origin = frame.Apply(new Point3D(a.X, a.Y, 0));
        var x = frame.XAxis * (side * dy) + frame.YAxis * (-side * dx);
        var plane = new SketchPlane([origin.X, origin.Y, origin.Z], [x.X, x.Y, x.Z], placed.Plane.Normal);
        return (plane, [.. regions.Select(r => new SketchRegion(Map(r.Outer), [.. r.Holes.Select(Map)]))], null);
    }

    // Isoform's topology: a face per kernel face; an edge per chain of kernel edges between the same two faces (a
    // seam, between a face and itself, is not an edge there); a vertex where three or more faces meet.
    static Topology TopologyOf(IReadOnlyList<NamedBody> bodies)
    {
        var faces = new List<TopoFace>();
        var edges = new List<TopoEdge>();
        var vertices = new List<TopoVertex>();
        for (var b = 0; b < bodies.Count; b++)
        {
            var (body, names) = (bodies[b].Body, bodies[b].Names);
            var offset = faces.Count;
            for (var f = 0; f < body.Faces.Length; f++) faces.Add(new TopoFace(offset + f, names[new FaceId(f)], b));

            var pairs = new Dictionary<(int, int), List<Edge>>();
            for (var i = 0; i < body.Edges.Length; i++)
            {
                var uses = body.UsesOf(new EdgeId(i));
                if (uses.Length < 2) continue; // a pole's, not an edge between faces
                var (fa, fb) = (body.FaceOf(uses[0]).Index, body.FaceOf(uses[1]).Index);
                if (fa == fb) continue;
                var key = (Math.Min(fa, fb), Math.Max(fa, fb));
                if (!pairs.TryGetValue(key, out var list)) pairs[key] = list = [];
                list.Add(body.Edges[i]);
            }
            foreach (var ((fa, fb), list) in pairs.OrderBy(p => p.Key))
                foreach (var _ in Chains(list)) edges.Add(new TopoEdge(edges.Count, offset + fa, offset + fb));

            var around = body.Coedges.Select((_, c) => new CoedgeId(c)).GroupBy(c => body.StartOf(c));
            foreach (var group in around.OrderBy(g => g.Key.Index))
            {
                var at = group.Select(c => offset + body.FaceOf(c).Index).Distinct().Order().ToList();
                if (at.Count >= 3) vertices.Add(new TopoVertex(vertices.Count, at));
            }
        }
        return new Topology(faces, edges, vertices, bodies.Count);
    }

    // Edges between the same two faces that share a vertex are one chain.
    static IEnumerable<List<Edge>> Chains(List<Edge> edges)
    {
        var left = edges.ToList();
        while (left.Count > 0)
        {
            var chain = new List<Edge> { left[0] };
            left.RemoveAt(0);
            for (var grown = true; grown;)
            {
                grown = false;
                foreach (var e in left.ToList())
                    if (chain.Any(c => c.Start == e.Start || c.Start == e.End || c.End == e.Start || c.End == e.End))
                    {
                        chain.Add(e);
                        left.Remove(e);
                        grown = true;
                    }
            }
            yield return chain;
        }
    }

    // How far a face's surface can stand from its corners: a whole circle round a cylinder's or a cone's axis is
    // that far out (a cone's radius here taken at its frame, which lies on the face), a torus's tube further.
    static double Reach(Surface s) => s switch { Cylinder c => c.Radius, Cone k => k.Radius, Torus t => t.Major + t.Minor, Sphere o => o.Radius, _ => 0 };

    static Point3D P(double[] a) => new(a[0], a[1], a[2]);
    static Vector3D V(double[] a) => new(a[0], a[1], a[2]);
}

/// <summary>A kernel body with Isoform's names for each face (several for a face merged from faces on one surface).</summary>
public sealed record NamedBody(Body Body, IReadOnlyDictionary<FaceId, IReadOnlyList<string>> Names);

public sealed class BrepResult(IReadOnlyList<Diagnostic> diagnostics, Topology? topology, IReadOnlyList<NamedBody> bodies)
    : ModelResult(diagnostics, topology)
{
    /// <summary>The kernel bodies, in the order of the topology's body indices; face i of body b is topology face (faces of earlier bodies) + i.</summary>
    public IReadOnlyList<NamedBody> Bodies { get; } = bodies;

    public override int? FaceNear(string source, double x, double y, double z)
    {
        var p = new Point3D(x, y, z);
        var offset = 0;
        (int Face, double Distance)? best = null;
        foreach (var (body, names) in Bodies)
        {
            for (var f = 0; f < body.Faces.Length; f++)
            {
                if (!names[new FaceId(f)].Contains(source)) continue;
                var d = Distance(body.Faces[f].Surface, p);
                if (best is null || d < best.Value.Distance) best = (offset + f, d);
            }
            offset += body.Faces.Length;
        }
        return best?.Face;
    }

    static double Distance(Surface s, Point3D p) => s switch
    {
        Plane plane => Math.Abs(plane.Frame.ApplyInverse(p).Z),
        Cylinder c => Math.Abs(Math.Sqrt(Square(c.Frame.ApplyInverse(p).X) + Square(c.Frame.ApplyInverse(p).Y)) - c.Radius),
        Torus t => Math.Abs(Math.Sqrt(Square(Math.Sqrt(Square(t.Frame.ApplyInverse(p).X) + Square(t.Frame.ApplyInverse(p).Y)) - t.Major) + Square(t.Frame.ApplyInverse(p).Z)) - t.Minor),
        Cone k => Math.Abs(Math.Sqrt(Square(k.Frame.ApplyInverse(p).X) + Square(k.Frame.ApplyInverse(p).Y)) - k.RadiusAt(k.Frame.ApplyInverse(p).Z)) / Math.Sqrt(1 + Square(k.Slope)),
        Sphere o => Math.Abs((p - o.Frame.Origin).Length - o.Radius),
        _ => throw new NotSupportedException(s.GetType().Name),
    };

    static double Square(double x) => x * x;
}
