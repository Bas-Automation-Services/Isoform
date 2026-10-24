using Isoform.Document;
using Isoform.Modeling;

namespace Isoform.Build;

/// <summary>
/// Runs a build document on a design: each section in order, by its method, against the design minus the hulls of the
/// parts before it (section order is priority), then packs the parts into a bundle: the design, the build document and
/// one document per distinct part, each instance a component pinned by hash.
/// </summary>
public sealed class Rationaliser(IBuildMethod plates, IBuildMethod tubes)
{
    /// <summary>Plates by <see cref="PlateMethod"/> (sections of <paramref name="cellSize"/> mm cells) and tubes by <see cref="TubeMethod"/>.</summary>
    public static Rationaliser PlatesAndTubes(double cellSize = 1) => new(new PlateMethod(cellSize), new TubeMethod());

    /// <param name="design">The design part; it goes into the bundle under the build's <see cref="BuildDocument.Design"/> path.</param>
    /// <param name="regenerated">The design regenerated: its faces bind the build's names.</param>
    /// <param name="buildPath">Where the build document goes in the bundle.</param>
    public BuildResult Run(BuildDocument build, PartDocument design, FrepResult regenerated, string buildPath = "build.json")
    {
        var keepouts = build.Keepouts ?? [];
        var model = new Design(design, regenerated, keepouts.Select(k => k.Body));
        var diagnostics = new List<Diagnostic>();
        var bodies = regenerated.Part.Bodies.Select(b => b.Name).ToHashSet();
        foreach (var k in keepouts.Where(k => !bodies.Contains(k.Body)))
            diagnostics.Add(new(BuildDiagnostic.FaceMissing, null, $"the design has no body '{k.Body}' to keep out"));
        foreach (var name in (build.Interfaces ?? []).Where(n => model.Faces(n).Count == 0))
            diagnostics.Add(new(BuildDiagnostic.FaceMissing, null, $"the design has no face '{name}' for an interface"));

        var built = new List<(BuildSection Section, BuiltPart Part)>();
        foreach (var section in build.Sections)
        {
            var (method, face) = section switch
            {
                PlateSection p => (plates, p.Face),
                TubeSection t => (tubes, t.Along),
                _ => throw new NotSupportedException(section.GetType().Name),
            };
            if (face is not null && model.Faces(face).Count == 0)
            {
                diagnostics.Add(new(BuildDiagnostic.FaceMissing, section.Id, $"the design has no face '{face}' for section '{section.Id}'"));
                continue;
            }
            var result = method.Build(section, new BuildContext(build, model, [.. built.Select(b => b.Part)]));
            diagnostics.AddRange(result.Diagnostics);
            if (result.Parts.Count == 0 && result.Diagnostics.Count == 0)
                diagnostics.Add(new(BuildDiagnostic.RegionEmpty, section.Id, $"section '{section.Id}' gave no part: nothing of the design is left there"));
            built.AddRange(result.Parts.Select(p => (section, p)));
        }
        return Pack(build, design, buildPath, built, diagnostics);
    }

    // Distinct parts by congruence (SameParts), in the order they first appear, each instance placed so the part written
    // first lands where it goes; an instance is named by its section, numbered when the section made several.
    static BuildResult Pack(BuildDocument build, PartDocument design, string buildPath, List<(BuildSection Section, BuiltPart Part)> built, List<Diagnostic> diagnostics)
    {
        var documents = new Dictionary<string, string> { [build.Design] = CanonicalFormat.Write(design), [buildPath] = CanonicalFormat.Write(build) };
        var parts = new Dictionary<string, PartDocument>();
        var (components, instances) = (new List<Component>(), new List<BuildInstance>());
        foreach (var group in built.GroupBy(b => b.Section))
        {
            var members = group.ToList();
            for (var i = 0; i < members.Count; i++)
            {
                var part = members[i].Part;
                var (path, placement) = parts.Select(p => (p.Key, At: SameParts.Match(part.Document, part.Placement, p.Value))).FirstOrDefault(p => p.At is not null);
                if (placement is null)
                {
                    path = Enumerable.Range(1, int.MaxValue).Select(n => n == 1 ? $"{part.Name}.json" : $"{part.Name}-{n}.json").First(p => !documents.ContainsKey(p));
                    (documents[path], parts[path], placement) = (CanonicalFormat.Write(part.Document), part.Document, part.Placement);
                }
                var id = members.Count == 1 ? group.Key.Id : $"{group.Key.Id}-{i + 1}";
                components.Add(new Component(id, path!, placement, CanonicalFormat.Hash(parts[path!])));
                instances.Add(new BuildInstance(id, group.Key.Id, path!, part));
            }
        }
        return new BuildResult(new Bundle(new AssemblyDocument(components), documents), parts, instances, diagnostics);
    }
}

/// <summary>
/// A placed part of the build: its component id in the bundle, the section that made it, the path of the document it
/// is placed as (a congruent part's, written once), and the part as its method made it (own document and placement,
/// hull, fits).
/// </summary>
public sealed record BuildInstance(string Id, string Section, string Part, BuiltPart Built);

/// <summary>
/// What a build gives: the bundle (assembly of the parts, with the design, the build document and every distinct part
/// in canonical form), the distinct parts by path, the instances and the build's diagnostics. The same inputs give the
/// same bundle, byte for byte.
/// </summary>
public sealed record BuildResult(Bundle Bundle, IReadOnlyDictionary<string, PartDocument> Parts, IReadOnlyList<BuildInstance> Instances,
    IReadOnlyList<Diagnostic> Diagnostics);
