using Isoform.Document;
using Isoform.Modeling;

namespace Isoform.Tests;

/// <summary>
/// Extrudes and revolves of sketches through the whole kernel: regions with holes, faces named after sketch entities,
/// dimensions driven by parameters, sketches on faces that follow the face, and sketch problems reported, never
/// swallowed.
/// </summary>
public class SketchExtrudeTests
{
    static readonly ModelingSettings Settings = new();

    static PartDocument Document(string features, string parameters = "") => CanonicalFormat.Read($$"""
        {"format": "isoform/0", "units": "mm", {{(parameters.Length > 0 ? $"\"parameters\": {{{parameters}}}," : "")}} "features": [{{features}}]}
        """);

    static FrepResult Regenerate(string features, string parameters = "") => new FrepModeler(Settings).Regenerate(Document(features, parameters));

    // Whole documents for the suites that collect every test document (DocumentDiffTests).
    static PartDocument SketchedPlate() => Document(Plate, "\"width\": 60");

    static PartDocument SketchOnTop() => Document(OnTop("join", 0, 8));

    static double Volume(FrepResult r) => MassProperties.Of(r.Part.Field!, r.Mesh!).Volume;

    // Mesh volumes, as the other feature tests take them: to a part in a thousand.
    static double Relative(double v) => Math.Abs(v) * 1e-3;

    static (int, int, int, int) Counts(FrepResult r) => (r.Topology!.Faces.Count, r.Topology.Edges.Count, r.Topology.Vertices.Count, r.Topology.BodyCount);

    static IEnumerable<string> Names(FrepResult r) => r.Topology!.Faces.SelectMany(f => f.Sources).Order(StringComparer.Ordinal);

    // A 60 × 40 rectangle with its corner at the origin and a 4 mm circle at (20, 20), dimensioned by width.
    static readonly string Plate = """
        {"id": "base", "type": "sketch", "on": {"plane": {"origin": [0, 0, 0], "x": [1, 0, 0], "normal": [0, 0, 1]}},
         "points": {"a": [0, 0], "b": [58, 1], "c": [61, 39], "d": [1, 41], "h": [20, 20]},
         "entities": [{"id": "bottom", "line": ["a", "b"]}, {"id": "right", "line": ["b", "c"]}, {"id": "top", "line": ["c", "d"]},
                      {"id": "left", "line": ["d", "a"]}, {"id": "bore", "circle": {"center": "h", "radius": 3}}],
         "constraints": [{"horizontal": "bottom"}, {"vertical": "right"}, {"horizontal": "top"}, {"vertical": "left"}, {"fix": "a"}, {"fix": "h"},
                         {"length": "bottom", "value": "=width"}, {"length": "left", "value": 40}, {"radius": "bore", "value": 4}]},
        {"id": "plate", "type": "extrude", "op": "new", "sketch": "base", "start": 0, "end": 5}
        """;

    [Fact]
    public void A_sketch_with_a_hole_extrudes_into_a_plate_named_after_its_entities()
    {
        var r = Regenerate(Plate, "\"width\": 60");

        r.Diagnostics.Should().BeEmpty();
        Names(r).Should().Equal("plate/bore", "plate/bottom", "plate/end", "plate/left", "plate/right", "plate/start", "plate/top");
        Counts(r).Should().Be((7, 14, 8, 1));
        Volume(r).Should().BeApproximately(60 * 40 * 5 - Math.PI * 16 * 5, Relative(60 * 40 * 5 - Math.PI * 16 * 5));
    }

    [Fact]
    public void A_parameter_drives_the_sketch()
    {
        var narrow = Regenerate(Plate, "\"width\": 30");

        narrow.Diagnostics.Should().BeEmpty();
        Volume(narrow).Should().BeApproximately(30 * 40 * 5 - Math.PI * 16 * 5, Relative(30 * 40 * 5 - Math.PI * 16 * 5));
    }

    // A block 100 × 50 × 10 and a sketch on its top: a circle of radius 5 at (20, 20).
    // On the bottom face the sketch looks up from below: its y runs along −y, so (20, −20) is the world's (20, 20).
    static string OnTop(string op, double start, double end, string face = "plate/z+") => $$$"""
        {"id": "plate", "type": "block", "op": "new", "origin": [0, 0, 0], "size": [100, 50, 10]},
        {"id": "spot", "type": "sketch", "on": {"face": "{{{face}}}"}, "points": {"c": [20, {{{(face == "plate/z-" ? -20 : 20)}}}]},
         "entities": [{"id": "disc", "circle": {"center": "c", "radius": 5}}], "constraints": [{"fix": "c"}]},
        {"id": "boss", "type": "extrude", "op": "{{{op}}}", "sketch": "spot", "start": {{{start}}}, "end": {{{end}}}}
        """;

    [Fact]
    public void A_sketch_on_a_face_builds_on_that_face_in_world_coordinates()
    {
        var boss = Regenerate(OnTop("join", 0, 8));
        var pocket = Regenerate(OnTop("cut", -3, 0));

        boss.Diagnostics.Should().BeEmpty();
        Volume(boss).Should().BeApproximately(100 * 50 * 10 + Math.PI * 25 * 8, Relative(100 * 50 * 10 + Math.PI * 25 * 8));
        boss.Part.Max.Z.Should().BeApproximately(18, 1e-9);
        boss.Part.Surfaces.First(s => s.Source == "boss/disc").Origin.X.Should().BeApproximately(20, 1e-12);
        pocket.Diagnostics.Should().BeEmpty();
        Volume(pocket).Should().BeApproximately(100 * 50 * 10 - Math.PI * 25 * 3, Relative(100 * 50 * 10 - Math.PI * 25 * 3));
    }

    [Fact]
    public void A_sketch_on_the_bottom_face_grows_downwards()
    {
        var r = Regenerate(OnTop("join", 0, 4, "plate/z-"));

        r.Diagnostics.Should().BeEmpty();
        r.Part.Min.Z.Should().BeApproximately(-4, 1e-9);
        Volume(r).Should().BeApproximately(100 * 50 * 10 + Math.PI * 25 * 4, Relative(100 * 50 * 10 + Math.PI * 25 * 4));
    }

    [Fact]
    public void A_face_that_is_not_there_is_reported()
    {
        var r = Regenerate(OnTop("join", 0, 8, "plate/w+"));

        r.Diagnostics.Should().Contain(d => d.Code == Diagnostic.SketchPlaneLost && d.FeatureId == "spot");
        r.Diagnostics.Should().Contain(d => d.Code == Diagnostic.SketchInvalid && d.FeatureId == "boss");
        Volume(r).Should().BeApproximately(100 * 50 * 10, 1e-6, "the plate is still built");
    }

    [Fact]
    public void A_sketch_that_does_not_solve_is_reported_on_the_sketch_and_the_extrude()
    {
        var r = Regenerate(Plate.Replace("""{"radius": "bore", "value": 4}""", """{"radius": "bore", "value": 4}, {"length": "top", "value": 50}"""), "\"width\": 60");

        r.Diagnostics.Should().Contain(d => d.Code == Diagnostic.SketchConflict && d.FeatureId == "base" && d.Message.Contains("length top"));
        r.Diagnostics.Should().Contain(d => d.Code == Diagnostic.SketchInvalid && d.FeatureId == "plate");
    }

    [Fact]
    public void One_region_of_several_can_be_swept()
    {
        var r = Regenerate("""
            {"id": "s", "type": "sketch", "on": {"plane": {"origin": [0, 0, 0], "x": [1, 0, 0], "normal": [0, 0, 1]}},
             "points": {"a": [0, 0], "b": [10, 0], "c": [5, 8], "k": [30, 0]},
             "entities": [{"id": "ab", "line": ["a", "b"]}, {"id": "bc", "line": ["b", "c"]}, {"id": "ca", "line": ["c", "a"]},
                          {"id": "disc", "circle": {"center": "k", "radius": 3}}]},
            {"id": "pin", "type": "extrude", "op": "new", "sketch": "s", "regions": [["disc"]], "start": 0, "end": 10}
            """);

        r.Diagnostics.Should().BeEmpty();
        Names(r).Should().Equal("pin/disc", "pin/end", "pin/start");
        Volume(r).Should().BeApproximately(Math.PI * 9 * 10, Relative(Math.PI * 9 * 10));
    }

    [Fact]
    public void A_sketch_revolves_into_a_tube()
    {
        // Profile x is the distance from the axis: a 5 × 20 rectangle from x = 5 to 10 makes a tube 20 high.
        var r = Regenerate("""
            {"id": "s", "type": "sketch", "on": {"plane": {"origin": [0, 0, 0], "x": [1, 0, 0], "normal": [0, -1, 0]}},
             "points": {"a": [5, 0], "b": [10, 0], "c": [10, 20], "d": [5, 20]},
             "entities": [{"id": "floor", "line": ["a", "b"]}, {"id": "outer", "line": ["b", "c"]}, {"id": "roof", "line": ["c", "d"]}, {"id": "inner", "line": ["d", "a"]}]},
            {"id": "tube", "type": "revolve", "op": "new", "sketch": "s", "angle": 360}
            """);

        r.Diagnostics.Should().BeEmpty();
        Names(r).Should().Equal("tube/floor", "tube/inner", "tube/outer", "tube/roof");
        Volume(r).Should().BeApproximately(Math.PI * (100 - 25) * 20, Relative(Math.PI * (100 - 25) * 20));
    }
}
