using Isoform.Document;
using Isoform.Modeling;
using Isoform.Step;

namespace Isoform.Brep.Modeling;

/// <summary>
/// The exact B-rep of a part for STEP: the managed kernel's own, made from exact geometry, where it builds the whole
/// part and its faces, names and neighbours are those of the F-rep core's result; else that result's exact B-rep
/// (<see cref="ExactBrep"/>), as before. The kernel's solids take the names the F-rep core gives its bodies.
/// </summary>
public static class ExactStep
{
    public static (Step.Brep Brep, bool Kernel) Of(PartDocument document, FrepResult result, ModelingSettings settings) =>
        Kernel(document, result, settings) is { } brep ? (brep, true) : (ExactBrep.Build(result, settings), false);

    /// <summary>An assembly's part as it was regenerated for the instance (its parameters applied).</summary>
    public static Step.Brep Of(Instance instance, ModelingSettings settings) =>
        instance.Document is { } document ? Of(document, instance.Result, settings).Brep : ExactBrep.Build(instance.Result, settings);

    static Step.Brep? Kernel(PartDocument document, FrepResult result, ModelingSettings settings)
    {
        if (result.Topology is not { } frep) return null;
        BrepResult kernel;
        try
        {
            kernel = new BrepModeler(settings).Regenerate(document);
        }
        // The F-rep core's export stands behind it: a kernel that fails, however, only means that one is taken.
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException or BooleanFailure)
        {
            return null;
        }
        if (kernel.Diagnostics.Count > 0 || kernel.Topology is not { } mine || !Describe(mine).SequenceEqual(Describe(frep))) return null;

        // Each solid named after the F-rep core's body that has its first face.
        var brep = StepConversion.ToStep(kernel.Bodies);
        string NameOf(BrepSolid solid)
        {
            var sources = solid.Faces[0].Sources.Order(StringComparer.Ordinal).ToList();
            var face = frep.Faces.First(f => f.Sources.Order(StringComparer.Ordinal).SequenceEqual(sources));
            return result.Bodies.FirstOrDefault(b => b.Faces.Contains(face.Index))?.Name ?? solid.Name;
        }
        return brep with { Solids = [.. brep.Solids.Select(s => s with { Name = NameOf(s) })] };
    }

    static List<string> Describe(Topology t) =>
        [.. t.Faces.Select(f => $"{string.Join("+", f.Sources.Order(StringComparer.Ordinal))} | {string.Join(",", t.Signature(f.Index))}").Order(StringComparer.Ordinal)];
}
