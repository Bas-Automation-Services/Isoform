using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Isoform.Build;
using Isoform.Document;
using Isoform.Modeling;

namespace Isoform.Interactive;

/// <summary>
/// The text of <c>describe_format</c>. Every JSON example is written by the kernel's own <see cref="CanonicalFormat"/>
/// from real records, so the keys cannot drift from the code; the tests regenerate each example and check the face
/// names listed for it, and fail when a feature type has no example.
/// </summary>
public static class FormatGuide
{
    /// <summary>A feature shown in context: the features before it, and face names its regeneration is known to have.</summary>
    public sealed record Example(string Summary, Feature Feature, string[] Faces, Feature[] Before);

    /// <summary>The part every example starts from: a 100 × 50 × 10 plate at the origin.</summary>
    public static readonly Block Plate = new("plate", BooleanOp.New, [0, 0, 0], [100, 50, 10]);

    static readonly SketchPlane Top = new([0, 0, 10], [1, 0, 0], [0, 0, 1]);
    static readonly Hole Bore = new("hole", [30, 25], 10);

    static readonly Extrude Rib = new("rib", BooleanOp.Join, Top, new Profile([10, 10],
        [new ProfileEdge("a", [40, 10]), new ProfileEdge("b", [40, 16]), new ProfileEdge("c", [10, 16]), new ProfileEdge("d", [10, 10])]), 0, 6);

    /// <summary>A 30 × 20 outline with a Ø8 bore on the plate's top, every point fixed by its constraints.</summary>
    static readonly Sketch Outline = new("outline", new SketchPlacement(Face: "plate/z+"),
        new Dictionary<string, double[]> { ["a"] = [60, 15], ["b"] = [90, 15], ["c"] = [90, 35], ["d"] = [60, 35], ["h"] = [75, 25] },
        [new SketchLine("bottom", "a", "b"), new SketchLine("right", "b", "c"), new SketchLine("top", "c", "d"), new SketchLine("left", "d", "a"),
         new SketchCircle("bore", "h", 4)],
        [
            new("horizontal", ["bottom"]), new("vertical", ["right"]), new("horizontal", ["top"]), new("vertical", ["left"]),
            new("fix", ["a"]), new("length", ["bottom"], 30), new("length", ["left"], 20),
            new("horizontal-distance", ["a", "h"], 15), new("vertical-distance", ["a", "h"], 10), new("diameter", ["bore"], 8),
        ]);

    /// <summary>A handle's path over the plate in the plane y = 25 (sketch [u,v] is world [x,z]): up, half a turn over, down.</summary>
    static readonly Sketch Rail = new("rail", new SketchPlacement(new SketchPlane([0, 25, 0], [1, 0, 0], [0, -1, 0])),
        new Dictionary<string, double[]> { ["a"] = [30, 10], ["b"] = [30, 20], ["k"] = [45, 20], ["c"] = [60, 20], ["d"] = [60, 10] },
        [new SketchLine("up", "a", "b"), new SketchArc("over", "k", "c", "b"), new SketchLine("down", "c", "d")], []);

    /// <summary>The handle's section: a Ø6 circle on the plate's top where the path leaves it.</summary>
    static readonly Sketch Grip = new("grip", new SketchPlacement(Face: "plate/z+"), new Dictionary<string, double[]> { ["o"] = [30, 25] },
        [new SketchCircle("grip", "o", 3)], []);

    static readonly Sweep Handle = new("handle", BooleanOp.Join, "grip", null, new SweepPath("rail", From: "up"));

    /// <summary>One or more examples per feature type, each regenerating cleanly on the plate.</summary>
    public static IReadOnlyList<Example> Examples { get; } =
    [
        new("Axis-aligned box: origin [x,y,z], size [W,L,H], faces <id>/x- x+ y- y+ z- z+. op: new starts a body, join adds, " +
            "cut removes; blend (mm, join/cut) merges it smoothly through a face <id>/blend.",
            new Block("boss", BooleanOp.Join, [60, 15, 10], [20, 20, 12]), ["boss/x-", "boss/z+"], []),
        new("Through-all hole along Z at center [x,y]. Face <id>/wall.", Bore, ["hole/wall"], []),
        new("Into a flat face (on), center in its sketch coordinates; or at a sketch's points (sketch, points; faces " +
            "<id>/<point>:wall). depth makes it blind, floor <id>/floor.",
            new Hole("drill", [25, 5], 4) { On = "plate/x+", Depth = 12 }, ["drill/wall", "drill/floor"], []),
        new("Removes the material on the normal's side of the plane through point. Face <id>/face.",
            new PlaneCut("cut", [90, 0, 0], [1, 0, 1]), ["cut/face"], []),
        new("Round of radius on the edge between two faces. Face <id>/face.",
            new Fillet("round", ["plate/z+", "plate/x+"], 2), ["round/face"], []),
        new("Loop fillet: every edge round one face; edge stays empty. Faces <id>/<face across the edge>.",
            new Fillet("round", [], 1, Loop: "plate/z+"), ["round/plate/x-", "round/plate/y+"], []),
        new("Equal-distance chamfer on an edge (a plane, or a cone on a hole or boss edge); loop works as for fillets. Face <id>/face.",
            new Chamfer("bevel", ["plate/z+", "plate/y-"], 1), ["bevel/face"], []),
        new("Hollows the solid into a closed skin of this wall thickness. Inner faces <id>/inner:<outer face>.",
            new Shell("skin", 1.5), ["skin/inner:plate/z+"], []),
        new("A closed profile (see below) swept along its plane's normal from start to end (mm). Side faces <id>/<edge id>, " +
            "caps <id>/start and <id>/end.",
            Rib, ["rib/a", "rib/d", "rib/end"], []),
        new("A profile revolved round the sketch's y axis (x = radius ≥ 0, y = height). angle in degrees (default 360); " +
            "below 360 the flat ends are <id>/start and <id>/end. Faces <id>/<edge id>; an edge on the axis makes none.",
            new Revolve("knob", BooleanOp.Join, new SketchPlane([70, 25, 10], [1, 0, 0], [0, -1, 0]), new Profile([0, 0],
                [new ProfileEdge("base", [8, 0]), new ProfileEdge("side", [8, 6]), new ProfileEdge("top", [0, 6]), new ProfileEdge("axis", [0, 0])])),
            ["knob/side", "knob/top"], []),
        new("A 2D sketch on a plane or a flat face by name (it follows the face); no solid of its own. See Sketches below.",
            Outline, ["plate/z+"], []),
        new("Extrude or revolve of a sketch: regions picks some, each by the entity ids round its outside (default all); " +
            "loops inside are holes. Side faces <id>/<entity id>. A revolve's axis may name a line of the sketch.",
            new Extrude("pad", BooleanOp.Join, null, null, 0, 5) { Sketch = "outline" }, ["pad/bottom", "pad/bore", "pad/end"], [Outline]),
        new("Sketch regions along a path: another sketch's lines and arcs end to end, or a circle (entities, any order; or from: " +
            "the chain through one), tangent throughout, starting square to the profile's plane, no arc tighter than the profile. " +
            "Faces <id>/<path entity>:<profile entity>, <id>/start, <id>/end. Fillets and chamfers: cap edges (on a line), and a " +
            "corner of the profile, which rounds the whole path; other edges along the path are refused.",
            Handle, ["handle/up:grip", "handle/over:grip"], [Rail, Grip]),
        new("Moves every face of the part so far along its normal (negative: inward). Faces keep their names; a grown " +
            "convex corner of a swept profile becomes <id>/corner:<face>+<face>.",
            new Offset("grow", 1), ["rib/a", "grow/corner:rib/a+rib/b"], [Rib]),
        new("Copies earlier blocks, holes, plane cuts, extrudes, revolves or sweeps. Copy k (1, 2, ...) of <src> is " +
            "<id>/<k>:<src>, so names survive a count change. Linear: count instances in all, spacing apart along direction.",
            new Pattern("row", ["hole"], new LinearMotion([1, 0, 0], 20, 3)), ["row/1:hole/wall", "row/2:hole/wall"], [Bore]),
        new("Circular pattern: count instances in all round an axis; angle (default 360) puts the last one there when below 360.",
            new Pattern("ring", ["pin"], new CircularMotion([50, 25, 0], [0, 0, 1], 4)), ["ring/1:pin/wall", "ring/3:pin/wall"],
            [new Hole("pin", [40, 25], 6)]),
        new("Mirror: one mirrored copy in the plane through point with normal.",
            new Pattern("mirror", ["hole"], new MirrorMotion([50, 0, 0], [1, 0, 0])), ["mirror/1:hole/wall"], [Bore]),
        new("Joins or cuts (op) body tool into the current body or the one named by body; the tool is used up unless keep " +
            "is true. Faces keep their names.",
            new CombineBodies("merge", "tab", BooleanOp.Join) { Body = "plate" }, ["tab/z+", "plate/z+"],
            [new Block("tab", BooleanOp.New, [40, 20, 5], [20, 10, 10])]),
        new("Fills the body with a lattice of period cell (mm) on the world grid: gyroid, schwarz-p or diamond sheets " +
            "at least thickness thick (face <id>/surface), or bcc, octet or cubic struts of diameter thickness (<id>/struts). " +
            "skin > 0 keeps a closed wall that thick (faces <id>/inner:<face>), the lattice joined inside. region, a box " +
            "{origin, size} or an earlier block, extrude, revolve or sweep id, limits it; solid outside, faces <id>/region:" +
            "<face>. Fillets, chamfers, shells and offsets go before it; STEP names the lattice face unsupported.",
            new Lattice("infill", "bcc", 10, 2, Region: new LatticeRegion(null, [60, 10, -1], [30, 30, 12])),
            ["infill/struts", "infill/region:x-"], []),
    ];

    /// <summary>Edits shown in the guide, valid on <see cref="EditTarget"/>.</summary>
    public static IReadOnlyList<string> EditExamples { get; } =
    [
        """{"set": "parameters.width", "value": 120}""",
        """{"set": "hole.diameter", "value": "=width / 8"}""",
        """{"set": "plate.size.2", "value": 12}""",
        """{"set": "row.linear.count", "value": 4}""",
        """{"insert": {"id": "edge", "type": "fillet", "edge": ["plate/z+", "plate/x-"], "radius": 2}, "before": "row"}""",
        """{"remove": "row"}""",
    ];

    /// <summary>Edits shown in the guide, valid on <see cref="BundleExample"/>: its assembly's, and one of its parts.</summary>
    public static IReadOnlyList<string> AssemblyEditExamples { get; } =
    [
        """{"set": "components.pin.parameters.length", "value": 30}""",
        """{"set": "mates.stop.offset", "value": 2}""",
        """{"remove": "mates.stop"}""",
        """{"document": "bracket.json", "set": "hole.diameter", "value": 10.5}""",
    ];

    /// <summary>The part the edit examples apply to, with parameters: the plate's width and the bore follow <c>width</c>.</summary>
    public static PartDocument EditTarget { get; } = new([Plate, Bore, new Pattern("row", ["hole"], new LinearMotion([1, 0, 0], 20, 3))])
    {
        Parameters = new Dictionary<string, string> { ["width"] = "100", ["bore"] = "width / 10" },
        Expressions = new Dictionary<string, string> { ["plate.size.0"] = "width", ["hole.diameter"] = "bore" },
    };

    /// <summary>A small assembly: a bracket and a pin standing in its bore, made 25 long by a parameter.</summary>
    public static AssemblyDocument AssemblyExample { get; } = new(
    [
        new Component("base", "bracket.json", Placement.Identity, Grounded: true),
        new Component("pin", "pin.json", new Placement([30, 25, -5], [1, 0, 0], [0, 0, 1]), Parameters: new Dictionary<string, string> { ["length"] = "25" }),
    ],
    [
        new Mate("seat", MateKind.Axis, "pin/shank/a", "base/hole/wall"),
        new Mate("stop", MateKind.Flush, "pin/shank/start", "base/plate/z-"),
    ]);

    /// <summary>The assembly example with its two documents: the plate with its hole, and a Ø9.8 pin of parameter length.</summary>
    public static Bundle BundleExample { get; } = new(AssemblyExample, new Dictionary<string, string>
    {
        ["bracket.json"] = CanonicalFormat.Write(new PartDocument([Plate, Bore])),
        ["pin.json"] = CanonicalFormat.Write(new PartDocument([new Extrude("shank", BooleanOp.New, new SketchPlane([0, 0, 0], [1, 0, 0], [0, 0, 1]),
            new Profile([-4.9, 0], [new ProfileEdge("a", [4.9, 0], 1), new ProfileEdge("b", [-4.9, 0], 1)]), 0, 20)])
        {
            Parameters = new Dictionary<string, string> { ["length"] = "20" },
            Expressions = new Dictionary<string, string> { ["shank.end"] = "length" },
        }),
    });

    /// <summary>A build document: the pump stand's top and legs (<c>Examples.PumpStandBuild</c>, shortened).</summary>
    public static BuildDocument BuildExample { get; } = new("stand.json",
        [new PlateSection("top", 12, Face: "cap/face"), new TubeSection("legs", 48.3, 3.2, Along: "leg/a")],
        Material: new Material("S235", 7.85e-6), Keepouts: [new Keepout("hose", 5)], Interfaces: ["cap/face"]);

    /// <summary>The guide as plain text, built once.</summary>
    public static string Text => _text.Value;

    static readonly Lazy<string> _text = new(Build);

    static string Build()
    {
        var s = new StringBuilder();
        s.AppendLine("# Isoform document format (" + PartDocument.FormatVersion + ")");
        s.AppendLine();
        s.AppendLine("A part is JSON: " + Compact(CanonicalFormat.Write(new PartDocument([Plate, Bore]))));
        s.AppendLine("Units mm, Z up. Features apply in order; the first after any sketches starts a body (op new). Ids are unique,");
        s.AppendLine("stable (never from position), non-empty, without '/'. Points [x,y,z], 2D points [u,v]. Tools return the canonical");
        s.AppendLine("text (fixed key order) and its SHA-256 hash.");
        s.AppendLine();
        s.AppendLine("## Feature types (on the plate above)");
        foreach (var e in Examples)
        {
            var json = FormatGuide.Feature(e.Feature);
            s.AppendLine(CultureInfo.InvariantCulture, $"- {json["type"]}: {e.Summary}");
            s.AppendLine(CultureInfo.InvariantCulture, $"  {json.ToJsonString(AgentTools.Compact)}");
            s.AppendLine(CultureInfo.InvariantCulture, $"  faces: {string.Join(", ", e.Faces)}");
        }
        s.AppendLine();
        s.AppendLine("Sketch plane: origin, x (in-plane x direction), normal; its y is normal × x. Profile: start [u,v] and edges");
        s.AppendLine("{id, to [u,v], bulge?}, each from the previous point; the last edge ends at start; either winding. bulge = tan(θ/4)");
        s.AppendLine("for an arc of included angle θ, positive turning left (1 = half circle). Edge ids are unique, not 'start' or 'end'.");
        s.AppendLine();
        s.AppendLine("## Sketches");
        s.AppendLine("on: {\"plane\": {...}} or {\"face\": \"plate/z+\"}. On a face the plane is the face's, normal out of the part, origin and x the");
        s.AppendLine("world's put onto it: on a top face sketch [u,v] is world [x,y]; on a face looking the other way v runs opposite.");
        s.AppendLine("points: {name: [u,v]}, the last solved coordinates. entities: {id, line: [p, q]}, {id, arc: {center, from, to}} (counter-");
        s.AppendLine("clockwise), {id, circle: {center, radius}}; construction: true takes part in constraints only. Names never contain '.' or '/'.");
        s.AppendLine("constraints: {<kind>: ref or [refs], value?}. Geometric: " + string.Join(", ", SketchConstraint.Geometric) + ".");
        s.AppendLine("Dimensions (value in mm, angle in degrees, or \"=expression\"): " + string.Join(", ", SketchConstraint.Dimensions) + ".");
        s.AppendLine("Refs: coincident [p,q]; horizontal/vertical line or [p,q]; parallel/perpendicular/angle [line,line]; tangent [line or round,");
        s.AppendLine("round]; equal [line,line] or [round,round]; midpoint [p,line]; symmetric [p,q,line]; on [p,line or round]; fix p; length line;");
        s.AppendLine("distance [p,q], [p,line] or [line,line]; horizontal-/vertical-distance [p,q]; radius/diameter round (arc or circle).");
        s.AppendLine("Every sketch is solved (sketch-conflict: constraints that cannot all hold; sketch-redundant: one the others imply);");
        s.AppendLine("free points keep their stored place. Lines and arcs join end to end; open chains are ignored (a sweep's path uses");
        s.AppendLine("them); a point on three or more edges, or crossing loops, are sketch-invalid.");
        s.AppendLine("sketch_edit fillets a corner point where two lines or arcs meet (a tangent arc; the corner stays, as a construction");
        s.AppendLine("point) or trims the piece of an entity between its crossings nearest [x, y]; constraints on a deleted piece are dropped and listed.");
        s.AppendLine("sketch_edit project copies a part edge [faceA, faceB] in (no ellipses); linked: construction with project: [faceA, faceB],");
        s.AppendLine("held on the edge each regeneration, else sketch-reference-lost and it stays put.");
        s.AppendLine();
        s.AppendLine("## Face names");
        s.AppendLine("Faces are named <feature id>/<face> by the feature that made them, as listed per type above, and keep their names");
        s.AppendLine("through parameter edits: refer to faces by name (edges, loops, mates), never by index. A face two features share");
        s.AppendLine("(flush faces, a merge) lists both names; a face cut in pieces appears once per piece; a lattice is one face however");
        s.AppendLine("many pieces. In an assembly: <component id>/<face>, sub-assemblies first (left/plate/z+).");
        s.AppendLine();
        s.AppendLine("## Bodies");
        s.AppendLine("op new starts a body named by the feature's body key, else its id. A feature works on the body it names in \"body\"");
        s.AppendLine("(which becomes current), else the current one; fillets and chamfers find theirs from their faces. Bodies stay");
        s.AppendLine("separate solids until a combine joins or cuts them.");
        s.AppendLine();
        s.AppendLine("## Parameters");
        s.AppendLine("\"parameters\" (after units) names values: a number, or \"=expression\" over other parameters, as any number in a");
        s.AppendLine("feature may be. Operators + - * / ^, parentheses, " + string.Join(", ", Expression.Reserved) + " (degrees; counts");
        s.AppendLine("must come out whole). Example:");
        s.AppendLine(Compact(CanonicalFormat.Write(EditTarget)));
        s.AppendLine();
        s.AppendLine("## Edits (apply_edits)");
        s.AppendLine("set takes a dotted path from a feature id (array indices are numbers: rib.profile.edges.1.to); the value may be");
        s.AppendLine("an \"=expression\". parameters.<name> sets (or removes) a parameter. insert adds a feature before an id (else at");
        s.AppendLine("the end); remove deletes one. Valid on the document above:");
        foreach (var e in EditExamples) s.AppendLine("- " + e);
        s.AppendLine("An assembly takes them on components.<id>.<field> and mates.<id>.<field>; insert adds a component (it has a part)");
        s.AppendLine("or a mate (a kind). In a bundle an edit changes the assembly, or with \"document\": \"<path>\" that part; set \"\" with");
        s.AppendLine("a document adds or replaces it whole, remove \"\" drops it. Valid on the bundle under Assemblies:");
        foreach (var e in AssemblyEditExamples) s.AppendLine("- " + e);
        s.AppendLine("Edits apply all or nothing; one that cannot apply is an error naming it (edit 2 {...}: why).");
        s.AppendLine();
        s.AppendLine("## Assemblies (assembly tool)");
        s.AppendLine($"An assembly ({AssemblyDocument.FormatVersion}) places parts by path; placement is origin, x and normal (z) of the part's frame.");
        s.AppendLine("hash (optional) pins the part's content; grounded components stay put (else the first); parameters override the");
        s.AppendLine("part's own. Mate kinds: mate (flat faces against each other, gap offset), flush (flat faces facing the same way),");
        s.AppendLine("axis (cylinders or cones coaxial).");
        s.AppendLine(Compact(CanonicalFormat.Write(AssemblyExample)));
        s.AppendLine($"A bundle ({Bundle.FormatVersion}) carries the assembly and its documents in one file:");
        s.AppendLine($"{{\"format\":\"{Bundle.FormatVersion}\",\"units\":\"mm\",\"assembly\":{{...}},\"documents\":{{\"bracket.json\":{{part}},\"pin.json\":{{part}}}}}}");
        s.AppendLine();
        s.AppendLine("## Builds (build, verify; native only)");
        s.AppendLine($"A build document ({BuildDocument.FormatVersion}) makes a design of plates and tubes, bound by face names:");
        s.AppendLine(Compact(CanonicalFormat.Write(BuildExample)));
        s.AppendLine("plate: on a flat face (its outer face; thickness into the material) or a plane; near [x,y,z] picks a region. tube:");
        s.AppendLine("along a cylinder's axis or a line [[x,y,z],[x,y,z]], ends cut by earlier plates. A name covers its pattern copies.");
        s.AppendLine("Sections are priority: each is cut by the parts before it. Keep-outs are bodies (clearance mm), interfaces faces");
        s.AppendLine("copied exactly. Edits: sections.<id>.<field>. build writes the bundle, STEP, DXFs, cut list, BOM and report;");
        s.AppendLine("verify checks any bundle (pass, fail, unknown, reported). A blend acts within k on both sides: run members long,");
        s.AppendLine("blend them, then trim with plane cuts. Build codes: " + string.Join(", ",
            BuildDiagnostic.FaceMissing, BuildDiagnostic.RegionEmpty, BuildDiagnostic.TubeMeetsTube, BuildDiagnostic.OpenEnd) + ".");
        s.AppendLine();
        s.AppendLine("## Diagnostics");
        s.AppendLine("Regeneration never fails silently: a problem is a diagnostic {code, feature?, message}. Codes: " + string.Join(", ", DiagnosticCodes()) + ".");
        s.AppendLine("below-resolution means a feature is under 2 × cellSize: lower cellSize (default 0.5 mm) to resolve it.");
        return s.ToString();
    }

    /// <summary>Every diagnostic code the kernel defines, read from <see cref="Diagnostic"/> itself.</summary>
    public static IEnumerable<string> DiagnosticCodes() => typeof(Diagnostic).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!);

    /// <summary>A feature as its canonical JSON.</summary>
    public static JsonObject Feature(Feature f) => CanonicalFormat.ToNode(new PartDocument([f]))["features"]![0]!.AsObject();

    static string Compact(string canonical) => JsonNode.Parse(canonical)!.ToJsonString(AgentTools.Compact);
}
