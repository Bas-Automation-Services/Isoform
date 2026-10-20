using System.Collections.Immutable;
using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Geometry;
using Isoform.Meshing;

namespace Isoform.Modeling;

/// <summary>
/// One placed part of an assembly. The part is regenerated once in its own coordinates; the instance only moves
/// it. Its faces are named by occurrence: <c>left/plate/z+</c>, with the ids of the enclosing sub-assemblies first.
/// </summary>
public sealed record Instance(string Id, string Part, string Hash, Frame Placement, FrepResult Result)
{
    /// <summary>The part's solid where the assembly puts it; null if the part has no geometry.</summary>
    public Field? Field => Result.Part.Field is { } f ? new PlacedField(f, Placement) : null;

    public string Name(string source) => $"{Id}/{source}";
}

public sealed class AssemblyResult(IReadOnlyList<Instance> instances, IReadOnlyList<Diagnostic> diagnostics, int freeDegrees = 0)
{
    public IReadOnlyList<Instance> Instances { get; } = instances;
    public IReadOnlyList<Diagnostic> Diagnostics { get; } = diagnostics;

    /// <summary>Degrees of freedom the mates leave the ungrounded components (0: fully held).</summary>
    public int FreeDegrees { get; } = freeDegrees;

    /// <summary>
    /// Clearance between every pair of instances whose boxes come within <paramref name="within"/> mm of each other
    /// (certified interval, see <see cref="Queries.Clearance(Field, Box3D, Field, Box3D, double, int)"/>); pairs
    /// that interfere have a negative upper end.
    /// </summary>
    public IReadOnlyList<(string A, string B, ClearanceResult Clearance)> Clearances(double within = 0)
    {
        var placed = Instances.Where(i => i.Field is not null).Select(i => (i.Id, Field: i.Field!, Box: WorldBox(i))).ToList();
        var result = new List<(string, string, ClearanceResult)>();
        for (var a = 0; a < placed.Count; a++)
        for (var b = a + 1; b < placed.Count; b++)
        {
            var (x, y) = (placed[a].Box, placed[b].Box);
            var gap = Math.Max(0, Math.Max(Math.Max(x.Min.X - y.Max.X, y.Min.X - x.Max.X), Math.Max(Math.Max(x.Min.Y - y.Max.Y, y.Min.Y - x.Max.Y), Math.Max(x.Min.Z - y.Max.Z, y.Min.Z - x.Max.Z))));
            if (gap > within) continue;
            result.Add((placed[a].Id, placed[b].Id, Queries.Clearance(placed[a].Field, x, placed[b].Field, y)));
        }
        return result;
    }

    /// <summary>The part's box moved into place, grown a little so the search has room round the solid.</summary>
    static Box3D WorldBox(Instance i)
    {
        var (min, max) = (i.Result.Part.Min, i.Result.Part.Max);
        var (lo, hi) = (new Point3D(double.MaxValue, double.MaxValue, double.MaxValue), new Point3D(double.MinValue, double.MinValue, double.MinValue));
        foreach (var x in new[] { min.X - 1, max.X + 1 })
        foreach (var y in new[] { min.Y - 1, max.Y + 1 })
        foreach (var z in new[] { min.Z - 1, max.Z + 1 })
        {
            var q = i.Placement.ToWorld(new Point3D(x, y, z));
            (lo, hi) = (new Point3D(Math.Min(lo.X, q.X), Math.Min(lo.Y, q.Y), Math.Min(lo.Z, q.Z)), new Point3D(Math.Max(hi.X, q.X), Math.Max(hi.Y, q.Y), Math.Max(hi.Z, q.Z)));
        }
        return new Box3D(lo, hi);
    }

    /// <summary>Every instance's mesh moved into place and laid end to end, with its first quad index.</summary>
    public (Mesh Mesh, IReadOnlyList<int> FirstQuad) WorldMesh()
    {
        var (vertices, quads, first) = (new List<Point3D>(), new List<Quad>(), new List<int>());
        foreach (var i in Instances)
        {
            first.Add(quads.Count);
            if (i.Result.Mesh is not { } mesh) continue;
            var v0 = vertices.Count;
            vertices.AddRange(mesh.Vertices.Select(i.Placement.ToWorld));
            quads.AddRange(mesh.Quads.Select(q => new Quad(q.A + v0, q.B + v0, q.C + v0, q.D + v0, q.Owner)));
        }
        return (new Mesh(vertices, quads), first);
    }
}

/// <summary>
/// Regenerates an assembly: resolves each component's document by its path, regenerates every distinct part once
/// (by content hash, so a hundred identical bolts cost one), and places it. Sub-assemblies nest their placements.
/// </summary>
public sealed class AssemblyModeler(ModelingSettings settings)
{
    /// <param name="load">The text of the document at a path, or null when there is none.</param>
    public AssemblyResult Regenerate(AssemblyDocument assembly, Func<string, string?> load)
    {
        var diagnostics = new List<Diagnostic>();
        var parts = new Dictionary<string, FrepResult>();
        var modeler = new FrepModeler(settings);
        // Instances keep their pose inside their top-level component; mates move whole top-level components.
        var found = new List<(int Top, string Id, string Part, string Hash, Frame Inner, FrepResult Result)>();

        void Add(Component c, int top, Frame inner, string prefix, ImmutableStack<string> open)
        {
            var id = prefix + c.Id;
            if (found.Any(i => i.Id == id))
            {
                diagnostics.Add(new Diagnostic(Diagnostic.InvalidParameter, id, $"component id '{c.Id}' is used twice"));
                return;
            }
            if (load(c.Part) is not { } text)
            {
                diagnostics.Add(new Diagnostic(Diagnostic.PartMissing, id, $"there is no document '{c.Part}'"));
                return;
            }
            if (CanonicalFormat.FormatOf(text) == AssemblyDocument.FormatVersion)
            {
                if (open.Contains(c.Part))
                {
                    diagnostics.Add(new Diagnostic(Diagnostic.InvalidParameter, id, $"'{c.Part}' contains itself"));
                    return;
                }
                var sub = CanonicalFormat.ReadAssembly(text);
                Pinned(c, CanonicalFormat.Hash(sub), id);
                foreach (var inside in sub.Components) Add(inside, top, FrameOf(inside.Placement).Then(inner), id + "/", open.Push(c.Part));
                return;
            }
            var doc = CanonicalFormat.Read(text);
            var hash = CanonicalFormat.Hash(doc);
            Pinned(c, hash, id);
            if (!parts.TryGetValue(hash, out var result))
            {
                result = parts[hash] = modeler.Regenerate(doc);
                diagnostics.AddRange(result.Diagnostics.Select(d => d with { FeatureId = $"{c.Part}:{d.FeatureId}" }));
            }
            found.Add((top, id, c.Part, hash, inner, result));
        }

        void Pinned(Component c, string hash, string id)
        {
            if (c.Hash is { } pinned && pinned != hash)
                diagnostics.Add(new Diagnostic(Diagnostic.PartChanged, id, $"'{c.Part}' changed since the assembly pinned it"));
        }

        for (var k = 0; k < assembly.Components.Count; k++)
            Add(assembly.Components[k], k, Frame.Identity, "", ImmutableStack<string>.Empty);

        var poses = assembly.Components.Select(c => FrameOf(c.Placement)).ToList();
        var freeDegrees = 0;
        if (assembly.Mates is { Count: > 0 } mates)
        {
            var terms = new List<MateTerm>();
            foreach (var m in mates)
            {
                if (Term(m, found, diagnostics) is { } term) terms.Add(term);
            }
            var grounded = assembly.Components.Select(c => c.Grounded).ToArray();
            if (!grounded.Any(g => g)) grounded[0] = true;
            var solution = MateSolver.Solve(poses, grounded, terms);
            poses = solution.Poses.ToList();
            freeDegrees = solution.FreeDegrees;
            if (solution.Residual > 1e-9)
                diagnostics.Add(new Diagnostic(Diagnostic.MatesConflict, null, $"the mates cannot all hold (left {solution.Residual:G3} apart)"));
        }

        var instances = found.Select(f => new Instance(f.Id, f.Part, f.Hash, f.Inner.Then(poses[f.Top]), f.Result)).ToList();
        return new AssemblyResult(instances, diagnostics, freeDegrees);
    }

    /// <summary>
    /// A mate's two faces in their top-level components' coordinates: a flat face by a point on it and its outward
    /// normal (taken from the solid itself, so a face of a cut points the right way), a cylinder or cone by its axis.
    /// </summary>
    static MateTerm? Term(Mate m, List<(int Top, string Id, string Part, string Hash, Frame Inner, FrepResult Result)> found, List<Diagnostic> diagnostics)
    {
        (int Top, Point3D Point, Vector3D Direction)? Face(string name)
        {
            var owner = found.Where(f => name.StartsWith(f.Id + "/")).MaxBy(f => f.Id.Length);
            if (owner.Result is null)
            {
                diagnostics.Add(new Diagnostic(Diagnostic.InvalidParameter, m.Id, $"no component has the face '{name}'"));
                return null;
            }
            var source = name[(owner.Id.Length + 1)..];
            var surface = owner.Result.Part.Surfaces.LastOrDefault(s => s.Source == source);
            var mesh = owner.Result.Mesh;
            var quad = mesh?.Quads.Select((q, i) => (q, i)).FirstOrDefault(x => surface is not null && x.q.Owner == surface.Id);
            if (surface is null || mesh is null || quad is not { } on || on.q.Owner != surface.Id)
            {
                diagnostics.Add(new Diagnostic(Diagnostic.InvalidParameter, m.Id, $"'{name}' is not a face of {owner.Id}"));
                return null;
            }
            var flat = m.Kind != MateKind.Axis;
            if (flat ? surface.Kind != SurfaceKind.Plane : surface.Kind is not (SurfaceKind.Cylinder or SurfaceKind.Cone))
            {
                diagnostics.Add(new Diagnostic(Diagnostic.InvalidParameter, m.Id,
                    $"a {m.Kind.ToString().ToLowerInvariant()} mate needs {(flat ? "flat faces" : "cylinders or cones")}; '{name}' is a {surface.Kind.ToString().ToLowerInvariant()}"));
                return null;
            }
            if (!flat) return (owner.Top, owner.Inner.ToWorld(surface.Origin), owner.Inner.ToWorld(surface.Axis));
            var point = mesh.Vertices[on.q.A];
            // The face's own surface field: at an edge vertex the solid's gradient would be the neighbour's.
            var normal = owner.Result.Part.Field!.SampleSurface(surface.Id, point)!.Value.Gradient.Normalized();
            return (owner.Top, owner.Inner.ToWorld(point), owner.Inner.ToWorld(normal));
        }

        if (Face(m.A) is not { } a || Face(m.B) is not { } b) return null;
        return new MateTerm(m.Kind, a.Top, a.Point, a.Direction, b.Top, b.Point, b.Direction, m.Offset);
    }

    public static Frame FrameOf(Placement p) =>
        Frame.FromXAndNormal(new Point3D(p.Origin[0], p.Origin[1], p.Origin[2]), new Vector3D(p.X[0], p.X[1], p.X[2]), new Vector3D(p.Normal[0], p.Normal[1], p.Normal[2]));
}
