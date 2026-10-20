using Isoform.Document;

namespace Isoform.Interactive;

/// <summary>Starting points for the playground, one per kind of feature.</summary>
public static class Examples
{
    static readonly Block Plate = new("plate", BooleanOp.New, [0, 0, 0], [100, 50, 10]);
    static readonly SketchPlane Xy = new([0, 0, 0], [1, 0, 0], [0, 0, 1]);
    static readonly SketchPlane Top = new([0, 0, 10], [1, 0, 0], [0, 0, 1]);
    static readonly SketchPlane Xz = new([0, 0, 0], [1, 0, 0], [0, -1, 0]);

    static Profile Circle(double cx, double cy, double r) => new([cx - r, cy], [new ProfileEdge("a", [cx + r, cy], 1), new ProfileEdge("b", [cx - r, cy], 1)]);

    static readonly PartDocument Bracket = new(
    [
        new Block("plate", BooleanOp.New, [0, 0, 0], [40, 20, 5]),
        new Hole("bore", [10, 10], 6),
        new Fillet("round", [], 1, Loop: "plate/z+"),
    ]);

    static readonly PartDocument Pin = new([new Revolve("pin", BooleanOp.New, Xz, new Profile([0, 0],
    [
        new ProfileEdge("tip", [2.9, 0]), new ProfileEdge("shank", [2.9, 24]), new ProfileEdge("head", [5, 24]),
        new ProfileEdge("collar", [5, 27]), new ProfileEdge("cap", [0, 27]), new ProfileEdge("axis", [0, 0]),
    ])), new Chamfer("lead", ["pin/tip", "pin/shank"], 1)]);

    /// <summary>Assemblies with their documents, as one bundle each.</summary>
    public static IReadOnlyList<(string Name, Bundle Bundle)> Bundles { get; } =
    [
        ("Assembly: two brackets and a pin", new Bundle(new AssemblyDocument(
        [
            new Component("lower", "bracket.json", Placement.Identity),
            new Component("upper", "bracket.json", new Placement([0, 20, 15], [1, 0, 0], [0, 0, -1])),
            new Component("pin", "pin.json", new Placement([10, 10, -9], [1, 0, 0], [0, 0, 1])),
        ]), new Dictionary<string, string> { ["bracket.json"] = CanonicalFormat.Write(Bracket), ["pin.json"] = CanonicalFormat.Write(Pin) })),
    ];

    public static IReadOnlyList<(string Name, PartDocument Doc)> All { get; } =
    [
        ("Plate with hole and fillet", new(
        [
            Plate,
            new Hole("hole", [30, 25], 10),
            new Fillet("fillet", ["plate/z+", "plate/x+"], 2),
        ])),
        ("Bracket: extrude, inside round, hole row", new(
        [
            new Extrude("bracket", BooleanOp.New, Xy, new Profile([0, 0],
            [
                new ProfileEdge("bottom", [60, 0]), new ProfileEdge("tip", [60, 6]), new ProfileEdge("inner", [6, 6]),
                new ProfileEdge("riser", [6, 40]), new ProfileEdge("top", [0, 40]), new ProfileEdge("back", [0, 0]),
            ]), 0, 30),
            new Fillet("inside", ["bracket/inner", "bracket/riser"], 4),
            new Fillet("outside", ["bracket/bottom", "bracket/back"], 6),
            new Fillet("edge", [], 1.5, Loop: "bracket/end"),
            new Revolve("bore", BooleanOp.Cut, new SketchPlane([40, 0, 15], [0, 0, 1], [-1, 0, 0]), new Profile([0, -1],
            [
                new ProfileEdge("bottom", [3, -1]), new ProfileEdge("wall", [3, 7]), new ProfileEdge("top", [0, 7]), new ProfileEdge("axis", [0, -1]),
            ])),
            new Pattern("row", ["bore"], new LinearMotion([-1, 0, 0], 12, 3)),
        ])),
        ("Turned shaft: revolve with undercut and dome", new(
        [
            new Revolve("shaft", BooleanOp.New, Xz, new Profile([0, 0],
            [
                new ProfileEdge("base", [12, 0]), new ProfileEdge("big", [12, 20]), new ProfileEdge("step", [7, 20]),
                new ProfileEdge("small", [7, 45]), new ProfileEdge("cone", [4, 52]), new ProfileEdge("dome", [0, 56], Math.Tan(Math.PI / 8)),
                new ProfileEdge("axis", [0, 0]),
            ])),
            new Fillet("undercut", ["shaft/step", "shaft/small"], 1.5),
            new Chamfer("lead", ["shaft/base", "shaft/big"], 1),
        ])),
        ("Flange: bolt circle and countersinks", new(
        [
            new Extrude("flange", BooleanOp.New, Xy, Circle(0, 0, 40), 0, 8),
            new Extrude("hub", BooleanOp.Join, new SketchPlane([0, 0, 8], [1, 0, 0], [0, 0, 1]), Circle(0, 0, 18), 0, 14),
            new Fillet("foot", ["flange/end", "hub/a"], 3),
            new Hole("bore", [0, 0], 16),
            new Chamfer("bore-edge", ["hub/end", "bore/wall"], 1),
            // A countersunk bolt hole as one revolved cut, so the pattern copies the countersink too.
            new Revolve("bolt", BooleanOp.Cut, new SketchPlane([30, 0, 0], [1, 0, 0], [0, -1, 0]), new Profile([0, -1],
            [
                new ProfileEdge("bottom", [3, -1]), new ProfileEdge("wall", [3, 7]), new ProfileEdge("sink", [5, 9]),
                new ProfileEdge("top", [0, 9]), new ProfileEdge("axis", [0, -1]),
            ])),
            new Pattern("circle", ["bolt"], new CircularMotion([0, 0, 0], [0, 0, 1], 6)),
        ])),
        ("Rounded outline with a slot: loop fillets", new(
        [
            new Extrude("outline", BooleanOp.New, Xy, new Profile([0, 0],
            [
                new ProfileEdge("s", [80, 0]), new ProfileEdge("e", [80, 50]), new ProfileEdge("n", [0, 50]), new ProfileEdge("w", [0, 0]),
            ]), 0, 8),
            new Fillet("corner", ["outline/s", "outline/e"], 10),
            new Fillet("corner2", ["outline/n", "outline/w"], 10),
            new Extrude("slot", BooleanOp.Cut, Xy, new Profile([25, 20],
            [
                new ProfileEdge("a", [55, 20]), new ProfileEdge("right", [55, 30], 1), new ProfileEdge("b", [25, 30]), new ProfileEdge("left", [25, 20], 1),
            ]), -1, 9),
            new Fillet("top", [], 1.5, Loop: "outline/end"),
            new Chamfer("bottom", [], 1, Loop: "outline/start"),
        ])),
        ("Pocket with rounded floor, boss with foot fillet", new(
        [
            Plate,
            new Block("pocket", BooleanOp.Cut, [10, 10, 4], [35, 30, 10]),
            new Fillet("floor", [], 3, Loop: "pocket/z-"),
            new Block("boss", BooleanOp.Join, [65, 15, 10], [20, 20, 12]),
            new Fillet("foot", ["plate/z+", "boss/x-"], 3),
            new Chamfer("top", ["boss/z+", "boss/x+"], 2),
        ])),
        ("Smooth blends and a shell", new(
        [
            Plate,
            new Extrude("knob", BooleanOp.Join, Top, Circle(30, 25, 10), 0, 15, Blend: 4),
            new Block("groove", BooleanOp.Cut, [60, -1, 6], [12, 52, 10], Blend: 3),
            new Shell("shell", 1.5),
        ])),
        ("Two bodies: a base and a clamp that cuts its own seat", new(
        [
            Plate,
            new Hole("mount", [10, 25], 6),
            new Pattern("mounts", ["mount"], new MirrorMotion([50, 0, 0], [1, 0, 0])),
            new Extrude("clamp", BooleanOp.New, new SketchPlane([0, 0, 6], [1, 0, 0], [0, 0, 1]), Circle(50, 25, 12), 0, 14),
            new Hole("bore", [50, 25], 10),
            new Fillet("lip", [], 1.5, Loop: "clamp/end"),
            new CombineBodies("seat", "clamp", BooleanOp.Cut, Keep: true) { Body = "plate" },
        ])),
        ("Mirror and offset", new(
        [
            Plate,
            new Extrude("rib", BooleanOp.Join, Top, new Profile([5, 5],
            [
                new ProfileEdge("a", [30, 5]), new ProfileEdge("b", [30, 12]), new ProfileEdge("c", [12, 12]),
                new ProfileEdge("d", [12, 40]), new ProfileEdge("e", [5, 40]), new ProfileEdge("f", [5, 5]),
            ]), 0, 6),
            new Pattern("mirror", ["rib"], new MirrorMotion([50, 0, 0], [1, 0, 0])),
            new Offset("grow", 1),
        ])),
    ];
}
