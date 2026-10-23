using System.Diagnostics;
using System.Text.Json;
using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Geometry;
using Isoform.Meshing;
using Isoform.Modeling;

namespace Isoform.Interactive;

/// <summary>
/// One front end's kernel: it regenerates documents, keeps the last good result per slot (<see cref="Main"/> for what
/// is committed, <see cref="Preview"/> for a drag in progress, or any other name) and the mesh buffers of the last
/// regeneration. Region results stay in a cache, so an edit re-meshes only the regions it changed; in a pool each
/// process meshes its <see cref="Share"/> and the coordinator <see cref="Import"/>s the others' before it regenerates.
/// Later features (handles, picking, queries, STEP) work on a slot's <see cref="Regeneration"/>.
/// </summary>
/// <remarks>
/// Not for concurrent use. The cache keeps what the last regeneration used, so two slots that alternate in one session
/// re-mesh each other's regions: a preview belongs in a session (a worker) of its own.
/// </remarks>
public sealed class Session
{
    public const string Main = "main";
    public const string Preview = "preview";

    readonly RegionCache _cache = new();
    readonly Dictionary<string, Regeneration> _slots = [];
    FrepModeler? _modeler;
    int _pooled; // regions the pool meshed for this regeneration outside Regenerate: own share and imports

    /// <summary>The buffers of the last regeneration; empty after one that failed.</summary>
    public MeshBuffers Buffers { get; private set; } = MeshBuffers.Empty;

    /// <summary>The last good regeneration in a slot, or null.</summary>
    public Regeneration? this[string slot] => _slots.GetValueOrDefault(slot);

    /// <summary>One modeler per settings, so the coordinator regenerates from the plans its share already made.</summary>
    FrepModeler Modeler(ModelingSettings settings) =>
        _modeler is { } m && m.Settings == settings ? m : _modeler = new FrepModeler(settings, _cache);

    /// <summary>
    /// Applies <paramref name="edits"/> (a JSON array of edits, or one; <c>[]</c> for none) to a part or bundle and
    /// regenerates it. A document or edit that does not apply gives a regeneration with an <see cref="Regeneration.Error"/>
    /// and leaves the slot as it was.
    /// </summary>
    public Regeneration Regenerate(string document, string edits, ModelingSettings settings, string slot = Main)
    {
        var clock = Stopwatch.StartNew();
        var edited = DocumentEdits.TryApply(document, edits);
        var editMs = clock.Elapsed.TotalMilliseconds;
        var (view, format, error) = ((View?)null, (string?)null, edited.Problem);
        if (edited.Text is { } text)
        {
            try
            {
                format = CanonicalFormat.FormatOf(text);
                if (format == Bundle.FormatVersion)
                {
                    var bundle = CanonicalFormat.ReadBundle(text);
                    view = View.Of(new AssemblyModeler(settings, _cache).Regenerate(bundle.Assembly, path => bundle.Documents.GetValueOrDefault(path)));
                }
                else view = View.Of(Modeler(settings).Regenerate(CanonicalFormat.Read(text)));
            }
            catch (Exception e) when (Unreadable(e)) { error = e.Message; }
        }
        var regenerateMs = clock.Elapsed.TotalMilliseconds - editMs;
        var (reused, meshed) = _cache.Sweep();
        (reused, meshed, _pooled) = (reused - _pooled, meshed + _pooled, 0);
        if (view is null)
        {
            Buffers = MeshBuffers.Empty;
            return new Regeneration(slot, null, null, null, error, edited.FailedEdit, null, MeshBuffers.Empty, new Timing(editMs, regenerateMs, 0), 0, 0, settings.Refinement);
        }
        // A drag preview has a time budget: it shows the octree mesh as it is.
        Buffers = slot == Preview ? MeshBuffers.Of(view.Mesh, view.FaceOfQuad) : MeshBuffers.Display(view, settings.ChordTolerance);
        var timing = new Timing(editMs, regenerateMs, clock.Elapsed.TotalMilliseconds - editMs - regenerateMs);
        return _slots[slot] = new Regeneration(slot, format, edited.Text, edited.Hash, null, null, view, Buffers, timing, reused, meshed, settings.Refinement) { Settings = settings };
    }

    /// <summary>
    /// Meshes share <paramref name="index"/> of <paramref name="count"/> of the regions of the edited document (of every
    /// part in a bundle) into the cache, and returns the results this process had to mesh, for the coordinator to
    /// import. A document that does not read gives nothing: the coordinator reports the error.
    /// </summary>
    public byte[] Share(string document, string edits, ModelingSettings settings, int index, int count)
    {
        var modeler = Modeler(settings);
        try
        {
            if (DocumentEdits.TryApply(document, edits).Text is { } text)
            {
                if (CanonicalFormat.FormatOf(text) != Bundle.FormatVersion) modeler.MeshShare(CanonicalFormat.Read(text), index, count);
                else
                    foreach (var part in CanonicalFormat.ReadBundle(text).Documents.Values)
                        if (CanonicalFormat.FormatOf(part) != AssemblyDocument.FormatVersion) modeler.MeshShare(CanonicalFormat.Read(part), index, count);
            }
        }
        catch (Exception e) when (Unreadable(e)) { }
        _pooled += _cache.TakeCounts().Misses;
        return _cache.TakeComputed();
    }

    /// <summary>Region results another process meshed (its <see cref="Share"/>).</summary>
    public void Import(byte[] regions) => _pooled += _cache.Import(regions);

    /// <summary>Ends a regeneration in a process that only meshes shares, as <see cref="Regenerate"/> does in the coordinator.</summary>
    public void Sweep()
    {
        _cache.Sweep();
        _pooled = 0;
    }

    internal static bool Unreadable(Exception e) =>
        e is JsonException or FormatException or InvalidOperationException or NullReferenceException or KeyNotFoundException or InvalidCastException;
}

/// <summary>
/// One regeneration: the canonical text and hash of the edited document and what it shows, or the error. Timing in ms.
/// </summary>
/// <param name="FailedEdit">When an edit could not be applied, its index in the list.</param>
/// <param name="Reused">Regions taken from the cache.</param>
/// <param name="Meshed">Regions meshed for it, here or (in a pool) elsewhere.</param>
/// <param name="Refinement">The settings' <see cref="ModelingSettings.Refinement"/>: how many times the cells may be halved locally.</param>
public sealed record Regeneration(string Slot, string? Format, string? Text, string? Hash, string? Error, int? FailedEdit, View? View,
    MeshBuffers Buffers, Timing Timing, int Reused, int Meshed, int Refinement = 0)
{
    /// <summary>The settings it was made with, for what works on its mesh (the exact B-rep); null when it failed.</summary>
    public ModelingSettings? Settings { get; init; }
}

/// <param name="Edits">Applying the edits and making the text canonical.</param>
/// <param name="Buffers">Simplifying the mesh for display (not for a preview) and flattening it into <see cref="MeshBuffers"/>.</param>
public sealed record Timing(double Edits, double Regenerate, double Buffers);

/// <summary>A thin spot: where two faces come closer than the cells resolve, by their source names.</summary>
public sealed record ThinSpotView(Point3D At, string A, string B, double Thickness);

/// <summary>An assembly instance: its part's content hash and where it sits (its faces are the matching body's).</summary>
public sealed record InstanceView(string Id, string Part, string Hash, Frame Placement);

/// <summary>
/// What a regeneration shows: one mesh in world coordinates, a face index per quad, every face's source names, and the
/// bodies (for a bundle: one per instance). The modeler's own result stays at hand for features that need the exact
/// geometry (<see cref="Part"/> for a part, <see cref="Assembly"/> for a bundle).
/// </summary>
public sealed record View(FrepResult? Part, AssemblyResult? Assembly, Mesh? Mesh, IReadOnlyList<int> FaceOfQuad, IReadOnlyList<IReadOnlyList<string>> Faces,
    IReadOnlyList<BodyResult> Bodies, IReadOnlyList<Diagnostic> Diagnostics, int Edges, int Vertices, IReadOnlyList<ThinSpotView> ThinSpots,
    IReadOnlyList<InstanceView> Instances)
{
    public static View Of(FrepResult r)
    {
        var t = r.Topology;
        var surfaces = r.Part.Surfaces;
        var spots = r.Mesh?.ThinSpots.Select(s => new ThinSpotView(s.Location, surfaces[s.OwnerA].Source, surfaces[s.OwnerB].Source, s.Thickness)).ToList() ?? [];
        return new View(r, null, r.Mesh, r.FaceOfQuad, t?.Faces.Select(f => f.Sources).ToList() ?? [], r.Bodies, r.Diagnostics,
            t?.Edges.Count ?? 0, t?.Vertices.Count ?? 0, spots, []);
    }

    /// <summary>Every instance in place, its faces named by occurrence (<c>left/plate/z+</c>), one body per instance.</summary>
    public static View Of(AssemblyResult r)
    {
        var (mesh, _) = r.WorldMesh();
        var (faceOfQuad, faces, bodies, spots) = (new List<int>(), new List<IReadOnlyList<string>>(), new List<BodyResult>(), new List<ThinSpotView>());
        var (edges, vertices) = (0, 0);
        foreach (var i in r.Instances)
        {
            var f0 = faces.Count;
            var t = i.Result.Topology;
            faceOfQuad.AddRange(i.Result.FaceOfQuad.Select(f => f + f0));
            faces.AddRange(t?.Faces.Select(f => (IReadOnlyList<string>)f.Sources.Select(i.Name).ToList()) ?? []);
            bodies.Add(new BodyResult(i.Id, Enumerable.Range(f0, faces.Count - f0).ToList()));
            (edges, vertices) = (edges + (t?.Edges.Count ?? 0), vertices + (t?.Vertices.Count ?? 0));
            var surfaces = i.Result.Part.Surfaces;
            spots.AddRange(i.Result.Mesh?.ThinSpots.Select(s => new ThinSpotView(i.Placement.ToWorld(s.Location), i.Name(surfaces[s.OwnerA].Source), i.Name(surfaces[s.OwnerB].Source), s.Thickness)) ?? []);
        }
        var instances = r.Instances.Select(i => new InstanceView(i.Id, i.Part, i.Hash, i.Placement)).ToList();
        return new View(null, r, mesh, faceOfQuad, faces, bodies, r.Diagnostics, edges, vertices, spots, instances);
    }
}
