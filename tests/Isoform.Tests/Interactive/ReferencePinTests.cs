using System.Text.Json;
using System.Text.Json.Nodes;
using Isoform.Document;
using Isoform.Interactive;
using Isoform.Modeling;

namespace Isoform.Tests.Interactive;

/// <summary>
/// Pins as the playground keeps them: picked by face index on one result, kept by name, resolved after a real edit.
/// Example 0 is the plate (100 × 50 × 10) with a hole at (30, 25) and a fillet on the plate/z+ | plate/x+ edge.
/// </summary>
public class ReferencePinTests
{
    static readonly ModelingSettings Settings = new() { CellSize = 0.5, ChordTolerance = 0.05 };
    static readonly string Plate = CanonicalFormat.Write(Examples.All[0].Doc);

    const string SlotAcrossTop = """{"insert": {"id": "slot", "type": "block", "op": "cut", "origin": [60, -1, 5], "size": [5, 52, 10]}, "before": "fillet"}""";
    const string SlotThroughBack = """{"insert": {"id": "slot", "type": "block", "op": "cut", "origin": [60, 30, -1], "size": [10, 30, 12]}, "before": "fillet"}""";

    /// <summary>Regenerates <paramref name="document"/> with <paramref name="edits"/> and resolves the pins: id → pin reply.</summary>
    static Dictionary<string, JsonObject> Resolve(Session session, string document, string edits, params JsonObject[] pins)
    {
        session.Regenerate(document, edits, Settings).Error.Should().BeNull();
        var reply = JsonNode.Parse(ReferencePins.Json(new JsonArray([.. pins.Select(p => p.DeepClone())]).ToJsonString(), session[Session.Main]))!;
        reply["error"].Should().BeNull();
        reply["hash"]!.GetValue<string>().Should().Be(session[Session.Main]!.Hash);
        return reply["pins"]!.AsArray().Select(p => p!.AsObject()).ToDictionary(p => (string)p["id"]!);
    }

    /// <summary>Pins faces of the document's result by index, the way a click does, and returns the pins by name.</summary>
    static JsonObject[] Pin(Session session, string document, string edits, params (string Id, string Face)[] faces)
    {
        session.Regenerate(document, edits, Settings);
        var names = session[Session.Main]!.View!.Faces;
        var picked = faces.Select(f => new JsonObject { ["id"] = f.Id, ["face"] = Enumerable.Range(0, names.Count).First(i => names[i][0] == f.Face) }).ToArray();
        return Resolve(session, document, edits, picked).Values.Select(p => new JsonObject { ["id"] = p["id"]!.DeepClone() }.Merge(p["ref"]!.AsObject())).ToArray();
    }

    static string Status(JsonObject pin) => (string)pin["status"]!;
    static int[] Faces(JsonObject pin) => pin["faces"]!.AsArray().Select(f => (int)f!).ToArray();
    static string[] Names(Session session, JsonObject pin) => Faces(pin).Select(f => session[Session.Main]!.View!.Faces[f][0]).ToArray();

    [Fact]
    public void A_pinned_face_stays_resolved_through_a_parameter_edit_and_follows_its_new_index()
    {
        var session = new Session();
        var pins = Pin(session, Plate, "[]", ("top", "plate/z+"), ("wall", "hole/wall"));
        JsonNode.DeepEquals(pins[0], JsonNode.Parse("""{"id": "top", "face": "plate/z+"}""")).Should().BeTrue("a face in one piece is pinned by its name alone");

        var r = Resolve(session, Plate, """[{"set": "hole.diameter", "value": 12}]""", pins);

        Status(r["top"]).Should().Be("resolved");
        Names(session, r["top"]).Should().Equal("plate/z+");
        Status(r["wall"]).Should().Be("resolved");
    }

    [Fact]
    public void A_cut_across_the_face_splits_it_and_the_pin_lands_on_every_piece()
    {
        var session = new Session();
        var pins = Pin(session, Plate, "[]", ("top", "plate/z+"));

        var r = Resolve(session, Plate, $"[{SlotAcrossTop}]", pins);

        Status(r["top"]).Should().Be("split");
        Names(session, r["top"]).Should().Equal("plate/z+", "plate/z+");
    }

    [Fact]
    public void Removing_the_hole_makes_its_wall_gone()
    {
        var session = new Session();
        var pins = Pin(session, Plate, "[]", ("wall", "hole/wall"), ("top", "plate/z+"));

        var r = Resolve(session, Plate, """[{"remove": "hole"}]""", pins);

        Status(r["wall"]).Should().Be("gone");
        Faces(r["wall"]).Should().BeEmpty();
        Status(r["top"]).Should().Be("resolved");
    }

    [Fact]
    public void A_boss_flush_with_the_top_merges_the_faces()
    {
        var session = new Session();
        var pins = Pin(session, Plate, "[]", ("top", "plate/z+"));

        var r = Resolve(session, Plate, """[{"insert": {"id": "tab", "type": "block", "op": "join", "origin": [-20, 10, 0], "size": [30, 30, 10]}, "before": "fillet"}]""", pins);

        Status(r["top"]).Should().Be("merged");
        session[Session.Main]!.View!.Faces[Faces(r["top"]).Single()].Should().Contain(["plate/z+", "tab/z+"]);
    }

    [Fact]
    public void A_piece_pinned_on_a_split_face_is_ambiguous_once_a_second_cut_splits_it_again()
    {
        // The slot splits plate/y+ in two, so a pin on it remembers which piece by the faces around it.
        var session = new Session();
        var setup = $"[{SlotThroughBack}]";
        session.Regenerate(Plate, setup, Settings);
        var left = session[Session.Main]!.View!.Part!.FaceNear("plate/y+", 20, 50, 5)!.Value;
        var pins = Resolve(session, Plate, setup, new JsonObject { ["id"] = "back", ["face"] = left }).Values
            .Select(p => new JsonObject { ["id"] = "back" }.Merge(p["ref"]!.AsObject())).ToArray();
        pins[0]["signature"].Should().NotBeNull();

        Status(Resolve(session, Plate, $"[{SlotThroughBack}, {{\"set\": \"slot.origin\", \"value\": [50, 30, -1]}}]", pins)["back"])
            .Should().Be("resolved", "moving the slot keeps the pieces apart");
        var r = Resolve(session, Plate, $"[{SlotThroughBack}, {SlotThroughBack.Replace("\"slot\"", "\"slot2\"").Replace("[60, 30, -1]", "[20, 30, -1]")}]", pins);

        Status(r["back"]).Should().Be("ambiguous");
        Names(session, r["back"]).Should().HaveCount(3).And.OnlyContain(n => n == "plate/y+");
    }

    [Fact]
    public void A_pinned_edge_resolves_to_its_face_pairs()
    {
        var session = new Session();
        session.Regenerate(Plate, "[]", Settings);
        var names = session[Session.Main]!.View!.Faces;
        int Index(string name) => Enumerable.Range(0, names.Count).First(i => names[i][0] == name);
        var edge = new JsonObject { ["id"] = "rim", ["edge"] = new JsonArray(Index("plate/z+"), Index("hole/wall")) };

        var pinned = Resolve(session, Plate, "[]", edge)["rim"];
        JsonNode.DeepEquals(pinned["ref"], JsonNode.Parse("""{"edge": ["plate/z+", "hole/wall"]}""")).Should().BeTrue();
        var r = Resolve(session, Plate, $"[{SlotAcrossTop}]", new JsonObject { ["id"] = "rim" }.Merge(pinned["ref"]!.AsObject()))["rim"];

        Status(r).Should().Be("resolved");
        Names(session, r).Should().BeEquivalentTo("plate/z+", "hole/wall");
        r["edges"]!.AsArray().Should().ContainSingle();
    }

    [Fact]
    public void Bundle_pins_name_faces_by_occurrence_and_index_them_as_the_result_does()
    {
        var session = new Session();
        var bundle = CanonicalFormat.Write(Examples.Bundles[0].Bundle);

        var r = Resolve(session, bundle, "[]", new JsonObject { ["id"] = "top", ["face"] = "upper/plate/z+" }, new JsonObject { ["id"] = "x", ["face"] = "lower/nothing" });

        Status(r["top"]).Should().Be("resolved");
        Names(session, r["top"]).Should().Equal("upper/plate/z+");
        Status(r["x"]).Should().Be("gone");
    }

    [Fact]
    public void Without_a_result_or_with_pins_that_do_not_read_the_reply_is_an_error()
    {
        JsonNode.Parse(ReferencePins.Json("[]", null))!["error"].Should().NotBeNull();
        var session = new Session();
        session.Regenerate(Plate, "[]", Settings);
        JsonNode.Parse(ReferencePins.Json("""[{"id": "a", "face": 999}]""", session[Session.Main]))!["error"].Should().NotBeNull();
        JsonNode.Parse(ReferencePins.Json("""[{"face": "plate/z+"}]""", session[Session.Main]))!["error"].Should().NotBeNull();
    }

    [Fact]
    public void The_report_names_what_appeared_split_and_went()
    {
        var session = new Session();
        var before = ReferencePins.TopologyOf(session.Regenerate(Plate, "[]", Settings).View!);
        var after = ReferencePins.TopologyOf(session.Regenerate(Plate, $$"""[{{SlotAcrossTop}}, {"remove": "hole"}]""", Settings).View!);

        var report = ReferencePins.Report(before, after);

        report.Appeared.Should().Contain("slot/x-");
        report.Gone.Should().Equal("hole/wall");
        report.Changed.Should().Contain(("plate/z+", ReferenceStatus.Resolved, ReferenceStatus.Split));
    }
}

static class JsonObjectMerge
{
    /// <summary>Copies the fields of <paramref name="other"/> in; returns this object.</summary>
    public static JsonObject Merge(this JsonObject o, JsonObject other)
    {
        foreach (var (k, v) in other) o[k] = v?.DeepClone();
        return o;
    }
}
