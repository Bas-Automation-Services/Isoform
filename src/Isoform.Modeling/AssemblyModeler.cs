using System.Collections.Immutable;
using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Geometry;
using Isoform.Meshing;

namespace Isoform.Modeling;

/// <summary>
/// One placed part of an assembly. The part is regenerated once in its own coordinates; the instance only moves
/// it. Its faces are named by occurrence: <c>left/plate/z+</c>, with the ids of the enclosing sub-assemblies first.
/// </summary>
public sealed record Instance(string Id, string Part, string Hash, Frame Placement, FrepResult Result)
{
    /// <summary>The part's solid where the assembly puts it; null if the part has no geometry.</summary>
    public Field? Field => Result.Part.Field is { } f ? new PlacedField(f, Placement) : null;

    public string Name(string source) => $"{Id}/{source}";
}

public sealed class AssemblyResult(IReadOnlyList<Instance> instances, IReadOnlyList<Diagnostic> diagnostics)
{
    public IReadOnlyList<Instance> Instances { get; } = instances;
    public IReadOnlyList<Diagnostic> Diagnostics { get; } = diagnostics;

    /// <summary>Every instance's mesh moved into place and laid end to end, with its first quad index.</summary>
    public (Mesh Mesh, IReadOnlyList<int> FirstQuad) WorldMesh()
    {
        var (vertices, quads, first) = (new List<Point3D>(), new List<Quad>(), new List<int>());
        foreach (var i in Instances)
        {
            first.Add(quads.Count);
            if (i.Result.Mesh is not { } mesh) continue;
            var v0 = vertices.Count;
            vertices.AddRange(mesh.Vertices.Select(i.Placement.ToWorld));
            quads.AddRange(mesh.Quads.Select(q => new Quad(q.A + v0, q.B + v0, q.C + v0, q.D + v0, q.Owner)));
        }
        return (new Mesh(vertices, quads), first);
    }
}

/// <summary>
/// Regenerates an assembly: resolves each component's document by its path, regenerates every distinct part once
/// (by content hash, so a hundred identical bolts cost one), and places it. Sub-assemblies nest their placements.
/// </summary>
public sealed class AssemblyModeler(ModelingSettings settings)
{
    /// <param name="load">The text of the document at a path, or null when there is none.</param>
    public AssemblyResult Regenerate(AssemblyDocument assembly, Func<string, string?> load)
    {
        var instances = new List<Instance>();
        var diagnostics = new List<Diagnostic>();
        var parts = new Dictionary<string, FrepResult>();
        var modeler = new FrepModeler(settings);

        void Add(AssemblyDocument a, Frame parent, string prefix, ImmutableStack<string> open)
        {
            foreach (var c in a.Components)
            {
                var id = prefix + c.Id;
                if (instances.Any(i => i.Id == id) || a.Components.Count(x => x.Id == c.Id) > 1)
                {
                    diagnostics.Add(new Diagnostic(Diagnostic.InvalidParameter, id, $"component id '{c.Id}' is used twice"));
                    continue;
                }
                if (load(c.Part) is not { } text)
                {
                    diagnostics.Add(new Diagnostic(Diagnostic.PartMissing, id, $"there is no document '{c.Part}'"));
                    continue;
                }
                var frame = FrameOf(c.Placement).Then(parent);
                if (CanonicalFormat.FormatOf(text) == AssemblyDocument.FormatVersion)
                {
                    if (open.Contains(c.Part))
                    {
                        diagnostics.Add(new Diagnostic(Diagnostic.InvalidParameter, id, $"'{c.Part}' contains itself"));
                        continue;
                    }
                    var sub = CanonicalFormat.ReadAssembly(text);
                    Pinned(c, CanonicalFormat.Hash(sub), id);
                    Add(sub, frame, id + "/", open.Push(c.Part));
                    continue;
                }
                var doc = CanonicalFormat.Read(text);
                var hash = CanonicalFormat.Hash(doc);
                Pinned(c, hash, id);
                if (!parts.TryGetValue(hash, out var result))
                {
                    result = parts[hash] = modeler.Regenerate(doc);
                    diagnostics.AddRange(result.Diagnostics.Select(d => d with { FeatureId = $"{c.Part}:{d.FeatureId}" }));
                }
                instances.Add(new Instance(id, c.Part, hash, frame, result));
            }
        }

        void Pinned(Component c, string hash, string id)
        {
            if (c.Hash is { } pinned && pinned != hash)
                diagnostics.Add(new Diagnostic(Diagnostic.PartChanged, id, $"'{c.Part}' changed since the assembly pinned it"));
        }

        Add(assembly, Frame.Identity, "", ImmutableStack<string>.Empty);
        return new AssemblyResult(instances, diagnostics);
    }

    public static Frame FrameOf(Placement p) =>
        Frame.FromXAndNormal(new Point3D(p.Origin[0], p.Origin[1], p.Origin[2]), new Vector3D(p.X[0], p.X[1], p.X[2]), new Vector3D(p.Normal[0], p.Normal[1], p.Normal[2]));
}
