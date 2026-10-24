using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Geometry;
using Isoform.Modeling;

namespace Isoform.Tests.Build;

/// <summary>
/// The pump stand of the demo plan (§1) and a bundle rationalising it by hand: the top plate, four feet and four legs
/// (Ø48.3 × 3.2 tubes cut by the feet's tops and the top's underside), each an exact part placed by its pattern copy.
/// </summary>
static class Stand
{
    static readonly SketchPlane Xy = new([0, 0, 0], [1, 0, 0], [0, 0, 1]);

    static Profile Circle(double r, double x = 0, double y = 0) =>
        new([x + r, y], [new ProfileEdge("a", [x - r, y], 1), new ProfileEdge("b", [x + r, y], 1)]);

    static SketchPlane At(double z) => Xy with { Origin = [0, 0, z] };

    static readonly CircularMotion Four = new([0, 0, 0], [0, 0, 1], 4);

    // A leg's axis, from the foot's top to the top's underside, 15.1° off vertical.
    static readonly (Point3D From, Point3D To) Axis = (new Point3D(200, 200, 12), new Point3D(90, 90, 588));

    static Frame LegFrame
    {
        get
        {
            var axis = (Axis.To - Axis.From).Normalized();
            return Frame.FromXAndNormal(Axis.From, new Vector3D(1, 0, 0).Cross(axis).Normalized(), axis);
        }
    }

    /// <summary>The design: one body <c>stand</c>, and the keep-outs <c>pump</c>, <c>hose</c> and <c>sockets</c>.</summary>
    public static readonly PartDocument Design = BuildDesign();

    static PartDocument BuildDesign()
    {
        var axis = (Axis.To - Axis.From).Normalized();
        var length = (Axis.To - Axis.From).Length;
        var start = Axis.From - axis * 60;
        var x = LegFrame.X;
        var legPlane = new SketchPlane([start.X, start.Y, start.Z], [x.X, x.Y, x.Z], [axis.X, axis.Y, axis.Z]);
        return new PartDocument(
        [
            new Extrude("top", BooleanOp.New, At(560), Circle(130), 0, 60) { Body = "stand" },
            new Extrude("lobe", BooleanOp.Join, At(560), Circle(45, 90, 90), 0, 60, Blend: 20) { Body = "stand" },
            new Pattern("lobes", ["lobe"], Four),
            new PlaneCut("under", [0, 0, 588], [0, 0, -1]),
            new Hole("bore", [0, 0], 80),
            new Hole("mount", [100, 0], 11),
            new Pattern("mounts", ["mount"], Four),
            new Block("foot", BooleanOp.Join, [150, 150, 0], [160, 160, 12]),
            new Hole("anchor", [270, 270], 18),
            new Pattern("feet", ["foot", "anchor"], Four),
            new Extrude("leg", BooleanOp.Join, legPlane, Circle(25), 0, length + 120, Blend: 10),
            new Pattern("legs", ["leg"], Four),
            new PlaneCut("floor", [0, 0, 0], [0, 0, -1]),
            new PlaneCut("cap", [0, 0, 600], [0, 0, 1]),
            new Extrude("pump", BooleanOp.New, At(600), Circle(150), 0, 300) { Body = "pump" },
            new Extrude("hose", BooleanOp.New, At(0), Circle(30), 0, 900) { Body = "hose" },
            // One socket: four would be one body in four pieces (a multiple-bodies diagnostic).
            new Extrude("socket", BooleanOp.New, At(12), Circle(20, 270, 270), 0, 60) { Body = "sockets" },
        ]);
    }

    public static readonly BuildDocument Build = new("stand.json",
    [
        new PlateSection("top", 12, Face: "cap/face"),
        new PlateSection("feet", 12, Face: "foot/z+"),
        new TubeSection("legs", 48.3, 3.2, Along: "leg/a"),
    ], Material: new Material("S235", 7.85e-6), Keepouts: [new Keepout("pump"), new Keepout("hose", 5), new Keepout("sockets")],
        Interfaces: ["cap/face", "mount/wall", "bore/wall", "foot/z-", "anchor/wall"]);

    /// <summary>The top plate in its own frame (z 0 to 12): its blended outline is extruded long and trimmed, as the design's.</summary>
    public static readonly PartDocument Top = new(
    [
        new Extrude("top", BooleanOp.New, At(-30), Circle(130), 0, 72),
        new Extrude("lobe", BooleanOp.Join, At(-30), Circle(45, 90, 90), 0, 72, Blend: 20),
        new Pattern("lobes", ["lobe"], Four),
        new PlaneCut("under", [0, 0, 0], [0, 0, -1]),
        new PlaneCut("cap", [0, 0, 12], [0, 0, 1]),
        new Hole("bore", [0, 0], 80),
        new Hole("mount", [100, 0], 11),
        new Pattern("mounts", ["mount"], Four),
    ]);

    public static readonly PartDocument Foot = new([new Block("foot", BooleanOp.New, [0, 0, 0], [160, 160, 12]), new Hole("anchor", [120, 120], 18)]);

    /// <summary>A leg: Ø48.3 × 3.2 along z in its own frame, cut by the foot's top and the top's underside.</summary>
    public static readonly PartDocument Leg = BuildLeg();

    static PartDocument BuildLeg()
    {
        var frame = LegFrame;
        var length = (Axis.To - Axis.From).Length;
        double[] Local(Point3D p) => [frame.ToLocal(p).X, frame.ToLocal(p).Y, frame.ToLocal(p).Z];
        double[] Dir(Vector3D v) => [frame.ToLocal(v).X, frame.ToLocal(v).Y, frame.ToLocal(v).Z];
        return new PartDocument(
        [
            new Extrude("tube", BooleanOp.New, At(-40), Circle(24.15), 0, length + 80),
            new Extrude("bore", BooleanOp.Cut, At(-50), Circle(24.15 - 3.2), 0, length + 100),
            new PlaneCut("low", Local(new Point3D(0, 0, 12)), Dir(new Vector3D(0, 0, -1))),
            new PlaneCut("high", Local(new Point3D(0, 0, 588)), Dir(new Vector3D(0, 0, 1))),
        ]);
    }

    static Point3D Turn(Point3D p, int k) => k switch { 0 => p, 1 => new(-p.Y, p.X, p.Z), 2 => new(-p.X, -p.Y, p.Z), _ => new(p.Y, -p.X, p.Z) };

    static Vector3D Turn(Vector3D v, int k) => Turn(new Point3D(v.X, v.Y, v.Z), k).AsVector();

    static Placement Place(Point3D origin, Vector3D x, Vector3D normal) => new([origin.X, origin.Y, origin.Z], [x.X, x.Y, x.Z], [normal.X, normal.Y, normal.Z]);

    /// <summary>The hand-made bundle: the design, the build document and the three parts, nine instances.</summary>
    public static Bundle Bundle(PartDocument? top = null, PartDocument? leg = null)
    {
        var (t, l) = (top ?? Top, leg ?? Leg);
        var frame = LegFrame;
        var z = new Vector3D(0, 0, 1);
        var components = new List<Component> { new("top", "top.json", Place(new Point3D(0, 0, 588), new Vector3D(1, 0, 0), z), CanonicalFormat.Hash(t)) };
        for (var k = 0; k < 4; k++)
            components.Add(new($"feet-{k + 1}", "foot.json", Place(Turn(new Point3D(150, 150, 0), k), Turn(new Vector3D(1, 0, 0), k), z), CanonicalFormat.Hash(Foot)));
        for (var k = 0; k < 4; k++)
            components.Add(new($"legs-{k + 1}", "leg.json", Place(Turn(frame.Origin, k), Turn(frame.X, k), Turn(frame.Z, k)), CanonicalFormat.Hash(l)));
        return new Bundle(new AssemblyDocument(components), new Dictionary<string, string>
        {
            ["stand.json"] = CanonicalFormat.Write(Design),
            ["build.json"] = CanonicalFormat.Write(Build),
            ["top.json"] = CanonicalFormat.Write(t),
            ["foot.json"] = CanonicalFormat.Write(Foot),
            ["leg.json"] = CanonicalFormat.Write(l),
        });
    }
}
