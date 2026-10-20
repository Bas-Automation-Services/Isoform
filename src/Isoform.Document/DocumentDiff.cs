using System.Text.Json.Nodes;

namespace Isoform.Document;

/// <summary>
/// The edits between two versions of a document, so a hand-typed change shows up as data. It is exact:
/// <c>DocumentEdits.Apply(old, Between(old, new))</c> gives the canonical text of <c>new</c>.
/// </summary>
/// <remarks>
/// The edits are the smallest natural ones, found on the canonical JSON:
/// <list type="bullet">
/// <item>a changed value is one <c>set</c> on the deepest path that holds it: <c>hole.diameter</c>, or an expression
/// <c>"=width / 2"</c>;</item>
/// <item>a list of plain values with one entry changed sets that entry (<c>plate.size.0</c>, as a handle drag does);
/// with more changed it sets the list (a moved point is one change, not three); a list of objects (profile edges) is
/// compared entry by entry; a list that changes length is set whole;</item>
/// <item>features, components and mates are matched by id: a missing one is removed, a new one inserted before the
/// item it now precedes, and the items outside the longest run both orders share are moved (remove, insert);</item>
/// <item>a feature that changes type, or swaps one object for another (a pattern's motion), is set whole;</item>
/// <item>parameters are set or removed one by one (<c>parameters.width</c>); a bundle's documents get their own edits
/// under <c>"document"</c>.</item>
/// </list>
/// Removes come first in each object, so a fillet can trade its edge for a loop. Should the edits ever not give the
/// new text (ids that paths cannot reach: duplicates, a '.', a feature named <c>parameters</c>), the diff is one set
/// of the whole document.
/// </remarks>
public static class DocumentDiff
{
    /// <summary>The edits that turn one part into the other.</summary>
    public static IReadOnlyList<JsonNode> Between(PartDocument old, PartDocument @new) =>
        Between(CanonicalFormat.Write(old), CanonicalFormat.Write(@new));

    /// <summary>The edits that turn one assembly into the other.</summary>
    public static IReadOnlyList<JsonNode> Between(AssemblyDocument old, AssemblyDocument @new) =>
        Between(CanonicalFormat.Write(old), CanonicalFormat.Write(@new));

    /// <summary>The edits that turn one bundle into the other: its assembly's, then each document's under <c>"document"</c>.</summary>
    public static IReadOnlyList<JsonNode> Between(Bundle old, Bundle @new) =>
        Between(CanonicalFormat.Write(old), CanonicalFormat.Write(@new));

    /// <summary>The edits between two documents of any format, given as text.</summary>
    /// <exception cref="FormatException">A document does not read (or <see cref="System.Text.Json.JsonException"/>: it is not JSON).</exception>
    public static IReadOnlyList<JsonNode> Between(string old, string @new)
    {
        var (a, b) = (DocumentEdits.Canonical(JsonNode.Parse(old)!), DocumentEdits.Canonical(JsonNode.Parse(@new)!));
        var edits = Edits(a, b);
        // The guarantee holds whatever the rules miss.
        try
        {
            if (edits is not null && DocumentEdits.Apply(old, edits) == DocumentEdits.CanonicalText(b)) return edits;
        }
        catch (EditException) { }
        return [Whole(b)];
    }

    static List<JsonNode>? Edits(JsonObject a, JsonObject b)
    {
        var format = (string?)a["format"];
        if (format != (string?)b["format"]) return null;
        var edits = new List<JsonNode>();
        if (format == PartDocument.FormatVersion)
        {
            Parameters(a["parameters"] as JsonObject ?? [], b["parameters"] as JsonObject ?? [], edits);
            return Items(a["features"], b["features"], "", edits) ? edits : null;
        }
        if (format == AssemblyDocument.FormatVersion)
            return Items(a["components"], b["components"], "components.", edits) && Items(a["mates"], b["mates"], "mates.", edits) ? edits : null;

        edits.AddRange(Edits(a["assembly"]!.AsObject(), b["assembly"]!.AsObject()) ?? [Whole(b["assembly"]!)]);
        var (old, @new) = (a["documents"]!.AsObject(), b["documents"]!.AsObject());
        foreach (var name in old.Select(d => d.Key).Union(@new.Select(d => d.Key)).Order(StringComparer.Ordinal))
        {
            List<JsonNode> inner = @new[name] is not JsonObject document ? [new JsonObject { ["remove"] = "" }]
                : old[name] is JsonObject before ? Edits(before, document) ?? [Whole(document)]
                : [Whole(document)];
            edits.AddRange(inner.Select(e => (JsonNode)new JsonObject(e.AsObject().Select(kv => KeyValuePair.Create(kv.Key, kv.Value?.DeepClone())).Prepend(KeyValuePair.Create("document", (JsonNode?)name)))));
        }
        return edits;
    }

    static void Parameters(JsonObject a, JsonObject b, List<JsonNode> edits)
    {
        foreach (var (name, _) in a)
            if (!b.ContainsKey(name)) edits.Add(Remove($"parameters.{name}"));
        foreach (var (name, value) in b)
            if (!a.ContainsKey(name) || !JsonNode.DeepEquals(a[name], value)) edits.Add(Set($"parameters.{name}", value));
    }

    /// <summary>Features, components or mates, matched by id; false when an id cannot be reached by a path.</summary>
    static bool Items(JsonNode? a, JsonNode? b, string prefix, List<JsonNode> edits)
    {
        var (olds, news) = (a as JsonArray ?? [], b as JsonArray ?? []);
        var (oldIds, newIds) = (olds.Select(n => (string)n!["id"]!).ToList(), news.Select(n => (string)n!["id"]!).ToList());
        bool Addressable(string id) => id.Length > 0 && !id.Contains('.') && (prefix != "" || id != "parameters");
        if (!oldIds.Concat(newIds).All(Addressable) || oldIds.Distinct().Count() != oldIds.Count || newIds.Distinct().Count() != newIds.Count)
            return false;

        var kept = Kept(oldIds, newIds);
        foreach (var id in oldIds.Where(id => !kept.Contains(id))) edits.Add(Remove(prefix + id));
        foreach (var id in newIds.Where(kept.Contains))
            Item(prefix + id, olds[oldIds.IndexOf(id)]!.AsObject(), news[newIds.IndexOf(id)]!.AsObject(), edits);
        for (var i = 0; i < newIds.Count; i++)
        {
            if (kept.Contains(newIds[i])) continue;
            var insert = new JsonObject { ["insert"] = news[i]!.DeepClone() };
            // Before the next item that stays, so consecutive inserts keep their order; none left means append.
            if (newIds.Skip(i + 1).FirstOrDefault(kept.Contains) is { } before) insert["before"] = before;
            edits.Add(insert);
        }
        return true;
    }

    // The longest run of ids both orders share: those stay where they are, the others are removed or (re)inserted.
    static HashSet<string> Kept(List<string> a, List<string> b)
    {
        var n = new int[a.Count + 1, b.Count + 1];
        for (var i = a.Count - 1; i >= 0; i--)
        for (var j = b.Count - 1; j >= 0; j--)
            n[i, j] = a[i] == b[j] ? n[i + 1, j + 1] + 1 : Math.Max(n[i + 1, j], n[i, j + 1]);
        var kept = new HashSet<string>();
        for (int i = 0, j = 0; i < a.Count && j < b.Count;)
            if (a[i] == b[j]) { kept.Add(a[i]); i++; j++; }
            else if (n[i + 1, j] >= n[i, j + 1]) i++;
            else j++;
        return kept;
    }

    static void Item(string path, JsonObject a, JsonObject b, List<JsonNode> edits)
    {
        if (JsonNode.DeepEquals(a, b)) return;
        // A new type, or one object traded for another (a linear pattern made circular), is a different thing: no
        // order of field edits gets there through documents that read.
        var (objectsA, objectsB) = (a.Where(kv => kv.Value is JsonObject).Select(kv => kv.Key).ToList(), b.Where(kv => kv.Value is JsonObject).Select(kv => kv.Key).ToList());
        if ((string?)a["type"] != (string?)b["type"] || objectsA.Except(objectsB).Any() && objectsB.Except(objectsA).Any())
            edits.Add(Set(path, b));
        else Fields(path, a, b, edits);
    }

    static void Fields(string path, JsonObject a, JsonObject b, List<JsonNode> edits)
    {
        foreach (var (key, _) in a)
            if (!b.ContainsKey(key)) edits.Add(Remove($"{path}.{key}"));
        foreach (var (key, value) in b)
            if (a.TryGetPropertyValue(key, out var old)) Value($"{path}.{key}", old, value, edits);
            // A new table (a component's first parameter) is its entries, as setting one adds the table.
            else if (value is JsonObject { Count: > 0 } table) Fields($"{path}.{key}", [], table, edits);
            else edits.Add(Set($"{path}.{key}", value));
    }

    static void Value(string path, JsonNode? a, JsonNode? b, List<JsonNode> edits)
    {
        if (JsonNode.DeepEquals(a, b)) return;
        switch (a, b)
        {
            case (JsonObject oa, JsonObject ob):
                Fields(path, oa, ob, edits);
                return;
            case (JsonArray xa, JsonArray xb) when xa.Count == xb.Count:
                var changed = Enumerable.Range(0, xa.Count).Where(i => !JsonNode.DeepEquals(xa[i], xb[i])).ToList();
                if (changed.Count == 1 || xb.Any(n => n is JsonObject or JsonArray))
                    foreach (var i in changed) Value($"{path}.{i}", xa[i], xb[i], edits);
                else edits.Add(Set(path, b));
                return;
        }
        edits.Add(Set(path, b));
    }

    static JsonObject Set(string path, JsonNode? value) => new() { ["set"] = path, ["value"] = value?.DeepClone() };

    static JsonObject Remove(string path) => new() { ["remove"] = path };

    static JsonObject Whole(JsonNode document) => Set("", document);
}
