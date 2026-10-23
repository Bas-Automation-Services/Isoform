using System.Diagnostics;
using System.Text;
using Isoform.Document;
using Isoform.Tests;
using Xunit.Abstractions;

namespace Isoform.Occt.Tests;

/// <summary>
/// K7: seeded random documents over the features both backends support, run on both and judged. The committed run is
/// short; <c>ISOFORM_K7_SEEDS=3000</c> (and optionally <c>ISOFORM_K7_FIRST</c>) runs thousands and writes the full report
/// with minimized repros to <c>robustness-report.md</c> next to the test assembly.
/// </summary>
public class RobustnessTests(ITestOutputHelper output)
{
    static int Env(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : fallback;

    [Fact]
    public void Random_documents_on_both_backends()
    {
        var (first, count) = (Env("ISOFORM_K7_FIRST", 0), Env("ISOFORM_K7_SEEDS", 150));
        var sw = Stopwatch.StartNew();
        var results = Enumerable.Range(first, count).Select(seed => (Seed: seed, C: Robustness.Compare(RobustnessDocuments.Common(seed)))).ToList();
        var wall = sw.Elapsed;

        var report = Report(results, wall, minimize: Env("ISOFORM_K7_MINIMIZE", 0));
        output.WriteLine(report);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "robustness-report.md"), report);
        // Every seed's labels, to diff two runs by document.
        File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "robustness-labels.txt"), results.Select(r => $"{r.Seed}: {string.Join(" ", r.C.Labels.Order(StringComparer.Ordinal))}"));

        // What holds today and must keep holding; the rest is measured, with known failures as skipped repros.
        results.Should().NotContain(r => r.C.Labels.Contains("frep:crash"), "the F-rep core never throws on a valid document");
    }

    static readonly (string Label, string Text)[] Classes =
    [
        ("crash", "crash (exception)"),
        ("silent:volume", "silent: volume outside the certified range"),
        ("silent:not-watertight", "silent: mesh not watertight"),
        ("silent:topology", "silent: topology wrong (finer cells agree with the other backend)"),
        ("silent:missed-feature", "silent: a cut that reaches into the part left no face"),
        ("silent:sub-resolution", "silent: geometry below the cells neither built nor reported"),
        ("silent:spurious-faces", "silent: faces from a contact within tolerance"),
        ("topology-differs", "unjudged: topology differs, F-rep stable at finer cells"),
        ("lost-names", "faces whose names the operation history lost"),
        ("invalid-shape", "result reported invalid"),
        ("refused:", "diagnosed where the other backend built it"),
    ];

    static string Report(List<(int Seed, Comparison C)> results, TimeSpan wall, int minimize)
    {
        var n = results.Count;
        var sb = new StringBuilder();
        sb.AppendLine($"# K7 robustness: {n} random documents (seeds {results[0].Seed}–{results[^1].Seed}), cells {Robustness.Settings.CellSize} mm");
        sb.AppendLine();
        string Pct(int k) => $"{k} ({100.0 * k / n:F1}%)";
        int Count(Func<IReadOnlySet<string>, bool> p) => results.Count(r => p(r.C.Labels));
        bool Has(IReadOnlySet<string> labels, string backend, string cls) => labels.Any(l => l.StartsWith($"{backend}:{cls}"));

        sb.AppendLine("| Failure class | F-rep | OCCT |");
        sb.AppendLine("|---|---|---|");
        foreach (var (label, text) in Classes)
            sb.AppendLine($"| {text} | {Pct(Count(l => Has(l, "frep", label)))} | {Pct(Count(l => Has(l, "occt", label)))} |");
        sb.AppendLine($"| **silent, any** | **{Pct(Count(l => Has(l, "frep", "silent:")))}** | **{Pct(Count(l => Has(l, "occt", "silent:")))}** |");
        var fails = (F: Count(l => l.Any(x => x.StartsWith("frep:"))), O: Count(l => l.Any(x => x.StartsWith("occt:"))));
        sb.AppendLine($"| **any of the above** | **{Pct(fails.F)}** | **{Pct(fails.O)}** |");
        sb.AppendLine();
        sb.AppendLine($"Both diagnose: {Pct(Count(l => l.Contains("both-diagnose")))}. Both build and agree: {Pct(Count(l => l.Contains("agree")))}, "
            + $"only face naming differs (coincident faces): {Pct(Count(l => l.Contains("naming-differs")))}. "
            + $"Unjudged: volume differs with fillets {Pct(Count(l => l.Contains("volume-differs:unjudged")))}, topology {Pct(Count(l => l.Contains("topology-differs:unjudged")))}.");
        sb.AppendLine();

        sb.AppendLine("Diagnosed where the other backend built it, by code:");
        sb.AppendLine();
        foreach (var g in results.SelectMany(r => r.C.Labels.Where(l => l.Contains(":refused:"))).GroupBy(l => l).OrderBy(g => g.Key))
            sb.AppendLine($"- `{g.Key}`: {g.Count()}");
        sb.AppendLine();

        string Times(Func<Comparison, double> ms)
        {
            var t = results.Select(r => ms(r.C)).Order().ToList();
            return $"mean {t.Average():F1} ms, median {t[t.Count / 2]:F1}, p95 {t[(int)(t.Count * 0.95)]:F1}, max {t[^1]:F0}";
        }
        sb.AppendLine($"Timing ({wall.TotalSeconds:F0} s wall): F-rep regenerate {Times(c => c.Frep.Ms)}; OCCT regenerate {Times(c => c.Occt.Ms)}; "
            + $"certified volume + referee {Times(c => c.TruthMs)}.");
        sb.AppendLine();

        // Minimized repros: for every failure label, the first few documents, shrunk while the label persists.
        sb.AppendLine("## Minimized repros");
        var seen = new HashSet<string>();
        foreach (var label in results.SelectMany(r => r.C.Labels).Where(l => l.Contains(':')).Distinct().Order(StringComparer.Ordinal))
        {
            var cases = results.Where(r => r.C.Labels.Contains(label)).ToList();
            sb.AppendLine();
            sb.AppendLine($"### `{label}`: {cases.Count} documents");
            foreach (var (seed, c) in cases.OrderBy(r => r.C.Doc.Features.Count).Take(Math.Max(1, minimize)))
            {
                var small = minimize > 0 ? RobustnessDocuments.Minimize(c.Doc, d => Robustness.Compare(d).Labels.Contains(label)) : c.Doc;
                var json = RobustnessDocuments.Compact(small);
                if (!seen.Add(label + json)) continue;
                var again = minimize > 0 ? Robustness.Compare(small) : c;
                sb.AppendLine();
                sb.AppendLine($"Seed {seed}, {c.Doc.Features.Count} → {small.Features.Count} features:");
                sb.AppendLine("```json");
                sb.AppendLine(json);
                sb.AppendLine("```");
                sb.AppendLine($"- F-rep: {Robustness.Summary(again.Frep)}");
                sb.AppendLine($"- OCCT: {Robustness.Summary(again.Occt)}");
                if (again.Truth is { } t) sb.AppendLine($"- certified volume: [{t.Lo:G10}, {t.Hi:G10}]");
                if (again.Referee is { } r) sb.AppendLine($"- F-rep at {Robustness.Settings.CellSize / 4} mm cells: {Robustness.Summary(r)}");
                if (again.Frep.Topology is { } ft && again.Occt.Topology is { } ot && !Robustness.SameTopology(ft, ot))
                {
                    var (fd, od) = (Robustness.Describe(ft), Robustness.Describe(ot));
                    foreach (var x in fd.Except(od)) sb.AppendLine($"  - F-rep only: `{x}`");
                    foreach (var x in od.Except(fd)) sb.AppendLine($"  - OCCT only: `{x}`");
                }
            }
        }
        return sb.ToString();
    }
}
