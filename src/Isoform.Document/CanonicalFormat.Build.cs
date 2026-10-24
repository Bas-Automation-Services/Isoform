using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Isoform.Document;

public static partial class CanonicalFormat
{
    /// <summary>
    /// A build document's text: format, units, design, material, tolerance, sections in priority order, keep-outs and
    /// interfaces. A section is its id, its method (<c>"plate": {"face": …}</c> or <c>"tube": {"along": …}</c>) and its
    /// sizes; material, keep-outs and interfaces are only written when given.
    /// </summary>
    public static string Write(BuildDocument build) => Text(w =>
    {
        w.WriteStartObject();
        w.WriteString("format", BuildDocument.FormatVersion);
        w.WriteString("units", PartDocument.Units);
        w.WriteString("design", build.Design);
        if (build.Material is { } material)
        {
            w.WriteStartObject("material");
            w.WriteString("name", material.Name);
            Number(w, "density", material.Density);
            w.WriteEndObject();
        }
        Number(w, "tolerance", build.Tolerance);
        w.WriteStartArray("sections");
        foreach (var section in build.Sections) WriteSection(w, section);
        w.WriteEndArray();
        if (build.Keepouts is { Count: > 0 } keepouts)
        {
            w.WriteStartArray("keepouts");
            foreach (var k in keepouts)
            {
                w.WriteStartObject();
                w.WriteString("body", k.Body);
                Number(w, "clearance", k.Clearance);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        if (build.Interfaces is { Count: > 0 } interfaces) Strings(w, "interfaces", [.. interfaces]);
        w.WriteEndObject();
    });

    public static string Hash(BuildDocument build) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Write(build))));

    /// <summary>A build document from its text; anything that cannot be built from says which section and why.</summary>
    public static BuildDocument ReadBuild(string text)
    {
        var root = JsonNode.Parse(text)!;
        if ((string?)root["format"] != BuildDocument.FormatVersion) throw new FormatException($"Unsupported format '{(string?)root["format"]}'.");
        if ((string?)root["units"] != PartDocument.Units) throw new FormatException("Units must be mm.");
        var sections = (root["sections"] ?? throw new FormatException("A build document needs its sections.")).AsArray().Select(n => ReadSection(n!)).ToList();
        foreach (var twice in sections.GroupBy(s => s.Id).Where(g => g.Count() > 1))
            throw new FormatException($"Section id '{twice.Key}' is used twice.");
        var tolerance = (double?)root["tolerance"] ?? BuildDocument.DefaultTolerance;
        if (!(tolerance > 0)) throw new FormatException("The tolerance must be positive.");
        var material = root["material"] is { } m ? new Material(Required(m, "name", "The material"), (double)m["density"]!) : null;
        if (material is { Density: not > 0 }) throw new FormatException("The material's density must be positive.");
        var keepouts = root["keepouts"]?.AsArray().Select(k => new Keepout(Required(k!, "body", "A keep-out"), (double?)k!["clearance"] ?? 0)).ToList();
        if (keepouts?.FirstOrDefault(k => !(k.Clearance >= 0)) is { } negative) throw new FormatException($"Keep-out '{negative.Body}' needs a clearance of 0 or more.");
        return new BuildDocument(Required(root, "design", "A build document"), sections, tolerance, material, keepouts,
            root["interfaces"]?.AsArray().Select(i => (string)i!).ToList());
    }

    static void WriteSection(Utf8JsonWriter w, BuildSection section)
    {
        w.WriteStartObject();
        w.WriteString("id", section.Id);
        switch (section)
        {
            case PlateSection p:
                w.WriteStartObject("plate");
                if (p.Face is { } face) w.WriteString("face", face);
                else WritePlane(w, p.Plane!);
                if (p.Near is { } near) Numbers(w, "near", near);
                w.WriteEndObject();
                Number(w, "thickness", p.Thickness);
                break;
            case TubeSection t:
                w.WriteStartObject("tube");
                if (t.Along is { } along) w.WriteString("along", along);
                else
                {
                    w.WriteStartArray("line");
                    foreach (var point in t.Line!)
                    {
                        w.WriteStartArray();
                        foreach (var v in point) w.WriteNumberValue(Normalize(v));
                        w.WriteEndArray();
                    }
                    w.WriteEndArray();
                }
                w.WriteEndObject();
                Number(w, "outside", t.Outside);
                Number(w, "wall", t.Wall);
                break;
            default:
                throw new NotSupportedException(section.GetType().Name);
        }
        w.WriteEndObject();
    }

    static BuildSection ReadSection(JsonNode n)
    {
        var id = Required(n, "id", "A section");
        if (id.Length == 0 || id.Contains('.') || id.Contains('/')) throw new FormatException($"Section id '{id}' must be non-empty, without '.' or '/'.");
        string Problem(string what) => $"Section '{id}': {what}.";
        switch (n["plate"], n["tube"])
        {
            case ({ } plate, null):
                var face = (string?)plate["face"];
                if ((face is null) == (plate["plane"] is null)) throw new FormatException(Problem("a plate is on a face or on a plane, one of the two"));
                var thickness = (double?)n["thickness"] ?? throw new FormatException(Problem("a plate needs a thickness"));
                if (!(thickness > 0)) throw new FormatException(Problem("the thickness must be positive"));
                return new PlateSection(id, thickness, face, plate["plane"] is { } plane ? ReadPlane(plane) : null,
                    plate["near"] is { } near ? Arr(near, 3) : null);
            case (null, { } tube):
                var along = (string?)tube["along"];
                if ((along is null) == (tube["line"] is null)) throw new FormatException(Problem("a tube runs along a cylinder or a line, one of the two"));
                var line = tube["line"]?.AsArray().Select(p => Arr(p, 3)).ToArray();
                if (line is not null && line.Length != 2) throw new FormatException(Problem("a tube's line is two points"));
                var (outside, wall) = ((double?)n["outside"], (double?)n["wall"]);
                if (outside is null || wall is null) throw new FormatException(Problem("a tube needs its outside diameter and wall"));
                if (!(wall > 0 && 2 * wall <= outside)) throw new FormatException(Problem("the wall must be positive and at most half the outside diameter"));
                return new TubeSection(id, outside.Value, wall.Value, along, line);
            default:
                throw new FormatException(Problem("a section is a plate or a tube, one of the two"));
        }
    }

    static string Required(JsonNode n, string key, string what) =>
        n[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? (string)v! : throw new FormatException($"{what} needs '{key}', a string.");
}
