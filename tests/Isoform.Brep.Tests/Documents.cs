using Isoform.Document;

namespace Isoform.Brep.Tests;

/// <summary>Isoform part documents the managed kernel builds in experiment 1, with their exact volumes.</summary>
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

    public static IEnumerable<object[]> All =>
    [
        ["mounting plate", MountingPlate, MountingPlateVolume],
        ["bracket", Bracket, BracketVolume],
        ["two pads", TwoPads, TwoPadsVolume],
    ];

    public static PartDocument Named(string name) => name switch
    {
        "mounting plate" => MountingPlate,
        "bracket" => Bracket,
        "two pads" => TwoPads,
        _ => throw new ArgumentException(name),
    };
}
