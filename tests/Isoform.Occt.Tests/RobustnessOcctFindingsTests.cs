using Isoform.Modeling;
using Isoform.Tests;

namespace Isoform.Occt.Tests;

/// <summary>
/// OCCT failures the K7 runs found, minimized, with the F-rep answer beside them: the baseline's failure modes on
/// record. They pass while OCCT fails this way; a newer OCCT that fixes one makes its test fail, and it can go.
/// </summary>
public class RobustnessOcctFindingsTests
{
    static readonly ModelingSettings Settings = new();

    /// <summary>
    /// A 1e-6 mm sliver block standing in the plate top: OCCT then drops the hole through both completely (volume
    /// 4800, the hole called no-effect, three faces with no history) and calls the shape valid. The certified F-rep
    /// volume is 4561–4568, and F-rep reports the sliver below resolution.
    /// </summary>
    [Fact]
    public void Occt_silently_drops_a_hole_next_to_a_sliver()
    {
        var doc = RobustnessDocuments.Parse("""
            [
              {"id":"plate","type":"block","op":"new","origin":[0,0,0],"size":[40,40,3]},
              {"id":"boss2","type":"block","op":"join","origin":[30,10,2.2],"size":[1E-06,10,1]},
              {"id":"hole4","type":"hole","center":[30,10],"diameter":10}
            ]
            """);

        var occt = new OcctModeler(Settings).Regenerate(doc);
        var frep = new FrepModeler(Settings).Regenerate(doc);
        var truth = MassProperties.VolumeBounds(frep.Part.Field!, frep.Part.Min, frep.Part.Max, 0.25);

        occt.Shape!.IsValid.Should().BeTrue();
        occt.Diagnostics.Select(d => d.Code).Should().Equal(Diagnostic.NoEffect);
        occt.Shape.Volume.Should().BeGreaterThan(truth.Hi + 200, "the hole's 236 mm³ are still there");
        frep.Diagnostics.Should().Contain(d => d.Code == Diagnostic.BelowResolution);
    }

    /// <summary>
    /// A 0.1 mm fillet on an edge of a 1e-6 mm thick block fused to the plate's side: OCCT's fillet recurses until the
    /// native stack overflows (0xC00000FD) and takes the process with it, so it runs in the worker here. F-rep reports the
    /// sliver and the fillet below resolution.
    /// </summary>
    [Fact]
    public void Occt_fillet_overflows_the_stack_on_a_sliver()
    {
        var doc = RobustnessDocuments.Parse("""
            [
              {"id":"plate","type":"block","op":"new","origin":[0,0,0],"size":[10,30,4]},
              {"id":"boss2","type":"block","op":"join","origin":[10,0,0],"size":[4,35,1E-06]},
              {"id":"round4","type":"fillet","edge":["boss2/x+","boss2/y+"],"radius":0.1}
            ]
            """);

        var occt = OcctWorker.Run(doc);
        var frep = new FrepModeler(Settings).Regenerate(doc);

        occt.Outcome.Should().Be(Outcome.Exception);
        occt.Error.Should().Contain("C00000FD");
        frep.Diagnostics.Should().Contain(d => d.Code == Diagnostic.BelowResolution && d.FeatureId == "round4");
    }

    /// <summary>A fillet whose radius equals the height of the face it rolls onto: OCCT fails, F-rep builds the exact quarter round.</summary>
    [Fact]
    public void Occt_fails_a_fillet_as_large_as_its_face()
    {
        var doc = RobustnessDocuments.Parse("""
            [
              {"id":"plate","type":"block","op":"new","origin":[0,0,0],"size":[10,20,12.5]},
              {"id":"round1","type":"fillet","edge":["plate/y-","plate/z-"],"radius":12.5}
            ]
            """);

        var occt = new OcctModeler(Settings).Regenerate(doc);
        var frep = new FrepModeler(Settings).Regenerate(doc);

        occt.Diagnostics.Select(d => d.Code).Should().Equal(Diagnostic.FilletInvalid);
        frep.Diagnostics.Should().BeEmpty();
        var exact = 10 * (20 * 12.5 - (1 - Math.PI / 4) * 12.5 * 12.5);
        MassProperties.Of(frep.Part.Field!, frep.Mesh!).Volume.Should().BeApproximately(exact, 1e-5 * exact);
    }
}
