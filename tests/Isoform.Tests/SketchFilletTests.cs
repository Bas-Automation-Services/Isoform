using Isoform.Document;
using Isoform.Geometry;
using Isoform.Modeling;
using Isoform.Sketching;

namespace Isoform.Tests;

/// <summary>
/// Sketch fillets: a tangent arc at a corner of two lines or arcs, every constraint holding after it, no freedom gained
/// or lost, the corner kept as a construction point for what is dimensioned to it, the region still closed with the
/// exact area, and the faces of an extrude named after the kept entities and the fillet. Corners it cannot round are
/// refused, saying why.
/// </summary>
public class SketchFilletTests
{
    static readonly SketchPlacement Xy = new(new SketchPlane([0, 0, 0], [1, 0, 0], [0, 0, 1]));

    static SketchConstraint C(string kind, params string[] refs) => new(kind, refs);

    static SketchConstraint D(string kind, double value, params string[] refs) => new(kind, refs, value);

    static Sketch Settled(Dictionary<string, double[]> points, SketchEntity[] entities, params SketchConstraint[] constraints)
    {
        var s = new Sketch("s", Xy, points, entities, constraints);
        return SketchSolver.Settle(s, SketchSolver.Solve(s));
    }

    static readonly SketchEntity[] Box =
        [new SketchLine("bottom", "a", "b"), new SketchLine("right", "b", "c"), new SketchLine("top", "c", "d"), new SketchLine("left", "d", "a")];

    // A 60 × 40 rectangle with its corner a at the origin, drawn a little skew and fully constrained.
    static Sketch Rectangle(params SketchConstraint[] sizes) => Settled(
        new() { ["a"] = [0, 0], ["b"] = [61, 1], ["c"] = [59, 41], ["d"] = [1, 38] }, Box,
        [C("horizontal", "bottom"), C("vertical", "right"), C("horizontal", "top"), C("vertical", "left"), C("fix", "a"),
         .. sizes.Length > 0 ? sizes : [D("length", 60, "bottom"), D("length", 40, "left")]]);

    // Half a disc of radius 20 round o: the line a (−20, 0) to b (20, 0), and the arc back over the top.
    static Sketch HalfDisc() => Settled(
        new() { ["o"] = [0, 0], ["a"] = [-20, 0], ["b"] = [20, 0] },
        [new SketchLine("bottom", "a", "b"), new SketchArc("cap", "o", "b", "a")],
        C("fix", "o"), C("horizontal", "bottom"), C("on", "o", "bottom"), D("radius", 20, "cap"));

    // A lens: two arcs of radius 20 round (0, 0) and (20, 0), meeting at p (10, −17.3) and q (10, 17.3).
    static Sketch Lens() => Settled(
        new() { ["m"] = [0, 0], ["n"] = [20, 0], ["p"] = [10, -17], ["q"] = [10, 17] },
        [new SketchArc("east", "m", "p", "q"), new SketchArc("west", "n", "q", "p")],
        C("fix", "m"), C("fix", "n"), D("radius", 20, "east"), D("radius", 20, "west"));

    static (double X, double Y) At(Sketch s, string p) => (s.Points[p][0], s.Points[p][1]);

    static double Distance(Sketch s, string p, string q) => Math.Sqrt(Math.Pow(s.Points[p][0] - s.Points[q][0], 2) + Math.Pow(s.Points[p][1] - s.Points[q][1], 2));

    /// <summary>The stored points satisfy every constraint: solving again leaves nothing to do and moves nothing.</summary>
    static SketchSolution Holds(Sketch s)
    {
        var solution = SketchSolver.Solve(s);
        solution.Solved.Should().BeTrue();
        solution.Residual.Should().BeLessThan(1e-10);
        foreach (var (name, xy) in s.Points)
        {
            solution.Points[name].X.Should().BeApproximately(xy[0], 1e-10, name);
            solution.Points[name].Y.Should().BeApproximately(xy[1], 1e-10, name);
        }
        return solution;
    }

    static double Area(Profile p)
    {
        var (x, y, area) = (p.Start[0], p.Start[1], 0.0);
        foreach (var e in p.Edges)
        {
            area += new ProfileEdge2D(x, y, e.To[0], e.To[1], e.Bulge).Area;
            (x, y) = (e.To[0], e.To[1]);
        }
        return area;
    }

    static SketchRegion Region(Sketch s)
    {
        var (regions, problem) = SketchRegions.Of(s, SketchSolver.Solve(s));
        problem.Should().BeNull();
        return regions.Should().ContainSingle().Subject;
    }

    [Fact]
    public void A_rectangle_corner_is_rounded_with_a_tangent_arc()
    {
        var before = Rectangle();
        var edit = SketchEdits.Fillet(before, "c", 5);
        var s = edit.Sketch;

        edit.Added.Should().Equal("c-fillet", "c-fillet-centre", "c-fillet-right", "c-fillet-top");
        edit.Dropped.Should().BeEmpty();
        Holds(s).FreeDegrees.Should().Be(SketchSolver.Solve(before).FreeDegrees).And.Be(0);
        At(s, "c-fillet-centre").Should().Be((55, 35));
        At(s, "c-fillet-right").X.Should().BeApproximately(60, 1e-12);
        At(s, "c-fillet-right").Y.Should().BeApproximately(35, 1e-12);
        At(s, "c-fillet-top").X.Should().BeApproximately(55, 1e-12);
        At(s, "c-fillet-top").Y.Should().BeApproximately(40, 1e-12);
        At(s, "c").Should().Be((60, 40), "the corner stays where it was");
        s.Entities.Should().ContainEquivalentOf(new SketchArc("c-fillet", "c-fillet-centre", "c-fillet-right", "c-fillet-top"), "counter-clockwise from the right side to the top");
        s.Entities.Should().Contain([new SketchLine("right", "b", "c-fillet-right"), new SketchLine("top", "c-fillet-top", "d")]);
        Area(Region(s).Outer).Should().BeApproximately(60 * 40 - (4 - Math.PI) / 4 * 25, 1e-9);
    }

    [Fact]
    public void Lengths_of_the_lines_cut_short_still_measure_to_the_corner()
    {
        var edit = SketchEdits.Fillet(Rectangle(), "a", 4);
        var s = edit.Sketch;

        Holds(s).FreeDegrees.Should().Be(0);
        s.Constraints.Should().ContainEquivalentOf(D("distance", 60, "a", "b")).And.ContainEquivalentOf(D("distance", 40, "a", "d"));
        s.Constraints.Should().NotContain(c => c.Kind == "length");
        At(s, "c").Should().Be((60, 40));
        Distance(s, "a-fillet-bottom", "b").Should().BeApproximately(56, 1e-10);
    }

    [Fact]
    public void A_dimension_to_the_old_corner_still_holds()
    {
        var edit = SketchEdits.Fillet(Rectangle(D("length", 60, "bottom"), D("vertical-distance", 40, "a", "c")), "c", 6);
        var s = edit.Sketch;

        Holds(s).FreeDegrees.Should().Be(0);
        At(s, "c").X.Should().BeApproximately(60, 1e-10);
        At(s, "c").Y.Should().BeApproximately(40, 1e-10);
        s.Constraints.Should().Contain(c => c.Kind == "on" && c.Refs.SequenceEqual(new[] { "c", "right" }))
            .And.Contain(c => c.Kind == "on" && c.Refs.SequenceEqual(new[] { "c", "top" }));
    }

    [Fact]
    public void Equal_sides_cut_short_move_to_construction_lines_along_the_old_sides()
    {
        var edit = SketchEdits.Fillet(Rectangle(D("length", 40, "bottom"), C("equal", "bottom", "left")), "a", 5);
        var s = edit.Sketch;

        edit.Added.Should().Equal("a-fillet", "a-fillet-centre", "a-fillet-bottom", "a-fillet-left", "bottom-sharp", "left-sharp");
        Holds(s).FreeDegrees.Should().Be(0);
        s.Entities.Should().ContainEquivalentOf(new SketchLine("bottom-sharp", "a", "b") { Construction = true });
        s.Constraints.Should().ContainEquivalentOf(C("equal", "bottom-sharp", "left-sharp"));
        At(s, "d").Y.Should().BeApproximately(40, 1e-10);
        Area(Region(s).Outer).Should().BeApproximately(40 * 40 - (4 - Math.PI) / 4 * 25, 1e-9);
    }

    [Fact]
    public void Every_corner_of_a_rectangle_rounds_in_turn()
    {
        var s = Rectangle();
        foreach (var corner in new[] { "a", "b", "c", "d" }) s = SketchEdits.Fillet(s, corner, 3).Sketch;

        Holds(s).FreeDegrees.Should().Be(0);
        Area(Region(s).Outer).Should().BeApproximately(60 * 40 - (4 - Math.PI) * 9, 1e-9);
        Region(s).Ids.Should().BeEquivalentTo(["bottom", "a-fillet", "left", "d-fillet", "top", "c-fillet", "right", "b-fillet"]);
    }

    [Fact]
    public void The_extruded_fillet_is_a_face_of_its_own_and_the_sides_keep_their_names()
    {
        var s = SketchEdits.Fillet(Rectangle(), "c", 5).Sketch;
        var doc = new PartDocument([s, new Extrude("plate", BooleanOp.New, null, null, 0, 5) { Sketch = "s" }]);

        var r = new FrepModeler(new ModelingSettings()).Regenerate(doc);

        r.Diagnostics.Should().BeEmpty();
        r.Topology!.Faces.SelectMany(f => f.Sources).Order(StringComparer.Ordinal).Should()
            .Equal("plate/bottom", "plate/c-fillet", "plate/end", "plate/left", "plate/right", "plate/start", "plate/top");
        var volume = (60 * 40 - (4 - Math.PI) / 4 * 25) * 5;
        MassProperties.Of(r.Part.Field!, r.Mesh!).Volume.Should().BeApproximately(volume, volume * 1e-3);
    }

    [Fact]
    public void A_corner_of_a_line_and_an_arc_rounds_inside_the_arc()
    {
        var before = HalfDisc();
        var s = SketchEdits.Fillet(before, "b", 5).Sketch;

        Holds(s).FreeDegrees.Should().Be(SketchSolver.Solve(before).FreeDegrees).And.Be(0);
        // The centre 5 above the line and 15 from o; the tangent points below it and on the cap's radius through it.
        var (cx, cy) = At(s, "b-fillet-centre");
        cx.Should().BeApproximately(Math.Sqrt(200), 1e-10);
        cy.Should().BeApproximately(5, 1e-10);
        At(s, "b-fillet-bottom").X.Should().BeApproximately(cx, 1e-10);
        At(s, "b-fillet-bottom").Y.Should().BeApproximately(0, 1e-10);
        Distance(s, "b-fillet-cap", "o").Should().BeApproximately(20, 1e-10);
        Distance(s, "b-fillet-cap", "b-fillet-centre").Should().BeApproximately(5, 1e-10);
        s.Entities.Should().Contain(new SketchArc("cap", "o", "b-fillet-cap", "a"));
        Region(s).Ids.Should().BeEquivalentTo(["bottom", "cap", "b-fillet"]);
        Area(Region(s).Outer).Should().BeLessThan(Math.PI * 200).And.BeGreaterThan(Math.PI * 200 - 25);

        // And at the other end, where the cap ends rather than starts: the mirror image.
        var both = SketchEdits.Fillet(s, "a", 5).Sketch;
        Holds(both).FreeDegrees.Should().Be(0);
        At(both, "a-fillet-centre").X.Should().BeApproximately(-Math.Sqrt(200), 1e-10);
        At(both, "a-fillet-centre").Y.Should().BeApproximately(5, 1e-10);
        both.Entities.Should().Contain(new SketchArc("cap", "o", "b-fillet-cap", "a-fillet-cap"));
    }

    [Fact]
    public void A_corner_of_two_arcs_rounds_inside_both()
    {
        var before = Lens();
        var s = SketchEdits.Fillet(before, "q", 3).Sketch;

        Holds(s).FreeDegrees.Should().Be(SketchSolver.Solve(before).FreeDegrees).And.Be(0);
        var (cx, cy) = At(s, "q-fillet-centre");
        cx.Should().BeApproximately(10, 1e-10);
        cy.Should().BeApproximately(Math.Sqrt(17 * 17 - 10 * 10), 1e-10);
        Distance(s, "q-fillet-east", "m").Should().BeApproximately(20, 1e-10);
        Distance(s, "q-fillet-west", "n").Should().BeApproximately(20, 1e-10);
        Distance(s, "q-fillet-east", "q-fillet-centre").Should().BeApproximately(3, 1e-10);
        Region(s).Ids.Should().BeEquivalentTo(["east", "west", "q-fillet"]);
    }

    [Fact]
    public void A_fillet_is_deterministic()
    {
        string Text() => CanonicalFormat.Write(new PartDocument([SketchEdits.Fillet(HalfDisc(), "a", 4).Sketch]));

        Text().Should().Be(Text());
    }

    [Fact]
    public void New_names_do_not_clash_with_the_sketch()
    {
        var s = SketchEdits.Fillet(Rectangle(), "c", 5).Sketch;
        s = s with { Points = new Dictionary<string, double[]>(s.Points) { ["a-fillet"] = [100, 100] } };

        SketchEdits.Fillet(s, "a", 2).Added.Should().StartWith(["a-fillet-2", "a-fillet-2-centre"]);
    }

    [Theory]
    [InlineData("x", 5, "not a point")]
    [InlineData("bottom", 5, "not a point")]
    [InlineData("c", 0, "above 0")]
    [InlineData("c", -2, "above 0")]
    [InlineData("c", 45, "does not fit")]
    public void Corners_that_cannot_be_rounded_are_refused(string corner, double radius, string why)
    {
        var fillet = () => SketchEdits.Fillet(Rectangle(), corner, radius);

        fillet.Should().Throw<ArgumentException>().WithMessage($"*{why}*");
    }

    [Fact]
    public void A_fillet_too_big_for_the_arc_is_refused() =>
        FluentActions.Invoking(() => SketchEdits.Fillet(HalfDisc(), "b", 25)).Should().Throw<ArgumentException>().WithMessage("*inside the arc*");

    [Fact]
    public void A_point_without_exactly_two_lines_or_arcs_is_refused()
    {
        var open = Settled(new() { ["a"] = [0, 0], ["b"] = [10, 0], ["c"] = [10, 10], ["e"] = [0, 10] },
            [new SketchLine("ab", "a", "b"), new SketchLine("bc", "b", "c"), new SketchLine("be", "b", "e")]);

        FluentActions.Invoking(() => SketchEdits.Fillet(open, "a", 1)).Should().Throw<ArgumentException>().WithMessage("*1 end at 'a'*");
        FluentActions.Invoking(() => SketchEdits.Fillet(open, "b", 1)).Should().Throw<ArgumentException>().WithMessage("*3 end at 'b'*");
    }

    [Fact]
    public void A_circle_or_construction_geometry_is_refused()
    {
        var s = Settled(new() { ["o"] = [0, 0], ["a"] = [10, 0], ["b"] = [10, 10], ["c"] = [20, 0] },
            [new SketchCircle("ring", "o", 5), new SketchLine("guide", "a", "b") { Construction = true }, new SketchLine("rail", "a", "c")]);

        FluentActions.Invoking(() => SketchEdits.Fillet(s, "o", 1)).Should().Throw<ArgumentException>().WithMessage("*circle*");
        FluentActions.Invoking(() => SketchEdits.Fillet(s, "a", 1)).Should().Throw<ArgumentException>().WithMessage("*construction*");
    }

    [Fact]
    public void Two_entities_that_meet_smoothly_have_no_corner_to_round()
    {
        // A line running into a quarter arc along its tangent.
        var s = Settled(new() { ["a"] = [0, 0], ["b"] = [10, 0], ["o"] = [10, 10], ["c"] = [20, 10] },
            [new SketchLine("run", "a", "b"), new SketchArc("bend", "o", "b", "c")]);

        FluentActions.Invoking(() => SketchEdits.Fillet(s, "b", 1)).Should().Throw<ArgumentException>().WithMessage("*without a corner*");
    }
}
