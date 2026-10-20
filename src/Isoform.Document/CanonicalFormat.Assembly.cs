using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Isoform.Document;

public static partial class CanonicalFormat
{
    /// <summary>The format a document's text declares: part, assembly or bundle.</summary>
    public static string? FormatOf(string text) => (string?)JsonNode.Parse(text)?["format"];

    public static string Write(AssemblyDocument assembly) => Text(w => WriteAssembly(w, assembly));

    public static string Hash(AssemblyDocument assembly) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Write(assembly))));

    public static AssemblyDocument ReadAssembly(string text) => ReadAssembly(JsonNode.Parse(text)!);

    /// <summary>A bundle holds every document in canonical form, keyed by the path the assembly uses.</summary>
    public static string Write(Bundle bundle) => Text(w =>
    {
        w.WriteStartObject();
        w.WriteString("format", Bundle.FormatVersion);
        w.WriteString("units", PartDocument.Units);
        w.WritePropertyName("assembly");
        WriteAssembly(w, bundle.Assembly);
        w.WriteStartObject("documents");
        foreach (var (path, text) in bundle.Documents.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            w.WritePropertyName(path);
            JsonNode.Parse(text)!.WriteTo(w);
        }
        w.WriteEndObject();
        w.WriteEndObject();
    });

    public static Bundle ReadBundle(string text)
    {
        var root = JsonNode.Parse(text)!;
        if ((string?)root["format"] != Bundle.FormatVersion) throw new FormatException($"Unsupported format '{(string?)root["format"]}'.");
        var documents = root["documents"]!.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value!.ToJsonString());
        return new Bundle(ReadAssembly(root["assembly"]!), documents);
    }

    static void WriteAssembly(Utf8JsonWriter w, AssemblyDocument assembly)
    {
        w.WriteStartObject();
        w.WriteString("format", AssemblyDocument.FormatVersion);
        w.WriteString("units", PartDocument.Units);
        w.WriteStartArray("components");
        foreach (var c in assembly.Components)
        {
            w.WriteStartObject();
            w.WriteString("id", c.Id);
            w.WriteString("part", c.Part);
            if (c.Hash is { } hash) w.WriteString("hash", hash);
            if (c.Grounded) w.WriteBoolean("grounded", true);
            if (c.Parameters is { Count: > 0 } parameters)
            {
                w.WritePropertyName("parameters");
                WriteParameters(parameters).WriteTo(w);
            }
            w.WriteStartObject("placement");
            Numbers(w, "origin", c.Placement.Origin);
            Numbers(w, "x", c.Placement.X);
            Numbers(w, "normal", c.Placement.Normal);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        // Only written when used, like every optional key.
        if (assembly.Mates is { Count: > 0 } mates)
        {
            w.WriteStartArray("mates");
            foreach (var m in mates)
            {
                w.WriteStartObject();
                w.WriteString("id", m.Id);
                w.WriteString("kind", m.Kind.ToString().ToLowerInvariant());
                w.WriteString("a", m.A);
                w.WriteString("b", m.B);
                if (m.Offset != 0) Number(w, "offset", m.Offset);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }

    static AssemblyDocument ReadAssembly(JsonNode root)
    {
        if ((string?)root["format"] != AssemblyDocument.FormatVersion) throw new FormatException($"Unsupported format '{(string?)root["format"]}'.");
        if ((string?)root["units"] != PartDocument.Units) throw new FormatException("Units must be mm.");
        return new AssemblyDocument(root["components"]!.AsArray().Select(n => n!).Select(n =>
        {
            var p = n["placement"];
            var placement = p is null ? Placement.Identity : new Placement(Arr(p["origin"], 3), Arr(p["x"], 3), Arr(p["normal"], 3));
            var parameters = n["parameters"] is { } values ? ReadParameters(values) : null;
            return new Component((string)n["id"]!, (string)n["part"]!, placement, (string?)n["hash"], (bool?)n["grounded"] ?? false, parameters);
        }).ToList(), root["mates"]?.AsArray().Select(n => n!).Select(n => new Mate((string)n["id"]!,
            Enum.Parse<MateKind>((string)n["kind"]!, ignoreCase: true), (string)n["a"]!, (string)n["b"]!, (double?)n["offset"] ?? 0)).ToList());
    }

    static string Text(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, WriterOptions)) write(w);
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }
}
