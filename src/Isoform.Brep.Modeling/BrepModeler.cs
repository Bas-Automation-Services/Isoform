using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Modeling;
using Isoform.Sketching;

namespace Isoform.Brep.Modeling;

/// <summary>
/// Isoform documents on the managed B-rep kernel: plain extrudes (an inline profile, or the regions of a sketch on a
/// plane) that start a body, join it or cut it, with Isoform's face names: sides after their profile edge, caps
/// <c>id/start</c> and <c>id/end</c>, faces merged on one surface named by all their sources. Anything else is
/// refused with <see cref="Diagnostic.BackendFailed"/>.
/// </summary>
public sealed class BrepModeler(ModelingSettings settings) : IModeler
{
    ModelResult IModeler.Regenerate(PartDocument document) => Regenerate(document);

    public BrepResult Regenerate(PartDocument document)
    {
        if (DocumentRules.FirstProblem(document, settings.Tolerance) is { } invalid) return Failed(invalid);
        document = Parametric.Resolve(document);

        var bodies = new List<NamedBody>();
        foreach (var feature in document.Features)
        {
            switch (feature)
            {
                case Sketch:
                    continue;
                case Extrude { Blend: 0 } e:
                    var (tools, problem) = Tools(document, e);
                    if (problem is not null) return Failed(problem);
                    if (Apply(bodies, tools!, e) is { } failed) return Failed(failed);
                    break;
                default:
                    return Failed(Unsupported(feature.Id, $"{feature.GetType().Name} is not supported by the managed B-rep kernel yet"));
            }
        }
        return new BrepResult([], TopologyOf(bodies), bodies);
    }

    static BrepResult Failed(Diagnostic d) => new([d], null, []);

    static Diagnostic Unsupported(string? feature, string message) => new(Diagnostic.BackendFailed, feature, message);

    // The extrude's tool bodies, one per region, named.
    (List<NamedBody>? Tools, Diagnostic? Problem) Tools(PartDocument document, Extrude e)
    {
        var (plane, regions, problem) = Shape(document, e);
        if (problem is not null) return (null, new Diagnostic(SketchSolution.Invalid, e.Id, $"{e.Id}: {problem}"));

        var frame = RigidTransform3D.FromFrame(P(plane!.Origin), V(plane.Normal), V(plane.X));
        var tools = new List<NamedBody>();
        foreach (var region in regions!)
        {
            var (loops, names) = ProfileConversion.Loops(region, settings.Tolerance);
            var (result, why) = Extrusion.Extrude(new Region(loops[0], loops.Skip(1).ToList()), frame, e.Start, e.End, settings.Tolerance);
            if (result is null) return (null, Unsupported(e.Id, $"{e.Id} cannot be extruded: {why}"));
            var faceNames = new Dictionary<FaceId, IReadOnlyList<string>> { [result.StartCap] = [$"{e.Id}/start"], [result.EndCap] = [$"{e.Id}/end"] };
            for (var l = 0; l < names.Count; l++)
            for (var s = 0; s < names[l].Count; s++)
                faceNames[result.Sides[l][s]] = [$"{e.Id}/{names[l][s]}"];
            var raw = new NamedBody(result.Body, faceNames);
            try
            {
                tools.Add(Named(Booleans.Merge(result.Body), [raw]));
            }
            catch (BooleanFailure failure)
            {
                return (null, Unsupported(e.Id, $"{e.Id} made an invalid body: {failure.Message}"));
            }
        }
        return (tools, null);
    }

    // The extrude's tools applied to the part's bodies: new bodies, joined to the bodies they meet, or cut out of each.
    static Diagnostic? Apply(List<NamedBody> bodies, List<NamedBody> tools, Extrude e)
    {
        switch (e.Op)
        {
            case BooleanOp.New:
                bodies.AddRange(tools);
                return null;
            case BooleanOp.Join:
                foreach (var tool in tools)
                {
                    var current = tool;
                    for (var i = 0; i < bodies.Count; i++)
                    {
                        var (result, problem) = Booleans.Unite(bodies[i].Body, current.Body);
                        if (problem is not null) return Unsupported(e.Id, $"{e.Id} cannot be joined: {problem}");
                        if (result is not [var one]) continue;
                        current = Named(one, [bodies[i], current]);
                        bodies.RemoveAt(i--);
                    }
                    bodies.Add(current);
                }
                return null;
            case BooleanOp.Cut:
                var pieces = bodies.ToList();
                foreach (var tool in tools)
                {
                    var next = new List<NamedBody>();
                    foreach (var piece in pieces)
                    {
                        var (result, problem) = Booleans.Subtract(piece.Body, tool.Body);
                        if (problem is not null) return Unsupported(e.Id, $"{e.Id} cannot be cut: {problem}");
                        next.AddRange(result!.Select(r => Named(r, [piece, tool])));
                    }
                    pieces = next;
                }
                bodies.Clear();
                bodies.AddRange(pieces);
                return null;
            default:
                throw new NotSupportedException(e.Op.ToString());
        }
    }

    // A result body named from its operands: each face by every source of the faces it came from.
    static NamedBody Named(Shaped shaped, NamedBody[] operands) => new(shaped.Body,
        Enumerable.Range(0, shaped.Body.Faces.Length).ToDictionary(f => new FaceId(f), f => (IReadOnlyList<string>)
            [.. shaped.Origins[f].SelectMany(o => operands[o.Operand].Names[o.Face]).Distinct().Order(StringComparer.Ordinal)]));

    // The plane and regions an extrude sweeps: its inline profile, or the regions of its sketch.
    static (SketchPlane? Plane, IReadOnlyList<SketchRegion>? Regions, string? Problem) Shape(PartDocument document, Extrude e)
    {
        if (e.Sketch is null) return (e.Plane, [new SketchRegion(e.Profile!, [])], null);
        var sketch = document.Features.OfType<Sketch>().First(s => s.Id == e.Sketch);
        if (sketch.On.Plane is null) return (null, null, $"its sketch {sketch.Id} lies on a face, which the managed B-rep kernel does not follow yet");
        var solution = SketchSolver.Solve(sketch);
        if (!solution.Solved) return (null, null, $"its sketch {sketch.Id} does not solve");
        var (all, problem) = SketchRegions.Of(sketch, solution);
        if (problem is not null) return (null, null, $"its sketch {sketch.Id}: {problem}");
        var (regions, why) = SketchRegions.Select(all, e.Regions);
        return why is null ? (sketch.On.Plane, regions, null) : (null, null, why);
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
