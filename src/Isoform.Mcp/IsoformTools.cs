using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Isoform.Document;
using Isoform.Interactive;
using Isoform.Modeling;
using Isoform.Step;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Isoform.Mcp;

/// <summary>
/// The kernel as MCP tools. Each tool is a pure function from its arguments to one compact JSON object: summaries,
/// names and numbers an agent can act on, never meshes. Bad input comes back as a tool error with the kernel's message.
/// The work is <see cref="AgentTools"/>'s, which the playground's chat shares; here are the MCP names and descriptions.
/// </summary>
[McpServerToolType]
public static class IsoformTools
{
    const string DocumentHelp = "A part document (format isoform/0): its JSON text, or a path to a .json file. See describe_format.";
    const string CellHelp = "Finest cell size in mm (default 0.5). Walls, gaps and holes under 2 × cellSize are meshed with cells up to 8 times finer where they are; only what is smaller still is reported as below-resolution.";
    const string ChordHelp = "Largest distance in mm the mesh may stray from a curved face (default 0.01).";

    /// <summary>How tool results are written: one line, and + or ≥ as themselves (face names are full of + signs).</summary>
    public static readonly JsonSerializerOptions Compact = AgentTools.Compact;

    [McpServerTool(Name = "regenerate", ReadOnly = true, Idempotent = true)]
    [Description("Regenerates a part: diagnostics, every face by its stable source name per body, edge and vertex counts, " +
                 "bounding box, and mass properties per body (volume, area, centroid, inertia about the centroid). Timing in ms.")]
    public static string Regenerate(
        [Description(DocumentHelp)] string document,
        [Description(CellHelp)] double cellSize = 0.5,
        [Description(ChordHelp)] double chordTolerance = 0.01,
        [Description("Mass per mm³; when given, mass and inertia are scaled by it (default: per unit density).")] double? density = null,
        [Description("Also give a certified [lower, upper] range of each body's volume (slower).")] bool certifyVolume = false) =>
        Run(() => AgentTools.Regenerate(document, cellSize, chordTolerance, density, certifyVolume));

    [McpServerTool(Name = "apply_edits", Idempotent = true)]
    [Description("Applies edits to a part, assembly or bundle and returns the new document (compact JSON, same content and " +
                 "hash as the canonical text), its hash and a regeneration summary; for a part also reference stability: " +
                 "which face names appeared, disappeared or changed (split, merged) against the old document. " +
                 "Part edits: {\"set\": \"hole.diameter\", \"value\": 12}, {\"set\": \"parameters.width\", \"value\": 120}, " +
                 "{\"insert\": {feature}, \"before\": \"fillet\"}, {\"remove\": \"slot\"}. Assembly edits address " +
                 "components.<id>... and mates.<id>...; in a bundle, \"document\": \"<path>\" on an edit changes that part. " +
                 "An edit that cannot be applied is a tool error naming it, and nothing is changed.")]
    public static string ApplyEdits(
        [Description("A part, assembly or bundle: JSON text or a path. An assembly is regenerated with the parts in its " +
                     "folder, so given as text it gets no summary.")] string document,
        [Description("Edits applied in order. set: \"<featureId>.<field>[.<field or index>...]\" with a JSON value; " +
                     "insert: a feature object, before an id (or appended); remove: a feature id. See describe_format.")] JsonElement[] edits,
        [Description("Regenerate for the summary and, for a part, reference stability (default true).")] bool regenerate = true,
        [Description("Write the new document here as canonical text (may be the input path) instead of returning it.")] string? outputPath = null,
        [Description(CellHelp)] double cellSize = 0.5,
        [Description(ChordHelp)] double chordTolerance = 0.01) =>
        Run(() => AgentTools.ApplyEdits(document, AgentTools.Edits(edits), regenerate, outputPath, cellSize, chordTolerance));

    [McpServerTool(Name = "query", ReadOnly = true, Idempotent = true)]
    [Description("Geometric queries on a part, several per call: ray (first boundary hit: point, distance, face, normal), " +
                 "distance (certified [lower, upper] to the boundary, closest point, face, inside), clearance (certified gap or " +
                 "interference between two bodies of the part) and wall (minimum wall thickness, as a caliper square to the face). " +
                 "A query the kernel cannot settle is answered {\"kind\", \"error\"}; the others still stand.")]
    public static string Query(
        [Description(DocumentHelp)] string document,
        [Description("The queries, answered in order.")] AgentTools.QuerySpec[] queries,
        [Description(CellHelp)] double cellSize = 0.5,
        [Description(ChordHelp)] double chordTolerance = 0.01) =>
        Run(() => AgentTools.Query(document, queries, cellSize, chordTolerance));

    [McpServerTool(Name = "sketch", ReadOnly = true, Idempotent = true)]
    [Description("Solves one sketch of a part: every point [u,v] and circle radius, the degrees of freedom left and the points " +
                 "and circles that can still move, the problems (sketch-conflict naming the constraints that cannot all hold, " +
                 "sketch-redundant, sketch-invalid), and its regions (entity ids round each outer loop and its holes). Also the " +
                 "document with every sketch's stored points moved to its solution (text and hash): after editing a sketch, " +
                 "continue from that text. dragPoint and dragTo move a point towards [u,v] first, as a user dragging it.")]
    public static string Sketch(
        [Description(DocumentHelp)] string document,
        [Description("The sketch's feature id.")] string id,
        [Description("A point to drag, by name.")] string? dragPoint = null,
        [Description("Where to drag it, [u, v] in sketch coordinates.")] double[]? dragTo = null) =>
        Run(() => SketchJson.Solve(AgentTools.Text(document), id, dragPoint, dragTo));

    [McpServerTool(Name = "sketch_edit", ReadOnly = true, Idempotent = true)]
    [Description("Fillets, trims or projects into one sketch of a part; the sketch stays lines and arcs. fillet rounds the corner point where " +
                 "two lines or arcs meet with a tangent arc of radius (the corner stays as a construction point, so dimensions " +
                 "to it hold). trim deletes the piece of entity, between its crossings with the other entities, nearest [x, y] " +
                 "in sketch coordinates; a trimmed circle becomes an arc. project adds the part edge between two faces (as the " +
                 "part is where the sketch stands) as exact lines, arcs and circles cut to the edge, or with near only its piece " +
                 "nearest that world point; linked makes them construction that follows the edge at every regeneration " +
                 "(sketch-reference-lost when it cannot). An edge that projects to an ellipse is refused. " +
                 "Returns what sketch returns for the edited document " +
                 "(settled.text is the new document: continue from it), plus added (the new point and entity ids) and dropped " +
                 "(the constraints that went with a deleted piece). An edit that cannot be made is a tool error saying why.")]
    public static string SketchEdit(
        [Description(DocumentHelp)] string document,
        [Description("The sketch's feature id.")] string id,
        [Description("fillet | trim | project")] string op,
        [Description("fillet: the corner point, by name.")] string? corner = null,
        [Description("fillet: the radius in mm.")] double? radius = null,
        [Description("trim: the line, arc or circle to trim, by id.")] string? entity = null,
        [Description("trim: u of a point near the piece to delete, in sketch coordinates.")] double? x = null,
        [Description("trim: v of that point.")] double? y = null,
        [Description("project: the edge's two faces by name, e.g. [\"plate/z+\", \"plate/x+\"].")] string[]? faces = null,
        [Description("project: a world point [x, y, z] near the piece of the edge to take; all pieces without it.")] double[]? near = null,
        [Description("project: keep the link, so the entities follow the edge (default false: a copy).")] bool linked = false) =>
        Run(() => SketchJson.Edit(AgentTools.Text(document), id, op, corner, radius, entity, x, y, faces, near, linked));

    [McpServerTool(Name = "validate", ReadOnly = true, Idempotent = true)]
    [Description("Fast check of a part without meshing: parses it, applies the document rules and compiles it. " +
                 "Returns ok, the hash, the bodies and the diagnostics. Parse errors come back as a diagnostic too.")]
    public static string Validate([Description(DocumentHelp)] string document) =>
        Run(() => AgentTools.Validate(document));

    [McpServerTool(Name = "export_step")]
    [Description("Exports a part, assembly or bundle as exact STEP (AP214): analytic faces (fillets, chamfers, loop fillets, " +
                 "shell cavities as voids), closed-form edges and vertices, one solid per body; an assembly as one product per " +
                 "distinct part, placed by its instances. Writes to outputPath, or returns the STEP text. Faces without a closed " +
                 "form (smooth blends, lattices) are named in 'unsupported'.")]
    public static string ExportStep(
        [Description(DocumentHelp + " Or an assembly or bundle, as for the assembly tool.")] string document,
        [Description("Write the STEP file here; without it the STEP text is returned.")] string? outputPath = null,
        [Description("Product name in the file (default: part, or assembly).")] string? name = null,
        [Description(CellHelp)] double cellSize = 0.5,
        [Description(ChordHelp)] double chordTolerance = 0.01) => Run(() =>
    {
        var settings = AgentTools.Settings(cellSize, chordTolerance);
        var text = AgentTools.Text(document);
        var o = new JsonObject();
        string step;
        try
        {
            if (CanonicalFormat.FormatOf(text) is Bundle.FormatVersion or AssemblyDocument.FormatVersion)
            {
                var (assembly, load) = AgentTools.Assembled(document, text) ?? throw new McpException(AgentTools.AssemblyTextHelp);
                var result = new AssemblyModeler(settings).Regenerate(assembly, load);
                o["diagnostics"] = AgentTools.Diagnostics(result.Diagnostics);
                step = StepWriter.Write(result, settings, name ?? "assembly");
                o["ok"] = true;
                o["instances"] = result.Instances.Count;
                o["parts"] = result.Instances.Select(i => i.Hash).Distinct().Count();
            }
            else
            {
                var result = new FrepModeler(settings).Regenerate(CanonicalFormat.Read(text));
                o["diagnostics"] = AgentTools.Diagnostics(result.Diagnostics);
                if (result.Topology is null)
                {
                    o["ok"] = false;
                    return o;
                }
                var brep = ExactBrep.Build(result, settings);
                step = StepWriter.Write(brep, name ?? "part");
                o["ok"] = true;
                o["solids"] = brep.Solids.Count;
                o["faces"] = brep.Solids.Sum(s => s.Faces.Count);
                o["edges"] = brep.Edges.Count;
                o["vertices"] = brep.Vertices.Count;
            }
        }
        // A face with no closed form, an instance without geometry, a mirrored placement: named, not a crash.
        catch (NotSupportedException e)
        {
            o["ok"] = false;
            o["unsupported"] = e.Message;
            return o;
        }
        if (outputPath is null) o["step"] = step;
        else
        {
            File.WriteAllText(outputPath, step);
            o["path"] = Path.GetFullPath(outputPath);
            o["bytes"] = new FileInfo(outputPath).Length;
        }
        return o;
    });

    [McpServerTool(Name = "assembly", ReadOnly = true, Idempotent = true)]
    [Description("Regenerates an assembly: instances with their solved placements, free degrees of freedom left by the mates, " +
                 "diagnostics, and certified clearances or interferences between instances whose boxes come close.")]
    public static string Assembly(
        [Description("A bundle (isoform-bundle/0: the assembly plus its documents) as JSON text or a path, or a path to an " +
                     "assembly document (isoform-assembly/0) whose parts are resolved relative to its folder.")] string document,
        [Description("Check clearance between instance pairs (default true).")] bool clearances = true,
        [Description("Only pairs whose boxes come within this many mm are checked (default 0: touching or overlapping boxes).")] double within = 0,
        [Description(CellHelp)] double cellSize = 0.5,
        [Description(ChordHelp)] double chordTolerance = 0.01) =>
        Run(() => AgentTools.Assembly(document, clearances, within, cellSize, chordTolerance));

    const string BuildCellHelp = "Cell size in mm for the design, the plate sections and the checks (default 1).";
    const string BudgetHelp = "Boxes each certified search may split before it stops with an honest interval (default 20000): more closes more bounds, and takes longer.";

    [McpServerTool(Name = "build", Idempotent = true)]
    [Description("Rationalises a design into exact plates and tubes by a build document (isoform-build/0, see describe_format) " +
                 "and writes everything into outputFolder: the bundle unpacked (assembly.json, the design, build.json and one " +
                 "document per distinct part), <design>.step (one product per distinct part, an occurrence per instance), a DXF " +
                 "per distinct plate, bom and cut-list (.csv, .md) and the verification report (report.json, report.md). " +
                 "Returns the verdict and each check's (pass, fail, unknown: a certified bound that does not close; reported, " +
                 "not checked), the build's diagnostics, the files and a markdown summary: checks, bill of materials, cut list. " +
                 "Takes seconds to a minute: the design is regenerated and every part searched.")]
    public static string Build(
        [Description("The design: " + DocumentHelp)] string design,
        [Description("The build document (isoform-build/0): its JSON text or a path to a .json file.")] string build,
        [Description("The folder to write into; created if need be, files of the same name replaced.")] string outputFolder,
        [Description(BuildCellHelp)] double cellSize = BuildTools.DefaultCellSize,
        [Description(BudgetHelp)] int budget = BuildTools.DefaultBudget) =>
        Run(() => BuildTools.Build(design, build, outputFolder, cellSize, budget));

    [McpServerTool(Name = "verify", Idempotent = true)]
    [Description("Checks any assembly of parts (a bundle, or an assembly file with its parts beside it, such as build writes) " +
                 "against the design it stands for: parts outside the design, design not covered per face, volumes and " +
                 "masses, contacts and interference, keep-outs, interfaces, minimum walls and diagnostics, every number with " +
                 "the faces it comes from. Returns the verdict and each check's, and the markdown report (with outputFolder: " +
                 "report.json and report.md written there, and the report's head returned). Fitted edges are only checked by build.")]
    public static string Verify(
        [Description("A bundle (isoform-bundle/0) as JSON text or a path, or a path to an assembly document whose parts are beside it.")] string bundle,
        [Description("The design: " + DocumentHelp + " Default: the design the build document names, from the bundle or its folder.")] string? design = null,
        [Description("The build document (tolerance, material, keep-outs, interfaces): JSON text or a path. Default: the bundle's own, " +
                     "or build.json beside the assembly; without one, the default tolerance and no keep-outs or interfaces.")] string? build = null,
        [Description("Write report.json and report.md here.")] string? outputFolder = null,
        [Description(BuildCellHelp)] double cellSize = BuildTools.DefaultCellSize,
        [Description(BudgetHelp)] int budget = BuildTools.DefaultBudget) =>
        Run(() => BuildTools.Verify(bundle, design, build, outputFolder, cellSize, budget));

    [McpServerTool(Name = "describe_format", ReadOnly = true, Idempotent = true)]
    [Description("The document format in brief: every feature type with a JSON example and the face names it makes, face naming " +
                 "rules, bodies, edits, assemblies and diagnostic codes. Read this before writing a document.")]
    public static string DescribeFormat() => FormatGuide.Text;

    // Errors an agent can fix come back as tool errors carrying the kernel's message.
    static string Run(Func<JsonObject> tool)
    {
        try
        {
            return tool().ToJsonString(Compact);
        }
        catch (Exception e) when (e is not (McpException or OperationCanceledException))
        {
            throw new McpException(AgentTools.Message(e), e);
        }
    }
}
