using Isoform.Document;
using Isoform.Modeling;

namespace Isoform.Occt;

/// <summary>
/// The B-rep baseline: replays a document as OCCT operations and names faces the conventional way, by carrying
/// each face's sources through OCCT's operation history (Modified / Generated / Deleted). Face merges from
/// same-domain unification keep every source, exactly as on the F-rep side.
/// </summary>
public sealed class OcctModeler(ModelingSettings settings) : IModeler
{
    public static bool IsAvailable => Native.IsAvailable;

    ModelResult IModeler.Regenerate(PartDocument document) => Regenerate(document);

    public OcctResult Regenerate(PartDocument document)
    {
        if (DocumentRules.FirstProblem(document, settings.Tolerance) is { } invalid)
            return new OcctResult([invalid], null, null);

        var diagnostics = new List<Diagnostic>();
        var bounds = Bounds(document);
        OcctShape part = null!;
        List<SortedSet<string>> tags = null!;

        foreach (var feature in document.Features)
        {
            try
            {
                switch (feature)
                {
                    case Block { Op: BooleanOp.New } b:
                        part = BlockShape(b);
                        tags = TagBlock(part, b.Id);
                        break;
                    case Block b:
                        var block = BlockShape(b);
                        (part, tags) = Combine(part, tags, block, TagBlock(block, b.Id), b.Op == BooleanOp.Join ? OcctBoolean.Fuse : OcctBoolean.Cut);
                        break;
                    case Hole h:
                        var cyl = OcctShape.Cylinder(h.Center[0], h.Center[1], bounds.Min[2] - 1, 0, 0, 1, h.Diameter / 2, bounds.Max[2] - bounds.Min[2] + 2);
                        var cylTags = Enumerable.Range(0, cyl.FaceCount)
                            .Select(f => Tag(cyl.Face(f).Kind == OcctFaceKind.Cylinder ? $"{h.Id}/wall" : $"{h.Id}/cap")).ToList();
                        (part, tags) = Combine(part, tags, cyl, cylTags, OcctBoolean.Cut);
                        break;
                    case PlaneCut c:
                        var size = 4 * bounds.Diagonal + 100;
                        var half = OcctShape.HalfSpace(c.Point, Unit(c.Normal), size);
                        var halfTags = Enumerable.Range(0, half.FaceCount)
                            .Select(f => Tag(OnPlane(half.Face(f), c.Point, Unit(c.Normal)) ? $"{c.Id}/face" : $"{c.Id}/outer")).ToList();
                        (part, tags) = Combine(part, tags, half, halfTags, OcctBoolean.Cut);
                        break;
                    case Fillet f:
                        (part, tags) = ApplyFillet(part, tags, f, diagnostics);
                        break;
                }
            }
            catch (OcctException e)
            {
                diagnostics.Add(new Diagnostic(Diagnostic.BackendFailed, feature.Id, e.Message));
                return new OcctResult(diagnostics, null, null);
            }
        }

        var topology = BuildTopology(part, tags);
        if (part.SolidCount > 1)
            diagnostics.Add(new Diagnostic(Diagnostic.MultipleBodies, null, $"the part falls apart into {part.SolidCount} bodies"));
        if (!part.IsValid)
            diagnostics.Add(new Diagnostic(Diagnostic.BackendFailed, null, "OCCT reports the result shape as invalid"));
        var present = topology.Faces.SelectMany(f => f.Sources).ToHashSet();
        foreach (var feature in document.Features.Where(f => f is not Fillet))
            if (!present.Any(s => s.StartsWith(feature.Id + "/")))
                diagnostics.Add(new Diagnostic(Diagnostic.NoEffect, feature.Id, $"{feature.Id} does not touch the part"));

        return new OcctResult(diagnostics, topology, part);
    }

    static (OcctShape, List<SortedSet<string>>) Combine(OcctShape part, List<SortedSet<string>> tags, OcctShape tool, List<SortedSet<string>> toolTags, OcctBoolean kind)
    {
        var op = part.Boolean(kind, tool, simplify: true);
        var result = op.Result();
        return (result, Propagate(op, result, [tags, toolTags]));
    }

    (OcctShape, List<SortedSet<string>>) ApplyFillet(OcctShape part, List<SortedSet<string>> tags, Fillet f, List<Diagnostic> diagnostics)
    {
        var edges = Enumerable.Range(0, part.EdgeCount).Where(e =>
        {
            var faces = part.EdgeFaces(e);
            return faces.Length == 2 && (
                (tags[faces[0]].Contains(f.Edge[0]) && tags[faces[1]].Contains(f.Edge[1])) ||
                (tags[faces[0]].Contains(f.Edge[1]) && tags[faces[1]].Contains(f.Edge[0])));
        }).ToArray();
        if (edges.Length == 0)
        {
            diagnostics.Add(new Diagnostic(Diagnostic.FilletInvalid, f.Id, $"edge [{string.Join(", ", f.Edge)}] does not exist on the part"));
            return (part, tags);
        }

        try
        {
            var op = part.Fillet(edges, f.Radius);
            var result = op.Result();
            if (!result.IsValid) throw new OcctException("fillet result is invalid");
            var next = Propagate(op, result, [tags]);
            foreach (var g in op.Generated(0)) next[g].Add($"{f.Id}/face");
            return (result, next);
        }
        catch (OcctException e)
        {
            diagnostics.Add(new Diagnostic(Diagnostic.FilletInvalid, f.Id, e.Message));
            return (part, tags);
        }
    }

    static List<SortedSet<string>> Propagate(OcctOperation op, OcctShape result, List<SortedSet<string>>[] argumentTags)
    {
        var next = Enumerable.Range(0, result.FaceCount).Select(_ => new SortedSet<string>(StringComparer.Ordinal)).ToList();
        for (var a = 0; a < argumentTags.Length; a++)
            for (var face = 0; face < argumentTags[a].Count; face++)
                foreach (var r in op.Image(a, face))
                    next[r].UnionWith(argumentTags[a][face]);
        return next;
    }

    static Topology BuildTopology(OcctShape part, List<SortedSet<string>> tags)
    {
        var faceCount = part.FaceCount;
        var bodies = new UnionFind(faceCount);
        var segments = new List<(int A, int B, int V1, int V2)>();
        for (var e = 0; e < part.EdgeCount; e++)
        {
            var around = part.EdgeFaces(e);
            if (around.Length < 2) continue; // seam or degenerate edge
            bodies.Union(around[0], around[1]);
            var (v1, v2) = part.EdgeVertices(e);
            segments.Add((Math.Min(around[0], around[1]), Math.Max(around[0], around[1]), v1, v2));
        }

        // Several OCCT edges between the same two faces that share a vertex form one chain, as on the F-rep side.
        var chains = new UnionFind(segments.Count);
        foreach (var group in segments.Select((s, i) => (s, i)).GroupBy(x => (x.s.A, x.s.B)))
        {
            var byVertex = new Dictionary<int, int>();
            foreach (var (s, i) in group)
                foreach (var v in new[] { s.V1, s.V2 })
                    if (byVertex.TryGetValue(v, out var other)) chains.Union(i, other); else byVertex[v] = i;
        }
        var edges = segments.Select((s, i) => (s, root: chains.Find(i))).GroupBy(x => x.root).OrderBy(g => g.Key)
            .Select((g, i) => new TopoEdge(i, g.First().s.A, g.First().s.B)).ToList();

        var bodyIndex = new Dictionary<int, int>();
        var faces = Enumerable.Range(0, faceCount).Select(f =>
        {
            var root = bodies.Find(f);
            if (!bodyIndex.ContainsKey(root)) bodyIndex[root] = bodyIndex.Count;
            var sources = tags[f].Count > 0 ? tags[f].ToList() : ["?"]; // a face the history lost track of
            return new TopoFace(f, sources, bodyIndex[root]);
        }).ToList();

        var vertices = Enumerable.Range(0, part.VertexCount)
            .Select(v => part.VertexFaces(v))
            .Where(fs => fs.Length >= 3)
            .Select((fs, i) => new TopoVertex(i, fs.Order().ToList()))
            .ToList();

        return new Topology(faces, edges, vertices, bodyIndex.Count);
    }

    static OcctShape BlockShape(Block b) =>
        OcctShape.Box(b.Origin[0], b.Origin[1], b.Origin[2], b.Origin[0] + b.Size[0], b.Origin[1] + b.Size[1], b.Origin[2] + b.Size[2]);

    // Box faces are named by their outward normal, the same names the F-rep compiler uses.
    static List<SortedSet<string>> TagBlock(OcctShape box, string id) =>
        Enumerable.Range(0, box.FaceCount).Select(f =>
        {
            var n = box.Face(f).Data[3..6];
            var axis = Array.FindIndex(n, x => Math.Abs(x) > 0.5);
            return Tag($"{id}/{"xyz"[axis]}{(n[axis] > 0 ? "+" : "-")}");
        }).ToList();

    static bool OnPlane(OcctFace face, double[] point, double[] normal)
    {
        if (face.Kind != OcctFaceKind.Plane) return false;
        var d = face.Data;
        var parallel = Math.Abs(Math.Abs(d[3] * normal[0] + d[4] * normal[1] + d[5] * normal[2]) - 1) < 1e-9;
        var offset = (d[0] - point[0]) * normal[0] + (d[1] - point[1]) * normal[1] + (d[2] - point[2]) * normal[2];
        return parallel && Math.Abs(offset) < 1e-9;
    }

    static SortedSet<string> Tag(string source) => new(StringComparer.Ordinal) { source };

    static double[] Unit(double[] v)
    {
        var len = Math.Sqrt(v.Sum(x => x * x));
        return v.Select(x => x / len).ToArray();
    }

    sealed record Box(double[] Min, double[] Max)
    {
        public double Diagonal => Math.Sqrt(Enumerable.Range(0, 3).Sum(i => (Max[i] - Min[i]) * (Max[i] - Min[i])));
    }

    static Box Bounds(PartDocument doc)
    {
        var blocks = doc.Features.OfType<Block>().Where(b => b.Op != BooleanOp.Cut).ToList();
        return new Box(
            Enumerable.Range(0, 3).Select(i => blocks.Min(b => b.Origin[i])).ToArray(),
            Enumerable.Range(0, 3).Select(i => blocks.Max(b => b.Origin[i] + b.Size[i])).ToArray());
    }
}

public sealed class OcctResult(IReadOnlyList<Diagnostic> diagnostics, Topology? topology, OcctShape? shape)
    : ModelResult(diagnostics, topology)
{
    public OcctShape? Shape { get; } = shape;

    public override int? FaceNear(string source, double x, double y, double z) =>
        Topology?.Faces.Where(f => f.Sources.Contains(source))
            .OrderBy(f => Shape!.FaceDistance(f.Index, x, y, z))
            .Select(f => (int?)f.Index)
            .FirstOrDefault();
}
