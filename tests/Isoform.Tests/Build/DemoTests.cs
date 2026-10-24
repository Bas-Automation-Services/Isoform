using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Isoform.Build;
using Isoform.Document;
using Isoform.Interactive;
using Isoform.Modeling;
using Xunit.Abstractions;

namespace Isoform.Tests.Build;

/// <summary>
/// The end-to-end demo (docs/demo.md): the pump stand built by the <c>build</c> tool from <c>Examples.PumpStand</c> and
/// its build document, as MCP and the chat run it; then the design edited (height 600 → 700, splay 110 → 140) and built
/// again. The STEP file is read back by OCCT in Isoform.Occt.Tests (BuildStepTests, on the same design).
/// </summary>
public class DemoTests(DemoTests.Demo demo, ITestOutputHelper output) : IClassFixture<DemoTests.Demo>
{
    const double Density = 7.85e-6;

    /// <summary>
    /// The build run twice into two folders, and the edited design built once more (without its report, which is the
    /// slow part and checked on the first run).
    /// </summary>
    public sealed class Demo
    {
        public string Folder { get; } = Directory.CreateTempSubdirectory("isoform-demo-").FullName;
        public string Again { get; } = Directory.CreateTempSubdirectory("isoform-demo-").FullName;
        public JsonObject Result { get; }
        public TimeSpan Time { get; }
        public TimeSpan Repeat { get; }
        public PartDocument Edited { get; }
        public FrepResult EditedDesign { get; }
        public BuildResult EditedBuild { get; }
        public TimeSpan EditTime { get; }

        public Demo()
        {
            var (design, build) = (CanonicalFormat.Write(Examples.PumpStand), CanonicalFormat.Write(Examples.PumpStandBuild));
            var clock = Stopwatch.StartNew();
            Result = BuildTools.Build(design, build, Folder);
            Time = clock.Elapsed;
            clock.Restart();
            BuildTools.Build(design, build, Again);
            Repeat = clock.Elapsed;

            // The edits an agent sends through apply_edits.
            clock.Restart();
            Edited = CanonicalFormat.Read(DocumentEdits.Apply(design,
                [JsonNode.Parse("""{"set": "parameters.height", "value": 700}""")!, JsonNode.Parse("""{"set": "parameters.splay", "value": 140}""")!]));
            EditedDesign = new FrepModeler(new ModelingSettings { CellSize = BuildTools.DefaultCellSize }).Regenerate(Edited);
            EditedBuild = Rationaliser.PlatesAndTubes(BuildTools.DefaultCellSize).Run(Examples.PumpStandBuild, Edited, EditedDesign);
            EditTime = clock.Elapsed;
        }

        public string Read(string file) => File.ReadAllText(Path.Combine(Folder, file));

        /// <summary>The bundle the folder holds: its assembly and the documents beside it.</summary>
        public Bundle Bundle => new(CanonicalFormat.ReadAssembly(Read("assembly.json")),
            new[] { "stand.json", "build.json", "top.json", "feet.json", "legs.json" }.ToDictionary(p => p, Read));
    }

    JsonNode Report => JsonNode.Parse(demo.Read("report.json"))!;

    static string[] Strings(JsonNode? array) => [.. array!.AsArray().Select(n => (string)n!)];

    // A leg's axis from the foot's top (z = 12) to the top's underside (12 below the top face), its top end splay in.
    static double LegLength(double height, double splay) => Math.Sqrt(2 * splay * splay + (height - 24) * (height - 24));

    static double Lean(double height, double splay) => Math.Atan(splay * Math.Sqrt(2) / (height - 24));

    [Fact]
    public void The_build_leaves_the_bundle_step_dxfs_cut_list_bom_and_report()
    {
        output.WriteLine($"build and report {demo.Time.TotalSeconds:F1} s ({demo.Result["ms"]!.ToJsonString()}), again {demo.Repeat.TotalSeconds:F1} s, " +
            $"edited design and build {demo.EditTime.TotalSeconds:F1} s");
        output.WriteLine((string)demo.Result["summary"]!);

        Strings(demo.Result["files"]).Should().Equal("assembly.json", "bom.csv", "bom.md", "build.json", "cut-list.csv", "cut-list.md", "feet.dxf", "feet.json",
            "legs.json", "report.json", "report.md", "stand.json", "stand.step", "top.dxf", "top.json");
        demo.Result["diagnostics"]!.AsArray().Should().BeEmpty();
        // The folder holds the bundle the report names, byte for byte: the design and build document as given.
        var bundle = demo.Bundle;
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalFormat.Write(bundle)))).Should().Be((string)demo.Result["bundle"]!);
        bundle.Documents["stand.json"].Should().Be(CanonicalFormat.Write(Examples.PumpStand));
        bundle.Documents["build.json"].Should().Be(CanonicalFormat.Write(Examples.PumpStandBuild));
        bundle.Assembly.Components.Select(c => c.Id).Should().Equal("top", "feet-1", "feet-2", "feet-3", "feet-4", "legs-1", "legs-2", "legs-3", "legs-4");
        bundle.Assembly.Components.Should().OnlyContain(c => c.Hash == CanonicalFormat.Hash(CanonicalFormat.Read(bundle.Documents[c.Part])));
        // One product per distinct part and the assembly, an occurrence per instance; OCCT reads it in BuildStepTests.
        var step = demo.Read("stand.step");
        (step.Split("NEXT_ASSEMBLY_USAGE_OCCURRENCE(").Length - 1).Should().Be(9);
        (step.Split("PRODUCT(").Length - 1).Should().Be(4);
        demo.Read("feet.dxf").Should().Contain("CIRCLE");
    }

    [Fact]
    public void The_bill_of_materials_and_the_cut_list()
    {
        var bom = demo.Read("bom.csv").Split('\n');
        bom[..4].Should().Equal("part,kind,quantity,description,mass each (kg),mass (kg)", "top,plate,1,Plate 12 S235 270 × 270,5.74,5.74",
            "feet,plate,4,Plate 12 S235 160 × 160,2.39,9.55", "legs,tube,4,Tube Ø48.3 × 3.2 S235 L 609.7,2.12,8.49");
        bom[4].Should().Be("total,,9,,,23.79");
        // The feet and legs as a hand calculation has them: a plate less its anchor hole, an annulus along the axis.
        double Each(int row) => double.Parse(bom[row].Split(',')[4], System.Globalization.CultureInfo.InvariantCulture);
        Each(2).Should().BeApproximately((160 * 160 - Math.PI * 81) * 12 * Density, 0.005);
        Each(3).Should().BeApproximately(Math.PI * (24.15 * 24.15 - 20.95 * 20.95) * LegLength(600, 110) * Density, 0.005);

        demo.Read("cut-list.csv").Should().Be("part,quantity,size,material,long point,short point,end A,end B,turn\nlegs,4,Ø48.3 × 3.2,S235,609.7,583.6,15.1°,15.1°,180°\n");
        demo.Read("cut-list.md").Should().Contain("| legs | 4 | Ø48.3 × 3.2 | S235 | 609.7 | 583.6 | 15.1° | 15.1° | 180° |");
    }

    [Fact]
    public void The_report_passes_what_is_certified_and_fails_nothing()
    {
        var report = Report;
        var checks = report["checks"]!.AsArray().ToDictionary(c => (string)c!["check"]!, c => (string)c!["verdict"]!);
        output.WriteLine(string.Join(", ", checks.Select(c => $"{c.Key} {c.Value}")));

        // Proved today: every joint a contact without interference, every fitted edge within the tolerance, no diagnostics.
        (checks["contacts"], checks["fits"], checks["diagnostics"]).Should().Be(("pass", "pass", "pass"));
        report["contacts"]!.AsArray().Should().HaveCount(8).And.OnlyContain(c => (string)c!["kind"]! == "contact" && (string)c["verdict"]! == "pass");
        report["fits"]!.AsArray().Should().HaveCount(8).And.OnlyContain(f => (double)f!["error"]! <= 0.05);
        // The pump sits on the top and the sockets stand on the feet, touching and clear.
        report["keepouts"]!.AsArray().Where(k => (string)k!["body"]! != "hose").Select(k => ((string)k!["body"]!, (string)k["verdict"]!)).Should().Equal(
            ("pump", "pass"), ("socket-1", "pass"), ("socket-2", "pass"), ("socket-3", "pass"), ("socket-4", "pass"));
        // Every interface lies on a part face of the same surface at every vertex; the pump seat and the floor are covered.
        var interfaces = report["interfaces"]!.AsArray();
        interfaces.Select(i => (string)i!["face"]!).Should().Equal("cap/face", "mount/wall", "bore/wall", "foot/z-", "anchor/wall");
        interfaces.Should().OnlyContain(i => (int)i!["unmatched"]! == 0 && (double)i["worst"]! <= 1e-6 && (int)i["vertices"]! > 0);
        interfaces.Where(i => (string)i!["face"]! is "cap/face" or "foot/z-").Should().OnlyContain(i => (string)i!["verdict"]! == "pass");
        // The legs lie 0.85 inside the Ø50 design legs, settled.
        report["outside"]!.AsArray().Where(o => (string)o!["part"]! == "legs.json").Should().HaveCount(4)
            .And.OnlyContain(o => (string)o!["verdict"]! == "pass" && Math.Abs((double)o["excess"]!["bounds"]![0]! + 0.85) < 1e-3);

        // The rest is not disproved: what is still unknown (the plates outside the design, the hose's 5 mm in the bore,
        // the holes' coverage) are certified bounds on holes and slabs that do not close yet at this budget; with those
        // bounds closed they read pass, and these asserts can say so.
        checks.Values.Should().NotContain("fail");
        ((string)report["verdict"]!).Should().NotBe("fail");
        report["keepouts"]!.AsArray().Single(k => (string)k!["body"]! == "hose")!["verdict"]!.GetValue<string>().Should().NotBe("fail");
        report["outside"]!.AsArray().Should().OnlyContain(o => (string)o!["verdict"]! != "fail");
        interfaces.Should().OnlyContain(i => (string)i!["verdict"]! != "fail");
    }

    [Fact]
    public void A_repeat_gives_the_same_files_byte_for_byte()
    {
        var files = Directory.GetFiles(demo.Folder).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();

        Directory.GetFiles(demo.Again).Select(Path.GetFileName).Order(StringComparer.Ordinal).Should().Equal(files);
        foreach (var f in files)
            File.ReadAllBytes(Path.Combine(demo.Again, f!)).Should().Equal(File.ReadAllBytes(Path.Combine(demo.Folder, f!)), f);
    }

    [Fact]
    public void The_edited_design_builds_again_with_every_binding_resolved()
    {
        var build = Examples.PumpStandBuild;
        var edited = demo.EditedBuild;

        demo.EditedDesign.Diagnostics.Should().BeEmpty();
        edited.Diagnostics.Should().BeEmpty();
        // Every name of the build document still binds: section faces, interfaces and keep-out bodies.
        var model = new Design(demo.Edited, demo.EditedDesign, build.Keepouts!.Select(k => k.Body));
        foreach (var name in build.Sections.Select(s => s switch { PlateSection p => p.Face!, TubeSection t => t.Along!, _ => "" }).Concat(build.Interfaces!))
            model.Faces(name).Should().NotBeEmpty(name);
        demo.EditedDesign.Part.Bodies.Select(b => b.Name).Should().Contain(build.Keepouts!.Select(k => k.Body));
        model.Copies("leg/a").Should().HaveCount(4);

        // The same three parts in nine instances; the top and the feet unchanged, the legs longer and leaning more.
        edited.Bundle.Assembly.Components.Select(c => (c.Id, c.Part)).Should().Equal(demo.Bundle.Assembly.Components.Select(c => (c.Id, c.Part)));
        edited.Bundle.Documents["top.json"].Should().Be(demo.Read("top.json"));
        edited.Bundle.Documents["feet.json"].Should().Be(demo.Read("feet.json"));
        var leg = TubeShape.Of(edited.Parts["legs.json"])!;
        output.WriteLine($"edited leg: axis {leg.Length:F4} mm, long point {leg.LongPoint:F4}, short point {leg.ShortPoint:F4}, ends {leg.AngleA:F4}°");
        leg.Length.Should().BeApproximately(LegLength(700, 140), 1e-9);
        leg.AngleA.Should().BeApproximately(Lean(700, 140) * 180 / Math.PI, 1e-9).And.BeApproximately(16.3, 0.05);
        leg.AngleB.Should().BeApproximately(leg.AngleA, 1e-9);
        leg.LongPoint.Should().BeApproximately(LegLength(700, 140) + 48.3 * Math.Tan(Lean(700, 140)), 1e-9);
        Outputs.Csv(Outputs.CutList(edited, build.Material)).Should().EndWith("\nlegs,4,Ø48.3 × 3.2,S235,718.5,690.3,16.3°,16.3°,180°\n");
        var top = edited.Bundle.Assembly.Components.Single(c => c.Id == "top").Placement;
        top.Origin[2].Should().Be(688, "the top's underside, 12 below the new height");
    }

    [Fact]
    public void The_diff_between_the_bundles_names_what_changed()
    {
        var diff = DocumentDiff.Between(demo.Bundle, demo.EditedBuild.Bundle);
        foreach (var e in diff) output.WriteLine(e.ToJsonString());

        // The design by its two parameters, the legs' document, and the placements of the top and the legs: nothing else.
        var texts = diff.Select(e => e.ToJsonString()).ToList();
        texts.Should().Contain("""{"document":"stand.json","set":"parameters.height","value":700}""")
            .And.Contain("""{"document":"stand.json","set":"parameters.splay","value":140}""");
        diff.Select(e => (string?)e["document"]).Distinct().Should().BeEquivalentTo([null, "stand.json", "legs.json"]);
        diff.Where(e => e["document"] is null).Select(e => ((string)e["set"]!).Split('.')[1]).Distinct()
            .Should().BeEquivalentTo("top", "legs-1", "legs-2", "legs-3", "legs-4");
        DocumentEdits.Apply(CanonicalFormat.Write(demo.Bundle), diff).Should().Be(CanonicalFormat.Write(demo.EditedBuild.Bundle));
    }
}
