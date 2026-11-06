using Isoform.Document;

namespace Isoform.Brep.Tests;

/// <summary>Isoform part documents the managed kernel builds (experiments 1 to 15), with their exact volumes.</summary>
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
    static double Mitre(double r) => r * r * r * (5.0 / 3 - Math.PI / 2);

    // A stadium 20 long between two R5 ends, from (40, 20) to (60, 30).
    static Profile Stadium() => new([40, 20], [new ProfileEdge("a", [60, 20]), new ProfileEdge("right", [60, 30], 1), new ProfileEdge("b", [40, 30]), new ProfileEdge("left", [40, 20], 1)]);

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
        // Experiment 7: loop fillets. Straight pieces mitre at convex corners (each corner gives back r³(5/3 − π/2));
        // a concave loop's pieces fill, and its corners take that back; arcs run on into lines through tori.
        ("outline with a loop round", new([new Extrude("outline", BooleanOp.New, Xy, Rectangle(0, 0, 60, 40), 0, 10), new Fillet("round", [], 2, Loop: "outline/end")]),
            60 * 40 * 10 - 200 * Spandrel(2) + 4 * Mitre(2)),
        ("pocket floor with a loop round", OnSlab(new Extrude("pocket", BooleanOp.Cut, Xy, Rectangle(20, 10, 60, 40), 4, 11), new Fillet("floor", [], 2, Loop: "pocket/start")),
            50_000 - 40 * 30 * 6 + 140 * Spandrel(2) - 4 * Mitre(2)),
        ("boss with a loop round", OnSlab(new Extrude("boss", BooleanOp.Join, Top, Stadium(), 0, 8), new Fillet("round", [], 1, Loop: "boss/end")),
            50_000 + (20 * 10 + Math.PI * 25) * 8 - 40 * Spandrel(1) - 2 * Math.PI * (5 - Centroid(1)) * Spandrel(1)),
        ("rim of a cut-out with a loop round", OnSlab(new Extrude("slot", BooleanOp.Cut, Xy, Stadium(), -1, 11), new Fillet("round", [], 1, Loop: "plate/z+")),
            50_000 - (20 * 10 + Math.PI * 25) * 10 - (300 * Spandrel(1) - 4 * Mitre(1)) - 40 * Spandrel(1) - 2 * Math.PI * (5 + Centroid(1)) * Spandrel(1)),
        ("boss with a rounded foot", OnSlab(
            new Sketch("ring", new SketchPlacement(Face: "plate/z+"), new Dictionary<string, double[]> { ["c"] = [50, 25] }, [new SketchCircle("rim", "c", 8)], []),
            new Extrude("boss", BooleanOp.Join, null, null, 0, 15) { Sketch = "ring" },
            new Fillet("foot", ["plate/z+", "boss/rim"], 3)), 50_000 + Math.PI * 64 * 15 + 2 * Math.PI * (8 + Centroid(3)) * Spandrel(3)),

    ];

    // Experiment 8: chamfers round circles and arcs are cones. Pappus: the triangle d²/2, its centroid d/3 from the wall.
    static double Triangle(double d) => d * d / 2;

    // ∫ (r² acos(2/r) − 2√(r² − 4)) dr, by parts: (r³/3) acos(2/r) − (2/3)((r/2) S + 2 ln(r + S)) − (r S − 4 ln(r + S)),
    // S = √(r² − 4).
    static double SegmentIntegral(double r)
    {
        var s = Math.Sqrt(r * r - 4);
        return r * r * r / 3 * Math.Acos(2 / r) - 2.0 / 3 * (r / 2 * s + 2 * Math.Log(r + s)) - (r * s - 4 * Math.Log(r + s));
    }

    static double ObliqueTip()
    {
        var (x1, x2) = (2.5 / 0.8125, -2.5 / 1.1875);
        var a = (x1 - x2) * Math.Sqrt(1.09) / 2;
        var xc = (x1 + x2) / 2;
        var rho = 5.0 / 8 * (4 + 0.3 * xc);
        var b = Math.Sqrt(rho * rho - xc * xc);
        return Math.PI * a * b * (4 / Math.Sqrt(1.09)) / 3;
    }

    static readonly (string Name, PartDocument Document, double Volume)[] Cones =
    [
        // A cone (radius 5, apex 8 up) with its tip cut off by a plane leaning 0.3 in x through (0, 0, 4): an ellipse.
        // In the plane z = 4 − 0.3x the cone is ρ = (5/8)(4 + 0.3x): its vertices on y = 0 at x = 2.5/0.8125 and
        // −2.5/1.1875, the minor axis at their middle reaching the cone's circle there. What goes is an oblique cone
        // on that ellipse: π a b times the apex's distance from the plane, 4/√1.09, over 3.
        ("oblique cut off a cone", new([new Revolve("cone", BooleanOp.New, Upright(0, 0), new Profile([0, 0],
            [new ProfileEdge("bottom", [5, 0]), new ProfileEdge("side", [0, 8]), new ProfileEdge("axis", [0, 0])])),
            new PlaneCut("cut", [0, 0, 4], [0.3, 0, 1])]), Math.PI * 25 * 8 / 3 - ObliqueTip()),
        // The cone cut by the plane x = 2, along its axis (a hyperbola, its vertex at z = 4.8), and its tip off at z = 6
        // (radius 1.25): the frustum less, at each height, the circle's segment past x = 2.
        ("frustum cut along its axis", new([new Revolve("cone", BooleanOp.New, Upright(0, 0), new Profile([0, 0],
            [new ProfileEdge("bottom", [5, 0]), new ProfileEdge("side", [0, 8]), new ProfileEdge("axis", [0, 0])])),
            new PlaneCut("cut", [2, 0, 0], [1, 0, 0]), new PlaneCut("top", [0, 0, 6], [0, 0, 1])]),
            Math.PI * 6 * (25 + 5 * 1.25 + 1.25 * 1.25) / 3 - 8.0 / 5 * (SegmentIntegral(5) - SegmentIntegral(2))),
        ("chamfered hole edge", OnSlab(new Hole("hole", [30, 25], 10), new Chamfer("bevel", ["plate/z+", "hole/wall"], 1.5)),
            50_000 - Math.PI * 250 - 2 * Math.PI * (5 + 0.5) * Triangle(1.5)),
        ("hole chamfered at both ends", OnSlab(new Hole("hole", [30, 25], 10), new Chamfer("top", ["plate/z+", "hole/wall"], 1), new Chamfer("bottom", ["plate/z-", "hole/wall"], 1)),
            50_000 - Math.PI * 250 - 2 * 2 * Math.PI * (5 + 1.0 / 3) * Triangle(1)),
        // Straight pieces run on into the cones round the ends, their planes touching the cones along a line.
        ("boss with a loop chamfer", OnSlab(new Extrude("boss", BooleanOp.Join, Top, Stadium(), 0, 8), new Chamfer("bevel", [], 1, Loop: "boss/end")),
            50_000 + (20 * 10 + Math.PI * 25) * 8 - 40 * Triangle(1) - 2 * Math.PI * (5 - 1.0 / 3) * Triangle(1)),
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

    // Experiment 10: revolves and patterns. A vertical half-plane through x: the sketch's x along world x, its y up.
    static SketchPlane Upright(double x, double y) => new([x, y, 0], [1, 0, 0], [0, -1, 0]);

    // A profile closed along the axis, from (0, y0): out to r, up to y1, back in.
    static Profile Turned(double r, double y0, double y1) =>
        new([0, y0], [new("bottom", [r, y0]), new("wall", [r, y1]), new("top", [0, y1]), new("axis", [0, y0])]);

    // A tab 15 × 6 from x = 15 on a disc of radius 20: the part outside the disc, ∫ (30 − √(400 − y²)) dy over |y| ≤ 3.
    static readonly double TabOutside = 180 - 3 * Math.Sqrt(391) - 400 * Math.Asin(0.15);

    static readonly (string Name, PartDocument Document, double Volume)[] Copies =
    [
        // The playground's pin: tip and cap are discs (their edge on the axis sweeps nothing), the lead a cone.
        ("revolved pin", new([new Revolve("pin", BooleanOp.New, Upright(0, 0), new Profile([0, 0],
            [
                new ProfileEdge("tip", [2.9, 0]), new ProfileEdge("shank", [2.9, 24]), new ProfileEdge("head", [5, 24]),
                new ProfileEdge("collar", [5, 27]), new ProfileEdge("cap", [0, 27]), new ProfileEdge("axis", [0, 0]),
            ])), new Chamfer("lead", ["pin/tip", "pin/shank"], 1)]),
            Math.PI * (2.9 * 2.9 * 24 + 25 * 3) - 2 * Math.PI * (2.9 - 1.0 / 3) * Triangle(1)),
        ("half turn closed on the axis", new([new Revolve("half", BooleanOp.New, Upright(0, 0), Turned(5, 0, 10), 180)]), Math.PI * 25 * 10 / 2),
        // A rectangle 12 to 15 out along the sketch's x, turned round a construction line at x = 10: a tube.
        ("revolved round a sketch line", new(
        [
            new Sketch("section", new SketchPlacement(Upright(0, 0)), new Dictionary<string, double[]>
                {
                    ["p0"] = [12, 0], ["p1"] = [15, 0], ["p2"] = [15, 10], ["p3"] = [12, 10], ["a0"] = [10, 0], ["a1"] = [10, 10],
                },
                [
                    new SketchLine("lower", "p0", "p1"), new SketchLine("outer", "p1", "p2"), new SketchLine("upper", "p2", "p3"),
                    new SketchLine("inner", "p3", "p0"), new SketchLine("axis", "a0", "a1") { Construction = true },
                ],
                []),
            new Revolve("tube", BooleanOp.New, null, null) { Sketch = "section", Axis = "axis" },
        ]), Math.PI * (25 - 4) * 10),
        ("row of revolved bores", OnSlab(new Revolve("bore", BooleanOp.Cut, Upright(20, 25), Turned(3, -1, 11)),
            new Pattern("row", ["bore"], new LinearMotion([1, 0, 0], 20, 3))), 50_000 - 3 * Math.PI * 9 * 10),
        // The playground's flange holes on a plain disc 8 thick: a bore of radius 3 to 7 high, then a countersink
        // widening to 4 at the top (a frustum π/3 (9 + 12 + 16)).
        ("countersunk bolt circle", new(
        [
            new Extrude("flange", BooleanOp.New, Xy, Circle(0, 0, 40), 0, 8),
            new Revolve("bolt", BooleanOp.Cut, Upright(30, 0), new Profile([0, -1],
            [
                new ProfileEdge("bottom", [3, -1]), new ProfileEdge("wall", [3, 7]), new ProfileEdge("sink", [5, 9]),
                new ProfileEdge("top", [0, 9]), new ProfileEdge("axis", [0, -1]),
            ])),
            new Pattern("circle", ["bolt"], new CircularMotion([0, 0, 0], [0, 0, 1], 6)),
        ]), Math.PI * 1_600 * 8 - 6 * Math.PI * (9 * 7 + 37.0 / 3)),
        // The disc a sketch circle (one face): a circle drawn as two half arcs is one face named by both, which the
        // kernel keeps for every piece the tabs cut it into, where the F-rep core names each by the arcs it lies on.
        ("tabs round a disc", new(
        [
            new Sketch("round", new SketchPlacement(Xy), new Dictionary<string, double[]> { ["c"] = [0, 0] }, [new SketchCircle("rim", "c", 20)], []),
            new Extrude("disc", BooleanOp.New, null, null, 0, 5) { Sketch = "round" },
            new Block("tab", BooleanOp.Join, [15, -3, 0], [15, 6, 5]),
            new Pattern("ring", ["tab"], new CircularMotion([0, 0, 0], [0, 0, 1], 3)),
        ]), Math.PI * 400 * 5 + 3 * 5 * TabOutside),
        ("mirrored tab", OnSlab(new Block("tab", BooleanOp.Join, [-10, 10, 0], [10, 10, 5]),
            new Pattern("mirror", ["tab"], new MirrorMotion([50, 0, 0], [1, 0, 0]))), 50_000 + 2 * 500),
        // A boss with a hole down through it and the slab, mirrored: a join and a cut, copied in order.
        ("mirrored boss with a hole", OnSlab(new Extrude("boss", BooleanOp.Join, Top, Circle(20, 25, 5), 0, 5), new Hole("hole", [20, 25], 4),
            new Pattern("mirror", ["boss", "hole"], new MirrorMotion([50, 0, 0], [1, 0, 0]))), 50_000 + 2 * Math.PI * 25 * 5 - 2 * Math.PI * 4 * 15),
    ];

    // Experiment 11: sweeps and bodies combined. A section with its centroid on the path sweeps its area times the
    // path's length (Pappus along each piece).
    static readonly SketchPlane YzAtOrigin = new([0, 0, 0], [0, 1, 0], [1, 0, 0]);

    static readonly Sketch Route = new("route", new SketchPlacement(Xy),
        new Dictionary<string, double[]> { ["a"] = [0, 0], ["b"] = [40, 0], ["c"] = [40, 25], ["d"] = [65, 25], ["e"] = [65, 60] },
        [new SketchLine("run", "a", "b"), new SketchArc("bend", "c", "b", "d"), new SketchLine("rise", "d", "e")], []);

    static readonly Sketch Ring = new("ring", new SketchPlacement(YzAtOrigin), new Dictionary<string, double[]> { ["o"] = [0, 0] },
        [new SketchCircle("outside", "o", 8), new SketchCircle("bore", "o", 5.5)], []);

    static readonly double PipeVolume = Math.PI * (64 - 5.5 * 5.5) * (40 + 25 * Math.PI / 2 + 35);

    static readonly (string Name, PartDocument Document, double Volume)[] Sweeps =
    [
        // The playground's pipe bend: cylinders along the lines, tori round the quarter turn.
        ("pipe bend", new([Route, Ring, new Sweep("pipe", BooleanOp.New, "ring", null, new SweepPath("route", From: "run"))]), PipeVolume),
        ("pipe bend with a flange", new([Route, Ring, new Sweep("pipe", BooleanOp.New, "ring", null, new SweepPath("route", From: "run")),
            new Block("flange", BooleanOp.Join, [-6, -20, -20], [6, 40, 40])]), PipeVolume + 6 * 40 * 40),
        // A 4 × 3 bar round a stadium (two lines 40 long, two half turns of radius 10), started at a vertex.
        ("bar round a closed path", new(
        [
            new Sketch("track", new SketchPlacement(Xy), new Dictionary<string, double[]>
                {
                    ["p0"] = [0, 0], ["p1"] = [40, 0], ["c1"] = [40, 10], ["p2"] = [40, 20], ["p3"] = [0, 20], ["c0"] = [0, 10],
                },
                [new SketchLine("near", "p0", "p1"), new SketchArc("right", "c1", "p1", "p2"), new SketchLine("far", "p2", "p3"), new SketchArc("left", "c0", "p3", "p0")],
                []),
            new Sketch("bar", new SketchPlacement(YzAtOrigin), new Dictionary<string, double[]> { ["q0"] = [-2, 0], ["q1"] = [2, 0], ["q2"] = [2, 3], ["q3"] = [-2, 3] },
                [new SketchLine("under", "q0", "q1"), new SketchLine("side", "q1", "q2"), new SketchLine("over", "q2", "q3"), new SketchLine("back", "q3", "q0")], []),
            new Sweep("loop", BooleanOp.New, "bar", null, new SweepPath("track", From: "near")),
        ]), 12 * (80 + 20 * Math.PI)),
        // A circle of radius 2 swept round a circle of radius 15: a torus.
        ("ring round a circle", new(
        [
            new Sketch("path", new SketchPlacement(Xy), new Dictionary<string, double[]> { ["o"] = [0, 0] }, [new SketchCircle("round", "o", 15)], []),
            new Sketch("section", new SketchPlacement(Upright(0, 0)), new Dictionary<string, double[]> { ["c"] = [15, 0] }, [new SketchCircle("tube", "c", 2)], []),
            new Sweep("ring", BooleanOp.New, "section", null, new SweepPath("path", From: "round")),
        ]), 2 * Math.PI * Math.PI * 15 * 4),
        // A clamp of its own cutting its seat into the slab and kept: two bodies.
        ("body cutting its seat", OnSlab(new Block("clamp", BooleanOp.New, [40, 15, 6], [20, 20, 10]),
            new CombineBodies("seat", "clamp", BooleanOp.Cut, Keep: true) { Body = "plate" }), 50_000 - 20 * 20 * 4 + 20 * 20 * 10),
        // A boss made as a body of its own, then joined into the slab.
        ("body joined in", OnSlab(new Block("boss", BooleanOp.New, [40, 15, 8], [20, 20, 10]),
            new CombineBodies("weld", "boss", BooleanOp.Join) { Body = "plate" }), 50_000 + 20 * 20 * 8),
    ];

    // Experiment 12: spheres. An arc round a centre on the axis turns into a sphere, closed at a pole where it reaches
    // the axis. A post of radius 5 and 10 high under a dome of radius 5 (a hemisphere, 2π/3 · 125).
    static Revolve DomedPost(string id) => new(id, BooleanOp.New, Upright(0, 0), new Profile([0, 0],
    [
        new ProfileEdge("bottom", [5, 0]), new ProfileEdge("wall", [5, 10]), new ProfileEdge("dome", [0, 15], Math.Tan(Math.PI / 8)), new ProfileEdge("axis", [0, 0]),
    ]));

    static readonly double DomedPostVolume = Math.PI * 25 * 10 + 2 * Math.PI / 3 * 125;

    // A circular segment's area: radius r, the arc's angle θ.
    static double Segment(double r, double theta) => r * r * (theta - Math.Sin(theta)) / 2;

    static readonly (string Name, PartDocument Document, double Volume)[] Domes =
    [
        // A barrel: a rectangle 10 out and 15 up whose side bulges out 1.5 (bulge 0.2: a circle of radius 19.5 round
        // (−8, 7.5), across the axis, so its torus has a negative major radius). Pappus: the rectangle's 150 at 5, and the
        // circular segment's r²(θ − sin θ)/2 at −8 + 4r sin³(θ/2) / (3(θ − sin θ)), θ = 4 atan 0.2.
        ("barrel", new([new Revolve("barrel", BooleanOp.New, Upright(0, 0), new Profile([0, 0],
            [new ProfileEdge("base", [10, 0]), new ProfileEdge("side", [10, 15], 0.2), new ProfileEdge("top", [0, 15]), new ProfileEdge("axis", [0, 0])]))]),
            2 * Math.PI * (150 * 5 + Segment(19.5, 4 * Math.Atan(0.2)) * (-8 + 4 * 19.5 * Math.Pow(Math.Sin(Math.Atan(0.2) * 2), 3) / (3 * (4 * Math.Atan(0.2) - Math.Sin(4 * Math.Atan(0.2))))))),
        // The playground's turned shaft: a quarter circle of radius 4 round (0, 52) closes it on the axis; an undercut
        // fillet at the step and a chamfered lead at the base.
        ("turned shaft", new(
        [
            new Revolve("shaft", BooleanOp.New, Upright(0, 0), new Profile([0, 0],
            [
                new ProfileEdge("base", [12, 0]), new ProfileEdge("big", [12, 20]), new ProfileEdge("step", [7, 20]),
                new ProfileEdge("small", [7, 45]), new ProfileEdge("cone", [4, 52]), new ProfileEdge("dome", [0, 56], Math.Tan(Math.PI / 8)),
                new ProfileEdge("axis", [0, 0]),
            ])),
            new Fillet("undercut", ["shaft/step", "shaft/small"], 1.5),
            new Chamfer("lead", ["shaft/base", "shaft/big"], 1),
        ]), Math.PI * (144 * 20 + 49 * 25 + 7.0 / 3 * (49 + 28 + 16) + 2.0 / 3 * 64)
            + 2 * Math.PI * (7 + Centroid(1.5)) * Spandrel(1.5) - 2 * Math.PI * (12 - 1.0 / 3) * Triangle(1)),
        ("ball", new([new Revolve("ball", BooleanOp.New, Upright(0, 0), new Profile([0, -5], [new ProfileEdge("round", [0, 5], 1), new ProfileEdge("axis", [0, -5])]))]),
            4 * Math.PI / 3 * 125),
        // A bore of radius 2 down the axis takes the pole away: the cylinder meets the dome in a circle at
        // 10 + √21; the dome loses ∫ 2πr √(25 − r²) dr over r ≤ 2.
        ("domed post with a bore", new([DomedPost("post"), new Hole("bore", [0, 0], 4)]),
            DomedPostVolume - Math.PI * 4 * 10 - 2 * Math.PI / 3 * (125 - 21 * Math.Sqrt(21))),
        // Cut square to the axis 3 above the dome's centre: the slice π ∫ (25 − z²) dz over 0 ≤ z ≤ 3 is left.
        ("domed post cut flat", new([DomedPost("post"), new PlaneCut("cut", [0, 0, 13], [0, 0, 1])]), Math.PI * 25 * 10 + Math.PI * (75 - 9)),
        // Cut in half through its axis: the cut runs through the dome's pole.
        ("half a domed post", new([DomedPost("post"), new PlaneCut("cut", [0, 0, 0], [1, 0, 0])]), DomedPostVolume / 2),
    ];

    // Experiment 13: offsets. Every face so far moves out: blocks grow, cut tools shrink, fillets get rounder, and a
    // profile's corners that part as it grows turn round (named id/corner:a+b after the two faces).
    static Profile Ell => new([5, 5],
    [
        new ProfileEdge("a", [30, 5]), new ProfileEdge("b", [30, 12]), new ProfileEdge("c", [12, 12]),
        new ProfileEdge("d", [12, 40]), new ProfileEdge("e", [5, 40]), new ProfileEdge("f", [5, 5]),
    ]);

    static readonly (string Name, PartDocument Document, double Volume)[] Offsets =
    [
        // The playground's: the plate grows to 102 × 52 × 12; each rib's L (area 371, edges 120 long) gains a strip
        // 1 wide round it, a quarter circle at each of its five convex corners, less the square where the strips
        // overlap at its reflex corner; it stands 6 above the plate.
        ("mirrored ribs grown", new(
        [
            new Block("plate", BooleanOp.New, [0, 0, 0], [100, 50, 10]),
            new Extrude("rib", BooleanOp.Join, Top, Ell, 0, 6),
            new Pattern("mirror", ["rib"], new MirrorMotion([50, 0, 0], [1, 0, 0])),
            new Offset("grow", 1),
        ]), 102 * 52 * 12 + 2 * 6 * (371 + 120 - 1 + 5 * Math.PI / 4)),
        // The pocket's tool shrinks to 18 × 8, from 7 up past the top at 11; the bore to radius 1.
        ("pocket and bore grown", new(
        [
            new Block("plate", BooleanOp.New, [0, 0, 0], [40, 30, 10]),
            new Extrude("pocket", BooleanOp.Cut, Top, new Profile([10, 10],
                [new ProfileEdge("a", [30, 10]), new ProfileEdge("b", [30, 20]), new ProfileEdge("c", [10, 20]), new ProfileEdge("d", [10, 10])]), -4, 3),
            new Hole("bore", [5, 5], 4),
            new Offset("grow", 1),
        ]), 42 * 32 * 12 - 18 * 8 * 4 - Math.PI * 12),
        // The post grows to radius 6 on a base 1 lower, its dome to radius 6; the corner at the base turns round a
        // circle of radius 5 (a quarter disc of radius 1, centroid 4/3π out from it).
        ("domed post grown", new([DomedPost("post"), new Offset("grow", 1)]),
            Math.PI * 36 * 10 + 2 * Math.PI / 3 * 216 + Math.PI * 25 + 2 * Math.PI * (5 + 4 / (3 * Math.PI)) * Math.PI / 4),
        // The plate grows to 42 × 32 × 12 and the round to radius 3 along its 32.
        ("filleted plate grown", new(
        [
            new Block("plate", BooleanOp.New, [0, 0, 0], [40, 30, 10]),
            new Fillet("round", ["plate/z+", "plate/x+"], 2),
            new Offset("grow", 1),
        ]), 42 * 32 * 12 - 9 * (1 - Math.PI / 4) * 32),
        // Shrunk, the L's reflex corner turns round instead: arms 23 × 5 and 5 × 28, and the corner square less a
        // quarter circle; 4 high.
        ("ell shrunk", new([new Extrude("rib", BooleanOp.New, Xy, Ell, 0, 6), new Offset("shrink", -1)]), 4 * (256 - Math.PI / 4)),
    ];

    // Experiment 14: shells. The cavity is every face moved in by the wall (a cut's tool grown by it), corners met
    // sharp, its faces named id/inner:face.
    static readonly Block Plate40 = new("plate", BooleanOp.New, [0, 0, 0], [40, 30, 10]);

    static readonly (string Name, PartDocument Document, double Volume)[] Shells =
    [
        // The L shrunk by 1 with its reflex corner sharp (arms 23 × 5 and 5 × 28), 4 high: the wall is √2 across the
        // inside corner, as CAD shells it.
        ("hollow ell", new([new Extrude("rib", BooleanOp.New, Xy, Ell, 0, 6), new Isoform.Document.Shell("hollow", 1)]), 371 * 6 - 4 * 255),
        // A tube of radius 3 round the bore stands through the cavity.
        ("hollow plate with a bore", new([Plate40, new Hole("bore", [20, 15], 4), new Isoform.Document.Shell("hollow", 1)]),
            12000 - Math.PI * 4 * 10 - (38 * 28 * 8 - Math.PI * 9 * 8)),
        // The pocket's tool grown by 1 (22 × 12, down to 5) dents the cavity's top 4 deep.
        ("hollow plate with a pocket", new(
        [
            Plate40,
            new Extrude("pocket", BooleanOp.Cut, Top, new Profile([10, 10],
                [new ProfileEdge("a", [30, 10]), new ProfileEdge("b", [30, 20]), new ProfileEdge("c", [10, 20]), new ProfileEdge("d", [10, 10])]), -4, 3),
            new Isoform.Document.Shell("hollow", 1),
        ]), 12000 - 20 * 10 * 4 - (38 * 28 * 8 - 22 * 12 * 4)),
        // The cavity's round is the fillet moved in: radius 2 along 28.
        ("hollow filleted plate", new([Plate40, new Fillet("round", ["plate/z+", "plate/x+"], 3), new Isoform.Document.Shell("hollow", 1)]),
            12000 - 9 * (1 - Math.PI / 4) * 30 - (38 * 28 * 8 - 4 * (1 - Math.PI / 4) * 28)),
        // A cavity of radius 4 from 1 up, under a dome of radius 4 round the same centre.
        ("hollow domed post", new([DomedPost("post"), new Isoform.Document.Shell("hollow", 1)]), DomedPostVolume - (Math.PI * 16 * 9 + 2 * Math.PI / 3 * 64)),
        // Round, the wall stays 1 across the L's inside corner: the corner square comes back less a quarter circle,
        // a round hollow/corner:rib/c+rib/d.
        ("round hollow ell", new([new Extrude("rib", BooleanOp.New, Xy, Ell, 0, 6), new Isoform.Document.Shell("hollow", 1, Round: true)]),
            371 * 6 - 4 * (256 - Math.PI / 4)),
        // Round, the pocket's grown tool rounds its four corners: 22 × 12 less 4 − π.
        ("round hollow plate with a pocket", new(
        [
            Plate40,
            new Extrude("pocket", BooleanOp.Cut, Top, new Profile([10, 10],
                [new ProfileEdge("a", [30, 10]), new ProfileEdge("b", [30, 20]), new ProfileEdge("c", [10, 20]), new ProfileEdge("d", [10, 10])]), -4, 3),
            new Isoform.Document.Shell("hollow", 1, Round: true),
        ]), 12000 - 20 * 10 * 4 - (38 * 28 * 8 - 4 * (22 * 12 - (4 - Math.PI)))),
        // Rounded after the shell, outside only.
        ("plate rounded after shelling", new([Plate40, new Isoform.Document.Shell("hollow", 2), new Fillet("round", ["plate/z+", "plate/x+"], 1)]),
            12000 - (1 - Math.PI / 4) * 30 - 36 * 26 * 6),
    ];

    // Experiment 15: ball corners. Where three fillets of one radius meet at a square corner, a sphere round the
    // point r inside all three faces: each fillet's spandrel (4 − π for r = 2) runs to the corner's cube, of which the
    // ball leaves its octant (8 − 4π/3 goes).
    static readonly double Spandrel2 = 4 - Math.PI;
    static readonly double CornerCap = 8 - 4 * Math.PI / 3;

    // From a 120° corner at the origin along x and at 120° to it, 30 each way, square at both far ends: they meet at
    // 60° at (30, 30√3).
    static readonly Profile Obtuse = new([0, 0],
    [
        new ProfileEdge("side1", [30, 0]), new ProfileEdge("end1", [30, 30 * Math.Sqrt(3)]),
        new ProfileEdge("end2", [-15, 15 * Math.Sqrt(3)]), new ProfileEdge("side2", [0, 0]),
    ]);

    static readonly (string Name, PartDocument Document, double Volume)[] Balls =
    [
        ("ball corner", new(
        [
            Plate40, new Fillet("r1", ["plate/z+", "plate/x+"], 2), new Fillet("r2", ["plate/z+", "plate/y+"], 2), new Fillet("r3", ["plate/x+", "plate/y+"], 2),
        ]), 12000 - Spandrel2 * (28 + 38 + 8) - CornerCap),
        // The upright edge first: the ball comes with the last, named after it.
        ("ball corner, upright edge first", new(
        [
            Plate40, new Fillet("r3", ["plate/x+", "plate/y+"], 2), new Fillet("r1", ["plate/z+", "plate/x+"], 2), new Fillet("r2", ["plate/z+", "plate/y+"], 2),
        ]), 12000 - Spandrel2 * (28 + 38 + 8) - CornerCap),
        // The top's four edges and the four upright ones: a ball at each top corner; the top edges run 36 and 26
        // between them, the upright ones 8 below.
        ("four ball corners", new(
        [
            Plate40,
            new Fillet("t1", ["plate/z+", "plate/x+"], 2), new Fillet("t2", ["plate/z+", "plate/x-"], 2),
            new Fillet("t3", ["plate/z+", "plate/y+"], 2), new Fillet("t4", ["plate/z+", "plate/y-"], 2),
            new Fillet("u1", ["plate/x+", "plate/y+"], 2), new Fillet("u2", ["plate/x+", "plate/y-"], 2),
            new Fillet("u3", ["plate/x-", "plate/y+"], 2), new Fillet("u4", ["plate/x-", "plate/y-"], 2),
        ]), 12000 - Spandrel2 * (2 * 36 + 2 * 26 + 4 * 8) - 4 * CornerCap),
        // A corner that is not square: a 20 × 16 × 12 box, its top end cut back at 45° from x = 8 on top and that cut
        // ended square by another (the end a pentagon across y, 152 in area); the top edge along x, the cut's top edge
        // and its edge on the far side rounded to 2. The ball is the sphere r inside all three faces, in the sector
        // their normals span from its centre: there the corner (the kite between the centre, the tangent points and
        // the tip, r² cot 67.5° across the 135° edge, r deep) less the ball's share (the normals of the top and the
        // cut 45° apart, both square to the side: a lune of π/4, π r³/12). Outside it each fillet runs from its far end
        // (square to it) to the sector: the side's top edge 8 − r cot 67.5°, its edge on the cut 8√2 − r cot 67.5°,
        // the cut's top edge 16 − r.
        ("ball at a 135° corner", new(
        [
            new Block("box", BooleanOp.New, [0, 0, 0], [20, 16, 12]),
            new PlaneCut("cut", [8, 0, 12], [1, 0, 1]), new PlaneCut("cut2", [16, 0, 4], [1, 0, -1]),
            new Fillet("r1", ["cut/face", "box/y+"], 2), new Fillet("r2", ["box/y+", "box/z+"], 2), new Fillet("r3", ["box/z+", "cut/face"], 2),
        ]), 152 * 16
            - Spandrel2 * (8 - 2 * (Math.Sqrt(2) - 1) + 8 * Math.Sqrt(2) - 2 * (Math.Sqrt(2) - 1))
            - 4 * (Math.Sqrt(2) - 1 - Math.PI / 8) * 14 - (8 * (Math.Sqrt(2) - 1) - 8 * Math.PI / 12)),
        // On an extruded profile: from a 120° corner 30 along each side (square at their other ends), 10 high, its top
        // edges there and then the profile's corner rounded to 2. As above, with the kite r² cot 60° across, the side
        // normals 60° apart (π r³/9), the top fillets 30 − r/√3 and the upright one 10 − r, spandrel r² (cot 60° − π/6).
        ("ball at a 120° corner of a profile", new(
        [
            new Extrude("corner", BooleanOp.New, Xy, Obtuse, 0, 10),
            new Fillet("r1", ["corner/end", "corner/side1"], 2), new Fillet("r2", ["corner/end", "corner/side2"], 2), new Fillet("r3", ["corner/side1", "corner/side2"], 2),
        ]), Math.Sqrt(3) * 900 * 10
            - Spandrel2 * 2 * (30 - 2 / Math.Sqrt(3)) - (1 / Math.Sqrt(3) - Math.PI / 6) * 4 * 8 - (8 / Math.Sqrt(3) - Math.PI * 8 / 9)),
        // A square corner of a profile: the ball corner of a box, on an extrude.
        ("ball at a square corner of a profile", new(
        [
            new Extrude("plate", BooleanOp.New, Xy, Rectangle(0, 0, 40, 30), 0, 10),
            new Fillet("r1", ["plate/end", "plate/e"], 2), new Fillet("r2", ["plate/end", "plate/n"], 2), new Fillet("r3", ["plate/e", "plate/n"], 2),
        ]), 12000 - Spandrel2 * (28 + 38 + 8) - CornerCap),
    ];

    // Pointed cones: a revolve's line at an angle that ends on the axis closes its cone at the apex, a pole.
    static readonly (string Name, PartDocument Document, double Volume)[] Points =
    [
        ("drill point", new([new Revolve("pin", BooleanOp.New, Upright(0, 0), new Profile([0, 0],
            [new ProfileEdge("bottom", [5, 0]), new ProfileEdge("wall", [5, 10]), new ProfileEdge("tip", [0, 15]), new ProfileEdge("axis", [0, 0])]))]),
            Math.PI * 25 * 10 + Math.PI * 25 * 5 / 3),
        ("cone", new([new Revolve("cone", BooleanOp.New, Upright(0, 0), new Profile([0, 0],
            [new ProfileEdge("bottom", [5, 0]), new ProfileEdge("side", [0, 8]), new ProfileEdge("axis", [0, 0])]))]), Math.PI * 25 * 8 / 3),
        // A cone cut point down into the plate, from its apex 4 below the top to radius 4 at the top.
        ("conical dimple", new([Plate40, new Revolve("dimple", BooleanOp.Cut, Upright(20, 15), new Profile([0, 6],
            [new ProfileEdge("side", [4, 10]), new ProfileEdge("over", [4, 11]), new ProfileEdge("top", [0, 11]), new ProfileEdge("axis", [0, 6])]))]),
            12000 - Math.PI * 16 * 4 / 3),
        // A cone (radius 5, apex 8 up) cut by the plane x = 2, along its axis: a hyperbola; the apex stays. What goes is, at each height, the circle's
        // segment past x = 2 (r² acos(2/r) − 2√(r² − 4) at radius r), over r from 5 down to 2 (dz = (8/5) dr).
        ("cone cut along its axis", new([new Revolve("cone", BooleanOp.New, Upright(0, 0), new Profile([0, 0],
            [new ProfileEdge("bottom", [5, 0]), new ProfileEdge("side", [0, 8]), new ProfileEdge("axis", [0, 0])])),
            new PlaneCut("cut", [2, 0, 0], [1, 0, 0])]), Math.PI * 25 * 8 / 3 - 8.0 / 5 * (SegmentIntegral(5) - SegmentIntegral(2))),
        // Cut in half through its axis: the cut runs through the apex.
        ("half a cone", new([new Revolve("cone", BooleanOp.New, Upright(0, 0), new Profile([0, 0],
            [new ProfileEdge("bottom", [5, 0]), new ProfileEdge("side", [0, 8]), new ProfileEdge("axis", [0, 0])])), new PlaneCut("cut", [0, 0, 0], [0, 1, 0])]),
            Math.PI * 25 * 8 / 6),
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
        .. Cones.Select(b => new object[] { b.Name, b.Document, b.Volume }),
        .. Copies.Select(b => new object[] { b.Name, b.Document, b.Volume }),
        .. Sweeps.Select(b => new object[] { b.Name, b.Document, b.Volume }),
        .. Domes.Select(b => new object[] { b.Name, b.Document, b.Volume }),
        .. Offsets.Select(b => new object[] { b.Name, b.Document, b.Volume }),
        .. Shells.Select(b => new object[] { b.Name, b.Document, b.Volume }),
        .. Balls.Select(b => new object[] { b.Name, b.Document, b.Volume }),
        .. Points.Select(b => new object[] { b.Name, b.Document, b.Volume }),
    ];

    /// <summary>
    /// The documents the F-rep core's own exact B-rep (<c>ExactBrep</c>) can be built for, to compare exact faces with:
    /// not the pointed cones, whose apex it cannot cut open, nor a dome cut through its pole (it sees the solid touch
    /// itself there).
    /// </summary>
    public static IEnumerable<object[]> ExactInTheFrep => All.Where(d => !Points.Any(p => p.Name == (string)d[0]) && (string)d[0] != "half a domed post");

    public static PartDocument Named(string name) => name switch
    {
        "mounting plate" => MountingPlate,
        "bracket" => Bracket,
        "two pads" => TwoPads,
        _ => Booleans.Concat(Holes).Concat(Angles).Concat(Rounds).Concat(Cones).Concat(Copies).Concat(Sweeps).Concat(Domes).Concat(Offsets).Concat(Shells).Concat(Balls).Concat(Points).Single(b => b.Name == name).Document,
    };
}
