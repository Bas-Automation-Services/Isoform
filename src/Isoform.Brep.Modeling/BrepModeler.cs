using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Modeling;
using Isoform.Sketching;

namespace Isoform.Brep.Modeling;

/// <summary>
/// Isoform documents on the managed B-rep kernel: blocks, plain extrudes (an inline profile, or the regions of a
/// sketch on a plane or on a flat face of the part), holes (through, or blind with a floor; along Z, into a face, or
/// at a sketch's points), plane cuts (face <c>id/face</c>), and fillets and chamfers on the edge between two flat
/// faces (face <c>id/face</c>). Each starts a body, joins it or cuts it, working on the current body as Isoform's
/// <see cref="Feature.Body"/> says. Faces carry Isoform's names: sides after their profile edge, caps <c>id/start</c>
/// and <c>id/end</c>, blocks <c>id/x-</c> … <c>id/z+</c>, holes <c>id/wall</c> and <c>id/floor</c>, faces merged on
/// one surface named by all their sources. Anything else is refused with <see cref="Diagnostic.BackendFailed"/>.
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

            if (feature is Fillet { Loop: null } or Chamfer { Loop: null })
            {
                if (Blend(feature, solids) is { } problemBlending) return Failed(problemBlending);
                continue;
            }

            var (tools, op, failed) = feature switch
            {
                Block { Blend: 0 } b => Guard(b.Id, () => [BlockTool(b)], b.Op),
                Extrude { Blend: 0 } e => Tools(e, sketches) is var (t, p) ? (t, e.Op, p) : default,
                Hole h => HoleTools(h, sketches, [.. solids.Where(s => s.Body == current).Select(s => s.Solid)]) is var (t, p) ? (t, BooleanOp.Cut, p) : default,
                PlaneCut c => Guard(c.Id, () => [PlaneTool(c, [.. solids.Where(s => s.Body == current).Select(s => s.Solid)])], BooleanOp.Cut),
                _ => (null, BooleanOp.New, Unsupported(feature.Id, $"{feature.GetType().Name} is not supported by the managed B-rep kernel yet")),
            };
            if (failed is not null) return Failed(failed);
            if (Apply(solids, current!, tools!, op, feature.Id) is { } problemApplying) return Failed(problemApplying);
        }
        var bodies = solids.Select(s => s.Solid).ToList();
        return new BrepResult([], TopologyOf(bodies), bodies);

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
    NamedBody BlockTool(Block b)
    {
        var (x0, y0, z0) = (b.Origin[0], b.Origin[1], b.Origin[2]);
        var (x1, y1, z1) = (x0 + b.Size[0], y0 + b.Size[1], z0 + b.Size[2]);
        Point2D Q(double x, double y) => new(x, y);
        var loop = new ProfileLoop([new LineSegment(Q(x0, y0), Q(x1, y0)), new LineSegment(Q(x1, y0), Q(x1, y1)),
            new LineSegment(Q(x1, y1), Q(x0, y1)), new LineSegment(Q(x0, y1), Q(x0, y0))]);
        return Swept(b.Id, new Region(loop), RigidTransform3D.Identity, z0, z1, [["y-", "x+", "y+", "x-"]], "z-", "z+");
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
        return Named(Booleans.Merge(result.Body), [new NamedBody(result.Body, names)]);
    }

    // The extrude's tool bodies, one per region, named.
    (List<NamedBody>? Tools, Diagnostic? Problem) Tools(Extrude e, Dictionary<string, Placed> sketches)
    {
        var (plane, regions, problem) = Shape(e, sketches);
        if (problem is not null) return (null, new Diagnostic(SketchSolution.Invalid, e.Id, $"{e.Id}: {problem}"));
        var frame = FrameOf(plane!);
        var (tools, _, failed) = Guard(e.Id, () => [.. regions!.Select(region =>
        {
            var (loops, names) = ProfileConversion.Loops(region, settings.Tolerance);
            return Swept(e.Id, new Region(loops[0], loops.Skip(1).ToList()), frame, e.Start, e.End, names, "start", "end");
        })], e.Op);
        return (tools, failed);
    }

    // A drill per spot: a cylinder along the outward normal where it goes in, from its floor (or from beyond the
    // part, for through all) out past the part.
    (List<NamedBody>? Tools, Diagnostic? Problem) HoleTools(Hole h, Dictionary<string, Placed> sketches, List<NamedBody> body)
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

        var corners = body.SelectMany(b => b.Body.Vertices.Select(v => v.Point)).ToList();
        var reach = body.SelectMany(b => b.Body.Faces).Select(f => f.Surface is Cylinder c ? c.Radius : 0).DefaultIfEmpty(0).Max() + h.Diameter + 1;
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
    NamedBody PlaneTool(PlaneCut c, List<NamedBody> body)
    {
        var (at, n) = (P(c.Point), V(c.Normal).Normalized());
        var reach = body.SelectMany(b => b.Body.Vertices).Select(v => (v.Point - at).Length).DefaultIfEmpty(0).Max()
                    + body.SelectMany(b => b.Body.Faces).Select(f => f.Surface is Cylinder k ? k.Radius : 0).DefaultIfEmpty(0).Max() + 1;
        var x = Math.Abs(n.X) < 0.9 ? new Vector3D(1, 0, 0) : new Vector3D(0, 1, 0);
        var frame = RigidTransform3D.FromFrame(at, n, (x - n * n.Dot(x)).Normalized());
        Point2D Q(double u, double v) => new(u, v);
        var square = new ProfileLoop([new LineSegment(Q(-reach, -reach), Q(reach, -reach)), new LineSegment(Q(reach, -reach), Q(reach, reach)),
            new LineSegment(Q(reach, reach), Q(-reach, reach)), new LineSegment(Q(-reach, reach), Q(-reach, -reach))]);
        return Swept(c.Id, new Region(square), frame, 0, reach, [["beyond-1", "beyond-2", "beyond-3", "beyond-4"]], "face", "beyond");
    }

    // A fillet or chamfer on the edge between two named faces, on the solid that has them (whichever body that is).
    static Diagnostic? Blend(Feature feature, List<(NamedBody Solid, string Body)> solids)
    {
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

    static bool Adjacent(Body body, FaceId a, FaceId b) => Enumerable.Range(0, body.Edges.Length)
        .Any(e => body.UsesOf(new EdgeId(e)).Select(body.FaceOf).ToHashSet().SetEquals([a, b]));

    // The feature's tools applied to the current body's solids: new solids of it, joined to the solids they meet, or
    // cut out of each. Other bodies are left as they are.
    static Diagnostic? Apply(List<(NamedBody Solid, string Body)> solids, string current, List<NamedBody> tools, BooleanOp op, string id)
    {
        switch (op)
        {
            case BooleanOp.New:
                solids.AddRange(tools.Select(t => (t, current)));
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
                    solids.Add((joined, current));
                }
                return null;
            case BooleanOp.Cut:
                foreach (var tool in tools)
                {
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

    // A result body named from its operands: each face by every source of the faces it came from.
    static NamedBody Named(Shaped shaped, NamedBody[] operands) => new(shaped.Body,
        Enumerable.Range(0, shaped.Body.Faces.Length).ToDictionary(f => new FaceId(f), f => (IReadOnlyList<string>)
            [.. shaped.Origins[f].SelectMany(o => operands[o.Operand].Names[o.Face]).Distinct().Order(StringComparer.Ordinal)]));

    // The plane and regions an extrude sweeps: its inline profile, or the regions of its sketch where it was solved.
    static (SketchPlane? Plane, IReadOnlyList<SketchRegion>? Regions, string? Problem) Shape(Extrude e, Dictionary<string, Placed> sketches)
    {
        if (e.Sketch is null) return (e.Plane, [new SketchRegion(e.Profile!, [])], null);
        var placed = sketches[e.Sketch];
        var (all, problem) = SketchRegions.Of(placed.Sketch, placed.Solution);
        if (problem is not null) return (null, null, $"its sketch {placed.Sketch.Id}: {problem}");
        var (regions, why) = SketchRegions.Select(all, e.Regions);
        return why is null ? (placed.Plane, regions, null) : (null, null, why);
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
        _ => throw new NotSupportedException(s.GetType().Name),
    };

    static double Square(double x) => x * x;
}
