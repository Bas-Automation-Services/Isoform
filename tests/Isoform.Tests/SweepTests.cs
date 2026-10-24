using System.Text.Json.Nodes;
using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Modeling;
using Xunit.Abstractions;

namespace Isoform.Tests;

/// <summary>
/// S10–S11: a sketch's regions swept along a flat path of lines and arcs. Lines extrude the profile, arcs revolve it,
/// so every face is a plane, cylinder, cone, sphere or torus and the volume is exact by Pappus. What would not be exact
/// (a corner in the path, a profile off square, an arc tighter than the profile) is refused with a diagnostic.
/// </summary>
public class SweepTests(ITestOutputHelper output)
{
    static readonly ModelingSettings Settings = new() { CellSize = 0.5 };

    static readonly Func<string, PartDocument> Read = features => CanonicalFormat.Read($$"""
        {"format": "isoform/0", "units": "mm", "features": [{{features}}]}
        """);

    FrepResult Regenerate(string features) => Regenerate(Read(features));

    FrepResult Regenerate(PartDocument doc)
    {
        var r = new FrepModeler(Settings).Regenerate(doc);
        foreach (var d in r.Diagnostics) output.WriteLine($"{d.Code} {d.FeatureId}: {d.Message}");
        if (r.Topology is { } t)
        {
            output.WriteLine($"{t.Faces.Count} faces, {t.Edges.Count} edges, {t.Vertices.Count} vertices, {t.BodyCount} bodies");
            foreach (var f in t.Faces) output.WriteLine($"  face {string.Join(", ", f.Sources)}");
        }
        return r;
    }

    static double Volume(FrepResult r) => MassProperties.Of(r.Part.Field!, r.Mesh!).Volume;

    static (int, int, int, int) Counts(FrepResult r) => (r.Topology!.Faces.Count, r.Topology.Edges.Count, r.Topology.Vertices.Count, r.Topology.BodyCount);

    static IEnumerable<string> Names(FrepResult r) => r.Topology!.Faces.SelectMany(f => f.Sources).Distinct().Order(StringComparer.Ordinal);

    const string Xy = """{"plane": {"origin": [0, 0, 0], "x": [1, 0, 0], "normal": [0, 0, 1]}}""";

    // On the plane x = 0, square to a path leaving the origin along +x: sketch x is world y, sketch y world z.
    const string AcrossX = """{"plane": {"origin": [0, 0, 0], "x": [0, 1, 0], "normal": [1, 0, 0]}}""";

    // In the XY plane: 40 along x, a quarter turn left of radius 20 round (40, 20), 30 up along y.
    const string Route = """
        {"id": "route", "type": "sketch", "on": {"plane": {"origin": [0, 0, 0], "x": [1, 0, 0], "normal": [0, 0, 1]}},
         "points": {"a": [0, 0], "b": [40, 0], "c": [40, 20], "d": [60, 20], "e": [60, 50]},
         "entities": [{"id": "l1", "line": ["a", "b"]}, {"id": "a1", "arc": {"center": "c", "from": "b", "to": "d"}}, {"id": "l2", "line": ["d", "e"]}]}
        """;

    // A sketch "section" of circles on a plane, each round its own point.
    static string Section(string on, params (double X, double Y, double R, string Id)[] circles)
    {
        var points = circles.Select((c, k) => FormattableString.Invariant($"\"o{k}\": [{c.X}, {c.Y}]"));
        var entities = circles.Select((c, k) => FormattableString.Invariant($"{{\"id\": \"{c.Id}\", \"circle\": {{\"center\": \"o{k}\", \"radius\": {c.R}}}}}"));
        return $"{{\"id\": \"section\", \"type\": \"sketch\", \"on\": {on}, \"points\": {{{string.Join(", ", points)}}}, \"entities\": [{string.Join(", ", entities)}]}}";
    }

    static string Sweep(string path = """{"sketch": "route", "entities": ["l1", "a1", "l2"]}""", string op = "new", string id = "pipe") =>
        $$"""{"id": "{{id}}", "type": "sweep", "op": "{{op}}", "sketch": "section", "path": {{path}}}""";

    static string Elbow(double radius = 5) => $"{Route}, {Section(AcrossX, (0, 0, radius, "wall"))}, {Sweep()}";

    const double ElbowLength = 40 + 30 + 20 * Math.PI / 2;

    // In the XY plane: 40 along x, a quarter turn right of radius 30 round (40, -30), 30 down along y.
    const string RightTurn = """
        {"id": "route", "type": "sketch", "on": {"plane": {"origin": [0, 0, 0], "x": [1, 0, 0], "normal": [0, 0, 1]}},
         "points": {"a": [0, 0], "b": [40, 0], "c": [40, -30], "d": [70, -30], "e": [70, -60]},
         "entities": [{"id": "l1", "line": ["a", "b"]}, {"id": "a1", "arc": {"center": "c", "from": "d", "to": "b"}}, {"id": "l2", "line": ["d", "e"]}]}
        """;

    // A 6 × 6 square from 2 to 8 left of the path, centred on its plane; drawn in a mirrored frame (sketch y is world −z).
    const string Bar = """
        {"id": "section", "type": "sketch", "on": {"plane": {"origin": [0, 0, 0], "x": [0, 1, 0], "normal": [-1, 0, 0]}},
         "points": {"p": [2, -3], "q": [8, -3], "r": [8, 3], "s": [2, 3]},
         "entities": [{"id": "low", "line": ["p", "q"]}, {"id": "outer", "line": ["q", "r"]}, {"id": "high", "line": ["r", "s"]}, {"id": "inner", "line": ["s", "p"]}]}
        """;

    // A stadium loop in the XY plane: two 40 mm sides and two half turns of radius 15.
    const string Loop = """
        {"id": "route", "type": "sketch", "on": {"plane": {"origin": [0, 0, 0], "x": [1, 0, 0], "normal": [0, 0, 1]}},
         "points": {"a": [0, 0], "b": [40, 0], "c": [40, 15], "d": [40, 30], "e": [0, 30], "f": [0, 15]},
         "entities": [{"id": "low", "line": ["a", "b"]}, {"id": "right", "arc": {"center": "c", "from": "b", "to": "d"}},
                      {"id": "high", "line": ["d", "e"]}, {"id": "left", "arc": {"center": "f", "from": "e", "to": "a"}}]}
        """;

    public sealed record Part(string Name, string Features, double Volume, (int Faces, int Edges, int Vertices, int Bodies) Topology);

    public static IEnumerable<Part> Parts()
    {
        yield return new("elbow pipe", Elbow(), Math.PI * 25 * ElbowLength, (5, 4, 0, 1));
        // Pappus: the square's centroid runs 35 from the turn's centre, its sides 32 and 38.
        yield return new("square bar turning right", $"{RightTurn}, {Bar}, {Sweep()}", 36 * (40 + 30 + Math.PI / 2 * 35), (10, 24, 16, 1));
        // Three quarters of a turn left round (20, 15), then a quarter right round (-5, 15): a triangle's slanted sides
        // turn into cones, and the long turn is cut by the union of its end half-spaces.
        yield return new("triangle along a loop and a bend", """
            {"id": "route", "type": "sketch", "on": {"plane": {"origin": [0, 0, 0], "x": [1, 0, 0], "normal": [0, 0, 1]}},
             "points": {"a": [0, 0], "b": [20, 0], "c": [20, 15], "f": [5, 15], "k": [-5, 15], "g": [-5, 5], "h": [-25, 5]},
             "entities": [{"id": "l1", "line": ["a", "b"]}, {"id": "a1", "arc": {"center": "c", "from": "b", "to": "f"}},
                          {"id": "a2", "arc": {"center": "k", "from": "g", "to": "f"}}, {"id": "l2", "line": ["g", "h"]}]},
            {"id": "section", "type": "sketch", "on": {"plane": {"origin": [0, 0, 0], "x": [0, 1, 0], "normal": [1, 0, 0]}},
             "points": {"p": [-3, 0], "q": [3, 0], "r": [0, 4]},
             "entities": [{"id": "base", "line": ["p", "q"]}, {"id": "right", "line": ["q", "r"]}, {"id": "left", "line": ["r", "p"]}]},
            {"id": "pipe", "type": "sweep", "op": "new", "sketch": "section", "path": {"sketch": "route", "from": "l1"}}
            """, 12 * (40 + 1.5 * Math.PI * 15 + 0.5 * Math.PI * 10), (11, 24, 15, 1));
        yield return new("hollow elbow", $"{Route}, {Section(AcrossX, (0, 0, 5, "wall"), (0, 0, 3, "bore"))}, {Sweep()}", Math.PI * 16 * ElbowLength, (8, 8, 0, 1));
        yield return new("closed ring", $"{Loop}, {Section(AcrossX, (0, 4, 3, "tube"))}, {Sweep("""{"sketch": "route", "from": "high"}""", id: "ring")}",
            Math.PI * 9 * (80 + 2 * Math.PI * 15), (4, 4, 0, 1));
        yield return new("torus", """
            {"id": "route", "type": "sketch", "on": {"plane": {"origin": [0, 0, 0], "x": [1, 0, 0], "normal": [0, 0, 1]}}, "points": {"c": [0, 0]},
             "entities": [{"id": "round", "circle": {"center": "c", "radius": 20}}]},
            {"id": "section", "type": "sketch", "on": {"plane": {"origin": [0, 0, 0], "x": [1, 0, 0], "normal": [0, -1, 0]}}, "points": {"o": [-20, 0]},
             "entities": [{"id": "tube", "circle": {"center": "o", "radius": 4}}]},
            {"id": "ring", "type": "sweep", "op": "new", "sketch": "section", "path": {"sketch": "route", "entities": ["round"]}}
            """, 2 * Math.PI * Math.PI * 20 * 16, (1, 0, 0, 1));
        // A block the pipe leaves from its face x = 0, and a bore cut along the same path.
        yield return new("pipe on a block, bored", $$$"""
            {"id": "flange", "type": "block", "op": "new", "origin": [-10, -15, -15], "size": [10, 30, 30]},
            {{{Route}}}, {{{Section(AcrossX, (0, 0, 5, "wall"))}}}, {{{Sweep(op: "join")}}},
            {"id": "hole", "type": "sketch", "on": {{{AcrossX}}}, "points": {"o": [0, 0]}, "entities": [{"id": "bore", "circle": {"center": "o", "radius": 3}}]},
            {"id": "drill", "type": "sweep", "op": "cut", "sketch": "hole", "path": {"sketch": "route", "from": "a1"}}
            """, 10 * 30 * 30 + Math.PI * 16 * ElbowLength, (14, 20, 8, 1));
        // The bored pipe and a copy 15 higher, on a taller block.
        yield return new("bored pipes on a block, patterned", $$$"""
            {"id": "flange", "type": "block", "op": "new", "origin": [-10, -15, -15], "size": [10, 30, 40]},
            {{{Route}}}, {{{Section(AcrossX, (0, 0, 5, "wall"))}}}, {{{Sweep(op: "join")}}},
            {"id": "hole", "type": "sketch", "on": {{{AcrossX}}}, "points": {"o": [0, 0]}, "entities": [{"id": "bore", "circle": {"center": "o", "radius": 3}}]},
            {"id": "drill", "type": "sweep", "op": "cut", "sketch": "hole", "path": {"sketch": "route", "from": "a1"}},
            {"id": "row", "type": "pattern", "features": ["pipe", "drill"], "linear": {"direction": [0, 0, 1], "spacing": 15, "count": 2}}
            """, 10 * 30 * 40 + 2 * Math.PI * 16 * ElbowLength, (22, 28, 8, 1));
    }

    public static IEnumerable<object[]> PartNames => Parts().Select(p => new object[] { p.Name });

    // Its bend's outer side, 13 round (−5, 15), runs through the start's corner (0, 3, 0).
    const string Touching = "triangle along a loop and a bend";

    /// <summary>A part's whole document, by its name.</summary>
    public static PartDocument Document(string name) => Read(Get(name).Features);

    static Part Get(string name) => Parts().Single(p => p.Name == name);

    [Theory]
    [MemberData(nameof(PartNames))]
    public void Part_has_the_exact_volume_and_topology(string name)
    {
        var part = Get(name);
        var r = Regenerate(part.Features);

        // The triangle's bend touches its start in a point: built, with a warning (see below).
        r.Diagnostics.Where(d => name != Touching || d.Code != Diagnostic.TangentContact).Should().BeEmpty();
        r.Mesh!.IsWatertight().Should().BeTrue();
        Counts(r).Should().Be(part.Topology);
        Volume(r).Should().BeApproximately(part.Volume, part.Volume * 1e-3);
    }

    [Theory]
    [MemberData(nameof(PartNames))]
    public void Field_is_1_lipschitz_bounded_and_simplifies_exactly(string name) =>
        FieldProperties.Check(new Compiler(new ModelingSettings()).Compile(Document(name)));

    [Fact]
    public void Faces_are_named_after_the_path_entity_and_the_profile_entity()
    {
        Names(Regenerate(Elbow())).Should().Equal("pipe/a1:wall", "pipe/end", "pipe/l1:wall", "pipe/l2:wall", "pipe/start");
        Names(Regenerate(Get("closed ring").Features)).Should().Equal("ring/high:tube", "ring/left:tube", "ring/low:tube", "ring/right:tube");
        Names(Regenerate(Get("torus").Features)).Should().Equal("ring/round:tube");
    }

    [Fact]
    public void The_chain_through_one_entity_is_the_path_named_entity_by_entity()
    {
        var listed = Regenerate(Elbow());
        var chained = Regenerate($"{Route}, {Section(AcrossX, (0, 0, 5, "wall"))}, {Sweep("""{"sketch": "route", "from": "l2"}""")}");
        var shuffled = Regenerate($"{Route}, {Section(AcrossX, (0, 0, 5, "wall"))}, {Sweep("""{"sketch": "route", "entities": ["l2", "l1", "a1"]}""")}");

        foreach (var r in new[] { chained, shuffled })
        {
            r.Diagnostics.Should().BeEmpty();
            Names(r).Should().Equal(Names(listed));
            Volume(r).Should().BeApproximately(Volume(listed), 1e-9);
        }
    }

    [Fact]
    public void A_profile_at_the_far_end_sweeps_the_path_backwards()
    {
        // Square to the path at (60, 50), where l2 ends going +y: the sweep starts there.
        var r = Regenerate($"{Route}, {Section("""{"plane": {"origin": [60, 50, 0], "x": [1, 0, 0], "normal": [0, 1, 0]}}""", (0, 0, 5, "wall"))}, {Sweep()}");

        r.Diagnostics.Should().BeEmpty();
        Names(r).Should().Equal("pipe/a1:wall", "pipe/end", "pipe/l1:wall", "pipe/l2:wall", "pipe/start");
        Volume(r).Should().BeApproximately(Math.PI * 25 * ElbowLength, Math.PI * 25 * ElbowLength * 1e-3);
        r.Part.Surfaces.Single(s => s.Source == "pipe/start").Origin.Y.Should().BeApproximately(50, 1e-12);
    }

    [Fact]
    public void Faces_keep_their_names_when_the_path_changes()
    {
        var modeler = new FrepModeler(Settings);
        var doc = Read(Elbow());
        // A wider turn and a longer leg: the same entities, so the same faces.
        var edited = DocumentEdits.Apply(doc, [
            JsonNode.Parse("""{ "set": "route.points.c", "value": [40, 30] }""")!,
            JsonNode.Parse("""{ "set": "route.points.d", "value": [70, 30] }""")!,
            JsonNode.Parse("""{ "set": "route.points.e", "value": [70, 80] }""")!,
        ]);
        var (before, after) = (modeler.Regenerate(doc), modeler.Regenerate(edited));

        after.Diagnostics.Should().BeEmpty();
        foreach (var source in before.Topology!.Faces.SelectMany(f => f.Sources))
            ReferenceResolver.Resolve(new FaceReference(source, source), after.Topology!).Status.Should().Be(ReferenceStatus.Resolved, source);
        var length = 40 + 50 + 30 * Math.PI / 2;
        Volume(after).Should().BeApproximately(Math.PI * 25 * length, Math.PI * 25 * length * 1e-3);
    }

    [Fact]
    public void Pattern_copies_of_a_sweep_are_named_after_their_copy()
    {
        var r = Regenerate(Get("bored pipes on a block, patterned").Features);

        Names(r).Should().Contain(["pipe/a1:wall", "drill/l2:bore", "row/1:pipe/a1:wall", "row/1:pipe/end", "row/1:drill/l2:bore"]);
        r.Part.Surfaces.Single(s => s.Source == "row/1:pipe/end").Origin.Should().Be(new Point3D(60, 50, 15));
    }

    [Fact]
    public void A_sweep_can_be_a_lattice_region()
    {
        // Cubic struts 1.2 thick on a 4 mm grid in the pipe; the flange stays solid. Struts graze the round wall here and
        // there (gaps the mesher reports), so the field is checked as compiled.
        var part = new Compiler(Settings).Compile(Read($$"""
            {"id": "flange", "type": "block", "op": "new", "origin": [-10, -15, -15], "size": [10, 30, 30]},
            {{Route}}, {{Section(AcrossX, (0, 0, 5, "wall"))}}, {{Sweep(op: "join")}},
            {"id": "infill", "type": "lattice", "kind": "cubic", "cell": 4, "thickness": 1.2, "region": "pipe"}
            """));

        part.Diagnostics.Should().BeEmpty();
        part.Surfaces.Select(s => s.Source).Should().Contain(["infill/struts", "infill/region:pipe/a1:wall", "infill/region:pipe/l1+a1"]);
        var field = part.Field!;
        field.Value(new Point3D(-5, 0, 0)).Should().Be(-5, "the flange is solid");
        field.Value(new Point3D(22, 2, 2)).Should().BeGreaterThan(0, "a cell's middle in the pipe is void");
        field.Value(new Point3D(22, 0, 0)).Should().BeApproximately(-0.6, 1e-12, "the strut along the pipe's axis is there");
        field.Value(new Point3D(50, 2, 2)).Should().BeGreaterThan(0, "the bend is in the region too");
        field.Value(new Point3D(50, 0, 0)).Should().BeApproximately(-0.6, 1e-12);
        FieldProperties.Check(part);
    }

    [Fact]
    public void Sweeps_round_trip_through_the_canonical_format()
    {
        foreach (var part in Parts())
        {
            var text = CanonicalFormat.Write(Document(part.Name));
            CanonicalFormat.Write(CanonicalFormat.Read(text)).Should().Be(text);
        }
        var sweep = (Sweep)Document("pipe on a block, bored")["drill"];
        sweep.Path.Should().BeEquivalentTo(new SweepPath("route", null, "a1"));
        CanonicalFormat.Write(Read(Elbow())).Should().Contain("""
                  "path": {
                    "sketch": "route",
                    "entities": [
            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void A_sweep_that_touches_itself_is_reported()
    {
        // Two stretches of the solid meet in a point: a contact of zero thickness no exact face can be bounded at.
        var r = Regenerate(Document(Touching));

        r.Diagnostics.Should().ContainSingle().Which.Should().Be(new Diagnostic(Diagnostic.TangentContact, "pipe",
            "the solid touches itself at (0, 3, 0): the corner of pipe/a1:base+pipe/a2:base+pipe/l1:base+pipe/l2:base, pipe/start, pipe/l1:right "
            + "lies on the edge between pipe/a1:base+pipe/a2:base+pipe/l1:base+pipe/l2:base and pipe/a2:right, a contact of zero thickness"));
    }

    [Theory]
    [InlineData(4)] // the bend's outer side clears the start's corner by 5.04 mm
    [InlineData(8)] // by 1.37 mm
    [InlineData(9)] // by 0.65 mm
    [InlineData(11)] // it runs 0.58 mm into the start: the two overlap, and their union touches nowhere in a point
    public void A_bend_clear_of_the_start_or_into_it_is_not_reported(double radius)
    {
        // The bend of the given radius round (5 − radius, 15), on from where the loop ends at (5, 15).
        var r = Regenerate(DocumentEdits.Apply(Document(Touching), [
            JsonNode.Parse(FormattableString.Invariant($$"""{ "set": "route.points.k", "value": [{{5 - radius}}, 15] }"""))!,
            JsonNode.Parse(FormattableString.Invariant($$"""{ "set": "route.points.g", "value": [{{5 - radius}}, {{15 - radius}}] }"""))!,
            JsonNode.Parse(FormattableString.Invariant($$"""{ "set": "route.points.h", "value": [-25, {{15 - radius}}] }"""))!,
        ]));

        r.Diagnostics.Should().BeEmpty();
        if (radius > 10) return;
        var volume = 12 * (20 + 30 - radius + 1.5 * Math.PI * 15 + 0.5 * Math.PI * radius);
        Volume(r).Should().BeApproximately(volume, volume * 1e-3);
    }

    // In the XY plane, every way a path can fail: a corner, a branch, two pieces, and a loop that closes at an angle.
    const string Paths = """
        {"id": "route", "type": "sketch", "on": {"plane": {"origin": [0, 0, 0], "x": [1, 0, 0], "normal": [0, 0, 1]}},
         "points": {"a": [0, 0], "b": [40, 0], "c": [40, 20], "d": [60, 20], "e": [60, 50], "g": [40, 30], "h": [80, 0], "k": [0, 18.75], "t": [0, 40],
                    "u": [-10, 0], "v": [40, 40], "w": [40, -10]},
         "entities": [{"id": "l1", "line": ["a", "b"]}, {"id": "a1", "arc": {"center": "c", "from": "b", "to": "d"}}, {"id": "l2", "line": ["d", "e"]},
                      {"id": "up", "line": ["b", "g"]}, {"id": "on", "line": ["b", "h"]}, {"id": "down", "line": ["w", "b"]},
                      {"id": "m1", "line": ["u", "b"]}, {"id": "m2", "arc": {"center": "c", "from": "b", "to": "v"}}, {"id": "m3", "line": ["v", "t"]},
                      {"id": "m4", "arc": {"center": "k", "from": "t", "to": "u"}}]}
        """;

    [Theory]
    [InlineData("corner", """{"sketch": "route", "entities": ["l1", "up"]}""", "turns 90° where l1 meets up")]
    [InlineData("branch", """{"sketch": "route", "entities": ["l1", "up", "on"]}""", "point 'b' joins 3 entities")]
    [InlineData("two pieces", """{"sketch": "route", "entities": ["l1", "l2"]}""", "do not form one chain")]
    [InlineData("closes at an angle", """{"sketch": "route", "entities": ["m1", "m2", "m3", "m4"]}""", "closes at an angle")]
    [InlineData("chain branches", """{"sketch": "route", "from": "l2"}""", "joins")]
    public void Paths_that_are_not_one_smooth_chain_are_refused(string kind, string path, string message)
    {
        var r = Regenerate($"{Paths}, {Section(AcrossX, (0, 0, 2, "wall"))}, {Sweep(path)}");
        output.WriteLine(kind);

        r.Topology.Should().BeNull();
        r.Diagnostics.Should().Contain(d => d.FeatureId == "pipe" && d.Message.Contains(message));
    }

    [Theory]
    [InlineData("tighter than the profile", """{"plane": {"origin": [0, 0, 0], "x": [0, 1, 0], "normal": [1, 0, 0]}}""", 21, "reaches 21 mm towards its centre")]
    [InlineData("off square", """{"plane": {"origin": [0, 0, 0], "x": [0, 0, 1], "normal": [1, 0.1, 0]}}""", 5, "from square")]
    [InlineData("not at an end", """{"plane": {"origin": [10, 0, 0], "x": [0, 1, 0], "normal": [1, 0, 0]}}""", 5, "does not cross an end")]
    [InlineData("in the path's plane", Xy, 5, "90° from square")]
    public void Profiles_the_path_cannot_carry_exactly_are_refused(string kind, string on, double radius, string message)
    {
        var r = Regenerate($"{Route}, {Section(on, (0, 0, radius, "wall"))}, {Sweep()}");
        output.WriteLine(kind);

        r.Topology.Should().BeNull();
        r.Diagnostics.Should().ContainSingle(d => d.Code == Diagnostic.InvalidParameter && d.FeatureId == "pipe").Which.Message.Should().Contain(message);
    }

    [Theory]
    [InlineData("""{"sketch": "nowhere", "entities": ["l1"]}""", "not an earlier sketch")]
    [InlineData("""{"sketch": "route", "entities": ["l1", "zz"]}""", "'zz' is not an entity of sketch route")]
    [InlineData("""{"sketch": "route", "entities": ["l1"], "from": "l1"}""", "not both")]
    [InlineData("""{"sketch": "route", "entities": []}""", "one or more distinct entities")]
    public void A_path_that_names_nothing_real_is_invalid(string path, string message)
    {
        var r = Regenerate($"{Route}, {Section(AcrossX, (0, 0, 5, "wall"))}, {Sweep(path)}");

        r.Diagnostics.Should().ContainSingle(d => d.Code == Diagnostic.InvalidParameter).Which.Message.Should().Contain(message);
    }
}
