using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Geometry;
using Isoform.Meshing;
using static System.FormattableString;

namespace Isoform.Modeling;

/// <summary>
/// Regenerates a document on the implicit (F-rep/CSG) core. Bodies are meshed as regions (<see cref="MeshPlan"/>);
/// with a <paramref name="cache"/> a regeneration reuses every region whose content did not change.
/// </summary>
/// <param name="cache">Region results kept across regenerations; the caller sweeps it after each one.</param>
public sealed class FrepModeler(ModelingSettings settings, RegionCache? cache = null) : IModeler
{
    public ModelingSettings Settings { get; } = settings;

    public RegionCache? Cache { get; } = cache;

    ModelResult IModeler.Regenerate(PartDocument document) => Regenerate(document);

    sealed record Prepared(string Hash, CompiledPart Part, MeshPlan[] Plans);

    // With a cache, the last document's compiled part and plans are kept: a pool's coordinator meshes its share of a
    // document and then regenerates the same one. A modeler with a cache is therefore not for concurrent use.
    Prepared? _last;

    Prepared Prepare(PartDocument document)
    {
        var hash = Cache is null ? "" : CanonicalFormat.Hash(document);
        if (_last is { } last && Cache is not null && last.Hash == hash) return last;
        var part = new Compiler(Settings).Compile(document);
        var plans = part.Field is null ? [] : part.Bodies.Select(b => DualContouring.Plan(b.Field, b.Min, b.Max, Settings.CellSize, Settings.ChordTolerance, Settings.SnapGrid)).ToArray();
        var prepared = new Prepared(hash, part, plans);
        if (Cache is not null) _last = prepared;
        return prepared;
    }

    /// <summary>
    /// Meshes this process's share of the regions into the cache, and nothing else: share <paramref name="index"/> of
    /// <paramref name="count"/> (<see cref="MeshPlan.Share"/>). Processes that each mesh a share and pass their
    /// results on (<see cref="RegionCache.TakeComputed"/>) let one of them regenerate from its cache alone.
    /// </summary>
    public void MeshShare(PartDocument document, int index, int count)
    {
        foreach (var plan in Prepare(document).Plans) plan.MeshRegions(Cache!, r => plan.Share(r, count) == index);
    }

    public FrepResult Regenerate(PartDocument document)
    {
        var (_, part, plans) = Prepare(document);
        if (part.Field is null) return new FrepResult(part.Diagnostics, null, part, null, [], []);
        document = Parametric.Resolve(document);

        var diagnostics = part.Diagnostics.ToList();
        // Each body is a solid of its own, meshed and given topology alone, so overlapping bodies stay apart. The
        // results are laid end to end: one mesh and one topology, with every face knowing its body.
        var (vertices, quads, spots) = (new List<Point3D>(), new List<Quad>(), new List<ThinSpot>());
        var (faces, edges, corners) = (new List<TopoFace>(), new List<TopoEdge>(), new List<TopoVertex>());
        var faceOfQuad = new List<int>();
        var bodies = new List<BodyResult>();
        var carried = new List<(HashSet<int> Owners, IReadOnlyList<Surface> Oriented)>();
        var pieces = 0;
        foreach (var (body, plan) in part.Bodies.Zip(plans))
        {
            var bodyMesh = plan.Build(Cache);
            var oriented = TopologyBuilder.Oriented(part.Surfaces, body.Field);
            carried.Add((bodyMesh.Quads.Select(q => q.Owner).ToHashSet(), oriented));
            var built = TopologyBuilder.Build(bodyMesh, oriented, Settings.Tolerance, body.Field);
            var t = built.Topology;
            if (t.BodyCount > 1)
                diagnostics.Add(new Diagnostic(Diagnostic.MultipleBodies, part.Bodies.Count == 1 ? null : body.Name,
                    part.Bodies.Count == 1 ? $"the part falls apart into {t.BodyCount} bodies" : $"body {body.Name} falls apart into {t.BodyCount} pieces"));
            var (v0, f0, e0, c0) = (vertices.Count, faces.Count, edges.Count, corners.Count);
            vertices.AddRange(bodyMesh.Vertices);
            quads.AddRange(bodyMesh.Quads.Select(q => new Quad(q.A + v0, q.B + v0, q.C + v0, q.D + v0, q.Owner)));
            spots.AddRange(bodyMesh.ThinSpots);
            faceOfQuad.AddRange(built.FaceOfQuad.Select(f => f + f0));
            faces.AddRange(t.Faces.Select(f => new TopoFace(f.Index + f0, f.Sources, f.Body + pieces)));
            edges.AddRange(t.Edges.Select(e => new TopoEdge(e.Index + e0, e.FaceA + f0, e.FaceB + f0)));
            corners.AddRange(t.Vertices.Select(v => new TopoVertex(v.Index + c0, v.Faces.Select(f => f + f0).ToList())));
            bodies.Add(new BodyResult(body.Name, Enumerable.Range(f0, t.Faces.Count).ToList()));
            pieces += t.BodyCount;
        }
        var mesh = new Mesh(vertices, quads, spots);
        var topology = new Topology(faces, edges, corners, pieces);

        diagnostics.AddRange(Resolution(document, part, mesh, diagnostics));

        // A feature that already has a diagnostic (below resolution, rejected, unsupported) says enough.
        var unresolved = diagnostics.Select(d => d.FeatureId).ToHashSet();
        var lost = Lost(part, carried, unresolved);
        var smallest = 2 * Settings.CellSize;
        static string FeatureOf(Surface s) => s.Source.Split('/')[0];
        // A face the mesh lost is the later feature's doing: the surface's own, or the one that hems it in.
        foreach (var g in lost.Where(x => x.Value.Verdict == LostFaces.Verdict.Face).GroupBy(x => FeatureOf(part.Surfaces[Math.Max(x.Key, x.Value.By)])))
        {
            var (id, r) = g.MaxBy(x => x.Value.Margin);
            diagnostics.Add(new Diagnostic(Diagnostic.BelowResolution, g.Key,
                Invariant($"{part.Surfaces[id].Source} bounds the part over a patch {2 * r.Margin:G3} mm across near ({r.At.X:F2}, {r.At.Y:F2}, {r.At.Z:F2}) that the cells lose, below the resolved size of {smallest:G4} mm")));
        }
        unresolved = diagnostics.Select(d => d.FeatureId).ToHashSet();

        var present = topology.Faces.SelectMany(f => f.Sources).ToHashSet();
        // An offset or a combine moves or merges faces that keep their names; it has none of its own to look for.
        foreach (var feature in document.Features.Where(f => f is not (Fillet or Offset or CombineBodies) && !unresolved.Contains(f.Id)))
        {
            var own = part.Surfaces.Where(s => s.Source.StartsWith(feature.Id + "/")).ToList();
            if (own.Any(s => present.Contains(s.Source))) continue;
            // Nothing of it in the mesh: no effect only where the search proved it changes the part by less than the tolerance.
            diagnostics.Add(own.All(s => lost.GetValueOrDefault(s.Id).Verdict == LostFaces.Verdict.None)
                ? new Diagnostic(Diagnostic.NoEffect, feature.Id, $"{feature.Id} does not touch the part")
                : new Diagnostic(Diagnostic.BelowResolution, feature.Id, Invariant($"{feature.Id} has no face the cells resolve, and its effect could not be shown to be below the tolerance; below the resolved size of {smallest:G4} mm")));
        }

        return new FrepResult(diagnostics, topology, part, mesh, faceOfQuad, bodies);
    }

    /// <summary>
    /// The search of <see cref="LostFaces"/> for every surface of a body that no polygon carries, nor any coincident
    /// surface, by surface id; features that already have a diagnostic are left out.
    /// </summary>
    Dictionary<int, LostFaces.Result> Lost(CompiledPart part, List<(HashSet<int> Owners, IReadOnlyList<Surface> Oriented)> carried, HashSet<string?> settled)
    {
        var result = new Dictionary<int, LostFaces.Result>();
        var pad = new Vector3D(1, 1, 1) * (10 * Settings.Tolerance);
        foreach (var (body, (owners, oriented)) in part.Bodies.Zip(carried))
        {
            var box = new Box3D(body.Min - pad, body.Max + pad);
            foreach (var s in part.Surfaces)
            {
                if (owners.Contains(s.Id) || s.Kind is SurfaceKind.Fillet or SurfaceKind.Blend || settled.Contains(s.Source.Split('/')[0])) continue;
                if (LostFaces.Find(body.Field, s.Id) is not { } path || owners.Any(o => oriented[o].SameGeometry(oriented[s.Id], Settings.Tolerance))) continue;
                var r = LostFaces.Search(path, s, box, Settings.Tolerance);
                // In several bodies: a face anywhere is a face, a doubt anywhere a doubt.
                if (!result.TryGetValue(s.Id, out var known) || r.Verdict == LostFaces.Verdict.Face || known.Verdict == LostFaces.Verdict.None) result[s.Id] = r;
            }
        }
        return result;
    }

    /// <summary>
    /// Features the cells cannot resolve: holes, shells and fillets below the guaranteed size, and every pair of
    /// surfaces the mesher found with a thinner wall or gap between them. A thin spot at a tangent contact is
    /// already reported as such.
    /// </summary>
    IEnumerable<Diagnostic> Resolution(PartDocument document, CompiledPart part, Mesh mesh, IReadOnlyList<Diagnostic> earlier)
    {
        var smallest = 2 * Settings.CellSize;
        foreach (var feature in document.Features)
        {
            var size = feature switch
            {
                Hole x => x.Diameter,
                Shell x => x.Thickness,
                Fillet x => 2 * x.Radius,
                Chamfer x => Math.Sqrt(2) * x.Distance,
                Block { Blend: > 0 } x => x.Blend,
                Extrude { Blend: > 0 } x => x.Blend,
                Revolve { Blend: > 0 } x => x.Blend,
                _ => double.PositiveInfinity,
            };
            if (size < smallest)
                yield return new Diagnostic(Diagnostic.BelowResolution, feature.Id, Invariant($"{feature.Id} ({size:G4} mm) is below the resolved size of {smallest:G4} mm; use a smaller CellSize"));
            // A profile edge sweeps a face as wide as the edge is long.
            var profile = feature switch { Extrude e => e.Profile, Revolve r => r.Profile, _ => null };
            if (profile is not null && Profiles.CounterClockwise(profile).FirstOrDefault(x => Width(x.Edge) < smallest) is { Id: { } edge } narrow)
                yield return new Diagnostic(Diagnostic.BelowResolution, feature.Id, Invariant($"{feature.Id}/{edge} ({Width(narrow.Edge):G4} mm) is below the resolved size of {smallest:G4} mm; use a smaller CellSize"));
        }

        static double Width(ProfileEdge2D e) => e.IsArc
            ? Math.Min(2 * e.R, e.R * Math.Abs(e.Sweep))
            : Math.Sqrt((e.U1 - e.U0) * (e.U1 - e.U0) + (e.V1 - e.V0) * (e.V1 - e.V0));

        var tangent = earlier.Where(d => d.Code == Diagnostic.TangentContact).Select(d => d.FeatureId).ToHashSet();
        string FeatureOf(int owner) => part.Surfaces[owner].Source.Split('/')[0];
        foreach (var pair in mesh.ThinSpots.GroupBy(s => (s.OwnerA, s.OwnerB)))
        {
            var (a, b) = (FeatureOf(pair.Key.OwnerA), FeatureOf(pair.Key.OwnerB));
            if (tangent.Contains(a) || tangent.Contains(b)) continue;
            var thinnest = pair.MinBy(s => s.Thickness);
            var p = thinnest.Location;
            yield return new Diagnostic(Diagnostic.BelowResolution, b,
                Invariant($"{part.Surfaces[pair.Key.OwnerA].Source} and {part.Surfaces[pair.Key.OwnerB].Source} are {thinnest.Thickness:G3} mm apart near ({p.X:F2}, {p.Y:F2}, {p.Z:F2}), below the resolved size of {smallest:G4} mm"));
        }
    }
}

/// <summary>A body of the result and the indices of its faces in the part's topology.</summary>
public sealed record BodyResult(string Name, IReadOnlyList<int> Faces);

public sealed class FrepResult(IReadOnlyList<Diagnostic> diagnostics, Topology? topology, CompiledPart part, Mesh? mesh, IReadOnlyList<int> faceOfQuad,
    IReadOnlyList<BodyResult> bodies)
    : ModelResult(diagnostics, topology)
{
    public IReadOnlyList<BodyResult> Bodies { get; } = bodies;

    public CompiledPart Part { get; } = part;
    public Mesh? Mesh { get; } = mesh;
    public IReadOnlyList<int> FaceOfQuad { get; } = faceOfQuad;

    public override int? FaceNear(string source, double x, double y, double z)
    {
        if (Mesh is null) return null;
        var p = new Point3D(x, y, z);
        int? best = null;
        var bestDistance = double.MaxValue;
        for (var q = 0; q < Mesh.Quads.Count; q++)
        {
            var face = FaceOfQuad[q];
            if (!Topology!.Faces[face].Sources.Contains(source)) continue;
            var d = (Mesh.Vertices[Mesh.Quads[q].A] - p).LengthSquared;
            if (d < bestDistance) (best, bestDistance) = (face, d);
        }
        return best;
    }
}
