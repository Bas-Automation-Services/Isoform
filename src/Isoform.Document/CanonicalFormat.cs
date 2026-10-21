using System.Collections.ObjectModel;
using System.Globalization;
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
public static partial class CanonicalFormat
{
    static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// The stored text. Parameters and expressions are kept as written (they are the design intent), so the hash
    /// covers them; a document without them has exactly the bytes it had before parameters existed.
    /// </summary>
    public static string Write(PartDocument doc)
    {
        if (!Parametric.IsParametric(doc)) return Plain(doc);
        // Read back first: fields with an expression then hold a placeholder that is never left out (as a 0 blend or
        // bulge is), so each expression lands at its key's own place in the text.
        var placed = FromNode(WithExpressions(doc));
        return Text(w => WithExpressions(placed).WriteTo(w));
    }

    public static string Hash(PartDocument doc) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Write(doc))));

    public static PartDocument Read(string text) => FromNode(JsonNode.Parse(text)!);

    public static JsonNode ToNode(PartDocument doc) => JsonNode.Parse(Write(doc))!;

    /// <summary>
    /// A document from its JSON. Any string starting with '=' in a feature is an expression (see
    /// <see cref="PartDocument.Expressions"/>); it is not evaluated here, so a bad one still reads.
    /// </summary>
    public static PartDocument FromNode(JsonNode root)
    {
        var format = (string?)root["format"];
        if (format != PartDocument.FormatVersion) throw new FormatException($"Unsupported format '{format}'.");
        if ((string?)root["units"] != PartDocument.Units) throw new FormatException("Units must be mm.");
        var features = root["features"]!.AsArray();
        var expressions = new Dictionary<string, string>();
        foreach (var f in features)
        foreach (var (key, value) in f!.AsObject())
            Collect(value, $"{(string?)f["id"]}.{key}", expressions);
        if (expressions.Count > 0)
        {
            features = features.DeepClone().AsArray();
            foreach (var path in expressions.Keys) DocumentEdits.Set(features, path, JsonNode.Parse("1"));
        }
        return new PartDocument(features.Select(n => ReadFeature(n!)).ToList())
        {
            Parameters = ReadParameters(root["parameters"]),
            Expressions = expressions.Count > 0 ? expressions : ReadOnlyDictionary<string, string>.Empty,
        };
    }

    static string Plain(PartDocument doc) => Text(w =>
    {
        w.WriteStartObject();
        w.WriteString("format", PartDocument.FormatVersion);
        w.WriteString("units", PartDocument.Units);
        w.WriteStartArray("features");
        foreach (var f in doc.Features) WriteFeature(w, f);
        w.WriteEndArray();
        w.WriteEndObject();
    });

    // The parameter table goes between the units and the features, sorted by name.
    static JsonObject WithExpressions(PartDocument doc)
    {
        var root = JsonNode.Parse(Plain(doc))!.AsObject();
        var features = root["features"]!.AsArray();
        foreach (var (path, expression) in doc.Expressions) DocumentEdits.Set(features, path, Scalar(expression));
        if (doc.Parameters.Count > 0) root.Insert(2, "parameters", WriteParameters(doc.Parameters));
        return root;
    }

    // Expressions are written "=text", so they are found anywhere without a schema; ids and names never start with '='.
    static void Collect(JsonNode? node, string path, Dictionary<string, string> found)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (key, value) in o) Collect(value, $"{path}.{key}", found);
                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++) Collect(a[i], $"{path}.{i}", found);
                break;
            case JsonValue v when v.GetValueKind() == JsonValueKind.String && ((string)v!).StartsWith('='):
                found[path] = ((string)v!)[1..];
                break;
        }
    }

    static JsonObject WriteParameters(IReadOnlyDictionary<string, string> parameters) =>
        new(parameters.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, (JsonNode?)Scalar(p.Value))));

    static IReadOnlyDictionary<string, string> ReadParameters(JsonNode? node) => node is null
        ? ReadOnlyDictionary<string, string>.Empty
        : node.AsObject().ToDictionary(p => p.Key, p => p.Value?.GetValueKind() switch
        {
            JsonValueKind.Number => ((double)p.Value!).ToString("R", CultureInfo.InvariantCulture),
            JsonValueKind.String when ((string)p.Value!).StartsWith('=') => ((string)p.Value!)[1..],
            _ => throw new FormatException($"Parameter '{p.Key}' must be a number or an expression starting with '=', got {p.Value?.ToJsonString() ?? "null"}."),
        });

    // A constant is stored as the number it is, anything else as "=expression".
    static JsonNode Scalar(string expression) =>
        double.TryParse(expression, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)
            ? JsonValue.Create(Normalize(v))
            : JsonValue.Create("=" + expression);

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
                EdgeOrLoop(w, fl.Edge, fl.Loop);
                Number(w, "radius", fl.Radius);
                break;
            case Chamfer ch:
                w.WriteString("type", "chamfer");
                EdgeOrLoop(w, ch.Edge, ch.Loop);
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
            case Lattice l:
                w.WriteString("type", "lattice");
                w.WriteString("kind", l.Kind);
                Number(w, "cell", l.Cell);
                Number(w, "thickness", l.Thickness);
                if (l.Skin != 0) Number(w, "skin", l.Skin);
                if (l.Region is { Feature: { } within }) w.WriteString("region", within);
                else if (l.Region is { } box)
                {
                    w.WriteStartObject("region");
                    Numbers(w, "origin", box.Origin!);
                    Numbers(w, "size", box.Size!);
                    w.WriteEndObject();
                }
                break;
            case CombineBodies cb:
                w.WriteString("type", "combine");
                w.WriteString("tool", cb.Tool);
                w.WriteString("op", cb.Op.ToString().ToLowerInvariant());
                if (cb.Keep) w.WriteBoolean("keep", true);
                break;
            default:
                throw new NotSupportedException(f.GetType().Name);
        }
        // Only written when used, so single-body documents keep their bytes and hash.
        if (f.Body is { } body) w.WriteString("body", body);
        w.WriteEndObject();
    }

    static Feature ReadFeature(JsonNode n) => ReadFeatureOnly(n) with { Body = (string?)n["body"] };

    static Feature ReadFeatureOnly(JsonNode n)
    {
        var id = (string)n["id"]!;
        return (string)n["type"]! switch
        {
            "block" => new Block(id, Enum.Parse<BooleanOp>((string)n["op"]!, ignoreCase: true), Arr(n["origin"], 3), Arr(n["size"], 3), (double?)n["blend"] ?? 0),
            "offset" => new Offset(id, (double)n["distance"]!),
            "hole" => new Hole(id, Arr(n["center"], 2), (double)n["diameter"]!),
            "plane-cut" => new PlaneCut(id, Arr(n["point"], 3), Arr(n["normal"], 3)),
            "shell" => new Shell(id, (double)n["thickness"]!),
            "fillet" => new Fillet(id, Edge(n), (double)n["radius"]!, (string?)n["loop"]),
            "chamfer" => new Chamfer(id, Edge(n), (double)n["distance"]!, (string?)n["loop"]),
            "extrude" => new Extrude(id, Op(n), ReadPlane(n["plane"]!), ReadProfile(n["profile"]!), (double)n["start"]!, (double)n["end"]!, (double?)n["blend"] ?? 0),
            "revolve" => new Revolve(id, Op(n), ReadPlane(n["plane"]!), ReadProfile(n["profile"]!), (double)n["angle"]!, (double?)n["blend"] ?? 0),
            "combine" => new CombineBodies(id, (string)n["tool"]!, Op(n), (bool?)n["keep"] ?? false),
            "pattern" => new Pattern(id, n["features"]!.AsArray().Select(f => (string)f!).ToArray(), ReadMotion(n)),
            "lattice" => new Lattice(id, (string)n["kind"]!, (double)n["cell"]!, (double)n["thickness"]!, (double?)n["skin"] ?? 0, ReadRegion(n["region"])),
            var t => throw new FormatException($"Unknown feature type '{t}'."),
        };
    }

    // A feature id, or a box {origin, size}.
    static LatticeRegion? ReadRegion(JsonNode? n) => n switch
    {
        null => null,
        JsonValue v => new LatticeRegion((string)v!),
        _ => new LatticeRegion(null, Arr(n["origin"], 3), Arr(n["size"], 3)),
    };

    static string[] Edge(JsonNode n) => n["edge"]?.AsArray().Select(e => (string)e!).ToArray() ?? [];

    // A loop names one face; an edge names two.
    static void EdgeOrLoop(Utf8JsonWriter w, string[] edge, string? loop)
    {
        if (loop is not null)
        {
            w.WriteString("loop", loop);
            return;
        }
        w.WriteStartArray("edge");
        foreach (var s in edge) w.WriteStringValue(s);
        w.WriteEndArray();
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
