using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Geometry;
using Isoform.Meshing;

namespace Isoform.Modeling;

/// <summary>Regenerates a document on the implicit (F-rep/CSG) core.</summary>
public sealed class FrepModeler(ModelingSettings settings) : IModeler
{
    public ModelingSettings Settings { get; } = settings;

    ModelResult IModeler.Regenerate(PartDocument document) => Regenerate(document);

    public FrepResult Regenerate(PartDocument document)
    {
        var part = new Compiler(Settings).Compile(document);
        if (part.Field is null) return new FrepResult(part.Diagnostics, null, part, null, []);

        var mesh = DualContouring.Build(part.Field, part.Min, part.Max, Settings.CellSize);
        var built = TopologyBuilder.Build(mesh, part.Surfaces, Settings.Tolerance);
        var topology = built.Topology;

        var diagnostics = part.Diagnostics.ToList();
        if (topology.BodyCount > 1)
            diagnostics.Add(new Diagnostic(Diagnostic.MultipleBodies, null, $"the part falls apart into {topology.BodyCount} bodies"));

        var present = topology.Faces.SelectMany(f => f.Sources).ToHashSet();
        foreach (var feature in document.Features.Where(f => f is not Fillet))
            if (!part.Surfaces.Any(s => s.Source.StartsWith(feature.Id + "/") && present.Contains(s.Source)))
                diagnostics.Add(new Diagnostic(Diagnostic.NoEffect, feature.Id, $"{feature.Id} does not touch the part"));

        return new FrepResult(diagnostics, topology, part, mesh, built.FaceOfQuad);
    }
}

public sealed class FrepResult(IReadOnlyList<Diagnostic> diagnostics, Topology? topology, CompiledPart part, Mesh? mesh, int[] faceOfQuad)
    : ModelResult(diagnostics, topology)
{
    public CompiledPart Part { get; } = part;
    public Mesh? Mesh { get; } = mesh;
    public IReadOnlyList<int> FaceOfQuad { get; } = faceOfQuad;

    public override int? FaceNear(string source, double x, double y, double z)
    {
        if (Mesh is null) return null;
        var p = new Point3D(x, y, z);
        int? best = null;
        var bestDistance = double.MaxValue;
        for (var q = 0; q < Mesh.Quads.Count; q++)
        {
            var face = FaceOfQuad[q];
            if (!Topology!.Faces[face].Sources.Contains(source)) continue;
            var d = (Mesh.Vertices[Mesh.Quads[q].A] - p).LengthSquared;
            if (d < bestDistance) (best, bestDistance) = (face, d);
        }
        return best;
    }
}
