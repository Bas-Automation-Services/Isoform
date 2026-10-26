using Isoform.Brep.Modeling;
using Isoform.Brep.Tests;
using Isoform.Modeling;
using Isoform.Step;
using Xunit.Abstractions;

namespace Isoform.Occt.Tests;

/// <summary>
/// The managed B-rep kernel's bodies, written as STEP by Isoform and read back by OCCT: valid solids with the kernel's
/// face, edge and corner counts and its exact volume, both with OCCT's shape healing and without it.
/// </summary>
public class BrepKernelStepTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Names => Documents.All.Select(d => new[] { d[0] });

    [Theory]
    [MemberData(nameof(Names))]
    public void Kernel_step_reads_back_in_occt_as_the_same_solid(string name)
    {
        var (document, volume) = (Documents.Named(name), (double)Documents.All.Single(d => (string)d[0] == name)[2]);
        var result = new BrepModeler(new ModelingSettings()).Regenerate(document);
        result.Diagnostics.Should().BeEmpty();
        var t = result.Topology!;

        var path = Path.Combine(Path.GetTempPath(), $"isoform-{Guid.NewGuid():N}.step");
        StepWriter.Write(StepConversion.ToStep(result.Bodies, name), path, name);
        var (shape, raw) = (OcctShape.ReadStep(path), OcctShape.ReadStep(path, heal: false));
        File.Delete(path);

        output.WriteLine($"kernel: {t.Faces.Count}F {t.Edges.Count}E {t.Vertices.Count}V {t.BodyCount}B");
        output.WriteLine($"OCCT:   {StepExportTests.Counts(shape)}, valid {shape.IsValid}, {shape.SolidCount} solid(s), max tolerance {shape.MaxTolerance:E2}");
        output.WriteLine($"unhealed: valid {raw.IsValid}, max tolerance {raw.MaxTolerance:E2}, relative volume error {Math.Abs(raw.Volume - volume) / volume:E2}");
        if (!raw.IsValid) output.WriteLine(raw.CheckReport);

        shape.IsValid.Should().BeTrue();
        shape.SolidCount.Should().Be(t.BodyCount);
        StepExportTests.Counts(shape).Should().Be((t.Faces.Count, t.Edges.Count, t.Vertices.Count));
        shape.Volume.Should().BeApproximately(volume, volume * 1e-11);
        raw.IsValid.Should().BeTrue("the file needs no healing");
        raw.MaxTolerance.Should().BeLessThanOrEqualTo(1e-7);
        raw.Volume.Should().BeApproximately(volume, volume * 1e-11);
    }
}
