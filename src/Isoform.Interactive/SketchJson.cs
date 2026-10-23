using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Isoform.Document;
using Isoform.Modeling;
using Isoform.Sketching;

namespace Isoform.Interactive;

/// <summary>
/// Sketches for the page and for agents: a sketch solved (optionally with a point dragged), a sketch filleted or
/// trimmed, and the plane of a face to sketch on. The MCP <c>sketch</c> and <c>sketch_edit</c> tools and the playground's
/// <c>sketch</c> and <c>sketchEdit</c> ops return the same JSON.
/// </summary>
public static class SketchJson
{
    static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// Sketch <paramref name="id"/> of a part document, solved: <c>{id, solved, freeDegrees, free, points, radii,
    /// problems, regions, settled: {text, hash}}</c>. The settled text has every sketch's stored points moved to its
    /// solution (the dragged one for this sketch).
    /// </summary>
    public static JsonObject Solve(string document, string id, string? dragPoint = null, double[]? dragTo = null)
    {
        var doc = CanonicalFormat.Read(document);
        var sketch = SketchDocuments.Resolved(doc, id);
        (string, double, double)? drag = dragPoint is { } p && dragTo is [var u, var v] ? (p, u, v) : null;
        var solution = SketchSolver.Solve(sketch, drag);
        var (regions, problem) = solution.Solved ? SketchRegions.Of(sketch, solution) : ([], null);
        var settled = CanonicalFormat.Write(SketchDocuments.Settle(doc, s => s.Id == id ? solution : null));
        var problems = new JsonArray([.. solution.Problems.Select(x => new JsonObject
        {
            ["code"] = x.Code, ["message"] = x.Message, ["constraints"] = new JsonArray([.. x.Constraints.Select(c => (JsonNode)c)]),
        })]);
        if (problem is not null) problems.Add((JsonNode)new JsonObject { ["code"] = SketchSolution.Invalid, ["message"] = problem, ["constraints"] = new JsonArray() });
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
    }

    /// <summary>
    /// <see cref="Solve"/> for the page: <paramref name="drag"/> is <c>{"point": "c", "to": [u, v]}</c> or empty. A sketch
    /// that does not read gives <c>{error}</c>.
    /// </summary>
    public static string Write(string document, string id, string drag)
    {
        try
        {
            var d = drag.Length > 0 ? JsonNode.Parse(drag) : null;
            return Solve(document, id, (string?)d?["point"], d?["to"]?.AsArray().Select(v => (double)v!).ToArray()).ToJsonString(Compact);
        }
        catch (Exception e) when (Session.Unreadable(e) || e is ArgumentException)
        {
            return new JsonObject { ["error"] = e.Message }.ToJsonString(Compact);
        }
    }

    /// <summary>
    /// A fillet or trim (<see cref="SketchEdits"/>) of sketch <paramref name="id"/>: <see cref="Solve"/> of the edited
    /// document plus <c>added</c> (the new point and entity ids) and <c>dropped</c> (the constraints the edit had to drop,
    /// worded as <see cref="SketchSolver.Describe"/> does). A fillet takes <paramref name="corner"/> and
    /// <paramref name="radius"/>; a trim <paramref name="entity"/> and the point (<paramref name="x"/>, <paramref name="y"/>)
    /// in sketch coordinates nearest the piece to delete. The sketch's expressions stay where what they set is still
    /// there. An edit that cannot be made throws an <see cref="ArgumentException"/> saying why.
    /// </summary>
    public static JsonObject Edit(string document, string id, string op, string? corner = null, double? radius = null,
        string? entity = null, double? x = null, double? y = null)
    {
        static T Need<T>(T? v, string what) where T : struct => v ?? throw new ArgumentException($"a sketch edit needs {what}");
        return Edit(document, id, sketch => op switch
        {
            "fillet" => SketchEdits.Fillet(sketch, corner ?? throw new ArgumentException("a fillet needs its corner point"), Need(radius, "a radius")),
            "trim" => SketchEdits.Trim(sketch, entity ?? throw new ArgumentException("a trim needs its entity"), Need(x, "x"), Need(y, "y")),
            _ => throw new ArgumentException($"there is no sketch edit '{op}'; use fillet or trim"),
        });
    }

    /// <summary><see cref="Edit"/> with the edit given: it gets the sketch as regeneration sees it.</summary>
    public static JsonObject Edit(string document, string id, Func<Sketch, SketchEdit> change)
    {
        var doc = CanonicalFormat.Read(document);
        var sketch = SketchDocuments.Resolved(doc, id);
        var edit = change(sketch);
        var edited = doc with
        {
            Features = [.. doc.Features.Select(f => f.Id == id ? edit.Sketch : f)],
            Expressions = Carried(doc.Expressions, id, sketch, edit.Sketch),
        };
        var o = Solve(CanonicalFormat.Write(edited), id);
        o["added"] = new JsonArray([.. edit.Added.Select(a => (JsonNode)a)]);
        o["dropped"] = new JsonArray([.. edit.Dropped.Select(d => (JsonNode)d)]);
        return o;
    }

    /// <summary>
    /// <see cref="Edit"/> for the page: <paramref name="edit"/> is <c>{op: "fillet", corner, radius}</c> or <c>{op: "trim",
    /// entity, x, y}</c>. An edit that cannot be made, or a sketch that does not read, gives <c>{error}</c>.
    /// </summary>
    public static string WriteEdit(string document, string id, string edit)
    {
        try
        {
            var e = JsonNode.Parse(edit)!;
            return Edit(document, id, (string)e["op"]!, (string?)e["corner"], (double?)e["radius"], (string?)e["entity"], (double?)e["x"], (double?)e["y"]).ToJsonString(Compact);
        }
        catch (Exception e) when (Session.Unreadable(e) || e is ArgumentException)
        {
            return new JsonObject { ["error"] = e.Message }.ToJsonString(Compact);
        }
    }

    // The sketch's field expressions after an edit: a constraint's follows the constraint (same kind and refs), a point's
    // its point, an entity's its entity while it keeps its shape; one whose target is gone goes with it (the edit lists
    // a dropped constraint). Other features' expressions stay as they are.
    static IReadOnlyDictionary<string, string> Carried(IReadOnlyDictionary<string, string> expressions, string id, Sketch before, Sketch after)
    {
        var kept = new Dictionary<string, string>();
        foreach (var (path, text) in expressions)
        {
            var parts = path.Split('.');
            if (parts[0] != id || parts.Length < 3)
            {
                kept[path] = text;
                continue;
            }
            var at = parts[1] switch
            {
                "constraints" when before.Constraints[Index(parts[2])] is var c => Position(after.Constraints, d => d.Kind == c.Kind && d.Refs.SequenceEqual(c.Refs)),
                "entities" when before.Entities[Index(parts[2])] is var e => Position(after.Entities, d => d.Id == e.Id && d.GetType() == e.GetType()),
                "points" => after.Points.ContainsKey(parts[2]) ? parts[2] : null,
                _ => parts[2],
            };
            if (at is null) continue;
            parts[2] = at;
            kept[string.Join('.', parts)] = text;
        }
        return kept;

        static int Index(string s) => int.Parse(s, CultureInfo.InvariantCulture);
        static string? Position<T>(IReadOnlyList<T> list, Func<T, bool> match)
        {
            for (var i = 0; i < list.Count; i++)
                if (match(list[i])) return i.ToString(CultureInfo.InvariantCulture);
            return null;
        }
    }

    /// <summary>
    /// The plane to sketch on a face of a regeneration's part, as a sketch on it would have it (at the end of the history):
    /// <c>{face, origin, x, y, normal}</c>, or <c>{face, error}</c> when the face is not a flat face of the part.
    /// </summary>
    public static string FaceFrame(Regeneration? r, string face)
    {
        var part = r?.View?.Part?.Part;
        var plane = part?.Field is null ? null : SketchPlanes.OfFace(face, part.Surfaces, part.Field);
        if (plane is null) return new JsonObject { ["face"] = face, ["error"] = $"{face} is not a flat face of the part" }.ToJsonString(Compact);
        var frame = Profiles.FrameOf(plane);
        // JsonNodes, not doubles: JsonArray.Add<double> needs reflection, which the trimmed wasm kernel has not.
        JsonArray V(double x, double y, double z) => [(JsonNode)x, (JsonNode)y, (JsonNode)z];
        return new JsonObject
        {
            ["face"] = face,
            ["origin"] = V(frame.Origin.X, frame.Origin.Y, frame.Origin.Z),
            ["x"] = V(frame.X.X, frame.X.Y, frame.X.Z),
            ["y"] = V(frame.Y.X, frame.Y.Y, frame.Y.Z),
            ["normal"] = V(frame.Z.X, frame.Z.Y, frame.Z.Z),
        }.ToJsonString(Compact);
    }
}
