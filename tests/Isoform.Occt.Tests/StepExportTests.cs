using System.Text.Json.Nodes;
using Isoform.Document;
using Isoform.Modeling;
using Isoform.Step;
using Isoform.Tests;
using Xunit.Abstractions;

namespace Isoform.Occt.Tests;

/// <summary>
/// K6: Isoform's own exact B-rep, written as STEP by Isoform and read back by OCCT. OCCT must find valid solids with
/// the B-rep's face, edge and vertex counts and the analytic volume, both with its reader's shape healing and without
/// it (so the file itself is complete); where the OCCT baseline can model the same document, every vertex and edge
/// must also lie within the modelling tolerance of OCCT's own.
/// </summary>
public class StepExportTests(ITestOutputHelper output)
{
    static readonly SketchPlane Xy = new([0, 0, 0], [1, 0, 0], [0, 0, 1]);
    static readonly SketchPlane Xz = new([0, 0, 0], [1, 0, 0], [0, -1, 0]);
    static readonly SketchPlane Top = new([0, 0, 10], [1, 0, 0], [0, 0, 1]);
    static readonly Block Plate = new("plate", BooleanOp.New, [0, 0, 0], [100, 50, 10]);

    static Profile Loop(double[] start, params (string Id, double U, double V, double Bulge)[] edges) =>
        new(start, edges.Select(e => new ProfileEdge(e.Id, [e.U, e.V], e.Bulge)).ToList());

    static Profile Rectangle(double x0, double y0, double x1, double y1) =>
        Loop([x0, y0], ("s", x1, y0, 0), ("e", x1, y1, 0), ("n", x0, y1, 0), ("w", x0, y0, 0));

    // A stadium 20 long between two R5 ends, from (40, 20) to (60, 30).
    static Profile Stadium() => Loop([40, 20], ("a", 60, 20, 0), ("right", 60, 30, 1), ("b", 40, 30, 0), ("left", 40, 20, 1));

    /// <summary>
    /// A part and its analytic volume. <paramref name="Healed"/>: the file still needs the reader's healing (a sphere
    /// cap holding a pole needs a degenerate edge there, which STEP cannot say; OCCT's own files leave it out too).
    /// <paramref name="Baseline"/>: OCCT's own modeller builds the same shape from the document, so its vertices and edges
    /// can be compared.
    /// </summary>
    public sealed record Part(string Name, PartDocument Doc, double Volume, int Solids = 1, bool Healed = false, bool Baseline = true);

    // The corner a rolling ball of radius r leaves out of a right-angled edge, its centroid's distance from either
    // face, and what two such edges mitring at a convex corner leave out twice (2 · area · centroid).
    static double Spandrel(double r) => r * r * (1 - Math.PI / 4);
    static double Centroid(double r) => r * (5.0 / 6 - Math.PI / 4) / (1 - Math.PI / 4);
    static double Mitre(double r) => r * r * r * (5.0 / 3 - Math.PI / 2);

    public static IEnumerable<Part> Parts()
    {
        yield return new("block", new([new Block("box", BooleanOp.New, [0, 0, 0], [40, 30, 20])]), 40 * 30 * 20);
        yield return new("plate with hole", new([Plate, new Hole("hole", [30, 25], 10)]), 50000 - Math.PI * 25 * 10);
        yield return new("two-hole plate", new([Plate, new Hole("hole", [30, 25], 10), new Hole("hole2", [70, 25], 8)]),
            50000 - Math.PI * (25 + 16) * 10);
        // A groove across the top and a 45° cut off the x+ top edge: 50000 − 20·50·4 − ½·10·10·50.
        yield return new("slot and plane cut", new(
            [
                Plate,
                new Block("slot", BooleanOp.Cut, [40, -1, 6], [20, 52, 5]),
                new PlaneCut("cut", [90, 25, 10], [1, 0, 1]),
            ]), 50000 - 4000 - 2500);
        yield return new("L bracket", new([new Extrude("bracket", BooleanOp.New, Xy, Loop([0, 0],
            ("bottom", 40, 0, 0), ("tip", 40, 5, 0), ("inner", 5, 5, 0), ("riser", 5, 30, 0), ("top", 0, 30, 0), ("back", 0, 0, 0)), 0, 20)]),
            (40 * 5 + 5 * 25) * 20);
        yield return new("stadium", new([new Extrude("slot", BooleanOp.New, Xy, Loop([0, 0],
            ("a", 20, 0, 0), ("right", 20, 10, 1), ("b", 0, 10, 0), ("left", 0, 0, 1)), 0, 5)]),
            (20 * 10 + Math.PI * 25) * 5);
        yield return new("stepped shaft", new([new Revolve("shaft", BooleanOp.New, Xz, Loop([0, 0],
            ("bottom", 10, 0, 0), ("big", 10, 20, 0), ("step", 6, 20, 0), ("small", 6, 40, 0), ("top", 0, 40, 0), ("axis", 0, 0, 0)))]),
            Math.PI * (100 * 20 + 36 * 20));

        // Beyond planes and cylinders: cones, spheres, tori, ellipses, a cylinder merged from two arcs.
        yield return new("cone with a dome", new([new Revolve("nose", BooleanOp.New, Xz, Loop([0, 0],
            ("base", 10, 0, 0), ("side", 10, 10, 0), ("cone", 5, 20, 0), ("dome", 0, 25, Math.Tan(Math.PI / 8)), ("axis", 0, 0, 0)))]),
            Math.PI * (1000 + 10.0 / 3 * (100 + 50 + 25) + 2.0 / 3 * 125), Healed: true);
        // A shallower dome: a cap of any height keeps its axis, so its rim is a circle of latitude (an exact image).
        yield return new("flat dome", new([new Revolve("cap", BooleanOp.New, Xz, Loop([0, 0],
            ("base", 8, 0, 0), ("side", 8, 4, 0), ("dome", 0, 20 - Math.Sqrt(192), Math.Tan(Math.PI / 24)), ("axis", 0, 0, 0)))]),
            Math.PI * 64 * 4 + Math.PI * Math.Pow(16 - Math.Sqrt(192), 2) * (3 * 16 - (16 - Math.Sqrt(192))) / 3, Healed: true);
        yield return new("torus", new([new Revolve("ring", BooleanOp.New, Xz, Loop([20, -3], ("outer", 20, 3, 1), ("inner", 20, -3, 1)))]),
            2 * Math.PI * Math.PI * 20 * 9);
        yield return new("quarter turn", new([new Revolve("elbow", BooleanOp.New, Xz, Loop([5, 0],
            ("low", 10, 0, 0), ("outer", 10, 4, 0), ("high", 5, 4, 0), ("inner", 5, 0, 0)), 90)]),
            0.25 * 2 * Math.PI * 7.5 * 20);
        yield return new("disc from two arcs", new([new Extrude("disc", BooleanOp.New, Xy, Loop([-20, 0], ("a", 20, 0, 1), ("b", -20, 0, 1)), 0, 8)]),
            Math.PI * 400 * 8);
        // The cut plane z = 10 − (x − 20)·tan 30° crosses the hole wall in an ellipse; the hole stays clear of the
        // plate's edges, so its volume is the plate's minus a cylinder averaging the cut's height at x = 30.
        var slope = Math.Tan(Math.PI / 6);
        yield return new("oblique cut through a hole", new(
            [
                Plate,
                new Hole("hole", [30, 25], 10),
                new PlaneCut("cut", [20, 25, 10], [slope, 0, 1]),
            ]), Prism(slope) - Math.PI * 25 * (10 - 10 * slope));

        // Rolling-ball fillets between planes are cylinders, between a plane and a perpendicular cylinder tori.
        yield return new("edit-suite base", EditSuite.Base, 50000 - Math.PI * 25 * 10 - Spandrel(1) * 4 * 50);
        // Pappus: the cut-off section sits its centroid outside the hole wall.
        yield return new("filleted hole edge", new([Plate, new Hole("hole", [30, 25], 10), new Fillet("round", ["plate/z+", "hole/wall"], 2)]),
            50000 - Math.PI * 25 * 10 - 2 * Math.PI * (5 + Centroid(2)) * Spandrel(2));
        yield return new("L bracket with inside fillet", new(
            [
                new Extrude("bracket", BooleanOp.New, Xy, Loop([0, 0],
                    ("bottom", 40, 0, 0), ("tip", 40, 5, 0), ("inner", 5, 5, 0), ("riser", 5, 30, 0), ("top", 0, 30, 0), ("back", 0, 0, 0)), 0, 20),
                new Fillet("inside", ["bracket/inner", "bracket/riser"], 3),
            ]), (40 * 5 + 5 * 25 + Spandrel(3)) * 20);

        // Loop fillets and chamfers: cylinders along lines and tori along arcs (planes and cones for a chamfer),
        // tangent where a line runs into an arc, mitred at convex corners.
        var boss = 50000 + (20 * 10 + Math.PI * 25) * 8;
        yield return new("boss with a loop round", new([Plate, new Extrude("boss", BooleanOp.Join, Top, Stadium(), 0, 8), new Fillet("round", [], 1, Loop: "boss/end")]),
            boss - 40 * Spandrel(1) - 2 * Math.PI * (5 - Centroid(1)) * Spandrel(1));
        yield return new("boss with a loop chamfer", new([Plate, new Extrude("boss", BooleanOp.Join, Top, Stadium(), 0, 8), new Chamfer("cut", [], 1, Loop: "boss/end")]),
            boss - 40 * 0.5 - 2 * Math.PI * (5 * 0.5 - 1.0 / 6));
        yield return new("outline with a loop round", new([new Extrude("outline", BooleanOp.New, Xy, Rectangle(0, 0, 60, 40), 0, 10), new Fillet("round", [], 2, Loop: "outline/end")]),
            60 * 40 * 10 - 200 * Spandrel(2) + 4 * Mitre(2));
        yield return new("pocket floor with a loop round", new(
                [Plate, new Extrude("pocket", BooleanOp.Cut, Xy, Rectangle(20, 10, 60, 40), 4, 11), new Fillet("floor", [], 2, Loop: "pocket/start")]),
            50000 - 40 * 30 * 6 + 140 * Spandrel(2) - 4 * Mitre(2));
        yield return new("rim of a cut-out with a loop round", new([Plate, new Extrude("slot", BooleanOp.Cut, Xy, Stadium(), -1, 11), new Fillet("round", [], 1, Loop: "plate/z+")]),
            50000 - (20 * 10 + Math.PI * 25) * 10 - (300 * Spandrel(1) - 4 * Mitre(1)) - 40 * Spandrel(1) - 2 * Math.PI * (5 + Centroid(1)) * Spandrel(1));
        // Single edges the profile is not behind, and rims of cuts: the corner along the edge is taken away, ending on
        // the faces beside it (the foot's top and the window's walls gain the fillet's ends).
        yield return new("L bracket with a riser fillet", new([new Extrude("bracket", BooleanOp.New, Xy, Loop([0, 0],
                ("bottom", 40, 0, 0), ("tip", 40, 5, 0), ("inner", 5, 5, 0), ("riser", 5, 30, 0), ("top", 0, 30, 0), ("back", 0, 0, 0)), 0, 20),
                new Fillet("round", ["bracket/end", "bracket/riser"], 1)]),
            (40 * 5 + 5 * 25) * 20 - 25 * Spandrel(1));
        yield return new("rim edge of a window", new([Plate, new Extrude("window", BooleanOp.Cut, Xy, Rectangle(30, 15, 70, 35), -1, 11), new Fillet("round", ["plate/z+", "window/s"], 1)]),
            50000 - 40 * 20 * 10 - 40 * Spandrel(1));
        yield return new("rim edge of a slot, round its tangent rim", new([Plate, new Extrude("slot", BooleanOp.Cut, Xy, Stadium(), -1, 11), new Fillet("round", ["plate/z+", "slot/a"], 1)]),
            50000 - (20 * 10 + Math.PI * 25) * 10 - 40 * Spandrel(1) - 2 * Math.PI * (5 + Centroid(1)) * Spandrel(1));
        // Open tangent runs: a piece per edge, a cylinder along a line and a torus round an arc meeting in the plane
        // across their junction. The riser runs over an R3 bend into the top (Pappus round the bend); the window's
        // rim over an R5 corner (the spandrel's centroid outside the wall).
        var bent = Loop([0, 0], ("bottom", 40, 0, 0), ("tip", 40, 5, 0), ("inner", 5, 5, 0), ("riser", 5, 27, 0), ("bend", 2, 30, Math.Tan(Math.PI / 8)),
            ("top", 0, 30, 0), ("back", 0, 0, 0));
        var bentArea = 40 * 5 + 5 * 22 + Math.PI * 9 / 4 + 2 * 3;
        yield return new("bent riser fillet over its tangent run", new([new Extrude("bracket", BooleanOp.New, Xy, bent, 0, 20), new Fillet("round", ["bracket/end", "bracket/riser"], 1)]),
            bentArea * 20 - 24 * Spandrel(1) - Math.PI / 2 * (3 - Centroid(1)) * Spandrel(1));
        // A 45° chamfer of 1 takes ½ along the lines, and round the bend a triangle whose centroid sits ⅓ in from the side.
        yield return new("bent riser chamfer over its tangent run", new([new Extrude("bracket", BooleanOp.New, Xy, bent, 0, 20), new Chamfer("round", ["bracket/end", "bracket/riser"], 1)]),
            bentArea * 20 - 24 * 0.5 - Math.PI / 2 * (3 - 1.0 / 3) * 0.5);
        var corner = Loop([30, 15], ("s", 65, 15, 0), ("bend", 70, 20, Math.Tan(Math.PI / 8)), ("e", 70, 35, 0), ("n", 30, 35, 0), ("w", 30, 15, 0));
        yield return new("rim edge of a window over its rounded corner", new([Plate, new Extrude("window", BooleanOp.Cut, Xy, corner, -1, 11), new Fillet("round", ["plate/z+", "window/s"], 1)]),
            50000 - (800 - 25 * (1 - Math.PI / 4)) * 10 - 50 * Spandrel(1) - Math.PI / 2 * (5 + Centroid(1)) * Spandrel(1));
        // A circle of two arcs: the loop's torus is two faces, whose edges are the tube circles at the arcs' joints.
        yield return new("clamp: one torus in two faces", new(
                [new Extrude("clamp", BooleanOp.New, Xy, Loop([-12, 0], ("a", 12, 0, 1), ("b", -12, 0, 1)), 0, 14), new Hole("bore", [0, 0], 10), new Fillet("lip", [], 1.5, Loop: "clamp/end")]),
            Math.PI * (144 - 25) * 14 - 2 * Math.PI * (12 - Centroid(1.5)) * Spandrel(1.5) - 2 * Math.PI * (5 + Centroid(1.5)) * Spandrel(1.5));

        // Fillets between two surfaces round one axis: tori round the ball centre circle (Pappus).
        yield return new("countersink rim fillet", CoaxialFilletTests.Countersink(1), CoaxialFilletTests.CountersinkVolume(1));
        yield return new("fillet where a bore leaves a cone", CoaxialFilletTests.BoredCone(0.5), CoaxialFilletTests.BoredConeVolume(0.5));
        yield return new("fillet where a dome meets a plate", CoaxialFilletTests.Dome(1.5), CoaxialFilletTests.DomeVolume(1.5), Healed: true);
        yield return new("fillet where a hemisphere meets a plate", CoaxialFilletTests.Hemisphere(1.5), CoaxialFilletTests.HemisphereVolume(1.5), Healed: true);
        yield return new("fillet where a bore leaves a torus", CoaxialFilletTests.Knob(1), CoaxialFilletTests.KnobVolume(1));

        // Three equal fillets at a corner meet in a sphere, tangent to all three.
        yield return new("box corner rounded by three fillets", CornerFilletTests.Box(3), CornerFilletTests.BoxVolume(3));
        yield return new("block with every edge rounded", CornerFilletTests.Rounded(2), CornerFilletTests.RoundedVolume(2));
        // Two inside fillets round a boss's foot meet round its edge in a horn torus, whose apex is a pole as a sphere's is.
        // OCCT runs the second fillet on differently past the first, so its own model is another shape.
        yield return new("inside fillets meeting round a boss's corner", CornerFilletTests.Boss(3), CornerFilletTests.BossVolume(3), Healed: true, Baseline: false);
        yield return new("boss rounded all round its foot", CornerFilletTests.Boss(2, all: true), CornerFilletTests.BossVolume(2, all: true), Healed: true, Baseline: false);
        yield return new("offset boss rounded all round its foot", new([.. CornerFilletTests.Boss(2, all: true).Features, new Offset("grow", 0.5)]),
            CornerFilletTests.BossVolume(2, all: true, d: 0.5), Healed: true, Baseline: false);

        // Profile corners next to arcs rounded in the profile: a tangent cylinder (extrude) or torus (revolve).
        yield return new("D with a corner rounded into its arc", ArcCornerTests.D(3), ArcCornerTests.DVolume(3));
        yield return new("tab with a reflex corner rounded", ArcCornerTests.Tab(2), ArcCornerTests.TabVolume(2));
        yield return new("lens with its rim rounded between two spheres", ArcCornerTests.Lens(1.5), ArcCornerTests.LensVolume(1.5), Healed: true);

        // Shells: the cavity is a void of the solid.
        yield return new("shelled block", new([new Block("box", BooleanOp.New, [0, 0, 0], [40, 30, 20]), new Shell("shell", 2)]), 24000 - 36 * 26 * 16);
        yield return new("shelled plate with hole", new([Plate, new Hole("hole", [30, 25], 10), new Shell("shell", 1.5)]),
            50000 - Math.PI * 25 * 10 - (97 * 47 * 7 - Math.PI * 6.5 * 6.5 * 7));
        yield return new("shelled edit-suite base", new([.. EditSuite.Base.Features, new Shell("shell", 1)]),
            50000 - Math.PI * 25 * 10 - Spandrel(2) * 50 - (98 * 48 * 8 - Math.PI * 36 * 8 - Spandrel(1) * 48));
        // An offset of a shell: the outside grows by 0.5 and the cavity shrinks by 0.5, a wall of 2.5.
        yield return new("offset shelled plate with hole", new([Plate, new Hole("hole", [30, 25], 10), new Shell("shell", 1.5), new Offset("grow", 0.5)]),
            101 * 51 * 11 - Math.PI * 4.5 * 4.5 * 11 - (96 * 46 * 6 - Math.PI * 7 * 7 * 6));
        // Fillets grow with it, outside (2.5) and in the cavity (2.5 − 2 = 0.5).
        yield return new("offset shelled edit-suite base", new([.. EditSuite.Base.Features, new Shell("shell", 1), new Offset("grow", 0.5)]),
            101 * 51 * 11 - Math.PI * 4.5 * 4.5 * 11 - Spandrel(2.5) * 51 - (97 * 47 * 7 - Math.PI * 6.5 * 6.5 * 7 - Spandrel(0.5) * 47));

        // Bodies stay solids of their own.
        yield return new("two overlapping bodies", new([Plate, new Block("post", BooleanOp.New, [40, 15, 5], [20, 20, 30])]), 50000 + 12000, Solids: 2);

        // Sketches and holes on faces: a constrained sketch's region (a 60 × 40 rectangle round an R4 bore) swept, and
        // holes along a face's normal, through, blind, or at a sketch's points.
        yield return new("sketched plate with a bore", new(
            [
                new Sketch("base", new SketchPlacement(Xy), new Dictionary<string, double[]> { ["a"] = [0, 0], ["b"] = [58, 1], ["c"] = [61, 39], ["d"] = [1, 41], ["h"] = [20, 20] },
                    [new SketchLine("bottom", "a", "b"), new SketchLine("right", "b", "c"), new SketchLine("top", "c", "d"), new SketchLine("left", "d", "a"), new SketchCircle("bore", "h", 3)],
                    [new("horizontal", ["bottom"]), new("vertical", ["right"]), new("horizontal", ["top"]), new("vertical", ["left"]), new("fix", ["a"]), new("fix", ["h"]),
                     new("length", ["bottom"], 60), new("length", ["left"], 40), new("radius", ["bore"], 4)]),
                new Extrude("plate", BooleanOp.New, null, null, 0, 5) { Sketch = "base" },
            ]), 60 * 40 * 5 - Math.PI * 16 * 5);
        yield return new("hole through a side face", new([Plate, new Hole("side", [25, 5], 4) { On = "plate/x+" }]), 50000 - Math.PI * 4 * 100);
        yield return new("blind hole in the top face", new([Plate, new Hole("pocket", [20, 20], 6) { On = "plate/z+", Depth = 4 }]), 50000 - Math.PI * 9 * 4);
        yield return new("blind holes at sketch points", new(
            [
                Plate,
                new Sketch("spots", new SketchPlacement(Face: "plate/z+"), new Dictionary<string, double[]> { ["near"] = [20, 25], ["far"] = [80, 25] }, [], []),
                new Hole("holes", [], 5) { Sketch = "spots", Points = ["near", "far"], Depth = 3 },
            ]), 50000 - 2 * Math.PI * 6.25 * 3);

        // Sweeps: at each joint the two pieces touch along the profile edge in the joint plane, a line (planes and cones)
        // or a circle (cylinders and tori); a torus round a turn is cut open along a circle round its axis. The triangle
        // along a loop and a bend touches itself at a point (see below): here its bend is tighter, clear of the loop.
        foreach (var sweep in SweepTests.Parts().Where(s => s.Name != "triangle along a loop and a bend"))
            yield return new($"sweep: {sweep.Name}", SweepTests.Document(sweep.Name), sweep.Volume);
        yield return new("sweep: triangle along a loop and a tight bend", TightBend(), 12 * (40 + 1.5 * Math.PI * 15 + 0.5 * Math.PI * 4));
    }

    /// <summary>The triangle along a loop and a bend, its bend of radius 4 round (1, 15) instead of 10 round (−5, 15).</summary>
    static PartDocument TightBend() => DocumentEdits.Apply(SweepTests.Document("triangle along a loop and a bend"), [
        JsonNode.Parse("""{ "set": "route.points.k", "value": [1, 15] }""")!,
        JsonNode.Parse("""{ "set": "route.points.g", "value": [1, 11] }""")!,
        JsonNode.Parse("""{ "set": "route.points.h", "value": [-19, 11] }""")!,
    ]);

    /// <summary>The plate below z = 10 − (x − 20)·slope, clipped at z = 0.</summary>
    static double Prism(double slope)
    {
        var zeroAt = 20 + 10 / slope; // where the cut reaches the bottom
        return 50 * (20 * 10 + 0.5 * (zeroAt - 20) * 10);
    }

    public static IEnumerable<object[]> PartNames => Parts().Select(p => new object[] { p.Name });

    [Theory]
    [MemberData(nameof(PartNames))]
    public void Exact_step_reads_back_in_occt_as_the_same_solid(string name)
    {
        var part = Parts().Single(p => p.Name == name);
        var settings = new ModelingSettings { CellSize = 0.5 };
        var frep = new FrepModeler(settings).Regenerate(part.Doc);
        frep.Diagnostics.Should().BeEmpty();
        var t = frep.Topology!;

        var (brep, shape, raw) = RoundTrip(frep, settings, name);
        var counts = Counts(shape);
        output.WriteLine($"Isoform: {t.Faces.Count}F {t.Edges.Count}E {t.Vertices.Count}V; B-rep {Counts(brep)}");
        output.WriteLine($"OCCT:    {counts.Faces}F {counts.Edges}E {counts.Vertices}V (all: {shape.EdgeCount} edges, {shape.VertexCount} vertices), valid {shape.IsValid}, {shape.SolidCount} solid(s), max tolerance {shape.MaxTolerance:E2}");
        output.WriteLine($"volume {shape.Volume:R} vs {part.Volume:R}: relative error {Math.Abs(shape.Volume - part.Volume) / part.Volume:E2}");
        output.WriteLine($"unhealed: valid {raw.IsValid}, max tolerance {raw.MaxTolerance:E2}, relative volume error {Math.Abs(raw.Volume - part.Volume) / part.Volume:E2}");
        if (!raw.IsValid) output.WriteLine(raw.CheckReport);

        shape.IsValid.Should().BeTrue();
        shape.SolidCount.Should().Be(part.Solids);
        counts.Should().Be(Counts(brep));
        // Where the mesh splits a corner (a horn torus's apex) the topology merges the halves: the same faces, edges and corners.
        counts.Should().Be((t.Faces.Count, t.Edges.Count, t.Vertices.Count));

        // The OCCT baseline models blocks, holes along Z, plane cuts and fillets.
        if (part.Solids == 1 && part.Baseline && part.Doc.Features.All(f => f is Block or Hole { On: null, Sketch: null } or PlaneCut or Fillet { Loop: null }))
        {
            var occt = new OcctModeler(settings).Regenerate(part.Doc).Shape!;
            var (vertexGap, edgeGap) = Gaps(shape, occt);
            output.WriteLine($"against OCCT's own model: vertices within {vertexGap:E2}, edges within {edgeGap:E2}; its volume {occt.Volume:R}");
            vertexGap.Should().BeLessThan(1e-7);
            edgeGap.Should().BeLessThan(1e-7);
        }
        shape.Volume.Should().BeApproximately(part.Volume, part.Volume * 1e-11);
        if (part.Healed) return;
        raw.IsValid.Should().BeTrue("the file needs no healing");
        raw.MaxTolerance.Should().BeLessThanOrEqualTo(1e-7);
        raw.Volume.Should().BeApproximately(part.Volume, part.Volume * 1e-11);
    }

    [Fact]
    public void Unsupported_faces_are_named()
    {
        // A smooth blend has no closed-form surface.
        var doc = new PartDocument([Plate, new Block("boss", BooleanOp.Join, [40, 15, 10], [20, 20, 10], Blend: 3)]);
        var settings = new ModelingSettings { CellSize = 0.5 };

        var build = () => ExactBrep.Build(new FrepModeler(settings).Regenerate(doc), settings);

        build.Should().Throw<NotSupportedException>().WithMessage("*boss/blend (Blend)*");
    }

    [Fact]
    public void A_sweep_that_touches_itself_is_named()
    {
        // The bend's outer side, 13 round (−5, 15), runs through the start's corner (0, 3, 0): the base face's boundary
        // touches itself there, a point no face can be bounded at. The F-rep side builds it and says so.
        var settings = new ModelingSettings { CellSize = 0.5 };
        var frep = new FrepModeler(settings).Regenerate(SweepTests.Document("triangle along a loop and a bend"));
        frep.Diagnostics.Should().ContainSingle(d => d.Code == Diagnostic.TangentContact).Which.Message
            .Should().Match("the solid touches itself at (0, 3, 0): the corner of *pipe/start* lies on the edge between *pipe/l1:base* and pipe/a2:right*");

        var build = () => ExactBrep.Build(frep, settings);

        build.Should().Throw<NotSupportedException>().WithMessage("the solid touches itself at (0, 3, 0), a corner on the edge between *pipe/l1:base* and pipe/a2:right");
    }

    [Fact]
    public void A_loop_round_a_reflex_corner_is_named()
    {
        // At a reflex corner of the profile the ball sweeps round the corner line: part of one loop face lies on a
        // torus no profile edge names, which the face's mesh shows.
        var boss = Loop([20, 10], ("a", 60, 10, 0), ("b", 60, 20, 0), ("c", 30, 20, 0), ("d", 30, 40, 0), ("e", 20, 40, 0), ("f", 20, 10, 0));
        var doc = new PartDocument([Plate, new Extrude("boss", BooleanOp.Join, Top, boss, 0, 8), new Fillet("round", [], 1, Loop: "boss/end")]);
        var settings = new ModelingSettings { CellSize = 0.5 };
        var frep = new FrepModeler(settings).Regenerate(doc);
        frep.Diagnostics.Should().BeEmpty();

        var build = () => ExactBrep.Build(frep, settings);

        build.Should().Throw<NotSupportedException>().WithMessage("faces that are not one analytic surface: round/boss/c (off its cylinder by *");
    }

    /// <summary>
    /// The playground's assembly (two brackets and a pin) as STEP product structure: OCCT's XCAF reader finds one
    /// product per distinct part, every occurrence under its component id, placed where the assembly put it.
    /// </summary>
    [Fact]
    public void Assembly_reads_back_with_its_product_structure()
    {
        var settings = new ModelingSettings { CellSize = 0.5 };
        var (_, bundle) = Isoform.Interactive.Examples.Bundles.Single();
        var assembly = new AssemblyModeler(settings).Regenerate(bundle.Assembly, p => bundle.Documents.GetValueOrDefault(p));
        assembly.Diagnostics.Should().BeEmpty();
        var path = Path.Combine(Path.GetTempPath(), $"isoform-{Guid.NewGuid():N}.step");
        File.WriteAllText(path, StepWriter.Write(assembly, settings, "brackets"));
        var read = OcctAssembly.ReadStep(path);
        var plain = OcctShape.ReadStep(path, heal: false);
        File.Delete(path);

        read.Name.Should().Be("brackets");
        read.Occurrences.Select(o => (o.Path, o.Product)).Should().Equal(("lower", "bracket"), ("upper", "bracket"), ("pin", "pin"));
        read.Occurrences.Select(o => o.Part).Should().Equal(0, 0, 1);
        plain.SolidCount.Should().Be(3);
        plain.IsValid.Should().BeTrue();
        foreach (var (o, instance) in read.Occurrences.Zip(assembly.Instances))
        {
            var f = instance.Placement;
            double[] expected = [f.X.X, f.Y.X, f.Z.X, f.Origin.X, f.X.Y, f.Y.Y, f.Z.Y, f.Origin.Y, f.X.Z, f.Y.Z, f.Z.Z, f.Origin.Z];
            o.Placement.Zip(expected).Max(x => Math.Abs(x.First - x.Second)).Should().BeLessThan(1e-12, o.Path);
            o.Shape.IsValid.Should().BeTrue();
            // The same solid as the part written alone, and each of its corners, placed, a corner of the occurrence.
            var brep = ExactBrep.Build(instance.Result, settings);
            var alone = Path.Combine(Path.GetTempPath(), $"isoform-{Guid.NewGuid():N}.step");
            StepWriter.Write(brep, alone, instance.Part);
            o.Shape.Volume.Should().BeApproximately(OcctShape.ReadStep(alone).Volume, 1e-9 * o.Shape.Volume);
            File.Delete(alone);
            var corners = brep.Vertices.Select(v => f.ToWorld(v.Point)).ToList();
            Enumerable.Range(0, o.Shape.VertexCount).Select(o.Shape.VertexPoint)
                .Max(p => corners.Min(c => Math.Sqrt((c.X - p[0]) * (c.X - p[0]) + (c.Y - p[1]) * (c.Y - p[1]) + (c.Z - p[2]) * (c.Z - p[2]))))
                .Should().BeLessThan(1e-9, o.Path);
            output.WriteLine($"{o.Path}: {o.Product}, part {o.Part}, volume {o.Shape.Volume:R}");
        }
    }

    /// <summary>
    /// Writes what exports today: every playground example, every edit-suite result and a two-body part, with
    /// OCCT's verdict on the read-back file (with and without its healing) or the reason the export stopped.
    /// </summary>
    [Fact]
    public void Report_export_coverage()
    {
        var settings = new ModelingSettings { CellSize = 0.5 };
        var parts = Isoform.Interactive.Examples.All.ToList();
        foreach (var id in EditSuite.CaseIds.Select(c => (string)c[0]))
        {
            var c = EditSuite.Case(id);
            parts.Add(($"{id}: {c["title"]}", DocumentEdits.Apply(DocumentEdits.Apply(EditSuite.Base, EditSuite.Edits(c, "setup")), EditSuite.Edits(c, "edit"))));
        }
        parts.Add(("two overlapping bodies", new PartDocument([Plate, new Block("post", BooleanOp.New, [40, 15, 5], [20, 20, 30])])));

        output.WriteLine("| Part | Isoform F/E/V | OCCT read-back | Unhealed | Volume vs mesh |");
        output.WriteLine("|---|---|---|---|---|");
        foreach (var (name, doc) in parts)
        {
            var frep = new FrepModeler(settings).Regenerate(doc);
            if (frep.Topology is not { } t)
            {
                output.WriteLine($"| {name} | no geometry | – | – | – |");
                continue;
            }
            try
            {
                var (brep, shape, raw) = RoundTrip(frep, settings, name);
                var counts = Counts(shape);
                var same = counts == (t.Faces.Count, t.Edges.Count, t.Vertices.Count) ? "same F/E/V" : $"{counts.Faces}/{counts.Edges}/{counts.Vertices}";
                var mesh = MeshVolume(frep.Mesh!);
                output.WriteLine($"| {name} | {t.Faces.Count}/{t.Edges.Count}/{t.Vertices.Count} | {(shape.IsValid ? "valid" : "**invalid**")}, {shape.SolidCount} solid(s), {same}, tolerance {shape.MaxTolerance:G2} " +
                                 $"| {(raw.IsValid ? "valid" : "**invalid**")}, tolerance {raw.MaxTolerance:G2} | {(shape.Volume - mesh) / mesh:E1} |");
            }
            catch (Exception e) when (e is NotSupportedException or OcctException)
            {
                output.WriteLine($"| {name} | {t.Faces.Count}/{t.Edges.Count}/{t.Vertices.Count} | not exported: {e.Message} | – | – |");
            }
        }
    }

    static (Brep Brep, OcctShape Healed, OcctShape Unhealed) RoundTrip(FrepResult frep, ModelingSettings settings, string name)
    {
        var brep = ExactBrep.Build(frep, settings);
        var path = Path.Combine(Path.GetTempPath(), $"isoform-{Guid.NewGuid():N}.step");
        StepWriter.Write(brep, path, name);
        var (shape, raw) = (OcctShape.ReadStep(path), OcctShape.ReadStep(path, heal: false));
        File.Delete(path);
        return (brep, shape, raw);
    }

    static double MeshVolume(Meshing.Mesh mesh)
    {
        var v = mesh.Vertices;
        return mesh.Triangles().Sum(t => v[t.A].AsVector().Dot(v[t.B].AsVector().Cross(v[t.C].AsVector()))) / 6;
    }

    /// <summary>Counts as Isoform's topology counts them: seams (one face) are not edges, corners are where 3+ faces meet.</summary>
    static (int Faces, int Edges, int Vertices) Counts(OcctShape s) => (
        s.FaceCount,
        Enumerable.Range(0, s.EdgeCount).Count(e => s.EdgeFaces(e).Length == 2),
        Enumerable.Range(0, s.VertexCount).Count(v => s.VertexFaces(v).Length >= 3));

    /// <summary>The same counts of the B-rep itself.</summary>
    static (int Faces, int Edges, int Vertices) Counts(Brep b)
    {
        var faces = b.Solids.SelectMany(s => s.Faces).ToList();
        var facesOfEdge = b.Edges.Select(e => faces.Where(f => f.Loops.Any(l => l.Coedges.Any(c => c.Edge == e.Index))).Count()).ToList();
        var facesOfVertex = b.Vertices.Select(v => faces.Count(f => f.Loops.Any(l => l.Coedges.Any(c => b.Edges[c.Edge].Start == v.Index || b.Edges[c.Edge].End == v.Index)))).ToList();
        return (faces.Count, facesOfEdge.Count(n => n == 2), facesOfVertex.Count(n => n >= 3));
    }

    /// <summary>
    /// The largest distance from a corner of <paramref name="a"/> to the nearest corner of <paramref name="b"/> (and
    /// back, so the sets match), and from points along every edge of <paramref name="a"/> to the nearest edge of <paramref name="b"/>.
    /// </summary>
    static (double Vertices, double Edges) Gaps(OcctShape a, OcctShape b)
    {
        double[][] Corners(OcctShape s) => Enumerable.Range(0, s.VertexCount).Where(v => s.VertexFaces(v).Length >= 3).Select(s.VertexPoint).ToArray();
        static double Dist(double[] p, double[] q) => Math.Sqrt(Enumerable.Range(0, 3).Sum(i => (p[i] - q[i]) * (p[i] - q[i])));
        var (ca, cb) = (Corners(a), Corners(b));
        ca.Length.Should().Be(cb.Length);
        var vertices = ca.Length == 0 ? 0 : Math.Max(ca.Max(p => cb.Min(q => Dist(p, q))), cb.Max(p => ca.Min(q => Dist(p, q))));

        var edges = 0.0;
        // Seams sit wherever each modeller chose to cut a periodic face open; only real edges are compared.
        for (var e = 0; e < a.EdgeCount; e++)
            if (a.EdgeFaces(e).Length == 2)
            foreach (var s in new[] { 0.0, 0.25, 0.5, 0.75 })
            {
                var p = a.EdgePoint(e, s);
                edges = Math.Max(edges, Enumerable.Range(0, b.EdgeCount).Min(f => b.EdgeDistance(f, p[0], p[1], p[2])));
            }
        return (vertices, edges);
    }
}
