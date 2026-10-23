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
}
