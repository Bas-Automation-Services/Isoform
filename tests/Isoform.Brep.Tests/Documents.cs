using Isoform.Document;

namespace Isoform.Brep.Tests;

/// <summary>Isoform part documents the managed kernel builds (experiments 1 to 5), with their exact volumes.</summary>
public static class Documents
{
    static readonly SketchPlane Xy = new([0, 0, 0], [1, 0, 0], [0, 0, 1]);

    /// <summary>
    /// A plate as <c>Isoform.Build</c>'s plate method writes one (a sketch and an extrude from 0 to its thickness):
    /// 200 × 120 × 8 with corners rounded to 10, four Ø11 mounting holes, a Ø40 bore and a 60 × 12 slot.
    /// </summary>
    public static PartDocument MountingPlate { get; } = new(
    [
        new Sketch("outline", new SketchPlacement(Xy), new Dictionary<string, double[]>
            {
                ["b0"] = [10, 0], ["b1"] = [190, 0], ["r0"] = [200, 10], ["r1"] = [200, 110],
                ["t0"] = [190, 120], ["t1"] = [10, 120], ["l0"] = [0, 110], ["l1"] = [0, 10],
                ["cbr"] = [190, 10], ["ctr"] = [190, 110], ["ctl"] = [10, 110], ["cbl"] = [10, 10],
                ["m1"] = [20, 20], ["m2"] = [180, 20], ["m3"] = [180, 100], ["m4"] = [20, 100], ["c"] = [100, 60],
                ["s0"] = [70, 94], ["s1"] = [130, 94], ["s2"] = [130, 106], ["s3"] = [70, 106], ["sr"] = [130, 100], ["sl"] = [70, 100],
            },
            [
                new SketchLine("bottom", "b0", "b1"), new SketchArc("corner-br", "cbr", "b1", "r0"),
                new SketchLine("right", "r0", "r1"), new SketchArc("corner-tr", "ctr", "r1", "t0"),
                new SketchLine("top", "t0", "t1"), new SketchArc("corner-tl", "ctl", "t1", "l0"),
                new SketchLine("left", "l0", "l1"), new SketchArc("corner-bl", "cbl", "l1", "b0"),
                new SketchCircle("mount1", "m1", 5.5), new SketchCircle("mount2", "m2", 5.5),
                new SketchCircle("mount3", "m3", 5.5), new SketchCircle("mount4", "m4", 5.5),
                new SketchCircle("bore", "c", 20),
                new SketchLine("slot-lower", "s0", "s1"), new SketchArc("slot-right", "sr", "s1", "s2"),
                new SketchLine("slot-upper", "s2", "s3"), new SketchArc("slot-left", "sl", "s3", "s0"),
            ],
            []),
        new Extrude("plate", BooleanOp.New, null, null, 0, 8) { Sketch = "outline" },
    ]);

    public static readonly double MountingPlateVolume = (22_880 - 457 * Math.PI) * 8;

    /// <summary>
    /// An L-shaped bracket with a concave R10 fillet in its inside corner, an inline profile on an oblique plane,
    /// extruded from −5 to 15.
    /// </summary>
    public static PartDocument Bracket { get; } = new(
    [
        new Extrude("bracket", BooleanOp.New, new SketchPlane([10, -20, 5], [1, 1, 0], [1, -1, 1]),
            new Profile([0, 0],
            [
                new ProfileEdge("base", [60, 0]),
                new ProfileEdge("tip", [60, 10]),
                new ProfileEdge("ledge", [20, 10]),
                new ProfileEdge("fillet", [10, 20], -Math.Tan(Math.PI / 8)),
                new ProfileEdge("upright", [10, 50]),
                new ProfileEdge("top", [0, 50]),
                new ProfileEdge("back", [0, 0]),
            ]), -5, 15),
    ]);

    public static readonly double BracketVolume = (1_100 - 25 * Math.PI) * 20;

    /// <summary>One sketch with two separate rectangles: two bodies.</summary>
    public static PartDocument TwoPads { get; } = new(
    [
        new Sketch("outlines", new SketchPlacement(Xy), new Dictionary<string, double[]>
            {
                ["a0"] = [0, 0], ["a1"] = [30, 0], ["a2"] = [30, 20], ["a3"] = [0, 20],
                ["b0"] = [50, 0], ["b1"] = [70, 0], ["b2"] = [70, 40], ["b3"] = [50, 40],
            },
            [
                new SketchLine("a-south", "a0", "a1"), new SketchLine("a-east", "a1", "a2"),
                new SketchLine("a-north", "a2", "a3"), new SketchLine("a-west", "a3", "a0"),
                new SketchLine("b-south", "b0", "b1"), new SketchLine("b-east", "b1", "b2"),
                new SketchLine("b-north", "b2", "b3"), new SketchLine("b-west", "b3", "b0"),
            ],
            []),
        new Extrude("pads", BooleanOp.New, null, null, 0, 5) { Sketch = "outlines" },
    ]);

    public static readonly double TwoPadsVolume = (600 + 800) * 5;

    // Experiment 2: a 100 × 60 × 10 plate joined or cut by a second extrude.
    static readonly SketchPlane Top = new([0, 0, 10], [1, 0, 0], [0, 0, 1]);
    static readonly SketchPlane Yz = new([0, 0, 0], [0, 1, 0], [1, 0, 0]);

    static Profile Rectangle(double x0, double y0, double x1, double y1) =>
        new([x0, y0], [new("s", [x1, y0]), new("e", [x1, y1]), new("n", [x0, y1]), new("w", [x0, y0])]);

    // A circle as two half arcs a and b: one face a+b.
    static Profile Circle(double cx, double cy, double r) => new([cx - r, cy], [new("a", [cx + r, cy], 1), new("b", [cx - r, cy], 1)]);

    static PartDocument OnPlate(Extrude second) => new([new Extrude("plate", BooleanOp.New, Xy, Rectangle(0, 0, 100, 60), 0, 10), second]);

    static readonly (string Name, PartDocument Document, double Volume)[] Booleans =
    [
        ("boss on top", OnPlate(new Extrude("boss", BooleanOp.Join, Top, Circle(50, 30, 10), 0, 15)), 60_000 + Math.PI * 1_500),
        ("pocket past the top", OnPlate(new Extrude("pocket", BooleanOp.Cut, Xy, Rectangle(20, 10, 60, 40), 4, 11)), 60_000 - 7_200),
        ("pocket flush with the top", OnPlate(new Extrude("pocket", BooleanOp.Cut, Top, Rectangle(20, 10, 60, 40), -6, 0)), 60_000 - 7_200),
        ("window", OnPlate(new Extrude("window", BooleanOp.Cut, Xy, Rectangle(20, 10, 60, 40), -1, 11)), 60_000 - 12_000),
        ("bore", OnPlate(new Extrude("bore", BooleanOp.Cut, Xy, Circle(50, 30, 8), -1, 11)), 60_000 - Math.PI * 640),
        ("boss flush with two sides", OnPlate(new Extrude("boss", BooleanOp.Join, Top, Rectangle(0, 0, 30, 20), 0, 5)), 63_000),
        ("notch in the side", OnPlate(new Extrude("notch", BooleanOp.Cut, Xy, Rectangle(-5, 20, 10, 40), -1, 11)), 58_000),
        ("bore along the plate", OnPlate(new Extrude("bore", BooleanOp.Cut, Yz, Circle(30, 5, 3), -1, 101)), 60_000 - Math.PI * 900),
        ("block over the end", OnPlate(new Extrude("ext", BooleanOp.Join, Xy, Rectangle(80, 20, 120, 40), 0, 10)), 64_000),
        ("block against the end", OnPlate(new Extrude("ext", BooleanOp.Join, Xy, Rectangle(100, 10, 130, 50), 0, 10)), 72_000),
        ("cut in two", OnPlate(new Extrude("gap", BooleanOp.Cut, Xy, Rectangle(40, -1, 50, 61), -1, 11)), 54_000),
        // A boss, a bore through boss and plate, and a counterbore 5 deep in the boss's top.
        ("boss with a counterbored hole", Then(OnPlate(new Extrude("boss", BooleanOp.Join, Top, Circle(50, 30, 10), 0, 15)),
            new Extrude("bore", BooleanOp.Cut, Xy, Circle(50, 30, 4), -1, 26),
            new Extrude("counterbore", BooleanOp.Cut, Xy, Circle(50, 30, 6), 20, 26)), 60_000 + Math.PI * 1_000),
        // A slot from a bore across its seam (the bore's u = 0 side, towards +x): the slot less what the disc covers,
        // ∫ √(64 − y²) dy over |y| ≤ 4 = 4√48 + 64π/6.
        ("slot across a bore's seam", Then(OnPlate(new Extrude("bore", BooleanOp.Cut, Xy, Circle(50, 30, 8), -1, 11)),
            new Extrude("slot", BooleanOp.Cut, Xy, Rectangle(50, 26, 70, 34), -1, 11)), (6_000 - Math.PI * 64 - 160 + 4 * Math.Sqrt(48) + 64 * Math.PI / 6) * 10),
        // A boss standing on a pocket's floor, its top level with the plate's.
        ("boss on a pocket floor", Then(OnPlate(new Extrude("pocket", BooleanOp.Cut, Xy, Rectangle(20, 10, 80, 50), 5, 11)),
            new Extrude("boss", BooleanOp.Join, Xy, Circle(50, 30, 5), 5, 10)), 48_000 + Math.PI * 125),
    ];

    static PartDocument Then(PartDocument document, params Feature[] more) => new([.. document.Features, .. more]);

    // Experiment 3: holes and sketches on faces, on a 100 × 50 × 10 block.
    static readonly Block Slab = new("plate", BooleanOp.New, [0, 0, 0], [100, 50, 10]);

    static PartDocument OnSlab(params Feature[] more) => new([Slab, .. more]);

    // Experiment 5: fillets and chamfers between flat faces. A rolling ball of radius r leaves the corner of a
    // right-angled edge out (r²(1 − π/4)), its centroid that far from either face.
    static double Spandrel(double r) => r * r * (1 - Math.PI / 4);
    static double Centroid(double r) => r * (5.0 / 6 - Math.PI / 4) / (1 - Math.PI / 4);

    static readonly Profile Bracket2 = new([0, 0],
    [
        new ProfileEdge("bottom", [40, 0]), new ProfileEdge("tip", [40, 5]), new ProfileEdge("inner", [5, 5]),
        new ProfileEdge("riser", [5, 30]), new ProfileEdge("top", [0, 30]), new ProfileEdge("back", [0, 0]),
    ]);

    static readonly (string Name, PartDocument Document, double Volume)[] Rounds =
    [
        ("rounded top edge", OnSlab(new Fillet("round", ["plate/z+", "plate/x+"], 2)), 50_000 - Spandrel(2) * 50),
        ("chamfered front edge", OnSlab(new Chamfer("bevel", ["plate/z+", "plate/y-"], 3)), 50_000 - 0.5 * 9 * 100),
        ("rounded vertical corner", OnSlab(new Fillet("round", ["plate/x+", "plate/y+"], 5)), 50_000 - Spandrel(5) * 10),
        ("L bracket with an inside fillet", new([new Extrude("bracket", BooleanOp.New, Xy, Bracket2, 0, 20), new Fillet("inside", ["bracket/inner", "bracket/riser"], 3)]),
            (40 * 5 + 5 * 25 + Spandrel(3)) * 20),
        ("rounded edge beside a hole", OnSlab(new Hole("hole", [30, 25], 10), new Fillet("round", ["plate/z+", "plate/x-"], 2)),
            50_000 - Math.PI * 250 - Spandrel(2) * 50),
        // The front top edge ends on the 45° face x + z = 100: each bit of the spandrel runs to x = 100 − z.
        ("rounded edge ending on an angled face", OnSlab(new PlaneCut("cut", [90, 25, 10], [1, 0, 1]), new Fillet("round", ["plate/z+", "plate/y-"], 2)),
            47_500 - Spandrel(2) * (90 + Centroid(2))),
        ("chamfer and fillet on two edges", OnSlab(new Chamfer("bevel", ["plate/z-", "plate/y+"], 1), new Fillet("round", ["plate/x-", "plate/y-"], 4)),
            50_000 - 0.5 * 100 - Spandrel(4) * 10),
        // Experiment 6: two fillets sharing the top face meet at the corner in a mitre, an ellipse in the plane
        // bisecting it; the corner gives back r³(5/3 − π/2) of what both edges would lose.
        ("two fillets meeting in a mitre", OnSlab(new Fillet("a", ["plate/z+", "plate/x+"], 2), new Fillet("b", ["plate/z+", "plate/y+"], 2)),
            50_000 - Spandrel(2) * 150 + 8 * (5.0 / 3 - Math.PI / 2)),
        // A fillet and a chamfer sharing the top face: the chamfer's plane cuts the fillet's cylinder at an angle. Over
        // the fillet's section the chamfer takes z − 8 of its length, so the corner gives back the spandrel times
        // (r − its centroid's depth).
        ("fillet meeting a chamfer", OnSlab(new Fillet("a", ["plate/z+", "plate/x+"], 2), new Chamfer("b", ["plate/z+", "plate/y+"], 2)),
            50_000 - Spandrel(2) * 50 - 2 * 100 + Spandrel(2) * (2 - Centroid(2))),
        // Tori (Pappus): the spandrel turned round the axis, its centroid outside a hole's wall or inside a boss's.
        ("filleted hole edge", OnSlab(new Hole("hole", [30, 25], 10), new Fillet("round", ["plate/z+", "hole/wall"], 2)),
            50_000 - Math.PI * 250 - 2 * Math.PI * (5 + Centroid(2)) * Spandrel(2)),
        ("boss with a rounded foot", OnSlab(
            new Sketch("ring", new SketchPlacement(Face: "plate/z+"), new Dictionary<string, double[]> { ["c"] = [50, 25] }, [new SketchCircle("rim", "c", 8)], []),
            new Extrude("boss", BooleanOp.Join, null, null, 0, 15) { Sketch = "ring" },
            new Fillet("foot", ["plate/z+", "boss/rim"], 3)), 50_000 + Math.PI * 64 * 15 + 2 * Math.PI * (8 + Centroid(3)) * Spandrel(3)),

    ];

    // Experiment 4: planes at an angle to cylinders, which meet them in ellipses. The slab under
    // z = 10 − (x − 20)·slope, clipped at z = 0.
    static double Wedge(double slope) => 50 * (20 * 10 + 0.5 * (10 / slope) * 10);

    static readonly double Tan30 = Math.Tan(Math.PI / 6), Tan10 = Math.Tan(Math.PI / 18);

    static readonly (string Name, PartDocument Document, double Volume)[] Angles =
    [
        ("plane cut off the end", OnSlab(new PlaneCut("cut", [90, 25, 10], [1, 0, 1])), 50_000 - 0.5 * 10 * 10 * 50),
        // The bore's column under the cut, at its mean height (the cut stays above z = 0 across it).
        ("oblique cut through a hole", OnSlab(new Hole("hole", [30, 25], 10), new PlaneCut("cut", [20, 25, 10], [Tan30, 0, 1])),
            Wedge(Tan30) - Math.PI * 25 * (10 - 10 * Tan30)),
        ("one cut through two holes", OnSlab(new Hole("a", [30, 25], 10), new Hole("b", [60, 25], 8), new PlaneCut("cut", [20, 25, 10], [Tan10, 0, 1])),
            Wedge(Tan10) - Math.PI * 25 * (10 - 10 * Tan10) - Math.PI * 16 * (10 - 40 * Tan10)),
        // The boss's column under a plane tilted 20° through its axis at z = 20, clear of the plate.
        ("tilted cut through a boss", OnSlab(
            new Sketch("ring", new SketchPlacement(Face: "plate/z+"), new Dictionary<string, double[]> { ["c"] = [50, 25] }, [new SketchCircle("rim", "c", 8)], []),
            new Extrude("boss", BooleanOp.Join, null, null, 0, 15) { Sketch = "ring" },
            new PlaneCut("cut", [50, 25, 20], [0, Math.Sin(Math.PI / 9), Math.Cos(Math.PI / 9)])), 50_000 + Math.PI * 640),
        // A bore 30° off vertical: horizontal slices are ellipses of area π r² / cos 30°.
        ("bore at an angle", OnSlab(new Extrude("bore", BooleanOp.Cut, new SketchPlane([50, 25, 5], [0, 1, 0], [Math.Sin(Math.PI / 6), 0, Math.Cos(Math.PI / 6)]),
            new Profile([-4, 0], [new ProfileEdge("a", [4, 0], 1), new ProfileEdge("b", [-4, 0], 1)]), -20, 20)), 50_000 - Math.PI * 160 / Math.Cos(Math.PI / 6)),
        // A post 30° off vertical, starting inside the plate: what stands above the top face, cut there in an ellipse.
        ("angled post", OnSlab(new Extrude("post", BooleanOp.Join, new SketchPlane([50, 25, 5], [0, 1, 0], [Math.Sin(Math.PI / 6), 0, Math.Cos(Math.PI / 6)]),
            new Profile([-4, 0], [new ProfileEdge("a", [4, 0], 1), new ProfileEdge("b", [-4, 0], 1)]), 0, 20)), 50_000 + Math.PI * 16 * (20 - 5 / Math.Cos(Math.PI / 6))),
        // Into the 45° face at (95, 25, 5), through to the bottom at (90, 25, 0): 5√2 along its axis.
        ("hole into the angled face", OnSlab(new PlaneCut("cut", [90, 25, 10], [1, 0, 1]), new Hole("drill", [45 * Math.Sqrt(2), 25], 4) { On = "cut/face" }),
            47_500 - Math.PI * 4 * 5 * Math.Sqrt(2)),
    ];

    // The lens two discs of radius a and b, d apart, share.
    static double Lens(double a, double b, double d) =>
        a * a * Math.Acos((d * d + a * a - b * b) / (2 * d * a)) + b * b * Math.Acos((d * d + b * b - a * a) / (2 * d * b))
        - Math.Sqrt((-d + a + b) * (d + a - b) * (d - a + b) * (d + a + b)) / 2;

    static readonly (string Name, PartDocument Document, double Volume)[] Holes =
    [
        ("hole through", OnSlab(new Hole("hole", [30, 25], 10)), 50_000 - Math.PI * 250),
        ("hole through a side face", OnSlab(new Hole("side", [25, 5], 4) { On = "plate/x+" }), 50_000 - Math.PI * 400),
        ("blind hole in the top face", OnSlab(new Hole("pocket", [20, 20], 6) { On = "plate/z+", Depth = 4 }), 50_000 - Math.PI * 36),
        ("blind holes at sketch points", OnSlab(
            new Sketch("spots", new SketchPlacement(Face: "plate/z+"), new Dictionary<string, double[]> { ["near"] = [20, 25], ["far"] = [80, 25] }, [], []),
            new Hole("holes", [], 5) { Sketch = "spots", Points = ["near", "far"], Depth = 3 }), 50_000 - 2 * Math.PI * 6.25 * 3),
        ("two holes overlapping", OnSlab(new Hole("a", [30, 25], 10), new Hole("b", [36, 25], 8)), 50_000 - 10 * (Math.PI * 41 - Lens(5, 4, 6))),
        // A constrained sketch: a 60 × 40 rectangle round an R4 bore, from points that start off it.
        ("sketched plate with a bore", new(
            [
                new Sketch("base", new SketchPlacement(Xy), new Dictionary<string, double[]> { ["a"] = [0, 0], ["b"] = [58, 1], ["c"] = [61, 39], ["d"] = [1, 41], ["h"] = [20, 20] },
                    [new SketchLine("bottom", "a", "b"), new SketchLine("right", "b", "c"), new SketchLine("top", "c", "d"), new SketchLine("left", "d", "a"), new SketchCircle("bore", "h", 3)],
                    [new("horizontal", ["bottom"]), new("vertical", ["right"]), new("horizontal", ["top"]), new("vertical", ["left"]), new("fix", ["a"]), new("fix", ["h"]),
                     new("length", ["bottom"], 60), new("length", ["left"], 40), new("radius", ["bore"], 4)]),
                new Extrude("plate", BooleanOp.New, null, null, 0, 5) { Sketch = "base" },
            ]), 60 * 40 * 5 - Math.PI * 16 * 5),
        // A boss sketched on the top face, and a hole along Z through boss and plate.
        ("boss sketched on the top, bored", OnSlab(
            new Sketch("ring", new SketchPlacement(Face: "plate/z+"), new Dictionary<string, double[]> { ["c"] = [50, 25] }, [new SketchCircle("rim", "c", 8)], []),
            new Extrude("boss", BooleanOp.Join, null, null, 0, 8) { Sketch = "ring" },
            new Hole("bore", [50, 25], 6)), 50_000 + Math.PI * 512 - Math.PI * 9 * 18),
        // A slot sketched on the front face (y = 0, sketch y up world z), cut 20 deep.
        ("slot sketched on the front face", OnSlab(
            new Sketch("slot", new SketchPlacement(Face: "plate/y-"), new Dictionary<string, double[]> { ["a"] = [40, 3], ["b"] = [60, 3], ["c"] = [60, 7], ["d"] = [40, 7] },
                [new SketchLine("low", "a", "b"), new SketchLine("deep", "b", "c"), new SketchLine("high", "c", "d"), new SketchLine("back", "d", "a")], []),
            new Extrude("groove", BooleanOp.Cut, null, null, -20, 1) { Sketch = "slot" }), 50_000 - 20 * 4 * 20),
        // A second body standing in the first: the hole cuts only the current one, the post.
        ("hole in the second of two bodies", OnSlab(
            new Block("post", BooleanOp.New, [40, 15, 5], [20, 20, 30]),
            new Hole("pin", [50, 25], 6)), 50_000 + 12_000 - Math.PI * 9 * 30),
    ];

    public static IEnumerable<object[]> All =>
    [
        ["mounting plate", MountingPlate, MountingPlateVolume],
        ["bracket", Bracket, BracketVolume],
        ["two pads", TwoPads, TwoPadsVolume],
        .. Booleans.Select(b => new object[] { b.Name, b.Document, b.Volume }),
        .. Holes.Select(b => new object[] { b.Name, b.Document, b.Volume }),
        .. Angles.Select(b => new object[] { b.Name, b.Document, b.Volume }),
        .. Rounds.Select(b => new object[] { b.Name, b.Document, b.Volume }),
    ];

    public static PartDocument Named(string name) => name switch
    {
        "mounting plate" => MountingPlate,
        "bracket" => Bracket,
        "two pads" => TwoPads,
        _ => Booleans.Concat(Holes).Concat(Angles).Concat(Rounds).Single(b => b.Name == name).Document,
    };
}
