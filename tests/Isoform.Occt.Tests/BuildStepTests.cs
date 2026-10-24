using Isoform.Build;
using Isoform.Modeling;
using Isoform.Tests.Build;
using Xunit.Abstractions;

namespace Isoform.Occt.Tests;

/// <summary>
/// B5: the pump stand's build as one STEP file with assembly structure, read back by OCCT's XCAF reader: one product
/// per distinct part, an occurrence per instance placed where the bundle puts it, each with the part's exact volume.
/// </summary>
public class BuildStepTests(ITestOutputHelper output)
{
    [Fact]
    public void The_stands_step_reads_back_as_three_parts_in_nine_occurrences()
    {
        var design = new FrepModeler(new ModelingSettings { CellSize = 1 }).Regenerate(StandBuildTests.Design);
        var result = Rationaliser.PlatesAndTubes().Run(StandBuildTests.Build, StandBuildTests.Design, design);
        result.Diagnostics.Should().BeEmpty();
        var path = Path.Combine(Path.GetTempPath(), $"isoform-{Guid.NewGuid():N}.step");
        File.WriteAllText(path, Outputs.Step(result, new ModelingSettings { CellSize = 1 }, "stand"));
        var read = OcctAssembly.ReadStep(path);
        var plain = OcctShape.ReadStep(path, heal: false);
        File.Delete(path);

        read.Name.Should().Be("stand");
        read.Occurrences.Select(o => (o.Path, o.Product)).Should().Equal(
            ("top", "top"), ("feet-1", "feet"), ("feet-2", "feet"), ("feet-3", "feet"), ("feet-4", "feet"),
            ("legs-1", "legs"), ("legs-2", "legs"), ("legs-3", "legs"), ("legs-4", "legs"));
        read.Occurrences.Select(o => o.Part).Should().Equal(0, 1, 1, 1, 1, 2, 2, 2, 2);
        plain.SolidCount.Should().Be(9);
        plain.IsValid.Should().BeTrue();

        // Each occurrence: valid, placed by its component, with its part's exact volume (a plate's outline area times
        // its thickness, a tube's annulus times its axis).
        var components = result.Bundle.Assembly.Components;
        foreach (var (o, c) in read.Occurrences.Zip(components))
        {
            var part = result.Parts[c.Part];
            var volume = PlateShape.Of(part) is { } plate ? plate.Area * plate.Thickness : TubeShape.Of(part)!.Volume;
            output.WriteLine($"{o.Path}: volume {o.Shape.Volume:F6} (exact {volume:F6}), valid {o.Shape.IsValid}");
            o.Shape.IsValid.Should().BeTrue(o.Path);
            o.Shape.Volume.Should().BeApproximately(volume, volume * 1e-9, o.Path);
            var f = AssemblyModeler.FrameOf(c.Placement);
            double[] expected = [f.X.X, f.Y.X, f.Z.X, f.Origin.X, f.X.Y, f.Y.Y, f.Z.Y, f.Origin.Y, f.X.Z, f.Y.Z, f.Z.Z, f.Origin.Z];
            o.Placement.Zip(expected).Max(x => Math.Abs(x.First - x.Second)).Should().BeLessThan(1e-12, o.Path);
        }
    }
}
