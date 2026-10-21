using System.Text.Json.Nodes;
using Isoform.Document;
using Isoform.Interactive;
using Isoform.Modeling;

namespace Isoform.Tests.Interactive;

/// <summary>The page's file ops: STEP of a slot's last regeneration, DXF of a sketch or of a flat face.</summary>
public class ExportJsonTests
{
    static readonly ModelingSettings Settings = new() { CellSize = 0.5, ChordTolerance = 0.05 };

    static readonly Block Plate = new("plate", BooleanOp.New, [0, 0, 0], [100, 50, 10]);

    static Regeneration Regenerate(Feature[] features) => new Session().Regenerate(CanonicalFormat.Write(new PartDocument(features)), "[]", Settings);

    [Fact]
    public void Step_writes_a_slots_part_or_assembly()
    {
        var part = JsonNode.Parse(ExportJson.Step(Regenerate([Plate, new Hole("hole", [30, 25], 10)]), "plate"))!;
        part["text"]!.GetValue<string>().Should().StartWith("ISO-10303-21;").And.Contain("PRODUCT('plate'");
        ((int)part["solids"]!, (int)part["faces"]!).Should().Be((1, 7));

        var (_, bundle) = Examples.Bundles.Single();
        var assembly = JsonNode.Parse(ExportJson.Step(new Session().Regenerate(CanonicalFormat.Write(bundle), "[]", Settings), "brackets"))!;
        ((int)assembly["instances"]!).Should().Be(3);

        JsonNode.Parse(ExportJson.Step(null, "part"))!["error"]!.GetValue<string>().Should().Contain("nothing has regenerated");
    }

    [Fact]
    public void Step_names_the_faces_it_cannot_write_exactly()
    {
        var r = JsonNode.Parse(ExportJson.Step(Regenerate([Plate, new Block("boss", BooleanOp.Join, [40, 15, 10], [20, 20, 10], Blend: 3)]), "part"))!;

        r["text"].Should().BeNull();
        r["error"]!.GetValue<string>().Should().Contain("boss/blend");
        r["unsupported"]!.AsArray().Select(f => (string)f!).Should().Equal("boss/blend");
    }

    [Fact]
    public void Dxf_writes_a_sketch_or_a_flat_face()
    {
        var doc = CanonicalFormat.Write(new PartDocument(
        [
            Plate,
            new Sketch("spot", new SketchPlacement(Face: "plate/z+"), new Dictionary<string, double[]> { ["c"] = [20, 20] }, [new SketchCircle("rim", "c", 5)], []),
            new Hole("hole", [70, 25], 10),
        ]));
        var r = new Session().Regenerate(doc, "[]", Settings);

        var sketch = JsonNode.Parse(ExportJson.Dxf(r, doc, "spot", ""))!;
        ((int)sketch["entities"]!).Should().Be(1);
        sketch["text"]!.GetValue<string>().Should().Contain("CIRCLE").And.EndWith("EOF\r\n");

        var face = JsonNode.Parse(ExportJson.Dxf(r, doc, "", "plate/z+"))!;
        ((int)face["entities"]!, (int)face["approximated"]!).Should().Be((5, 0));

        JsonNode.Parse(ExportJson.Dxf(r, doc, "plate", ""))!["error"]!.GetValue<string>().Should().Contain("not a sketch");
        JsonNode.Parse(ExportJson.Dxf(r, doc, "", "hole/wall"))!["error"]!.GetValue<string>().Should().Contain("not a flat face");
    }
}
