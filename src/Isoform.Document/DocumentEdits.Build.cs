using System.Globalization;
using System.Text.Json.Nodes;

namespace Isoform.Document;

/// <summary>
/// The same edits on build documents, which address sections by id and the rest by field:
/// <c>{"set": "sections.legs.wall", "value": 4}</c>, <c>{"set": "sections.top.plate.face", "value": "under/face"}</c>,
/// <c>{"insert": {section}, "before": "legs"}</c> (order is priority), <c>{"remove": "sections.feet"}</c>,
/// <c>{"set": "tolerance", "value": 0.02}</c>, <c>{"set": "keepouts.1.clearance", "value": 8}</c>,
/// <c>{"set": "material", "value": {"name": "S355", "density": 7.85e-6}}</c>.
/// </summary>
public static partial class DocumentEdits
{
    /// <summary>Applies edits to a build document, in order.</summary>
    /// <exception cref="EditException">The first edit that cannot be applied.</exception>
    public static BuildDocument Apply(BuildDocument build, IEnumerable<JsonNode> edits) =>
        CanonicalFormat.ReadBuild(Apply(CanonicalFormat.Write(build), edits));

    static (JsonNode Parent, string Key, int Depth) BuildItem(JsonObject root, string[] keys) => keys switch
    {
        ["sections", var id, ..] => (root["sections"]!, IndexOf(root["sections"]!.AsArray(), id, "section").ToString(CultureInfo.InvariantCulture), 2),
        [("sections" or "design" or "material" or "tolerance" or "keepouts" or "interfaces") and var field, ..] => (root, field, 1),
        _ => throw new FormatException($"'{keys[0]}' is not a field of a build document: design, material, tolerance, sections.<id>, keepouts or interfaces"),
    };
}
