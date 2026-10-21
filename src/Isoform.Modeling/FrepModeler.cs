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

    /// <summary>A box meshed with a finer cell, and the feature whose size asked for it (null for a spot of the mesh).</summary>
    sealed record Zone(Box3D Box, double Cell, string? Feature);

    /// <summary>Something below the resolution with a place: where it is, how small (NaN: not known) and whose.</summary>
    readonly record struct Finding(Box3D Where, double Size, string? Feature);

    // Refinement passes after the first: each meshes again with the zones found so far.
    const int Passes = 3;

    public FrepResult Regenerate(PartDocument document)
    {
        var (_, part, plans) = Prepare(document);
        if (part.Field is null) return new FrepResult(part.Diagnostics, null, part, null, [], []);
        document = Parametric.Resolve(document);

        var zones = new List<Zone>();
        var findings = new List<Finding>();
        var result = Pass(document, part, plans, zones, findings);
        for (var pass = 0; pass < Passes; pass++)
        {
            var added = Zones(findings, zones);
            if (added.Count == 0) break;
            zones.AddRange(added);
            var refine = zones.Select(z => (z.Box, z.Cell)).ToList();
            plans = [.. part.Bodies.Select(b => DualContouring.Plan(b.Field, b.Min, b.Max, Settings.CellSize, Settings.ChordTolerance, Settings.SnapGrid, refine))];
            findings.Clear();
            result = Pass(document, part, plans, zones, findings);
        }
        return result;
    }

    /// <summary>
    /// The zones the findings ask for that the ones there do not give: the cell that resolves the size (two cells
    /// across it), or the finest allowed where the size is not known, over the finding grown by two cells.
    /// </summary>
    List<Zone> Zones(List<Finding> findings, List<Zone> zones)
    {
        var added = new List<Zone>();
        var pad = new Vector3D(1, 1, 1) * (2 * Settings.CellSize);
        foreach (var f in findings)
        {
            var levels = double.IsNaN(f.Size) ? Settings.Refinement : (int)Math.Ceiling(Math.Log2(2 * Settings.CellSize / f.Size) - 1e-9);
            if (levels <= 0 || levels > Settings.Refinement) continue;
            var cell = Settings.CellSize / (1 << levels);
            if (zones.Concat(added).Any(z => z.Cell <= cell && Contains(z.Box, f.Where))) continue;
            added.Add(new Zone(new Box3D(f.Where.Min - pad, f.Where.Max + pad), cell, f.Feature));
        }
        return added;
    }

    static bool Contains(Box3D outer, Box3D inner) =>
        outer.Min.X <= inner.Min.X && outer.Min.Y <= inner.Min.Y && outer.Min.Z <= inner.Min.Z
        && outer.Max.X >= inner.Max.X && outer.Max.Y >= inner.Max.Y && outer.Max.Z >= inner.Max.Z;

    static Box3D Around(Point3D p) => new(p, p);

    static Box3D Around(IEnumerable<Point3D> points)
    {
        var (lo, hi) = (new Point3D(double.MaxValue, double.MaxValue, double.MaxValue), new Point3D(double.MinValue, double.MinValue, double.MinValue));
        foreach (var p in points)
            (lo, hi) = (new(Math.Min(lo.X, p.X), Math.Min(lo.Y, p.Y), Math.Min(lo.Z, p.Z)), new(Math.Max(hi.X, p.X), Math.Max(hi.Y, p.Y), Math.Max(hi.Z, p.Z)));
        return new Box3D(lo, hi);
    }

    /// <summary>The resolved size at a point: two of the finest cells there.</summary>
    double ResolvedAt(IReadOnlyList<Zone> zones, Point3D p) =>
        2 * zones.Where(z => Contains(z.Box, Around(p))).Select(z => z.Cell).Append(Settings.CellSize).Min();

    /// <summary>The resolved size for a feature's own size: two of the cells of the zones it asked for.</summary>
    double ResolvedFor(IReadOnlyList<Zone> zones, string feature) =>
        2 * zones.Where(z => z.Feature == feature).Select(z => z.Cell).Append(Settings.CellSize).Min();

    FrepResult Pass(PartDocument document, CompiledPart part, MeshPlan[] plans, IReadOnlyList<Zone> zones, List<Finding> findings)
    {
        var diagnostics = part.Diagnostics.ToList();
        // Each body is a solid of its own, meshed and given topology alone, so overlapping bodies stay apart. The
        // results are laid end to end: one mesh and one topology, with every face knowing its body.
        var (vertices, quads, spots) = (new List<Point3D>(), new List<Quad>(), new List<ThinSpot>());
        var (faces, edges, corners) = (new List<TopoFace>(), new List<TopoEdge>(), new List<TopoVertex>());
        var faceOfQuad = new List<int>();
        var bodies = new List<BodyResult>();
        var pinched = new List<(Point3D At, int[] Owners)>();
        var falseEdges = new List<FalseEdges.Found>();
        var falseCorners = new List<FalseEdges.Corner>();
        var carried = new List<Carried>();
        var pieces = 0;
        foreach (var (body, plan) in part.Bodies.Zip(plans))
        {
            var bodyMesh = plan.Build(Cache);
            var oriented = TopologyBuilder.Oriented(part.Surfaces, body.Field);
            var built = TopologyBuilder.Build(bodyMesh, oriented, Settings.Tolerance, body.Field);
            carried.Add(new Carried(bodyMesh.Quads.Select(q => q.Owner).ToHashSet(), oriented, bodyMesh, [.. built.EdgePoints, .. built.VertexPoints]));
            pinched.AddRange(built.Pinches);
            falseEdges.AddRange(FalseEdges.Find(body.Field, oriented, built, Settings.Tolerance, Settings.CellSize));
            falseCorners.AddRange(FalseEdges.Corners(body.Field, built, Settings.Tolerance));
            var t = built.Topology;
            if (t.BodyCount > 1)
                diagnostics.Add(new Diagnostic(Diagnostic.MultipleBodies, part.Bodies.Count == 1 ? null : body.Name,
                    part.Bodies.Count == 1 ? $"the part falls apart into {t.BodyCount} bodies" : $"body {body.Name} falls apart into {t.BodyCount} pieces"));
            // Pieces that touch within the tolerance: one solid at the modeling tolerance, kept apart by the cells.
            if (PieceContacts.Find(bodyMesh, built.FaceOfQuad, t, Settings.Tolerance) is { } contact)
            {
                var (a, b) = (string.Join("+", t.Faces[contact.FaceA].Sources), string.Join("+", t.Faces[contact.FaceB].Sources));
                var later = part.Surfaces[Math.Max(built.FaceOwners[contact.FaceA].Max(), built.FaceOwners[contact.FaceB].Max())].Source.Split('/')[0];
                var at = contact.At;
                diagnostics.Add(new Diagnostic(Diagnostic.TangentContact, later,
                    Invariant($"{a} and {b} touch within the tolerance near ({at.X:F2}, {at.Y:F2}, {at.Z:F2}), but the pieces they bound are kept apart: a contact of zero thickness")));
            }
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

        diagnostics.AddRange(Resolution(document, part, mesh, diagnostics, zones, findings));
        // Surface pieces the cells merged into one vertex: something there is thinner than a cell. The later feature's.
        foreach (var g in pinched.GroupBy(x => string.Join(",", x.Owners)))
        {
            var (at, owners) = g.First();
            findings.AddRange(g.Select(x => new Finding(Around(x.At), double.NaN, null)));
            diagnostics.Add(new Diagnostic(Diagnostic.BelowResolution, part.Surfaces[owners[^1]].Source.Split('/')[0],
                Invariant($"{string.Join(" and ", owners.Select(o => part.Surfaces[o].Source).Distinct())} pinch the mesh near ({at.X:F2}, {at.Y:F2}, {at.Z:F2}): pieces of them there are below the resolved size of {ResolvedAt(zones, at):G4} mm")));
        }

        // Faces the cells joined across a piece of a third one narrower than a cell. The later feature's.
        var reported = diagnostics.Select(d => d.FeatureId).ToHashSet();
        foreach (var g in falseEdges.GroupBy(x => part.Surfaces[Math.Max(x.A, x.B)].Source.Split('/')[0]).Where(g => !reported.Contains(g.Key)))
        {
            var (a, b, at, _, _) = g.First();
            findings.AddRange(g.Select(x => new Finding(x.Extent, x.Gap, null)));
            diagnostics.Add(new Diagnostic(Diagnostic.BelowResolution, g.Key,
                Invariant($"{part.Surfaces[a].Source} and {part.Surfaces[b].Source} do not meet near ({at.X:F2}, {at.Y:F2}, {at.Z:F2}), but the cells join them: a face between them is below the resolved size of {ResolvedAt(zones, at):G4} mm")));
        }

        // Faces the cells joined in one vertex across an edge or face between them shorter than a cell. The later feature's.
        reported = diagnostics.Select(d => d.FeatureId).ToHashSet();
        foreach (var g in falseCorners.GroupBy(x => part.Surfaces[x.Owners.Max()].Source.Split('/')[0]).Where(g => !reported.Contains(g.Key)))
        {
            var (owners, at, _) = g.First();
            findings.AddRange(g.Select(x => new Finding(x.Extent, double.NaN, null)));
            diagnostics.Add(new Diagnostic(Diagnostic.BelowResolution, g.Key,
                Invariant($"{string.Join(", ", owners.Select(o => part.Surfaces[o].Source))} meet in a vertex near ({at.X:F2}, {at.Y:F2}, {at.Z:F2}) that the part does not have: an edge or face between them is below the resolved size of {ResolvedAt(zones, at):G4} mm")));
        }

        // A feature that already has a diagnostic (below resolution, rejected, unsupported) says enough.
        var unresolved = diagnostics.Select(d => d.FeatureId).ToHashSet();
        var lost = Lost(part, carried, unresolved);
        var smallest = 2 * Settings.CellSize;
        static string FeatureOf(Surface s) => s.Source.Split('/')[0];
        // A face the mesh lost is the later feature's doing: the surface's own, or the one that hems it in.
        foreach (var g in lost.Where(x => x.Value.Verdict == LostFaces.Verdict.Face).GroupBy(x => FeatureOf(part.Surfaces[Math.Max(x.Key, x.Value.By)])))
        {
            var (id, r) = g.MaxBy(x => x.Value.Margin);
            findings.AddRange(g.Select(x => new Finding(Around(x.Value.At), 2 * x.Value.Margin, null)));
            diagnostics.Add(new Diagnostic(Diagnostic.BelowResolution, g.Key,
                Invariant($"{part.Surfaces[id].Source} bounds the part over a patch {2 * r.Margin:G3} mm across near ({r.At.X:F2}, {r.At.Y:F2}, {r.At.Z:F2}) that the cells lose, below the resolved size of {ResolvedAt(zones, r.At):G4} mm")));
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

        return new FrepResult(diagnostics, topology, part, mesh, faceOfQuad, bodies) { Refined = [.. zones.Select(z => (z.Box, z.Cell))] };
    }

    /// <summary>What a body's mesh carries: the owners of its polygons, its surfaces as it sees them, the mesh, and the points of its edges and vertices.</summary>
    sealed record Carried(HashSet<int> Owners, IReadOnlyList<Surface> Oriented, Mesh Mesh, IReadOnlyList<Point3D[]> Edges);

    // How far a carried face's polygons may end from the face, in cells: a vertex may stray four cells to reach a
    // wedge's tip (DualContouring.Reach), and a cell more. Lost pieces are looked for in blocks of PieceSearch cells
    // along every edge and round every vertex, grown by PieceGrow: a piece the cells lost lies at an edge or vertex
    // they made instead.
    const double PieceReach = 5, PieceSearch = 8, PieceGrow = 2;
    const int PieceBudget = 32;

    /// <summary>
    /// The search of <see cref="LostFaces"/> for every surface of a body that no polygon carries, nor any coincident
    /// surface, by surface id; features that already have a diagnostic are left out. A surface the mesh does carry can
    /// still have lost a piece smaller than a cell where other faces meet: it is searched round every vertex, away from
    /// its polygons, and a piece found is a face like any other.
    /// </summary>
    Dictionary<int, LostFaces.Result> Lost(CompiledPart part, List<Carried> carried, HashSet<string?> settled)
    {
        var result = new Dictionary<int, LostFaces.Result>();
        var pad = new Vector3D(1, 1, 1) * (10 * Settings.Tolerance);
        foreach (var (body, (owners, oriented, mesh, edges)) in part.Bodies.Zip(carried))
        {
            var box = new Box3D(body.Min - pad, body.Max + pad);
            foreach (var s in part.Surfaces)
            {
                if (owners.Contains(s.Id) || !s.Analytic || settled.Contains(s.Source.Split('/')[0])) continue;
                if (LostFaces.Find(body.Field, s.Id) is not { } path || owners.Any(o => oriented[o].SameGeometry(oriented[s.Id], Settings.Tolerance))) continue;
                var r = LostFaces.Search(path, s, box, Settings.Tolerance);
                // In several bodies: a face anywhere is a face, a doubt anywhere a doubt.
                if (!result.TryGetValue(s.Id, out var known) || r.Verdict == LostFaces.Verdict.Face || known.Verdict == LostFaces.Verdict.None) result[s.Id] = r;
            }

            var reach = PieceReach * Settings.CellSize;
            var (block, grow) = (PieceSearch * Settings.CellSize, new Vector3D(1, 1, 1) * (PieceGrow * Settings.CellSize));
            // The blocks of the grid holding a point of an edge or a vertex.
            var blocks = edges.SelectMany(e => e).Select(p => ((long)Math.Floor(p.X / block), (long)Math.Floor(p.Y / block), (long)Math.Floor(p.Z / block)))
                .Distinct().Order().Select(k => new Box3D(new Point3D(k.Item1 * block, k.Item2 * block, k.Item3 * block) - grow,
                    new Point3D((k.Item1 + 1) * block, (k.Item2 + 1) * block, (k.Item3 + 1) * block) + grow));
            var bvhs = new Dictionary<int, TriangleBvh>();
            ILookup<int, Triangle>? byOwner = null;
            TriangleBvh Polygons(int id)
            {
                if (bvhs.TryGetValue(id, out var bvh)) return bvh;
                byOwner ??= mesh.Triangles().ToLookup(t => t.Owner);
                var same = owners.Where(o => oriented[o].SameGeometry(oriented[id], Settings.Tolerance)).Append(id).Distinct();
                return bvhs[id] = new TriangleBvh(mesh.Vertices, same.SelectMany(o => byOwner[o]).ToList());
            }
            foreach (var near in blocks)
            {
                // The body as the block sees it: a surface simplified away cannot bound the part there.
                var local = body.Field.Simplify(near);
                var candidates = new List<(int Id, LostFaces.Path Path)>();
                foreach (var id in owners)
                {
                    var s = part.Surfaces[id];
                    if (!s.Analytic || settled.Contains(s.Source.Split('/')[0])) continue;
                    if (new LeafField(s).Bounds(near) is var b && (b.Lo > 0 || b.Hi < 0)) continue;
                    if (LostFaces.Find(local, id) is { } p) candidates.Add((id, p));
                }
                // A lost piece lies between at least two other faces, which the cells joined instead.
                if (candidates.Count < 3) continue;
                foreach (var (id, p) in candidates)
                {
                    if (result.GetValueOrDefault(id).Verdict == LostFaces.Verdict.Face) continue;
                    // Built only once a search needs it: most end on the margin alone.
                    TriangleBvh? polygons = null;
                    double Distance(Point3D q) => (polygons ??= Polygons(id)).Distance(q);
                    var r = LostFaces.Search(p, part.Surfaces[id], near, Settings.Tolerance, budget: PieceBudget, carried: (Distance, reach));
                    if (r.Verdict == LostFaces.Verdict.Face) result[id] = r;
                }
            }
        }
        return result;
    }

    /// <summary>
    /// A lattice's walls (sheets or struts), the gaps between them and its skin, each against the resolved size, with
    /// the cell size that resolves it: the lattice has one face, so the mesher's thin spots, which pair two faces, never
    /// see its own walls and gaps.
    /// </summary>
    static IEnumerable<Diagnostic> LatticeResolution(Lattice l, double smallest)
    {
        string Resolve(double size) => Invariant($"use a CellSize of {size / 2:G3} mm or less");
        var walls = l.Sheets ? "sheets" : "struts";
        if (l.Thickness < smallest)
            yield return new Diagnostic(Diagnostic.BelowResolution, l.Id, Invariant($"{l.Id} ({l.Thickness:G4} mm) is below the resolved size of {smallest:G4} mm; {Resolve(l.Thickness)}"));
        var gap = LatticeField.Gap(DocumentRules.LatticeKindOf(l.Kind)!.Value, l.Cell, l.Thickness);
        if (gap < smallest)
            yield return new Diagnostic(Diagnostic.BelowResolution, l.Id, Invariant($"the {walls} of {l.Id} are {gap:G3} mm apart, below the resolved size of {smallest:G4} mm; {Resolve(gap)}"));
        if (l.Skin > 0 && l.Skin < smallest)
            yield return new Diagnostic(Diagnostic.BelowResolution, l.Id, Invariant($"{l.Id} skin ({l.Skin:G4} mm) is below the resolved size of {smallest:G4} mm; {Resolve(l.Skin)}"));
    }

    /// <summary>
    /// Features the cells cannot resolve: holes, shells and fillets below the guaranteed size, and every pair of
    /// surfaces the mesher found with a thinner wall or gap between them. A thin spot at a tangent contact is
    /// already reported as such.
    /// </summary>
    IEnumerable<Diagnostic> Resolution(PartDocument document, CompiledPart part, Mesh mesh, IReadOnlyList<Diagnostic> earlier, IReadOnlyList<Zone> zones, List<Finding> findings)
    {
        string FeatureOf(int owner) => part.Surfaces[owner].Source.Split('/')[0];
        // Where each feature's faces are in the mesh: a feature too small there is refined over them.
        var extent = mesh.Quads.GroupBy(q => FeatureOf(q.Owner))
            .ToDictionary(g => g.Key, g => Around(g.SelectMany(q => new[] { mesh.Vertices[q.A], mesh.Vertices[q.B], mesh.Vertices[q.C], mesh.Vertices[q.D] })));
        foreach (var feature in document.Features)
        {
            var smallest = ResolvedFor(zones, feature.Id);
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
            // Only a feature with faces in the mesh: one without is searched for as a lost face, or has no effect. A
            // fillet's face has no closed form to search for, so a fillet without faces and without another diagnostic
            // is reported by its size, with nowhere to refine.
            if (!extent.TryGetValue(feature.Id, out var where))
            {
                if (feature is Fillet && size < smallest && !earlier.Any(d => d.FeatureId == feature.Id))
                    yield return new Diagnostic(Diagnostic.BelowResolution, feature.Id, Invariant($"{feature.Id} ({size:G4} mm) is below the resolved size of {smallest:G4} mm; use a smaller CellSize"));
                continue;
            }
            if (size < smallest)
            {
                findings.Add(new Finding(where, size, feature.Id));
                yield return new Diagnostic(Diagnostic.BelowResolution, feature.Id, Invariant($"{feature.Id} ({size:G4} mm) is below the resolved size of {smallest:G4} mm; use a smaller CellSize"));
            }
            if (feature is Lattice l)
                foreach (var d in LatticeResolution(l, 2 * Settings.CellSize)) yield return d;
            // A profile edge sweeps a face as wide as the edge is long.
            var profile = feature switch { Extrude e => e.Profile, Revolve r => r.Profile, _ => null };
            if (profile is not null && Profiles.CounterClockwise(profile).FirstOrDefault(x => Width(x.Edge) < smallest) is { Id: { } edge } narrow)
            {
                findings.Add(new Finding(where, Width(narrow.Edge), feature.Id));
                yield return new Diagnostic(Diagnostic.BelowResolution, feature.Id, Invariant($"{feature.Id}/{edge} ({Width(narrow.Edge):G4} mm) is below the resolved size of {smallest:G4} mm; use a smaller CellSize"));
            }
        }

        static double Width(ProfileEdge2D e) => e.IsArc
            ? Math.Min(2 * e.R, e.R * Math.Abs(e.Sweep))
            : Math.Sqrt((e.U1 - e.U0) * (e.U1 - e.U0) + (e.V1 - e.V0) * (e.V1 - e.V0));

        var tangent = earlier.Where(d => d.Code == Diagnostic.TangentContact).Select(d => d.FeatureId).ToHashSet();
        foreach (var pair in mesh.ThinSpots.GroupBy(s => (s.OwnerA, s.OwnerB)))
        {
            var (a, b) = (FeatureOf(pair.Key.OwnerA), FeatureOf(pair.Key.OwnerB));
            if (tangent.Contains(a) || tangent.Contains(b)) continue;
            var thinnest = pair.MinBy(s => s.Thickness);
            var p = thinnest.Location;
            findings.Add(new Finding(Around(pair.Select(s => s.Location)), thinnest.Thickness, null));
            yield return new Diagnostic(Diagnostic.BelowResolution, b,
                Invariant($"{part.Surfaces[pair.Key.OwnerA].Source} and {part.Surfaces[pair.Key.OwnerB].Source} are {thinnest.Thickness:G3} mm apart near ({p.X:F2}, {p.Y:F2}, {p.Z:F2}), below the resolved size of {ResolvedAt(zones, p):G4} mm"));
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

    /// <summary>Boxes meshed with a finer cell than <see cref="ModelingSettings.CellSize"/>, and that cell.</summary>
    public IReadOnlyList<(Box3D Box, double Cell)> Refined { get; init; } = [];

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
