using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Isoform.Document;

/// <summary>
/// Edits as data, applied to the canonical JSON so any field of any feature can be changed:
/// <c>{"set": "hole.diameter", "value": 60}</c>, <c>{"insert": {...}, "before": "fillet"}</c>, <c>{"remove": "slot"}</c>.
/// A set path may go deeper, with array indices as numbers: <c>"rib.profile.edges.1.to"</c>. A value may be an
/// expression: <c>{"set": "hole.diameter", "value": "=width / 2"}</c>. Paths starting with <c>parameters.</c> set or
/// remove a parameter: <c>{"set": "parameters.width", "value": 120}</c>, <c>{"remove": "parameters.width"}</c>.
/// </summary>
/// <remarks>
/// A path of one id is the whole feature (<c>{"set": "hole", "value": {...}}</c> replaces it); a deeper remove drops a
/// field or a list entry (<c>{"remove": "boss.blend"}</c>). The empty path is the whole document. Assemblies and
/// bundles take the same edits (see <c>DocumentEdits.Assembly.cs</c>).
/// <para>Each edit is checked as it is applied: a path that names nothing, a field the format does not have, or a
/// result that no longer reads is an <see cref="EditException"/>. <see cref="TryApply(string, string)"/> returns it
/// as a problem instead.</para>
/// </remarks>
public static partial class DocumentEdits
{
    /// <summary>Applies edits to a part, in order.</summary>
    /// <exception cref="EditException">The first edit that cannot be applied.</exception>
    public static PartDocument Apply(PartDocument doc, IEnumerable<JsonNode> edits) =>
        CanonicalFormat.Read(Apply(CanonicalFormat.Write(doc), edits));

    /// <summary>Applies edits to a part, assembly or bundle given as text, and returns the canonical text of the result.</summary>
    /// <exception cref="EditException">The first edit that cannot be applied.</exception>
    public static string Apply(string document, IEnumerable<JsonNode> edits)
    {
        var root = Canonical(JsonNode.Parse(document)!);
        var index = 0;
        foreach (var edit in edits)
        {
            try { root = Step(root, edit as JsonObject ?? throw new FormatException("an edit is a JSON object")); }
            catch (Exception e) when (IsBadData(e)) { throw new EditException(index, $"edit {index + 1} {edit?.ToJsonString()}: {e.Message}"); }
            index++;
        }
        return CanonicalText(root);
    }

    /// <summary>
    /// <see cref="Apply(string, IEnumerable{JsonNode})"/> that never throws on bad input: a document or edit list that
    /// does not read, or an edit that cannot be applied, comes back as the result's problem.
    /// </summary>
    /// <param name="document">A part, assembly or bundle.</param>
    /// <param name="edits">A JSON array of edits, or a single edit.</param>
    public static EditResult TryApply(string document, string edits)
    {
        JsonNode? parsed;
        try { parsed = JsonNode.Parse(edits); }
        catch (JsonException e) { return new EditResult(null, null, $"the edits are not JSON: {e.Message}"); }
        return TryApply(document, parsed is JsonArray list ? [.. list.Select(e => e!)] : [parsed!]);
    }

    /// <inheritdoc cref="TryApply(string, string)"/>
    public static EditResult TryApply(string document, IEnumerable<JsonNode> edits)
    {
        try
        {
            var text = Apply(document, edits);
            return new EditResult(text, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))), null);
        }
        catch (EditException e) { return new EditResult(null, null, e.Message, e.Index); }
        catch (Exception e) when (IsBadData(e)) { return new EditResult(null, null, $"the document does not read: {e.Message}"); }
    }

    // One edit on a canonical document, giving the canonical result; a bundle passes it on to one of its documents.
    static JsonObject Step(JsonObject root, JsonObject edit)
    {
        if (Format(root) == Bundle.FormatVersion) return InBundle(root, edit);
        if (edit.ContainsKey("set"))
        {
            var path = PathOf(edit, "set");
            if (!edit.TryGetPropertyValue("value", out var given)) throw new FormatException("a set needs a value");
            // Through text, so a value built in code (an int, say) reads back like one parsed from a file.
            var value = given is null ? null : JsonNode.Parse(given.ToJsonString());
            if (path.Length == 0) return Read(value ?? throw new FormatException("a document cannot be null"));
            var keys = path.Split('.');
            var (parent, key, depth) = Locate(root, keys, create: true);
            Put(parent, key, value);
            var result = Read(root);
            // The canonical form drops what the format does not know: a misspelt field must not pass silently.
            if (keys.Length > depth && !IsDefault(value) && !Exists(result, keys))
                throw new FormatException($"'{path}' is not a field of the document");
            return result;
        }
        if (edit.ContainsKey("insert"))
        {
            var item = edit["insert"] as JsonObject;
            if (item?["id"] is not JsonValue id || id.GetValueKind() != JsonValueKind.String)
                throw new FormatException("an insert gives an object with an id");
            var (list, what) = ListFor(root, item);
            list.Insert(edit["before"] is null ? list.Count : IndexOf(list, PathOf(edit, "before"), what), item.DeepClone());
            return Read(root);
        }
        if (edit.ContainsKey("remove"))
        {
            var path = PathOf(edit, "remove");
            if (path.Length == 0) throw new FormatException("only a bundle's document can be removed whole");
            var (parent, key, _) = Locate(root, path.Split('.'), create: false);
            if (parent is JsonArray array) array.RemoveAt(Index(array, key));
            else if (!parent.AsObject().Remove(key)) throw new FormatException($"'{path}' is not in the document");
            return Read(root);
        }
        throw new FormatException("an edit has a set, an insert or a remove");
    }

    /// <summary>
    /// The node holding the field a path names, and the field's key in it (an index in a list). <c>Depth</c> is the
    /// number of keys that name the item itself: a feature or the parameter table, a component or a mate. With
    /// <paramref name="create"/> missing objects on the way are added, as a first parameter needs its table.
    /// </summary>
    static (JsonNode Parent, string Key, int Depth) Locate(JsonObject root, string[] keys, bool create)
    {
        var (parent, key, depth) = Item(root, keys);
        for (var i = depth; i < keys.Length; i++)
        {
            var node = Get(parent, key);
            if (node is null && create && parent is JsonObject) Put(parent, key, node = new JsonObject());
            if (node is not (JsonObject or JsonArray)) throw new FormatException($"'{string.Join('.', keys[..i])}' has no field '{keys[i]}'");
            (parent, key) = (node, keys[i]);
        }
        return (parent, key, depth);
    }

    // The parameter table is "parameters", so no feature may have that id; the rules say so.
    static (JsonNode Parent, string Key, int Depth) Item(JsonObject root, string[] keys)
    {
        if (Format(root) == AssemblyDocument.FormatVersion) return AssemblyItem(root, keys);
        if (keys[0] == "parameters") return (root, "parameters", 1);
        var features = root["features"]!.AsArray();
        return (features, IndexOf(features, keys[0], "feature").ToString(CultureInfo.InvariantCulture), 1);
    }

    static JsonNode? Get(JsonNode parent, string key) => parent is JsonArray a ? a[Index(a, key)] : parent[key];

    static void Put(JsonNode parent, string key, JsonNode? value)
    {
        if (parent is JsonArray a) a[Index(a, key)] = value;
        else parent[key] = value;
    }

    static int Index(JsonArray a, string key) =>
        int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < a.Count
            ? i : throw new FormatException($"there is no entry '{key}' in a list of {a.Count}");

    // The first item with the id: a duplicate id is the rules' to report.
    static int IndexOf(JsonArray list, string id, string what)
    {
        for (var i = 0; i < list.Count; i++)
            if (list[i]?["id"] is JsonValue v && v.TryGetValue<string>(out var s) && s == id) return i;
        throw new FormatException($"there is no {what} '{id}'");
    }

    static bool Exists(JsonObject root, string[] keys)
    {
        try
        {
            var (parent, key, _) = Locate(root, keys, create: false);
            return Get(parent, key) is not null;
        }
        catch (FormatException) { return false; }
    }

    // What the canonical form leaves out when it is the value: a zero blend, no loop, an empty parameter table.
    static bool IsDefault(JsonNode? value) => value switch
    {
        null => true,
        JsonValue v => v.GetValueKind() is JsonValueKind.False or JsonValueKind.Null || v.GetValueKind() == JsonValueKind.Number && (double)v == 0,
        JsonObject o => o.Count == 0,
        JsonArray a => a.Count == 0,
        _ => false,
    };

    static string PathOf(JsonObject edit, string key) =>
        edit[key] is JsonValue v && v.TryGetValue<string>(out var path) ? path : throw new FormatException($"'{key}' takes a string");

    static JsonObject Read(JsonNode root)
    {
        try { return Canonical(root); }
        catch (Exception e) when (IsBadData(e)) { throw new FormatException($"the result does not read: {e.Message}"); }
    }

    static string? Format(JsonNode root) => (string?)root["format"];

    internal static JsonObject Canonical(JsonNode root) => JsonNode.Parse(CanonicalText(root))!.AsObject();

    /// <summary>The canonical text of a part, assembly or bundle given as JSON; a bundle's documents are made canonical too.</summary>
    internal static string CanonicalText(JsonNode root) => Format(root) switch
    {
        PartDocument.FormatVersion => CanonicalFormat.Write(CanonicalFormat.FromNode(root)),
        AssemblyDocument.FormatVersion => CanonicalFormat.Write(CanonicalFormat.ReadAssembly(root.ToJsonString())),
        Bundle.FormatVersion => CanonicalFormat.Write(new Bundle(CanonicalFormat.ReadAssembly(root["assembly"]!.ToJsonString()),
            root["documents"]!.AsObject().ToDictionary(d => d.Key, d => CanonicalText(d.Value!)))),
        var format => throw new FormatException($"Unsupported format '{format}'."),
    };

    // What malformed JSON or a wrong field type surfaces as while reading; anything else is a bug and stays one.
    static bool IsBadData(Exception e) => e is FormatException or JsonException or InvalidOperationException or KeyNotFoundException
        or InvalidCastException or ArgumentException or NullReferenceException or IndexOutOfRangeException or OverflowException;

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
}

/// <summary>An edit that cannot be applied; the message says which and why.</summary>
/// <param name="index">The edit's place in the list, from 0.</param>
public sealed class EditException(int index, string message) : FormatException(message)
{
    public int Index { get; } = index;
}

/// <summary>
/// What <see cref="DocumentEdits.TryApply(string, string)"/> gives: the edited document's canonical text and hash,
/// or the problem (and, when an edit caused it, that edit's index).
/// </summary>
public sealed record EditResult(string? Text, string? Hash, string? Problem, int? FailedEdit = null);
