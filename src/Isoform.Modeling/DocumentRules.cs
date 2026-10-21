using System.Globalization;
using Isoform.Document;
using Isoform.Geometry;
using static System.FormattableString;

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
                // Edit paths start with the id and split at '.'; "parameters." is the parameter table.
                _ when feature.Id == "parameters" || feature.Id.Contains('.') => $"edit paths could not reach a feature with the id '{feature.Id}': ids may not contain '.' or be 'parameters'",
                _ when !ids.Add(feature.Id) => $"the feature id '{feature.Id}' is used twice",
                CombineBodies c when c.Tool == (c.Body ?? current) => $"body '{c.Tool}' cannot be combined with itself",
                _ when StartsBody(feature) && bodies.Contains(feature.Body ?? feature.Id) => $"there already is a body named '{feature.Body ?? feature.Id}'",
                _ when !StartsBody(feature) && feature.Body is { } b && !bodies.Contains(b) => $"there is no body named '{b}'",
                CombineBodies c when !bodies.Contains(c.Tool) => $"there is no body named '{c.Tool}'",
                CombineBodies c when c.Op == BooleanOp.New => "combine joins or cuts a body, it does not start one",
                CombineBodies c when bodies.Count < 2 => "combine needs a second body to use as the tool",
                Block b when b.Size.Any(s => s <= tolerance) => $"block size must be positive, got [{string.Join(", ", b.Size.Select(s => s.ToString(CultureInfo.InvariantCulture)))}]",
                Hole h when h.Diameter <= tolerance => Invariant($"hole diameter must be positive, got {h.Diameter}"),
                Hole h when h.Depth <= tolerance => Invariant($"hole depth must be positive, got {h.Depth}"),
                Hole { On: not null, Sketch: not null } => "a hole goes on a face or at sketch points, not both",
                Hole { On: { } face } when !doc.Features.Take(i).Any(f => f.Id == face.Split('/')[0]) => $"a hole on a face needs that face made earlier; '{face.Split('/')[0]}' is not an earlier feature",
                Hole { Sketch: { } s } h when !AtSketchPoints(h, doc.Features.Take(i).FirstOrDefault(f => f.Id == s) as Sketch) =>
                    "a hole at sketch points names an earlier sketch and one or more of its points",
                PlaneCut c when Math.Sqrt(c.Normal.Sum(x => x * x)) <= tolerance => "plane-cut normal must not be zero",
                Shell sh when sh.Thickness <= tolerance => Invariant($"shell thickness must be positive, got {sh.Thickness}"),
                Fillet f when f.Radius <= tolerance => Invariant($"fillet radius must be positive, got {f.Radius}"),
                Fillet f when (f.Loop is null ? f.Edge.Length != 2 : f.Edge.Length != 0) => "a fillet names either an edge of two source faces or one loop face",
                Chamfer c when c.Distance <= tolerance => Invariant($"chamfer distance must be positive, got {c.Distance}"),
                Offset o when Math.Abs(o.Distance) <= tolerance => "an offset distance must not be zero",
                _ when BlendOf(feature) is var (op, blend) && (blend < 0 || blend > 0 && op == BooleanOp.New) =>
                    blend < 0 ? Invariant($"blend must not be negative, got {blend}") : "only a join or a cut can blend",
                Chamfer c when (c.Loop is null ? c.Edge.Length != 2 : c.Edge.Length != 0) => "a chamfer names either an edge of two source faces or one loop face",
                Extrude e when e.End - e.Start <= tolerance => Invariant($"extrude end must be beyond its start, got {e.Start} to {e.End}"),
                Extrude { Sketch: { } s } e => SweepProblem(s, e.Regions, doc.Features.Take(i).ToList()),
                Extrude e => Profiles.Problem(e.Plane!, e.Profile!, tolerance, revolve: false),
                Revolve r when !(r.Angle > tolerance && r.Angle <= 360) => Invariant($"revolve angle must be in (0, 360], got {r.Angle}"),
                Revolve { Sketch: { } s } r => SweepProblem(s, r.Regions, doc.Features.Take(i).ToList()),
                Revolve r => Profiles.Problem(r.Plane!, r.Profile!, tolerance, revolve: true),
                Sketch s => SketchProblem(s, doc.Features.Take(i).ToList(), tolerance),
                Pattern p => PatternProblem(p, doc.Features.Take(i).ToList(), tolerance),
                Lattice l => LatticeProblem(l, doc.Features.Take(i).ToList(), tolerance),
                _ when bodies.Count == 0 && feature is not Sketch && !StartsBody(feature) => "the first feature after any sketches must start a new body",
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

    static bool AtSketchPoints(Hole h, Sketch? sketch) => sketch is not null && h.Points is { Length: > 0 } points && points.All(sketch.Points.ContainsKey);

    /// <summary>An extrude or revolve of a sketch: an earlier sketch, and region names that are not empty.</summary>
    static string? SweepProblem(string sketch, IReadOnlyList<string[]>? regions, List<Feature> earlier) =>
        earlier.FirstOrDefault(f => f.Id == sketch) is not Sketch ? $"'{sketch}' is not an earlier sketch"
        : regions is { Count: 0 } || regions?.Any(r => r.Length == 0) == true ? "each region is named by one or more entities of its outer loop"
        : null;

    /// <summary>A sketch's structure (names, references, constraints) and where it lies.</summary>
    static string? SketchProblem(Sketch s, List<Feature> earlier, double tolerance)
    {
        if (Sketching.SketchSolver.Problem(s) is { } problem) return problem;
        if (s.On is { Face: { } face })
        {
            var owner = face.Split('/')[0];
            return earlier.Any(f => f.Id == owner) ? null : $"a sketch on a face needs that face made earlier; '{owner}' is not an earlier feature";
        }
        if (s.On.Plane is not { } plane) return "a sketch lies on a plane or a face";
        var (n, x) = (new BasAutomation.Geometry.Vector3D(plane.Normal[0], plane.Normal[1], plane.Normal[2]), new BasAutomation.Geometry.Vector3D(plane.X[0], plane.X[1], plane.X[2]));
        return n.Length <= tolerance || x.Cross(n).Length <= tolerance * Math.Max(1, x.Length)
            ? "the sketch plane needs a normal and an x direction that is not parallel to it" : null;
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
            LinearMotion l when l.Count is < 2 or > 1000 => Invariant($"a pattern count must be 2 to 1000, got {l.Count}"),
            LinearMotion l when Length(l.Direction) <= tolerance || Math.Abs(l.Spacing) <= tolerance => "a linear pattern needs a direction and a non-zero spacing",
            CircularMotion c when c.Count is < 2 or > 1000 => Invariant($"a pattern count must be 2 to 1000, got {c.Count}"),
            CircularMotion c when Length(c.Axis) <= tolerance => "a circular pattern needs an axis",
            CircularMotion c when !(c.Angle > tolerance && c.Angle <= 360) => Invariant($"a circular pattern angle must be in (0, 360], got {c.Angle}"),
            MirrorMotion m when Length(m.Normal) <= tolerance => "a mirror needs a plane normal",
            _ => null,
        };
    }

    /// <summary>The geometry's lattice kind for the format's name (both list them in the same order), or null.</summary>
    public static LatticeKind? LatticeKindOf(string kind) => Array.IndexOf(Lattice.Kinds, kind) is var i and >= 0 ? (LatticeKind)i : null;

    static string? LatticeProblem(Lattice l, List<Feature> earlier, double tolerance)
    {
        if (LatticeKindOf(l.Kind) is not { } kind) return $"lattice kind must be one of {string.Join(", ", Lattice.Kinds)}, got '{l.Kind}'";
        if (l.Cell <= tolerance) return Invariant($"lattice cell must be positive, got {l.Cell}");
        if (l.Thickness <= tolerance) return Invariant($"lattice thickness must be positive, got {l.Thickness}");
        if (l.Skin < 0) return Invariant($"lattice skin must not be negative, got {l.Skin}");
        if (LatticeField.Gap(kind, l.Cell, l.Thickness) <= 0)
            return Invariant($"a {l.Kind} lattice of cell {l.Cell} mm closes its voids at thickness {l.Thickness} mm; keep the thickness below {LatticeField.MaxThickness(kind, l.Cell):G4} mm");
        return l.Region switch
        {
            { Feature: { } id } when earlier.FirstOrDefault(f => f.Id == id) is not (Block or Extrude or Revolve) =>
                $"a lattice region is a box or an earlier block, extrude or revolve; '{id}' is not one",
            { Feature: null, Origin: var o, Size: var s } when o?.Length != 3 || s?.Length != 3 || s.Any(x => x <= tolerance) =>
                "a lattice region box needs an origin and a positive size",
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
