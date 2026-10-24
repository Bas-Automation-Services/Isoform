using System.Diagnostics;
using BasAutomation.Geometry;
using Isoform.Build;
using Isoform.Document;
using Isoform.Geometry;
using Isoform.Interactive;
using Isoform.Modeling;
using Xunit.Abstractions;

namespace Isoform.Tests.Build;

/// <summary>
/// The pump stand of docs/demo-plan.md §1 rationalised end to end: the top and four feet as plates, four legs as tubes;
/// three distinct parts in nine instances, and what leaves (bundle, DXF, cut list, BOM). The STEP file is read back by
/// OCCT in Isoform.Occt.Tests.
/// </summary>
public class StandBuildTests(StandBuildTests.Built stand, ITestOutputHelper output) : IClassFixture<StandBuildTests.Built>
{
    // A leg's axis, from the foot's top to the top's underside: 15.1° off vertical.
    static readonly double LegLength = Math.Sqrt(110 * 110 * 2 + 576 * 576);

    /// <summary>The stand's design model, the D1 example: blended top, anchored feet, splayed legs trimmed by plane cuts, keep-outs.</summary>
    public static PartDocument Design => Examples.PumpStand;

    public static BuildDocument Build => Examples.PumpStandBuild;

    /// <summary>The design regenerated at 1 mm and built once for all the tests.</summary>
    public sealed class Built
    {
        public FrepResult Design { get; }
        public BuildResult Result { get; }
        public TimeSpan Regeneration { get; }
        public TimeSpan Build { get; }

        public Built()
        {
            var clock = Stopwatch.StartNew();
            Design = new FrepModeler(new ModelingSettings { CellSize = 1 }).Regenerate(StandBuildTests.Design);
            Regeneration = clock.Elapsed;
            Result = Rationaliser.PlatesAndTubes().Run(StandBuildTests.Build, StandBuildTests.Design, Design);
            Build = clock.Elapsed - Regeneration;
        }
    }

    BuildResult Result => stand.Result;

    Component Component(string id) => Result.Bundle.Assembly.Components.Single(c => c.Id == id);

    PlateShape Plate(string path) => PlateShape.Of(Result.Parts[path])!;

    static Point3D P(double[] v) => new(v[0], v[1], v[2]);

    [Fact]
    public void The_stand_is_three_distinct_parts_in_nine_instances()
    {
        output.WriteLine($"design {stand.Regeneration.TotalSeconds:F1} s, build {stand.Build.TotalSeconds:F1} s");

        stand.Design.Diagnostics.Should().BeEmpty();
        Result.Diagnostics.Should().BeEmpty();
        Result.Parts.Keys.Should().Equal("top.json", "feet.json", "legs.json");
        Result.Bundle.Assembly.Components.Select(c => (c.Id, c.Part)).Should().Equal(
            ("top", "top.json"), ("feet-1", "feet.json"), ("feet-2", "feet.json"), ("feet-3", "feet.json"), ("feet-4", "feet.json"),
            ("legs-1", "legs.json"), ("legs-2", "legs.json"), ("legs-3", "legs.json"), ("legs-4", "legs.json"));
        Result.Instances.Select(i => i.Section).Should().Equal("top", "feet", "feet", "feet", "feet", "legs", "legs", "legs", "legs");
        Result.Bundle.Assembly.Components.Should().OnlyContain(c => c.Hash == CanonicalFormat.Hash(Result.Parts[c.Part]));
        Result.Bundle.Documents.Keys.Should().BeEquivalentTo("stand.json", "build.json", "top.json", "feet.json", "legs.json");
    }

    [Fact]
    public void Each_instance_is_its_written_part_placed()
    {
        // The part written once, placed by the instance's component, is the part its method made for that instance:
        // the same solid, point for point, across the instance's box.
        var compiler = new Compiler(new ModelingSettings());
        var rng = new Random(7);
        foreach (var instance in Result.Instances)
        {
            var written = new PlacedField(compiler.Compile(Result.Parts[instance.Part]).Field!, AssemblyModeler.FrameOf(Component(instance.Id).Placement));
            var own = new PlacedField(compiler.Compile(instance.Built.Document).Field!, AssemblyModeler.FrameOf(instance.Built.Placement));
            var origin = P(instance.Built.Placement.Origin);
            for (var k = 0; k < 2000; k++)
            {
                var p = origin + new Vector3D(rng.NextDouble() * 700 - 350, rng.NextDouble() * 700 - 350, rng.NextDouble() * 700 - 350) * (k % 2 == 0 ? 1 : 0.3);
                written.Value(p).Should().BeApproximately(own.Value(p), 1e-6, instance.Id);
            }
        }
    }

    [Fact]
    public void The_top_copies_its_arcs_and_holes_and_fits_its_blends()
    {
        var top = Result.Instances.Single(i => i.Id == "top");
        var plate = Plate("top.json");
        var (box, edges) = (plate.Box, plate.Edges);
        var entities = plate.Sketch.Entities;
        output.WriteLine($"top: {entities.Count} entities, {top.Built.Fits.Count} fitted edges of {top.Built.Fits.Sum(f => f.Arcs)} arcs, largest error {top.Built.Fits.Max(f => f.Error):E3}");

        plate.Thickness.Should().Be(12);
        (box.MaxX - box.MinX).Should().BeApproximately(270, 1e-9);
        (box.MinX, box.MinY).Should().Be((0, 0));
        // In the plate's frame the stand's axis is at (135, 135); the plate lies on z 588–600, the frame the world's.
        var placement = Component("top").Placement;
        P(placement.Origin).Should().Be(new Point3D(-135, -135, 588));
        placement.Normal.Should().Equal(0, 0, 1);
        var axis = new Point2D(135, 135);

        // Five holes, copied: the bore and the four mounts, named after their design faces.
        var circles = entities.OfType<SketchCircle>().ToList();
        circles.Select(c => c.Id).Should().BeEquivalentTo(["bore-wall~1", "mount-wall~1", "mounts-1:mount-wall~1", "mounts-2:mount-wall~1", "mounts-3:mount-wall~1"]);
        foreach (var c in edges.Where(e => e.Kind == OutlineEdgeKind.Circle))
        {
            var d = (c.Centre - axis).Length;
            (c.Radius == 40 ? d : Math.Abs(d - 100)).Should().BeLessThan(1e-9);
            c.Radius.Should().BeOneOf(40, 5.5);
        }

        // The outline: the disc's and lobes' arcs exact, the blends between them fitted within the build's tolerance.
        top.Built.Fits.Should().HaveCount(8).And.OnlyContain(f => f.Source.EndsWith("lobe/blend") && f.Error <= Build.Tolerance);
        var fitted = entities.Where(e => e.Id.Contains("lobe-blend")).Select(e => e.Id).ToHashSet();
        fitted.Should().HaveCount(top.Built.Fits.Sum(f => f.Arcs));
        var exact = entities.Zip(edges).Where(x => x.First is SketchArc && !fitted.Contains(x.First.Id)).Select(x => (x.First.Id, Edge: x.Second)).ToList();
        exact.Should().NotBeEmpty();
        foreach (var (id, e) in exact)
        {
            var lobe = id.StartsWith("lobe") || id.Contains(":lobe");
            e.Radius.Should().BeApproximately(lobe ? 45 : 130, 1e-9, id);
            var c = e.Centre - axis;
            (lobe ? Math.Abs(Math.Abs(c.X) - 90) + Math.Abs(Math.Abs(c.Y) - 90) : c.Length).Should().BeLessThan(1e-9, id);
        }
    }

    [Fact]
    public void A_foot_is_four_exact_lines_and_its_anchor_hole()
    {
        var plate = Plate("feet.json");

        plate.Sketch.Entities.Select(e => e.Id).Should().BeEquivalentTo(["anchor-wall~1", "foot-x+~1", "foot-x-~1", "foot-y+~1", "foot-y-~1"]);
        var hole = plate.Edges.Single(e => e.Kind == OutlineEdgeKind.Circle);
        (hole.Centre - new Point2D(120, 120)).Length.Should().BeLessThan(1e-9);
        hole.Radius.Should().BeApproximately(9, 1e-12);
        plate.Edges.Where(e => e.Kind == OutlineEdgeKind.Line).SelectMany(e => new[] { e.From, e.To }).Should()
            .OnlyContain(p => (Math.Abs(p.X) < 1e-9 || Math.Abs(p.X - 160) < 1e-9) && (Math.Abs(p.Y) < 1e-9 || Math.Abs(p.Y - 160) < 1e-9));
        plate.Area.Should().BeApproximately(160 * 160 - Math.PI * 81, 1e-6);
        Result.Instances.Where(i => i.Section == "feet").Should().OnlyContain(i => i.Built.Fits.Count == 0);

        // Each foot where the design has it: the written foot turned a quarter at a time round the stand's axis.
        Point3D[] corners = [new(150, 150, 0), new(-150, 150, 0), new(-150, -150, 0), new(150, -150, 0)];
        for (var k = 0; k < 4; k++)
        {
            var placement = Component($"feet-{k + 1}").Placement;
            (P(placement.Origin) - corners[k]).Length.Should().BeLessThan(1e-9);
            var x = new Vector3D(placement.X[0], placement.X[1], placement.X[2]);
            (x - new Vector3D(Math.Cos(k * Math.PI / 2), Math.Sin(k * Math.PI / 2), 0)).Length.Should().BeLessThan(1e-12);
        }
    }

    [Fact]
    public void A_leg_runs_from_foot_to_top_with_parallel_ends_15_1_degrees_off_square()
    {
        var tube = TubeShape.Of(Result.Parts["legs.json"])!;
        output.WriteLine($"leg: axis {tube.Length:F4} mm, long point {tube.LongPoint:F4}, short point {tube.ShortPoint:F4}, ends {tube.AngleA:F4}° and {tube.AngleB:F4}°, turn {tube.Turn:F2}°");

        // The axis from the foot's top (z = 12) to the top's underside (z = 588); each end cut by that plate's face.
        var lean = Math.Atan(110 * Math.Sqrt(2) / 576);
        (tube.Outside, tube.Wall).Should().Be((48.3, 3.2));
        tube.Length.Should().BeApproximately(LegLength, 1e-9);
        tube.AngleA.Should().BeApproximately(lean * 180 / Math.PI, 1e-9).And.BeApproximately(15.1, 0.05);
        tube.AngleB.Should().BeApproximately(tube.AngleA, 1e-9);
        tube.Turn.Should().BeApproximately(180, 1e-9, "the cuts are parallel");
        // Every line along the tube's wall is as long as the axis; the tips stand out r·tan θ at each end.
        tube.LongPoint.Should().BeApproximately(LegLength + 48.3 * Math.Tan(lean), 1e-9);
        tube.ShortPoint.Should().BeApproximately(LegLength - 48.3 * Math.Tan(lean), 1e-9);

        for (var k = 0; k < 4; k++)
        {
            var (sx, sy) = (k is 0 or 3 ? 1 : -1, k is 0 or 1 ? 1 : -1);
            var placement = Component($"legs-{k + 1}").Placement;
            (P(placement.Origin) - new Point3D(200 * sx, 200 * sy, 12)).Length.Should().BeLessThan(1e-9);
            (new Vector3D(placement.Normal[0], placement.Normal[1], placement.Normal[2]) - new Vector3D(-110 * sx, -110 * sy, 576) / LegLength).Length.Should().BeLessThan(1e-12);
        }
    }

    [Fact]
    public void The_bill_of_materials_and_the_cut_list()
    {
        var bom = Outputs.Bom(Result, Build.Material);
        var cuts = Outputs.CutList(Result, Build.Material);
        output.WriteLine(Outputs.Markdown(bom));
        output.WriteLine(Outputs.Markdown(cuts));

        bom.Select(r => (r.Part, r.Kind, r.Quantity, r.Description)).Should().Equal(
            ("top", "plate", 1, "Plate 12 S235 270 × 270"), ("feet", "plate", 4, "Plate 12 S235 160 × 160"), ("legs", "tube", 4, "Tube Ø48.3 × 3.2 S235 L 609.7"));
        bom[1].MassEach.Should().BeApproximately((160 * 160 - Math.PI * 81) * 12 * 7.85e-6, 1e-12);
        bom[2].MassEach.Should().BeApproximately(Math.PI * (24.15 * 24.15 - 20.95 * 20.95) * LegLength * 7.85e-6, 1e-12);
        bom[0].MassEach.Should().BeInRange(5.5, 6);
        cuts.Should().ContainSingle().Which.Should().Be(new CutListRow("legs", 4, 48.3, 3.2, "S235", cuts[0].LongPoint, cuts[0].ShortPoint, cuts[0].AngleA, cuts[0].AngleB, cuts[0].Turn));
        Outputs.Csv(cuts).Should().Be("part,quantity,size,material,long point,short point,end A,end B,turn\nlegs,4,Ø48.3 × 3.2,S235,609.7,583.6,15.1°,15.1°,180°\n");
        Outputs.Csv(bom).Split('\n')[^2].Should().StartWith("total,,9,,,");
    }

    [Fact]
    public void A_dxf_per_plate_with_its_outline()
    {
        var dxf = Outputs.Dxf(Result);

        dxf.Keys.Should().Equal("top.dxf", "feet.dxf");
        int Count(string text, string entity) => text.Split("\r\n0\r\n" + entity + "\r\n").Length - 1;
        var (top, foot) = (dxf["top.dxf"], dxf["feet.dxf"]);
        (Count(foot, "LINE"), Count(foot, "ARC"), Count(foot, "CIRCLE")).Should().Be((4, 0, 1));
        (Count(top, "LINE"), Count(top, "CIRCLE")).Should().Be((0, 5));
        Count(top, "ARC").Should().Be(Plate("top.json").Sketch.Entities.Count(e => e is SketchArc));
        foot.Should().Contain("\r\n40\r\n9\r\n", "the anchor hole's radius");
    }

    [Fact]
    public void The_same_inputs_give_the_same_bundle_byte_for_byte()
    {
        var again = Rationaliser.PlatesAndTubes().Run(Build, Design, stand.Design);

        CanonicalFormat.Write(again.Bundle).Should().Be(CanonicalFormat.Write(Result.Bundle));
        Outputs.Csv(Outputs.Bom(again, Build.Material)).Should().Be(Outputs.Csv(Outputs.Bom(Result, Build.Material)));
        Outputs.Dxf(again).Should().Equal(Outputs.Dxf(Result));
    }
}
