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
                _ when i > 0 && StartsBody(feature) => "only the first feature may start a new body",
                Block b when b.Size.Any(s => s <= tolerance) => $"block size must be positive, got [{string.Join(", ", b.Size)}]",
                Hole h when h.Diameter <= tolerance => $"hole diameter must be positive, got {h.Diameter}",
                PlaneCut c when Math.Sqrt(c.Normal.Sum(x => x * x)) <= tolerance => "plane-cut normal must not be zero",
                Shell sh when sh.Thickness <= tolerance => $"shell thickness must be positive, got {sh.Thickness}",
                Fillet f when f.Radius <= tolerance => $"fillet radius must be positive, got {f.Radius}",
                Fillet f when f.Edge.Length != 2 => "a fillet edge names exactly two source faces",
                Extrude e when e.End - e.Start <= tolerance => $"extrude end must be beyond its start, got {e.Start} to {e.End}",
                Extrude e => Profiles.Problem(e.Plane, e.Profile, tolerance, revolve: false),
                Revolve r when !(r.Angle > tolerance && r.Angle <= 360) => $"revolve angle must be in (0, 360], got {r.Angle}",
                Revolve r => Profiles.Problem(r.Plane, r.Profile, tolerance, revolve: true),
                _ when i == 0 && !StartsBody(feature) => "the first feature must start a new body",
                _ => null,
            };
            if (problem is not null) return new Diagnostic(Diagnostic.InvalidParameter, feature.Id, problem);
        }
        return null;
    }

    static bool StartsBody(Feature f) => f is Block { Op: BooleanOp.New } or Extrude { Op: BooleanOp.New } or Revolve { Op: BooleanOp.New };
}
