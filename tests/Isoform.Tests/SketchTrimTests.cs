using Isoform.Document;
using Isoform.Modeling;
using Isoform.Sketching;

namespace Isoform.Tests;

/// <summary>
/// Sketch intersections and trim: closed-form hits in order along an entity, tangent contacts once, arcs held to their
/// sweep; trim splits at the hits, deletes the picked piece, keeps ids, lists what it drops, and leaves a sketch that
/// solves to the same geometry with every constraint holding.
/// </summary>
public class SketchTrimTests
{
    static readonly SketchPlacement Xy = new(new SketchPlane([0, 0, 0], [1, 0, 0], [0, 0, 1]));

    static Sketch Of(Dictionary<string, double[]> points, SketchEntity[] entities, params SketchConstraint[] constraints) =>
        new("s", Xy, points, entities, constraints);

    static SketchConstraint C(string kind, params string[] refs) => new(kind, refs);

    static SketchConstraint D(string kind, double value, params string[] refs) => new(kind, refs, value);

    static IEnumerable<(double X, double Y, string Other)> Hits(Sketch s, string entity) =>
        SketchIntersections.Of(s, entity).Select(h => (Math.Round(h.X, 9), Math.Round(h.Y, 9), h.Other));

    static T Entity<T>(SketchEdit edit, string id) where T : SketchEntity => edit.Sketch.Entities.Single(e => e.Id == id).Should().BeOfType<T>().Subject;

    static (double X, double Y) At(SketchEdit edit, string point) => (edit.Sketch.Points[point][0], edit.Sketch.Points[point][1]);

    /// <summary>The trimmed sketch solves where it is, every constraint to 1e-10, and no point it kept moved.</summary>
    static SketchEdit Holds(SketchEdit edit, Sketch before)
    {
        var s = SketchSolver.Solve(edit.Sketch);
        s.Solved.Should().BeTrue(string.Join("; ", s.Problems.Select(p => p.Message)));
        s.Residual.Should().BeLessThan(1e-10);
        foreach (var (name, p) in before.Points.Where(p => edit.Sketch.Points.ContainsKey(p.Key)))
        {
            edit.Sketch.Points[name][0].Should().BeApproximately(p[0], 1e-9, name);
            edit.Sketch.Points[name][1].Should().BeApproximately(p[1], 1e-9, name);
        }
        return edit;
    }

    // ---- Intersections ----

    [Fact]
    public void Crossing_lines_meet_once_and_lines_that_would_cross_beyond_their_ends_do_not()
    {
        var s = Of(new() { ["a"] = [0, 0], ["b"] = [10, 10], ["c"] = [0, 10], ["d"] = [10, 0], ["e"] = [20, 0], ["f"] = [30, -10], ["g"] = [0, 1], ["h"] = [10, 11] },
            [new SketchLine("ab", "a", "b"), new SketchLine("cd", "c", "d"), new SketchLine("ef", "e", "f"), new SketchLine("gh", "g", "h")]);

        Hits(s, "ab").Should().Equal((5, 5, "cd"));
        SketchIntersections.Of(s, "ab")[0].At.Should().BeApproximately(0.5, 1e-15);
        Hits(s, "ef").Should().BeEmpty("ef's line crosses ab's only past both segments");
        Hits(s, "gh").Should().Equal([(4.5, 5.5, "cd")], "gh is parallel to ab: no hit there");
    }

    [Fact]
    public void A_line_crosses_a_circle_twice_in_order_along_the_line_and_a_tangent_once()
    {
        var s = Of(new() { ["o"] = [0, 0], ["a"] = [10, 3], ["b"] = [-10, 3], ["c"] = [-10, 5], ["d"] = [10, 5], ["e"] = [-10, 6], ["f"] = [10, 6] },
            [new SketchCircle("disc", "o", 5), new SketchLine("ab", "a", "b"), new SketchLine("cd", "c", "d"), new SketchLine("ef", "e", "f")]);

        Hits(s, "ab").Should().Equal((4, 3, "disc"), (-4, 3, "disc"));
        Hits(s, "cd").Should().Equal((0, 5, "disc"));
        Hits(s, "ef").Should().BeEmpty();
        // Round the circle from +x: the crossing at 36.9°, the tangent point at 90°, the crossing at 143.1°.
        SketchIntersections.Of(s, "disc").Select(h => h.Other).Should().Equal("ab", "cd", "ab");
        SketchIntersections.Of(s, "disc")[1].At.Should().BeApproximately(Math.PI / 2, 1e-12);
    }

    [Fact]
    public void Circles_cross_twice_touch_once_and_concentric_ones_not_at_all()
    {
        var s = Of(new() { ["o"] = [0, 0], ["p"] = [6, 0], ["q"] = [8, 0], ["r"] = [2, 0] },
            [new SketchCircle("big", "o", 5), new SketchCircle("cross", "p", 3), new SketchCircle("outside", "q", 3), new SketchCircle("inside", "r", 3),
             new SketchCircle("ring", "o", 2) { Construction = true }, new SketchCircle("same", "o", 0.5)]);

        var x = (25.0 - 9 + 36) / 12;
        var y = Math.Round(Math.Sqrt(25 - x * x), 9);
        Hits(s, "cross").Select(h => h.Other).Should().Equal("outside", "big", "inside", "inside", "big", "outside");
        Hits(s, "big").Where(h => h.Other == "cross").Should().Equal((Math.Round(x, 9), y, "cross"), (Math.Round(x, 9), -y, "cross"));
        Hits(s, "big").Where(h => h.Other is "outside" or "inside").Should().Equal((5, 0, "inside"), (5, 0, "outside"));
        Hits(s, "same").Should().BeEmpty("concentric circles share no single point, and construction geometry cuts nothing");
    }

    [Fact]
    public void An_arc_is_met_only_within_its_sweep_ends_included()
    {
        // The upper half of a circle of 5, counter-clockwise from (5, 0) to (−5, 0).
        var s = Of(new() { ["o"] = [0, 0], ["f"] = [5, 0], ["t"] = [-5, 0], ["a"] = [0, -10], ["b"] = [0, 10], ["c"] = [-10, 0], ["d"] = [10, 0] },
            [new SketchArc("cap", "o", "f", "t"), new SketchLine("ab", "a", "b"), new SketchLine("cd", "c", "d")]);

        Hits(s, "ab").Should().Equal((0, 0, "cd"), (0, 5, "cap"));
        SketchIntersections.Of(s, "cap").Select(h => h.At).Should().Equal([0, Math.PI / 2, Math.PI], (a, b) => Math.Abs(a - b) < 1e-12);
        Hits(s, "cd").Should().Equal((-5, 0, "cap"), (0, 0, "ab"), (5, 0, "cap"));
    }

    // ---- Trim ----

    // A fixed 60 × 40 rectangle and a horizontal line across it at y = 20, from p (−10, 20) to q (70, 20).
    static Sketch Crossed(params SketchConstraint[] more) => Of(
        new() { ["a"] = [0, 0], ["b"] = [60, 0], ["c"] = [60, 40], ["d"] = [0, 40], ["p"] = [-10, 20], ["q"] = [70, 20] },
        [new SketchLine("bottom", "a", "b"), new SketchLine("right", "b", "c"), new SketchLine("top", "c", "d"), new SketchLine("left", "d", "a"), new SketchLine("cut", "p", "q")],
        [C("horizontal", "bottom"), C("vertical", "right"), C("horizontal", "top"), C("vertical", "left"), C("fix", "a"), D("length", 60, "bottom"), D("length", 40, "left"), C("horizontal", "cut"), .. more]);

    [Fact]
    public void Trimming_the_middle_of_a_line_across_a_rectangle_leaves_two_pieces_on_one_line()
    {
        var before = Crossed();
        var edit = Holds(SketchEdits.Trim(before, "cut", 30, 21), before);

        edit.Added.Should().Equal("cut-left", "cut-right", "cut-2");
        edit.Dropped.Should().BeEmpty();
        Entity<SketchLine>(edit, "cut").Should().Be(new SketchLine("cut", "p", "cut-left"), "the piece at its From end keeps the id");
        Entity<SketchLine>(edit, "cut-2").Should().Be(new SketchLine("cut-2", "cut-right", "q"));
        At(edit, "cut-left").Should().Be((0, 20));
        At(edit, "cut-right").Should().Be((60, 20));
        edit.Sketch.Constraints.Skip(8).Select(c => $"{c.Kind} {string.Join(" ", c.Refs)}")
            .Should().Equal("on cut-left left", "on cut-right right", "on cut-right cut", "on q cut");
        SketchRegions.Of(edit.Sketch, SketchSolver.Solve(edit.Sketch)).Regions.Should().ContainSingle();
    }

    [Fact]
    public void Trimming_the_end_of_a_line_drops_its_length_and_its_lost_end_and_lists_them()
    {
        var before = Crossed(C("fix", "p"), D("length", 80, "cut"), D("vertical-distance", 20, "a", "p"));
        var edit = Holds(SketchEdits.Trim(before, "cut", -6, 19), before);

        Entity<SketchLine>(edit, "cut").Should().Be(new SketchLine("cut", "cut-left", "q"), "the only piece left keeps the id");
        edit.Sketch.Points.Should().NotContainKey("p");
        edit.Added.Should().Equal("cut-left");
        edit.Dropped.Should().Equal("#8 (fix p)", "#9 (length cut = 80)", "#10 (vertical-distance a p = 20)");
    }

    [Fact]
    public void A_circle_trimmed_by_a_line_becomes_an_arc_with_its_id_and_its_radius()
    {
        var before = Of(new() { ["h"] = [0, 0], ["p"] = [-10, 2], ["q"] = [10, 2] },
            [new SketchCircle("bore", "h", 5), new SketchLine("cut", "p", "q")],
            C("fix", "h"), D("radius", 5, "bore"), C("horizontal", "cut"), C("fix", "p"));
        var edit = Holds(SketchEdits.Trim(before, "bore", 0, 6), before);

        var w = Math.Sqrt(21);
        var arc = Entity<SketchArc>(edit, "bore");
        arc.Should().Be(new SketchArc("bore", "h", "bore-cut-2", "bore-cut"), "counter-clockwise round the bottom, from the left cut to the right one");
        At(edit, "bore-cut").X.Should().BeApproximately(w, 1e-12);
        At(edit, "bore-cut-2").X.Should().BeApproximately(-w, 1e-12);
        edit.Sketch.Constraints.Should().Contain(c => c.Kind == "radius" && c.Refs[0] == "bore", "the radius dimension moves to the arc");
        edit.Dropped.Should().BeEmpty();

        // Trimming the line back to the arc closes a D: one region, its exact area the circle less the cap above y = 2.
        var d = SketchEdits.Trim(SketchEdits.Trim(edit.Sketch, "cut", -9, 2).Sketch, "cut", 9, 2);
        Entity<SketchLine>(d, "cut").Should().Be(new SketchLine("cut", "bore-cut-2", "bore-cut"));
        var (regions, problem) = SketchRegions.Of(d.Sketch, SketchSolver.Solve(d.Sketch));
        problem.Should().BeNull();
        Area(regions.Should().ContainSingle().Subject).Should().BeApproximately(25 * Math.PI - (25 * Math.Acos(0.4) - 2 * w), 1e-9);
    }

    [Fact]
    public void Trimming_the_middle_of_an_arc_leaves_two_arcs_on_one_circle()
    {
        var before = Of(new() { ["o"] = [0, 0], ["f"] = [5, 0], ["t"] = [-5, 0], ["a"] = [-2, -1], ["b"] = [-2, 7], ["c"] = [2, -1], ["d"] = [2, 7] },
            [new SketchArc("cap", "o", "f", "t"), new SketchLine("west", "a", "b"), new SketchLine("east", "c", "d")],
            C("fix", "o"), D("radius", 5, "cap"));
        var edit = Holds(SketchEdits.Trim(before, "cap", 0, 5.2), before);

        edit.Added.Should().Equal("cap-east", "cap-west", "cap-2");
        Entity<SketchArc>(edit, "cap").Should().Be(new SketchArc("cap", "o", "f", "cap-east"));
        Entity<SketchArc>(edit, "cap-2").Should().Be(new SketchArc("cap-2", "o", "cap-west", "t"));
        edit.Sketch.Constraints.Should().Contain(c => c.Kind == "on" && c.Refs.SequenceEqual(new[] { "cap-west", "cap" }), "the new arc stays on the kept one's circle");
    }

    // Two 20 × 10 rectangles overlapping by 10 × 5: a from (0, 0), b from (10, 5).
    static Sketch Overlapping() => Of(
        new() { ["a"] = [0, 0], ["b"] = [20, 0], ["c"] = [20, 10], ["d"] = [0, 10], ["e"] = [10, 5], ["f"] = [30, 5], ["g"] = [30, 15], ["h"] = [10, 15] },
        [new SketchLine("bottom", "a", "b"), new SketchLine("right", "b", "c"), new SketchLine("top", "c", "d"), new SketchLine("left", "d", "a"),
         new SketchLine("bbottom", "e", "f"), new SketchLine("bright", "f", "g"), new SketchLine("btop", "g", "h"), new SketchLine("bleft", "h", "e")],
        C("horizontal", "bottom"), C("vertical", "right"), C("horizontal", "top"), C("vertical", "left"), C("fix", "a"), D("length", 20, "bottom"), D("length", 10, "left"),
        C("horizontal", "bbottom"), C("vertical", "bright"), C("horizontal", "btop"), C("vertical", "bleft"), C("fix", "e"), D("length", 20, "bbottom"), D("length", 10, "bleft"));

    static SketchEdit Merged()
    {
        var s = Overlapping();
        var dropped = new List<string>();
        foreach (var (id, x, y) in new[] { ("top", 15.0, 10.0), ("right", 20, 7.5), ("bleft", 10, 7.5), ("bbottom", 15, 5) })
        {
            var edit = Holds(SketchEdits.Trim(s, id, x, y), s);
            dropped.AddRange(edit.Dropped);
            s = edit.Sketch;
        }
        return new SketchEdit(s, [], dropped);
    }

    [Fact]
    public void Two_overlapping_rectangles_trimmed_make_one_region_that_extrudes_to_its_exact_area()
    {
        SketchRegions.Of(Overlapping(), SketchSolver.Solve(Overlapping())).Problem.Should().Contain("cross");

        var merged = Merged();
        var (regions, problem) = SketchRegions.Of(merged.Sketch, SketchSolver.Solve(merged.Sketch));
        problem.Should().BeNull();
        var region = regions.Should().ContainSingle().Subject;
        region.Ids.Should().BeEquivalentTo(["bottom", "right", "bbottom", "bright", "btop", "bleft", "top", "left"]);
        Area(region).Should().BeApproximately(350, 1e-9);
        merged.Sketch.Points.Should().NotContainKeys("c", "e");
        merged.Dropped.Should().Equal("#13 (length bleft = 10)", "#14 (on top-bleft bleft)", "#11 (fix e)", "#12 (length bbottom = 20)", "#13 (on right-bbottom bbottom)");

        var r = new FrepModeler(new ModelingSettings()).Regenerate(new PartDocument([merged.Sketch, new Extrude("ext", BooleanOp.New, null, null, 0, 5) { Sketch = "s" }]));
        r.Diagnostics.Should().BeEmpty();
        r.Topology!.Faces.SelectMany(f => f.Sources).Order(StringComparer.Ordinal).Should().Equal(
            "ext/bbottom", "ext/bleft", "ext/bottom", "ext/bright", "ext/btop", "ext/end", "ext/left", "ext/right", "ext/start", "ext/top");
        MassProperties.Of(r.Part.Field!, r.Mesh!).Volume.Should().BeApproximately(350 * 5, 350 * 5 * 1e-3);
    }

    [Fact]
    public void Trim_is_deterministic()
    {
        string Text(SketchEdit e) => CanonicalFormat.Write(new PartDocument([e.Sketch])) + string.Join(",", e.Added) + string.Join(",", e.Dropped);

        Text(Merged()).Should().Be(Text(Merged()));
        Text(SketchEdits.Trim(Crossed(), "cut", 30, 21)).Should().Be(Text(SketchEdits.Trim(Crossed(), "cut", 30, 21)));
    }

    [Fact]
    public void What_cannot_be_trimmed_is_refused_saying_why()
    {
        var s = Of(new() { ["o"] = [0, 0], ["a"] = [-10, 5], ["b"] = [10, 5], ["c"] = [20, 0], ["d"] = [30, 0] },
            [new SketchCircle("disc", "o", 5), new SketchLine("touch", "a", "b"), new SketchLine("lone", "c", "d"), new SketchLine("guide", "a", "d") { Construction = true }]);

        FluentActions.Invoking(() => SketchEdits.Trim(s, "nope", 0, 0)).Should().Throw<ArgumentException>().WithMessage("*no entity 'nope'*");
        FluentActions.Invoking(() => SketchEdits.Trim(s, "guide", 0, 0)).Should().Throw<ArgumentException>().WithMessage("*construction*");
        FluentActions.Invoking(() => SketchEdits.Trim(s, "lone", 25, 0)).Should().Throw<ArgumentException>().WithMessage("*crosses nothing*delete it*");
        FluentActions.Invoking(() => SketchEdits.Trim(s, "disc", 0, -5)).Should().Throw<ArgumentException>().WithMessage("*one point only*");
    }

    /// <summary>The exact area a region encloses, arcs included, less its holes.</summary>
    static double Area(SketchRegion r) =>
        Math.Abs(Profiles.CounterClockwise(r.Outer).Sum(e => e.Edge.Area)) - r.Holes.Sum(h => Math.Abs(Profiles.CounterClockwise(h).Sum(e => e.Edge.Area)));
}
