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
                Blend(w, b.Blend);
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
            case Chamfer ch:
                w.WriteString("type", "chamfer");
                w.WriteStartArray("edge");
                foreach (var s in ch.Edge) w.WriteStringValue(s);
                w.WriteEndArray();
                Number(w, "distance", ch.Distance);
                break;
            case Shell sh:
                w.WriteString("type", "shell");
                Number(w, "thickness", sh.Thickness);
                break;
            case Extrude e:
                w.WriteString("type", "extrude");
                w.WriteString("op", e.Op.ToString().ToLowerInvariant());
                WritePlane(w, e.Plane);
                WriteProfile(w, e.Profile);
                Number(w, "start", e.Start);
                Number(w, "end", e.End);
                Blend(w, e.Blend);
                break;
            case Pattern p:
                w.WriteString("type", "pattern");
                w.WriteStartArray("features");
                foreach (var id in p.Features) w.WriteStringValue(id);
                w.WriteEndArray();
                switch (p.Motion)
                {
                    case LinearMotion l:
                        w.WriteStartObject("linear");
                        Numbers(w, "direction", l.Direction);
                        Number(w, "spacing", l.Spacing);
                        w.WriteNumber("count", l.Count);
                        break;
                    case CircularMotion c:
                        w.WriteStartObject("circular");
                        Numbers(w, "origin", c.Origin);
                        Numbers(w, "axis", c.Axis);
                        w.WriteNumber("count", c.Count);
                        Number(w, "angle", c.Angle);
                        break;
                    case MirrorMotion m:
                        w.WriteStartObject("mirror");
                        Numbers(w, "point", m.Point);
                        Numbers(w, "normal", m.Normal);
                        break;
                }
                w.WriteEndObject();
                break;
            case Revolve r:
                w.WriteString("type", "revolve");
                w.WriteString("op", r.Op.ToString().ToLowerInvariant());
                WritePlane(w, r.Plane);
                WriteProfile(w, r.Profile);
                Number(w, "angle", r.Angle);
                Blend(w, r.Blend);
                break;
            case Offset o:
                w.WriteString("type", "offset");
                Number(w, "distance", o.Distance);
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
            "block" => new Block(id, Enum.Parse<BooleanOp>((string)n["op"]!, ignoreCase: true), Arr(n["origin"], 3), Arr(n["size"], 3), (double?)n["blend"] ?? 0),
            "offset" => new Offset(id, (double)n["distance"]!),
            "hole" => new Hole(id, Arr(n["center"], 2), (double)n["diameter"]!),
            "plane-cut" => new PlaneCut(id, Arr(n["point"], 3), Arr(n["normal"], 3)),
            "shell" => new Shell(id, (double)n["thickness"]!),
            "fillet" => new Fillet(id, n["edge"]!.AsArray().Select(e => (string)e!).ToArray(), (double)n["radius"]!),
            "chamfer" => new Chamfer(id, n["edge"]!.AsArray().Select(e => (string)e!).ToArray(), (double)n["distance"]!),
            "extrude" => new Extrude(id, Op(n), ReadPlane(n["plane"]!), ReadProfile(n["profile"]!), (double)n["start"]!, (double)n["end"]!, (double?)n["blend"] ?? 0),
            "revolve" => new Revolve(id, Op(n), ReadPlane(n["plane"]!), ReadProfile(n["profile"]!), (double)n["angle"]!, (double?)n["blend"] ?? 0),
            "pattern" => new Pattern(id, n["features"]!.AsArray().Select(f => (string)f!).ToArray(), ReadMotion(n)),
            var t => throw new FormatException($"Unknown feature type '{t}'."),
        };
    }

    static PatternMotion ReadMotion(JsonNode n) =>
        n["linear"] is { } l ? new LinearMotion(Arr(l["direction"], 3), (double)l["spacing"]!, (int)l["count"]!)
        : n["circular"] is { } c ? new CircularMotion(Arr(c["origin"], 3), Arr(c["axis"], 3), (int)c["count"]!, (double)c["angle"]!)
        : n["mirror"] is { } m ? new MirrorMotion(Arr(m["point"], 3), Arr(m["normal"], 3))
        : throw new FormatException("A pattern needs a linear, circular or mirror motion.");

    // Only written when used, so documents without blends keep their bytes and hash.
    static void Blend(Utf8JsonWriter w, double blend)
    {
        if (blend != 0) Number(w, "blend", blend);
    }

    static BooleanOp Op(JsonNode n) => Enum.Parse<BooleanOp>((string)n["op"]!, ignoreCase: true);

    static SketchPlane ReadPlane(JsonNode n) => new(Arr(n["origin"], 3), Arr(n["x"], 3), Arr(n["normal"], 3));

    static Profile ReadProfile(JsonNode n) => new(Arr(n["start"], 2), n["edges"]!.AsArray()
        .Select(e => new ProfileEdge((string)e!["id"]!, Arr(e["to"], 2), (double?)e["bulge"] ?? 0)).ToList());

    static void WritePlane(Utf8JsonWriter w, SketchPlane p)
    {
        w.WriteStartObject("plane");
        Numbers(w, "origin", p.Origin);
        Numbers(w, "x", p.X);
        Numbers(w, "normal", p.Normal);
        w.WriteEndObject();
    }

    // A line has no bulge key, so documents without arcs stay as short as possible.
    static void WriteProfile(Utf8JsonWriter w, Profile p)
    {
        w.WriteStartObject("profile");
        Numbers(w, "start", p.Start);
        w.WriteStartArray("edges");
        foreach (var e in p.Edges)
        {
            w.WriteStartObject();
            w.WriteString("id", e.Id);
            Numbers(w, "to", e.To);
            if (e.Bulge != 0) Number(w, "bulge", e.Bulge);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
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
