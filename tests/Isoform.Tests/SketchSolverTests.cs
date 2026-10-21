using Isoform.Document;
using Isoform.Sketching;

namespace Isoform.Tests;

/// <summary>
/// The sketch solver: constraints hold to <see cref="SketchSolver.Tolerance"/> from a perturbed start, degrees of
/// freedom are counted, conflicts and redundancy are named, solving is deterministic and a drag moves only what is
/// free. And the regions a solved sketch encloses.
/// </summary>
public class SketchSolverTests
{
    static readonly SketchPlacement Xy = new(new SketchPlane([0, 0, 0], [1, 0, 0], [0, 0, 1]));

    static Sketch Of(Dictionary<string, double[]> points, SketchEntity[] entities, params SketchConstraint[] constraints) =>
        new("s", Xy, points, entities, constraints);

    static SketchConstraint C(string kind, params string[] refs) => new(kind, refs);

    static SketchConstraint D(string kind, double value, params string[] refs) => new(kind, refs, value);

    // A rectangle drawn a little skew: a (0, 0) b (61, 1) c (59, 41) d (1, 38).
    static Dictionary<string, double[]> Skew() => new() { ["a"] = [0, 0], ["b"] = [61, 1], ["c"] = [59, 41], ["d"] = [1, 38] };

    static readonly SketchEntity[] Box =
        [new SketchLine("bottom", "a", "b"), new SketchLine("right", "b", "c"), new SketchLine("top", "c", "d"), new SketchLine("left", "d", "a")];

    static SketchConstraint[] Rectangle(double width, double height) =>
    [
        C("horizontal", "bottom"), C("vertical", "right"), C("horizontal", "top"), C("vertical", "left"), C("fix", "a"),
        D("length", width, "bottom"), D("length", height, "left"),
    ];

    [Fact]
    public void A_dimensioned_rectangle_solves_with_no_freedom_left()
    {
        var s = SketchSolver.Solve(Of(Skew(), Box, Rectangle(60, 40)));

        s.Solved.Should().BeTrue();
        s.FreeDegrees.Should().Be(0);
        s.Free.Should().BeEmpty();
        s.Residual.Should().BeLessThan(SketchSolver.Tolerance);
        s.Points["c"].X.Should().BeApproximately(60, 1e-12);
        s.Points["c"].Y.Should().BeApproximately(40, 1e-12);
    }

    [Fact]
    public void A_free_triangle_has_six_degrees_of_freedom()
    {
        var s = SketchSolver.Solve(Of(new() { ["a"] = [0, 0], ["b"] = [10, 0], ["c"] = [0, 10] },
            [new SketchLine("ab", "a", "b"), new SketchLine("bc", "b", "c"), new SketchLine("ca", "c", "a")]));

        s.FreeDegrees.Should().Be(6);
        s.Free.Should().BeEquivalentTo(["a", "b", "c"]);
        s.Points["b"].Should().Be((10, 0), "nothing moves what no constraint touches");
    }

    [Fact]
    public void Constraints_that_cannot_all_hold_are_named()
    {
        var s = SketchSolver.Solve(Of(Skew(), Box, [.. Rectangle(60, 40), D("length", 50, "top")]));

        s.Solved.Should().BeFalse();
        var conflict = s.Problems.Single(p => p.Code == SketchSolution.Conflict);
        conflict.Constraints.Should().Contain([5, 7], "the two lengths of opposite sides");
        conflict.Message.Should().Contain("length top").And.Contain("length bottom");
    }

    [Fact]
    public void A_constraint_the_others_imply_is_a_warning()
    {
        var s = SketchSolver.Solve(Of(Skew(), Box, [.. Rectangle(60, 40), C("perpendicular", "bottom", "left")]));

        s.Solved.Should().BeTrue();
        s.FreeDegrees.Should().Be(0);
        s.Problems.Should().Contain(p => p.Code == SketchSolution.Redundant);
    }

    [Fact]
    public void Solving_is_deterministic()
    {
        var sketch = Of(Skew(), Box, Rectangle(60, 40)[..^1]);
        var (a, b) = (SketchSolver.Solve(sketch), SketchSolver.Solve(sketch));

        a.Points.Should().Equal(b.Points);
        a.FreeDegrees.Should().Be(1);
    }

    [Fact]
    public void A_drag_moves_what_is_free_and_keeps_the_constraints()
    {
        // Width fixed, height free: dragging c up changes the height only.
        var sketch = Of(Skew(), Box, Rectangle(60, 40)[..^1]);
        var s = SketchSolver.Solve(sketch, ("c", 60, 55));

        s.Solved.Should().BeTrue();
        s.Points["c"].X.Should().BeApproximately(60, 1e-9);
        s.Points["c"].Y.Should().BeApproximately(55, 1e-6);
        s.Points["d"].Y.Should().BeApproximately(55, 1e-6);
        s.Points["a"].X.Should().BeApproximately(0, 1e-12);
        s.Points["a"].Y.Should().BeApproximately(0, 1e-12);
    }

    public static TheoryData<string, Sketch, Func<SketchSolution, double>> Kinds() => new()
    {
        { "coincident", Of(new() { ["p"] = [0, 0], ["q"] = [3, 1] }, [], C("coincident", "p", "q")), s => Dist(s, "p", "q") },
        { "horizontal points", Of(new() { ["p"] = [0, 0], ["q"] = [3, 1] }, [], C("horizontal", "p", "q")), s => s.Points["q"].Y - s.Points["p"].Y },
        { "vertical", Of(new() { ["p"] = [0, 0], ["q"] = [1, 3] }, [new SketchLine("l", "p", "q")], C("vertical", "l")), s => s.Points["q"].X - s.Points["p"].X },
        { "parallel", Lines(C("parallel", "l", "m")), s => Cross(s, "l", "m") },
        { "perpendicular", Lines(C("perpendicular", "l", "m")), s => Dot(s, "l", "m") },
        { "equal lines", Lines(C("equal", "l", "m")), s => Length(s, "p", "q") - Length(s, "r", "t") },
        { "midpoint", Of(new() { ["p"] = [0, 0], ["q"] = [10, 0], ["m"] = [4, 1] }, [new SketchLine("l", "p", "q")], C("midpoint", "m", "l")),
            s => Dist(s, "m", ("p", "q")) },
        { "symmetric", Of(new() { ["p"] = [-3, 1], ["q"] = [4, 2], ["a"] = [0, -5], ["b"] = [0, 5] }, [new SketchLine("axis", "a", "b")], C("symmetric", "p", "q", "axis")),
            s => Math.Abs(SignedToLine(s, "m", "a", "b", ((s.Points["p"].X + s.Points["q"].X) / 2, (s.Points["p"].Y + s.Points["q"].Y) / 2))) + Math.Abs(Along(s, "p", "q", "a", "b")) },
        { "on line", Of(new() { ["p"] = [2, 3], ["a"] = [0, 0], ["b"] = [10, 0] }, [new SketchLine("l", "a", "b")], C("on", "p", "l")), s => DistanceToLine(s, "p", "a", "b") },
        { "on circle", Of(new() { ["p"] = [6, 1], ["c"] = [0, 0] }, [new SketchCircle("o", "c", 5)], C("on", "p", "o")), s => Length(s, "p", "c") - s.Radii["o"] },
        { "length", Lines(D("length", 7, "l")), s => Length(s, "p", "q") - 7 },
        { "distance points", Of(new() { ["p"] = [0, 0], ["q"] = [3, 1] }, [], D("distance", 5, "p", "q")), s => Length(s, "p", "q") - 5 },
        { "distance to line", Of(new() { ["p"] = [2, 3], ["a"] = [0, 0], ["b"] = [10, 0] }, [new SketchLine("l", "a", "b")], D("distance", 8, "p", "l")),
            s => DistanceToLine(s, "p", "a", "b") - 8 },
        { "horizontal distance", Of(new() { ["p"] = [0, 0], ["q"] = [3, 1] }, [], D("horizontal-distance", 9, "p", "q")), s => s.Points["q"].X - s.Points["p"].X - 9 },
        { "radius", Of(new() { ["c"] = [0, 0] }, [new SketchCircle("o", "c", 5)], D("radius", 3, "o")), s => s.Radii["o"] - 3 },
        { "diameter of an arc", Of(new() { ["c"] = [0, 0], ["f"] = [5, 0], ["t"] = [0, 5.5] }, [new SketchArc("o", "c", "f", "t")], D("diameter", 6, "o")),
            s => Length(s, "f", "c") - 3 + Math.Abs(Length(s, "t", "c") - 3) },
        { "angle", Lines(D("angle", 30, "l", "m")), s => Angle(s, "l", "m") - 30 },
        { "tangent line and circle", Of(new() { ["c"] = [0, 0], ["a"] = [-10, 6], ["b"] = [10, 7] }, [new SketchCircle("o", "c", 5), new SketchLine("l", "a", "b")], C("tangent", "l", "o")),
            s => DistanceToLine(s, "c", "a", "b") - s.Radii["o"] },
        { "tangent circles", Of(new() { ["c"] = [0, 0], ["d"] = [9, 1] }, [new SketchCircle("o", "c", 5), new SketchCircle("k", "d", 3)], C("tangent", "o", "k")),
            s => Length(s, "c", "d") - s.Radii["o"] - s.Radii["k"] },
    };

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Every_constraint_kind_holds_after_solving(string kind, Sketch sketch, Func<SketchSolution, double> error)
    {
        var s = SketchSolver.Solve(sketch);

        s.Solved.Should().BeTrue(kind + ": " + string.Join("; ", s.Problems.Select(p => p.Message)));
        Math.Abs(error(s)).Should().BeLessThan(1e-9, kind);
    }

    [Fact]
    public void A_bad_reference_is_reported_not_thrown()
    {
        var s = SketchSolver.Solve(Of(Skew(), Box, C("parallel", "bottom", "nowhere")));

        s.Problems.Should().ContainSingle(p => p.Code == SketchSolution.Invalid).Which.Message.Should().Contain("nowhere");
    }

    // ---- Regions ----

    static (IReadOnlyList<SketchRegion> Regions, string? Problem) Regions(Sketch sketch) => SketchRegions.Of(sketch, SketchSolver.Solve(sketch));

    [Fact]
    public void A_rectangle_with_a_circle_in_it_is_one_region_with_a_hole()
    {
        var points = Skew();
        points["h"] = [20, 20];
        var (regions, problem) = Regions(Of(points, [.. Box, new SketchCircle("bore", "h", 4)], Rectangle(60, 40)));

        problem.Should().BeNull();
        var region = regions.Should().ContainSingle().Subject;
        region.Ids.Should().BeEquivalentTo(["bottom", "right", "top", "left"]);
        region.Holes.Should().ContainSingle().Which.Edges.Should().OnlyContain(e => e.Id == "bore");
    }

    [Fact]
    public void Separate_loops_are_separate_regions_and_can_be_picked()
    {
        var points = new Dictionary<string, double[]> { ["a"] = [0, 0], ["b"] = [10, 0], ["c"] = [5, 8], ["k"] = [30, 0] };
        var sketch = Of(points, [new SketchLine("ab", "a", "b"), new SketchLine("bc", "b", "c"), new SketchLine("ca", "c", "a"), new SketchCircle("disc", "k", 3)]);
        var (regions, _) = Regions(sketch);

        regions.Should().HaveCount(2);
        SketchRegions.Select(regions, [["disc"]]).Regions.Should().ContainSingle().Which.Ids.Should().Equal("disc");
        SketchRegions.Select(regions, [["nope"]]).Problem.Should().Contain("nope");
    }

    [Fact]
    public void Construction_lines_and_open_chains_are_not_profiles()
    {
        var points = Skew();
        (points["x"], points["y"]) = ([70, 0], [80, 5]);
        var (regions, problem) = Regions(Of(points,
            [.. Box, new SketchLine("diagonal", "a", "c") { Construction = true }, new SketchLine("stray", "x", "y")], Rectangle(60, 40)));

        problem.Should().BeNull();
        regions.Should().ContainSingle().Which.Ids.Should().BeEquivalentTo(["bottom", "right", "top", "left"]);
    }

    [Fact]
    public void Crossing_loops_and_branches_are_problems()
    {
        var points = Skew();
        points["h"] = [60, 20];
        Regions(Of(points, [.. Box, new SketchCircle("bore", "h", 4)], Rectangle(60, 40))).Problem.Should().Contain("cross");

        var branch = Skew();
        branch["e"] = [30, 20];
        Regions(Of(branch, [.. Box, new SketchLine("spur", "a", "e"), new SketchLine("back", "e", "c")], Rectangle(60, 40))).Problem.Should().Contain("joins 3 edges");
    }

    [Fact]
    public void A_slot_of_tangent_arcs_solves_into_a_bulged_profile()
    {
        var points = new Dictionary<string, double[]>
        {
            ["a"] = [0, 0], ["b"] = [20, 0], ["c"] = [20, 10], ["d"] = [0, 10], ["r"] = [20.5, 5], ["l"] = [-0.5, 5],
        };
        var sketch = Of(points,
        [
            new SketchLine("bottom", "a", "b"), new SketchArc("east", "r", "b", "c"), new SketchLine("top", "c", "d"), new SketchArc("west", "l", "d", "a"),
        ],
        C("fix", "a"), C("horizontal", "bottom"), C("tangent", "top", "east"), C("tangent", "top", "west"), C("tangent", "bottom", "east"), C("tangent", "bottom", "west"),
        C("equal", "east", "west"), D("length", 20, "bottom"), D("radius", 5, "east"));

        var s = SketchSolver.Solve(sketch);
        var (regions, problem) = SketchRegions.Of(sketch, s);

        s.Solved.Should().BeTrue(string.Join("; ", s.Problems.Select(p => p.Message)));
        s.FreeDegrees.Should().Be(0);
        problem.Should().BeNull();
        regions.Single().Outer.Edges.Where(e => e.Bulge != 0).Should().HaveCount(2).And.OnlyContain(e => Math.Abs(Math.Abs(e.Bulge) - 1) < 1e-9, "half circles");
    }

    [Fact]
    public void A_sketch_reads_back_as_written_with_expressions_kept()
    {
        var text = """
            {
              "format": "isoform/0",
              "units": "mm",
              "parameters": {
                "width": 60
              },
              "features": [
                {
                  "id": "base",
                  "type": "sketch",
                  "on": {
                    "face": "plate/z+"
                  },
                  "points": {
                    "a": [0, 0],
                    "b": [60, 0]
                  },
                  "entities": [
                    {
                      "id": "bottom",
                      "line": ["a", "b"]
                    },
                    {
                      "id": "guide",
                      "line": ["b", "a"],
                      "construction": true
                    }
                  ],
                  "constraints": [
                    {
                      "horizontal": "bottom"
                    },
                    {
                      "length": "bottom",
                      "value": "=width"
                    }
                  ]
                }
              ]
            }
            """;
        var doc = CanonicalFormat.Read(text);
        var sketch = (Sketch)doc.Features[0];

        sketch.On.Face.Should().Be("plate/z+");
        sketch.Entities[1].Construction.Should().BeTrue();
        doc.Expressions.Should().ContainKey("base.constraints.1.value").WhoseValue.Should().Be("width");
        CanonicalFormat.Read(CanonicalFormat.Write(doc)).Should().Match<PartDocument>(d => CanonicalFormat.Write(d) == CanonicalFormat.Write(doc));
        ((Sketch)Parametric.Resolve(doc).Features[0]).Constraints[1].Value.Should().Be(60);
    }

    // ---- Measures ----

    static Sketch Lines(SketchConstraint c) => Of(new() { ["p"] = [0, 0], ["q"] = [10, 1], ["r"] = [2, 5], ["t"] = [9, 9] },
        [new SketchLine("l", "p", "q"), new SketchLine("m", "r", "t")], c);

    static double Length(SketchSolution s, string a, string b) => Math.Sqrt(Math.Pow(s.Points[a].X - s.Points[b].X, 2) + Math.Pow(s.Points[a].Y - s.Points[b].Y, 2));

    static double Dist(SketchSolution s, string a, string b) => Length(s, a, b);

    static double Dist(SketchSolution s, string m, (string A, string B) line) =>
        Math.Sqrt(Math.Pow(s.Points[m].X - (s.Points[line.A].X + s.Points[line.B].X) / 2, 2) + Math.Pow(s.Points[m].Y - (s.Points[line.A].Y + s.Points[line.B].Y) / 2, 2));

    static (double X, double Y) Dir(SketchSolution s, string line) => line == "l"
        ? (s.Points["q"].X - s.Points["p"].X, s.Points["q"].Y - s.Points["p"].Y)
        : (s.Points["t"].X - s.Points["r"].X, s.Points["t"].Y - s.Points["r"].Y);

    static double Cross(SketchSolution s, string a, string b) => Dir(s, a).X * Dir(s, b).Y - Dir(s, a).Y * Dir(s, b).X;

    static double Dot(SketchSolution s, string a, string b) => Dir(s, a).X * Dir(s, b).X + Dir(s, a).Y * Dir(s, b).Y;

    static double Angle(SketchSolution s, string a, string b) =>
        (Math.Atan2(Dir(s, b).Y, Dir(s, b).X) - Math.Atan2(Dir(s, a).Y, Dir(s, a).X)) * 180 / Math.PI;

    // Signed distance of a point (by name, or given) from the line through a and b.
    static double SignedToLine(SketchSolution s, string p, string a, string b, (double X, double Y)? at = null)
    {
        var q = at ?? s.Points[p];
        var (dx, dy) = (s.Points[b].X - s.Points[a].X, s.Points[b].Y - s.Points[a].Y);
        return (dx * (q.Y - s.Points[a].Y) - dy * (q.X - s.Points[a].X)) / Math.Sqrt(dx * dx + dy * dy);
    }

    // The component of pq along the line through a and b.
    static double Along(SketchSolution s, string p, string q, string a, string b)
    {
        var (dx, dy) = (s.Points[b].X - s.Points[a].X, s.Points[b].Y - s.Points[a].Y);
        return ((s.Points[q].X - s.Points[p].X) * dx + (s.Points[q].Y - s.Points[p].Y) * dy) / Math.Sqrt(dx * dx + dy * dy);
    }

    static double DistanceToLine(SketchSolution s, string p, string a, string b)
    {
        var (dx, dy) = (s.Points[b].X - s.Points[a].X, s.Points[b].Y - s.Points[a].Y);
        return Math.Abs(dx * (s.Points[p].Y - s.Points[a].Y) - dy * (s.Points[p].X - s.Points[a].X)) / Math.Sqrt(dx * dx + dy * dy);
    }
}
