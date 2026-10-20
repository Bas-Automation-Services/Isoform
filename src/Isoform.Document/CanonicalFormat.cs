using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Isoform.Document;

/// <summary>
/// The one text form of a document: fixed key order, LF newlines, shortest round-trip doubles.
/// Same document → same bytes → same hash, so it diffs and merges cleanly in git.
/// </summary>
public static class CanonicalFormat
{
    static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Write(PartDocument doc)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteString("format", PartDocument.FormatVersion);
            w.WriteString("units", PartDocument.Units);
            w.WriteStartArray("features");
            foreach (var f in doc.Features) WriteFeature(w, f);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    public static string Hash(PartDocument doc) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Write(doc))));

    public static PartDocument Read(string text) => FromNode(JsonNode.Parse(text)!);

    public static JsonNode ToNode(PartDocument doc) => JsonNode.Parse(Write(doc))!;

    public static PartDocument FromNode(JsonNode root)
    {
        var format = (string?)root["format"];
        if (format != PartDocument.FormatVersion) throw new FormatException($"Unsupported format '{format}'.");
        if ((string?)root["units"] != PartDocument.Units) throw new FormatException("Units must be mm.");
        return new PartDocument(root["features"]!.AsArray().Select(n => ReadFeature(n!)).ToList());
    }

    static void WriteFeature(Utf8JsonWriter w, Feature f)
    {
        w.WriteStartObject();
        w.WriteString("id", f.Id);
        switch (f)
        {
            case Block b:
                w.WriteString("type", "block");
                w.WriteString("op", b.Op.ToString().ToLowerInvariant());
                Numbers(w, "origin", b.Origin);
                Numbers(w, "size", b.Size);
                break;
            case Hole h:
                w.WriteString("type", "hole");
                Numbers(w, "center", h.Center);
                Number(w, "diameter", h.Diameter);
                break;
            case PlaneCut c:
                w.WriteString("type", "plane-cut");
                Numbers(w, "point", c.Point);
                Numbers(w, "normal", c.Normal);
                break;
            case Fillet fl:
                w.WriteString("type", "fillet");
                w.WriteStartArray("edge");
                foreach (var s in fl.Edge) w.WriteStringValue(s);
                w.WriteEndArray();
                Number(w, "radius", fl.Radius);
                break;
            default:
                throw new NotSupportedException(f.GetType().Name);
        }
        w.WriteEndObject();
    }

    static Feature ReadFeature(JsonNode n)
    {
        var id = (string)n["id"]!;
        return (string)n["type"]! switch
        {
            "block" => new Block(id, Enum.Parse<BooleanOp>((string)n["op"]!, ignoreCase: true), Arr(n["origin"], 3), Arr(n["size"], 3)),
            "hole" => new Hole(id, Arr(n["center"], 2), (double)n["diameter"]!),
            "plane-cut" => new PlaneCut(id, Arr(n["point"], 3), Arr(n["normal"], 3)),
            "fillet" => new Fillet(id, n["edge"]!.AsArray().Select(e => (string)e!).ToArray(), (double)n["radius"]!),
            var t => throw new FormatException($"Unknown feature type '{t}'."),
        };
    }

    static double[] Arr(JsonNode? n, int length)
    {
        var values = n!.AsArray().Select(v => (double)v!).ToArray();
        if (values.Length != length) throw new FormatException($"Expected {length} numbers, got {values.Length}.");
        return values;
    }

    static void Numbers(Utf8JsonWriter w, string name, double[] values)
    {
        w.WriteStartArray(name);
        foreach (var v in values) w.WriteNumberValue(Normalize(v));
        w.WriteEndArray();
    }

    static void Number(Utf8JsonWriter w, string name, double v) => w.WriteNumber(name, Normalize(v));

    // -0 and 0 must hash the same; NaN/Infinity are never valid parameters.
    static double Normalize(double v) =>
        double.IsFinite(v) ? (v == 0 ? 0 : v) : throw new FormatException("Parameters must be finite.");
}
