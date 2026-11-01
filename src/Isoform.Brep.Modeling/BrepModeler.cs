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
/// (linear, circular or mirrored; copy k of face s is <c>id/k:s</c>), bodies joined or cut into others, and fillets and chamfers on the edge between two flat faces or round a cylinder square to a flat
/// face (face <c>id/face</c>, a cone for a chamfer there). Each starts a body, joins it or cuts it, working on the
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
        if (DocumentRules.FirstProblem(document, settings.Tolerance) is { } invalid) return Failed(invalid);
        document = Parametric.Resolve(document);

        var solids = new List<(NamedBody Solid, string Body)>();
        var sketches = new Dictionary<string, Placed>();
        var bodyOf = new Dictionary<string, string>();
        string? current = null;
        foreach (var feature in document.Features)
        {
            if (feature is Sketch sketch)
            {
                if (Prepare(sketch) is { } problem) return Failed(problem);
                continue;
            }
            if (DocumentRules.StartsBody(feature)) current = feature.Body ?? feature.Id;
            else if (feature.Body is { } named) current = named;
            bodyOf[feature.Id] = current!;

            if (feature is Fillet or Chamfer)
            {
                if (Blend(feature, solids) is { } problemBlending) return Failed(problemBlending);
                continue;
            }

            if (feature is Pattern pattern)
            {
                if (Copies(pattern) is { } problemCopying) return Failed(problemCopying);
                continue;
            }

            if (feature is CombineBodies combine)
            {
                if (Combined(combine) is { } problemCombining) return Failed(problemCombining);
                continue;
            }

            var (tools, op, failed) = Tools(feature, current!, Motion.None);
            if (failed is not null) return Failed(failed);
            if (Apply(solids, current!, tools!, op, feature.Id) is { } problemApplying) return Failed(problemApplying);
        }
        var bodies = solids.Select(s => s.Solid).ToList();
        return new BrepResult([], TopologyOf(bodies), bodies);

        // A solid-making feature's tools, placed by a motion, against the solids of the body it works on.
        (List<NamedBody>? Tools, BooleanOp Op, Diagnostic? Problem) Tools(Feature feature, string body, Motion motion)
        {
            List<NamedBody> Part() => [.. solids.Where(s => s.Body == body).Select(s => s.Solid)];
            return feature switch
            {
                Block { Blend: 0 } b => Guard(b.Id, () => [BlockTool(b, motion)], b.Op),
                Extrude { Blend: 0 } e => Extruded(e, sketches, motion) is var (t, p) ? (t, e.Op, p) : default,
                Revolve { Blend: 0 } r => Revolved(r, sketches, motion) is var (t, p) ? (t, r.Op, p) : default,
                Sweep w => SweepTools(w, sketches, motion) is var (t, p) ? (t, w.Op, p) : default,
                Hole h => HoleTools(h, sketches, Part(), motion) is var (t, p) ? (t, BooleanOp.Cut, p) : default,
                PlaneCut c => Guard(c.Id, () => [PlaneTool(c, Part(), motion)], BooleanOp.Cut),
                _ => (null, BooleanOp.New, Unsupported(feature.Id, $"{feature.GetType().Name} is not supported by the managed B-rep kernel yet")),
            };
        }

        // A pattern's copies, each in the body of the feature it copies (joined to it where the original started it),
        // copy k of face s named id/k:s. Copies of cuts only are cut out together; others go copy by copy, feature by
        // feature, as the F-rep core applies them.
        Diagnostic? Copies(Pattern p)
        {
            var count = p.Motion is MirrorMotion ? 1 : p.Motion switch { LinearMotion l => l.Count, CircularMotion c => c.Count, _ => 1 } - 1;
            var copies = (from k in Enumerable.Range(1, count) from id in p.Features select (K: k, Source: document[id])).ToList();
            var cutsOnly = copies.All(c => c.Source is Hole or PlaneCut or Block { Op: BooleanOp.Cut } or Extrude { Op: BooleanOp.Cut } or Revolve { Op: BooleanOp.Cut } or Sweep { Op: BooleanOp.Cut })
                           && copies.Select(c => bodyOf[c.Source.Id]).Distinct().Count() == 1;
            var batch = new List<NamedBody>();
            foreach (var (k, source) in copies)
            {
                var body = bodyOf[source.Id];
                var (tools, op, failed) = Tools(source, body, Motion.Of(p.Motion, k));
                if (failed is not null) return failed with { FeatureId = p.Id };
                var renamed = tools!.Select(t => new NamedBody(t.Body, t.Names.ToDictionary(n => n.Key,
                    n => (IReadOnlyList<string>)[.. n.Value.Select(name => $"{p.Id}/{k}:{name}")]))).ToList();
                if (cutsOnly) batch.AddRange(renamed);
                else if (Apply(solids, body, renamed, op == BooleanOp.New ? BooleanOp.Join : op, p.Id) is { } problem) return problem;
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

    // A block: its rectangle swept up Z, sides named by the side of the box they face.
    NamedBody BlockTool(Block b, Motion motion)
    {
        var (x0, y0, z0) = (b.Origin[0], b.Origin[1], b.Origin[2]);
        var (x1, y1, z1) = (x0 + b.Size[0], y0 + b.Size[1], z0 + b.Size[2]);
        Point2D Q(double x, double y) => new(x, y);
        var loop = new ProfileLoop([new LineSegment(Q(x0, y0), Q(x1, y0)), new LineSegment(Q(x1, y0), Q(x1, y1)),
            new LineSegment(Q(x1, y1), Q(x0, y1)), new LineSegment(Q(x0, y1), Q(x0, y0))]);
        var (frame, region) = motion.Place(RigidTransform3D.Identity, new Region(loop));
        return Swept(b.Id, region, frame, z0, z1, [["y-", "x+", "y+", "x-"]], "z-", "z+");
    }

    // A region swept along the frame's Z, its faces named: sides by `sides`, caps `start` and `end`.
    NamedBody Swept(string id, Region region, RigidTransform3D frame, double from, double to, List<List<string>> sides, string start, string end)
    {
        var (result, why) = Extrusion.Extrude(region, frame, from, to, settings.Tolerance);
        if (result is null) throw new BooleanFailure($"{id} cannot be swept: {why}");
        var names = new Dictionary<FaceId, IReadOnlyList<string>> { [result.StartCap] = [$"{id}/{start}"], [result.EndCap] = [$"{id}/{end}"] };
        for (var l = 0; l < sides.Count; l++)
        for (var s = 0; s < sides[l].Count; s++)
            names[result.Sides[l][s]] = [$"{id}/{sides[l][s]}"];
        return new NamedBody(result.Body, names);
    }

    // The extrude's tool bodies, one per region, named.
    (List<NamedBody>? Tools, Diagnostic? Problem) Extruded(Extrude e, Dictionary<string, Placed> sketches, Motion motion)
    {
        var (plane, regions, problem) = Shape(e, sketches);
        if (problem is not null) return (null, new Diagnostic(SketchSolution.Invalid, e.Id, $"{e.Id}: {problem}"));
        var frame = FrameOf(plane!);
        var (tools, _, failed) = Guard(e.Id, () => [.. regions!.Select(region =>
        {
            var (loops, names) = ProfileConversion.Loops(region, settings.Tolerance);
            var (placed, moved) = motion.Place(frame, new Region(loops[0], loops.Skip(1).ToList()));
            return Swept(e.Id, moved, placed, e.Start, e.End, names, "start", "end");
        })], e.Op);
        return (tools, failed);
    }

    // The revolve's tool bodies, one per region turned round the sketch's y axis (or the line it names), faces named
    // after their profile edges and, below a whole turn, the ends id/start and id/end.
    (List<NamedBody>? Tools, Diagnostic? Problem) Revolved(Revolve r, Dictionary<string, Placed> sketches, Motion motion)
    {
        var (plane, regions, problem) = Shape(r, sketches);
        if (problem is not null) return (null, new Diagnostic(SketchSolution.Invalid, r.Id, $"{r.Id}: {problem}"));
        var (tools, _, failed) = Guard(r.Id, () => [.. regions!.Select(region =>
        {
            var (loops, names) = ProfileConversion.Loops(region, settings.Tolerance);
            var (sketch, moved) = motion.Place(FrameOf(plane!), new Region(loops[0], loops.Skip(1).ToList()));
            // The kernel turns (x, z) round its frame's Z from its X: the sketch's x round its y, towards −normal.
            var frame = RigidTransform3D.FromFrame(sketch.Origin, sketch.YAxis, sketch.XAxis);
            return Turned(r.Id, [.. moved.Loops], names, frame, Math.Min(r.Angle, 360) * Math.PI / 180, "start", "end");
        })], r.Op);
        return (tools, failed);
    }

    // Loops turned round the frame's Z (see Revolution), faces named id/<name> after their segments and a part turn's
    // ends id/start and id/end.
    NamedBody Turned(string id, List<ProfileLoop> loops, List<List<string>> names, RigidTransform3D frame, double angle, string start, string end)
    {
        // Outer loop counter-clockwise, holes clockwise: the kernel would turn them so, and number their faces so.
        for (var l = 0; l < loops.Count; l++)
            if (loops[l].SignedArea > 0 != (l == 0)) (loops[l], names[l]) = (loops[l].Reversed(), [.. Enumerable.Reverse(names[l])]);
        var (result, why) = Revolution.Revolve(new Region(loops[0], loops.Skip(1).ToList()), frame, settings.Tolerance, angle);
        if (result is null) throw new BooleanFailure($"{id} cannot be revolved: {why}");
        List<string> source = [.. names.SelectMany(n => n.Select(e => $"{id}/{e}")), $"{id}/{start}", $"{id}/{end}"];
        var faces = Enumerable.Range(0, result.Body.Faces.Length).ToDictionary(f => new FaceId(f),
            f => (IReadOnlyList<string>)[.. result.Origins[f].Select(o => source[o.Face.Index])]);
        return new NamedBody(result.Body, faces);
    }

    // A sweep, one tool per region: the profile extruded along each line of the path and turned round each arc's
    // axis (as Sweeps.Plan places them, with the F-rep core), the pieces joined where they meet across the path. Sides
    // id/<path entity>:<profile entity>, an open path's ends id/start and id/end.
    (List<NamedBody>? Tools, Diagnostic? Problem) SweepTools(Sweep sw, Dictionary<string, Placed> sketches, Motion motion)
    {
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
            var parts = pieces!.Select((piece, k) => PieceOf(sw.Id, piece, section,
                k == 0 && !closed ? "start" : $"{piece.Id}:from", k == pieces!.Count - 1 && !closed ? "end" : $"{piece.Id}:to")).ToList();
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
    NamedBody PieceOf(string id, Isoform.Geometry.SweepPiece piece, SketchRegion section, string start, string end)
    {
        double[] At(double[] x)
        {
            var (u, v) = piece.Section(x[0], x[1]);
            return [u, v];
        }
        Profile Map(Profile p) => piece.Arc ? new(At(p.Start), [.. p.Edges.Select(e => e with { To = At(e.To), Bulge = -e.Bulge })]) : p;
        var (loops, names) = ProfileConversion.Loops(new SketchRegion(Map(section.Outer), [.. section.Holes.Select(Map)]), settings.Tolerance);
        var sides = names.Select(l => l.Select(n => $"{piece.Id}:{n}").ToList()).ToList();
        var f = piece.Frame;
        return piece.Arc
            ? Turned(id, loops, sides, RigidTransform3D.FromFrame(f.Origin, f.Y, f.X), piece.Angle, start, end)
            : Swept(id, new Region(loops[0], loops.Skip(1).ToList()), RigidTransform3D.FromFrame(f.Origin, f.Z, f.X), 0, piece.Length, sides, start, end);
    }

    // A drill per spot: a cylinder along the outward normal where it goes in, from its floor (or from beyond the
    // part, for through all) out past the part.
    (List<NamedBody>? Tools, Diagnostic? Problem) HoleTools(Hole h, Dictionary<string, Placed> sketches, List<NamedBody> body, Motion motion)
    {
        List<(Point3D At, Vector3D Out, string Spot)> spots;
        if (h.On is null && h.Sketch is null) spots = [(new Point3D(h.Center[0], h.Center[1], 0), new Vector3D(0, 0, 1), "")];
        else if (h.On is { } face)
        {
            if (FacePlane(face, body) is not { } plane)
                return (null, new Diagnostic(Diagnostic.SketchPlaneLost, h.Id, $"{h.Id} is on {face}, which is not a flat face of the part here"));
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
            var circle = new Region(new ProfileLoop([ArcSegment.Circle(new Point2D(0, 0), h.Diameter / 2)]));
            var from = h.Depth is { } depth ? -depth : along.Min() - reach;
            return Swept(h.Id, circle, frame, from, along.Max() + reach, [[$"{spot.Spot}wall"]], $"{spot.Spot}floor", $"{spot.Spot}mouth");
        })], BooleanOp.Cut);
        return (tools, failed);
    }

    // A plane cut: a box standing on the plane on the side its normal points to, reaching past the body; its base is
    // the cut face, id/face.
    NamedBody PlaneTool(PlaneCut c, List<NamedBody> body, Motion motion)
    {
        var (at, n) = (motion.Apply(P(c.Point)), motion.Apply(V(c.Normal)).Normalized());
        var reach = body.SelectMany(b => b.Body.Vertices).Select(v => (v.Point - at).Length).DefaultIfEmpty(0).Max()
                    + body.SelectMany(b => b.Body.Faces).Select(f => Reach(f.Surface)).DefaultIfEmpty(0).Max() + 1;
        var x = Math.Abs(n.X) < 0.9 ? new Vector3D(1, 0, 0) : new Vector3D(0, 1, 0);
        var frame = RigidTransform3D.FromFrame(at, n, (x - n * n.Dot(x)).Normalized());
        Point2D Q(double u, double v) => new(u, v);
        var square = new ProfileLoop([new LineSegment(Q(-reach, -reach), Q(reach, -reach)), new LineSegment(Q(reach, -reach), Q(reach, reach)),
            new LineSegment(Q(reach, reach), Q(-reach, reach)), new LineSegment(Q(-reach, reach), Q(-reach, -reach))]);
        return Swept(c.Id, new Region(square), frame, 0, reach, [["beyond-1", "beyond-2", "beyond-3", "beyond-4"]], "face", "beyond");
    }

    // A fillet or chamfer on the edge between two named faces, or round the loops of one, on the solid that has them
    // (whichever body that is).
    static Diagnostic? Blend(Feature feature, List<(NamedBody Solid, string Body)> solids)
    {
        if ((feature switch { Fillet f => f.Loop, Chamfer c => c.Loop, _ => null }) is { } loop) return LoopBlend(feature, loop, solids);
        var (edge, size, kind) = feature switch
        {
            Fillet f => (f.Edge, f.Radius, BlendKind.Fillet),
            Chamfer c => (c.Edge, c.Distance, BlendKind.Chamfer),
            _ => throw new NotSupportedException(feature.GetType().Name),
        };
        var what = kind == BlendKind.Fillet ? "rounded" : "chamfered";
        for (var i = 0; i < solids.Count; i++)
        {
            var (solid, body) = solids[i];
            IEnumerable<FaceId> Called(string name) => Enumerable.Range(0, solid.Body.Faces.Length).Select(f => new FaceId(f)).Where(f => solid.Names[f].Contains(name));
            var pairs = (from a in Called(edge[0]) from b in Called(edge[1]) where Adjacent(solid.Body, a, b) select (a, b)).ToList();
            if (pairs.Count == 0) continue;
            if (pairs.Count > 1) return Unsupported(feature.Id, $"{feature.Id}: {edge[0]} and {edge[1]} meet in more than one edge; the managed B-rep kernel rounds one");
            var (result, blend, problem) = Blends.Apply(solid.Body, pairs[0].a, pairs[0].b, size, kind);
            if (problem is not null) return Unsupported(feature.Id, $"the edge between {edge[0]} and {edge[1]} cannot be {what}: {problem}");
            // Only the blend's own face takes its name; the rest of its prism lies on faces of the body.
            var face = new Dictionary<FaceId, IReadOnlyList<string>>();
            foreach (var o in result!.Origins.SelectMany(o => o).Where(o => o.Operand == 1)) face[o.Face] = o.Face == blend ? [$"{feature.Id}/face"] : [];
            solids[i] = (Named(result, [solid, new NamedBody(result.Body, face)]), body);
            return null;
        }
        return Unsupported(feature.Id, $"{feature.Id}: no edge between {edge[0]} and {edge[1]}");
    }

    // Every edge round a named flat face: each piece's face named after the face on the other side, id/<side>.
    static Diagnostic? LoopBlend(Feature feature, string loop, List<(NamedBody Solid, string Body)> solids)
    {
        var (size, kind) = feature switch
        {
            Fillet f => (f.Radius, BlendKind.Fillet),
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
        Enumerable.Range(0, shaped.Body.Faces.Length).ToDictionary(f => new FaceId(f), f => (IReadOnlyList<string>)
            [.. shaped.Origins[f].SelectMany(o => operands[o.Operand].Names[o.Face]).Distinct().Order(StringComparer.Ordinal)]));

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
    static double Reach(Surface s) => s switch { Cylinder c => c.Radius, Cone k => k.Radius, Torus t => t.Major + t.Minor, _ => 0 };

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
        _ => throw new NotSupportedException(s.GetType().Name),
    };

    static double Square(double x) => x * x;
}
