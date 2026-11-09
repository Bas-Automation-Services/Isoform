using Isoform.Brep.Modeling;
using Isoform.Brep.Tests;
using Isoform.Modeling;
using Isoform.Step;
using Xunit.Abstractions;

namespace Isoform.Occt.Tests;

/// <summary>
/// The managed B-rep kernel's bodies, written as STEP by Isoform and read back by OCCT: valid solids with the kernel's
/// face, edge and corner counts and its exact volume, both with OCCT's shape healing and without it (but for a sphere
/// cap holding a pole, which needs it).
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
        var step = StepConversion.ToStep(result.Bodies, name);
        StepWriter.Write(step, path, name);
        var (shape, raw) = (OcctShape.ReadStep(path), OcctShape.ReadStep(path, heal: false));
        File.Delete(path);
        // OCCT's default Gauss orders miss by 1e-9 on faces bounded by fitted B-spline images (traced curves).
        double Volume(OcctShape s) => Documents.IsTraced(name) ? s.VolumeWithin(1e-13) : s.Volume;

        output.WriteLine($"kernel: {t.Faces.Count}F {t.Edges.Count}E {t.Vertices.Count}V {t.BodyCount}B");
        output.WriteLine($"OCCT:   {StepExportTests.Counts(shape)}, valid {shape.IsValid}, {shape.SolidCount} solid(s), max tolerance {shape.MaxTolerance:E2}");
        output.WriteLine($"unhealed: valid {raw.IsValid}, max tolerance {raw.MaxTolerance:E2}, relative volume error {Math.Abs(Volume(raw) - volume) / volume:E2}");
        if (!raw.IsValid) output.WriteLine(raw.CheckReport);

        shape.IsValid.Should().BeTrue();
        shape.SolidCount.Should().Be(t.BodyCount);
        // OCCT reads the file's own faces, edges and corners. The topology runs one edge on between the same two faces
        // where the file has two meeting at a point of only those faces (through a pole, a half cone's two generators;
        // at a seam's foot, a hyperbola the seam ends on), so against it only faces and corners are compared.
        var pole = result.Bodies.Any(b => b.Body.Edges.Any(e => e.Curve is Isoform.Brep.Pole));
        var file = StepExportTests.Counts(step);
        StepExportTests.Counts(shape).Should().Be(file);
        if (!pole) (file.Faces, file.Vertices).Should().Be((t.Faces.Count, t.Vertices.Count));
        var tolerance = Math.Max(1e-11, Documents.VolumeTolerance(name));
        Volume(shape).Should().BeApproximately(volume, volume * tolerance);
        // A sphere cap holding a pole needs the reader's healing, as Isoform's own export does (StepExportTests): the
        // degenerate edge there is one STEP cannot say.
        if (pole) return;
        raw.IsValid.Should().BeTrue("the file needs no healing");
        raw.MaxTolerance.Should().BeLessThanOrEqualTo(1e-7);
        Volume(raw).Should().BeApproximately(volume, volume * tolerance);
    }
}
