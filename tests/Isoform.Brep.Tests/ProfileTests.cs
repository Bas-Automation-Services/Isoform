using BasAutomation.Geometry;
using static Isoform.Brep.Tests.Shapes;

namespace Isoform.Brep.Tests;

public class ProfileTests
{
    const double Tolerance = Body.DefaultTolerance;

    [Fact]
    public void Signed_areas_are_exact_for_lines_and_arcs()
    {
        Rectangle(0, 0, 3, 2).SignedArea.Should().Be(6);
        Rectangle(0, 0, 3, 2).Reversed().SignedArea.Should().Be(-6);
        Circle(5, 5, 2).SignedArea.Should().BeApproximately(Math.PI * 4, 1e-14);
        Slot(10, 1).SignedArea.Should().BeApproximately(20 + Math.PI, 1e-14);
        Bitten().SignedArea.Should().BeApproximately(BittenArea, 1e-12);
        D(0, 0, 3).SignedArea.Should().BeApproximately(Math.PI * 9 / 2, 1e-14);
    }

    [Fact]
    public void Winding_counts_arcs_that_bulge_round_the_point()
    {
        var bitten = Bitten();
        bitten.Winding(P(30, 35)).Should().Be(0, "the bite is outside");
        bitten.Winding(P(30, 25)).Should().Be(1, "below the bite is inside");
        bitten.Winding(P(59, 1)).Should().Be(0, "the rounded corner is cut away");
        bitten.Winding(P(57, 3)).Should().Be(1);
        D(0, 0, 3).Winding(P(2.5, 0)).Should().Be(1, "inside the half circle, past the chord's x");
        D(0, 0, 3).Winding(P(-0.1, 0)).Should().Be(0);
        Circle(0, 0, 1).Reversed().Winding(P(0, 0)).Should().Be(-1);
    }

    [Fact]
    public void Valid_regions_have_no_problem()
    {
        new Region(Rectangle(0, 0, 10, 10), [Circle(5, 5, 2)]).Problem(Tolerance).Should().BeNull();
        new Region(Slot(10, 1)).Problem(Tolerance).Should().BeNull();
        new Region(Bitten()).Problem(Tolerance).Should().BeNull();
        new Region(D(0, 0, 3)).Problem(Tolerance).Should().BeNull();
    }

    public static TheoryData<string, Region, string> Refused => new()
    {
        { "open loop", new Region(new ProfileLoop([new LineSegment(P(0, 0), P(1, 0)), new LineSegment(P(1, 0), P(1, 1))])), "does not close" },
        { "zero-length line", new Region(new ProfileLoop([.. Rectangle(0, 0, 1, 1).Segments, new LineSegment(P(0, 0), P(0, 0))])), "shorter than the tolerance" },
        { "bow tie", new Region(new ProfileLoop(
            [
                new LineSegment(P(0, 0), P(1, 1)), new LineSegment(P(1, 1), P(1, 0)),
                new LineSegment(P(1, 0), P(0, 1)), new LineSegment(P(0, 1), P(0, 0)),
            ])), "meet" },
        { "hole outside", new Region(Rectangle(0, 0, 10, 10), [Circle(20, 5, 2)]), "outside the outer loop" },
        { "hole through the side", new Region(Rectangle(0, 0, 10, 10), [Circle(9, 5, 2)]), "meet" },
        { "hole touching the side", new Region(Rectangle(0, 0, 10, 10), [Circle(8, 5, 2)]), "meet" },
        { "hole 1e-8 from the side", new Region(Rectangle(0, 0, 10, 10), [Circle(8 - 1e-8, 5, 2)]), "come within" },
        { "hole in a hole", new Region(Rectangle(0, 0, 10, 10), [Circle(5, 5, 3), Circle(5, 5, 1)]), "inside hole" },
        { "touching holes", new Region(Rectangle(0, 0, 20, 10), [Circle(6, 5, 2), Circle(10, 5, 2)]), "meet" },
        { "circle in a chain", new Region(new ProfileLoop([ArcSegment.Circle(P(0, 0), 1), new LineSegment(P(1, 0), P(2, 0))])), "full circle" },
        { "line folding back", new Region(new ProfileLoop(
            [
                new LineSegment(P(0, 0), P(10, 0)), new LineSegment(P(10, 0), P(5, 0)),
                new LineSegment(P(5, 0), P(5, 5)), new LineSegment(P(5, 5), P(0, 0)),
            ])), "meet" },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void Regions_that_cannot_bound_a_solid_are_refused(string what, Region region, string reason)
    {
        region.Problem(Tolerance).Should().NotBeNull(what).And.Contain(reason);
    }

    [Fact]
    public void A_hole_just_clear_of_the_side_is_accepted()
    {
        new Region(Rectangle(0, 0, 10, 10), [Circle(8 - 1e-6, 5, 2)]).Problem(Tolerance).Should().BeNull();
    }

    [Fact]
    public void A_point_on_the_chord_of_half_an_arc_is_counted_once()
    {
        // A slot's end round (55, 25), radius 6.5: the chord of its first half runs from (55, 18.5) to (61.5, 25), and a
        // mesher's grid point lay exactly on it (x − y = 36.5).
        var slot = new ProfileLoop(
        [
            new LineSegment(P(25, 18.5), P(55, 18.5)), new ArcSegment(P(55, 25), 6.5, -Math.PI / 2, Math.PI),
            new LineSegment(P(55, 31.5), P(25, 31.5)), new ArcSegment(P(25, 25), 6.5, Math.PI / 2, Math.PI),
        ]);
        slot.Winding(P(60.340844056908104, 23.840844056908104)).Should().Be(1);
        slot.Winding(P(58.25, 21.75)).Should().Be(1);
        slot.Winding(P(62, 25.5)).Should().Be(0);
    }

    [Fact]
    public void Grown_corners_turn_round_and_shrunk_ones_stay_sharp()
    {
        bool Never(Point2D _) => false;
        var (grown, origins, problem) = ProfileOffset.Grow(Rectangle(0, 0, 3, 2), [1, 1, 1, 1], Never, Tolerance);
        problem.Should().BeNull();
        origins.Should().Equal(0, ~0, 1, ~1, 2, ~2, 3, ~3);
        grown!.SignedArea.Should().BeApproximately(6 + 10 + Math.PI, 1e-12);

        var (shrunk, kept, _) = ProfileOffset.Grow(Rectangle(0, 0, 3, 2), [-0.5, -0.5, -0.5, -0.5], Never, Tolerance);
        kept.Should().Equal(0, 1, 2, 3);
        shrunk!.SignedArea.Should().BeApproximately(2, 1e-12);

        // A slot's ends stay half circles, one radius bigger; a smooth join gets no corner.
        var (slot, ends, _) = ProfileOffset.Grow(Slot(10, 1), [1, 1, 1, 1], Never, Tolerance);
        ends.Should().Equal(0, 1, 2, 3);
        slot!.SignedArea.Should().BeApproximately(40 + 4 * Math.PI, 1e-12);
    }

    [Fact]
    public void An_offset_that_uses_up_a_segment_is_refused()
    {
        bool Never(Point2D _) => false;
        ProfileOffset.Grow(Rectangle(0, 0, 3, 2), [-1, -1, -1, -1], Never, Tolerance).Problem.Should().Contain("used up");
        ProfileOffset.Grow(Circle(0, 0, 1), [-1], Never, Tolerance).Problem.Should().Contain("shrinks to nothing");
    }
}
