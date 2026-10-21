using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Geometry;
using Isoform.Interactive;
using Isoform.Meshing;
using Isoform.Modeling;
using Isoform.Sketching;
using Isoform.Step;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Isoform.Mcp;

/// <summary>
/// The kernel as MCP tools. Each tool is a pure function from its arguments to one compact JSON object: summaries,
/// names and numbers an agent can act on, never meshes. Bad input comes back as a tool error with the kernel's message.
/// </summary>
[McpServerToolType]
public static class IsoformTools
{
    const string DocumentHelp = "A part document (format isoform/0): its JSON text, or a path to a .json file. See describe_format.";
    const string CellHelp = "Finest cell size in mm (default 0.5). Walls, gaps and holes under 2 × cellSize are meshed with cells up to 8 times finer where they are; only what is smaller still is reported as below-resolution.";
    const string ChordHelp = "Largest distance in mm the mesh may stray from a curved face (default 0.01).";

    /// <summary>How tool results are written: one line, and + or ≥ as themselves (face names are full of + signs).</summary>
    public static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [McpServerTool(Name = "regenerate", ReadOnly = true, Idempotent = true)]
    [Description("Regenerates a part: diagnostics, every face by its stable source name per body, edge and vertex counts, " +
                 "bounding box, and mass properties per body (volume, area, centroid, inertia about the centroid). Timing in ms.")]
    public static string Regenerate(
        [Description(DocumentHelp)] string document,
        [Description(CellHelp)] double cellSize = 0.5,
        [Description(ChordHelp)] double chordTolerance = 0.01,
        [Description("Mass per mm³; when given, mass and inertia are scaled by it (default: per unit density).")] double? density = null,
        [Description("Also give a certified [lower, upper] range of each body's volume (slower).")] bool certifyVolume = false) => Run(() =>
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
    });

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
        [Description(ChordHelp)] double chordTolerance = 0.01) => Run(() =>
    {
        var before = Text(document);
        var edited = DocumentEdits.TryApply(before, edits.Select(e => JsonNode.Parse(e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText())!));
        // The problem names the edit and why: "edit 2 {...}: 'slot' is not a feature id in the document".
        if (edited.Problem is { } problem) throw new McpException(problem);
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
    });

    [McpServerTool(Name = "query", ReadOnly = true, Idempotent = true)]
    [Description("Geometric queries on a part, several per call: ray (first boundary hit: point, distance, face, normal), " +
                 "distance (certified [lower, upper] to the boundary, closest point, face, inside), clearance (certified gap or " +
                 "interference between two bodies of the part) and wall (minimum wall thickness, as a caliper square to the face). " +
                 "A query the kernel cannot settle is answered {\"kind\", \"error\"}; the others still stand.")]
    public static string Query(
        [Description(DocumentHelp)] string document,
        [Description("The queries, answered in order.")] QuerySpec[] queries,
        [Description(CellHelp)] double cellSize = 0.5,
        [Description(ChordHelp)] double chordTolerance = 0.01) => Run(() =>
    {
        var doc = Part(document);
        var settings = Settings(cellSize, chordTolerance);
        var part = new Compiler(settings).Compile(doc);
        if (part.Field is null) throw new McpException($"the document has no geometry: {string.Join("; ", part.Diagnostics.Select(d => d.Message))}");
        // Only the wall query needs a mesh; regenerate once, and only if asked.
        var regenerated = new Lazy<FrepResult>(() => new FrepModeler(settings).Regenerate(doc));
        string Face(int owner) => part.Surfaces[owner].Source;
        CompiledBody? BodyOf(string? name) => name is null ? null
            : part.Bodies.FirstOrDefault(b => b.Name == name) ?? throw new McpException($"there is no body '{name}'; bodies: {string.Join(", ", part.Bodies.Select(b => b.Name))}");

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
                            : throw new McpException("a clearance names two bodies, a and b");
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
                        throw new McpException($"unknown query kind '{q.Kind}'; use ray, distance, clearance or wall");
                }
            }
            // The kernel declining one query (a ray march that does not settle, a distance search out of budget) is
            // that query's answer, not the call's failure: the other answers stand.
            catch (InvalidOperationException e)
            {
                a = new JsonObject { ["error"] = e.Message };
            }
            a.Insert(0, "kind", q.Kind);
            answers.Add(a);
        }
        return new JsonObject { ["hash"] = CanonicalFormat.Hash(doc), ["answers"] = answers };
    });

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
        [Description("Where to drag it, [u, v] in sketch coordinates.")] double[]? dragTo = null) => Run(() =>
    {
        var doc = Part(document);
        var sketch = SketchDocuments.Resolved(doc, id);
        (string, double, double)? drag = dragPoint is { } p && dragTo is [var u, var v] ? (p, u, v) : null;
        var solution = SketchSolver.Solve(sketch, drag);
        var (regions, problem) = solution.Solved ? SketchRegions.Of(sketch, solution) : ([], null);
        var settled = CanonicalFormat.Write(SketchDocuments.Settle(doc, s => s.Id == id ? solution : null));
        var problems = new JsonArray([.. solution.Problems.Select(x => new JsonObject
        {
            ["code"] = x.Code, ["message"] = x.Message, ["constraints"] = new JsonArray([.. x.Constraints.Select(c => (JsonNode)c)]),
        })]);
        if (problem is not null) problems.Add(new JsonObject { ["code"] = SketchSolution.Invalid, ["message"] = problem, ["constraints"] = new JsonArray() });
        return new JsonObject
        {
            ["id"] = id,
            ["solved"] = solution.Solved && problem is null,
            ["freeDegrees"] = solution.FreeDegrees,
            ["free"] = new JsonArray([.. solution.Free.Order(StringComparer.Ordinal).Select(f => (JsonNode)f)]),
            ["points"] = new JsonObject(solution.Points.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => KeyValuePair.Create(x.Key, (JsonNode?)new JsonArray(x.Value.X, x.Value.Y)))),
            ["radii"] = new JsonObject(solution.Radii.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => KeyValuePair.Create(x.Key, (JsonNode?)x.Value))),
            ["problems"] = problems,
            ["regions"] = new JsonArray([.. regions.Select(r => new JsonObject
            {
                ["outer"] = new JsonArray([.. r.Ids.Select(i => (JsonNode)i)]),
                ["holes"] = new JsonArray([.. r.Holes.Select(h => (JsonNode)new JsonArray([.. h.Edges.Select(e => e.Id).Distinct().Select(i => (JsonNode)i)]))]),
            })]),
            ["settled"] = new JsonObject { ["text"] = settled, ["hash"] = CanonicalFormat.Hash(CanonicalFormat.Read(settled)) },
        };
    });

    [McpServerTool(Name = "validate", ReadOnly = true, Idempotent = true)]
    [Description("Fast check of a part without meshing: parses it, applies the document rules and compiles it. " +
                 "Returns ok, the hash, the bodies and the diagnostics. Parse errors come back as a diagnostic too.")]
    public static string Validate([Description(DocumentHelp)] string document) => Run(() =>
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
    });

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
        var settings = Settings(cellSize, chordTolerance);
        var text = Text(document);
        var o = new JsonObject();
        string step;
        try
        {
            if (CanonicalFormat.FormatOf(text) is Bundle.FormatVersion or AssemblyDocument.FormatVersion)
            {
                var (assembly, load) = Assembled(document, text) ?? throw new McpException(AssemblyTextHelp);
                var result = new AssemblyModeler(settings).Regenerate(assembly, load);
                o["diagnostics"] = Diagnostics(result.Diagnostics);
                step = StepWriter.Write(result, settings, name ?? "assembly");
                o["ok"] = true;
                o["instances"] = result.Instances.Count;
                o["parts"] = result.Instances.Select(i => i.Hash).Distinct().Count();
            }
            else
            {
                var result = new FrepModeler(settings).Regenerate(CanonicalFormat.Read(text));
                o["diagnostics"] = Diagnostics(result.Diagnostics);
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
        [Description(ChordHelp)] double chordTolerance = 0.01) => Run(() =>
    {
        var (assembly, load) = Assembled(document, Text(document)) ?? throw new McpException(AssemblyTextHelp);
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
    });

    [McpServerTool(Name = "describe_format", ReadOnly = true, Idempotent = true)]
    [Description("The document format in brief: every feature type with a JSON example and the face names it makes, face naming " +
                 "rules, bodies, edits, assemblies and diagnostic codes. Read this before writing a document.")]
    public static string DescribeFormat() => FormatGuide.Text;

    /// <summary>One query of the <c>query</c> tool.</summary>
    public sealed record QuerySpec(
        [property: Description("ray | distance | clearance | wall")] string Kind,
        [property: Description("ray: start point [x, y, z] in mm.")] double[]? Origin = null,
        [property: Description("ray: direction [x, y, z], any length.")] double[]? Direction = null,
        [property: Description("distance: the point [x, y, z] in mm.")] double[]? Point = null,
        [property: Description("ray, distance: query this body only (default: the whole part).")] string? Body = null,
        [property: Description("clearance: first body (default: the part's first of exactly two).")] string? A = null,
        [property: Description("clearance: second body.")] string? B = null);

    // ---- Shared pieces ----

    /// <summary>Errors an agent can fix come back as tool errors carrying the kernel's message.</summary>
    static string Run(Func<JsonObject> tool)
    {
        try
        {
            return tool().ToJsonString(Compact);
        }
        catch (Exception e) when (e is not (McpException or OperationCanceledException))
        {
            throw new McpException(Message(e), e);
        }
    }

    static string Message(Exception e) => e is NullReferenceException or ArgumentNullException ? "a required field is missing (see describe_format)" : e.Message;

    static bool LooksLikeText(string document) => document.TrimStart().StartsWith('{');

    static string Text(string document) => LooksLikeText(document) ? document : File.ReadAllText(document);

    static PartDocument Part(string document)
    {
        var text = Text(document);
        var format = CanonicalFormat.FormatOf(text);
        if (format is Bundle.FormatVersion or AssemblyDocument.FormatVersion)
            throw new McpException($"this is an {format} document; use the assembly tool");
        return CanonicalFormat.Read(text);
    }

    const string AssemblyTextHelp = "an assembly given as text has no folder to find its parts in; pass a bundle or a path";

    /// <summary>
    /// An assembly and where its parts come from: a bundle's own documents, or the folder of the assembly file named by
    /// <paramref name="document"/>. <paramref name="text"/> is the assembly or bundle itself (perhaps edited). Null for
    /// an assembly given as text, which has no folder.
    /// </summary>
    static (AssemblyDocument Assembly, Func<string, string?> Load)? Assembled(string document, string text)
    {
        switch (CanonicalFormat.FormatOf(text))
        {
            case Bundle.FormatVersion:
                var b = CanonicalFormat.ReadBundle(text);
                return (b.Assembly, p => b.Documents.GetValueOrDefault(p));
            case AssemblyDocument.FormatVersion:
                return LooksLikeText(document) ? null : (CanonicalFormat.ReadAssembly(text), Resolver(Path.GetDirectoryName(Path.GetFullPath(document))!));
            case var f:
                throw new McpException($"expected a bundle or an assembly, got '{f}'");
        }
    }

    static Func<string, string?> Resolver(string folder) => p =>
    {
        var path = Path.Combine(folder, p);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    };

    static ModelingSettings Settings(double cellSize, double chordTolerance)
    {
        if (!(cellSize > 0) || !(chordTolerance > 0)) throw new McpException("cellSize and chordTolerance must be positive");
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
            bodies.Add(b);
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

    static JsonArray Diagnostics(IEnumerable<Diagnostic> diagnostics) =>
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
        v is { Length: 3 } ? new Point3D(v[0], v[1], v[2]) : throw new McpException($"{name} must be three numbers [x, y, z]");

    static Vector3D Vector(double[]? v, string name) =>
        v is { Length: 3 } && v.Any(x => x != 0) ? new Vector3D(v[0], v[1], v[2]) : throw new McpException($"{name} must be three numbers [x, y, z], not all zero");

    // Nine significant digits: finer than the kernel's accuracy on every measure it reports, and far fewer tokens. Below
    // 1e-12 mm is solver noise (a solved placement's 1e-24), so 0, which also turns -0 into 0. JSON has no infinity: an
    // unbounded end (nothing found) is null.
    static JsonNode? N(double v) => !double.IsFinite(v) ? null
        : JsonValue.Create(Math.Abs(v) < 1e-12 ? 0 : double.Parse(v.ToString("G9", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));

    static JsonArray V(Point3D p) => new(N(p.X), N(p.Y), N(p.Z));

    static JsonArray V(Vector3D v) => new(N(v.X), N(v.Y), N(v.Z));
}
