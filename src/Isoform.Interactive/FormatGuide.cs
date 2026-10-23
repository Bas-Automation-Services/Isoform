using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
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

    /// <summary>One or more examples per feature type, each regenerating cleanly on the plate.</summary>
    public static IReadOnlyList<Example> Examples { get; } =
    [
        new("Axis-aligned box from origin [x,y,z] with size [W,L,H]. op: new starts a body, join adds, cut removes. " +
            "Faces <id>/x-, x+, y-, y+, z-, z+. Optional blend (mm, join/cut only) merges it smoothly through a face <id>/blend.",
            new Block("boss", BooleanOp.Join, [60, 15, 10], [20, 20, 12]), ["boss/x-", "boss/x+", "boss/y-", "boss/y+", "boss/z+"], []),
        new("Through-all hole along Z at center [x,y]. Face <id>/wall.", Bore, ["hole/wall"], []),
        new("Hole into a flat face (on) at center in the face's sketch coordinates, or at points of a sketch (sketch, points; " +
            "faces <id>/<point>:wall). depth makes it blind with a floor <id>/floor; without it, through all.",
            new Hole("drill", [25, 5], 4) { On = "plate/x+", Depth = 12 }, ["drill/wall", "drill/floor"], []),
        new("Removes all material on the side the normal points to, from point. Face <id>/face.",
            new PlaneCut("cut", [90, 0, 0], [1, 0, 1]), ["cut/face"], []),
        new("Constant-radius fillet on the edge between two source faces. Face <id>/face.",
            new Fillet("round", ["plate/z+", "plate/x+"], 2), ["round/face"], []),
        new("Loop fillet: every edge round one face; edge stays empty. Faces <id>/<face across the edge>.",
            new Fillet("round", [], 1, Loop: "plate/z+"), ["round/plate/x-", "round/plate/x+", "round/plate/y-", "round/plate/y+"], []),
        new("Equal-distance chamfer on an edge (a plane, or a cone on a hole or boss edge); loop works as for fillets. Face <id>/face.",
            new Chamfer("bevel", ["plate/z+", "plate/y-"], 1), ["bevel/face"], []),
        new("Hollows the solid into a closed skin of this wall thickness. Inner faces <id>/inner:<outer face>.",
            new Shell("skin", 1.5), ["skin/inner:plate/z+", "skin/inner:plate/x-"], []),
        new("A closed profile of lines and arcs on a sketch plane, swept along the plane normal from start to end (mm). " +
            "Side faces <id>/<edge id>, caps <id>/start and <id>/end.",
            Rib, ["rib/a", "rib/b", "rib/c", "rib/d", "rib/end"], []),
        new("A profile revolved round the sketch's y axis (x = radius ≥ 0, y = height). angle in degrees (default 360); " +
            "below 360 the flat ends are <id>/start and <id>/end. Faces <id>/<edge id>; an edge on the axis makes none.",
            new Revolve("knob", BooleanOp.Join, new SketchPlane([70, 25, 10], [1, 0, 0], [0, -1, 0]), new Profile([0, 0],
                [new ProfileEdge("base", [8, 0]), new ProfileEdge("side", [8, 6]), new ProfileEdge("top", [0, 6]), new ProfileEdge("axis", [0, 0])])),
            ["knob/side", "knob/top"], []),
        new("A 2D sketch on a plane or on a flat face by name (it follows the face): named points with their last solved " +
            "coordinates, lines, arcs and circles between them, and constraints. It makes no solid; extrudes and revolves sweep " +
            "its regions. See Sketches below.",
            Outline, ["plate/z+"], []),
        new("An extrude (or revolve) of a sketch: sketch names it, regions optionally picks regions, each by entity ids round " +
            "its outside (all when left out). Loops inside a region are holes. Side faces <id>/<entity id>, caps as before. " +
            "A revolve's axis may name a line of the sketch to turn round (construction or not) instead of the sketch's y axis.",
            new Extrude("pad", BooleanOp.Join, null, null, 0, 5) { Sketch = "outline" },
            ["pad/bottom", "pad/right", "pad/top", "pad/left", "pad/bore", "pad/end"], [Outline]),
        new("Moves every face of the part so far along its normal (negative: inward). Faces keep their names; a grown " +
            "convex corner of a swept profile becomes <id>/corner:<face>+<face>.",
            new Offset("grow", 1), ["plate/z+", "rib/a", "grow/corner:rib/a+rib/b"], [Rib]),
        new("Copies earlier blocks, holes, plane cuts, extrudes or revolves. Copy k (1, 2, ...) of <src> is <id>/<k>:<src>, " +
            "so names survive a count change. Linear: count instances in all, spacing apart along direction.",
            new Pattern("row", ["hole"], new LinearMotion([1, 0, 0], 20, 3)), ["hole/wall", "row/1:hole/wall", "row/2:hole/wall"], [Bore]),
        new("Circular pattern: count instances in all round an axis; angle (default 360) puts the last one there when below 360.",
            new Pattern("ring", ["pin"], new CircularMotion([50, 25, 0], [0, 0, 1], 4)), ["pin/wall", "ring/1:pin/wall", "ring/3:pin/wall"],
            [new Hole("pin", [40, 25], 6)]),
        new("Mirror: one mirrored copy in the plane through point with normal.",
            new Pattern("mirror", ["hole"], new MirrorMotion([50, 0, 0], [1, 0, 0])), ["hole/wall", "mirror/1:hole/wall"], [Bore]),
        new("Joins (op join) or cuts (op cut) body tool into the current body or the one named by body. The tool is used " +
            "up unless keep is true; its faces keep their names.",
            new CombineBodies("merge", "tab", BooleanOp.Join) { Body = "plate" }, ["tab/z+", "plate/z+"],
            [new Block("tab", BooleanOp.New, [40, 20, 5], [20, 10, 10])]),
        new("Fills the body with a periodic lattice of period cell (mm) on the world grid: kind gyroid, schwarz-p or diamond " +
            "(sheets at least thickness thick, face <id>/surface) or bcc, octet or cubic (struts of diameter thickness, face " +
            "<id>/struts). skin > 0 keeps a closed wall that thick, faces <id>/inner:<face> as a shell's, with the lattice joined " +
            "inside. Optional region, a box {origin, size} or the id of an earlier block, extrude or revolve, limits it; the body " +
            "stays solid outside, faces <id>/region:<face>. Fillets, chamfers, shells and offsets go before it; STEP export " +
            "names the lattice face unsupported. Walls and gaps under 2 × cellSize are below-resolution.",
            new Lattice("infill", "bcc", 10, 2, Region: new LatticeRegion(null, [60, 10, -1], [30, 30, 12])),
            ["infill/struts", "infill/region:x-", "infill/region:y+", "plate/z+"], []),
    ];

    /// <summary>Edits shown in the guide, valid on <see cref="EditTarget"/>.</summary>
    public static IReadOnlyList<string> EditExamples { get; } =
    [
        """{"set": "parameters.width", "value": 120}""",
        """{"set": "hole.diameter", "value": "=width / 8"}""",
        """{"set": "hole.diameter", "value": 12}""",
        """{"set": "rib.profile.edges.1.to", "value": [40, 20]}""",
        """{"set": "row.linear.count", "value": 4}""",
        """{"insert": {"id": "edge", "type": "fillet", "edge": ["plate/z+", "plate/x-"], "radius": 2}, "before": "row"}""",
        """{"remove": "rib"}""",
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
    public static PartDocument EditTarget { get; } = new([Plate, Bore, Rib, new Pattern("row", ["hole"], new LinearMotion([1, 0, 0], 20, 3))])
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

    /// <summary>The guide as plain text, built once.</summary>
    public static string Text => _text.Value;

    static readonly Lazy<string> _text = new(Build);

    static string Build()
    {
        var s = new StringBuilder();
        s.AppendLine("# Isoform document format (" + PartDocument.FormatVersion + ")");
        s.AppendLine();
        s.AppendLine("A part is JSON: " + Compact(CanonicalFormat.Write(new PartDocument([Plate, Bore]))));
        s.AppendLine("Units mm, Z up. Features apply in order; the first after any sketches must start a body (op new). Ids are unique, non-empty, without '/'.");
        s.AppendLine("Ids are stable: never derive them from position. Numbers are plain JSON numbers; points [x,y,z], 2D points [u,v].");
        s.AppendLine("Tools return the canonical text (fixed key order, optional keys only when used) and its SHA-256 hash.");
        s.AppendLine();
        s.AppendLine("## Feature types");
        s.AppendLine("Examples sit on the plate above; listed faces are ones the example really produces.");
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
        s.AppendLine("Regeneration solves every sketch: sketch-conflict names constraints that cannot all hold, sketch-redundant one the others");
        s.AppendLine("imply; a sketch that is not fully constrained keeps its free points where they are stored. Lines and arcs join where");
        s.AppendLine("their ends meet; open chains are ignored; a point joining three or more edges, or loops that cross, are sketch-invalid.");
        s.AppendLine("sketch_edit fillets a corner point where two lines or arcs meet (a tangent arc; the corner stays, as a construction");
        s.AppendLine("point) or trims the piece of an entity between its crossings nearest [x, y]; constraints on a deleted piece are dropped and listed.");
        s.AppendLine("sketch_edit project copies a part edge [faceA, faceB] in (no ellipses); linked: construction with project: [faceA, faceB],");
        s.AppendLine("held on the edge each regeneration, else sketch-reference-lost and it stays put.");
        s.AppendLine();
        s.AppendLine("## Face names");
        s.AppendLine("Every face is named by the feature that made it: <feature id>/<face>. Names survive parameter edits, so refer to");
        s.AppendLine("faces by name (fillet edges, loops, mates), never by index. A face that two features share (flush faces, a merge)");
        s.AppendLine("lists both names; a source face cut in pieces appears once per piece.");
        s.AppendLine("- block <id>/x- x+ y- y+ z- z+; hole <id>/wall, <id>/floor, <id>/<point>:wall; plane-cut and single-edge fillet/chamfer <id>/face");
        s.AppendLine("- extrude/revolve <id>/<profile edge id> or <id>/<sketch entity id>, <id>/start, <id>/end; blend <id>/blend");
        s.AppendLine("- loop fillet/chamfer <id>/<face across the edge>, e.g. round/rib/a; shell <id>/inner:<outer face>");
        s.AppendLine("- pattern copy k: <id>/<k>:<source face>, e.g. row/2:hole/wall; offset corners <id>/corner:<face>+<face>");
        s.AppendLine("- lattice <id>/surface (sheets) or <id>/struts, one face however many pieces; skin <id>/inner:<face>; region <id>/region:<face>");
        s.AppendLine("- in an assembly: <component id>/<face>, sub-assemblies first, e.g. left/plate/z+");
        s.AppendLine();
        s.AppendLine("## Bodies");
        s.AppendLine("A feature with op new starts a body named by its body key, else its id. Any feature may name an existing body in");
        s.AppendLine("\"body\" to work on it (it becomes current); otherwise it works on the current body. Fillets and chamfers find");
        s.AppendLine("their body from their faces. Bodies stay separate solids until a combine joins or cuts them.");
        s.AppendLine();
        s.AppendLine("## Parameters");
        s.AppendLine("\"parameters\" (after units) names values: a number, or \"=expression\" over other parameters. Any number in a feature");
        s.AppendLine("may be \"=expression\" too. Operators + - * / ^, parentheses, and " + string.Join(", ", Expression.Reserved) + " (angles");
        s.AppendLine("in degrees; a count must come out whole). Ids and face names never change with parameters. Example:");
        s.AppendLine(Compact(CanonicalFormat.Write(EditTarget)));
        s.AppendLine();
        s.AppendLine("## Edits (apply_edits)");
        s.AppendLine("set takes a dotted path into a feature, starting with its id; array indices are numbers; the value may be an");
        s.AppendLine("\"=expression\". parameters.<name> sets (or removes) a parameter. insert adds a feature before an id (appended");
        s.AppendLine("without before). remove deletes a feature by id. On the document above these are valid edits:");
        foreach (var e in EditExamples) s.AppendLine("- " + e);
        s.AppendLine("An assembly takes the same edits on components.<id>.<field> and mates.<id>.<field>; insert adds a component (it");
        s.AppendLine("has a part) or a mate (it has a kind); remove takes components.<id> or mates.<id>. In a bundle an edit changes the");
        s.AppendLine("assembly, or with \"document\": \"<path>\" that part; set \"\" with a document adds or replaces it whole, remove \"\"");
        s.AppendLine("drops it. On the bundle under Assemblies these are valid:");
        foreach (var e in AssemblyEditExamples) s.AppendLine("- " + e);
        s.AppendLine("Edits apply all or nothing: one that cannot be applied is an error naming it (edit 2 {...}: why).");
        s.AppendLine();
        s.AppendLine("## Assemblies (assembly tool)");
        s.AppendLine($"An assembly ({AssemblyDocument.FormatVersion}) places parts by path; placement is origin, x and normal (z) of the part's frame.");
        s.AppendLine("hash (optional) pins the stored part's content hash; grounded components stay put (else the first); parameters");
        s.AppendLine("overrides the part's own (one document, several variants). Mate kinds: mate (flat faces against each other, gap");
        s.AppendLine("offset), flush (flat faces facing the same way), axis (cylinders or cones coaxial).");
        s.AppendLine(Compact(CanonicalFormat.Write(AssemblyExample)));
        s.AppendLine($"A bundle ({Bundle.FormatVersion}) carries the assembly and its documents in one file:");
        s.AppendLine($"{{\"format\":\"{Bundle.FormatVersion}\",\"units\":\"mm\",\"assembly\":{{...}},\"documents\":{{\"bracket.json\":{{part}},\"pin.json\":{{part}}}}}}");
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
