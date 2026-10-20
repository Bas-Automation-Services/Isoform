using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using BasAutomation.Geometry;

namespace Isoform.Interactive;

/// <summary>A slot's handles as JSON (the shape is in the README), written by hand so trimming keeps it working.</summary>
public static class HandleJson
{
    static readonly JsonWriterOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary><c>{slot, hash, handles}</c>; no hash and no handles when the slot has no regeneration.</summary>
    public static string Write(string slot, Regeneration? r)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, Options))
        {
            w.WriteStartObject();
            w.WriteString("slot", slot);
            if (r?.Hash is { } hash) w.WriteString("hash", hash);
            w.WriteStartArray("handles");
            foreach (var h in Handles.Of(r)) Write(w, h);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    static void Write(Utf8JsonWriter w, Handle h)
    {
        w.WriteStartObject();
        w.WriteString("id", h.Id);
        w.WriteString("feature", h.Feature);
        w.WriteString("kind", h.Kind.ToString().ToLowerInvariant());
        w.WriteString("label", h.Label);
        Vector(w, "anchor", h.Anchor.X, h.Anchor.Y, h.Anchor.Z);
        Vector(w, "axis", h.Axis.X, h.Axis.Y, h.Axis.Z);
        switch (h.Kind)
        {
            case HandleKind.Planar:
                Vector(w, "u", h.U.X, h.U.Y, h.U.Z);
                Vector(w, "v", h.V.X, h.V.Y, h.V.Z);
                break;
            case HandleKind.Angle:
                Vector(w, "center", h.Center.X, h.Center.Y, h.Center.Z);
                break;
            case HandleKind.Frame:
                // Column-major, as three.js Matrix4.fromArray reads it (the instance's matrix in result v2).
                var (o, x, y, z) = (h.Frame.Origin, h.Frame.X, h.Frame.Y, h.Frame.Z);
                Numbers(w, "matrix", [x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, o.X, o.Y, o.Z, 1]);
                break;
        }
        if (h.Kind == HandleKind.Planar) Numbers(w, "value", h.Value);
        else if (h.Kind != HandleKind.Frame) w.WriteNumber("value", h.Value[0]);
        if (h.Min is { } min) w.WriteNumber("min", min);
        if (h.Max is { } max) w.WriteNumber("max", max);
        if (h.Kind != HandleKind.Frame) w.WriteNumber("step", h.Step);
        w.WriteStartArray("faces");
        foreach (var f in h.Faces) w.WriteStringValue(f);
        w.WriteEndArray();

        w.WriteStartArray("edits");
        if (h.Placement is { } p)
        {
            // A frame sets the placement's vectors from the dragged matrix: origin, x (its first column), normal (third).
            foreach (var (field, v) in (ReadOnlySpan<(string, double[])>)[("origin", p.Origin), ("x", p.X), ("normal", p.Normal)])
            {
                w.WriteStartObject();
                w.WriteString("path", $"components.{h.Feature}.placement.{field}");
                Numbers(w, "base", v);
                w.WriteEndObject();
            }
        }
        foreach (var e in h.Edits)
        {
            w.WriteStartObject();
            w.WriteString("path", e.Path);
            w.WriteNumber("base", e.Base);
            Numbers(w, "per", e.Per);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        if (h.Expression is { } expression)
        {
            w.WriteBoolean("readOnly", true);
            w.WriteString("expression", expression);
        }
        if (h.Count is { } count)
        {
            w.WriteStartObject("count");
            w.WriteString("path", count.Path);
            w.WriteNumber("value", count.Value);
            w.WriteNumber("min", count.Min);
            w.WriteEndObject();
        }
        w.WriteEndObject();
    }

    static void Vector(Utf8JsonWriter w, string name, double x, double y, double z) => Numbers(w, name, [x, y, z]);

    static void Numbers(Utf8JsonWriter w, string name, ReadOnlySpan<double> values)
    {
        w.WriteStartArray(name);
        // Rounded to well below any tolerance, so 1e-17 noise does not reach the page as 1.0000000000000002.
        foreach (var v in values) w.WriteNumberValue(Math.Round(v, 9));
        w.WriteEndArray();
    }
}
