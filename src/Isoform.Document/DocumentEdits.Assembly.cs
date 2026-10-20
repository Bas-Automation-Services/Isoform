using System.Globalization;
using System.Text.Json.Nodes;

namespace Isoform.Document;

/// <summary>
/// The same edits on assemblies, which address components and mates by id:
/// <c>{"set": "components.upper.placement.origin", "value": [0, 20, 15]}</c>,
/// <c>{"set": "components.bolt.parameters.length", "value": 30}</c>, <c>{"set": "mates.seat.offset", "value": 2}</c>,
/// <c>{"insert": {component or mate}, "before": "pin"}</c> (a component names a part, a mate has a kind),
/// <c>{"remove": "components.pin"}</c>.
/// </summary>
/// <remarks>
/// In a bundle a plain edit changes the assembly; with <c>"document"</c> it changes that document:
/// <c>{"document": "bracket.json", "set": "plate.size.0", "value": 50}</c>. The empty path adds or replaces a document
/// whole (<c>{"document": "bolt.json", "set": "", "value": {...}}</c>) or drops it (<c>{"document": "bolt.json", "remove": ""}</c>).
/// A component that pins its part's hash notices the edited part, as it should.
/// </remarks>
public static partial class DocumentEdits
{
    /// <summary>Applies edits to an assembly, in order.</summary>
    /// <exception cref="EditException">The first edit that cannot be applied.</exception>
    public static AssemblyDocument Apply(AssemblyDocument assembly, IEnumerable<JsonNode> edits) =>
        CanonicalFormat.ReadAssembly(Apply(CanonicalFormat.Write(assembly), edits));

    /// <summary>Applies edits to a bundle, in order; every document comes back in canonical form.</summary>
    /// <exception cref="EditException">The first edit that cannot be applied.</exception>
    public static Bundle Apply(Bundle bundle, IEnumerable<JsonNode> edits) =>
        CanonicalFormat.ReadBundle(Apply(CanonicalFormat.Write(bundle), edits));

    static (JsonNode Parent, string Key, int Depth) AssemblyItem(JsonObject root, string[] keys)
    {
        if (keys is not [("components" or "mates") and var list, var id, ..])
            throw new FormatException($"an assembly path starts with components.<id> or mates.<id>, got '{string.Join('.', keys)}'");
        var items = root[list] as JsonArray ?? [];
        return (items, IndexOf(items, id, list[..^1]).ToString(CultureInfo.InvariantCulture), 2);
    }

    // Where an insert goes: a part's features, or an assembly's components (they name a part) or mates (they have a kind).
    static (JsonArray List, string What) ListFor(JsonObject root, JsonObject item)
    {
        if (Format(root) != AssemblyDocument.FormatVersion) return (root["features"]!.AsArray(), "feature");
        var list = item.ContainsKey("part") ? "components" : item.ContainsKey("kind") ? "mates"
            : throw new FormatException("an assembly inserts a component (with a part) or a mate (with a kind)");
        return ((root[list] ??= new JsonArray()).AsArray(), list[..^1]);
    }

    static JsonObject InBundle(JsonObject root, JsonObject edit)
    {
        if (!edit.ContainsKey("document"))
        {
            root["assembly"] = Step(root["assembly"]!.AsObject(), edit);
            return Read(root);
        }
        var name = PathOf(edit, "document");
        var inner = new JsonObject(edit.Where(kv => kv.Key != "document").Select(kv => KeyValuePair.Create(kv.Key, kv.Value?.DeepClone())));
        var documents = root["documents"]!.AsObject();
        if (inner.ContainsKey("remove") && PathOf(inner, "remove") == "")
        {
            if (!documents.Remove(name)) throw new FormatException($"the bundle has no document '{name}'");
        }
        else if (documents[name] is JsonObject document) documents[name] = Step(document, inner);
        else if (inner.ContainsKey("set") && PathOf(inner, "set") == "") documents[name] = Step([], inner);
        else throw new FormatException($"the bundle has no document '{name}'");
        return Read(root);
    }
}
