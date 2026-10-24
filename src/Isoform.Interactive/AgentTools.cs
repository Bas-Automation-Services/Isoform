using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Geometry;
using Isoform.Meshing;
using Isoform.Modeling;

namespace Isoform.Interactive;

/// <summary>Input an agent can fix: the message says what is wrong. Tools report it to the agent rather than fail.</summary>
public sealed class ToolException(string message) : Exception(message);

/// <summary>
/// The kernel's tools for agents as plain functions, shared by the MCP server (<c>src/Isoform.Mcp</c>) and the
/// playground's chat (<c>src/Isoform.Server</c>). Each goes from its arguments to one compact JSON object: summaries,
/// names and numbers an agent can act on, never meshes. A document is its JSON text or a path to a file. Input an agent
/// can fix throws, a <see cref="ToolException"/> or the kernel's own exception; <see cref="Message"/> is what to tell it.
/// </summary>
public static class AgentTools
{
    /// <summary>How tool results are written: one line, and + or ≥ as themselves (face names are full of + signs).</summary>
    public static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// A part regenerated: hash, diagnostics, every face by its stable source name per body, edge and vertex counts,
    /// bounding box, and mass properties per body (volume, area, centroid, inertia about the centroid); timing in ms.
    /// </summary>
    public static JsonObject Regenerate(string document, double cellSize = 0.5, double chordTolerance = 0.01, double? density = null, bool certifyVolume = false)
    {
        var doc = Part(document);
        var settings = Settings(cellSize, chordTolerance);
        var clock = Stopwatch.StartNew();
        var result = new FrepModeler(settings).Regenerate(doc);
        var regenerate = clock.Elapsed.TotalMilliseconds;
        var o = new JsonObject { ["hash"] = CanonicalFormat.Hash(doc) };
        Summarize(o, result, full: true, density, certifyVolume ? settings.CellSize : null);
        o["ms"] = new JsonObject { ["regenerate"] = Math.Round(regenerate), ["total"] = Math.Round(clock.Elapsed.TotalMilliseconds) };
        return o;
    }

    /// <summary>
    /// Edits applied to a part, assembly or bundle: <c>{hash, previousHash, document | path, result?, references?}</c>.
    /// The new document is an object (compact, the same hash as its canonical text), or written to
    /// <paramref name="outputPath"/>. With <paramref name="regenerate"/>, a summary, and for a part which face names
    /// appeared, disappeared or changed. An edit that does not apply throws, naming it; nothing is written then.
    /// </summary>
    public static JsonObject ApplyEdits(string document, IEnumerable<JsonNode> edits, bool regenerate = true, string? outputPath = null,
        double cellSize = 0.5, double chordTolerance = 0.01)
    {
        var before = Text(document);
        var edited = DocumentEdits.TryApply(before, edits);
        // The problem names the edit and why: "edit 2 {...}: 'slot' is not a feature id in the document".
        if (edited.Problem is { } problem) throw new ToolException(problem);
        var text = edited.Text!;
        var o = new JsonObject { ["hash"] = edited.Hash, ["previousHash"] = DocumentEdits.TryApply(before, []).Hash };
        // As an object, not the indented text: a third of the tokens, and it reads back to the same hash.
        if (outputPath is null) o["document"] = JsonNode.Parse(text);
        else
        {
            File.WriteAllText(outputPath, text);
            o["path"] = Path.GetFullPath(outputPath);
        }
        if (!regenerate) return o;

        var settings = Settings(cellSize, chordTolerance);
        if (CanonicalFormat.FormatOf(text) == PartDocument.FormatVersion)
        {
            var modeler = new FrepModeler(settings);
            var (old, now) = (modeler.Regenerate(CanonicalFormat.Read(before)), modeler.Regenerate(CanonicalFormat.Read(text)));
            var summary = new JsonObject();
            Summarize(summary, now, full: false, density: null, certifyCell: null);
            o["result"] = summary;
            o["references"] = Stability(old.Topology, now.Topology);
        }
        // The parts come from the original file's folder: the edited assembly still names them from there.
        else if (Assembled(document, text) is var (assembly, load))
        {
            var r = new AssemblyModeler(settings).Regenerate(assembly, load);
            o["result"] = new JsonObject
            {
                ["ok"] = r.Diagnostics.Count == 0, ["diagnostics"] = Diagnostics(r.Diagnostics),
                ["instances"] = r.Instances.Count, ["freeDegrees"] = r.FreeDegrees,
            };
        }
        return o;
    }

    /// <summary>
    /// Edits as tools receive them: JSON objects, or an object given as a string (agents sometimes quote them).
    /// </summary>
    public static IEnumerable<JsonNode> Edits(IEnumerable<JsonElement> edits) =>
        edits.Select(e => JsonNode.Parse(e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText())!);

    /// <summary>
    /// Geometric queries on a part, answered in order: ray, distance, clearance and wall. A query the kernel cannot
    /// settle is answered <c>{kind, error}</c>; the others still stand.
    /// </summary>
    public static JsonObject Query(string document, IReadOnlyList<QuerySpec> queries, double cellSize = 0.5, double chordTolerance = 0.01)
    {
        var doc = Part(document);
        var settings = Settings(cellSize, chordTolerance);
        var part = new Compiler(settings).Compile(doc);
        if (part.Field is null) throw new ToolException($"the document has no geometry: {string.Join("; ", part.Diagnostics.Select(d => d.Message))}");
        // Only the wall query needs a mesh; regenerate once, and only if asked.
        var regenerated = new Lazy<FrepResult>(() => new FrepModeler(settings).Regenerate(doc));
        string Face(int owner) => part.Surfaces[owner].Source;
        CompiledBody? BodyOf(string? name) => name is null ? null
            : part.Bodies.FirstOrDefault(b => b.Name == name) ?? throw new ToolException($"there is no body '{name}'; bodies: {string.Join(", ", part.Bodies.Select(b => b.Name))}");

        var answers = new JsonArray();
        foreach (var q in queries)
        {
            var body = BodyOf(q.Body);
            var field = body?.Field ?? part.Field;
            var (min, max) = body is null ? (part.Min, part.Max) : (body.Min, body.Max);
            JsonObject a;
            try
            {
                switch (q.Kind)
                {
                    case "ray":
                        var origin = Point(q.Origin, "origin");
                        // As far as the ray can still meet the box round the solid.
                        var reach = Corners(min, max).Max(c => (c - origin).Length) + 1;
                        a = Queries.Raycast(field, origin, Vector(q.Direction, "direction"), reach) is { } hit
                            ? new JsonObject { ["hit"] = true, ["point"] = V(hit.Point), ["distance"] = N(hit.Distance), ["face"] = Face(hit.Owner), ["normal"] = V(hit.Normal) }
                            : new JsonObject { ["hit"] = false };
                        break;
                    case "distance":
                        var d = Queries.Distance(field, Point(q.Point, "point"));
                        a = new JsonObject { ["lower"] = N(d.Lower), ["upper"] = N(d.Upper), ["closest"] = V(d.Closest), ["face"] = Face(d.Owner), ["inside"] = d.Inside };
                        break;
                    case "clearance":
                        var names = (q.A, q.B) is ({ } x, { } y) ? (x, y)
                            : part.Bodies.Count == 2 ? (part.Bodies[0].Name, part.Bodies[1].Name)
                            : throw new ToolException("a clearance names two bodies, a and b");
                        var (ba, bb) = (BodyOf(names.Item1)!, BodyOf(names.Item2)!);
                        var c = Queries.Clearance(ba.Field, Grown(ba.Min, ba.Max), bb.Field, Grown(bb.Min, bb.Max), 1e-4, 20_000);
                        a = new JsonObject
                        {
                            ["a"] = ba.Name, ["b"] = bb.Name, ["lower"] = N(c.Lower), ["upper"] = N(c.Upper),
                            ["onA"] = V(c.OnA), ["onB"] = V(c.OnB), ["interferes"] = c.Interferes,
                        };
                        break;
                    case "wall":
                        var r = regenerated.Value;
                        a = Queries.MinimumWall(r) is { } w
                            ? new JsonObject { ["thickness"] = N(w.Thickness), ["from"] = V(w.From), ["to"] = V(w.To), ["fromFace"] = Face(w.FromOwner), ["toFace"] = Face(w.ToOwner) }
                            : new JsonObject { ["thickness"] = null };
                        break;
                    default:
                        throw new ToolException($"unknown query kind '{q.Kind}'; use ray, distance, clearance or wall");
                }
            }
            // The kernel declining one query (a ray march that does not settle, a distance search out of budget) is
            // that query's answer, not the call's failure: the other answers stand.
            catch (InvalidOperationException e)
            {
                a = new JsonObject { ["error"] = e.Message };
            }
            a.Insert(0, "kind", q.Kind);
            answers.Add((JsonNode)a);
        }
        return new JsonObject { ["hash"] = CanonicalFormat.Hash(doc), ["answers"] = answers };
    }

    /// <summary>
    /// A fast check of a part without meshing: parsed, the document rules applied, compiled. <c>{ok, hash, features,
    /// bodies, diagnostics}</c>; a document that does not parse is a <c>parse-error</c> diagnostic.
    /// </summary>
    public static JsonObject Validate(string document)
    {
        PartDocument doc;
        try
        {
            doc = Part(document);
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or InvalidCastException or NullReferenceException or ArgumentException or KeyNotFoundException)
        {
            return new JsonObject { ["ok"] = false, ["diagnostics"] = new JsonArray(Diagnostic("parse-error", null, Message(e))) };
        }
        var settings = new ModelingSettings();
        var problems = DocumentRules.FirstProblem(doc, settings.Tolerance) is { } first ? [first] : new Compiler(settings).Compile(doc).Diagnostics;
        var bodies = doc.Features.Where(DocumentRules.StartsBody).Select(f => f.Body ?? f.Id);
        return new JsonObject
        {
            ["ok"] = problems.Count == 0, ["hash"] = CanonicalFormat.Hash(doc), ["features"] = doc.Features.Count,
            ["bodies"] = new JsonArray(bodies.Select(b => (JsonNode)b).ToArray()), ["diagnostics"] = Diagnostics(problems),
        };
    }

    /// <summary>
    /// An assembly regenerated: instances with their solved placements, the free degrees of freedom the mates leave,
    /// diagnostics, and certified clearances or interferences between instances whose boxes come within
    /// <paramref name="within"/> mm.
    /// </summary>
    public static JsonObject Assembly(string document, bool clearances = true, double within = 0, double cellSize = 0.5, double chordTolerance = 0.01)
    {
        var (assembly, load) = Assembled(document, Text(document)) ?? throw new ToolException(AssemblyTextHelp);
        var clock = Stopwatch.StartNew();
        var result = new AssemblyModeler(Settings(cellSize, chordTolerance)).Regenerate(assembly, load);
        var o = new JsonObject
        {
            ["hash"] = CanonicalFormat.Hash(assembly),
            ["ok"] = result.Diagnostics.Count == 0,
            ["diagnostics"] = Diagnostics(result.Diagnostics),
            ["instances"] = new JsonArray(result.Instances.Select(i => (JsonNode)new JsonObject
            {
                ["id"] = i.Id, ["part"] = i.Part, ["faces"] = i.Result.Topology?.Faces.Count ?? 0,
                ["origin"] = V(i.Placement.Origin), ["x"] = V(i.Placement.X), ["normal"] = V(i.Placement.Z),
            }).ToArray()),
            ["mates"] = assembly.Mates?.Count ?? 0,
            ["freeDegrees"] = result.FreeDegrees,
        };
        if (clearances)
            o["clearances"] = new JsonArray(result.Clearances(within).Select(c => (JsonNode)new JsonObject
            {
                ["a"] = c.A, ["b"] = c.B, ["lower"] = N(c.Clearance.Lower), ["upper"] = N(c.Clearance.Upper), ["interferes"] = c.Clearance.Interferes,
            }).ToArray());
        o["ms"] = Math.Round(clock.Elapsed.TotalMilliseconds);
        return o;
    }

    /// <summary>One query of <see cref="Query"/>.</summary>
    public sealed record QuerySpec(
        [property: Description("ray | distance | clearance | wall")] string Kind,
        [property: Description("ray: start point [x, y, z] in mm.")] double[]? Origin = null,
        [property: Description("ray: direction [x, y, z], any length.")] double[]? Direction = null,
        [property: Description("distance: the point [x, y, z] in mm.")] double[]? Point = null,
        [property: Description("ray, distance: query this body only (default: the whole part).")] string? Body = null,
        [property: Description("clearance: first body (default: the part's first of exactly two).")] string? A = null,
        [property: Description("clearance: second body.")] string? B = null);

    // ---- Shared pieces ----

    /// <summary>What to tell an agent about a failed call: the exception's message, or what a missing field means.</summary>
    public static string Message(Exception e) => e is NullReferenceException or ArgumentNullException ? "a required field is missing (see describe_format)" : e.Message;

    /// <summary>A document's text: given as text, or read from the file it names.</summary>
    public static string Text(string document) => LooksLikeText(document) ? document : File.ReadAllText(document);

    static bool LooksLikeText(string document) => document.TrimStart().StartsWith('{');

    internal static PartDocument Part(string document)
    {
        var text = Text(document);
        var format = CanonicalFormat.FormatOf(text);
        if (format is Bundle.FormatVersion or AssemblyDocument.FormatVersion)
            throw new ToolException($"this is an {format} document; use the assembly tool");
        return CanonicalFormat.Read(text);
    }

    /// <summary>Why an assembly given as text cannot be regenerated.</summary>
    public const string AssemblyTextHelp = "an assembly given as text has no folder to find its parts in; pass a bundle or a path";

    /// <summary>
    /// An assembly and where its parts come from: a bundle's own documents, or the folder of the assembly file named by
    /// <paramref name="document"/>. <paramref name="text"/> is the assembly or bundle itself (perhaps edited). Null for
    /// an assembly given as text, which has no folder.
    /// </summary>
    public static (AssemblyDocument Assembly, Func<string, string?> Load)? Assembled(string document, string text)
    {
        switch (CanonicalFormat.FormatOf(text))
        {
            case Bundle.FormatVersion:
                var b = CanonicalFormat.ReadBundle(text);
                return (b.Assembly, p => b.Documents.GetValueOrDefault(p));
            case AssemblyDocument.FormatVersion:
                return LooksLikeText(document) ? null : (CanonicalFormat.ReadAssembly(text), Resolver(Path.GetDirectoryName(Path.GetFullPath(document))!));
            case var f:
                throw new ToolException($"expected a bundle or an assembly, got '{f}'");
        }
    }

    static Func<string, string?> Resolver(string folder) => p =>
    {
        var path = Path.Combine(folder, p);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    };

    /// <summary>Settings for a tool call; both values must be positive.</summary>
    public static ModelingSettings Settings(double cellSize, double chordTolerance)
    {
        if (!(cellSize > 0) || !(chordTolerance > 0)) throw new ToolException("cellSize and chordTolerance must be positive");
        return new ModelingSettings { CellSize = cellSize, ChordTolerance = chordTolerance };
    }

    /// <summary>
    /// Diagnostics, topology counts, the box and per body its volume, area and centroid; <paramref name="full"/> adds
    /// the face names and the inertia.
    /// </summary>
    static void Summarize(JsonObject o, FrepResult r, bool full, double? density, double? certifyCell)
    {
        o["ok"] = r.Diagnostics.Count == 0;
        o["diagnostics"] = Diagnostics(r.Diagnostics);
        if (r.Topology is not { } t || r.Mesh is not { } mesh) return;
        o["faces"] = t.Faces.Count;
        o["edges"] = t.Edges.Count;
        o["vertices"] = t.Vertices.Count;
        if (mesh.Vertices.Count > 0)
        {
            var (min, max) = Extent(mesh.Vertices);
            o["box"] = new JsonObject { ["min"] = V(min), ["max"] = V(max) };
        }
        var bodies = new JsonArray();
        for (var k = 0; k < r.Bodies.Count; k++)
        {
            var body = r.Bodies[k];
            var compiled = r.Part.Bodies[k];
            var mine = body.Faces.ToHashSet();
            // A body's own quads: mass properties per solid, so overlapping bodies are not merged.
            var own = new Mesh(mesh.Vertices, mesh.Quads.Where((_, q) => mine.Contains(r.FaceOfQuad[q])).ToList());
            var m = MassProperties.Of(compiled.Field, own, density ?? 1);
            var b = new JsonObject { ["name"] = body.Name };
            if (full) b["faces"] = new JsonArray(body.Faces.Select(f => FaceName(t.Faces[f])).ToArray());
            b["volume"] = N(m.Volume);
            if (certifyCell is { } cell)
            {
                var bounds = MassProperties.VolumeBounds(compiled.Field, compiled.Min, compiled.Max, cell);
                b["volumeBounds"] = new JsonArray(N(bounds.Lo), N(bounds.Hi));
            }
            b["area"] = N(m.Area);
            if (density is not null) b["mass"] = N(m.Mass);
            b["centroid"] = V(m.Centroid);
            if (full)
            {
                var i = m.Inertia;
                b["inertia"] = new JsonObject { ["xx"] = N(i.Xx), ["yy"] = N(i.Yy), ["zz"] = N(i.Zz), ["xy"] = N(i.Xy), ["yz"] = N(i.Yz), ["zx"] = N(i.Zx) };
            }
            bodies.Add((JsonNode)b);
        }
        o["bodies"] = bodies;
    }

    /// <summary>Face stability across an edit (<see cref="ReferencePins.Report"/>): kept, appeared, gone, changed.</summary>
    static JsonObject Stability(Topology? before, Topology? after)
    {
        var r = ReferencePins.Report(before, after);
        var changed = new JsonObject();
        foreach (var (s, old, now) in r.Changed) changed[s] = $"{Lower(old)} -> {Lower(now)}";
        return new JsonObject { ["kept"] = r.Kept, ["appeared"] = Names(r.Appeared), ["gone"] = Names(r.Gone), ["changed"] = changed };

        static string Lower(ReferenceStatus s) => s.ToString().ToLowerInvariant();
        static JsonArray Names(IEnumerable<string> names) => new(names.Select(n => (JsonNode)n).ToArray());
    }

    /// <summary>A face by its source name; a face several sources share lists them all.</summary>
    static JsonNode FaceName(TopoFace f) => f.Sources.Count == 1 ? f.Sources[0] : new JsonArray(f.Sources.Select(s => (JsonNode)s).ToArray());

    /// <summary>Diagnostics as <c>[{code, feature?, message}]</c>.</summary>
    public static JsonArray Diagnostics(IEnumerable<Diagnostic> diagnostics) =>
        new(diagnostics.Select(d => (JsonNode)Diagnostic(d.Code, d.FeatureId, d.Message)).ToArray());

    static JsonObject Diagnostic(string code, string? feature, string message)
    {
        var o = new JsonObject { ["code"] = code };
        if (feature is not null) o["feature"] = feature;
        o["message"] = message;
        return o;
    }

    static (Point3D Min, Point3D Max) Extent(IEnumerable<Point3D> points)
    {
        var (lo, hi) = (new Point3D(double.MaxValue, double.MaxValue, double.MaxValue), new Point3D(double.MinValue, double.MinValue, double.MinValue));
        foreach (var p in points)
            (lo, hi) = (new Point3D(Math.Min(lo.X, p.X), Math.Min(lo.Y, p.Y), Math.Min(lo.Z, p.Z)), new Point3D(Math.Max(hi.X, p.X), Math.Max(hi.Y, p.Y), Math.Max(hi.Z, p.Z)));
        return (lo, hi);
    }

    static IEnumerable<Point3D> Corners(Point3D min, Point3D max) =>
        from x in new[] { min.X, max.X } from y in new[] { min.Y, max.Y } from z in new[] { min.Z, max.Z } select new Point3D(x, y, z);

    // Grown a little, like the assembly's boxes, so the search has room round the solid.
    static Box3D Grown(Point3D min, Point3D max) => new(min - new Vector3D(1, 1, 1), max + new Vector3D(1, 1, 1));

    static Point3D Point(double[]? v, string name) =>
        v is { Length: 3 } ? new Point3D(v[0], v[1], v[2]) : throw new ToolException($"{name} must be three numbers [x, y, z]");

    static Vector3D Vector(double[]? v, string name) =>
        v is { Length: 3 } && v.Any(x => x != 0) ? new Vector3D(v[0], v[1], v[2]) : throw new ToolException($"{name} must be three numbers [x, y, z], not all zero");

    // Nine significant digits: finer than the kernel's accuracy on every measure it reports, and far fewer tokens. Below
    // 1e-12 mm is solver noise (a solved placement's 1e-24), so 0, which also turns -0 into 0. JSON has no infinity: an
    // unbounded end (nothing found) is null.
    static JsonNode? N(double v) => !double.IsFinite(v) ? null
        : JsonValue.Create(Math.Abs(v) < 1e-12 ? 0 : double.Parse(v.ToString("G9", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));

    static JsonArray V(Point3D p) => new(N(p.X), N(p.Y), N(p.Z));

    static JsonArray V(Vector3D v) => new(N(v.X), N(v.Y), N(v.Z));
}
