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
                Chamfer c when c.Distance <= tolerance => $"chamfer distance must be positive, got {c.Distance}",
                Chamfer c when c.Edge.Length != 2 => "a chamfer edge names exactly two source faces",
                Extrude e when e.End - e.Start <= tolerance => $"extrude end must be beyond its start, got {e.Start} to {e.End}",
                Extrude e => Profiles.Problem(e.Plane, e.Profile, tolerance, revolve: false),
                Revolve r when !(r.Angle > tolerance && r.Angle <= 360) => $"revolve angle must be in (0, 360], got {r.Angle}",
                Revolve r => Profiles.Problem(r.Plane, r.Profile, tolerance, revolve: true),
                Pattern p => PatternProblem(p, doc.Features.Take(i).ToList(), tolerance),
                _ when i == 0 && !StartsBody(feature) => "the first feature must start a new body",
                _ => null,
            };
            if (problem is not null) return new Diagnostic(Diagnostic.InvalidParameter, feature.Id, problem);
        }
        return null;
    }

    static string? PatternProblem(Pattern p, List<Feature> earlier, double tolerance)
    {
        static double Length(double[] v) => Math.Sqrt(v.Sum(x => x * x));
        if (p.Features.Length == 0 || p.Features.Distinct().Count() != p.Features.Length) return "a pattern lists one or more distinct features";
        foreach (var id in p.Features)
        {
            var f = earlier.FirstOrDefault(x => x.Id == id);
            if (f is null) return $"a pattern can only copy earlier features; '{id}' is not one";
            if (f is not (Block or Hole or PlaneCut or Extrude or Revolve)) return $"'{id}' is a {f.GetType().Name.ToLowerInvariant()}, which cannot be patterned";
        }
        return p.Motion switch
        {
            LinearMotion l when l.Count is < 2 or > 1000 => $"a pattern count must be 2 to 1000, got {l.Count}",
            LinearMotion l when Length(l.Direction) <= tolerance || Math.Abs(l.Spacing) <= tolerance => "a linear pattern needs a direction and a non-zero spacing",
            CircularMotion c when c.Count is < 2 or > 1000 => $"a pattern count must be 2 to 1000, got {c.Count}",
            CircularMotion c when Length(c.Axis) <= tolerance => "a circular pattern needs an axis",
            CircularMotion c when !(c.Angle > tolerance && c.Angle <= 360) => $"a circular pattern angle must be in (0, 360], got {c.Angle}",
            MirrorMotion m when Length(m.Normal) <= tolerance => "a mirror needs a plane normal",
            _ => null,
        };
    }

    static bool StartsBody(Feature f) => f is Block { Op: BooleanOp.New } or Extrude { Op: BooleanOp.New } or Revolve { Op: BooleanOp.New };
}
