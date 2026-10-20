using System.Text.Json.Nodes;

namespace Isoform.Document;

/// <summary>
/// Edits as data, applied to the canonical JSON so any field of any feature can be changed:
/// <c>{"set": "hole.diameter", "value": 60}</c>, <c>{"insert": {...}, "before": "fillet"}</c>, <c>{"remove": "slot"}</c>.
/// A set path may go deeper, with array indices as numbers: <c>"rib.profile.edges.1.to"</c>. A value may be an
/// expression: <c>{"set": "hole.diameter", "value": "=width / 2"}</c>. Paths starting with <c>parameters.</c> set or
/// remove a parameter: <c>{"set": "parameters.width", "value": 120}</c>, <c>{"remove": "parameters.width"}</c>.
/// </summary>
public static class DocumentEdits
{
    const string Parameters = "parameters.";

    public static PartDocument Apply(PartDocument doc, IEnumerable<JsonNode> edits)
    {
        var root = CanonicalFormat.ToNode(doc).AsObject();
        foreach (var edit in edits) Apply(root, edit.AsObject());
        return CanonicalFormat.FromNode(root);
    }

    static void Apply(JsonObject root, JsonObject edit)
    {
        var features = root["features"]!.AsArray();
        if (edit["set"] is { } set)
        {
            var path = (string)set!;
            // Through text, so a value built in code (an int, say) reads back like one parsed from a file.
            var value = JsonNode.Parse(edit["value"]!.ToJsonString());
            if (path.StartsWith(Parameters)) (root["parameters"] ??= new JsonObject())[path[Parameters.Length..]] = value;
            else
            {
                Find(features, path.Split('.')[0]);
                Set(features, path, value);
            }
        }
        else if (edit["insert"] is { } insert)
        {
            var before = (string?)edit["before"];
            var index = before is null ? features.Count : features.IndexOf(Find(features, before));
            features.Insert(index, insert.DeepClone());
        }
        else if (edit["remove"] is { } remove)
        {
            var target = (string)remove!;
            if (target.StartsWith(Parameters)) root["parameters"]!.AsObject().Remove(target[Parameters.Length..]);
            else features.Remove(Find(features, target));
        }
        else throw new FormatException($"Unknown edit: {edit.ToJsonString()}");
    }

    /// <summary>
    /// Sets the field at an edit path. Shared with the canonical format and parameter resolution, which reach fields
    /// by the same paths; it takes the first feature with the id, as a duplicate id is the rules' to report.
    /// </summary>
    internal static void Set(JsonArray features, string path, JsonNode? value)
    {
        var keys = path.Split('.');
        JsonNode node = features.First(f => (string?)f!["id"] == keys[0])!;
        for (var i = 1; i < keys.Length - 1; i++) node = Child(node, keys[i]);
        if (node is JsonArray array) array[int.Parse(keys[^1])] = value;
        else node[keys[^1]] = value;
    }

    static JsonNode Child(JsonNode node, string key) => node is JsonArray a ? a[int.Parse(key)]! : node[key]!;

    static JsonObject Find(JsonArray features, string id) =>
        features.Single(f => (string?)f!["id"] == id)!.AsObject();
}
