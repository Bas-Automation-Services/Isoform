using static System.FormattableString;
using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Geometry;
using Isoform.Modeling;
using Xunit.Abstractions;

namespace Isoform.Tests;

/// <summary>
/// Fillets and chamfers on a sweep's edges: its start and end caps, a corner of its profile along the path, and where
/// it meets the part it joins. Each is exact (measured against a brute-force rolling ball or setback, point by point
/// and by volume) or refused with a diagnostic.
/// </summary>
public class SweepEdgeTests(ITestOutputHelper output)
{
    static readonly ModelingSettings Settings = new() { CellSize = 0.5 };

    static readonly Func<string, PartDocument> Read = features => CanonicalFormat.Read($$"""
        {"format": "isoform/0", "units": "mm", "features": [{{features}}]}
        """);

    // In the XY plane: 40 along x, a quarter turn left of radius 20 round (40, 20), 30 up along y.
    const string Route = """
        {"id": "route", "type": "sketch", "on": {"plane": {"origin": [0, 0, 0], "x": [1, 0, 0], "normal": [0, 0, 1]}},
         "points": {"a": [0, 0], "b": [40, 0], "c": [40, 20], "d": [60, 20], "e": [60, 50]},
         "entities": [{"id": "l1", "line": ["a", "b"]}, {"id": "a1", "arc": {"center": "c", "from": "b", "to": "d"}}, {"id": "l2", "line": ["d", "e"]}]}
        """;

    const double Length = 40 + 30 + 20 * Math.PI / 2;

    // A Ø10 circle on the plane x = 0 (sketch x is world y, sketch y world z), or a 6 × 6 square: high is z = 3, right
    // y = −3 (the outside of the turn).
    static string Section(string shape, double x = 0) => shape == "circle"
        ? $$$"""{"id": "section", "type": "sketch", "on": {"plane": {"origin": [{{{x.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}, 0, 0], "x": [0, 1, 0], "normal": [1, 0, 0]}}, "points": {"o": [0, 0]}, "entities": [{"id": "wall", "circle": {"center": "o", "radius": 5}}]}"""
        : """
          {"id": "section", "type": "sketch", "on": {"plane": {"origin": [0, 0, 0], "x": [0, 1, 0], "normal": [1, 0, 0]}},
           "points": {"p": [-3, -3], "q": [3, -3], "r": [3, 3], "s": [-3, 3]},
           "entities": [{"id": "low", "line": ["p", "q"]}, {"id": "left", "line": ["q", "r"]}, {"id": "high", "line": ["r", "s"]}, {"id": "right", "line": ["s", "p"]}]}
          """;

    static string Sweep(string op = "new", string path = """["l1", "a1", "l2"]""") =>
        $$$"""{"id": "pipe", "type": "sweep", "op": "{{{op}}}", "sketch": "section", "path": {"sketch": "route", "entities": {{{path}}}}}""";

    /// <summary>A fillet or chamfer on the edge [a, b], or with " loop" round face a.</summary>
    static string Edge(string type, string a, string b, double size)
    {
        var (kind, key) = type.StartsWith("fillet") ? ("fillet", "radius") : ("chamfer", "distance");
        var where = type.EndsWith(" loop") ? $"\"loop\": \"{a}\"" : $"\"edge\": [\"{a}\", \"{b}\"]";
        return Invariant($"{{\"id\": \"round\", \"type\": \"{kind}\", {where}, \"{key}\": {size}}}");
    }

    /// <summary>
    /// The sweep's lateral offset to the right of the path at p (in the XY plane) and how far along the path it is; the
    /// path's own pieces decide by where p lies.
    /// </summary>
    static (double Right, double Along) OnRoute(Point3D p) =>
        p.X <= 40 ? (-p.Y, p.X)
        : p.Y <= 20 ? (Math.Sqrt(Sq(p.X - 40) + Sq(p.Y - 20)) - 20, 40 + 20 * Math.Atan2(p.X - 40, 20 - p.Y))
        : (p.X - 60, 40 + 10 * Math.PI + p.Y - 20);

    static double Sq(double x) => x * x;

    /// <summary>Whether the round of radius r (or a chamfer of distance r) takes p away, at distances a and b from its two faces.</summary>
    static bool Rounded(double a, double b, double r, bool round) =>
        a < r && b < r && (round ? Sq(r - a) + Sq(r - b) > r * r : a + b < r);

    /// <summary>
    /// Points round the edge where the part before and after differ from the brute force: the material before with the
    /// corner taken away (or, inside, the corner gap filled). Points within 1e-6 of either side's boundary are skipped.
    /// </summary>
    int Mismatches(Field before, Field after, IEnumerable<Point3D> points, Func<Point3D, (double A, double B)> distances, double r, bool round, bool inside = false)
    {
        var (bad, checkedCount) = (0, 0);
        foreach (var p in points)
        {
            var (a, b) = distances(p);
            var s = before.Value(p);
            var margin = new[] { Math.Abs(s), Math.Abs(a - r), Math.Abs(b - r), round ? Math.Abs(Sq(r - a) + Sq(r - b) - r * r) : Math.Abs(a + b - r), Math.Abs(after.Value(p)) }.Min();
            if (margin < 1e-6) continue;
            checkedCount++;
            var expected = inside ? s < 0 || a > 0 && b > 0 && Rounded(a, b, r, round) : s < 0 && !Rounded(a, b, r, round);
            if (expected == after.Value(p) < 0) continue;
            if (bad++ < 5) output.WriteLine($"  at ({p.X:F3}, {p.Y:F3}, {p.Z:F3}): expected {(expected ? "material" : "empty")}, field {after.Value(p):G4}");
        }
        output.WriteLine($"{bad} of {checkedCount} points differ");
        checkedCount.Should().BeGreaterThan(1000);
        return bad;
    }

    CompiledPart Compile(string features)
    {
        var part = new Compiler(Settings).Compile(Read(features));
        foreach (var d in part.Diagnostics) output.WriteLine($"{d.Code} {d.FeatureId}: {d.Message}");
        return part;
    }

    double Volume(string features)
    {
        var r = new FrepModeler(Settings with { CellSize = 0.25 }).Regenerate(Read(features));
        foreach (var d in r.Diagnostics) output.WriteLine($"{d.Code} {d.FeatureId}: {d.Message}");
        r.Diagnostics.Should().BeEmpty();
        r.Mesh!.IsWatertight().Should().BeTrue();
        return MassProperties.Of(r.Part.Field!, r.Mesh!).Volume;
    }

    // The corner a rolling ball of radius r leaves out of a right-angled edge, and its centroid's distance from either face.
    static double Spandrel(double r) => r * r * (1 - Math.PI / 4);
    static double Centroid(double r) => r * (5.0 / 6 - Math.PI / 4) / (1 - Math.PI / 4);

    static IEnumerable<Point3D> Random(int seed, int count, Point3D lo, Point3D hi)
    {
        var rng = new Random(seed);
        for (var i = 0; i < count; i++)
            yield return new Point3D(lo.X + rng.NextDouble() * (hi.X - lo.X), lo.Y + rng.NextDouble() * (hi.Y - lo.Y), lo.Z + rng.NextDouble() * (hi.Z - lo.Z));
    }

    [Theory]
    [InlineData("fillet", 40, 30)]
    [InlineData("chamfer", 40, 30)]
    // Legs of 3: where a short line meets the bend, the bend's ring reaches back over the cap.
    [InlineData("fillet", 3, 3)]
    [InlineData("chamfer", 3, 3)]
    [InlineData("fillet loop", 40, 30)]
    [InlineData("fillet loop", 3, 3)]
    [InlineData("chamfer loop", 3, 3)]
    public void A_cap_edge_is_rounded_all_round(string type, double first, double last)
    {
        var (x0, y1) = (40 - first, 20 + last);
        var route = Route.Replace("\"a\": [0, 0]", Invariant($"\"a\": [{x0}, 0]")).Replace("\"e\": [60, 50]", Invariant($"\"e\": [60, {y1}]"));
        var section = Section("circle", x0);
        var before = Compile($"{route}, {section}, {Sweep()}").Field!;
        var start = Compile($"{route}, {section}, {Sweep()}, {Edge(type, "pipe/start", "pipe/l1:wall", 1.5)}");
        var end = Compile($"{route}, {section}, {Sweep()}, {Edge(type, "pipe/end", "pipe/l2:wall", 1.5)}");

        start.Diagnostics.Should().BeEmpty();
        end.Diagnostics.Should().BeEmpty();
        Mismatches(before, start.Field!, Random(1, 20000, new Point3D(x0 - 1, -6, -6), new Point3D(x0 + 4, 6, 6)),
            p => (p.X - x0, 5 - Math.Sqrt(Sq(p.Y) + Sq(p.Z))), 1.5, type.StartsWith("fillet")).Should().Be(0);
        Mismatches(before, end.Field!, Random(2, 20000, new Point3D(54, y1 - 4, -6), new Point3D(66, y1 + 1, 6)),
            p => (y1 - p.Y, 5 - Math.Sqrt(Sq(p.X - 60) + Sq(p.Z))), 1.5, type.StartsWith("fillet")).Should().Be(0);
    }

    [Theory]
    [InlineData("fillet", "pipe/l1:high", "pipe/l1:right")]
    [InlineData("fillet", "pipe/a1:right", "pipe/a1:high")]
    [InlineData("chamfer", "pipe/l2:high", "pipe/l2:right")]
    public void A_profile_corner_is_rounded_along_the_whole_path(string type, string a, string b)
    {
        var before = Compile($"{Route}, {Section("square")}, {Sweep()}").Field!;
        var after = Compile($"{Route}, {Section("square")}, {Sweep()}, {Edge(type, a, b, 1)}");

        after.Diagnostics.Should().BeEmpty();
        // Along the whole path, and past both ends.
        Mismatches(before, after.Field!, Random(3, 60000, new Point3D(-1, -4, 1), new Point3D(64, 51, 4)), p => (3 - p.Z, 3 - OnRoute(p).Right), 1, type == "fillet")
            .Should().Be(0);
    }

    [Fact]
    public void A_profile_corner_round_has_the_volume_of_the_ball()
    {
        // The outer top corner: 70 straight, and a quarter turn at radius 23, the spandrel's centroid inside it.
        var volume = Volume($"{Route}, {Section("square")}, {Sweep()}, {Edge("fillet", "pipe/l1:high", "pipe/l1:right", 1)}");
        var exact = 36 * Length - Spandrel(1) * (70 + Math.PI / 2 * (23 - Centroid(1)));

        volume.Should().BeApproximately(exact, exact * 1e-4);
    }

    [Fact]
    public void A_sweep_meeting_a_block_is_rounded_into_it()
    {
        const string flange = """{"id": "flange", "type": "block", "op": "new", "origin": [-10, -15, -15], "size": [10, 30, 30]}""";
        var before = Compile($"{flange}, {Route}, {Section("circle")}, {Sweep("join")}").Field!;
        var after = Compile($"{flange}, {Route}, {Section("circle")}, {Sweep("join")}, {Edge("fillet", "flange/x+", "pipe/l1:wall", 2)}");

        after.Diagnostics.Should().BeEmpty();
        Mismatches(before, after.Field!, Random(4, 20000, new Point3D(-1, -8, -8), new Point3D(3, 8, 8)),
            p => (p.X, Math.Sqrt(Sq(p.Y) + Sq(p.Z)) - 5), 2, round: true, inside: true).Should().Be(0);
    }

    [Fact]
    public void A_cap_edge_between_profile_corners_ends_at_the_sides()
    {
        // The square's top edge at a start 3 before the bend, where the bend's ring reaches back over the cap.
        var route = Route.Replace("\"a\": [0, 0]", "\"a\": [37, 0]");
        var section = Section("square").Replace("\"origin\": [0, 0, 0], \"x\": [0, 1, 0]", "\"origin\": [37, 0, 0], \"x\": [0, 1, 0]");
        var before = Compile($"{route}, {section}, {Sweep()}").Field!;
        var after = Compile($"{route}, {section}, {Sweep()}, {Edge("fillet", "pipe/start", "pipe/l1:high", 1)}");

        after.Diagnostics.Should().BeEmpty();
        Mismatches(before, after.Field!, Random(5, 20000, new Point3D(36, -4, 0), new Point3D(40, 4, 4)), p => (p.X - 37, 3 - p.Z), 1, round: true).Should().Be(0);
    }

    // A Ø6 bore along the whole pipe from its bend.
    static readonly string Bore = """
        {"id": "hole", "type": "sketch", "on": {"plane": {"origin": [0, 0, 0], "x": [0, 1, 0], "normal": [1, 0, 0]}}, "points": {"o": [0, 0]},
         "entities": [{"id": "bore", "circle": {"center": "o", "radius": 3}}]},
        {"id": "drill", "type": "sweep", "op": "cut", "sketch": "hole", "path": {"sketch": "route", "from": "a1"}}
        """;

    static readonly string Plate = """{"id": "plate", "type": "block", "op": "new", "origin": [-5, -10, -10], "size": [75, 65, 10]}""";

    [Theory]
    [InlineData("start cap on a bend", "fillet", "pipe/start", "pipe/a1:wall")]
    [InlineData("start cap on a bend", "chamfer", "pipe/start", "pipe/a1:wall")]
    [InlineData("block face on a bend", "fillet", "flange/x+", "pipe/a1:wall")]
    [InlineData("pipe half in a plate", "fillet", "plate/z+", "pipe/l1:wall")]
    [InlineData("pipe half in a plate", "chamfer", "plate/z+", "pipe/l1:wall")]
    [InlineData("pipe half in a plate", "fillet", "plate/z+", "pipe/a1:wall")]
    [InlineData("bar half in a plate", "fillet", "plate/z+", "pipe/l1:left")]
    [InlineData("bar half in a plate", "fillet", "plate/z+", "pipe/a1:left")]
    [InlineData("bar cut flat", "fillet", "cut/face", "pipe/l1:left")]
    [InlineData("bar cut flat", "chamfer", "cut/face", "pipe/l1:left")]
    [InlineData("bar cut flat", "fillet", "cut/face", "pipe/a1:left")]
    [InlineData("groove", "fillet", "plate/z+", "pipe/l1:left")]
    [InlineData("groove", "fillet", "plate/z+", "pipe/a1:left")]
    [InlineData("groove", "chamfer", "plate/z+", "pipe/l2:left")]
    [InlineData("bored pipe", "fillet", "pipe/end", "drill/l2:bore")]
    [InlineData("groove", "fillet loop", "plate/z+", "")]
    [InlineData("groove", "chamfer loop", "plate/z+", "")]
    [InlineData("round groove", "fillet loop", "plate/z+", "")]
    public void An_edge_with_no_exact_round_is_refused(string kind, string type, string a, string b)
    {
        // Starting on the arc at (40, 0), the plane across the path holds the arc's axis: the ball rolling between it and
        // the torus has no closed form. An edge along the path runs on from one piece into the next, as a line, then a
        // circle round the bend's axis: a round of one piece would end in a step at the joint.
        var features = kind switch
        {
            "start cap on a bend" => $"{Route}, {Section("circle", 40)}, {Sweep("new", """["a1", "l2"]""")}",
            "block face on a bend" => $$"""{"id": "flange", "type": "block", "op": "new", "origin": [30, -15, -15], "size": [10, 30, 30]}, {{Route}}, {{Section("circle", 40)}}, {{Sweep("join", """["a1", "l2"]""")}}""",
            "pipe half in a plate" => $"{Plate}, {Route}, {Section("circle")}, {Sweep("join")}",
            "bar half in a plate" => $"{Plate}, {Route}, {Section("square")}, {Sweep("join")}",
            "bar cut flat" => $$"""{{Route}}, {{Section("square")}}, {{Sweep()}}, {"id": "cut", "type": "plane-cut", "point": [0, 0, 2], "normal": [0, 0, 1]}""",
            "groove" => $"{Plate}, {Route}, {Section("square")}, {Sweep("cut")}",
            "round groove" => $"{Plate}, {Route}, {Section("circle")}, {Sweep("cut")}",
            _ => $"{Route}, {Section("circle")}, {Sweep()}, {Bore}",
        };
        var compiled = Compile($"{features}, {Edge(type, a, b, 1)}");

        compiled.Diagnostics.Should().ContainSingle().Which.Code.Should().Be(type.StartsWith("fillet") ? Diagnostic.UnsupportedFillet : Diagnostic.UnsupportedChamfer);
    }
}
