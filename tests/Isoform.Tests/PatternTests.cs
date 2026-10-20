using System.Text.Json.Nodes;
using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Modeling;

namespace Isoform.Tests;

/// <summary>K4: linear, circular and mirror patterns place exact copies that keep their names.</summary>
public class PatternTests
{
    static readonly Block Plate = new("plate", BooleanOp.New, [0, 0, 0], [100, 50, 10]);
    static readonly FrepModeler Modeler = new(new ModelingSettings { CellSize = 0.5 });

    static PartDocument HoleRow(int count) => new(
    [
        Plate,
        new Hole("h", [10, 25], 6),
        new Pattern("row", ["h"], new LinearMotion([1, 0, 0], 20, count)),
    ]);

    [Fact]
    public void Linear_pattern_copies_a_hole_with_numbered_names()
    {
        var result = Modeler.Regenerate(HoleRow(5));

        result.Diagnostics.Should().BeEmpty();
        var walls = result.Topology!.Faces.SelectMany(f => f.Sources).Where(s => s.EndsWith("h/wall")).Order().ToList();
        walls.Should().Equal("h/wall", "row/1:h/wall", "row/2:h/wall", "row/3:h/wall", "row/4:h/wall");
        result.Part.Surfaces.Single(s => s.Source == "row/4:h/wall").Origin.X.Should().BeApproximately(90, 1e-12);
    }

    [Fact]
    public void Copies_keep_their_names_when_count_and_spacing_change()
    {
        var before = Modeler.Regenerate(HoleRow(4)).Topology!;
        var edits = new[]
        {
            JsonNode.Parse("""{ "set": "row.linear.count", "value": 5 }""")!,
            JsonNode.Parse("""{ "set": "row.linear.spacing", "value": 18 }""")!,
        };
        var after = Modeler.Regenerate(DocumentEdits.Apply(HoleRow(4), edits)).Topology!;

        foreach (var source in before.Faces.SelectMany(f => f.Sources))
            ReferenceResolver.Resolve(new FaceReference(source, source), after).Status.Should().Be(ReferenceStatus.Resolved, source);
        ReferenceResolver.Resolve(new EdgeReference("e", "plate/z+", "row/3:h/wall"), after).Status.Should().Be(ReferenceStatus.Resolved);
        after.Faces.Count.Should().Be(before.Faces.Count + 1);
    }

    [Fact]
    public void Circular_pattern_spreads_copies_evenly_round_the_axis()
    {
        var doc = new PartDocument(
        [
            Plate,
            new Hole("bolt", [65, 25], 4),
            new Pattern("circle", ["bolt"], new CircularMotion([50, 25, 0], [0, 0, 1], 6)),
        ]);

        var result = Modeler.Regenerate(doc);

        result.Diagnostics.Should().BeEmpty();
        result.Topology!.Faces.Should().HaveCount(6 + 6);
        for (var k = 1; k < 6; k++)
        {
            var centre = result.Part.Surfaces.Single(s => s.Source == $"circle/{k}:bolt/wall").Origin;
            var a = k * Math.PI / 3;
            (centre - new Point3D(50 + 15 * Math.Cos(a), 25 + 15 * Math.Sin(a), 0)).Length.Should().BeLessThan(1e-12);
        }
    }

    [Fact]
    public void Mirror_makes_the_part_exactly_symmetric()
    {
        // An L-shaped boss and a quarter-turn revolve on the left; the mirror in x = 50 copies both to the right.
        var doc = new PartDocument(
        [
            Plate,
            new Extrude("boss", BooleanOp.Join, new SketchPlane([0, 0, 10], [1, 0, 0], [0, 0, 1]), new Profile([5, 5],
            [
                new ProfileEdge("a", [25, 5]), new ProfileEdge("b", [25, 10]), new ProfileEdge("c", [10, 10]),
                new ProfileEdge("d", [10, 30]), new ProfileEdge("e", [5, 30]), new ProfileEdge("f", [5, 5]),
            ]), 0, 6),
            new Revolve("arc", BooleanOp.Join, new SketchPlane([30, 35, 10], [1, 0, 0], [0, -1, 0]), new Profile([2, 0],
            [
                new ProfileEdge("low", [6, 0]), new ProfileEdge("out", [6, 3]), new ProfileEdge("high", [2, 3]), new ProfileEdge("in", [2, 0]),
            ]), 120),
            new Pattern("mirror", ["boss", "arc"], new MirrorMotion([50, 0, 0], [1, 0, 0])),
        ]);
        var field = new Compiler(new ModelingSettings()).Compile(doc).Field!;
        var rng = new Random(5);

        for (var i = 0; i < 100_000; i++)
        {
            var p = new Point3D(rng.NextDouble() * 120 - 10, rng.NextDouble() * 70 - 10, rng.NextDouble() * 30 - 5);
            field.Value(new Point3D(100 - p.X, p.Y, p.Z)).Should().BeApproximately(field.Value(p), 1e-12);
        }
        var result = Modeler.Regenerate(doc);
        result.Diagnostics.Should().BeEmpty();
        result.Mesh!.IsWatertight().Should().BeTrue();
        result.Topology!.Faces.SelectMany(f => f.Sources).Should().Contain(["mirror/1:boss/d", "mirror/1:arc/end"]);
    }

    [Fact]
    public void Patterns_round_trip_through_the_canonical_format()
    {
        PartDocument[] docs =
        [
            HoleRow(3),
            new([Plate, new Hole("b", [65, 25], 4), new Pattern("c", ["b"], new CircularMotion([50, 25, 0], [0, 0, 1], 4, 90))]),
            new([Plate, new Hole("b", [65, 25], 4), new Pattern("m", ["b"], new MirrorMotion([50, 0, 0], [1, 0, 0]))]),
        ];
        foreach (var doc in docs)
        {
            var text = CanonicalFormat.Write(doc);
            CanonicalFormat.Write(CanonicalFormat.Read(text)).Should().Be(text);
        }
    }

    [Theory]
    [InlineData("later", "earlier features")]
    [InlineData("fillet", "cannot be patterned")]
    [InlineData("count", "count must be 2 to 1000")]
    public void Bad_patterns_are_rejected(string kind, string message)
    {
        Feature[] features = kind switch
        {
            "later" => [Plate, new Pattern("p", ["h"], new LinearMotion([1, 0, 0], 20, 3)), new Hole("h", [10, 25], 6)],
            "fillet" => [Plate, new Fillet("f", ["plate/z+", "plate/x+"], 2), new Pattern("p", ["f"], new LinearMotion([1, 0, 0], 20, 3))],
            _ => [Plate, new Hole("h", [10, 25], 6), new Pattern("p", ["h"], new LinearMotion([1, 0, 0], 20, 1))],
        };

        var result = Modeler.Regenerate(new PartDocument(features));

        result.Diagnostics.Should().ContainSingle().Which.Message.Should().Contain(message);
    }
}
