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
        // The top's outline is the design's own blend, coincident: the search cannot close on it, and says so.
        v.Outside.Single(o => o.Instance == "top").Verdict.Should().NotBe(Verdict.Fail);

        v.Contacts.Should().HaveCount(8).And.OnlyContain(c => c.Kind == ContactKind.Contact && c.Verdict == Verdict.Pass);
        v.Keepouts.Select(k => (k.Body, k.Verdict)).Should().Equal(("pump", Verdict.Pass), ("hose", Verdict.Pass), ("sockets", Verdict.Pass));
        v.Keepouts.Single(k => k.Body == "hose").Lower.Should().BeApproximately(10, 1e-3, "the Ø60 hose in the Ø80 bore");
        v.Interfaces.Should().HaveCount(5).And.OnlyContain(i => i.Unmatched == 0 && i.Worst <= 1e-6 && i.Verdict != Verdict.Fail);
        v.Parts.Select(p => (p.Part, p.Count)).Should().Equal(("top.json", 1), ("foot.json", 4), ("leg.json", 4));
        v.Volumes.PartsOutside.Should().BeApproximately(0, 1e3);
        v.Volumes.PartsMass.Should().BeInRange(20, 30);
        v.Walls.Single(w => w.Part == "foot.json").Thickness.Should().BeApproximately(12, 1e-6);
    }
}
