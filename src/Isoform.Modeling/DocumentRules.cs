using Isoform.Document;

namespace Isoform.Modeling;

/// <summary>Backend-independent checks: a document that fails them produces no geometry on any backend.</summary>
public static class DocumentRules
{
    public static Diagnostic? FirstProblem(PartDocument doc, double tolerance)
    {
        for (var i = 0; i < doc.Features.Count; i++)
        {
            var feature = doc.Features[i];
            string? problem = feature switch
            {
                Block { Op: BooleanOp.New } when i > 0 => "only the first feature may start a new body",
                Block b when b.Size.Any(s => s <= tolerance) => $"block size must be positive, got [{string.Join(", ", b.Size)}]",
                Hole h when h.Diameter <= tolerance => $"hole diameter must be positive, got {h.Diameter}",
                PlaneCut c when Math.Sqrt(c.Normal.Sum(x => x * x)) <= tolerance => "plane-cut normal must not be zero",
                Fillet f when f.Radius <= tolerance => $"fillet radius must be positive, got {f.Radius}",
                Fillet f when f.Edge.Length != 2 => "a fillet edge names exactly two source faces",
                _ when i == 0 && feature is not Block { Op: BooleanOp.New } => "the first feature must be a new block",
                _ => null,
            };
            if (problem is not null) return new Diagnostic(Diagnostic.InvalidParameter, feature.Id, problem);
        }
        return null;
    }
}
