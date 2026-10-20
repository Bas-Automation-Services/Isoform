using System.Text.Json.Nodes;
using Isoform.Document;

namespace Isoform.Tests;

public class DocumentTests
{
    [Fact]
    public void Canonical_text_round_trips_byte_for_byte()
    {
        var text = CanonicalFormat.Write(EditSuite.Base);

        CanonicalFormat.Write(CanonicalFormat.Read(text)).Should().Be(text);
    }

    [Fact]
    public void Doubles_read_back_exactly()
    {
        var doc = new PartDocument([new Hole("h", [0.1 + 0.2, 1.0 / 3.0], 9.999999999999998)]);

        var back = (Hole)CanonicalFormat.Read(CanonicalFormat.Write(doc)).Features[0];

        back.Center.Should().Equal(0.1 + 0.2, 1.0 / 3.0);
        back.Diameter.Should().Be(9.999999999999998);
    }

    [Fact]
    public void Negative_zero_hashes_like_zero()
    {
        var a = new PartDocument([new Block("b", BooleanOp.New, [0, 0, 0], [1, 1, 1])]);
        var b = new PartDocument([new Block("b", BooleanOp.New, [-0.0, 0, 0], [1, 1, 1])]);

        CanonicalFormat.Hash(b).Should().Be(CanonicalFormat.Hash(a));
    }

    [Fact]
    public void Hash_of_the_base_part_is_stable()
    {
        // Golden value: if this changes, every stored document's identity changes with it.
        CanonicalFormat.Hash(EditSuite.Base).Should().Be(GoldenBaseHash);
    }

    [Fact]
    public void Text_is_one_value_per_line_with_lf_newlines()
    {
        var text = CanonicalFormat.Write(EditSuite.Base);

        text.Should().NotContain("\r");
        text.Should().Contain("\"diameter\": 10\n");
    }

    [Fact]
    public void Edits_set_insert_and_remove_by_feature_id()
    {
        var edited = DocumentEdits.Apply(EditSuite.Base,
        [
            JsonNode.Parse("""{ "set": "hole.diameter", "value": 12 }""")!,
            JsonNode.Parse("""{ "insert": { "id": "cut", "type": "plane-cut", "point": [0, 0, 0], "normal": [1, 0, 0] }, "before": "fillet" }""")!,
            JsonNode.Parse("""{ "remove": "plate" }""")!,
        ]);

        edited.Features.Select(f => f.Id).Should().Equal("hole", "cut", "fillet");
        ((Hole)edited["hole"]).Diameter.Should().Be(12);
    }

    const string GoldenBaseHash = "d98dc59be6660cedd83b944db873edbbb7244c422ff858ddf6ad5a774994d641";
}
