using System.Text.Json.Nodes;
using Isoform.Build;
using Isoform.Document;
using Isoform.Modeling;

namespace Isoform.Tests.Build;

/// <summary>
/// The verifier on hand-made bundles of a small design (a slab with a bore and two posts, a keep-out above them): every
/// check passes on exact parts, and each fails on the part that breaks it, naming the faces.
/// </summary>
public class VerifierTests
{
    static readonly ModelingSettings Settings = new() { CellSize = 2 };

    static readonly PartDocument Frame = new(
    [
        new Block("slab", BooleanOp.New, [0, 0, 0], [100, 40, 10]) { Body = "frame" },
        new Hole("bore", [80, 20], 10),
        new Block("post", BooleanOp.Join, [10, 10, 10], [20, 20, 50]),
        new Pattern("posts", ["post"], new LinearMotion([1, 0, 0], 30, 2)),
        new Block("box", BooleanOp.New, [0, 0, 80], [100, 40, 20]) { Body = "keep" },
    ]);

    static readonly FrepResult Regenerated = new FrepModeler(Settings).Regenerate(Frame);

    static readonly BuildDocument Build = new("frame.json",
    [
        new PlateSection("slab", 10, Face: "slab/z-"),
        new TubeSection("posts", 20, 2, Along: "post/x+"),
    ], Material: new Material("S235", 7.85e-6), Keepouts: [new Keepout("keep", 5)], Interfaces: ["slab/z-", "bore/wall"]);

    static readonly PartDocument Slab = new([new Block("plate", BooleanOp.New, [0, 0, 0], [100, 40, 10]), new Hole("bore", [80, 20], 10)]);

    static PartDocument Bar(double height) => new([new Block("bar", BooleanOp.New, [0, 0, 0], [20, 20, height])]);

    static Placement At(double x, double y, double z) => new([x, y, z], [1, 0, 0], [0, 0, 1]);

    static Bundle Bundle(PartDocument? slab = null, PartDocument? post = null, double postZ = 10) => new(new AssemblyDocument(
    [
        new Component("slab", "slab.json", At(0, 0, 0)),
        new Component("posts-1", "post.json", At(10, 10, postZ)),
        new Component("posts-2", "post.json", At(40, 10, postZ)),
    ]), new Dictionary<string, string>
    {
        ["slab.json"] = CanonicalFormat.Write(slab ?? Slab),
        ["post.json"] = CanonicalFormat.Write(post ?? Bar(50)),
    });

    static Verification Verify(Bundle bundle, BuildDocument? build = null, IReadOnlyList<Diagnostic>? diagnostics = null, IReadOnlyList<EdgeFit>? fits = null) =>
        new Verifier(Settings).Verify(build ?? Build, Frame, Regenerated, bundle, diagnostics, fits);

    static Verdict Check(Verification v, string check) => v.Checks.Single(c => c.Check == check).Verdict;

    [Fact]
    public void Exact_parts_pass_every_check()
    {
        var v = Verify(Bundle());

        v.Verdict.Should().Be(Verdict.Pass);
        v.Checks.Select(c => c.Check).Should().Equal(Verifier.Checks.Outside, Verifier.Checks.Uncovered, Verifier.Checks.Volumes, Verifier.Checks.Contacts,
            Verifier.Checks.Keepouts, Verifier.Checks.Interfaces, Verifier.Checks.Walls, Verifier.Checks.Fits, Verifier.Checks.Diagnostics);
        Check(v, Verifier.Checks.Fits).Should().Be(Verdict.NotChecked);

        v.Outside.Should().OnlyContain(o => o.Excess.Settled && o.Excess.Upper <= 1e-3 && o.Verdict == Verdict.Pass);
        // Pattern copies merge: the posts' sides are one face each, from both copies.
        var side = v.Uncovered.Single(f => f.Face == "post/x+");
        side.Copies.Should().Be(2);
        side.Excess.Upper.Should().BeLessThanOrEqualTo(1e-3);
        v.Uncovered.Should().OnlyContain(f => f.Excess.Upper <= 1e-3, "the parts are the design");
        v.Uncovered.Select(f => f.Face).Should().NotContain(f => f.StartsWith("box/"), "a keep-out is not the design");

        v.Contacts.Select(c => (c.A, c.B, c.Kind)).Should().BeEquivalentTo([("slab", "posts-1", ContactKind.Contact), ("slab", "posts-2", ContactKind.Contact)]);
        v.Keepouts.Single().Should().Match<KeepoutClearance>(k => k.Body == "keep" && k.Lower >= 20 - 1e-3 && k.Upper <= 20 + 1e-3 && k.Verdict == Verdict.Pass);
        v.Interfaces.Should().OnlyContain(i => i.Verdict == Verdict.Pass && i.Unmatched == 0 && i.Worst <= 1e-6);
        v.Interfaces.Single(i => i.Face == "bore/wall").PartFaces.Should().Equal("slab/bore/wall");

        v.Parts.Select(p => (p.Part, p.Count)).Should().Equal(("slab.json", 1), ("post.json", 2));
        v.Volumes.Parts.Should().BeApproximately(v.Volumes.Design, 1);
        v.Volumes.DesignUncovered.Should().BeApproximately(0, 1);
        v.Volumes.PartsMass.Should().BeApproximately(v.Volumes.Parts * 7.85e-6, 1e-9);
        v.Volumes.PartsRange.Lo.Should().BeLessThanOrEqualTo(v.Volumes.Parts);
        v.Volumes.PartsRange.Hi.Should().BeGreaterThanOrEqualTo(v.Volumes.Parts);
        v.Walls.Single(w => w.Part == "post.json").Thickness.Should().BeApproximately(20, 1e-6);
    }

    [Fact]
    public void A_part_poking_out_fails_and_names_both_faces()
    {
        var v = Verify(Bundle(post: Bar(51)));

        v.Verdict.Should().Be(Verdict.Fail);
        var post = v.Outside.Single(o => o.Instance == "posts-1");
        post.Verdict.Should().Be(Verdict.Fail);
        post.Excess.Lower.Should().BeApproximately(1, 1e-3);
        (post.PartFace, post.DesignFace).Should().Be(("posts-1/bar/z+", "post/z+"));
        v.Outside.Single(o => o.Instance == "slab").Verdict.Should().Be(Verdict.Pass);
    }

    [Fact]
    public void Parts_sunk_into_each_other_interfere()
    {
        var v = Verify(Bundle(post: Bar(52), postZ: 8));

        Check(v, Verifier.Checks.Contacts).Should().Be(Verdict.Fail);
        v.Contacts.Should().OnlyContain(c => c.Kind == ContactKind.Interference && c.Upper < -0.05);
    }

    [Fact]
    public void A_keepout_closer_than_its_clearance_fails()
    {
        var v = Verify(Bundle(), Build with { Keepouts = [new Keepout("keep", 25)] });

        Check(v, Verifier.Checks.Keepouts).Should().Be(Verdict.Fail);
        v.Keepouts.Single().Instance.Should().StartWith("posts-");
    }

    [Fact]
    public void An_interface_the_parts_do_not_reproduce_fails()
    {
        var v = Verify(Bundle(slab: new([new Block("plate", BooleanOp.New, [0, 0, 0], [100, 40, 10])])));

        var bore = v.Interfaces.Single(i => i.Face == "bore/wall");
        bore.Verdict.Should().Be(Verdict.Fail);
        bore.Unmatched.Should().Be(bore.Vertices);
        v.Interfaces.Single(i => i.Face == "slab/z-").Verdict.Should().Be(Verdict.Pass);
        v.Outside.Single(o => o.Instance == "slab").Excess.Lower.Should().BeApproximately(5, 1e-3, "the plate fills the bore");
    }

    [Fact]
    public void Fits_and_diagnostics_are_judged_when_given()
    {
        var refusal = new Diagnostic(BuildDiagnostic.RegionEmpty, "posts", "section 'posts' gave no part");
        var good = Verify(Bundle(), fits: [new EdgeFit("slab.json", "lobes/1:lobe/blend", 4, 0.03)]);
        var bad = Verify(Bundle(), diagnostics: [refusal], fits: [new EdgeFit("slab.json", "lobes/1:lobe/blend", 4, 0.08)]);

        Check(good, Verifier.Checks.Fits).Should().Be(Verdict.Pass);
        good.Verdict.Should().Be(Verdict.Pass);
        Check(bad, Verifier.Checks.Fits).Should().Be(Verdict.Fail);
        Check(bad, Verifier.Checks.Diagnostics).Should().Be(Verdict.Unknown);
        bad.Diagnostics.Should().Equal(new SourcedDiagnostic("build", refusal));
        Report.Markdown(bad).Should().Contain("build: `build-region-empty` (posts)").And.Contain("lobes/1:lobe/blend");
    }

    [Fact]
    public void The_report_is_the_same_bytes_for_the_same_inputs()
    {
        var bundle = Bundle(post: Bar(51));
        var json = Report.Json(Verify(bundle));

        Report.Json(Verify(bundle)).Should().Be(json);
        json.Should().NotContain("\r").And.EndWith("}\n");
        var root = JsonNode.Parse(json)!;
        ((string?)root["format"]).Should().Be(Report.FormatVersion);
        ((string?)root["verdict"]).Should().Be("fail");
        ((string?)root["design"]).Should().Be(CanonicalFormat.Hash(Frame));
        ((string?)root["build"]).Should().Be(CanonicalFormat.Hash(Build));
        root["outside"]!.AsArray().Select(o => (string?)o!["instance"]).Should().Equal("slab", "posts-1", "posts-2");
        ((double)root["outside"]![1]!["excess"]!["bounds"]![0]!).Should().BeApproximately(1, 1e-3);
    }

    [Fact]
    public void The_markdown_reads_as_a_report_and_says_what_it_does_not_check()
    {
        var md = Report.Markdown(Verify(Bundle(post: Bar(51))));

        md.Should().StartWith("# Verification: FAIL\n");
        md.Should().Contain("| Parts outside the design | fail | at most 1.000 mm (posts-1/bar/z+ at design post/z+); limit 0.05 mm |");
        md.Should().Contain("| post.json | 2 |");
        md.Should().Contain("## Not checked").And.Contain("**Welds.**").And.Contain("**Fit-up gaps.**").And.Contain("**Strength and stiffness.**");
    }
}
