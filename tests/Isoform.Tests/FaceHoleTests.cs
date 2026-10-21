using Isoform.Document;
using Isoform.Modeling;

namespace Isoform.Tests;

/// <summary>
/// Holes on any flat face: through or blind, at a centre in the face's sketch coordinates or at sketch points, named
/// after the point; a plain hole along Z keeps its text and hash.
/// </summary>
public class FaceHoleTests
{
    static readonly ModelingSettings Settings = new();

    static readonly Block Plate = new("plate", BooleanOp.New, [0, 0, 0], [100, 50, 10]);

    static FrepResult Regenerate(params Feature[] features) => new FrepModeler(Settings).Regenerate(new PartDocument([Plate, .. features]));

    static double Volume(FrepResult r) => MassProperties.Of(r.Part.Field!, r.Mesh!).Volume;

    static IEnumerable<string> Names(FrepResult r) => r.Topology!.Faces.SelectMany(f => f.Sources);

    static PartDocument SideHoles() => new([Plate, new Hole("side", [25, 5], 4) { On = "plate/x+" }]);

    [Fact]
    public void A_hole_through_a_side_face_runs_along_its_normal()
    {
        // On plate/x+ the sketch x is world y and its y world z: [25, 5] is y = 25, z = 5.
        var r = new FrepModeler(Settings).Regenerate(SideHoles());

        r.Diagnostics.Should().BeEmpty();
        var wall = r.Part.Surfaces.Single(s => s.Source == "side/wall");
        (wall.Origin.Y, wall.Origin.Z).Should().Be((25, 5));
        Math.Abs(wall.Axis.X).Should().Be(1);
        Volume(r).Should().BeApproximately(100 * 50 * 10 - Math.PI * 4 * 100, 0.5);
        r.Topology!.Faces.Should().HaveCount(7);
    }

    [Fact]
    public void A_blind_hole_has_a_floor()
    {
        var r = Regenerate(new Hole("pocket", [20, 20], 6) { On = "plate/z+", Depth = 4 });

        r.Diagnostics.Should().BeEmpty();
        Names(r).Should().Contain(["pocket/wall", "pocket/floor"]);
        (r.Topology!.Faces.Count, r.Topology.Edges.Count, r.Topology.Vertices.Count).Should().Be((8, 14, 8));
        Volume(r).Should().BeApproximately(100 * 50 * 10 - Math.PI * 9 * 4, 0.1);
        r.Part.Surfaces.Single(s => s.Source == "pocket/floor").Origin.Z.Should().BeApproximately(6, 1e-12);
    }

    [Fact]
    public void Holes_at_sketch_points_are_named_after_the_points()
    {
        var spots = new Sketch("spots", new SketchPlacement(Face: "plate/z+"),
            new Dictionary<string, double[]> { ["near"] = [20, 25], ["far"] = [80, 25] }, [], [new("fix", ["near"]), new("horizontal-distance", ["near", "far"], 60)]);
        var r = Regenerate(spots, new Hole("holes", [], 5) { Sketch = "spots", Points = ["near", "far"], Depth = 3 });

        r.Diagnostics.Should().BeEmpty();
        Names(r).Should().Contain(["holes/near:wall", "holes/near:floor", "holes/far:wall", "holes/far:floor"]);
        r.Part.Surfaces.Single(s => s.Source == "holes/far:wall").Origin.X.Should().BeApproximately(80, 1e-12);
        Volume(r).Should().BeApproximately(100 * 50 * 10 - 2 * Math.PI * 6.25 * 3, 0.1);
    }

    [Fact]
    public void A_side_hole_can_be_patterned()
    {
        var r = Regenerate(new Hole("side", [10, 5], 4) { On = "plate/x+" }, new Pattern("row", ["side"], new LinearMotion([0, 1, 0], 15, 3)));

        r.Diagnostics.Should().BeEmpty();
        Names(r).Should().Contain(["side/wall", "row/1:side/wall", "row/2:side/wall"]);
        Volume(r).Should().BeApproximately(100 * 50 * 10 - 3 * Math.PI * 4 * 100, 1.5);
    }

    [Fact]
    public void A_face_hole_on_a_face_the_part_lacks_is_reported()
    {
        var r = Regenerate(new Hole("side", [10, 5], 4) { On = "plate/w-" });

        r.Diagnostics.Should().ContainSingle(d => d.Code == Diagnostic.SketchPlaneLost && d.FeatureId == "side");
    }

    [Fact]
    public void A_plain_hole_keeps_its_text()
    {
        CanonicalFormat.Write(new PartDocument([Plate, new Hole("h", [30, 25], 10)])).Should().NotContain("depth").And.NotContain("\"on\"");
        var side = CanonicalFormat.Write(SideHoles());
        CanonicalFormat.Write(CanonicalFormat.Read(side)).Should().Be(side);
        side.Should().Contain("\"on\": \"plate/x+\"");
    }
}
