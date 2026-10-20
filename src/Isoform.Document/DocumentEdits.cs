using System.Text.Json.Nodes;

namespace Isoform.Document;

/// <summary>
/// Edits as data, applied to the canonical JSON so any field of any feature can be changed:
/// <c>{"set": "hole.diameter", "value": 60}</c>, <c>{"insert": {...}, "before": "fillet"}</c>, <c>{"remove": "slot"}</c>.
/// </summary>
public static class DocumentEdits
{
    public static PartDocument Apply(PartDocument doc, IEnumerable<JsonNode> edits)
    {
        var root = CanonicalFormat.ToNode(doc);
        var features = root["features"]!.AsArray();
        foreach (var edit in edits) Apply(features, edit.AsObject());
        return CanonicalFormat.FromNode(root);
    }

    static void Apply(JsonArray features, JsonObject edit)
    {
        if (edit["set"] is { } set)
        {
            var path = ((string)set!).Split('.', 2);
            Find(features, path[0])[path[1]] = edit["value"]!.DeepClone();
        }
        else if (edit["insert"] is { } insert)
        {
            var before = (string?)edit["before"];
            var index = before is null ? features.Count : features.IndexOf(Find(features, before));
            features.Insert(index, insert.DeepClone());
        }
        else if (edit["remove"] is { } remove)
        {
            features.Remove(Find(features, (string)remove!));
        }
        else throw new FormatException($"Unknown edit: {edit.ToJsonString()}");
    }

    static JsonObject Find(JsonArray features, string id) =>
        features.Single(f => (string?)f!["id"] == id)!.AsObject();
}
