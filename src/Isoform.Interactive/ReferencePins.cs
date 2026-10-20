using System.Text.Json;
using Isoform.Modeling;

namespace Isoform.Interactive;

/// <summary>A pinned reference resolved against a result: its status and the faces it lands on now (by result index).</summary>
/// <param name="Edges">For an edge: the face pairs (lower first) of every edge it resolved to, as in <c>lineFaces</c>.</param>
public sealed record PinResolution(Reference Reference, ReferenceStatus Status, IReadOnlyList<int> Faces, IReadOnlyList<(int A, int B)> Edges);

/// <summary>
/// Every source face of one result resolved against the next: names that appeared, that are gone, and those whose
/// status changed (a face split in two, merged with another). Unchanged names are only counted.
/// </summary>
public sealed record ReferenceReport(int Kept, IReadOnlyList<string> Appeared, IReadOnlyList<string> Gone,
    IReadOnlyList<(string Source, ReferenceStatus Before, ReferenceStatus After)> Changed);

/// <summary>
/// References a front end pins to faces and edges and keeps across edits, resolved through the kernel's reference
/// layer (<see cref="ReferenceResolver"/>): by source names, never by result index, so a pin reports what an edit did
/// to its face (Resolved, Split, Merged, Gone, Ambiguous) and where the face is now.
/// </summary>
public static class ReferencePins
{
    /// <summary>
    /// Pins face <paramref name="face"/> of a result by its first source: every piece of that source, or, when the
    /// source is already in pieces, this piece by the sources around it (it resolves or becomes ambiguous, never split).
    /// </summary>
    public static Reference PinFace(string id, Topology t, int face)
    {
        var source = t.Faces[face].Sources[0];
        return t.Faces.Count(f => f.Sources.Contains(source)) > 1 ? new FacePieceReference(id, source, t.Signature(face)) : new FaceReference(id, source);
    }

    /// <summary>Pins the edge between two faces of a result: every edge between their first sources.</summary>
    public static Reference PinEdge(string id, Topology t, int a, int b) => new EdgeReference(id, t.Faces[a].Sources[0], t.Faces[b].Sources[0]);

    public static PinResolution Resolve(Reference reference, Topology t)
    {
        var r = ReferenceResolver.Resolve(reference, t);
        if (reference is not EdgeReference) return new(reference, r.Status, r.Targets, []);
        var edges = r.Targets.Select(e => t.Edges[e]).Select(e => (Math.Min(e.FaceA, e.FaceB), Math.Max(e.FaceA, e.FaceB))).ToList();
        return new(reference, r.Status, edges.SelectMany(e => new[] { e.Item1, e.Item2 }).Distinct().Order().ToList(), edges);
    }

    /// <summary>
    /// A view's topology with faces indexed as in its result: a part's own, or for a bundle every instance's in
    /// instance order with its faces named by occurrence (<c>lower/plate/z+</c>). Null when there is no geometry.
    /// </summary>
    public static Topology? TopologyOf(View view)
    {
        if (view.Part is { } part) return part.Topology;
        var (faces, edges, vertices, bodies) = (new List<TopoFace>(), new List<TopoEdge>(), new List<TopoVertex>(), 0);
        foreach (var i in view.Assembly!.Instances)
        {
            if (i.Result.Topology is not { } t) continue;
            var (f0, e0, v0) = (faces.Count, edges.Count, vertices.Count);
            faces.AddRange(t.Faces.Select(f => new TopoFace(f.Index + f0, f.Sources.Select(i.Name).ToList(), f.Body + bodies)));
            edges.AddRange(t.Edges.Select(e => new TopoEdge(e.Index + e0, e.FaceA + f0, e.FaceB + f0)));
            vertices.AddRange(t.Vertices.Select(v => new TopoVertex(v.Index + v0, v.Faces.Select(f => f + f0).ToList())));
            bodies += t.BodyCount;
        }
        return new Topology(faces, edges, vertices, bodies);
    }

    /// <summary>Every source face of <paramref name="before"/> against <paramref name="after"/>.</summary>
    public static ReferenceReport Report(Topology? before, Topology? after)
    {
        static Dictionary<string, ReferenceStatus> Statuses(Topology? t) => t is null ? []
            : t.Faces.SelectMany(f => f.Sources).Distinct().ToDictionary(s => s, s => ReferenceResolver.Resolve(new FaceReference(s, s), t).Status);
        var (old, now) = (Statuses(before), Statuses(after));
        return new(
            old.Keys.Count(s => now.TryGetValue(s, out var n) && n == old[s]),
            now.Keys.Except(old.Keys).Order(StringComparer.Ordinal).ToList(),
            old.Keys.Except(now.Keys).Order(StringComparer.Ordinal).ToList(),
            old.Keys.Intersect(now.Keys).Where(s => old[s] != now[s]).Order(StringComparer.Ordinal).Select(s => (s, old[s], now[s])).ToList());
    }

    /// <summary>
    /// The playground's <c>Resolve</c>: pins (a JSON array) resolved against a slot's last good result, as
    /// <c>{slot, hash, pins: [{id, ref, status, faces, edges?}]}</c>, or <c>{error}</c>. A pin is
    /// <c>{id, face: "plate/z+", signature?: [...]}</c> or <c>{id, edge: ["plate/z+", "hole/wall"]}</c>; given by
    /// face indices instead (<c>face: 7</c>, <c>edge: [3, 5]</c>) it is pinned on this result first. Each reply carries
    /// the pin by name as <c>ref</c>, the form to keep for later edits.
    /// </summary>
    public static string Json(string pins, Regeneration? r)
    {
        if (r?.View is not { } view || TopologyOf(view) is not { } t)
            return ViewJson.Json(w => w.WriteString("error", "There is no result to resolve the pins against."));
        List<PinResolution> resolved;
        try
        {
            using var doc = JsonDocument.Parse(pins);
            resolved = doc.RootElement.EnumerateArray().Select(p => Resolve(Read(p, t), t)).ToList();
        }
        catch (Exception e) when (Session.Unreadable(e) || e is ArgumentOutOfRangeException)
        {
            return ViewJson.Json(w => w.WriteString("error", $"The pins do not read: {e.Message}"));
        }
        return ViewJson.Json(w =>
        {
            w.WriteString("slot", r.Slot);
            w.WriteString("hash", r.Hash);
            w.WriteStartArray("pins");
            foreach (var p in resolved)
            {
                w.WriteStartObject();
                w.WriteString("id", p.Reference.Id);
                w.WriteStartObject("ref");
                Write(w, p.Reference);
                w.WriteEndObject();
                w.WriteString("status", p.Status.ToString().ToLowerInvariant());
                Numbers(w, "faces", p.Faces);
                if (p.Reference is EdgeReference)
                {
                    w.WriteStartArray("edges");
                    foreach (var (a, b) in p.Edges)
                    {
                        w.WriteStartArray();
                        w.WriteNumberValue(a);
                        w.WriteNumberValue(b);
                        w.WriteEndArray();
                    }
                    w.WriteEndArray();
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
        });
    }

    static Reference Read(JsonElement p, Topology t)
    {
        var id = p.GetProperty("id").GetString()!;
        if (p.TryGetProperty("edge", out var edge))
        {
            var (a, b) = (edge[0], edge[1]);
            return a.ValueKind == JsonValueKind.Number ? PinEdge(id, t, a.GetInt32(), b.GetInt32()) : new EdgeReference(id, a.GetString()!, b.GetString()!);
        }
        var face = p.GetProperty("face");
        if (face.ValueKind == JsonValueKind.Number) return PinFace(id, t, face.GetInt32());
        return p.TryGetProperty("signature", out var s)
            ? new FacePieceReference(id, face.GetString()!, s.EnumerateArray().Select(n => n.GetString()!).ToList())
            : new FaceReference(id, face.GetString()!);
    }

    static void Write(Utf8JsonWriter w, Reference reference)
    {
        switch (reference)
        {
            case FaceReference f:
                w.WriteString("face", f.Source);
                break;
            case FacePieceReference p:
                w.WriteString("face", p.Source);
                Strings(w, "signature", p.Signature);
                break;
            case EdgeReference e:
                Strings(w, "edge", [e.SourceA, e.SourceB]);
                break;
        }
    }

    static void Strings(Utf8JsonWriter w, string name, IEnumerable<string> values)
    {
        w.WriteStartArray(name);
        foreach (var v in values) w.WriteStringValue(v);
        w.WriteEndArray();
    }

    static void Numbers(Utf8JsonWriter w, string name, IEnumerable<int> values)
    {
        w.WriteStartArray(name);
        foreach (var v in values) w.WriteNumberValue(v);
        w.WriteEndArray();
    }
}
