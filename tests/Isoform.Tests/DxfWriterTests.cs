using System.Globalization;
using Isoform.Document;
using Isoform.Modeling;
using Isoform.Sketching;
using Isoform.Step;

namespace Isoform.Tests;

/// <summary>
/// DXF R12 of sketches and of flat faces' exact outlines, read back by a small parser of group code / value pairs:
/// the entities, their coordinates, and arcs counter-clockwise from start to end angle in degrees.
/// </summary>
public class DxfWriterTests
{
    static readonly ModelingSettings Settings = new() { CellSize = 0.5, ChordTolerance = 0.05 };

    static readonly SketchPlane Xy = new([0, 0, 0], [1, 0, 0], [0, 0, 1]);

    static readonly Block Plate = new("plate", BooleanOp.New, [0, 0, 0], [100, 50, 10]);

    /// <summary>An entity as read back: its type, layer and numbers by group code.</summary>
    sealed record Read(string Type, string Layer, Dictionary<int, double> Values)
    {
        public double this[int code] => Values[code];
    }

    /// <summary>The ENTITIES section of a DXF file, checking the file's frame on the way.</summary>
    static List<Read> Parse(string dxf)
    {
        var lines = dxf.Split("\r\n");
        lines[^1].Should().BeEmpty("the file ends with a line break");
        var pairs = Enumerable.Range(0, lines.Length / 2).Select(i => (Code: int.Parse(lines[2 * i].Trim(), CultureInfo.InvariantCulture), Value: lines[2 * i + 1])).ToList();
        pairs[^1].Should().Be((0, "EOF"));
        pairs.Should().Contain((1, "AC1009"));
        var start = pairs.IndexOf((2, "ENTITIES"));
        start.Should().BePositive();
        var entities = new List<Read>();
        for (var i = start + 1; pairs[i] != (0, "ENDSEC"); i++)
        {
            if (pairs[i].Code == 0) entities.Add(new Read(pairs[i].Value, "", []));
            else if (pairs[i].Code == 8) entities[^1] = entities[^1] with { Layer = pairs[i].Value };
            else entities[^1].Values[pairs[i].Code] = double.Parse(pairs[i].Value, CultureInfo.InvariantCulture);
        }
        entities.Should().OnlyContain(e => e.Layer == "0" && e.Values[30] == 0);
        return entities;
    }

    static List<Read> Of(string type, List<Read> entities) => entities.Where(e => e.Type == type).ToList();

    /// <summary>The end points of lines and arcs.</summary>
    static IEnumerable<(double U, double V)> Ends(Read e) => e.Type switch
    {
        "LINE" => [(e[10], e[20]), (e[11], e[21])],
        "ARC" => [At(e, e[50]), At(e, e[51])],
        _ => [],
    };

    static (double U, double V) At(Read arc, double degrees) =>
        (arc[10] + arc[40] * Math.Cos(degrees * Math.PI / 180), arc[20] + arc[40] * Math.Sin(degrees * Math.PI / 180));

    /// <summary>Every end point meets another: the outline is closed.</summary>
    static void Closed(List<Read> entities)
    {
        var ends = entities.SelectMany(Ends).ToList();
        for (var i = 0; i < ends.Count; i++)
            ends.Where((p, j) => j != i && Math.Abs(p.U - ends[i].U) < 1e-6 && Math.Abs(p.V - ends[i].V) < 1e-6).Should().NotBeEmpty($"end {ends[i]} is open");
    }

    static string Sketch(Sketch sketch) => DxfWriter.Write(DxfWriter.Sketch(sketch, SketchSolver.Solve(sketch)));

    static (string Dxf, int Approximated) Face(PartDocument doc, string face)
    {
        var result = new FrepModeler(Settings).Regenerate(doc);
        result.Diagnostics.Should().BeEmpty();
        var (entities, approximated) = DxfWriter.Face(result, ExactBrep.Build(result, Settings), face, Settings.ChordTolerance)!.Value;
        return (DxfWriter.Write(entities), approximated);
    }

    [Fact]
    public void A_sketched_rectangle_with_a_hole_is_four_lines_and_a_circle_at_its_solution()
    {
        // Stored off square; the constraints make it 60 × 40 with an R4 bore. The diagonal is construction: left out.
        var sketch = new Sketch("base", new SketchPlacement(Xy),
            new Dictionary<string, double[]> { ["a"] = [0, 0], ["b"] = [58, 1], ["c"] = [61, 39], ["d"] = [1, 41], ["h"] = [20, 20] },
            [
                new SketchLine("bottom", "a", "b"), new SketchLine("right", "b", "c"), new SketchLine("top", "c", "d"), new SketchLine("left", "d", "a"),
                new SketchCircle("bore", "h", 3), new SketchLine("diagonal", "a", "c") { Construction = true },
            ],
            [
                new("horizontal", ["bottom"]), new("vertical", ["right"]), new("horizontal", ["top"]), new("vertical", ["left"]), new("fix", ["a"]),
                new("fix", ["h"]), new("length", ["bottom"], 60), new("length", ["left"], 40), new("radius", ["bore"], 4),
            ]);

        var entities = Parse(Sketch(sketch));

        entities.Select(e => e.Type).Should().Equal("LINE", "LINE", "LINE", "LINE", "CIRCLE");
        var corners = Of("LINE", entities).SelectMany(Ends).Select(p => (Math.Round(p.U, 6), Math.Round(p.V, 6))).Distinct();
        corners.Should().BeEquivalentTo([(0.0, 0.0), (60.0, 0.0), (60.0, 40.0), (0.0, 40.0)]);
        var bore = Of("CIRCLE", entities).Single();
        (bore[10], bore[20], bore[40]).Should().Be((20, 20, 4));
        Closed(entities);
    }

    [Fact]
    public void A_sketched_slot_has_its_arcs_counter_clockwise_from_start_to_end()
    {
        // A 20 long slot between two R5 ends: the right end from (20, 0) up to (20, 10), the left from (0, 10) down.
        var slot = new Sketch("slot", new SketchPlacement(Xy),
            new Dictionary<string, double[]> { ["a"] = [0, 0], ["b"] = [20, 0], ["c"] = [20, 10], ["d"] = [0, 10], ["r"] = [20, 5], ["l"] = [0, 5] },
            [new SketchLine("bottom", "a", "b"), new SketchArc("right", "r", "b", "c"), new SketchLine("top", "c", "d"), new SketchArc("left", "l", "d", "a")], []);

        var entities = Parse(Sketch(slot));

        entities.Select(e => e.Type).Should().Equal("LINE", "ARC", "LINE", "ARC");
        var (right, left) = (entities[1], entities[3]);
        (right[10], right[20], right[40], right[50], right[51]).Should().Be((20, 5, 5, 270, 90));
        (left[10], left[20], left[40], left[50], left[51]).Should().Be((0, 5, 5, 90, 270));
        Closed(entities);
    }

    [Fact]
    public void A_sketched_circle_is_one_circle()
    {
        var disc = new Sketch("disc", new SketchPlacement(Xy), new Dictionary<string, double[]> { ["o"] = [5, -3] }, [new SketchCircle("rim", "o", 12.5)], []);

        var circle = Parse(Sketch(disc)).Should().ContainSingle().Which;

        circle.Type.Should().Be("CIRCLE");
        (circle[10], circle[20], circle[40]).Should().Be((5, -3, 12.5));
    }

    [Fact]
    public void A_face_outline_is_exact_in_the_plane_a_sketch_on_it_takes()
    {
        var doc = new PartDocument([Plate, new Hole("hole", [30, 25], 10)]);

        // The top face reads in world x and y; the bottom face looks down, so its v runs along world −y.
        foreach (var (face, sign) in new[] { ("plate/z+", 1), ("plate/z-", -1) })
        {
            var (dxf, approximated) = Face(doc, face);
            var entities = Parse(dxf);

            approximated.Should().Be(0);
            Of("LINE", entities).Should().HaveCount(4);
            var corners = Of("LINE", entities).SelectMany(Ends).Select(p => (Math.Round(p.U, 9), Math.Round(p.V, 9))).Distinct();
            corners.Should().BeEquivalentTo([(0.0, 0.0), (100.0, 0.0), (100.0, sign * 50.0), (0.0, sign * 50.0)], face);
            var hole = Of("CIRCLE", entities).Should().ContainSingle().Which;
            (hole[10], hole[20], hole[40]).Should().Be((30, sign * 25, 5));
            Closed(entities);
        }
    }

    [Fact]
    public void A_face_with_a_slot_through_it_has_the_slot_ends_as_arcs()
    {
        // A stadium from (40, 20) to (60, 30) cut through the plate: R5 ends round (40, 25) and (60, 25).
        var stadium = new Profile([40, 20], [new("a", [60, 20]), new("right", [60, 30], 1), new("b", [40, 30]), new("left", [40, 20], 1)]);
        var doc = new PartDocument([Plate, new Extrude("slot", BooleanOp.Cut, Xy, stadium, -1, 11)]);

        foreach (var (face, sign) in new[] { ("plate/z+", 1), ("plate/z-", -1) })
        {
            var entities = Parse(Face(doc, face).Dxf);

            Of("LINE", entities).Should().HaveCount(6, face);
            var arcs = Of("ARC", entities).OrderBy(a => a[10]).ToList();
            arcs.Should().HaveCount(2);
            // Seen from either side the left end bulges towards −u and the right towards +u.
            (arcs[0][10], arcs[0][20], arcs[0][40], arcs[0][50], arcs[0][51]).Should().Be((40, sign * 25, 5, 90, 270), face);
            (arcs[1][10], arcs[1][20], arcs[1][40], arcs[1][50], arcs[1][51]).Should().Be((60, sign * 25, 5, 270, 90), face);
            Closed(entities);
        }
    }

    [Fact]
    public void An_ellipse_on_a_sloping_face_is_lines_within_the_chord_tolerance()
    {
        // The cut plane meets the hole wall in an ellipse.
        var doc = new PartDocument([Plate, new Hole("hole", [30, 25], 10), new PlaneCut("cut", [20, 25, 10], [Math.Tan(Math.PI / 6), 0, 1])]);

        var (dxf, approximated) = Face(doc, "cut/face");
        var entities = Parse(dxf);

        approximated.Should().Be(1);
        Of("CIRCLE", entities).Should().BeEmpty();
        Of("LINE", entities).Count.Should().BeGreaterThan(16);
        Closed(entities);
    }

    [Fact]
    public void A_curved_face_has_no_outline()
    {
        var doc = new PartDocument([Plate, new Hole("hole", [30, 25], 10)]);
        var result = new FrepModeler(Settings).Regenerate(doc);

        DxfWriter.Face(result, ExactBrep.Build(result, Settings), "hole/wall", Settings.ChordTolerance).Should().BeNull();
    }
}
