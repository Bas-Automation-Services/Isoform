using System.Diagnostics;
using Isoform.Build;
using Isoform.Modeling;
using Xunit.Abstractions;

namespace Isoform.Tests.Build;

/// <summary>
/// The verifier on the pump stand and a hand-made rationalisation of it (top plate, four feet, four Ø48.3 × 3.2 legs):
/// what each check says, and how long it takes (2 mm cells and a small search budget, to keep the suite quick).
/// </summary>
public class VerifierStandTests(ITestOutputHelper output)
{
    [Fact]
    public void The_hand_made_stand_verifies()
    {
        var settings = new ModelingSettings { CellSize = 2 };
        var sw = Stopwatch.StartNew();
        var regenerated = new FrepModeler(settings).Regenerate(Stand.Design);
        output.WriteLine($"design: {sw.ElapsedMilliseconds} ms");
        regenerated.Diagnostics.Should().BeEmpty();

        sw.Restart();
        var v = new Verifier(settings, 10_000).Verify(Stand.Build, Stand.Design, regenerated, Stand.Bundle(),
            trace: (s, t) => output.WriteLine($"  {s}: {t.TotalMilliseconds:F0} ms"));
        output.WriteLine($"verify: {sw.ElapsedMilliseconds} ms");
        output.WriteLine(Report.Markdown(v));

        // The legs lie 0.85 inside the Ø50 design legs, and leave that much of their sides uncovered.
        v.Outside.Where(o => o.Part == "leg.json").Should().HaveCount(4).And.OnlyContain(o => o.Verdict == Verdict.Pass && Math.Abs(o.Excess.Lower + 0.85) < 1e-3);
        foreach (var side in new[] { "leg/a", "leg/b" })
            v.Uncovered.Single(f => f.Face == side).Should().Match<FaceCover>(f => f.Copies == 4 && f.Excess.Settled && Math.Abs(f.Excess.Lower - 0.85) < 1e-3);
        // The leg's blends are what the tubes leave out: a few mm, its true distance certified at the worst point.
        var blend = v.Uncovered.Single(f => f.Face == "leg/blend");
        blend.Excess.Lower.Should().BeInRange(2, 5);
        blend.Distance!.Value.Lo.Should().BeGreaterThanOrEqualTo(blend.Excess.Lower - 1e-6);
        v.Outside.Where(o => o.Part == "foot.json").Should().OnlyContain(o => o.Verdict == Verdict.Pass);
        // The top's outline is the design's own blend, coincident: the blend's tangent plane bounds it to second order,
        // well within the tolerance though not to 1e-3 at this budget.
        v.Outside.Single(o => o.Instance == "top").Verdict.Should().Be(Verdict.Pass);

        v.Contacts.Should().HaveCount(8).And.OnlyContain(c => c.Kind == ContactKind.Contact && c.Verdict == Verdict.Pass);
        v.Keepouts.Select(k => (k.Body, k.Verdict)).Should().Equal(("pump", Verdict.Pass), ("hose", Verdict.Pass), ("sockets", Verdict.Pass));
        v.Keepouts.Single(k => k.Body == "hose").Lower.Should().BeApproximately(10, 1e-3, "the Ø60 hose in the Ø80 bore");
        v.Interfaces.Should().HaveCount(5).And.OnlyContain(i => i.Unmatched == 0 && i.Worst <= 1e-6 && i.Verdict != Verdict.Fail);
        v.Parts.Select(p => (p.Part, p.Count)).Should().Equal(("top.json", 1), ("foot.json", 4), ("leg.json", 4));
        v.Volumes.PartsOutside.Should().BeApproximately(0, 1e3);
        v.Volumes.PartsMass.Should().BeInRange(20, 30);
        v.Walls.Single(w => w.Part == "foot.json").Thickness.Should().BeApproximately(12, 1e-6);
    }

    [Fact]
    public void The_built_stand_passes_with_every_certified_check_settled()
    {
        // The stand as the plate and tube methods build it: the top's outline fitted to the design's blends, the plates'
        // holes loops of their sketches. Every certified check settles within 20 000 boxes, and the stand passes.
        var settings = new ModelingSettings { CellSize = 2 };
        var regenerated = new FrepModeler(settings).Regenerate(StandBuildTests.Design);
        var built = Rationaliser.PlatesAndTubes().Run(StandBuildTests.Build, StandBuildTests.Design, regenerated);
        var sw = Stopwatch.StartNew();
        var v = new Verifier(settings, 20_000).Verify(built, StandBuildTests.Build, StandBuildTests.Design, regenerated,
            (s, t) => output.WriteLine($"  {s}: {t.TotalMilliseconds:F0} ms"));
        output.WriteLine($"verify: {sw.ElapsedMilliseconds} ms");
        output.WriteLine(Report.Markdown(v));

        v.Verdict.Should().Be(Verdict.Pass);
        v.Outside.Should().OnlyContain(o => o.Excess.Settled && o.Verdict == Verdict.Pass);
        v.Outside.Single(o => o.Instance == "top").Excess.Lower.Should().BeInRange(0, StandBuildTests.Build.Tolerance, "the fitted arcs stand a little out of the blend");
        v.Uncovered.Should().OnlyContain(f => f.Excess.Settled);
        v.Keepouts.Single(k => k.Body == "hose").Lower.Should().BeApproximately(10, 1e-3, "the Ø60 hose in the Ø80 bore");
        v.Interfaces.Should().HaveCount(5).And.OnlyContain(i => i.Verdict == Verdict.Pass && i.Uncovered!.Settled);
    }
}
