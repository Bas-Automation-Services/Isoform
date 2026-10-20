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

        var mesh = DualContouring.Build(part.Field, part.Min, part.Max, Settings.CellSize, Settings.ChordTolerance);
        var built = TopologyBuilder.Build(mesh, part.Surfaces, Settings.Tolerance);
        var topology = built.Topology;

        var diagnostics = part.Diagnostics.ToList();
        if (topology.BodyCount > 1)
            diagnostics.Add(new Diagnostic(Diagnostic.MultipleBodies, null, $"the part falls apart into {topology.BodyCount} bodies"));

        diagnostics.AddRange(Resolution(document, part, mesh, diagnostics));

        var present = topology.Faces.SelectMany(f => f.Sources).ToHashSet();
        var unresolved = diagnostics.Where(d => d.Code == Diagnostic.BelowResolution).Select(d => d.FeatureId).ToHashSet();
        // An offset moves faces that keep their names; it has none of its own to look for.
        foreach (var feature in document.Features.Where(f => f is not (Fillet or Offset) && !unresolved.Contains(f.Id)))
            if (!part.Surfaces.Any(s => s.Source.StartsWith(feature.Id + "/") && present.Contains(s.Source)))
                diagnostics.Add(new Diagnostic(Diagnostic.NoEffect, feature.Id, $"{feature.Id} does not touch the part"));

        return new FrepResult(diagnostics, topology, part, mesh, built.FaceOfQuad);
    }

    /// <summary>
    /// Features the cells cannot resolve: holes, shells and fillets below the guaranteed size, and every pair of
    /// surfaces the mesher found with a thinner wall or gap between them. A thin spot at a tangent contact is
    /// already reported as such.
    /// </summary>
    IEnumerable<Diagnostic> Resolution(PartDocument document, CompiledPart part, Mesh mesh, IReadOnlyList<Diagnostic> earlier)
    {
        var smallest = 2 * Settings.CellSize;
        foreach (var feature in document.Features)
        {
            var size = feature switch
            {
                Hole x => x.Diameter,
                Shell x => x.Thickness,
                Fillet x => 2 * x.Radius,
                Chamfer x => Math.Sqrt(2) * x.Distance,
                Block { Blend: > 0 } x => x.Blend,
                Extrude { Blend: > 0 } x => x.Blend,
                Revolve { Blend: > 0 } x => x.Blend,
                _ => double.PositiveInfinity,
            };
            if (size < smallest)
                yield return new Diagnostic(Diagnostic.BelowResolution, feature.Id, $"{feature.Id} ({size:G4} mm) is below the resolved size of {smallest:G4} mm; use a smaller CellSize");
            // A profile edge sweeps a face as wide as the edge is long.
            var profile = feature switch { Extrude e => e.Profile, Revolve r => r.Profile, _ => null };
            if (profile is not null && Profiles.CounterClockwise(profile).FirstOrDefault(x => Width(x.Edge) < smallest) is { Id: { } edge } narrow)
                yield return new Diagnostic(Diagnostic.BelowResolution, feature.Id, $"{feature.Id}/{edge} ({Width(narrow.Edge):G4} mm) is below the resolved size of {smallest:G4} mm; use a smaller CellSize");
        }

        static double Width(ProfileEdge2D e) => e.IsArc
            ? Math.Min(2 * e.R, e.R * Math.Abs(e.Sweep))
            : Math.Sqrt((e.U1 - e.U0) * (e.U1 - e.U0) + (e.V1 - e.V0) * (e.V1 - e.V0));

        var tangent = earlier.Where(d => d.Code == Diagnostic.TangentContact).Select(d => d.FeatureId).ToHashSet();
        string FeatureOf(int owner) => part.Surfaces[owner].Source.Split('/')[0];
        foreach (var pair in mesh.ThinSpots.GroupBy(s => (s.OwnerA, s.OwnerB)))
        {
            var (a, b) = (FeatureOf(pair.Key.OwnerA), FeatureOf(pair.Key.OwnerB));
            if (tangent.Contains(a) || tangent.Contains(b)) continue;
            var thinnest = pair.MinBy(s => s.Thickness);
            var p = thinnest.Location;
            yield return new Diagnostic(Diagnostic.BelowResolution, b,
                $"{part.Surfaces[pair.Key.OwnerA].Source} and {part.Surfaces[pair.Key.OwnerB].Source} are {thinnest.Thickness:G3} mm apart near ({p.X:F2}, {p.Y:F2}, {p.Z:F2}), below the resolved size of {smallest:G4} mm");
        }
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
