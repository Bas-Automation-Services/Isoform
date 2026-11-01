using Isoform.Brep.Modeling;
using Isoform.Brep.Tests;
using Isoform.Document;
using Isoform.Modeling;
using Isoform.Step;

namespace Isoform.Occt.Tests;

/// <summary>
/// STEP of a part from the managed kernel where it builds the part as the F-rep core does, else from the F-rep core's
/// exact B-rep: either way OCCT reads the same solid back.
/// </summary>
public class ExactStepTests
{
    static readonly ModelingSettings Settings = new();

    [Theory]
    [InlineData("mounting plate")]
    [InlineData("four ball corners")]
    [InlineData("hollow plate with a bore")]
    public void A_part_the_kernel_builds_comes_from_it_and_reads_back_without_healing(string name)
    {
        var (document, volume) = (Documents.Named(name), (double)Documents.All.Single(d => (string)d[0] == name)[2]);
        var (brep, kernel) = ExactStep.Of(document, new FrepModeler(Settings).Regenerate(document), Settings);
        kernel.Should().BeTrue();
        var shape = Read(brep, heal: false);
        shape.IsValid.Should().BeTrue();
        shape.Volume.Should().BeApproximately(volume, volume * 1e-11);
    }

    [Fact]
    public void A_part_the_kernel_does_not_follow_comes_from_the_frep_core()
    {
        // A chamfer before an offset: the kernel refuses it, the F-rep core's exact B-rep does not.
        var document = new PartDocument(
        [
            new Block("plate", BooleanOp.New, [0, 0, 0], [40, 30, 10]),
            new Chamfer("bevel", ["plate/z+", "plate/x+"], 2),
            new Offset("grow", 1),
        ]);
        var (brep, kernel) = ExactStep.Of(document, new FrepModeler(Settings).Regenerate(document), Settings);
        kernel.Should().BeFalse();
        Read(brep, heal: true).IsValid.Should().BeTrue();
    }

    static OcctShape Read(Step.Brep brep, bool heal)
    {
        var path = Path.Combine(Path.GetTempPath(), $"isoform-{Guid.NewGuid():N}.step");
        StepWriter.Write(brep, path, "part");
        var shape = OcctShape.ReadStep(path, heal);
        File.Delete(path);
        return shape;
    }
}
