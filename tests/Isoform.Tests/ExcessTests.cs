using System.Diagnostics;
using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Geometry;
using Isoform.Modeling;
using Xunit.Abstractions;

namespace Isoform.Tests;

/// <summary>
/// V1: how far parts stray outside a design and how much of the design they leave uncovered, certified, measured
/// against analytic answers.
/// </summary>
public class ExcessTests(ITestOutputHelper output)
{
    static readonly SketchPlane Xy = new([0, 0, 0], [1, 0, 0], [0, 0, 1]);
    static readonly SketchPlane Xz = new([0, 0, 0], [1, 0, 0], [0, -1, 0]);

    static CompiledPart Compile(params Feature[] features) => new Compiler(new ModelingSettings()).Compile(new PartDocument(features));

    static Profile Circle(double r) => new([r, 0], [new ProfileEdge("a", [-r, 0], 1), new ProfileEdge("b", [r, 0], 1)]);

    static Revolve Ball(string id, double r, double[] centre) =>
        new(id, BooleanOp.New, Xz with { Origin = centre }, new Profile([0, -r], [new ProfileEdge("arc", [0, r], 1), new ProfileEdge("axis", [0, -r], 0)]));

    void Report(string what, ExcessResult e, double expected) =>
        output.WriteLine($"{what}: [{e.Lower:F9}, {e.Upper:F9}] vs {expected:F9}, settled {e.Settled}");

    static void Brackets(ExcessResult e, double expected, double tolerance)
    {
        e.Settled.Should().BeTrue();
        e.Lower.Should().BeLessThanOrEqualTo(expected + 1e-9);
        e.Upper.Should().BeGreaterThanOrEqualTo(expected - 1e-9);
        (e.Upper - e.Lower).Should().BeLessThanOrEqualTo(tolerance);
    }

    [Fact]
    public void A_rod_inside_a_larger_rod_is_inside_and_leaves_the_difference_of_radii_uncovered()
    {
        var design = Compile(new Extrude("leg", BooleanOp.New, Xy, Circle(25), 0, 600));
        var parts = Compile(new Extrude("tube", BooleanOp.New, Xy, Circle(24.15), 0, 600));

        var outside = Queries.Excess(design, parts, 1e-6);
        Report("outside", outside, 0);
        Brackets(outside, 0, 1e-6); // the ends are flush

        var sw = Stopwatch.StartNew();
        var uncovered = Queries.ExcessByFace(parts, design, 1e-6);
        output.WriteLine($"{sw.ElapsedMilliseconds} ms");
        foreach (var (face, e) in uncovered) Report(face, e, 0.85);
        uncovered.Select(u => u.Face).Should().BeEquivalentTo("leg/a", "leg/b", "leg/start", "leg/end");
        foreach (var (face, e) in uncovered)
        {
            Brackets(e, 0.85, 1e-6); // on the side everywhere, on the ends at the rim
            design.Surfaces[e.RegionOwner].Source.Should().Be(face);
        }
    }

    [Fact]
    public void A_box_poking_out_of_another_strays_by_the_overhang()
    {
        var design = Compile(new Block("d", BooleanOp.New, [0, 0, 0], [10, 10, 10]));
        var parts = Compile(new Block("p", BooleanOp.New, [2, 3, 2], [6, 4, 9])); // z 2 to 11

        var outside = Queries.Excess(design, parts, 1e-9);
        Report("outside", outside, 1);
        Brackets(outside, 1, 1e-9);
        outside.At.Z.Should().BeApproximately(11, 1e-9);
        design.Surfaces[outside.FieldOwner].Source.Should().Be("d/z+");
        parts.Surfaces[outside.RegionOwner].Source.Should().Be("p/z+");

        var uncovered = Queries.ExcessByFace(parts, design, 1e-9).ToDictionary(u => u.Face, u => u.Excess);
        uncovered.Keys.Should().BeEquivalentTo("d/x-", "d/x+", "d/y-", "d/y+", "d/z-", "d/z+");
        foreach (var (face, e) in uncovered)
        {
            Report(face, e, 3);
            Brackets(e, 3, 1e-9); // every corner of d is 3 out from p's sides in y
        }
    }

    [Fact]
    public void An_offset_ball_inside_a_ball()
    {
        var design = Compile(Ball("d", 10, [0, 0, 0]));
        var parts = Compile(Ball("p", 5, [1, 0, 0]));

        var outside = Queries.Excess(design, parts, 1e-6);
        Report("outside", outside, -4);
        Brackets(outside, -4, 1e-6); // 1 + 5 − 10, at (6, 0, 0)
        (outside.At - new Point3D(6, 0, 0)).Length.Should().BeLessThan(1e-2);

        // A round peak: the box bounds close on it to first order only, so this asks for 1e-4.
        var sw = Stopwatch.StartNew();
        var uncovered = Queries.ExcessByFace(parts, design, 1e-4).Single();
        Report(uncovered.Face, uncovered.Excess, 6);
        output.WriteLine($"{sw.ElapsedMilliseconds} ms");
        uncovered.Face.Should().Be("d/arc");
        Brackets(uncovered.Excess, 6, 1e-4); // 10 + 1 − 5, at (−10, 0, 0)
        (uncovered.Excess.At - new Point3D(-10, 0, 0)).Length.Should().BeLessThan(0.2, "within 1e-4 of a round peak's value is ~0.1 from it");
    }

    [Fact]
    public void A_tilted_plate_in_a_slab_pokes_out_by_its_top_edge()
    {
        // A 100 × 50 × 10 plate centred at z = 10, turned 15° about x: its top edge rises 25 sin θ + 5 cos θ.
        var theta = 15 * Math.PI / 180;
        var (c, s) = (Math.Cos(theta), Math.Sin(theta));
        var plane = new SketchPlane([0, 5 * s, 10 - 5 * c], [1, 0, 0], [0, -s, c]);
        var rectangle = new Profile([-50, -25], [new ProfileEdge("s", [50, -25]), new ProfileEdge("e", [50, 25]), new ProfileEdge("n", [-50, 25]), new ProfileEdge("w", [-50, -25])]);
        var parts = Compile(new Extrude("plate", BooleanOp.New, plane, rectangle, 0, 10));
        var design = Compile(new Block("slab", BooleanOp.New, [-100, -100, 0], [200, 200, 20]));

        var expected = 25 * s + 5 * c - 10;
        var outside = Queries.Excess(design, parts, 1e-9);
        Report("outside", outside, expected);
        Brackets(outside, expected, 1e-9);

        // The slab's top is farthest from the plate at one of its corners: the plate's field is convex.
        var uncovered = Queries.ExcessByFace(parts, design, 1e-6).Single(u => u.Face == "slab/z+").Excess;
        var corner = new[] { -100.0, 100 }.SelectMany(x => new[] { -100.0, 100 }.Select(y => parts.Field!.Value(new Point3D(x, y, 20)))).Max();
        Report("slab/z+", uncovered, corner);
        Brackets(uncovered, corner, 1e-6);
    }

    [Fact]
    public void When_the_budget_runs_out_the_interval_stays_honest_and_says_so()
    {
        var design = Compile(Ball("d", 10, [0, 0, 0]));
        var parts = Compile(Ball("p", 5, [1, 0, 0]));
        var box = new Box3D(new Point3D(-11, -11, -11), new Point3D(11, 11, 11));

        var e = Queries.BoundaryExcess(parts.Field!, design.Field!, box, design.Surfaces.Select(x => x.Id).ToHashSet(), 1e-12, budget: 30);

        Report("budget 30", e, 6);
        e.Settled.Should().BeFalse();
        e.Lower.Should().BeLessThanOrEqualTo(6 + 1e-12);
        e.Upper.Should().BeGreaterThanOrEqualTo(6);
        double.IsInfinity(e.Upper).Should().BeFalse();
    }

    [Fact]
    public void A_stand_leg_rationalised_as_a_tube()
    {
        // The pump stand's leg: Ø50 from (200, 200, 12) to (90, 90, 588), run 60 mm past both ends, blended (k = 10)
        // into a foot and a top plate, then cut at z = 0 and z = 600.
        var (from, to) = (new Point3D(200, 200, 12), new Point3D(90, 90, 588));
        var axis = (to - from).Normalized();
        var length = (to - from).Length;
        var start = from - axis * 60;
        var x = new Vector3D(1, 0, 0).Cross(axis).Normalized();
        var legPlane = new SketchPlane([start.X, start.Y, start.Z], [x.X, x.Y, x.Z], [axis.X, axis.Y, axis.Z]);
        var design = Compile(
            new Block("foot", BooleanOp.New, [150, 150, 0], [160, 160, 12]),
            new Block("top", BooleanOp.Join, [-130, -130, 588], [260, 260, 40]),
            new Extrude("leg", BooleanOp.Join, legPlane, Circle(25), 0, length + 120, Blend: 10),
            new PlaneCut("floor", [0, 0, 0], [0, 0, -1]),
            new PlaneCut("cap", [0, 0, 600], [0, 0, 1]));
        design.Diagnostics.Should().BeEmpty();

        // The part: a tube Ø48.3 × 3.2 along z in its own frame, cut by the foot's top and the top's underside, placed.
        var frame = Frame.FromXAndNormal(from, x, axis);
        double[] Local(Point3D p) => [frame.ToLocal(p).X, frame.ToLocal(p).Y, frame.ToLocal(p).Z];
        double[] Dir(Vector3D v) => [frame.ToLocal(v).X, frame.ToLocal(v).Y, frame.ToLocal(v).Z];
        var tube = Compile(
            new Extrude("tube", BooleanOp.New, Xy with { Origin = [0, 0, -40] }, Circle(24.15), 0, length + 80),
            new Extrude("bore", BooleanOp.Cut, Xy with { Origin = [0, 0, -50] }, Circle(24.15 - 3.2), 0, length + 100),
            new PlaneCut("low", Local(new Point3D(0, 0, 12)), Dir(new Vector3D(0, 0, -1))),
            new PlaneCut("high", Local(new Point3D(0, 0, 588)), Dir(new Vector3D(0, 0, 1))));
        tube.Diagnostics.Should().BeEmpty();
        var placed = new PlacedField(tube.Field!, frame);
        var tubeBox = new Box3D(new Point3D(60, 60, 11), new Point3D(230, 230, 589));
        var designBox = new Box3D(new Point3D(-131, -131, -1), new Point3D(311, 311, 601));

        var sw = Stopwatch.StartNew();
        var outside = Queries.Excess(design.Field!, placed, tubeBox);
        output.WriteLine($"outside: [{outside.Lower:F6}, {outside.Upper:F6}] at {outside.At}, design {design.Surfaces[outside.FieldOwner].Source}, " +
            $"part {tube.Surfaces[outside.RegionOwner].Source}, settled {outside.Settled}, {sw.ElapsedMilliseconds} ms");
        Brackets(outside, -0.85, 1e-3); // the tube lies 0.85 inside the leg's surface, everywhere along it

        sw.Restart();
        var uncovered = Queries.ExcessByFace(placed, design.Field!, designBox, design.Surfaces);
        output.WriteLine($"uncovered, {uncovered.Count} faces: {sw.ElapsedMilliseconds} ms");
        foreach (var (face, e) in uncovered)
            output.WriteLine($"  {face}: [{e.Lower:F6}, {e.Upper:F6}] part {tube.Surfaces[e.FieldOwner].Source}, settled {e.Settled}");
        foreach (var side in new[] { "leg/a", "leg/b" })
            Brackets(uncovered.Single(u => u.Face == side).Excess, 0.85, 1e-3);
        uncovered.Should().OnlyContain(u => u.Excess.Settled);
    }
}
