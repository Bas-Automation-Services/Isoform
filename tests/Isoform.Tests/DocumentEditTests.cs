using System.Text.Json.Nodes;
using Isoform.Document;
using Isoform.Modeling;
using Isoform.Playground;

namespace Isoform.Tests;

/// <summary>Edits on assemblies and bundles, and edits that cannot be applied coming back as problems.</summary>
public class DocumentEditTests
{
    static readonly Bundle Brackets = Examples.Bundles[0].Bundle;

    static string Text(Bundle bundle) => CanonicalFormat.Write(bundle);

    static JsonNode[] Edits(string json) => JsonNode.Parse(json)!.AsArray().Select(e => e!).ToArray();

    static string Problem(string document, string edits)
    {
        var result = DocumentEdits.TryApply(document, edits);
        result.Text.Should().BeNull();
        return result.Problem!;
    }

    [Fact]
    public void Components_and_mates_are_edited_by_id()
    {
        var assembly = DocumentEdits.Apply(Brackets.Assembly, Edits("""
            [
              { "set": "components.upper.placement.origin", "value": [0, 25, 15] },
              { "set": "components.pin.placement.origin.2", "value": -12 },
              { "set": "components.pin.parameters.length", "value": 30 },
              { "set": "components.lower.grounded", "value": true },
              { "insert": { "id": "spare", "part": "pin.json", "placement": { "origin": [50, 0, 0], "x": [1, 0, 0], "normal": [0, 0, 1] } }, "before": "pin" },
              { "insert": { "id": "seat", "kind": "mate", "a": "upper/plate/z+", "b": "lower/plate/z+" } },
              { "set": "mates.seat.offset", "value": 2 }
            ]
            """));

        assembly.Components.Select(c => c.Id).Should().Equal("lower", "upper", "spare", "pin");
        assembly.Components[1].Placement.Origin.Should().Equal(0, 25, 15);
        assembly.Components[3].Placement.Origin.Should().Equal(10, 10, -12);
        assembly.Components[3].Parameters.Should().Equal(new Dictionary<string, string> { ["length"] = "30" });
        assembly.Components[0].Grounded.Should().BeTrue();
        assembly.Mates.Should().Equal(new Mate("seat", MateKind.Mate, "upper/plate/z+", "lower/plate/z+", 2));

        var back = DocumentEdits.Apply(assembly, Edits("""
            [{ "remove": "components.spare" }, { "remove": "components.pin.parameters.length" }, { "remove": "mates.seat" }, { "remove": "components.lower.grounded" }, { "set": "components.upper.placement.origin.1", "value": 20 }, { "set": "components.pin.placement.origin.2", "value": -9 }]
            """));
        CanonicalFormat.Write(back).Should().Be(CanonicalFormat.Write(Brackets.Assembly), "the edits undo each other exactly");
    }

    [Fact]
    public void A_bundle_edit_changes_its_assembly_or_the_document_it_names()
    {
        var bundle = DocumentEdits.Apply(Brackets, Edits("""
            [
              { "document": "bracket.json", "set": "plate.size.0", "value": 50 },
              { "set": "components.upper.placement.origin.2", "value": 16 },
              { "document": "spacer.json", "set": "", "value": { "format": "isoform/0", "units": "mm", "features": [{ "id": "ring", "type": "block", "op": "new", "origin": [0, 0, 0], "size": [8, 8, 2] }] } }
            ]
            """));

        ((Block)CanonicalFormat.Read(bundle.Documents["bracket.json"]).Features[0]).Size.Should().Equal(50, 20, 5);
        CanonicalFormat.Read(bundle.Documents["pin.json"]).Should().BeEquivalentTo(CanonicalFormat.Read(Brackets.Documents["pin.json"]));
        bundle.Assembly.Components[1].Placement.Origin.Should().Equal(0, 20, 16);
        bundle.Documents.Keys.Should().BeEquivalentTo("bracket.json", "pin.json", "spacer.json");

        var dropped = DocumentEdits.Apply(bundle, Edits("""[{ "document": "spacer.json", "remove": "" }]"""));
        dropped.Documents.Keys.Should().BeEquivalentTo("bracket.json", "pin.json");
    }

    [Fact]
    public void Part_edits_replace_features_and_drop_optional_fields()
    {
        var doc = DocumentEdits.Apply(EditSuite.Base, Edits("""
            [
              { "set": "hole", "value": { "id": "hole", "type": "hole", "center": [20, 20], "diameter": 8 } },
              { "set": "plate.blend", "value": 0 },
              { "remove": "fillet.edge" },
              { "set": "fillet.loop", "value": "hole/wall" }
            ]
            """));

        doc.Features.OfType<Hole>().Single().Center.Should().Equal(20, 20);
        doc.Features.OfType<Fillet>().Single().Should().Match<Fillet>(f => f.Loop == "hole/wall" && f.Edge.Length == 0);
        CanonicalFormat.Write(doc).Should().NotContain("blend", "a zero blend is a default, not an unknown field");
    }

    [Fact]
    public void Edits_that_cannot_apply_come_back_as_problems()
    {
        var part = CanonicalFormat.Write(EditSuite.Base);
        var bundle = Text(Brackets);

        Problem(part, """[{ "set": "hole.diamter", "value": 60 }]""").Should().Contain("'hole.diamter' is not a field");
        Problem(part, """[{ "set": "slot.diameter", "value": 60 }]""").Should().Contain("no feature 'slot'");
        Problem(part, """[{ "set": "plate.size.3", "value": 1 }]""").Should().Contain("no entry '3'");
        Problem(part, """[{ "set": "hole.diameter", "value": "wide" }]""").Should().Contain("does not read");
        Problem(part, """[{ "set": "hole.diameter.x", "value": 1 }]""").Should().Contain("has no field 'x'");
        Problem(part, """[{ "set": "parameters.width", "value": "120 / 2" }]""").Should().Contain("'='");
        Problem(part, """[{ "insert": { "type": "hole" } }]""").Should().Contain("with an id");
        Problem(part, """[{ "insert": { "id": "h2", "type": "hole", "center": [1, 1], "diameter": 2 }, "before": "nope" }]""").Should().Contain("no feature 'nope'");
        Problem(part, """[{ "remove": "" }]""").Should().Contain("removed whole");
        Problem(part, """[{ "move": "hole" }]""").Should().Contain("set, an insert or a remove");
        Problem(part, """[{ "set": "hole.diameter" }]""").Should().Contain("needs a value");
        Problem(part, """[{ "set": 3, "value": 1 }]""").Should().Contain("'set' takes a string");
        Problem(part, """[42]""").Should().Contain("JSON object");
        Problem(part, """[{ "set": """).Should().Contain("not JSON");
        Problem("{ not json", "[]").Should().Contain("does not read");
        Problem("""{ "format": "isoform/9" }""", "[]").Should().Contain("Unsupported format");

        Problem(bundle, """[{ "set": "components.left.placement.origin", "value": [0, 0, 0] }]""").Should().Contain("no component 'left'");
        Problem(bundle, """[{ "set": "components.upper.placment.origin", "value": [0, 0, 0] }]""").Should().Contain("not a field");
        Problem(bundle, """[{ "set": "upper.placement", "value": 1 }]""").Should().Contain("starts with components.<id> or mates.<id>");
        Problem(bundle, """[{ "set": "mates.seat.offset", "value": 1 }]""").Should().Contain("no mate 'seat'");
        Problem(bundle, """[{ "insert": { "id": "x" } }]""").Should().Contain("a component (with a part) or a mate");
        Problem(bundle, """[{ "set": "components.pin.placement.origin", "value": [1, 2] }]""").Should().Contain("Expected 3 numbers");
        Problem(bundle, """[{ "document": "nut.json", "set": "plate.size.0", "value": 1 }]""").Should().Contain("no document 'nut.json'");

        var second = DocumentEdits.TryApply(part, """[{ "set": "hole.diameter", "value": 12 }, { "set": "hole.radius", "value": 6 }]""");
        second.FailedEdit.Should().Be(1);
        second.Problem.Should().StartWith("edit 2 ");
        FluentActions.Invoking(() => DocumentEdits.Apply(EditSuite.Base, Edits("""[{ "remove": "slot" }]""")))
            .Should().Throw<EditException>().Which.Index.Should().Be(0);
    }

    [Fact]
    public void A_good_edit_gives_canonical_text_and_its_hash()
    {
        var result = DocumentEdits.TryApply(CanonicalFormat.Write(EditSuite.Base), """{ "set": "hole.diameter", "value": 12 }""");
        var expected = DocumentEdits.Apply(EditSuite.Base, Edits("""[{ "set": "hole.diameter", "value": 12 }]"""));

        result.Problem.Should().BeNull();
        result.Text.Should().Be(CanonicalFormat.Write(expected));
        result.Hash.Should().Be(CanonicalFormat.Hash(expected));
    }

    [Fact]
    public void Random_edits_never_throw()
    {
        var rng = new Random(7);
        string[] documents = [CanonicalFormat.Write(EditSuite.Base), Text(Brackets), CanonicalFormat.Write(Brackets.Assembly), .. Examples.All.Select(e => CanonicalFormat.Write(e.Doc))];
        string[] heads = ["plate", "hole", "fillet", "parameters", "components", "mates", "components.pin", "components.upper", "mates.x", "", "bracket", "nope"];
        string[] tails = ["size", "origin", "0", "1", "3", "-1", "center", "diameter", "placement", "placement.origin", "parameters.length", "profile.edges.1.to", "x", "edge", "loop", "type", "id"];
        JsonNode?[] values = [null, 0, 1, -5, 1e300, "=1+", "=width", "abc", true, new JsonArray(1, 2), new JsonArray(1, 2, 3), new JsonObject(), new JsonObject { ["id"] = "q" }, new JsonArray()];

        for (var k = 0; k < 3000; k++)
        {
            var path = string.Join('.', new[] { heads[rng.Next(heads.Length)] }.Concat(Enumerable.Range(0, rng.Next(3)).Select(_ => tails[rng.Next(tails.Length)]))).Trim('.');
            var value = values[rng.Next(values.Length)]?.DeepClone();
            JsonObject edit = rng.Next(4) switch
            {
                0 => new() { ["set"] = path, ["value"] = value },
                1 => new() { ["insert"] = value, ["before"] = heads[rng.Next(heads.Length)] },
                2 => new() { ["remove"] = path },
                _ => new() { ["document"] = rng.Next(2) == 0 ? "bracket.json" : "pin.json", ["set"] = path, ["value"] = value },
            };
            var document = documents[rng.Next(documents.Length)];
            var result = DocumentEdits.TryApply(document, [edit]);
            (result.Text is null).Should().Be(result.Problem is not null, edit.ToJsonString());
        }
    }

    [Theory]
    [InlineData("parameters")]
    [InlineData("a.b")]
    public void Feature_ids_must_be_reachable_by_edit_paths(string id)
    {
        var doc = new PartDocument([new Block(id, BooleanOp.New, [0, 0, 0], [10, 10, 10])]);

        DocumentRules.FirstProblem(doc, 1e-6).Should().Match<Diagnostic>(d => d.Code == Diagnostic.InvalidParameter && d.FeatureId == id && d.Message.Contains("edit paths"));
    }
}
