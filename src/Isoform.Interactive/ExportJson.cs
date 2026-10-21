using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Isoform.Document;
using Isoform.Modeling;
using Isoform.Sketching;
using Isoform.Step;

namespace Isoform.Interactive;

/// <summary>
/// Files for the page to download: STEP of a slot's last good regeneration, and DXF of a sketch or of a flat face's
/// outline. Each answers <c>{text, …}</c> with the file's text, or <c>{error}</c>.
/// </summary>
public static class ExportJson
{
    static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// STEP (AP214) of a slot's last good regeneration, a part or a bundle's assembly, with <paramref name="name"/> as
    /// its product name: <c>{text, solids, faces}</c> for a part, <c>{text, instances}</c> for an assembly. The exact
    /// B-rep takes its combinatorics from the regeneration's mesh, so it is made with the settings that mesh was made
    /// with. Faces with no exact form stop the export: <c>{error, unsupported: [face names]}</c>.
    /// </summary>
    public static string Step(Regeneration? r, string name) => Exported(r, (view, settings) =>
    {
        if (view.Assembly is { } assembly)
            return new JsonObject { ["text"] = StepWriter.Write(assembly, settings, name), ["instances"] = assembly.Instances.Count };
        var brep = ExactBrep.Build(view.Part!, settings);
        return new JsonObject { ["text"] = StepWriter.Write(brep, name), ["solids"] = brep.Solids.Count, ["faces"] = brep.Solids.Sum(s => s.Faces.Count) };
    });

    /// <summary>
    /// DXF R12 (<see cref="DxfWriter"/>): with <paramref name="sketch"/>, that sketch of the part document solved, in its
    /// (u, v) coordinates; else the exact outline of the flat face <paramref name="face"/> of a slot's last good
    /// regeneration, in the plane a sketch on that face takes. <c>{text, entities, approximated}</c>, approximated
    /// counting the edges written as lines (not lines or circles); or <c>{error}</c>.
    /// </summary>
    public static string Dxf(Regeneration? r, string document, string sketch, string face)
    {
        if (sketch.Length > 0)
        {
            try
            {
                var s = SketchDocuments.Resolved(CanonicalFormat.Read(document), sketch);
                var solution = SketchSolver.Solve(s);
                if (!solution.Solved) return Error($"sketch {sketch} does not solve: {string.Join("; ", solution.Problems.Select(p => p.Message))}");
                return Written(DxfWriter.Sketch(s, solution), 0).ToJsonString(Compact);
            }
            catch (Exception e) when (Session.Unreadable(e) || e is ArgumentException)
            {
                return Error(e.Message);
            }
        }
        return Exported(r, (view, settings) =>
        {
            if (view.Part is not { } part) throw new NotSupportedException("a face outline is of a part, not an assembly");
            var (entities, approximated) = DxfWriter.Face(part, ExactBrep.Build(part, settings), face, settings.ChordTolerance)
                ?? throw new NotSupportedException($"{face} is not a flat face of the part");
            return Written(entities, approximated);
        });
    }

    static JsonObject Written(List<DxfEntity> entities, int approximated) =>
        new() { ["text"] = DxfWriter.Write(entities), ["entities"] = entities.Count, ["approximated"] = approximated };

    /// <summary>An export of a slot's regeneration; what the exact B-rep cannot do comes back as an error.</summary>
    static string Exported(Regeneration? r, Func<View, ModelingSettings, JsonObject> export)
    {
        if (r is not { View: { } view, Settings: { } settings }) return Error("nothing has regenerated yet");
        if (view.Part is { Topology: null }) return Error("the part has no geometry");
        try
        {
            return export(view, settings).ToJsonString(Compact);
        }
        catch (UnsupportedFacesException e)
        {
            return new JsonObject { ["error"] = e.Message, ["unsupported"] = new JsonArray([.. e.Faces.Select(f => (JsonNode)f)]) }.ToJsonString(Compact);
        }
        catch (NotSupportedException e)
        {
            return Error(e.Message);
        }
    }

    static string Error(string message) => new JsonObject { ["error"] = message }.ToJsonString(Compact);
}
