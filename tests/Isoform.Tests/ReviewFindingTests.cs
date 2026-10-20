using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Geometry;
using Isoform.Meshing;
using Isoform.Modeling;

namespace Isoform.Tests;

/// <summary>Regressions for the findings of the K4 code review, one test each, in its order.</summary>
public class ReviewFindingTests
{
    static readonly FrepModeler Modeler = new(new ModelingSettings { CellSize = 0.25 });
    static readonly SketchPlane Xy = new([0, 0, 0], [1, 0, 0], [0, 0, 1]);
    static readonly Block Plate = new("plate", BooleanOp.New, [0, 0, 0], [100, 50, 10]);

    static Profile Rectangle(double x0, double y0, double x1, double y1) => new([x0, y0],
        [new ProfileEdge("s", [x1, y0]), new ProfileEdge("e", [x1, y1]), new ProfileEdge("n", [x0, y1]), new ProfileEdge("w", [x0, y0])]);

    static Extrude Box => new("box", BooleanOp.New, Xy, Rectangle(0, 0, 40, 20), 0, 10);

    static Profile Stadium => new([40, 20],
        [new ProfileEdge("a", [60, 20]), new ProfileEdge("right", [60, 30], 1), new ProfileEdge("b", [40, 30]), new ProfileEdge("left", [40, 20], 1)]);

    static IReadOnlyList<string> Codes(PartDocument doc) => Modeler.Regenerate(doc).Diagnostics.Select(d => d.Code).ToList();

    [Fact]
    public void Corner_round_after_an_offset_is_the_round_of_the_grown_profile()
    {
        var doc = new PartDocument([Box, new Offset("grow", 1), new Fillet("round", ["box/s", "box/e"], 3)]);
        var result = Modeler.Regenerate(doc);

        result.Diagnostics.Should().BeEmpty();
        FieldProperties.Check(result.Part);
        // Grown rectangle (A + P·d + πd²), one radius-1 corner replaced by a radius-3 one, between caps 12 apart.
        var area = 800 + 120 * 1 + Math.PI - (9 - Math.PI * 9 / 4) + (1 - Math.PI / 4);
        SignedVolume(result.Mesh!).Should().BeApproximately(area * 12, area * 12 * 1e-4);
    }

    [Fact]
    public void Rounds_after_a_shell_that_cut_through_the_wall_are_rejected()
    {
        Modeler.Regenerate(new PartDocument([Box, new Shell("shell", 1), new Fillet("round", ["box/s", "box/e"], 5)]))
            .Diagnostics.Should().ContainSingle().Which.Message.Should().Contain("cuts through the shell wall");
        Modeler.Regenerate(new PartDocument([Box, new Shell("shell", 1), new Fillet("round", [], 5, Loop: "box/end")]))
            .Diagnostics.Should().ContainSingle().Which.Message.Should().Contain("cuts through the shell wall");
    }

    [Fact]
    public void An_offset_after_a_rim_loop_grows_the_round_and_keeps_its_names()
    {
        var doc = new PartDocument([Plate, new Extrude("slot", BooleanOp.Cut, Xy, Stadium, -1, 11), new Fillet("round", [], 1, Loop: "plate/z+"), new Offset("grow", 0.5)]);
        var result = Modeler.Regenerate(doc);

        result.Diagnostics.Should().BeEmpty();
        FieldProperties.Check(result.Part);
        result.Topology!.Faces.SelectMany(f => f.Sources).Should().Contain(["round/slot/a", "round/slot/right", "round/slot/b", "round/slot/left"]);
    }

    [Fact]
    public void A_loop_rounds_block_window_rims_and_says_what_it_cannot_round()
    {
        var window = Modeler.Regenerate(new PartDocument([Plate, new Block("window", BooleanOp.Cut, [30, 15, -1], [40, 20, 12]), new Fillet("round", [], 1, Loop: "plate/z+")]));
        window.Diagnostics.Should().BeEmpty();
        window.Topology!.Faces.SelectMany(f => f.Sources).Should().Contain(["round/window/x-", "round/window/y+"]);

        var bore = new Revolve("bore", BooleanOp.Cut, new SketchPlane([50, 25, 0], [1, 0, 0], [0, -1, 0]),
            new Profile([0, -1], [new ProfileEdge("bottom", [5, -1]), new ProfileEdge("wall", [5, 11]), new ProfileEdge("top", [0, 11]), new ProfileEdge("axis", [0, -1])]));
        Codes(new PartDocument([Plate, bore, new Fillet("round", [], 1, Loop: "plate/z+")])).Should().Equal(Diagnostic.UnsupportedFillet);

        var looped = Modeler.Regenerate(new PartDocument([Plate, new Extrude("pocket", BooleanOp.Cut, Xy, Stadium, 4, 11),
            new Fillet("floor", [], 1, Loop: "pocket/start"), new Fillet("rim", [], 1, Loop: "plate/z+")]));
        looped.Diagnostics.Should().BeEmpty();
        looped.Topology!.Faces.SelectMany(f => f.Sources).Should().Contain("rim/pocket/right");
    }

    [Fact]
    public void A_body_cannot_be_combined_with_itself()
    {
        var post = new Block("post", BooleanOp.New, [40, 15, 5], [20, 20, 30]);
        Codes(new PartDocument([Plate, post, new CombineBodies("weld", "post", BooleanOp.Join)])).Should().Equal(Diagnostic.InvalidParameter);
        Codes(new PartDocument([Plate, post, new CombineBodies("weld", "post", BooleanOp.Join) { Body = "post" }])).Should().Equal(Diagnostic.InvalidParameter);
    }

    [Fact]
    public void Patterns_of_features_in_a_used_up_body_go_where_it_went()
    {
        var doc = new PartDocument(
        [
            Plate,
            new Block("post", BooleanOp.New, [40, 15, 5], [20, 20, 30]),
            new Hole("h", [45, 25], 4),
            new CombineBodies("weld", "post", BooleanOp.Join) { Body = "plate" },
            new Pattern("row", ["h"], new LinearMotion([1, 0, 0], 8, 2)),
        ]);

        var result = Modeler.Regenerate(doc);

        result.Diagnostics.Should().BeEmpty();
        result.Topology!.Faces.SelectMany(f => f.Sources).Should().Contain("row/1:h/wall");
    }

    [Fact]
    public void A_body_is_shelled_once()
    {
        var result = Modeler.Regenerate(new PartDocument([Plate, new Shell("outer", 2), new Shell("inner", 1)]));

        result.Diagnostics.Should().ContainSingle().Which.Message.Should().Contain("already shelled");
    }

    [Fact]
    public void Points_on_a_revolve_axis_have_the_right_sign()
    {
        var shaft = new Revolve("shaft", BooleanOp.New, new SketchPlane([0, 0, 0], [1, 0, 0], [0, -1, 0]), new Profile([0, 0],
            [new ProfileEdge("bottom", [10, 0]), new ProfileEdge("big", [10, 20]), new ProfileEdge("top", [0, 20]), new ProfileEdge("axis", [0, 0])]));
        var field = new Compiler(new ModelingSettings()).Compile(new PartDocument([shaft])).Field!;

        field.Value(new Point3D(0, 0, 3)).Should().BeApproximately(-3, 1e-12);
        field.Value(new Point3D(1e-9, 0, 3)).Should().BeApproximately(-3, 1e-9);
        field.Value(new Point3D(0, 0, 25)).Should().BeApproximately(5, 1e-12);

        var grown = new Compiler(new ModelingSettings()).Compile(new PartDocument([shaft, new Offset("grow", 1)]));
        grown.Surfaces.Should().NotContain(s => s.Source.Contains("axis"), "a corner on the axis is no corner");
    }

    [Fact]
    public void Loop_faces_never_borrow_another_face_s_name()
    {
        var looped = new PartDocument([Box, new Fillet("top", [], 1, Loop: "box/end"), new Fillet("corner", ["box/s", "box/e"], 4)]);
        Codes(looped).Should().Equal(Diagnostic.UnsupportedFillet);

        var grown = Modeler.Regenerate(new PartDocument([Box, new Fillet("top", [], 1, Loop: "box/end"), new Offset("grow", 1)]));
        grown.Diagnostics.Should().BeEmpty();
        grown.Topology!.Faces.SelectMany(f => f.Sources).Should().Contain("top/grow/corner:box/s+box/e");
    }

    [Fact]
    public void A_pattern_leaves_the_current_body_alone()
    {
        var doc = new PartDocument(
        [
            Plate,
            new Hole("h", [10, 25], 4),
            new Block("post", BooleanOp.New, [40, 15, 5], [20, 20, 30]),
            new Pattern("row", ["h"], new LinearMotion([1, 0, 0], 8, 2)),
            new Hole("h2", [50, 25], 4),
        ]);

        var result = Modeler.Regenerate(doc);

        result.Diagnostics.Should().BeEmpty();
        result.Bodies.Single(b => b.Name == "post").Faces.SelectMany(f => result.Topology!.Faces[f].Sources).Should().Contain("h2/wall");
    }

    [Fact]
    public void Feature_ids_are_unique_and_slash_free()
    {
        Codes(new PartDocument([Plate, new Hole("h", [10, 25], 4), new Hole("h", [30, 25], 4)])).Should().Equal(Diagnostic.InvalidParameter);
        Codes(new PartDocument([Plate, new Hole("a/b", [10, 25], 4)])).Should().Equal(Diagnostic.InvalidParameter);
    }

    [Fact]
    public void A_shelled_torus_reports_the_inner_tube_s_curvature()
    {
        var torus = new LeafField(new Surface(0, "ring", SurfaceKind.Torus, new Point3D(0, 0, 0), new Vector3D(0, 0, 1), 20, 5));
        var shell = new ShellField(torus, 1, new Dictionary<int, int> { [0] = 1 });
        // A box on the inner surface (tube radius 4) far from the outer one.
        var box = new Box3D(new Point3D(23.9, -0.1, -0.1), new Point3D(24.1, 0.1, 0.1));

        shell.SinglePatch(box).Should().BeLessThanOrEqualTo(4 + 1e-9);
    }

    [Fact]
    public void Inner_faces_of_a_shelled_pocket_lie_on_the_material_side()
    {
        var part = new Compiler(new ModelingSettings()).Compile(new PartDocument([Plate, new Block("pocket", BooleanOp.Cut, [30, 15, 4], [40, 20, 10]), new Shell("shell", 1)]));

        part.Surfaces.Single(s => s.Source == "shell/inner:pocket/x+").Origin.X.Should().BeApproximately(71, 1e-12);
        part.Surfaces.Single(s => s.Source == "shell/inner:pocket/z-").Origin.Z.Should().BeApproximately(3, 1e-12);
    }

    [Fact]
    public void Opposite_offsets_leave_no_negative_corners()
    {
        var part = new Compiler(new ModelingSettings()).Compile(new PartDocument([Box, new Offset("grow", 2), new Offset("shrink", -3)]));

        part.Diagnostics.Should().BeEmpty();
        FieldAlgebra.Descendants(part.Field!).OfType<ProfileField>().SelectMany(p => p.Leaves).Should().OnlyContain(l => l.Surface.Kind != SurfaceKind.Cylinder || l.Surface.Radius > 0);
    }

    [Fact]
    public void Loops_report_only_real_problems()
    {
        // A slot cut up from below that stops short of the top: the top's loop just has no rim there.
        Codes(new PartDocument([Plate, new Extrude("slot", BooleanOp.Cut, Xy, Stadium, -1, 6), new Fillet("round", [], 1, Loop: "plate/z+")])).Should().BeEmpty();
        // Loops on both ends of one extrude.
        Codes(new PartDocument([Box, new Fillet("top", [], 1, Loop: "box/end"), new Fillet("bottom", [], 1, Loop: "box/start")])).Should().BeEmpty();
    }

    static double SignedVolume(Mesh mesh)
    {
        var v = mesh.Vertices;
        return mesh.Triangles().Sum(t => v[t.A].AsVector().Dot(v[t.B].AsVector().Cross(v[t.C].AsVector()))) / 6;
    }
}
