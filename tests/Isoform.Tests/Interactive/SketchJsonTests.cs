using System.Text.Json.Nodes;
using Isoform.Document;
using Isoform.Interactive;
using Isoform.Modeling;

namespace Isoform.Tests.Interactive;

/// <summary>The page's sketch ops: a face's plane to sketch on, and a sketch solved with a point dragged.</summary>
public class SketchJsonTests
{
    static readonly ModelingSettings Settings = new() { CellSize = 0.5, ChordTolerance = 0.05 };

    static readonly string Plate = CanonicalFormat.Write(new PartDocument([new Block("plate", BooleanOp.New, [0, 0, 0], [100, 50, 10]), new Hole("hole", [30, 25], 10)]));

    static double[] Numbers(JsonNode? n) => [.. n!.AsArray().Select(v => (double)v!)];

    [Fact]
    public void A_face_frame_is_the_plane_a_sketch_on_it_takes()
    {
        var session = new Session();
        var r = session.Regenerate(Plate, "[]", Settings);

        var top = JsonNode.Parse(SketchJson.FaceFrame(r, "plate/z+"))!;
        Numbers(top["normal"]).Should().Equal(0, 0, 1);
        Numbers(top["origin"]).Should().Equal(0, 0, 10);
        Numbers(top["x"]).Should().Equal(1, 0, 0);

        var bottom = JsonNode.Parse(SketchJson.FaceFrame(r, "plate/z-"))!;
        Numbers(bottom["normal"]).Should().Equal(0, 0, -1);
        Numbers(bottom["y"]).Should().Equal(0, -1, 0);

        JsonNode.Parse(SketchJson.FaceFrame(r, "hole/wall"))!["error"]!.GetValue<string>().Should().Contain("not a flat face");
    }

    [Fact]
    public void A_sketch_solves_with_a_drag_and_a_bad_one_answers_an_error()
    {
        var doc = CanonicalFormat.Write(new PartDocument(
        [
            new Block("plate", BooleanOp.New, [0, 0, 0], [100, 50, 10]),
            new Sketch("tri", new SketchPlacement(Face: "plate/z+"), new Dictionary<string, double[]> { ["a"] = [10, 10], ["b"] = [30, 10], ["c"] = [20, 30] },
                [new SketchLine("ab", "a", "b"), new SketchLine("bc", "b", "c"), new SketchLine("ca", "c", "a")], [new("fix", ["a"]), new("fix", ["b"])]),
        ]));

        var r = JsonNode.Parse(SketchJson.Write(doc, "tri", """{"point": "c", "to": [25, 40]}"""))!;
        Numbers(r["points"]!["c"]).Should().Equal(25, 40);
        r["regions"]!.AsArray().Should().ContainSingle();
        ((Sketch)CanonicalFormat.Read(r["settled"]!["text"]!.GetValue<string>()).Features[1]).Points["c"].Should().Equal(25, 40);

        JsonNode.Parse(SketchJson.Write(doc, "plate", ""))!["error"]!.GetValue<string>().Should().Contain("not a sketch");
    }
}
