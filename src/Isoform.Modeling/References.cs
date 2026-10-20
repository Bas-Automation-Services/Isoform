namespace Isoform.Modeling;

public enum ReferenceStatus { Resolved, Merged, Split, Gone, Ambiguous }

public sealed record Resolution(ReferenceStatus Status, IReadOnlyList<int> Targets);

/// <summary>References bind to sources (what built the geometry), never to result indices.</summary>
public abstract record Reference(string Id);

/// <summary>Every result piece of one source face. A feature consuming it works on all pieces.</summary>
public sealed record FaceReference(string Id, string Source) : Reference(Id);

/// <summary>One specific piece of a source face, remembered by the sources of its neighbours.</summary>
public sealed record FacePieceReference(string Id, string Source, IReadOnlyList<string> Signature) : Reference(Id);

/// <summary>Every result edge between faces of two sources.</summary>
public sealed record EdgeReference(string Id, string SourceA, string SourceB) : Reference(Id);

/// <summary>Every result vertex where faces of all given sources meet.</summary>
public sealed record VertexReference(string Id, IReadOnlyList<string> Sources) : Reference(Id);

public static class ReferenceResolver
{
    public static Resolution Resolve(Reference reference, Topology topology) => reference switch
    {
        FaceReference f => ResolveFace(f, topology),
        FacePieceReference p => ResolvePiece(p, topology),
        EdgeReference e => Count(EdgesBetween(topology, e.SourceA, e.SourceB)),
        VertexReference v => Count(topology.Vertices
            .Where(x => v.Sources.All(s => x.Faces.Any(f => topology.Faces[f].Sources.Contains(s))))
            .Select(x => x.Index).ToList()),
        _ => throw new NotSupportedException(reference.GetType().Name),
    };

    /// <summary>Remembers the piece of <paramref name="source"/> nearest to a picked point.</summary>
    public static FacePieceReference CapturePiece(string id, string source, ModelResult result, double x, double y, double z)
    {
        var face = result.FaceNear(source, x, y, z) ?? throw new InvalidOperationException($"No face of '{source}' to pick.");
        return new FacePieceReference(id, source, result.Topology!.Signature(face));
    }

    static Resolution ResolveFace(FaceReference f, Topology t)
    {
        var pieces = PiecesOf(t, f.Source);
        return pieces.Count switch
        {
            0 => new(ReferenceStatus.Gone, pieces),
            1 when t.Faces[pieces[0]].Sources.Count > 1 => new(ReferenceStatus.Merged, pieces),
            1 => new(ReferenceStatus.Resolved, pieces),
            _ => new(ReferenceStatus.Split, pieces),
        };
    }

    static Resolution ResolvePiece(FacePieceReference p, Topology t)
    {
        var pieces = PiecesOf(t, p.Source);
        if (pieces.Count == 0) return new(ReferenceStatus.Gone, pieces);
        var exact = pieces.Where(i => t.Signature(i).SequenceEqual(p.Signature, StringComparer.Ordinal)).ToList();
        if (exact.Count == 1) return new(ReferenceStatus.Resolved, exact);
        // A single piece is unambiguous even if its neighbours changed; several candidates never get guessed.
        if (exact.Count == 0 && pieces.Count == 1) return new(ReferenceStatus.Resolved, pieces);
        return new(ReferenceStatus.Ambiguous, exact.Count > 0 ? exact : pieces);
    }

    static List<int> PiecesOf(Topology t, string source) =>
        t.Faces.Where(f => f.Sources.Contains(source)).Select(f => f.Index).ToList();

    static List<int> EdgesBetween(Topology t, string a, string b) =>
        t.Edges.Where(e =>
                (t.Faces[e.FaceA].Sources.Contains(a) && t.Faces[e.FaceB].Sources.Contains(b)) ||
                (t.Faces[e.FaceA].Sources.Contains(b) && t.Faces[e.FaceB].Sources.Contains(a)))
            .Select(e => e.Index).ToList();

    static Resolution Count(List<int> targets) => targets.Count switch
    {
        0 => new(ReferenceStatus.Gone, targets),
        1 => new(ReferenceStatus.Resolved, targets),
        _ => new(ReferenceStatus.Split, targets),
    };
}
