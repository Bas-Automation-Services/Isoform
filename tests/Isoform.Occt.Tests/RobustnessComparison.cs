using System.Diagnostics;
using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Geometry;
using Isoform.Meshing;
using Isoform.Modeling;
using Isoform.Tests;

namespace Isoform.Occt.Tests;

public enum Outcome { Built, Diagnosed, NoGeometry, Invalid, Exception }

/// <summary>One backend's answer to one document.</summary>
public sealed record BackendRun(Outcome Outcome, IReadOnlyList<Diagnostic> Diagnostics, Topology? Topology, double? Volume, bool Watertight, double Ms, string? Error)
{
    public IEnumerable<string> FailCodes => Diagnostics.Select(d => d.Code).Where(Robustness.IsFailure).Distinct();
}

/// <summary>Both backends on one document, the certified volume when there is one, and what went wrong where.</summary>
public sealed record Comparison(PartDocument Doc, BackendRun Frep, BackendRun Occt, Interval? Truth, BackendRun? Referee, IReadOnlySet<string> Labels, double TruthMs);

/// <summary>
/// K7: runs a document on the F-rep core and on OCCT and classifies the outcome. The judge of volume is the certified
/// <see cref="MassProperties.VolumeBounds"/> of the F-rep field; without fillets that field is plain CSG of boxes,
/// cylinders and half-spaces and so exact by construction. The first judge of topology is the F-rep core again at a
/// quarter of the cell size: a default-cell answer that changes there was a resolution failure. Where it stays, see
/// <see cref="JudgeTopology"/>.
/// </summary>
public static class Robustness
{
    public static readonly ModelingSettings Settings = new();
    static readonly ModelingSettings Fine = new() { CellSize = Settings.CellSize / 4 };
    const double TruthCell = 0.25;

    /// <summary>Diagnostics that say a feature failed or was refused; no-effect and multiple-bodies only describe the result.</summary>
    public static bool IsFailure(string code) => code is not (Diagnostic.NoEffect or Diagnostic.MultipleBodies);

    // A native crash takes the test host down with it: ISOFORM_K7_TRACE names a file that always holds the document in hand.
    static readonly string? Trace = Environment.GetEnvironmentVariable("ISOFORM_K7_TRACE");

    public static Comparison Compare(PartDocument doc)
    {
        if (Trace is not null) File.WriteAllText(Trace, CanonicalFormat.Write(doc));
        var frep = RunFrep(doc, Settings, out var part);
        var occt = RunOcct(doc);

        Interval? truth = null;
        var sw = Stopwatch.StartNew();
        var certified = !doc.Features.OfType<Fillet>().Any();
        if (certified && part?.Field is { } field && (frep.Outcome == Outcome.Built || occt.Outcome == Outcome.Built))
            truth = MassProperties.VolumeBounds(field, part.Min, part.Max, TruthCell);

        BackendRun? referee = null;
        if (frep.Outcome == Outcome.Built && occt.Outcome == Outcome.Built && !SameTopology(frep.Topology!, occt.Topology!))
            referee = RunFrep(doc, Fine, out _);
        var labels = Classify(frep, occt, truth, referee);
        if (referee is not null) JudgeTopology(doc, frep.Topology!, occt.Topology!, labels);
        var truthMs = sw.Elapsed.TotalMilliseconds;

        return new Comparison(doc, frep, occt, truth, referee, labels, truthMs);
    }

    /// <summary>
    /// Where both build and the topology differs, three judges in turn.
    /// <list type="number">
    /// <item>A removing feature with faces of its own on one backend only: the certified interference between the part
    /// before it and its tool decides. Deeper than twice the tolerance it is real and the other backend missed it;
    /// shallower than half the tolerance it is a touch and the faces are spurious.</item>
    /// <item>The topologies match once sources of features with no face of their own are dropped: a coincident face named
    /// differently (OCCT keeps the sources of a coincident cut on the merged face), not a failure.</item>
    /// <item>F-rep stable at finer cells: the numbers are snapped to a grid. OCCT at 5e-7 agreeing with F-rep means OCCT
    /// built faces from geometry within its tolerance. Both backends agreeing once snapped to 1e-4, 0.01 or 0.1 mm means
    /// the difference was a detail finer than that: well above OCCT's tolerance, below the cells, and F-rep neither built
    /// nor reported it. (Snapping can also make new coincidences, so this judge is a strong hint, not a proof.)</item>
    /// </list>
    /// </summary>
    static void JudgeTopology(PartDocument doc, Topology frep, Topology occt, SortedSet<string> labels)
    {
        static bool Owns(Topology t, string id) => t.Faces.Any(f => f.Sources.All(s => s.StartsWith(id + "/")));
        void Settle(string label)
        {
            labels.RemoveWhere(l => l is "agree" or "occt:topology-differs" or "topology-differs:unjudged" or "frep:silent:topology");
            labels.Add(label);
        }

        for (var i = 1; i < doc.Features.Count; i++)
        {
            var x = doc.Features[i];
            if (x is not (Hole or PlaneCut or Block { Op: BooleanOp.Cut }) || Owns(frep, x.Id) == Owns(occt, x.Id)) continue;
            // A cut can also take a whole loose piece away and leave no face; judge only a part in one piece.
            var before = new PartDocument(doc.Features.Take(i).ToList());
            if (new FrepModeler(Settings).Regenerate(before).Topology is not { BodyCount: 1 }) continue;
            var depth = Overlap(before, x);
            var (with, without) = Owns(frep, x.Id) ? ("frep", "occt") : ("occt", "frep");
            if (depth > 2 * Settings.Tolerance) Settle($"{without}:silent:missed-feature");
            else if (depth < Settings.Tolerance / 2) Settle($"{with}:silent:spurious-faces");
        }
        if (labels.Any(l => l.EndsWith(":missed-feature") || l.EndsWith(":spurious-faces"))) return;

        var faceless = doc.Features.Select(f => f.Id).Where(id => !Owns(frep, id) && !Owns(occt, id)).Select(id => id + "/").ToList();
        Topology Strip(Topology t) => new(t.Faces.Select(f => f with { Sources = f.Sources.Where(s => !faceless.Any(s.StartsWith)).DefaultIfEmpty("?").ToList() }).ToList(),
            t.Edges, t.Vertices, t.BodyCount);
        if (SameTopology(Strip(frep), Strip(occt)))
        {
            Settle("naming-differs");
            return;
        }

        if (!labels.Contains("occt:topology-differs")) return;
        if (RunOcct(RobustnessDocuments.Snap(doc, 5e-7)) is { Outcome: Outcome.Built, Topology: { } fine } && SameTopology(fine, frep))
            Settle("occt:silent:spurious-faces");
        else if (new[] { 1e-4, 1e-2, 0.1 }.Any(grid => RobustnessDocuments.Snap(doc, grid) is var coarse
            && RunOcct(coarse) is { Outcome: Outcome.Built, Topology: { } o } && RunFrep(coarse, Settings, out _) is { Outcome: Outcome.Built, Topology: { } f } && SameTopology(o, f)))
            Settle("frep:silent:sub-resolution");
    }

    /// <summary>How deep the tool of a removing feature reaches into the part before it (0 when apart or touching).</summary>
    public static double Overlap(PartDocument before, Feature x)
    {
        var part = new Compiler(Settings).Compile(before);
        if (part.Field is null) return 0;
        Field tool = x switch
        {
            Hole h => new LeafField(new Surface(0, "tool", SurfaceKind.Cylinder, new(h.Center[0], h.Center[1], 0), new(0, 0, 1), h.Diameter / 2)),
            // The removed side of the plane is where the normal points: a plane facing back.
            PlaneCut c => new LeafField(new Surface(0, "tool", SurfaceKind.Plane, new(c.Point[0], c.Point[1], c.Point[2]), -new Vector3D(c.Normal[0], c.Normal[1], c.Normal[2]).Normalized())),
            Block b => new Compiler(Settings).Compile(new PartDocument([b with { Op = BooleanOp.New }])).Field!,
            _ => throw new ArgumentException(x.Id),
        };
        var box = new Box3D(part.Min - new Vector3D(1, 1, 1), part.Max + new Vector3D(1, 1, 1));
        var r = Queries.Clearance(part.Field, box, tool, box, tolerance: 1e-9);
        return r.Interferes ? -r.Upper : 0;
    }

    static SortedSet<string> Classify(BackendRun frep, BackendRun occt, Interval? truth, BackendRun? referee)
    {
        var labels = new SortedSet<string>(StringComparer.Ordinal);
        if (frep.Outcome == Outcome.Exception) labels.Add("frep:crash");
        if (occt.Outcome == Outcome.Exception) labels.Add("occt:crash");
        if (occt.Outcome == Outcome.Invalid) labels.Add("occt:invalid-shape");

        bool Outside(double v, Interval t) => v < t.Lo - 1e-6 * Math.Abs(t.Hi) - 1e-6 || v > t.Hi + 1e-6 * Math.Abs(t.Hi) + 1e-6;
        if (frep.Outcome == Outcome.Built)
        {
            if (!frep.Watertight) labels.Add("frep:silent:not-watertight");
            if (truth is { } t && Outside(frep.Volume!.Value, t)) labels.Add("frep:silent:volume");
        }
        if (occt.Outcome == Outcome.Built && truth is { } u && Outside(occt.Volume!.Value, u)) labels.Add("occt:silent:volume");

        var (fb, ob) = (frep.Outcome == Outcome.Built, occt.Outcome == Outcome.Built);
        if (fb && ob)
        {
            var (f, o) = (frep.Volume!.Value, occt.Volume!.Value);
            if (truth is null && Math.Abs(f - o) > 1e-5 * Math.Max(Math.Abs(f), Math.Abs(o)) + 1e-5) labels.Add("volume-differs:unjudged");
            if (referee is not null)
            {
                // The finer F-rep run agrees with OCCT: the default run was wrong. It agrees with itself: OCCT differs.
                if (referee.Topology is { } r && SameTopology(r, occt.Topology!)) labels.Add("frep:silent:topology");
                else if (referee.Topology is { } s && SameTopology(s, frep.Topology!)) labels.Add("occt:topology-differs");
                else labels.Add("topology-differs:unjudged");
            }
            if (!labels.Any(l => l.Contains(":silent:") || l.Contains("differs"))) labels.Add("agree");
        }
        else if (fb && occt.Outcome is Outcome.Diagnosed or Outcome.NoGeometry)
            foreach (var c in occt.FailCodes) labels.Add($"occt:refused:{c}");
        else if (ob && frep.Outcome is Outcome.Diagnosed or Outcome.NoGeometry)
            foreach (var c in frep.FailCodes) labels.Add($"frep:refused:{c}");
        else if (frep.Outcome is Outcome.Diagnosed or Outcome.NoGeometry && occt.Outcome is Outcome.Diagnosed or Outcome.NoGeometry or Outcome.Invalid)
            labels.Add("both-diagnose");
        return labels;
    }

    public static BackendRun RunFrep(PartDocument doc, ModelingSettings settings, out CompiledPart? part)
    {
        part = null;
        var sw = Stopwatch.StartNew();
        try
        {
            var r = new FrepModeler(settings).Regenerate(doc);
            var ms = sw.Elapsed.TotalMilliseconds;
            part = r.Part;
            if (r.Topology is null) return new(Outcome.NoGeometry, r.Diagnostics, null, null, false, ms, null);
            var volume = r.Mesh!.Vertices.Count == 0 ? 0 : MassProperties.Of(r.Part.Field!, r.Mesh).Volume;
            var outcome = r.Diagnostics.Any(d => IsFailure(d.Code)) ? Outcome.Diagnosed : Outcome.Built;
            return new(outcome, r.Diagnostics, r.Topology, volume, r.Mesh.IsWatertight(), ms, null);
        }
        catch (Exception e)
        {
            return new(Outcome.Exception, [], null, null, false, sw.Elapsed.TotalMilliseconds, $"{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>OCCT in its own process (see <see cref="OcctWorker"/>), so a native crash is counted instead of fatal.</summary>
    public static BackendRun RunOcct(PartDocument doc) => OcctWorker.Run(doc);

    public static BackendRun RunOcctHere(PartDocument doc)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var r = new OcctModeler(Settings).Regenerate(doc);
            var volume = r.Shape?.Volume;
            var ms = sw.Elapsed.TotalMilliseconds;
            if (r.Topology is null) return new(Outcome.NoGeometry, r.Diagnostics, null, null, false, ms, null);
            var outcome = r.Shape!.IsValid
                ? r.Diagnostics.Any(d => IsFailure(d.Code)) ? Outcome.Diagnosed : Outcome.Built
                : Outcome.Invalid;
            return new(outcome, r.Diagnostics, r.Topology, volume, true, ms, null);
        }
        catch (Exception e)
        {
            return new(Outcome.Exception, [], null, null, false, sw.Elapsed.TotalMilliseconds, $"{e.GetType().Name}: {e.Message}");
        }
    }

    public static bool SameTopology(Topology a, Topology b) =>
        a.Faces.Count == b.Faces.Count && a.Edges.Count == b.Edges.Count && a.Vertices.Count == b.Vertices.Count && a.BodyCount == b.BodyCount
        && Describe(a).SequenceEqual(Describe(b));

    /// <summary>Order-independent: each face's sources and its neighbours' sources.</summary>
    public static List<string> Describe(Topology t) =>
        t.Faces.Select(f => $"{string.Join("+", f.Sources)} | {string.Join(",", t.Signature(f.Index))}").Order(StringComparer.Ordinal).ToList();

    public static string Summary(BackendRun r) =>
        $"{r.Outcome}" + (r.Topology is { } t ? $" {t.Faces.Count}F {t.Edges.Count}E {t.Vertices.Count}V {t.BodyCount}B" : "")
        + (r.Volume is { } v ? $" vol {v:G10}" : "")
        + (r.Diagnostics.Count > 0 ? $" [{string.Join("; ", r.Diagnostics.Select(d => $"{d.Code}: {d.Message}"))}]" : "")
        + (r.Error is { } e ? $" {e}" : "");
}
