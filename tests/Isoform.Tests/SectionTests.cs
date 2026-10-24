using System.Diagnostics;
using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Geometry;
using Isoform.Modeling;
using Isoform.Sketching;
using Xunit.Abstractions;

namespace Isoform.Tests;

/// <summary>
/// S1–S4: plane sections of solids, their loops as exact lines and arcs or fitted tangent arcs, and the loops as a
/// sketch, against analytic answers.
/// </summary>
public class SectionTests(ITestOutputHelper output)
{
    static readonly ModelingSettings Settings = new();

    static CompiledPart Compile(params Feature[] features) => new Compiler(Settings).Compile(new PartDocument(features));

    static Point2D P(double x, double y) => new(x, y);

    static SketchPlane At(double x, double y, double z) => new([x, y, z], [1, 0, 0], [0, 0, 1]);

    // A circle of radius r round the plane's origin, as two half arcs.
    static Profile Circle(double r) => new([r, 0], [new ProfileEdge("a", [-r, 0], 1), new ProfileEdge("b", [r, 0], 1)]);

    static Extrude Disc(string id, BooleanOp op, double x, double y, double r, double z0, double z1, double blend = 0) =>
        new(id, op, At(x, y, 0), Circle(r), z0, z1, blend);

    // A sketched circle extruded: one face, id/rim.
    static Feature[] SketchedDisc(string id, BooleanOp op, double x, double y, double r, double z0, double z1, double blend = 0) =>
    [
        new Sketch($"{id}-sketch", new SketchPlacement(At(0, 0, 0)), new Dictionary<string, double[]> { ["c"] = [x, y] }, [new SketchCircle("rim", "c", r)], []),
        new Extrude(id, op, null, null, z0, z1, blend) { Sketch = $"{id}-sketch" },
    ];

    static Frame Horizontal(double z) => new(new Point3D(0, 0, z), new Vector3D(1, 0, 0), new Vector3D(0, 1, 0), new Vector3D(0, 0, 1));

    (Section Section, SectionOutline Outline) Cut(CompiledPart part, Frame plane, double cell = 1, double tolerance = 0.05)
    {
        var clock = Stopwatch.StartNew();
        var section = Sections.Of(part.Field!, part.Surfaces, plane, new Box3D(part.Min, part.Max), cell);
        var sliced = clock.Elapsed;
        var outline = SectionCurves.Of(section, part.Surfaces, tolerance);
        output.WriteLine($"section {sliced.TotalMilliseconds:F0} ms, curves {(clock.Elapsed - sliced).TotalMilliseconds:F0} ms; {section.Loops.Count} loops, {section.Diagnostics.Count} reports");
        foreach (var d in section.Diagnostics) output.WriteLine($"  {d.Code} {d.FeatureId}: {d.Message}");
        foreach (var loop in outline.Loops)
        {
            output.WriteLine($"loop ({(loop.Outer ? "outer" : "hole")}):");
            foreach (var e in loop.Edges)
                output.WriteLine($"  {e.Kind,-6} {e.Source,-24} ({e.Start.X:F6}, {e.Start.Y:F6}) -> ({e.End.X:F6}, {e.End.Y:F6}), {e.Segments.Count} segment(s), error {e.Error:E2}");
        }
        return (section, outline);
    }

    static void Closed(OutlineLoop loop)
    {
        for (var i = 0; i < loop.Edges.Count; i++)
        {
            var (e, next) = (loop.Edges[i], loop.Edges[(i + 1) % loop.Edges.Count]);
            (e.End - next.Start).Length.Should().BeLessThan(1e-12);
            for (var k = 1; k < e.Segments.Count; k++) (e.Segments[k].Start - e.Segments[k - 1].End).Length.Should().BeLessThan(1e-12);
        }
    }

    // ---- S1/S2: exact lines, arcs and corners ----

    // A 100 × 60 × 20 block with a Ø20 hole at (30, 30) and a Ø60 boss at (100, 30) whose sides run on tangentially.
    static CompiledPart Boss() => Compile(
        new Block("plate", BooleanOp.New, [0, 0, 0], [100, 60, 20]),
        new Hole("hole", [30, 30], 20),
        Disc("boss", BooleanOp.Join, 100, 30, 30, 0, 20));

    [Fact]
    public void A_block_and_cylinder_section_is_exact_lines_and_arcs_with_exact_corners()
    {
        var (section, outline) = Cut(Boss(), Horizontal(10));

        section.Diagnostics.Should().BeEmpty();
        outline.Loops.Should().HaveCount(2);
        outline.Regions.Should().ContainSingle().Which.Holes.Should().ContainSingle();
        var outer = outline.Loops.Single(l => l.Outer);
        Closed(outer);
        outer.Edges.Should().OnlyContain(e => e.Exact && e.Error == 0 && e.Segments.Count == 1);
        // From the bottom side round, counter-clockwise: the boss's two halves meet at its far point.
        var start = outer.Edges.ToList().FindIndex(e => e.Source == "plate/y-");
        var edges = outer.Edges.Skip(start).Concat(outer.Edges.Take(start)).ToList();
        edges.Select(e => (e.Source, e.Kind)).Should().Equal(
            ("plate/y-", SectionEdgeKind.Line), ("boss/b", SectionEdgeKind.Arc), ("boss/a", SectionEdgeKind.Arc),
            ("plate/y+", SectionEdgeKind.Line), ("plate/x-", SectionEdgeKind.Line));
        Point2D[] corners = [P(0, 0), P(100, 0), P(130, 30), P(100, 60), P(0, 60)];
        for (var i = 0; i < 5; i++) (edges[i].Start - corners[i]).Length.Should().BeLessThan(1e-9, edges[i].Source);
        foreach (var arc in edges.Where(e => e.Kind == SectionEdgeKind.Arc).Select(e => e.Segments[0]))
        {
            (arc.Center!.Value - P(100, 30)).Length.Should().BeLessThan(1e-12);
            arc.Radius.Should().BeApproximately(30, 1e-12);
            arc.Ccw.Should().BeTrue();
            arc.Sweep.Should().BeApproximately(Math.PI / 2, 1e-9);
        }

        var hole = outline.Loops.Single(l => !l.Outer).Edges.Should().ContainSingle().Subject;
        hole.Kind.Should().Be(SectionEdgeKind.Circle);
        hole.Source.Should().Be("hole/wall");
        var circle = hole.Segments[0];
        (circle.Center!.Value - P(30, 30)).Length.Should().BeLessThan(1e-12);
        circle.Radius.Should().BeApproximately(10, 1e-12);
        circle.Ccw.Should().BeFalse("a hole runs clockwise, the material on its left");
    }

    [Fact]
    public void A_hole_through_an_edge_leaves_a_notch_with_exact_transversal_corners()
    {
        var part = Compile(new Block("plate", BooleanOp.New, [0, 0, 0], [100, 60, 20]), new Hole("notch", [30, 0], 20));
        var (_, outline) = Cut(part, Horizontal(10));

        var loop = outline.Loops.Should().ContainSingle().Subject;
        Closed(loop);
        var arc = loop.Edges.Should().ContainSingle(e => e.Source == "notch/wall").Subject;
        arc.Kind.Should().Be(SectionEdgeKind.Arc);
        (arc.Start - P(20, 0)).Length.Should().BeLessThan(1e-9);
        (arc.End - P(40, 0)).Length.Should().BeLessThan(1e-9);
        arc.Segments[0].Ccw.Should().BeFalse();
        arc.Segments[0].Sweep.Should().BeApproximately(-Math.PI, 1e-9);
        loop.Edges.Where(e => e.Source == "plate/y-").Should().HaveCount(2).And.OnlyContain(e => e.Kind == SectionEdgeKind.Line);
    }

    [Fact]
    public void Loop_points_lie_on_the_boundary_and_carry_their_owner()
    {
        var part = Boss();
        var (section, _) = Cut(part, Horizontal(7.3), cell: 2);

        foreach (var p in section.Loops.SelectMany(l => l.Points))
        {
            var w = section.World(p.At);
            w.Z.Should().BeApproximately(7.3, 1e-12);
            Math.Abs(part.Field!.Value(w)).Should().BeLessThan(1e-12);
            part.Field.Sample(w).Owner.Should().Be(p.Owner);
        }
        section.Loops.Select(l => l.Area).Order().Should().Equal(new[] { -Math.PI * 100, 0 }, (a, b) => true);
        section.Loops.Single(l => l.Outer).Area.Should().BeApproximately(100 * 60 + Math.PI * 900 / 2 - 0, 2, "the boss adds half its disc");
    }

    [Fact]
    public void An_oblique_plane_cuts_a_cylinder_in_a_fitted_ellipse()
    {
        var part = Compile(Disc("tube", BooleanOp.New, 0, 0, 25, 0, 100));
        var tilt = 15 * Math.PI / 180;
        var normal = new Vector3D(0, -Math.Sin(tilt), Math.Cos(tilt));
        var plane = Frame.FromXAndNormal(new Point3D(0, 0, 50), new Vector3D(1, 0, 0), normal);

        var (_, outline) = Cut(part, plane);

        var edges = outline.Loops.Should().ContainSingle().Subject.Edges;
        Closed(outline.Loops[0]);
        edges.Should().OnlyContain(e => e.Kind == SectionEdgeKind.Fitted);
        var arcs = edges.SelectMany(e => e.Segments).ToList();
        var error = edges.Max(e => e.Error);
        output.WriteLine($"ellipse: {arcs.Count} arcs, measured error {error:E3}");
        error.Should().BeLessThanOrEqualTo(0.05);
        // Against the ellipse itself: x²/25² + y²/(25/cos)² = 1 in the plane, the distance about |F| / |∇F|.
        var (a, b) = (25.0, 25 / Math.Cos(tilt));
        double Off(Point2D p)
        {
            var f = p.X * p.X / (a * a) + p.Y * p.Y / (b * b) - 1;
            return Math.Abs(f) / Math.Sqrt(Math.Pow(2 * p.X / (a * a), 2) + Math.Pow(2 * p.Y / (b * b), 2));
        }
        arcs.SelectMany(s => Enumerable.Range(0, 33).Select(j => s.At(j / 32.0))).Max(Off).Should().BeLessThanOrEqualTo(0.0501);
        arcs.Count.Should().BeLessThanOrEqualTo(32);
    }

    // ---- S2/S3: blends ----

    // Discs Ø60 at the origin and Ø40 at (45, 0), joined with a blend k = 10.
    static CompiledPart Discs(double k = 10) => Compile(
        Disc("big", BooleanOp.New, 0, 0, 30, 0, 20),
        Disc("small", BooleanOp.Join, 45, 0, 20, 0, 20, k));

    [Fact]
    public void A_blended_pair_of_discs_is_exact_arcs_and_fitted_blends_that_end_where_the_blend_does()
    {
        var part = Discs();
        var (section, outline) = Cut(part, Horizontal(10));

        var loop = outline.Loops.Should().ContainSingle().Subject;
        Closed(loop);
        loop.Edges.Select(e => e.Source).Should().BeEquivalentTo(["big/a", "big/b", "small/blend", "small/a", "small/b", "small/blend"]);
        // The disc's arcs, exact; the small disc's halves meet at its far point.
        foreach (var e in loop.Edges.Where(e => e.Exact))
        {
            e.Kind.Should().Be(SectionEdgeKind.Arc);
            var arc = e.Segments[0];
            var (centre, r) = e.Source.StartsWith("big") ? (P(0, 0), 30.0) : (P(45, 0), 20.0);
            (arc.Center!.Value - centre).Length.Should().BeLessThan(1e-12);
            arc.Radius.Should().BeApproximately(r, 1e-12);
        }
        foreach (var e in loop.Edges.Where(e => !e.Exact))
        {
            // The blend ends on each disc's circle where the other disc's field is k: |f_a − f_b| = k.
            var (start, end) = (e.Start, e.End);
            foreach (var p in new[] { start, end })
            {
                var (fa, fb) = ((p - P(0, 0)).Length - 30, (p - P(45, 0)).Length - 20);
                Math.Min(Math.Abs(fa), Math.Abs(fb)).Should().BeLessThan(1e-12);
                Math.Max(fa, fb).Should().BeApproximately(10, 1e-9);
            }
            e.Error.Should().BeLessThanOrEqualTo(0.05);
            output.WriteLine($"blend: {e.Segments.Count} arcs, error {e.Error:E3}");
        }
        // Tangent all round: each edge leaves in the direction the one before arrives.
        for (var i = 0; i < loop.Edges.Count; i++)
        {
            var (e, next) = (loop.Edges[i], loop.Edges[(i + 1) % loop.Edges.Count]);
            (e.Segments[^1].Tangent(1) - next.Segments[0].Tangent(0)).Length.Should().BeLessThan(1e-9, $"{e.Source} into {next.Source}");
        }
    }

    [Fact]
    public void A_fitted_blend_stays_within_the_tolerance_of_the_true_curve()
    {
        var part = Discs(k: 15);
        foreach (var tolerance in new[] { 0.05, 0.01 })
        {
            var (section, outline) = Cut(part, Horizontal(10), tolerance: tolerance);
            foreach (var e in outline.Loops[0].Edges.Where(e => !e.Exact))
            {
                e.Error.Should().BeLessThanOrEqualTo(tolerance);
                // Independently: points along the arcs, onto the solid's boundary by bisection along the normal.
                foreach (var s in e.Segments)
                    for (var j = 0; j <= 32; j++)
                    {
                        var q = s.At(j / 32.0);
                        var n = section.Sample(q).Gradient.Normalized();
                        var (lo, hi) = (-1.0, 1.0);
                        for (var it = 0; it < 80; it++)
                        {
                            var mid = (lo + hi) / 2;
                            if (part.Field!.Value(section.World(q + n * mid)) < 0) lo = mid;
                            else hi = mid;
                        }
                        Math.Abs(lo).Should().BeLessThanOrEqualTo(tolerance * 1.001);
                    }
            }
        }
    }

    // ---- S1: below resolution ----

    [Fact]
    public void A_hole_closer_to_the_edge_than_a_cell_is_reported()
    {
        var part = Compile(new Block("plate", BooleanOp.New, [0, 0, 0], [100, 60, 20]), new Hole("hole", [30, 10.3], 20));
        var (section, _) = Cut(part, Horizontal(10));

        section.Diagnostics.Should().Contain(d => d.Code == Diagnostic.BelowResolution);
    }

    [Fact]
    public void A_hole_smaller_than_a_cell_is_reported()
    {
        var part = Compile(new Block("plate", BooleanOp.New, [0, 0, 0], [100, 60, 20]), new Hole("pin", [30.3, 20.4], 0.5));
        var (section, _) = Cut(part, Horizontal(10));

        section.Diagnostics.Should().Contain(d => d.Code == Diagnostic.BelowResolution && d.FeatureId == "pin");
    }

    // ---- The stand's top ----

    // A disc Ø260 and four lobes Ø90 at (±90, ±90) blended k = 20, extruded long and trimmed to 588–600 by plane cuts;
    // a Ø80 bore and four Ø11 holes at radius 100 on the axes.
    static CompiledPart Top() => Compile([
        .. SketchedDisc("disc", BooleanOp.New, 0, 0, 130, 560, 630),
        .. SketchedDisc("lobe", BooleanOp.Join, 90, 90, 45, 560, 630, 20),
        new Pattern("lobes", ["lobe"], new CircularMotion([0, 0, 0], [0, 0, 1], 4)),
        new PlaneCut("level", [0, 0, 600], [0, 0, 1]),
        new PlaneCut("under", [0, 0, 588], [0, 0, -1]),
        new Hole("bore", [0, 0], 80),
        new Hole("mount", [100, 0], 11),
        new Pattern("mounts", ["mount"], new CircularMotion([0, 0, 0], [0, 0, 1], 4))]);

    [Fact]
    public void The_stand_top_is_eight_exact_arcs_eight_fitted_blends_and_five_exact_holes()
    {
        var part = Top();
        part.Diagnostics.Should().BeEmpty();
        var (section, outline) = Cut(part, Horizontal(594));

        section.Diagnostics.Should().BeEmpty();
        var region = outline.Regions.Should().ContainSingle().Subject;
        region.Holes.Should().HaveCount(5);
        var outer = outline.Loops[region.Outer];
        Closed(outer);
        var exact = outer.Edges.Where(e => e.Exact).ToList();
        var fitted = outer.Edges.Where(e => !e.Exact).ToList();
        exact.Should().HaveCount(8).And.OnlyContain(e => e.Kind == SectionEdgeKind.Arc);
        fitted.Should().HaveCount(8).And.OnlyContain(e => e.Source.EndsWith("lobe/blend"));
        fitted.Max(e => e.Error).Should().BeLessThanOrEqualTo(0.05);
        output.WriteLine($"blends: {fitted.Sum(e => e.Segments.Count)} arcs in all, {fitted.Min(e => e.Segments.Count)}–{fitted.Max(e => e.Segments.Count)} each, largest error {fitted.Max(e => e.Error):E3}");
        foreach (var e in exact)
        {
            var arc = e.Segments[0];
            var lobe = arc.Radius < 100;
            arc.Radius.Should().BeApproximately(lobe ? 45 : 130, 1e-12);
            var c = arc.Center!.Value;
            (lobe ? Math.Abs(Math.Abs(c.X) - 90) + Math.Abs(Math.Abs(c.Y) - 90) : Math.Abs(c.X) + Math.Abs(c.Y)).Should().BeLessThan(1e-12);
        }
        foreach (var i in region.Holes)
        {
            var hole = outline.Loops[i].Edges.Should().ContainSingle().Subject;
            hole.Kind.Should().Be(SectionEdgeKind.Circle);
            hole.Segments[0].Radius.Should().BeApproximately(hole.Source == "bore/wall" ? 40 : 5.5, 1e-12);
        }
    }

    // ---- S4: sketch ----

    [Fact]
    public void A_section_becomes_a_sketch_that_extrudes_back_into_the_plate()
    {
        var part = Boss();
        var (section, outline) = Cut(part, Horizontal(10));

        var sketch = SectionSketch.Of("outline", At(0, 0, 0), outline);

        SketchSolver.Problem(sketch).Should().BeNull();
        sketch.Entities.Select(e => e.Id).Should().BeEquivalentTo(["boss-a~1", "boss-b~1", "hole-wall~1", "plate-x-~1", "plate-y+~1", "plate-y-~1"]);
        var solution = SketchSolver.Solve(sketch);
        var (regions, problem) = SketchRegions.Of(sketch, solution);
        problem.Should().BeNull();
        regions.Should().ContainSingle().Which.Holes.Should().ContainSingle();

        // The plate again, extruded from the sketch: the same solid in the section's plane, the same boundary.
        var plate = Compile(sketch, new Extrude("again", BooleanOp.New, null, null, 0, 20) { Sketch = "outline" });
        plate.Diagnostics.Should().BeEmpty();
        var rng = new Random(5);
        for (var i = 0; i < 2000; i++)
        {
            var p = new Point3D(rng.NextDouble() * 140 - 5, rng.NextDouble() * 70 - 5, 10);
            if (Math.Abs(part.Field!.Value(p)) > 1e-9) (plate.Field!.Value(p) < 0).Should().Be(part.Field.Value(p) < 0);
        }
        foreach (var p in section.Loops.SelectMany(l => l.Points)) Math.Abs(plate.Field!.Value(section.World(p.At))).Should().BeLessThan(1e-9);
        plate.Surfaces.Select(s => s.Source).Should().Contain(["again/boss-a~1", "again/hole-wall~1", "again/plate-y-~1"]);
    }

    [Fact]
    public void The_stand_tops_sketch_names_every_entity_after_its_design_face()
    {
        var part = Top();
        var (_, outline) = Cut(part, Horizontal(594));

        var sketch = SectionSketch.Of("outline", At(0, 0, 0), outline);

        SketchSolver.Problem(sketch).Should().BeNull();
        var clock = Stopwatch.StartNew();
        var solved = SketchSolver.Solve(sketch);
        output.WriteLine($"solve {clock.ElapsedMilliseconds} ms, {sketch.Points.Count} points, {sketch.Entities.Count} entities");
        clock.Restart();
        var (regions, problem) = SketchRegions.Of(sketch, solved);
        output.WriteLine($"regions {clock.ElapsedMilliseconds} ms");
        problem.Should().BeNull();
        regions.Should().ContainSingle().Which.Holes.Should().HaveCount(5);
        sketch.Entities.OfType<SketchCircle>().Select(c => c.Id).Should().BeEquivalentTo(
            ["bore-wall~1", "mount-wall~1", "mounts-1:mount-wall~1", "mounts-2:mount-wall~1", "mounts-3:mount-wall~1"], o => o.WithoutStrictOrdering());
        sketch.Entities.Should().Contain(e => e.Id == "lobes-1:lobe-blend~1");
        output.WriteLine(string.Join(", ", sketch.Entities.Select(e => e.Id)));

        clock.Restart();
        var plate = Compile(sketch, new Extrude("top", BooleanOp.New, null, null, 0, 12) { Sketch = "outline" });
        output.WriteLine($"compile {clock.ElapsedMilliseconds} ms");
        plate.Diagnostics.Should().BeEmpty();
    }
}
