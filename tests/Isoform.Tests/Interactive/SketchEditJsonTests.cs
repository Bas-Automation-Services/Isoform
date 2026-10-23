using System.Text.Json.Nodes;
using Isoform.Document;
using Isoform.Interactive;
using Isoform.Sketching;

namespace Isoform.Tests.Interactive;

/// <summary>
/// The sketch edit op and tool (<see cref="SketchJson.Edit(string, string, string, string?, double?, string?, double?, double?)"/>):
/// the request read, the edited sketch put back with its expressions and solved, added and dropped passed on, and an
/// edit that cannot be made answered with its message.
/// </summary>
public class SketchEditJsonTests
{
    static readonly Block Plate = new("plate", BooleanOp.New, [0, 0, 0], [100, 50, 10]);

    // A 60 × 20 rectangle r: a fixed corner, its sides level and plumb, the right side =h long and the bottom =w; and a
    // hole whose diameter is =h / 4.
    static readonly Sketch Rectangle = new("r", new SketchPlacement(Face: "plate/z+"),
        new Dictionary<string, double[]> { ["a"] = [10, 10], ["b"] = [70, 10], ["c"] = [70, 30], ["d"] = [10, 30] },
        [new SketchLine("bottom", "a", "b"), new SketchLine("right", "b", "c"), new SketchLine("top", "c", "d"), new SketchLine("left", "d", "a")],
        [new("fix", ["a"]), new("horizontal", ["bottom"]), new("vertical", ["right"]), new("horizontal", ["top"]), new("vertical", ["left"]),
         new("length", ["right"], 0), new("length", ["bottom"], 0)]);

    static readonly string Parametric = CanonicalFormat.Write(new PartDocument([Plate, Rectangle, new Hole("hole", [85, 40], 0)])
    {
        Parameters = new Dictionary<string, string> { ["w"] = "60", ["h"] = "20" },
        Expressions = new Dictionary<string, string> { ["r.constraints.5.value"] = "h", ["r.constraints.6.value"] = "w", ["hole.diameter"] = "h / 4" },
    });

    static string Plain(params Feature[] extra) => CanonicalFormat.Write(new PartDocument([Plate, Rectangle with
    {
        Constraints = [.. Rectangle.Constraints.Take(5), new("length", ["right"], 20), new("length", ["bottom"], 60)],
    }, .. extra]));

    static string Error(string json) => JsonNode.Parse(json)!["error"]!.GetValue<string>();

    [Fact]
    public void An_edit_comes_back_solved_with_its_added_and_dropped_and_the_expressions_that_still_apply()
    {
        // A stand-in for a trim: the right side's length goes (listed), the bottom's moves up one, a point is added.
        var r = SketchJson.Edit(Parametric, "r", s => new SketchEdit(
            s with { Points = new Dictionary<string, double[]>(s.Points) { ["e"] = [40, 10] }, Constraints = [.. s.Constraints.Where((_, i) => i != 5)] },
            ["e"], [SketchSolver.Describe(s, 5)]));

        r["solved"]!.GetValue<bool>().Should().BeTrue();
        r["freeDegrees"]!.GetValue<int>().Should().Be(3, "the height and the loose point e are free now");
        r["added"]!.AsArray().Select(n => (string)n!).Should().Equal("e");
        r["dropped"]!.AsArray().Select(n => (string)n!).Should().Equal("#5 (length right = 20)");
        var settled = CanonicalFormat.Read((string)r["settled"]!["text"]!);
        settled.Expressions.Should().BeEquivalentTo(new Dictionary<string, string> { ["r.constraints.5.value"] = "w", ["hole.diameter"] = "h / 4" });
        settled.Parameters.Keys.Should().BeEquivalentTo("w", "h");
        ((Sketch)settled["r"]).Points.Keys.Should().Contain("e");
    }

    [Fact]
    public void The_page_op_reads_its_request_and_answers_what_is_missing_or_wrong()
    {
        var doc = Plain();
        Error(SketchJson.WriteEdit(doc, "plate", """{"op": "fillet", "corner": "c", "radius": 2}""")).Should().Contain("not a sketch");
        Error(SketchJson.WriteEdit(doc, "r", """{"op": "chamfer", "corner": "c"}""")).Should().Contain("no sketch edit 'chamfer'");
        Error(SketchJson.WriteEdit(doc, "r", """{"op": "fillet", "corner": "c"}""")).Should().Contain("needs a radius");
        Error(SketchJson.WriteEdit(doc, "r", """{"op": "fillet", "radius": 2}""")).Should().Contain("corner");
        Error(SketchJson.WriteEdit(doc, "r", """{"op": "trim", "entity": "top", "x": 40}""")).Should().Contain("needs y");
        Error(SketchJson.WriteEdit(doc, "r", """{"op": "trim", "x": 40, "y": 30}""")).Should().Contain("entity");
        Error(SketchJson.WriteEdit("{", "r", """{"op": "trim"}""")).Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void An_edit_that_cannot_be_made_throws_its_message_and_changes_nothing()
    {
        var doc = Plain();
        var edit = () => SketchJson.Edit(doc, "r", _ => throw new ArgumentException("the corner c joins three entities"));
        edit.Should().Throw<ArgumentException>().WithMessage("the corner c joins three entities");
    }

    // ---- With the real edits (SketchEdits.Fillet and Trim) ----

    [Fact]
    public void A_fillet_rounds_a_corner_with_an_arc_and_keeps_the_corner_and_the_expressions()
    {
        var r = JsonNode.Parse(SketchJson.WriteEdit(Parametric, "r", """{"op": "fillet", "corner": "c", "radius": 5}"""))!;

        r["error"].Should().BeNull();
        r["solved"]!.GetValue<bool>().Should().BeTrue();
        r["dropped"]!.AsArray().Should().BeEmpty();
        var settled = CanonicalFormat.Read((string)r["settled"]!["text"]!);
        var sketch = (Sketch)settled["r"];
        var added = r["added"]!.AsArray().Select(n => (string)n!).ToList();
        sketch.Entities.Where(e => added.Contains(e.Id)).OfType<SketchArc>().Should().ContainSingle();
        sketch.Points.Keys.Should().Contain("c");
        // The right side is cut short: its length =h is now a distance from the corner, in its place, still =h.
        sketch.Constraints[5].Kind.Should().Be("distance");
        settled.Expressions.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["r.constraints.5.value"] = "h", ["r.constraints.6.value"] = "w", ["hole.diameter"] = "h / 4",
        });
    }

    [Fact]
    public void A_trim_leaves_a_circle_an_arc_and_lists_the_constraints_it_dropped()
    {
        // A bore of radius 4 round h, and a chord 10 long across it.
        var withBore = Rectangle with
        {
            Points = new Dictionary<string, double[]>(Rectangle.Points) { ["h"] = [40, 20], ["p"] = [35, 20], ["q"] = [45, 20] },
            Entities = [.. Rectangle.Entities, new SketchCircle("bore", "h", 4), new SketchLine("chord", "p", "q")],
            Constraints = [.. Rectangle.Constraints.Take(5), new("length", ["right"], 20), new("length", ["bottom"], 60), new("length", ["chord"], 10)],
        };
        var doc = CanonicalFormat.Write(new PartDocument([Plate, withBore]));
        JsonNode Trim(string entity, double x, double y) => JsonNode.Parse(SketchJson.WriteEdit(doc, "r", $$"""{"op": "trim", "entity": "{{entity}}", "x": {{x}}, "y": {{y}}}"""))!;

        var top = Trim("bore", 40, 24);
        top["error"].Should().BeNull();
        ((Sketch)CanonicalFormat.Read((string)top["settled"]!["text"]!)["r"]).Entities.Single(e => e.Id == "bore").Should().BeOfType<SketchArc>();

        var end = Trim("chord", 35, 20);
        end["solved"]!.GetValue<bool>().Should().BeTrue();
        end["dropped"]!.AsArray().Select(n => (string)n!).Should().Contain("#7 (length chord = 10)", "its end moved");

        Error(SketchJson.WriteEdit(doc, "r", """{"op": "trim", "entity": "bottom", "x": 40, "y": 10}""")).Should().Contain("delete it instead");
    }
}
