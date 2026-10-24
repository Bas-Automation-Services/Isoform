using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Geometry;
using Isoform.Meshing;
using Isoform.Modeling;

namespace Isoform.Build;

/// <summary>
/// What a check says: <see cref="Pass"/> and <see cref="Fail"/> are proved (a certified bound, or a real point that
/// breaks the limit), <see cref="Unknown"/> when the interval straddles the limit, <see cref="Reported"/> for a
/// measurement with no limit, <see cref="NotChecked"/> when the inputs to check it were not given.
/// </summary>
public enum Verdict { Pass, Fail, Unknown, Reported, NotChecked }

/// <summary>How two instances meet: apart, in contact (within the build tolerance: a joint), or interfering beyond it.</summary>
public enum ContactKind { Apart, Contact, Interference }

/// <summary>
/// One fitted plate edge of a part: the design face it follows, how many tangent arcs make it and their largest
/// distance from the design's section (the plate method's <c>FittedEdge</c>, with the part it belongs to).
/// </summary>
public sealed record EdgeFit(string Part, string Edge, int Arcs, double Error);

/// <summary>A certified maximum: in [<paramref name="Lower"/>, <paramref name="Upper"/>], attained (Lower) at <paramref name="At"/>.</summary>
public sealed record Deviation(double Lower, double Upper, bool Settled, Point3D At)
{
    public static Deviation Of(ExcessResult e) => new(e.Lower, e.Upper, e.Settled, e.At);
}

/// <summary>What the report was made from: hashes and settings, no date, so the same inputs give the same report.</summary>
public sealed record VerifySettings(double CellSize, double Tolerance, double SearchTolerance, double InterfaceTolerance, Material? Material);

public sealed record CheckVerdict(string Check, Verdict Verdict);

/// <summary>
/// Volumes from the meshes (estimates) and certified ranges for the design and the parts; the parts outside the design
/// (A∖D) and the design they leave uncovered (D∖A), from the meshes clipped against the other solid. Masses only with
/// a material.
/// </summary>
public sealed record Volumes(double Design, Interval DesignRange, double Parts, Interval PartsRange, double PartsOutside, double DesignUncovered,
    double? DesignMass, double? PartsMass, Point3D DesignCentroid, Point3D PartsCentroid);

/// <summary>A distinct part: how many instances, its volume (mesh estimate and certified range) and mass each.</summary>
public sealed record PartMass(string Part, int Count, double Volume, Interval Range, double? Mass);

/// <summary>How far one instance strays outside the design (certified), the design and part faces at the witness, and the mean over its mesh vertices.</summary>
public sealed record PartOutside(string Instance, string Part, Deviation Excess, string DesignFace, string PartFace, double Mean, Verdict Verdict);

/// <summary>
/// How far a design face (merged over its pattern copies) is from the parts where they leave it uncovered: the parts'
/// field, certified, which is at most the true distance; <paramref name="Distance"/> is the certified distance at the
/// witness when the face is uncovered beyond the tolerance. The part face nearest the witness, and the mean over the
/// face's mesh vertices.
/// </summary>
public sealed record FaceCover(string Face, int Copies, Deviation Excess, string PartFace, Interval? Distance, double Mean);

/// <summary>Two instances whose boxes meet: their certified clearance (negative: interference) and what it means.</summary>
public sealed record Contact(string A, string B, double Lower, double Upper, Point3D OnA, Point3D OnB, ContactKind Kind, Verdict Verdict);

/// <summary>
/// A keep-out body against the assembly: the certified clearance to the assembly and the instance nearest it, against
/// the clearance asked for. An instance whose box is farther than the nearest clearance found is not searched.
/// </summary>
public sealed record KeepoutClearance(string Body, double Required, string Instance, double Lower, double Upper, Verdict Verdict);

/// <summary>
/// An interface face of the design: every mesh vertex of it lies on a part face of the same surface (to the interface
/// tolerance) that reaches it within the build tolerance (<paramref name="Unmatched"/> counts those that do not); the
/// parts' boundary at its vertices (<paramref name="Worst"/>, |field|), and how far the face is left uncovered, certified.
/// </summary>
public sealed record InterfaceMatch(string Face, Deviation? Uncovered, int Vertices, double Worst, int Unmatched, IReadOnlyList<string> PartFaces, Verdict Verdict);

/// <summary>The thinnest wall of a distinct part (sampled, in the part's own frame), and the faces at its ends.</summary>
public sealed record PartWall(string Part, double? Thickness, Point3D From, Point3D To, string? FromFace, string? ToFace);

/// <summary>A diagnostic and where it came from: <c>design</c>, <c>parts</c> (the assembly and every part), <c>build</c>, <c>verify</c>.</summary>
public sealed record SourcedDiagnostic(string From, Diagnostic Diagnostic);

/// <summary>Everything the verifier found, a verdict per check and overall; <see cref="Report"/> writes it out.</summary>
public sealed record Verification(string DesignHash, string BuildHash, string BundleHash, VerifySettings Settings, Verdict Verdict,
    IReadOnlyList<CheckVerdict> Checks, Volumes Volumes, IReadOnlyList<PartMass> Parts, IReadOnlyList<PartOutside> Outside,
    IReadOnlyList<FaceCover> Uncovered, IReadOnlyList<Contact> Contacts, IReadOnlyList<KeepoutClearance> Keepouts,
    IReadOnlyList<InterfaceMatch> Interfaces, IReadOnlyList<PartWall> Walls, IReadOnlyList<EdgeFit>? Fits,
    IReadOnlyList<SourcedDiagnostic> Diagnostics);

/// <summary>
/// Compares an assembly of parts (any bundle: built or made by hand) with the design it rationalises. The instances,
/// placed as fields, are unioned into A and measured against the design D (its bodies, keep-outs left out), with the
/// build document's tolerance, material, keep-outs and interfaces. Every number names the faces it comes from.
/// </summary>
/// <param name="budget">Boxes each excess search may split before it stops with an honest interval.</param>
public sealed class Verifier(ModelingSettings settings, int budget = 100_000)
{
    /// <summary>The excess searches close their intervals to this, in mm.</summary>
    public const double SearchTolerance = 1e-3;

    /// <summary>How close a part face must lie to an interface's surface to be the same surface, in mm.</summary>
    public const double InterfaceTolerance = 1e-6;

    // Contacts and keep-outs: area contacts use the budget up, and are read as contacts through the upper end.
    const double ClearanceTolerance = 1e-4;
    const int ClearanceBudget = 20_000;

    public static class Checks
    {
        public const string Outside = "outside";
        public const string Uncovered = "uncovered";
        public const string Volumes = "volumes";
        public const string Contacts = "contacts";
        public const string Keepouts = "keepouts";
        public const string Interfaces = "interfaces";
        public const string Walls = "walls";
        public const string Fits = "fits";
        public const string Diagnostics = "diagnostics";
    }

    /// <summary>A build's bundle against its design, with the build's own diagnostics.</summary>
    public Verification Verify(BuildResult built, BuildDocument build, PartDocument design, FrepResult regenerated,
        IReadOnlyList<EdgeFit>? fits = null, Action<string, TimeSpan>? trace = null) =>
        Verify(build, design, regenerated, built.Bundle, built.Diagnostics, fits, trace);

    /// <param name="regenerated">The design regenerated with these settings.</param>
    /// <param name="buildDiagnostics">The build's diagnostics, when the bundle came from one.</param>
    /// <param name="fits">The fitted plate edges (null: not given, the check is not made).</param>
    /// <param name="trace">Called after each stage with its name and time.</param>
    public Verification Verify(BuildDocument build, PartDocument design, FrepResult regenerated, Bundle bundle,
        IReadOnlyList<Diagnostic>? buildDiagnostics = null, IReadOnlyList<EdgeFit>? fits = null, Action<string, TimeSpan>? trace = null)
    {
        var clock = Stopwatch.StartNew();
        void Stage(string name)
        {
            trace?.Invoke(name, clock.Elapsed);
            clock.Restart();
        }
        var tolerance = build.Tolerance;
        var keepouts = build.Keepouts ?? [];
        var kept = keepouts.Select(k => k.Body).ToHashSet();
        var model = new Design(design, regenerated, kept);
        var part = regenerated.Part;
        var bodies = part.Bodies.Where(b => !kept.Contains(b.Name)).ToList();
        var designBox = Grow(bodies.Count == 0 ? new Box3D(part.Min, part.Max) : Hull(bodies.Select(b => new Box3D(b.Min, b.Max))), 1);
        var designFaces = regenerated.Bodies.Where(b => !kept.Contains(b.Name)).SelectMany(b => b.Faces).ToHashSet();
        var mesh = regenerated.Mesh!;
        var designMesh = new Mesh(mesh.Vertices, [.. mesh.Quads.Where((_, q) => designFaces.Contains(regenerated.FaceOfQuad[q]))]);
        var vertices = VerticesByOwner(designMesh);

        var assembly = new AssemblyModeler(settings).Regenerate(bundle.Assembly, p => bundle.Documents.GetValueOrDefault(p));
        var instances = assembly.Instances.Where(i => i.Field is not null).ToList();
        var boxes = instances.Select(AssemblyResult.WorldBox).ToList();
        var parts = instances.Count == 1 ? instances[0].Field! : new MinField([.. instances.Select(i => i.Field!)]);
        Stage("assembly");

        // Volumes and mass.
        var density = build.Material?.Density;
        var designMass = MassProperties.Of(model.Field, designMesh, density ?? 1);
        var designRange = MassProperties.VolumeBounds(model.Field, designBox.Min, designBox.Max, settings.CellSize);
        var distinct = instances.GroupBy(i => i.Hash).Select(g => (Instance: g.First(), Count: g.Count())).ToList();
        var mass = distinct.ToDictionary(d => d.Instance.Hash, d => MassProperties.Of(d.Instance.Result.Part.Field!, d.Instance.Result.Mesh!, density ?? 1));
        var range = distinct.ToDictionary(d => d.Instance.Hash, d => MassProperties.VolumeBounds(d.Instance.Result.Part.Field!, d.Instance.Result.Part.Min, d.Instance.Result.Part.Max, settings.CellSize));
        var partMasses = distinct.Select(d => new PartMass(d.Instance.Part, d.Count, mass[d.Instance.Hash].Volume, range[d.Instance.Hash],
            density is null ? null : mass[d.Instance.Hash].Mass)).ToList();
        var partsVolume = instances.Sum(i => mass[i.Hash].Volume);
        var moment = instances.Aggregate(new Vector3D(0, 0, 0), (s, i) => s + i.Placement.ToWorld(mass[i.Hash].Centroid).AsVector() * mass[i.Hash].Volume);
        var partsRange = instances.Aggregate(new Interval(0, 0), (s, i) => new Interval(s.Lo + range[i.Hash].Lo, s.Hi + range[i.Hash].Hi));
        var (partsOutside, designUncovered) = Differences(designMesh, model.Field, instances, parts, designMass.Centroid, tolerance);
        var volumes = new Volumes(designMass.Volume, designRange, partsVolume, partsRange, partsOutside, designUncovered,
            density is null ? null : designMass.Mass, density is null ? null : partsVolume * density.Value, designMass.Centroid,
            new Point3D(moment.X / partsVolume, moment.Y / partsVolume, moment.Z / partsVolume));
        Stage("volumes");

        // Parts outside the design, per instance.
        var outside = new PartOutside[instances.Count];
        Parallel.For(0, instances.Count, k =>
        {
            var i = instances[k];
            var e = Queries.Excess(model.Field, i.Field!, boxes[k], SearchTolerance, budget);
            var mean = i.Result.Mesh!.Vertices.Average(v => model.Field.Value(i.Placement.ToWorld(v)));
            outside[k] = new PartOutside(i.Id, i.Part, Deviation.Of(e), part.Surfaces[e.FieldOwner].Source, i.Name(i.Result.Part.Surfaces[e.RegionOwner].Source),
                mean, Limit(e.Lower, e.Upper, tolerance));
        });
        Stage("outside");

        // Design not covered, per design face merged over its pattern copies.
        var byFace = Queries.ExcessByFace(parts, model.Field, designBox, part.Surfaces, SearchTolerance, budget);
        var uncovered = byFace.GroupBy(f => Merged(f.Face)).Select(g =>
        {
            var (lower, upper) = (g.Max(f => f.Excess.Lower), g.Max(f => f.Excess.Upper));
            var at = g.MaxBy(f => f.Excess.Lower).Excess.At;
            var points = part.Surfaces.Where(s => Merged(s.Source) == g.Key).SelectMany(s => vertices.GetValueOrDefault(s.Id) ?? []).ToList();
            var mean = points.Count == 0 ? double.NaN : points.Average(v => parts.Value(designMesh.Vertices[v]));
            Interval? distance = lower > tolerance && Queries.Distance(parts, at) is var d ? new Interval(d.Lower, d.Upper) : null;
            return new FaceCover(g.Key, g.Count(), new Deviation(lower, upper, upper - lower <= SearchTolerance, at), Nearest(instances, at), distance, mean);
        }).OrderBy(f => f.Face, StringComparer.Ordinal).ToList();
        Stage("uncovered");

        // Contacts and interference between instances: a joint is an area contact, interference only beyond the tolerance.
        var contacts = assembly.Clearances(0, ClearanceTolerance, ClearanceBudget).Select(c => new Contact(c.A, c.B, c.Clearance.Lower, c.Clearance.Upper,
            c.Clearance.OnA, c.Clearance.OnB,
            c.Clearance.Upper < -tolerance ? ContactKind.Interference : c.Clearance.Upper <= tolerance ? ContactKind.Contact : ContactKind.Apart,
            c.Clearance.Upper < -tolerance ? Verdict.Fail : c.Clearance.Lower >= -tolerance ? Verdict.Pass : Verdict.Unknown)).ToList();
        Stage("contacts");

        // Keep-outs: each body against every instance near enough to matter.
        var own = new List<SourcedDiagnostic>();
        var keepoutResults = new List<KeepoutClearance>();
        foreach (var k in keepouts)
        {
            if (part.Bodies.FirstOrDefault(b => b.Name == k.Body) is not { } body)
            {
                own.Add(new("verify", new Diagnostic(BuildDiagnostic.FaceMissing, null, $"the design has no body '{k.Body}' to keep out")));
                continue;
            }
            var box = Grow(new Box3D(body.Min, body.Max), 1);
            // Nearest box first; an instance whose box is farther than the clearance found so far cannot be nearer, and
            // its box gap bounds it.
            var gaps = boxes.Select(b => Gap(box, b)).ToList();
            var found = gaps.Select(g => (Lower: g, Upper: double.PositiveInfinity)).ToArray();
            foreach (var n in Enumerable.Range(0, instances.Count).OrderBy(n => gaps[n]))
            {
                if (gaps[n] >= found.Min(f => f.Upper)) break;
                var c = Queries.Clearance(body.Field, box, instances[n].Field!, boxes[n], ClearanceTolerance, ClearanceBudget);
                found[n] = (c.Lower, c.Upper);
            }
            var nearest = Enumerable.Range(0, instances.Count).MinBy(n => (found[n].Upper, found[n].Lower));
            var (low, up) = (found.Min(f => f.Lower), found.Min(f => f.Upper));
            keepoutResults.Add(new KeepoutClearance(k.Body, k.Clearance, instances[nearest].Id, low, up,
                low >= k.Clearance - tolerance ? Verdict.Pass : up < k.Clearance - tolerance ? Verdict.Fail : Verdict.Unknown));
        }
        Stage("keepouts");

        // Interfaces: the same surface, exactly, over the whole face (to the tolerance at its rim, where an edge may be fitted).
        var interfaces = (build.Interfaces ?? []).Select(name =>
        {
            var owners = part.Surfaces.Where(s => Named(s.Source, name) && vertices.ContainsKey(s.Id)).ToList();
            if (owners.Count == 0) return new InterfaceMatch(name, null, 0, double.NaN, 0, [], Verdict.Fail);
            var e = Deviation.Of(Queries.BoundaryExcess(parts, model.Field, designBox, owners.Select(s => s.Id).ToHashSet(), SearchTolerance, budget));
            var (count, worst, unmatched, names) = (0, 0.0, 0, new SortedSet<string>(StringComparer.Ordinal));
            foreach (var s in owners)
            {
                var same = instances.Select(i => (Instance: i, Faces: i.Result.Part.Surfaces.Where(t => Same(s, t, i.Placement)).ToList())).Where(x => x.Faces.Count > 0).ToList();
                foreach (var v in vertices[s.Id])
                {
                    var p = designMesh.Vertices[v];
                    count++;
                    worst = Math.Max(worst, Math.Abs(parts.Value(p)));
                    var on = same.Where(x => Math.Abs(x.Instance.Field!.Value(p)) <= tolerance).ToList();
                    if (on.Count == 0) unmatched++;
                    foreach (var x in on) names.UnionWith(x.Faces.Select(t => x.Instance.Name(t.Source)));
                }
            }
            var verdict = unmatched > 0 || worst > tolerance || e.Lower > tolerance ? Verdict.Fail : e.Upper <= tolerance ? Verdict.Pass : Verdict.Unknown;
            return new InterfaceMatch(name, e, count, worst, unmatched, [.. names], verdict);
        }).ToList();
        Stage("interfaces");

        // Minimum wall per distinct part.
        var walls = distinct.Select(d =>
        {
            var surfaces = d.Instance.Result.Part.Surfaces;
            return Queries.MinimumWall(d.Instance.Result) is { } w
                ? new PartWall(d.Instance.Part, w.Thickness, w.From, w.To, surfaces[w.FromOwner].Source, surfaces[w.ToOwner].Source)
                : new PartWall(d.Instance.Part, null, default, default, null, null);
        }).ToList();
        Stage("walls");

        var diagnostics = regenerated.Diagnostics.Select(d => new SourcedDiagnostic("design", d))
            .Concat(assembly.Diagnostics.Select(d => new SourcedDiagnostic("parts", d)))
            .Concat((buildDiagnostics ?? []).Select(d => new SourcedDiagnostic("build", d)))
            .Concat(own).ToList();

        List<CheckVerdict> checks =
        [
            new(Checks.Outside, Worst(outside.Select(o => o.Verdict))),
            new(Checks.Uncovered, Verdict.Reported),
            new(Checks.Volumes, Verdict.Reported),
            new(Checks.Contacts, Worst(contacts.Select(c => c.Verdict))),
            new(Checks.Keepouts, Worst(keepoutResults.Select(k => k.Verdict))),
            new(Checks.Interfaces, Worst(interfaces.Select(i => i.Verdict))),
            new(Checks.Walls, Verdict.Reported),
            new(Checks.Fits, fits is null ? Verdict.NotChecked : fits.All(f => f.Error <= tolerance) ? Verdict.Pass : Verdict.Fail),
            new(Checks.Diagnostics, diagnostics.Count == 0 ? Verdict.Pass : Verdict.Unknown),
        ];
        return new Verification(CanonicalFormat.Hash(design), CanonicalFormat.Hash(build),
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalFormat.Write(bundle)))),
            new VerifySettings(settings.CellSize, tolerance, SearchTolerance, InterfaceTolerance, build.Material),
            Worst(checks.Select(c => c.Verdict)), checks, volumes, partMasses, outside, uncovered, contacts, keepoutResults, interfaces, walls, fits, diagnostics);
    }

    /// <summary>Fail if any fails, else unknown if any is, else pass; measurements and checks not made do not count.</summary>
    static Verdict Worst(IEnumerable<Verdict> verdicts)
    {
        var all = verdicts.ToList();
        return all.Contains(Verdict.Fail) ? Verdict.Fail : all.Contains(Verdict.Unknown) ? Verdict.Unknown : Verdict.Pass;
    }

    /// <summary>A certified maximum against a limit: pass when its upper end is within it, fail when its lower end is beyond.</summary>
    static Verdict Limit(double lower, double upper, double limit) => upper <= limit ? Verdict.Pass : lower > limit ? Verdict.Fail : Verdict.Unknown;

    /// <summary>A face name without its pattern copies' prefixes: <c>legs/2:leg/a</c> is <c>leg/a</c>.</summary>
    public static string Merged(string source) => PatternCopy.Replace(source, "");

    static readonly Regex PatternCopy = new(@"^(?:[^/:]+/\d+:)+", RegexOptions.Compiled);

    /// <summary>A build document's face name binds the face itself and its pattern copies (as <see cref="Design.Faces"/>).</summary>
    static bool Named(string source, string name) => source == name || source.EndsWith(":" + name, StringComparison.Ordinal);

    /// <summary>The part face nearest a point: the instance whose field is lowest there, and its owner. Owner ids repeat across instances.</summary>
    static string Nearest(IReadOnlyList<Instance> instances, Point3D p)
    {
        var i = instances.MinBy(x => x.Field!.Value(p))!;
        return i.Name(i.Result.Part.Surfaces[i.Field!.Sample(p).Owner].Source);
    }

    /// <summary>A part's surface placed in the world is the design's own surface (a plane facing either way).</summary>
    static bool Same(Surface design, Surface part, Frame placement)
    {
        var s = part with { Origin = placement.ToWorld(part.Origin), Axis = placement.ToWorld(part.Axis) };
        return s.SameGeometry(design, InterfaceTolerance) || s.Kind == SurfaceKind.Plane && (s with { Axis = -s.Axis }).SameGeometry(design, InterfaceTolerance);
    }

    /// <summary>The vertices of each owner's polygons.</summary>
    static Dictionary<int, SortedSet<int>> VerticesByOwner(Mesh mesh)
    {
        var result = new Dictionary<int, SortedSet<int>>();
        foreach (var q in mesh.Quads)
        {
            if (!result.TryGetValue(q.Owner, out var set)) result[q.Owner] = set = [];
            set.UnionWith(q.IsTriangle ? [q.A, q.B, q.C] : [q.A, q.B, q.C, q.D]);
        }
        return result;
    }

    /// <summary>
    /// A∖D and D∖A from the meshes there are, by the divergence theorem: the polyhedral volume of A less that of A∩D,
    /// and of D less that of A∩D, all three from the same triangles, so the meshes' chord errors cancel. A∩D is bounded
    /// by the parts' triangles inside the design or on its boundary (a face flush with the design's counts once, as the
    /// part's) and the design's triangles inside the parts. A triangle counts by the other field's mean over its
    /// vertices, which lie on the exact surface (its centroid would sag off a curved face), so one crossing the other
    /// solid's boundary is off by its share: an estimate to about a cell times the length of the crossing. Faces within
    /// <paramref name="flush"/> of each other are flush.
    /// </summary>
    static (double PartsOutside, double DesignUncovered) Differences(Mesh design, Field designField, IReadOnlyList<Instance> instances, Field parts,
        Point3D origin, double flush)
    {
        static double Tetra(Point3D o, Point3D a, Point3D b, Point3D c) => (a - o).Dot((b - o).Cross(c - o)) / 6;
        (double All, double Inside) Sum(IReadOnlyList<Point3D> v, IEnumerable<Triangle> triangles, Field other, Func<double, bool> counts)
        {
            var value = new double[v.Count];
            Parallel.For(0, v.Count, k => value[k] = other.Value(v[k]));
            var (all, inside) = (0.0, 0.0);
            foreach (var t in triangles)
            {
                var x = Tetra(origin, v[t.A], v[t.B], v[t.C]);
                all += x;
                if (counts((value[t.A] + value[t.B] + value[t.C]) / 3)) inside += x;
            }
            return (all, inside);
        }
        var (a, aInD) = instances.Select(i => Sum([.. i.Result.Mesh!.Vertices.Select(i.Placement.ToWorld)], i.Result.Mesh.Triangles(), designField, f => f <= flush))
            .Aggregate((x, y) => (x.All + y.All, x.Inside + y.Inside));
        var (d, dInA) = Sum(design.Vertices, design.Triangles(), parts, f => f < -flush);
        return (a - aInD - dInA, d - aInD - dInA);
    }

    static Box3D Hull(IEnumerable<Box3D> boxes) => boxes.Aggregate((a, b) => new Box3D(Min(a.Min, b.Min), Max(a.Max, b.Max)));

    static Box3D Grow(Box3D b, double by) => new(b.Min - new Vector3D(by, by, by), b.Max + new Vector3D(by, by, by));

    static Point3D Min(Point3D a, Point3D b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));

    static Point3D Max(Point3D a, Point3D b) => new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));

    /// <summary>The gap between two boxes: no point of one is nearer the other.</summary>
    static double Gap(Box3D a, Box3D b) => Math.Max(0, Math.Max(Math.Max(Math.Max(a.Min.X - b.Max.X, b.Min.X - a.Max.X), Math.Max(a.Min.Y - b.Max.Y, b.Min.Y - a.Max.Y)),
        Math.Max(a.Min.Z - b.Max.Z, b.Min.Z - a.Max.Z)));
}
