using System.Diagnostics;
using System.Text;
using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Meshing;
using Isoform.Modeling;
using Xunit.Abstractions;

namespace Isoform.Tests;

/// <summary>
/// K7 for what the OCCT baseline cannot run: seeded random documents with sketches, patterns, loops, chamfers, shells,
/// offsets, blends and several bodies, checked against the kernel's own invariants. No exceptions; the K1 field
/// properties; and where nothing was reported below resolution, a watertight mesh whose volume lies in the certified
/// range of every body. <c>ISOFORM_K7_FREP_SEEDS</c> runs more, <c>ISOFORM_K7_MINIMIZE</c> shrinks repros.
/// </summary>
public class RobustnessFrepTests(ITestOutputHelper output)
{
    static readonly ModelingSettings Settings = new();
    static int Env(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : fallback;

    public sealed record Check(SortedSet<string> Labels, IReadOnlyList<Diagnostic> Diagnostics, string Detail, double Ms);

    /// <summary>Everything that went wrong on one document: empty when it is sound.</summary>
    public static Check Run(PartDocument doc)
    {
        var labels = new SortedSet<string>(StringComparer.Ordinal);
        var detail = new StringBuilder();
        var sw = Stopwatch.StartNew();
        if (CanonicalFormat.Write(CanonicalFormat.Read(CanonicalFormat.Write(doc))) != CanonicalFormat.Write(doc)) labels.Add("round-trip");
        FrepResult r;
        try { r = new FrepModeler(Settings).Regenerate(doc); }
        catch (Exception e)
        {
            labels.Add($"crash:{e.GetType().Name}");
            return new(labels, [], $"{e.GetType().Name}: {e.Message} {e.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}", sw.Elapsed.TotalMilliseconds);
        }
        var ms = sw.Elapsed.TotalMilliseconds;
        if (r.Part.Field is null) return new(labels, r.Diagnostics, "", ms);

        if (FieldViolation(r.Part) is { } k1)
        {
            labels.Add($"k1:{k1.Kind}");
            detail.Append(k1.Detail);
        }
        if (r.Topology!.Faces.Any(f => f.Sources.Count == 0)) labels.Add("face-without-source");

        // A part reported below resolution may be wrong there; everything else must be exactly right.
        if (r.Diagnostics.Any(d => d.Code is Diagnostic.BelowResolution or Diagnostic.TangentContact)) return new(labels, r.Diagnostics, detail.ToString(), ms);
        if (!r.Mesh!.IsWatertight()) labels.Add("not-watertight");
        foreach (var body in r.Part.Bodies)
        {
            var mesh = DualContouring.Build(body.Field, body.Min, body.Max, Settings.CellSize, Settings.ChordTolerance);
            var volume = mesh.Vertices.Count == 0 ? 0 : MassProperties.Of(body.Field, mesh).Volume;
            var bounds = MassProperties.VolumeBounds(body.Field, body.Min, body.Max, Settings.CellSize);
            var slack = 1e-6 * Math.Abs(bounds.Hi) + 1e-6;
            if (volume < bounds.Lo - slack || volume > bounds.Hi + slack)
            {
                labels.Add("volume-outside-bounds");
                detail.Append($"body {body.Name}: mesh volume {volume:G8} outside certified [{bounds.Lo:G8}, {bounds.Hi:G8}]. ");
            }
        }
        return new(labels, r.Diagnostics, detail.ToString(), ms);
    }

    /// <summary>The K1 checks of <see cref="FieldProperties"/>, lighter, reporting the first violation instead of throwing.</summary>
    static (string Kind, string Detail)? FieldViolation(CompiledPart part, int seed = 3)
    {
        var field = part.Field!;
        var (lo, hi) = (part.Min - new Vector3D(5, 5, 5), part.Max + new Vector3D(5, 5, 5));
        var rng = new Random(seed);
        Point3D Random() => new(lo.X + rng.NextDouble() * (hi.X - lo.X), lo.Y + rng.NextDouble() * (hi.Y - lo.Y), lo.Z + rng.NextDouble() * (hi.Z - lo.Z));

        for (var i = 0; i < 3000; i++)
        {
            var p = Random();
            var q = rng.NextDouble() < 0.5 ? Random() : p + new Vector3D(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5, rng.NextDouble() - 0.5);
            var (fp, fq) = (field.Value(p), field.Value(q));
            if (Math.Abs(fp - fq) > (p - q).Length * (1 + 1e-12) + 1e-12)
                return ("lipschitz", $"|f(p) − f(q)| = {Math.Abs(fp - fq):G6} > |p − q| = {(p - q).Length:G6} at p {p}, q {q}. ");
            if (fp != field.Sample(p).Value) return ("value-sample", $"Value {fp} ≠ Sample {field.Sample(p).Value} at {p}. ");
        }
        for (var b = 0; b < 150; b++)
        {
            var size = Math.Pow(10, rng.NextDouble() * 3 - 1.5);
            var min = Random();
            var box = new Box3D(min, new Point3D(min.X + size, min.Y + size, min.Z + size));
            var bounds = field.Bounds(box);
            var simple = field.Simplify(box);
            for (var i = 0; i < 10; i++)
            {
                var p = new Point3D(min.X + size * rng.NextDouble(), min.Y + size * rng.NextDouble(), min.Z + size * rng.NextDouble());
                var (full, reduced) = (field.Sample(p), simple.Sample(p));
                if (full.Value < bounds.Lo - 1e-12 || full.Value > bounds.Hi + 1e-12)
                    return ("bounds", $"value {full.Value:G6} outside bounds [{bounds.Lo:G6}, {bounds.Hi:G6}] at {p} in a box of {size:G3}. ");
                if (reduced.Value != full.Value || reduced.Owner != full.Owner || (reduced.Gradient - full.Gradient).Length != 0)
                    return ("simplify", $"simplified field differs at {p}: {reduced.Value} vs {full.Value}, owner {reduced.Owner} vs {full.Owner}. ");
            }
        }
        return null;
    }

    [Fact]
    public void Random_frep_only_documents_keep_the_kernel_invariants()
    {
        var (first, count, minimize) = (Env("ISOFORM_K7_FIRST", 0), Env("ISOFORM_K7_FREP_SEEDS", 100), Env("ISOFORM_K7_MINIMIZE", 0));
        var sw = Stopwatch.StartNew();
        var results = Enumerable.Range(first, count).Select(seed => (Seed: seed, Doc: RobustnessDocuments.FrepOnly(seed)))
            .Select(x => (x.Seed, x.Doc, C: Run(x.Doc))).ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"# K7 F-rep only: {count} random documents (seeds {first}–{first + count - 1}), {sw.Elapsed.TotalSeconds:F0} s");
        sb.AppendLine();
        string Pct(int k) => $"{k} ({100.0 * k / count:F1}%)";
        sb.AppendLine($"Sound: {Pct(results.Count(r => r.C.Labels.Count == 0))}. Violations:");
        sb.AppendLine();
        foreach (var g in results.SelectMany(r => r.C.Labels).GroupBy(l => l).OrderBy(g => g.Key))
            sb.AppendLine($"- `{g.Key}`: {Pct(g.Count())}");
        sb.AppendLine();
        sb.AppendLine("Diagnostics (documents with each code):");
        sb.AppendLine();
        foreach (var g in results.SelectMany(r => r.C.Diagnostics.Select(d => d.Code).Distinct()).GroupBy(c => c).OrderBy(g => g.Key))
            sb.AppendLine($"- `{g.Key}`: {Pct(g.Count())}");
        var times = results.Select(r => r.C.Ms).Order().ToList();
        sb.AppendLine();
        sb.AppendLine($"Regenerate: mean {times.Average():F1} ms, median {times[count / 2]:F1}, p95 {times[(int)(count * 0.95)]:F1}, max {times[^1]:F0}.");

        sb.AppendLine();
        sb.AppendLine("## Repros");
        foreach (var label in results.SelectMany(r => r.C.Labels).Distinct().Order(StringComparer.Ordinal))
        {
            var cases = results.Where(r => r.C.Labels.Contains(label)).OrderBy(r => r.Doc.Features.Count).Take(Math.Max(1, minimize)).ToList();
            foreach (var (seed, doc, c) in cases)
            {
                var small = minimize > 0 ? RobustnessDocuments.Minimize(doc, d => Run(d).Labels.Contains(label)) : doc;
                var again = minimize > 0 ? Run(small) : c;
                sb.AppendLine();
                sb.AppendLine($"### `{label}`, seed {seed}, {doc.Features.Count} → {small.Features.Count} features");
                sb.AppendLine("```json");
                sb.AppendLine(RobustnessDocuments.Compact(small));
                sb.AppendLine("```");
                sb.AppendLine($"{again.Detail}{string.Join("; ", again.Diagnostics.Select(d => $"{d.Code}: {d.Message}"))}");
            }
        }
        var report = sb.ToString();
        output.WriteLine(report);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "robustness-frep-report.md"), report);

        // Guarded today; the rest is measured, with known failures as skipped repros.
        results.Should().NotContain(r => r.C.Labels.Any(l => l.StartsWith("crash:")), "the kernel never throws on a valid document");
    }
}
