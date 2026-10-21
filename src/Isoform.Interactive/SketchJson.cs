using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Isoform.Document;
using Isoform.Modeling;
using Isoform.Sketching;

namespace Isoform.Interactive;

/// <summary>
/// Sketches for the page and for agents: a sketch solved (optionally with a point dragged), and the plane of a face to
/// sketch on. The MCP <c>sketch</c> tool and the playground's <c>sketch</c> op return the same JSON.
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
