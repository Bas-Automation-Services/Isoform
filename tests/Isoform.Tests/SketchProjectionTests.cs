using System.Text.Json.Nodes;
using Isoform.Document;
using Isoform.Interactive;
using Isoform.Modeling;
using Isoform.Sketching;

namespace Isoform.Tests;

/// <summary>
/// Part edges projected into a sketch (docs/kernel-gaps-plan.md §4 item 2): as a copy, exact lines, arcs and circles
/// from the two faces' closed-form intersection, cut to the edge; linked, held on the projection at every regeneration,
/// following the face when it moves, still there when it splits, reported when it is gone. Ellipses are refused.
/// </summary>
public class SketchProjectionTests
{
    const double Exact = 1e-9;

    // A 100 × 50 × 10 plate with a 10 mm hole at (30, 25), and what comes after it.
    static string Doc(string sketch, string more = "", string plate = "[100, 50, 10]", string before = "") => $$"""
        {"format": "isoform/0", "units": "mm", "features": [
          {"id": "plate", "type": "block", "op": "new", "origin": [0, 0, 0], "size": {{plate}}},
          {"id": "hole", "type": "hole", "center": [30, 25], "diameter": 10}{{before}},
          {{sketch}}{{more}}]}
        """;

    static string Sketch(string on = "plate/z+", string points = "", string entities = "") =>
        $$"""{"id": "s", "type": "sketch", "on": {"face": "{{on}}"}, "points": {{{points}}}, "entities": [{{entities}}], "constraints": []}""";

    static JsonObject Project(string doc, string a, string b, double[]? near = null, bool linked = false) =>
        SketchJson.Edit(doc, "s", "project", faces: [a, b], near: near, linked: linked);

    static Sketch Settled(JsonObject r) => (Sketch)CanonicalFormat.Read((string)r["settled"]!["text"]!)["s"];

    static (double X, double Y) At(Sketch s, string p) => (s.Points[p][0], s.Points[p][1]);

    static string Error(Action a) => a.Should().Throw<ArgumentException>().Which.Message;

    [Fact]
    public void A_plate_edge_projects_as_a_line_between_its_exact_corners()
    {
        var s = Settled(Project(Doc(Sketch()), "plate/z+", "plate/x+"));

        var line = s.Entities.Should().ContainSingle().Which.Should().BeOfType<SketchLine>().Which;
        line.Id.Should().Be("projected");
        line.Construction.Should().BeFalse();
        line.Project.Should().BeNull("a copy keeps no link");
        var ends = new[] { At(s, line.From), At(s, line.To) }.OrderBy(p => p.Y).ToList();
        ends[0].X.Should().BeApproximately(100, Exact);
        ends[0].Y.Should().BeApproximately(0, Exact);
        ends[1].X.Should().BeApproximately(100, Exact);
        ends[1].Y.Should().BeApproximately(50, Exact);
    }

    [Fact]
    public void An_edge_below_the_sketch_plane_projects_along_its_normal()
    {
        var s = Settled(Project(Doc(Sketch()), "plate/z-", "plate/y-"));

        var line = (SketchLine)s.Entities.Single();
        new[] { At(s, line.From), At(s, line.To) }.Select(p => p.Y).Should().AllSatisfy(y => y.Should().BeApproximately(0, Exact));
        new[] { At(s, line.From).X, At(s, line.To).X }.Order().Should().Equal([0, 100], (a, b) => Math.Abs(a - b) <= Exact);
    }

    [Fact]
    public void A_hole_rim_projects_as_an_exact_circle_on_a_parallel_sketch()
    {
        var r = Project(Doc(Sketch(on: "plate/z-")), "plate/z+", "hole/wall");
        var s = Settled(r);

        var circle = s.Entities.Should().ContainSingle().Which.Should().BeOfType<SketchCircle>().Which;
        circle.Radius.Should().BeApproximately(5, Exact);
        // A sketch on the bottom face looks up: its y runs along world −y.
        var c = At(s, circle.Center);
        c.X.Should().BeApproximately(30, Exact);
        c.Y.Should().BeApproximately(-25, Exact);
        r["added"]!.AsArray().Select(n => (string)n!).Should().Equal("projected", "projected-centre");
    }

    [Fact]
    public void A_hole_across_the_plate_edge_projects_as_an_arc_cut_where_it_meets_the_side()
    {
        var doc = Doc(Sketch()).Replace("\"center\": [30, 25]", "\"center\": [100, 25]").Replace("\"diameter\": 10", "\"diameter\": 20");
        var s = Settled(Project(doc, "plate/z+", "hole/wall"));

        var arc = s.Entities.Should().ContainSingle().Which.Should().BeOfType<SketchArc>().Which;
        var (c, from, to) = (At(s, arc.Center), At(s, arc.From), At(s, arc.To));
        c.X.Should().BeApproximately(100, Exact);
        c.Y.Should().BeApproximately(25, Exact);
        // Counter-clockwise round the centre through (90, 25): from the top end down to the bottom one.
        from.X.Should().BeApproximately(100, Exact);
        from.Y.Should().BeApproximately(35, Exact);
        to.X.Should().BeApproximately(100, Exact);
        to.Y.Should().BeApproximately(15, Exact);
    }

    [Fact]
    public void Edges_projected_one_after_another_share_their_corners_and_close_a_region()
    {
        var doc = Doc(Sketch());
        foreach (var side in new[] { "plate/x+", "plate/y+", "plate/x-", "plate/y-" })
            doc = (string)Project(doc, "plate/z+", side)["settled"]!["text"]!;
        var r = SketchJson.Solve(doc, "s");

        var s = Settled(r);
        s.Entities.Should().HaveCount(4);
        s.Points.Should().HaveCount(4, "each corner is one point");
        r["regions"]!.AsArray().Should().ContainSingle();
    }

    [Fact]
    public void Near_picks_one_piece_of_an_edge_a_slot_cuts_in_two()
    {
        var slot = """, {"id": "slot", "type": "block", "op": "cut", "origin": [40, -1, -1], "size": [10, 52, 20]}""";
        var doc = Doc(Sketch(on: "plate/z-"), before: slot);

        var both = Settled(Project(doc, "plate/z-", "plate/y-"));
        both.Entities.Should().HaveCount(2);
        var near = Settled(Project(doc, "plate/z-", "plate/y-", near: [80, 0, 0]));
        var line = (SketchLine)near.Entities.Single();
        new[] { At(near, line.From).X, At(near, line.To).X }.Order().Should().Equal([50, 100], (a, b) => Math.Abs(a - b) <= Exact);
    }

    [Fact]
    public void An_oblique_circle_or_an_ellipse_is_refused()
    {
        Error(() => Project(Doc(Sketch(on: "plate/x+")), "plate/z+", "hole/wall")).Should().Contain("projects to an ellipse");

        var slant = """, {"id": "cut", "type": "plane-cut", "point": [0, 0, 8], "normal": [0, 0.3, 1]}""";
        Error(() => Project(Doc(Sketch(on: "plate/z-"), before: slant), "cut/face", "hole/wall")).Should().Contain("ellipse");
    }

    [Fact]
    public void Faces_that_are_not_there_or_do_not_meet_are_refused()
    {
        Error(() => Project(Doc(Sketch()), "plate/z+", "boss/x+")).Should().Contain("boss/x+ is not a face");
        Error(() => Project(Doc(Sketch()), "plate/z+", "plate/z-")).Should().Contain("meet in no line or circle");
        // The hole wall and the plate's side are both there, but meet nowhere on the part.
        Error(() => Project(Doc(Sketch()), "plate/x+", "hole/wall")).Should().Contain("meet in no line or circle");
    }

    [Fact]
    public void An_edge_seen_end_on_projects_to_a_point_and_cannot_be_linked()
    {
        var s = Settled(Project(Doc(Sketch()), "plate/x+", "plate/y+"));
        s.Entities.Should().BeEmpty();
        At(s, "projected").Should().Be((100.0, 50.0));

        Error(() => Project(Doc(Sketch()), "plate/x+", "plate/y+", linked: true)).Should().Contain("end-on");
    }

    [Fact]
    public void A_projection_is_the_same_every_time()
    {
        var doc = Doc(Sketch(on: "plate/z-"));
        Project(doc, "plate/z+", "hole/wall")["settled"]!.ToJsonString().Should().Be(Project(doc, "plate/z+", "hole/wall")["settled"]!.ToJsonString());
    }

    // ---- Linked ----

    [Fact]
    public void A_linked_projection_is_construction_that_keeps_its_faces_and_reads_back()
    {
        var r = Project(Doc(Sketch()), "plate/z+", "plate/x+", linked: true);
        var text = (string)r["settled"]!["text"]!;

        var line = (SketchLine)Settled(r).Entities.Single();
        line.Construction.Should().BeTrue();
        line.Project.Should().Equal("plate/z+", "plate/x+");
        var stored = JsonNode.Parse(text)!["features"]![2]!["entities"]![0]!;
        stored["project"]!.AsArray().Select(f => (string)f!).Should().Equal("plate/z+", "plate/x+");
        stored["construction"]!.GetValue<bool>().Should().BeTrue();
        CanonicalFormat.Write(CanonicalFormat.Read(text)).Should().Be(text);
        r["freeDegrees"]!.GetValue<int>().Should().Be(2, "each end slides along the edge's line");
    }

    [Fact]
    public void A_projected_entity_that_is_not_construction_or_names_one_face_is_invalid()
    {
        var points = """ "a": [100, 0], "b": [100, 50] """;
        SketchJson.Solve(Doc(Sketch(points: points, entities: """{"id": "e", "line": ["a", "b"], "project": ["plate/z+", "plate/x+"]}""")), "s")["problems"]!
            .AsArray().Select(p => (string)p!["message"]!).Should().ContainMatch("*construction*");
        SketchJson.Solve(Doc(Sketch(points: points, entities: """{"id": "e", "line": ["a", "b"], "project": ["plate/z+"], "construction": true}""")), "s")["problems"]!
            .AsArray().Select(p => (string)p!["message"]!).Should().ContainMatch("*two different face names*");
    }

    static string Linked(string plate = "[100, 50, 10]", string before = "", string more = "") => Doc(Sketch(
        points: """ "a": [100, 0], "b": [100, 50], "c": [30, 25], "d": [35, 25] """,
        entities: """
            {"id": "edge", "line": ["a", "b"], "project": ["plate/z+", "plate/x+"], "construction": true},
            {"id": "rim", "circle": {"center": "c", "radius": 5}, "project": ["plate/z+", "hole/wall"], "construction": true}
            """), more, plate, before);

    static IEnumerable<Diagnostic> Lost(string doc) =>
        new Compiler(new ModelingSettings()).Compile(CanonicalFormat.Read(doc)).Diagnostics.Where(d => d.Code == Diagnostic.SketchReferenceLost);

    [Fact]
    public void A_linked_line_follows_its_face_when_the_face_moves()
    {
        var r = SketchJson.Solve(Linked(plate: "[120, 50, 10]"), "s");

        r["solved"]!.GetValue<bool>().Should().BeTrue();
        foreach (var p in new[] { "a", "b" }) ((double)r["points"]![p]![0]!).Should().BeApproximately(120, Exact);
        ((double)r["points"]!["a"]![1]!).Should().BeApproximately(0, 1e-6, "the least-norm step moves it straight across");
        Lost(Linked(plate: "[120, 50, 10]")).Should().BeEmpty();
    }

    [Fact]
    public void A_linked_circle_follows_its_hole_when_the_hole_grows()
    {
        var r = SketchJson.Solve(Linked().Replace("\"diameter\": 10", "\"diameter\": 14"), "s");

        ((double)r["radii"]!["rim"]!).Should().BeApproximately(7, Exact);
        r["points"]!["c"]!.AsArray().Select(v => (double)v!).Should().Equal(30, 25);
    }

    [Fact]
    public void A_linked_line_still_resolves_when_a_cut_splits_its_face()
    {
        // A slot through the whole height splits plate/x+ in two.
        var slot = """, {"id": "slot", "type": "block", "op": "cut", "origin": [90, 20, -1], "size": [30, 10, 12]}""";
        var doc = Linked(plate: "[110, 50, 10]", before: slot);
        var faces = new FrepModeler(new ModelingSettings()).Regenerate(CanonicalFormat.Read(doc)).Topology!.Faces;
        faces.Count(f => f.Sources.Contains("plate/x+")).Should().Be(2);

        Lost(doc).Should().BeEmpty();
        var r = SketchJson.Solve(doc, "s");
        foreach (var p in new[] { "a", "b" }) ((double)r["points"]![p]![0]!).Should().BeApproximately(110, Exact);
    }

    [Fact]
    public void A_linked_projection_whose_face_is_gone_is_reported_and_stays_where_it_was()
    {
        var doc = Linked().Replace("""{"id": "hole", "type": "hole", "center": [30, 25], "diameter": 10},""", "")
            .Replace("[100, 50, 10]", "[120, 50, 10]");

        var lost = Lost(doc).Should().ContainSingle().Which;
        lost.FeatureId.Should().Be("s");
        lost.Message.Should().Contain("rim").And.Contain("hole/wall is not a face");
        var r = SketchJson.Solve(doc, "s");
        r["problems"]!.AsArray().Select(p => (string)p!["code"]!).Should().Contain(Diagnostic.SketchReferenceLost);
        r["points"]!["c"]!.AsArray().Select(v => (double)v!).Should().Equal(30, 25);
        ((double)r["radii"]!["rim"]!).Should().Be(5);
        ((double)r["points"]!["a"]![0]!).Should().BeApproximately(120, Exact, "the edge that is still there follows");
    }

    [Fact]
    public void A_linked_circle_whose_faces_no_longer_meet_is_reported_not_guessed()
    {
        // The hole now runs along x, below the top face.
        var doc = Linked().Replace("""{"id": "hole", "type": "hole", "center": [30, 25], "diameter": 10},""",
            """{"id": "hole", "type": "hole", "on": "plate/x+", "center": [25, 5], "diameter": 4},""");

        Lost(doc).Should().ContainSingle().Which.Message.Should().Contain("rim").And.Contain("meet in no line or circle");
    }

    [Fact]
    public void A_linked_circle_seen_from_a_tilted_plane_is_an_ellipse_now_and_reported()
    {
        static string On(string normal) => Linked().Replace("\"on\": {\"face\": \"plate/z+\"}",
            $$$"""
            "on": {"plane": {"origin": [0, 0, 20], "x": [1, 0, 0], "normal": {{{normal}}}}}
            """);

        Lost(On("[0, 0, 1]")).Should().BeEmpty();
        Lost(On("[0, 0.1, 1]")).Select(d => d.Message).Should().ContainMatch("*rim*ellipse*");
    }
}
