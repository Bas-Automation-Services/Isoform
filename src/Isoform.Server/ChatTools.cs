using System.Text.Json;
using System.Text.Json.Nodes;
using Isoform.Document;
using Isoform.Interactive;

namespace Isoform.Server;

/// <summary>
/// The chat agent's tools: the MCP tool set (<see cref="AgentTools"/>, <see cref="SketchJson"/>) on one working
/// document instead of a document argument. The read-only tools look at the working text; <c>apply_edits</c>,
/// <c>sketch</c> and <c>sketch_edit</c> change it, and their edits are kept, in order, for the page to commit as one history entry.
/// </summary>
public sealed class ChatTools(string document)
{
    /// <summary>The working document: the page's document with this turn's edits applied, as canonical text.</summary>
    public string Text { get; private set; } = DocumentEdits.Apply(document, []);

    /// <summary>The edits made so far, in order: applied to the page's document they give <see cref="Text"/>.</summary>
    public List<JsonNode> Edits { get; } = [];

    /// <summary>The hash of <see cref="Text"/>, as the page computes it for a committed document.</summary>
    public string Hash => DocumentEdits.TryApply(Text, []).Hash!;

    /// <summary>Each tool's name, description and input schema (JSON Schema properties and required).</summary>
    public static IReadOnlyList<(string Name, string Description, string Schema)> Definitions { get; } =
    [
        ("apply_edits",
         "Applies edits to the document, all or nothing, and returns the new document (compact), its hash, a regeneration " +
         "summary and which face names appeared, disappeared or changed. This is how you change the document; the page " +
         "commits your edits as one history entry when your turn ends. Edits: {\"set\": \"hole.diameter\", \"value\": 12}, " +
         "{\"set\": \"parameters.width\", \"value\": 120}, {\"insert\": {feature}, \"before\": \"fillet\"} (appended without " +
         "before), {\"remove\": \"slot\"}. To add to a sketch's points, entities or constraints, set the whole object or list. " +
         "An edit that cannot be applied is an error naming it, and nothing changes.",
         """
         {"properties": {
           "edits": {"type": "array", "items": {"type": "object"}, "description": "Edits applied in order."},
           "regenerate": {"type": "boolean", "description": "Regenerate for the summary and reference stability (default true)."}},
          "required": ["edits"]}
         """),
        ("sketch",
         "Solves one sketch of the document: every point [u,v] and circle radius, the degrees of freedom left and the points " +
         "and circles that can still move, problems (sketch-conflict naming the constraints that cannot all hold, " +
         "sketch-redundant, sketch-invalid) and its regions (entity ids round each outer loop and its holes). Call it after " +
         "adding or changing a sketch's entities or constraints. With settle (default true) a solved sketch's stored points " +
         "move to the solution, as edits like apply_edits's. dragPoint and dragTo move a point towards [u,v] first, as a " +
         "user dragging it.",
         """
         {"properties": {
           "id": {"type": "string", "description": "The sketch's feature id."},
           "dragPoint": {"type": "string", "description": "A point to drag, by name."},
           "dragTo": {"type": "array", "items": {"type": "number"}, "description": "Where to drag it, [u, v] in sketch coordinates."},
           "settle": {"type": "boolean", "description": "Store the solution in the document (default true)."}},
          "required": ["id"]}
         """),
        ("sketch_edit",
         "Fillets, trims or projects into one sketch of the document; it stays lines and arcs. fillet rounds the corner point where two " +
         "lines or arcs meet with a tangent arc of radius (the corner stays as a construction point, so dimensions to it " +
         "hold). trim deletes the piece of entity, between its crossings with the other entities, nearest [x, y] in sketch " +
         "coordinates; a trimmed circle becomes an arc. project adds the part edge between two faces as exact lines, arcs " +
         "and circles cut to the edge (near: only the piece nearest a world point); linked makes them construction that " +
         "follows the edge at every regeneration. An edge that projects to an ellipse is refused. " +
         "Returns the sketch solved as sketch does, added (the new point and " +
         "entity ids) and dropped (constraints that went with a deleted piece: tell the user). The edit is stored in the " +
         "document like apply_edits's. One that cannot be made is an error saying why, and nothing changes.",
         """
         {"properties": {
           "id": {"type": "string", "description": "The sketch's feature id."},
           "op": {"type": "string", "enum": ["fillet", "trim", "project"]},
           "corner": {"type": "string", "description": "fillet: the corner point, by name."},
           "radius": {"type": "number", "description": "fillet: the radius in mm."},
           "entity": {"type": "string", "description": "trim: the line, arc or circle to trim, by id."},
           "x": {"type": "number", "description": "trim: u of a point near the piece to delete."},
           "y": {"type": "number", "description": "trim: v of a point near the piece to delete."},
           "faces": {"type": "array", "items": {"type": "string"}, "description": "project: the edge's two faces by name."},
           "near": {"type": "array", "items": {"type": "number"}, "description": "project: a world point [x, y, z] near the piece to take."},
           "linked": {"type": "boolean", "description": "project: follow the edge at every regeneration (default false: a copy)."}},
          "required": ["id", "op"]}
         """),
        ("regenerate",
         "Regenerates the document: diagnostics, every face by its stable source name per body, edge and vertex counts, " +
         "bounding box, and mass properties per body (volume, area, centroid, inertia). Use it to learn face names and to " +
         "check a part.",
         """
         {"properties": {
           "cellSize": {"type": "number", "description": "Finest cell size in mm (default 0.5); features under 2 x cellSize are below-resolution."},
           "density": {"type": "number", "description": "Mass per mm3; when given, mass and inertia are scaled by it."}}}
         """),
        ("validate",
         "Fast check of the document without meshing: parses it, applies the document rules and compiles it. Returns ok, " +
         "the hash, the bodies and the diagnostics.",
         """{"properties": {}}"""),
        ("query",
         "Geometric queries on the part, several per call: ray (first boundary hit: point, distance, face, normal), distance " +
         "(certified [lower, upper] to the boundary, closest point, face, inside), clearance (certified gap or interference " +
         "between two bodies) and wall (minimum wall thickness).",
         """
         {"properties": {
           "queries": {"type": "array", "description": "The queries, answered in order.", "items": {"type": "object", "properties": {
             "kind": {"type": "string", "enum": ["ray", "distance", "clearance", "wall"]},
             "origin": {"type": "array", "items": {"type": "number"}, "description": "ray: start point [x, y, z] in mm."},
             "direction": {"type": "array", "items": {"type": "number"}, "description": "ray: direction [x, y, z]."},
             "point": {"type": "array", "items": {"type": "number"}, "description": "distance: the point [x, y, z] in mm."},
             "body": {"type": "string", "description": "ray, distance: this body only."},
             "a": {"type": "string", "description": "clearance: first body."},
             "b": {"type": "string", "description": "clearance: second body."}},
             "required": ["kind"]}}},
          "required": ["queries"]}
         """),
        ("assembly",
         "Regenerates the document when it is a bundle (an assembly with its parts): instances with their solved placements, " +
         "free degrees of freedom left by the mates, diagnostics, and clearances or interferences between instances.",
         """{"properties": {"clearances": {"type": "boolean", "description": "Check clearance between instance pairs (default true)."}}}"""),
    ];

    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>A tool's result as compact JSON. A call that fails throws, with the message for the model.</summary>
    public string Run(string name, IReadOnlyDictionary<string, JsonElement> input)
    {
        JsonElement? Arg(string key) => input.TryGetValue(key, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;
        var result = name switch
        {
            "apply_edits" => ApplyEdits([.. AgentTools.Edits((Arg("edits") ?? throw new ToolException("edits is required")).EnumerateArray())], Arg("regenerate")?.GetBoolean() ?? true),
            "sketch" => Sketch(Arg("id")?.GetString() ?? throw new ToolException("id is required"), Arg("dragPoint")?.GetString(),
                Arg("dragTo")?.Deserialize<double[]>(), Arg("settle")?.GetBoolean() ?? true),
            "sketch_edit" => SketchEdit(Arg("id")?.GetString() ?? throw new ToolException("id is required"), Arg("op")?.GetString() ?? throw new ToolException("op is required"),
                Arg("corner")?.GetString(), Arg("radius")?.GetDouble(), Arg("entity")?.GetString(), Arg("x")?.GetDouble(), Arg("y")?.GetDouble(),
                Arg("faces")?.Deserialize<string[]>(), Arg("near")?.Deserialize<double[]>(), Arg("linked")?.GetBoolean() ?? false),
            "regenerate" => AgentTools.Regenerate(Text, Arg("cellSize")?.GetDouble() ?? 0.5, density: Arg("density")?.GetDouble()),
            "validate" => AgentTools.Validate(Text),
            "query" => AgentTools.Query(Text, (Arg("queries") ?? throw new ToolException("queries is required")).Deserialize<AgentTools.QuerySpec[]>(Web)!),
            "assembly" => AgentTools.Assembly(Text, Arg("clearances")?.GetBoolean() ?? true),
            _ => throw new ToolException($"there is no tool {name}"),
        };
        return result.ToJsonString(AgentTools.Compact);
    }

    JsonObject ApplyEdits(List<JsonNode> edits, bool regenerate)
    {
        var o = AgentTools.ApplyEdits(Text, edits, regenerate);
        Text = DocumentEdits.Apply(Text, edits);
        Edits.AddRange(edits);
        return o;
    }

    // The solve as the MCP tool gives it, without the settled text: settling is edits to the working document instead.
    JsonObject Sketch(string id, string? dragPoint, double[]? dragTo, bool settle)
    {
        var o = SketchJson.Solve(Text, id, dragPoint, dragTo);
        var settled = (string)o["settled"]!["text"]!;
        o.Remove("settled");
        if (settle && (bool)o["solved"]!) o["settled"] = Store(settled);
        return o;
    }

    // The edit as the MCP tool gives it, stored in the working document whether or not it solves: it is the user's ask.
    JsonObject SketchEdit(string id, string op, string? corner, double? radius, string? entity, double? x, double? y, string[]? faces, double[]? near, bool linked)
    {
        var o = SketchJson.Edit(Text, id, op, corner, radius, entity, x, y, faces, near, linked);
        o["settled"] = Store((string)o["settled"]!["text"]!);
        return o;
    }

    // The working document becomes text, by edits kept for the page: {edits (how many), hash}.
    JsonObject Store(string text)
    {
        var edits = DocumentEdits.TryApply(text, []).Text == Text ? [] : DocumentDiff.Between(Text, text);
        Text = DocumentEdits.Apply(Text, edits);
        Edits.AddRange(edits);
        return new JsonObject { ["edits"] = edits.Count, ["hash"] = Hash };
    }
}
