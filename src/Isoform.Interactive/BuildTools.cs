using System.Diagnostics;
using System.Text.Json.Nodes;
using Isoform.Build;
using Isoform.Document;
using Isoform.Modeling;

namespace Isoform.Interactive;

/// <summary>
/// The build and verify tools (docs/demo-plan.md §3–§5), shared by the MCP server and the chat as <see cref="AgentTools"/>
/// is. They run natively: a build regenerates the design and searches every part on all cores, too much for the
/// playground's single thread. The defaults (1 mm cells, 20,000 boxes per certified search) keep a build to seconds.
/// </summary>
public static class BuildTools
{
    public const double DefaultCellSize = 1;
    public const int DefaultBudget = 20_000;

    /// <summary>
    /// A design rationalised by a build document, everything written to <paramref name="folder"/>: the bundle unpacked
    /// (<c>assembly.json</c>, the design, the build document and one document per distinct part, each at its path in the
    /// bundle), <c>&lt;design&gt;.step</c>, a DXF per distinct plate, <c>bom</c> and <c>cut-list</c> (CSV and markdown)
    /// and the verification report (<c>report.json</c>, <c>report.md</c>). Returns the verdict and each check's, the
    /// build's diagnostics, the files and a markdown summary: the report's head, the bill of materials and the cut list.
    /// </summary>
    public static JsonObject Build(string design, string build, string folder, double cellSize = DefaultCellSize, int budget = DefaultBudget)
    {
        var (part, plan) = (AgentTools.Part(design), CanonicalFormat.ReadBuild(AgentTools.Text(build)));
        var settings = AgentTools.Settings(cellSize, new ModelingSettings().ChordTolerance);
        var (clock, ms) = (Stopwatch.StartNew(), new JsonObject());
        void Lap(string stage)
        {
            ms[stage] = Math.Round(clock.Elapsed.TotalMilliseconds);
            clock.Restart();
        }
        var regenerated = Regenerate(part, settings);
        Lap("regenerate");
        var result = Rationaliser.PlatesAndTubes(cellSize).Run(plan, part, regenerated);
        Lap("build");

        var name = Path.GetFileNameWithoutExtension(plan.Design);
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["assembly.json"] = CanonicalFormat.Write(result.Bundle.Assembly) };
        foreach (var (path, text) in result.Bundle.Documents) files[path] = text;
        files[name + ".step"] = Outputs.Step(result, settings, name);
        foreach (var (path, text) in Outputs.Dxf(result)) files[path] = text;
        var (bom, cuts) = (Outputs.Bom(result, plan.Material), Outputs.CutList(result, plan.Material));
        (files["bom.csv"], files["bom.md"], files["cut-list.csv"], files["cut-list.md"]) = (Outputs.Csv(bom), Outputs.Markdown(bom), Outputs.Csv(cuts), Outputs.Markdown(cuts));
        Lap("outputs");
        var verification = new Verifier(settings, budget).Verify(result, plan, part, regenerated);
        Lap("verify");

        (files["report.json"], files["report.md"]) = (Report.Json(verification), Report.Markdown(verification));
        var o = Verdicts(files["report.json"]);
        Write(o, folder, files);
        o["diagnostics"] = AgentTools.Diagnostics(result.Diagnostics);
        o["summary"] = Head(files["report.md"]) + "\n## Bill of materials\n\n" + files["bom.md"] + (cuts.Count > 0 ? "\n## Cut list\n\n" + files["cut-list.md"] : "");
        o["ms"] = ms;
        return o;
    }

    /// <summary>
    /// Any bundle, built or made by hand, checked against its design: a bundle (text or file), or an assembly file with
    /// its parts beside it. The build document defaults to the bundle's own (or <c>build.json</c> beside the assembly),
    /// the design to the one it names there; with no build document the checks use the default tolerance and no
    /// material, keep-outs or interfaces. Fitted edges are not checked: only a build knows them. With
    /// <paramref name="folder"/>, writes <c>report.json</c> and <c>report.md</c> there and returns the report's head;
    /// without, returns the whole markdown report.
    /// </summary>
    public static JsonObject Verify(string bundle, string? design = null, string? build = null, string? folder = null,
        double cellSize = DefaultCellSize, int budget = DefaultBudget)
    {
        var text = AgentTools.Text(bundle);
        var (assembly, load) = AgentTools.Assembled(bundle, text) ?? throw new ToolException(AgentTools.AssemblyTextHelp);
        var own = CanonicalFormat.FormatOf(text) == Bundle.FormatVersion ? CanonicalFormat.ReadBundle(text).Documents : null;
        var buildText = build is not null ? AgentTools.Text(build)
            : own?.Values.FirstOrDefault(d => CanonicalFormat.FormatOf(d) == BuildDocument.FormatVersion) ?? (own is null ? load("build.json") : null);
        var plan = buildText is null ? new BuildDocument("", []) : CanonicalFormat.ReadBuild(buildText);
        var part = design is not null ? AgentTools.Part(design)
            : load(plan.Design) is { } d ? CanonicalFormat.Read(d)
            : throw new ToolException("give the design: the bundle has no build document naming one it holds");
        // An assembly file's documents as a bundle holds them: its parts, and the build and design beside it.
        var documents = own ?? assembly.Components.Select(c => c.Part).Append("build.json").Append(plan.Design).Distinct()
            .Select(p => (Path: p, Text: load(p))).Where(p => p.Text is not null).ToDictionary(p => p.Path, p => p.Text!);
        var settings = AgentTools.Settings(cellSize, new ModelingSettings().ChordTolerance);
        var clock = Stopwatch.StartNew();
        var verification = new Verifier(settings, budget).Verify(plan, part, Regenerate(part, settings), new Bundle(assembly, documents));
        var ms = Math.Round(clock.Elapsed.TotalMilliseconds);

        var (json, markdown) = (Report.Json(verification), Report.Markdown(verification));
        var o = Verdicts(json);
        if (folder is null) o["report"] = markdown;
        else
        {
            Write(o, folder, new SortedDictionary<string, string>(StringComparer.Ordinal) { ["report.json"] = json, ["report.md"] = markdown });
            o["summary"] = Head(markdown);
        }
        o["ms"] = ms;
        return o;
    }

    static FrepResult Regenerate(PartDocument design, ModelingSettings settings)
    {
        var r = new FrepModeler(settings).Regenerate(design);
        return r.Topology is null ? throw new ToolException($"the design does not regenerate: {string.Join("; ", r.Diagnostics.Select(d => d.Message))}") : r;
    }

    // The files written to the folder, which o then names.
    static void Write(JsonObject o, string folder, SortedDictionary<string, string> files)
    {
        foreach (var (name, text) in files)
        {
            var path = Path.Combine(folder, name);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, text);
        }
        o["folder"] = Path.GetFullPath(folder);
        o["files"] = new JsonArray([.. files.Keys.Select(k => (JsonNode)k)]);
    }

    // The overall verdict, each check's, and the bundle's hash, as the JSON report names them.
    static JsonObject Verdicts(string report)
    {
        var r = JsonNode.Parse(report)!;
        var checks = new JsonObject();
        foreach (var c in r["checks"]!.AsArray()) checks[(string)c!["check"]!] = (string)c["verdict"]!;
        return new JsonObject { ["verdict"] = (string)r["verdict"]!, ["checks"] = checks, ["bundle"] = (string)r["bundle"]! };
    }

    // The report's verdict line, hashes and settings, and its checks table: what comes before the parts.
    static string Head(string markdown) => markdown[..(markdown.IndexOf("\n## Parts", StringComparison.Ordinal) + 1)];
}
