using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using BasAutomation.Geometry;
using Isoform.Document;

namespace Isoform.Interactive;

/// <summary>
/// What the session's exports return, as JSON text: a regeneration (result v2), an applied edit list, a diff and the
/// examples. The shapes are in this project's README.
/// </summary>
/// <remarks>Written by hand with <see cref="Utf8JsonWriter"/>: reflection-based serialization would not survive trimming of the published app.</remarks>
public static class ViewJson
{
    public const int Version = 2;

    /// <summary>A regeneration: on success the canonical text, hash and everything shown but the mesh; else the error.</summary>
    public static string Write(Regeneration r) => Json(w =>
    {
        w.WriteNumber("version", Version);
        w.WriteString("slot", r.Slot);
        if (r.View is not { } view)
        {
            w.WriteString("error", r.Error);
            if (r.FailedEdit is { } failed) w.WriteNumber("failedEdit", failed);
            Timing(w, r.Timing);
            return;
        }
        w.WriteString("format", r.Format);
        w.WriteString("hash", r.Hash);
        w.WriteString("text", r.Text);

        w.WriteStartArray("diagnostics");
        foreach (var d in view.Diagnostics)
        {
            w.WriteStartObject();
            w.WriteString("code", d.Code);
            w.WriteString("feature", d.FeatureId);
            w.WriteString("message", d.Message);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("faces");
        foreach (var f in view.Faces) Strings(w, f);
        w.WriteEndArray();
        w.WriteStartArray("bodies");
        foreach (var b in view.Bodies)
        {
            w.WriteStartObject();
            w.WriteString("name", b.Name);
            w.WriteStartArray("faces");
            foreach (var f in b.Faces) w.WriteNumberValue(f);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteNumber("edges", view.Edges);
        w.WriteNumber("vertices", view.Vertices);

        var buffers = r.Buffers;
        w.WriteNumber("triangles", buffers.Triangles);
        w.WriteNumber("lines", buffers.LineEdges.Length);
        w.WriteNumber("smoothLines", buffers.SmoothLineEdges.Length);
        w.WriteStartArray("lineFaces");
        for (var k = 0; k < buffers.LineFaces.Length; k += 2)
        {
            w.WriteStartArray();
            w.WriteNumberValue(buffers.LineFaces[k]);
            w.WriteNumberValue(buffers.LineFaces[k + 1]);
            w.WriteEndArray();
        }
        w.WriteEndArray();

        w.WriteStartArray("thinSpots");
        foreach (var s in view.ThinSpots)
        {
            w.WriteStartObject();
            w.WritePropertyName("at");
            Point(w, s.At);
            w.WritePropertyName("faces");
            Strings(w, [s.A, s.B]);
            w.WriteNumber("thickness", s.Thickness);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("instances");
        foreach (var i in view.Instances)
        {
            w.WriteStartObject();
            w.WriteString("id", i.Id);
            w.WriteString("part", i.Part);
            w.WriteString("hash", i.Hash);
            // Column-major, as three.js Matrix4.fromArray reads it.
            var (o, x, y, z) = (i.Placement.Origin, i.Placement.X, i.Placement.Y, i.Placement.Z);
            w.WriteStartArray("matrix");
            foreach (var n in (double[])[x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, o.X, o.Y, o.Z, 1]) w.WriteNumberValue(n);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteNumber("reused", r.Reused);
        w.WriteNumber("meshed", r.Meshed);
        Timing(w, r.Timing);
    });

    /// <summary>Edits applied to a document: <c>{text, hash}</c>, or <c>{problem, failedEdit?}</c>.</summary>
    public static string Apply(string document, string edits)
    {
        var r = DocumentEdits.TryApply(document, edits);
        return Json(w =>
        {
            if (r.Problem is { } problem)
            {
                w.WriteString("problem", problem);
                if (r.FailedEdit is { } failed) w.WriteNumber("failedEdit", failed);
                return;
            }
            w.WriteString("text", r.Text);
            w.WriteString("hash", r.Hash);
        });
    }

    /// <summary>The edits that turn one document into the other, <c>{edits}</c>; <c>{error}</c> when one does not read.</summary>
    public static string Diff(string old, string @new)
    {
        IReadOnlyList<System.Text.Json.Nodes.JsonNode> edits;
        try { edits = DocumentDiff.Between(old, @new); }
        catch (Exception e) when (Session.Unreadable(e)) { return Json(w => w.WriteString("error", e.Message)); }
        return Json(w =>
        {
            w.WriteStartArray("edits");
            foreach (var e in edits) e.WriteTo(w);
            w.WriteEndArray();
        });
    }

    /// <summary>The examples, name → canonical document text.</summary>
    public static string Examples() => Json(w =>
    {
        foreach (var (name, doc) in Interactive.Examples.All) w.WriteString(name, CanonicalFormat.Write(doc));
        foreach (var (name, bundle) in Interactive.Examples.Bundles) w.WriteString(name, CanonicalFormat.Write(bundle));
    });

    static void Timing(Utf8JsonWriter w, Timing t)
    {
        w.WriteStartObject("timing");
        w.WriteNumber("edits", t.Edits);
        w.WriteNumber("regenerate", t.Regenerate);
        w.WriteNumber("buffers", t.Buffers);
        w.WriteEndObject();
    }

    static void Strings(Utf8JsonWriter w, IEnumerable<string> values)
    {
        w.WriteStartArray();
        foreach (var v in values) w.WriteStringValue(v);
        w.WriteEndArray();
    }

    static void Point(Utf8JsonWriter w, Point3D p)
    {
        w.WriteStartArray();
        w.WriteNumberValue(p.X);
        w.WriteNumberValue(p.Y);
        w.WriteNumberValue(p.Z);
        w.WriteEndArray();
    }

    // Relaxed escaping: face names are full of + signs, which the default encoder writes as +.
    static readonly JsonWriterOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static string Json(Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, Options))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
