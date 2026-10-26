using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Modeling;
using Isoform.Sketching;

namespace Isoform.Brep.Modeling;

/// <summary>
/// Isoform documents on the managed B-rep kernel. Experiment 1 covers one extrude that starts the part (an inline
/// profile, or the regions of a sketch on a plane), with Isoform's face names: sides after their profile edge, caps
/// <c>id/start</c> and <c>id/end</c>. Anything else is refused with <see cref="Diagnostic.BackendFailed"/>.
/// </summary>
public sealed class BrepModeler(ModelingSettings settings) : IModeler
{
    ModelResult IModeler.Regenerate(PartDocument document) => Regenerate(document);

    public BrepResult Regenerate(PartDocument document)
    {
        if (DocumentRules.FirstProblem(document, settings.Tolerance) is { } invalid) return Failed(invalid);
        document = Parametric.Resolve(document);

        var extrudes = document.Features.OfType<Extrude>().ToList();
        if (document.Features.FirstOrDefault(f => f is not (Sketch or Extrude)) is { } other)
            return Failed(Unsupported(other.Id, $"{other.GetType().Name} is not supported by the managed B-rep kernel yet"));
        if (extrudes is not [{ Op: BooleanOp.New, Blend: 0 } e])
            return Failed(Unsupported(extrudes.FirstOrDefault()?.Id, "the managed B-rep kernel supports one plain extrude that starts the part"));

        var (plane, regions, problem) = Shape(document, e);
        if (problem is not null) return Failed(new Diagnostic(SketchSolution.Invalid, e.Id, $"{e.Id}: {problem}"));

        var frame = RigidTransform3D.FromFrame(P(plane!.Origin), V(plane.Normal), V(plane.X));
        var bodies = new List<NamedBody>();
        foreach (var region in regions!)
        {
            var (loops, names) = ProfileConversion.Loops(region, settings.Tolerance);
            var (result, why) = Extrusion.Extrude(new Region(loops[0], loops.Skip(1).ToList()), frame, e.Start, e.End, settings.Tolerance);
            if (result is null) return Failed(Unsupported(e.Id, $"{e.Id} cannot be extruded: {why}"));
            var faceNames = new Dictionary<FaceId, string> { [result.StartCap] = $"{e.Id}/start", [result.EndCap] = $"{e.Id}/end" };
            for (var l = 0; l < names.Count; l++)
            for (var s = 0; s < names[l].Count; s++)
                faceNames[result.Sides[l][s]] = $"{e.Id}/{names[l][s]}";
            if (BodyCheck.Problems(result.Body) is [var first, ..])
                return Failed(Unsupported(e.Id, $"{e.Id} made an invalid body: {first.Kind} at {first.Where}: {first.Message}"));
            bodies.Add(new NamedBody(result.Body, faceNames));
        }
        return new BrepResult([], TopologyOf(bodies), bodies);
    }

    static BrepResult Failed(Diagnostic d) => new([d], null, []);

    static Diagnostic Unsupported(string? feature, string message) => new(Diagnostic.BackendFailed, feature, message);

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
            for (var f = 0; f < body.Faces.Length; f++) faces.Add(new TopoFace(offset + f, [names[new FaceId(f)]], b));

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

/// <summary>A kernel body with Isoform's name for each face.</summary>
public sealed record NamedBody(Body Body, IReadOnlyDictionary<FaceId, string> Names);

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
                if (names[new FaceId(f)] != source) continue;
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
