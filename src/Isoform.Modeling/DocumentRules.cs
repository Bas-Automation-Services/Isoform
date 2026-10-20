using Isoform.Document;

namespace Isoform.Modeling;

/// <summary>Backend-independent checks: a document that fails them produces no geometry on any backend.</summary>
public static class DocumentRules
{
    /// <summary>
    /// The first problem, or null. Parameters and expressions are resolved first (failing as an invalid-parameter
    /// diagnostic), so every rule sees the numbers; a caller that goes on builds <see cref="Parametric.Resolve"/>'s document.
    /// </summary>
    public static Diagnostic? FirstProblem(PartDocument doc, double tolerance)
    {
        try { doc = Parametric.Resolve(doc); }
        catch (ParameterException e) { return new Diagnostic(Diagnostic.InvalidParameter, e.FeatureId, e.Message); }
        var bodies = new List<string>();
        var ids = new HashSet<string>();
        string? current = null;
        for (var i = 0; i < doc.Features.Count; i++)
        {
            var feature = doc.Features[i];
            string? problem = feature switch
            {
                _ when feature.Id.Length == 0 || feature.Id.Contains('/') => $"feature ids may not be empty or contain '/', got '{feature.Id}'",
                _ when !ids.Add(feature.Id) => $"the feature id '{feature.Id}' is used twice",
                CombineBodies c when c.Tool == (c.Body ?? current) => $"body '{c.Tool}' cannot be combined with itself",
                _ when StartsBody(feature) && bodies.Contains(feature.Body ?? feature.Id) => $"there already is a body named '{feature.Body ?? feature.Id}'",
                _ when !StartsBody(feature) && feature.Body is { } b && !bodies.Contains(b) => $"there is no body named '{b}'",
                CombineBodies c when !bodies.Contains(c.Tool) => $"there is no body named '{c.Tool}'",
                CombineBodies c when c.Op == BooleanOp.New => "combine joins or cuts a body, it does not start one",
                CombineBodies c when bodies.Count < 2 => "combine needs a second body to use as the tool",
                Block b when b.Size.Any(s => s <= tolerance) => $"block size must be positive, got [{string.Join(", ", b.Size)}]",
                Hole h when h.Diameter <= tolerance => $"hole diameter must be positive, got {h.Diameter}",
                PlaneCut c when Math.Sqrt(c.Normal.Sum(x => x * x)) <= tolerance => "plane-cut normal must not be zero",
                Shell sh when sh.Thickness <= tolerance => $"shell thickness must be positive, got {sh.Thickness}",
                Fillet f when f.Radius <= tolerance => $"fillet radius must be positive, got {f.Radius}",
                Fillet f when (f.Loop is null ? f.Edge.Length != 2 : f.Edge.Length != 0) => "a fillet names either an edge of two source faces or one loop face",
                Chamfer c when c.Distance <= tolerance => $"chamfer distance must be positive, got {c.Distance}",
                Offset o when Math.Abs(o.Distance) <= tolerance => "an offset distance must not be zero",
                _ when BlendOf(feature) is var (op, blend) && (blend < 0 || blend > 0 && op == BooleanOp.New) =>
                    blend < 0 ? $"blend must not be negative, got {blend}" : "only a join or a cut can blend",
                Chamfer c when (c.Loop is null ? c.Edge.Length != 2 : c.Edge.Length != 0) => "a chamfer names either an edge of two source faces or one loop face",
                Extrude e when e.End - e.Start <= tolerance => $"extrude end must be beyond its start, got {e.Start} to {e.End}",
                Extrude e => Profiles.Problem(e.Plane, e.Profile, tolerance, revolve: false),
                Revolve r when !(r.Angle > tolerance && r.Angle <= 360) => $"revolve angle must be in (0, 360], got {r.Angle}",
                Revolve r => Profiles.Problem(r.Plane, r.Profile, tolerance, revolve: true),
                Pattern p => PatternProblem(p, doc.Features.Take(i).ToList(), tolerance),
                _ when i == 0 && !StartsBody(feature) => "the first feature must start a new body",
                _ => null,
            };
            if (problem is not null) return new Diagnostic(Diagnostic.InvalidParameter, feature.Id, problem);
            // The current body: the one last started or named. Edge features and patterns find theirs by themselves.
            if (StartsBody(feature)) bodies.Add(current = feature.Body ?? feature.Id);
            else if (feature.Body is { } named && feature is not (Fillet or Chamfer or Pattern)) current = named;
            if (feature is CombineBodies { Keep: false } used) bodies.Remove(used.Tool);
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

    static (BooleanOp Op, double Blend) BlendOf(Feature f) => f switch
    {
        Block b => (b.Op, b.Blend),
        Extrude e => (e.Op, e.Blend),
        Revolve r => (r.Op, r.Blend),
        _ => (BooleanOp.Join, 0),
    };

    public static bool StartsBody(Feature f) => f is Block { Op: BooleanOp.New } or Extrude { Op: BooleanOp.New } or Revolve { Op: BooleanOp.New };
}
