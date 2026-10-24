using System.Diagnostics;
using BasAutomation.Geometry;
using Isoform.Build;
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

    // ---- Plates from sketches: holes in the profile, fitted outlines ----

    static Sketch Outline(double r) => new("outline", new SketchPlacement(Xy),
        new Dictionary<string, double[]> { ["a"] = [0, 0], ["b"] = [100, 0], ["c"] = [100, 60], ["d"] = [0, 60], ["o"] = [30, 30] },
        [new SketchLine("s", "a", "b"), new SketchLine("e", "b", "c"), new SketchLine("n", "c", "d"), new SketchLine("w", "d", "a"), new SketchCircle("bore", "o", r)], []);

    static CompiledPart Sketched(double r) => Compile(Outline(r), new Extrude("plate", BooleanOp.New, null, null, 0, 10) { Sketch = "outline" });

    static Box3D Around(Point3D min, Point3D max, Frame? frame = null)
    {
        var f = frame ?? Frame.Identity;
        var corners = (from x in new[] { min.X - 1, max.X + 1 } from y in new[] { min.Y - 1, max.Y + 1 } from z in new[] { min.Z - 1, max.Z + 1 }
                       select f.ToWorld(new Point3D(x, y, z))).ToList();
        return new(new(corners.Min(p => p.X), corners.Min(p => p.Y), corners.Min(p => p.Z)), new(corners.Max(p => p.X), corners.Max(p => p.Y), corners.Max(p => p.Z)));
    }

    [Fact]
    public void A_sketched_plate_holds_its_round_hole_as_the_hole_features_cylinder()
    {
        // The design: a 100 × 60 × 10 block with a Ø20 hole. The parts: the same plate sketched, its hole a circle of the
        // profile (a hole in the region, not a hole feature), and one with a Ø24 hole.
        var design = Compile(new Block("d", BooleanOp.New, [0, 0, 0], [100, 60, 10]), new Hole("bore", [30, 30], 20));
        var (plate, wide) = (Sketched(10), Sketched(12));
        var sw = Stopwatch.StartNew();

        // Flush everywhere, the hole's wall on the design's: exactly 0, in a few boxes.
        var outside = Queries.Excess(design.Field!, plate.Field!, Around(plate.Min, plate.Max), 1e-9, budget: 2_000);
        Report("outside", outside, 0);
        Brackets(outside, 0, 1e-9);

        // The wider hole leaves the design's wall 2 from the plate's all round.
        var wall = design.Surfaces.Where(s => s.Source == "bore/wall").Select(s => s.Id).ToHashSet();
        var uncovered = Queries.BoundaryExcess(wide.Field!, design.Field!, Around(design.Min, design.Max), wall, 1e-9, budget: 2_000);
        Report("bore/wall", uncovered, 2);
        Brackets(uncovered, 2, 1e-9);

        // A Ø16 pin through the hole is 2 from its wall everywhere: a pin in a coaxial bore, exact at once.
        var pin = Compile(new Extrude("pin", BooleanOp.New, Xy with { Origin = [30, 30, -20] }, Circle(8), 0, 50));
        var clearance = Queries.Clearance(plate.Field!, Around(plate.Min, plate.Max), pin.Field!, Around(pin.Min, pin.Max), 1e-9, budget: 2_000);
        output.WriteLine($"pin: [{clearance.Lower:F12}, {clearance.Upper:F12}], all three {sw.ElapsedMilliseconds} ms");
        clearance.Lower.Should().BeApproximately(2, 1e-9);
        clearance.Upper.Should().BeApproximately(2, 1e-9);
    }

    // A disc and a lobe blended (k = 10), with a bore, cut 10 thick: the pump stand's top at half size.
    static readonly PartDocument Lobed = new(
    [
        new Extrude("disc", BooleanOp.New, Xy with { Origin = [0, 0, -5] }, Circle(65), 0, 20),
        new Extrude("lobe", BooleanOp.Join, Xy with { Origin = [45, 45, -5] }, Circle(22.5), 0, 20, Blend: 10),
        new PlaneCut("under", [0, 0, 0], [0, 0, -1]),
        new PlaneCut("cap", [0, 0, 10], [0, 0, 1]),
        new Hole("bore", [0, 0], 20),
    ]);

    /// <summary>The lobed design and the plate the plate method makes of it, placed: the disc's and lobe's arcs and the bore copied, the blend fitted.</summary>
    static (FrepResult Design, CompiledPart Plate, Frame Frame, BuildInstance Instance) LobedPlate()
    {
        var design = new FrepModeler(new ModelingSettings { CellSize = 1 }).Regenerate(Lobed);
        var built = Rationaliser.PlatesAndTubes().Run(new BuildDocument("design.json", [new PlateSection("plate", 10, Face: "cap/face")]), Lobed, design);
        var instance = built.Instances.Single();
        return (design, new Compiler(new ModelingSettings()).Compile(instance.Built.Document), AssemblyModeler.FrameOf(instance.Built.Placement), instance);
    }

    static IEnumerable<Point2D> Along(OutlineEdge e, int n) => e.Kind == OutlineEdgeKind.Line
        ? Enumerable.Range(0, n + 1).Select(i => new Point2D(e.From.X + (e.To.X - e.From.X) * i / n, e.From.Y + (e.To.Y - e.From.Y) * i / n))
        : Enumerable.Range(0, n + 1).Select(i => Math.Atan2(e.From.Y - e.Centre.Y, e.From.X - e.Centre.X) + e.Sweep * i / n)
            .Select(a => new Point2D(e.Centre.X + e.Radius * Math.Cos(a), e.Centre.Y + e.Radius * Math.Sin(a)));

    [Fact]
    public void A_plate_with_a_fitted_outline_strays_from_the_blend_by_its_fit()
    {
        var (design, part, frame, instance) = LobedPlate();
        var fits = instance.Built.Fits;
        fits.Should().NotBeEmpty().And.OnlyContain(f => f.Source == "lobe/blend");
        var shape = PlateShape.Of(instance.Built.Document)!;
        var plate = new PlacedField(part.Field!, frame);

        // The answer from the analytic outline: the design's field along every edge of the plate, densely; the slab does
        // not change through its thickness. The fitted arcs stand a few hundredths out of the blend, within the fit.
        var field = design.Part.Field!;
        var sampled = shape.Edges.SelectMany(e => Along(e, 4000)).Max(p => field.Value(frame.ToWorld(new Point3D(p.X, p.Y, 5))));
        sampled.Should().BePositive().And.BeLessThanOrEqualTo(fits.Max(f => f.Error) + 1e-9);

        var sw = Stopwatch.StartNew();
        var outside = Queries.Excess(field, plate, Around(part.Min, part.Max, frame), 1e-3, budget: 20_000);
        output.WriteLine($"outside: [{outside.Lower:F6}, {outside.Upper:F6}] vs {sampled:F6} sampled, fit {fits.Max(f => f.Error):F6}, " +
            $"settled {outside.Settled}, {sw.ElapsedMilliseconds} ms");
        outside.Settled.Should().BeTrue();
        outside.Upper.Should().BeGreaterThanOrEqualTo(sampled - 1e-12);
        outside.Lower.Should().BeApproximately(sampled, 1e-3);
        design.Part.Surfaces[outside.FieldOwner].Source.Should().Be("lobe/blend");

        // And the other way: the design's blend face is at most a few hundredths from the plate, settled too.
        sw.Restart();
        var blend = design.Part.Surfaces.Where(s => s.Source == "lobe/blend").Select(s => s.Id).ToHashSet();
        var uncovered = Queries.BoundaryExcess(plate, field, Around(design.Part.Min, design.Part.Max), blend, 1e-3, budget: 20_000);
        output.WriteLine($"lobe/blend uncovered: [{uncovered.Lower:F6}, {uncovered.Upper:F6}], settled {uncovered.Settled}, {sw.ElapsedMilliseconds} ms");
        uncovered.Settled.Should().BeTrue();
        uncovered.Upper.Should().BeLessThanOrEqualTo(fits.Max(f => f.Error) + 1e-3);
    }

    [Fact]
    public void A_box_bound_is_never_below_the_field_at_a_point_of_the_region()
    {
        // K1 for the bounds the excess searches put on a box (Queries.RegionHigh): holds cut into polytopes (chords, tangent
        // planes, blend slabs), convex and concave fields at the polytope's corners, a blend's tangent plane, two convex
        // parts' union. Two pairs: the lobed design and its fitted plate (with holes); a block with a leaning rod blended
        // in, against a smaller block and a thinner rod unioned; and a sloping plane over each region.
        var (lobed, part, frame, _) = LobedPlate();
        var plate = new PlacedField(part.Field!, frame);
        var theta = 15 * Math.PI / 180;
        var leaning = Xy with { Origin = [50, 30, -10], X = [1, 0, 0], Normal = [0, Math.Sin(theta), Math.Cos(theta)] };
        var post = new FrepModeler(new ModelingSettings { CellSize = 1 }).Regenerate(new PartDocument(
        [
            new Block("base", BooleanOp.New, [0, 0, 0], [100, 60, 10]),
            new Extrude("rod", BooleanOp.Join, leaning, Circle(8), 0, 60, Blend: 5),
            new PlaneCut("floor", [0, 0, 0], [0, 0, -1]),
        ]));
        var parts = new MinField([Compile(new Block("b", BooleanOp.New, [0.5, 0.5, 0], [99, 59, 10])).Field!,
            Compile(new Extrude("r", BooleanOp.New, leaning, Circle(7.5), 0, 60)).Field!]);
        var slope = new LeafField(new Surface(-1, "", SurfaceKind.Plane, new Point3D(0, 0, 0), new Vector3D(0.6, 0.48, 0.64)));
        IReadOnlySet<int> Faces(FrepResult r, params string[] names) => r.Part.Surfaces.Where(s => names.Contains(s.Source)).Select(s => s.Id).ToHashSet();
        (Field Field, Field Solid, IReadOnlySet<int>? Faces, Point3D Min, Point3D Max)[] cases =
        [
            (lobed.Part.Field!, plate, null, lobed.Part.Min, lobed.Part.Max),
            (plate, lobed.Part.Field!, Faces(lobed, "lobe/blend", "bore/wall", "disc/a", "disc/b", "cap/face"), lobed.Part.Min, lobed.Part.Max),
            (post.Part.Field!, parts, null, post.Part.Min, post.Part.Max),
            (parts, post.Part.Field!, Faces(post, "rod/blend", "rod/a", "base/z+"), post.Part.Min, post.Part.Max),
            // A sloping plane rises across the shapes the region is cut to: a cut that left out some of the region shows.
            (slope, plate, null, lobed.Part.Min, lobed.Part.Max),
            (slope, plate, null, new Point3D(-12, -12, 1), new Point3D(12, 12, 9)), // round the bore, outside its wall
            (slope, lobed.Part.Field!, Faces(lobed, "lobe/blend", "bore/wall", "disc/a"), lobed.Part.Min, lobed.Part.Max),
            (slope, post.Part.Field!, Faces(post, "rod/blend"), post.Part.Min, post.Part.Max),
        ];
        var rng = new Random(5);
        var (checkedPoints, nulls) = (0, 0);
        for (var k = 0; k < cases.Length; k++)
            for (var b = 0; b < 600; b++)
            {
                var (field, solid, faces, min, max) = cases[k];
                // A box round a point of the solid's boundary, 0.01 to 5 mm and up to 8 to 1 in its sides.
                var start = new Point3D(min.X + (max.X - min.X) * rng.NextDouble(), min.Y + (max.Y - min.Y) * rng.NextDouble(), min.Z + (max.Z - min.Z) * rng.NextDouble());
                if (Project(solid, start) is not { } centre) continue;
                var size = Math.Pow(10, rng.NextDouble() * 2.7 - 2);
                var half = new Vector3D(size * (0.125 + rng.NextDouble()), size * (0.125 + rng.NextDouble()), size * (0.125 + rng.NextDouble())) / 2;
                var box = new Box3D(centre - half, centre + half);
                var bound = Queries.RegionHigh(field, solid, box, faces);
                if (bound is null) nulls++;
                for (var i = 0; i < 200; i++)
                {
                    var p = new Point3D(box.Min.X + (box.Max.X - box.Min.X) * rng.NextDouble(), box.Min.Y + (box.Max.Y - box.Min.Y) * rng.NextDouble(),
                        box.Min.Z + (box.Max.Z - box.Min.Z) * rng.NextDouble());
                    if (faces is not null)
                    {
                        // A boundary point one of the faces owns, inside the box.
                        if (Project(solid, p) is not { } q || q.X < box.Min.X || q.X > box.Max.X || q.Y < box.Min.Y || q.Y > box.Max.Y || q.Z < box.Min.Z || q.Z > box.Max.Z
                            || !faces.Contains(solid.Sample(q).Owner)) continue;
                        p = q;
                    }
                    else if (solid.Value(p) > 0) continue;
                    bound.Should().NotBeNull("the box holds a point of the region");
                    field.Value(p).Should().BeLessThanOrEqualTo(bound!.Value + 1e-9, $"case {k}, box {box.Min} to {box.Max}, at {p}");
                    checkedPoints++;
                }
            }
        output.WriteLine($"{checkedPoints} points checked, {nulls} boxes ruled out");
        checkedPoints.Should().BeGreaterThan(50_000);
    }

    [Fact]
    public void Outside_a_cylinder_a_box_is_cut_at_the_chord_its_width_allows()
    {
        // The solid outside a Ø20 hole along z; the field −x, highest where the solid comes nearest the axis. In the box
        // x 9.5 to 10.5, y ±2.5 that is at the box's sides, x = √(10² − 2.5²): exactly where the chord cut lies.
        var outside = new LeafField(new Surface(0, "hole", SurfaceKind.Cylinder, new Point3D(0, 0, 0), new Vector3D(0, 0, 1), 10), complement: true);
        var minusX = new LeafField(new Surface(1, "x", SurfaceKind.Plane, new Point3D(0, 0, 0), new Vector3D(-1, 0, 0)));
        var box = new Box3D(new Point3D(9.5, -2.5, 0), new Point3D(10.5, 2.5, 1));

        var high = Queries.RegionHigh(minusX, outside, box);

        high.Should().BeApproximately(-Math.Sqrt(100 - 2.5 * 2.5), 1e-9);
    }

    /// <summary>Newton onto the solid's boundary, to 1e-12; null if it does not get there.</summary>
    static Point3D? Project(Field solid, Point3D p)
    {
        for (var i = 0; i < 30; i++)
        {
            var s = solid.Sample(p);
            if (Math.Abs(s.Value) < 1e-12) return p;
            if (s.Gradient.LengthSquared == 0) return null;
            p -= s.Gradient * (s.Value / s.Gradient.LengthSquared);
        }
        return null;
    }
}
