using Isoform.Document;
using Isoform.Meshing;
using Isoform.Modeling;

namespace Isoform.Tests;

/// <summary>
/// Minimized failures from the K7 robustness runs, each asserting what the kernel promises: the right answer, or a
/// below-resolution report. Skipped until fixed; the expected values are exact (or OCCT's, where noted, which agreed
/// with the certified volume).
/// </summary>
public class RobustnessReproTests
{
    const string Open = "K7 finding, not fixed yet";
    static readonly ModelingSettings Settings = new();

    static FrepResult Regenerate(string features) => new FrepModeler(Settings).Regenerate(RobustnessDocuments.Parse(features));

    static bool Reported(FrepResult r) => r.Diagnostics.Any(d => d.Code == Diagnostic.BelowResolution);

    static (int, int, int, int) Counts(FrepResult r) => (r.Topology!.Faces.Count, r.Topology.Edges.Count, r.Topology.Vertices.Count, r.Topology.BodyCount);

    /// <summary>
    /// A plane cut leaving a 25° wedge along the bottom edge: the topology is right but the mesh loses about 0.2 mm² of
    /// section per mm of edge (1% here), with nothing reported. Every wedge from about 14° to 25° does this at 0.5 mm cells;
    /// 30° and up are exact, 10° is reported. The thin-spot test in DualContouring.FindThin skips surfaces that meet
    /// within four cells, assuming the cell vertices then reproduce the edge; below ~30° they did not, as a vertex could
    /// stray only half a cell. A vertex on a real edge may now go up to those four cells.
    /// </summary>
    [Fact]
    public void Acute_wedge_keeps_its_volume()
    {
        var a = 25 * Math.PI / 180;
        var r = new FrepModeler(Settings).Regenerate(new PartDocument(
        [
            new Block("plate", BooleanOp.New, [0, 0, 0], [10, 20, 4]),
            new PlaneCut("cut", [0, 10, 0], [0, Math.Sin(a), Math.Cos(a)]),
        ]));
        var exact = 10 * 4 * (10 - 2 / Math.Tan(a));

        var volume = MassProperties.Of(r.Part.Field!, r.Mesh!).Volume;

        (Reported(r) || Math.Abs(volume - exact) < 1e-6 * exact).Should().BeTrue($"volume {volume} vs {exact}");
    }

    /// <summary>
    /// The same along an edge oblique to the grid: the wedge's tip runs along the plate's diagonal into two corners. Up
    /// to 40° this lost up to 5% silently; the tip now meets the side faces in corners, and a side face too thin for one
    /// vertex is reported.
    /// </summary>
    [Theory]
    [InlineData(10)]
    [InlineData(14)]
    [InlineData(20)]
    [InlineData(30)]
    [InlineData(40)]
    public void Oblique_acute_wedge_keeps_its_volume(double degrees)
    {
        var a = degrees * Math.PI / 180;
        var r = new FrepModeler(Settings).Regenerate(new PartDocument(
        [
            new Block("plate", BooleanOp.New, [0, 0, 0], [20, 20, 1]),
            new PlaneCut("cut", [10, 10, 0], [Math.Sqrt(0.5) * Math.Sin(a), -Math.Sqrt(0.5) * Math.Sin(a), Math.Cos(a)]),
        ]));
        // Kept: z ≤ d·tan a over the half of the square at distance d from the diagonal, where chords are 20√2 − 2d long.
        var d0 = 1 / Math.Tan(a);
        var exact = 200 - 10 * Math.Sqrt(2) * d0 + d0 * d0 / 3;

        var volume = MassProperties.Of(r.Part.Field!, r.Mesh!).Volume;

        (Reported(r) || Math.Abs(volume - exact) < 1e-6 * exact).Should().BeTrue($"volume {volume} vs {exact}");
    }

    /// <summary>
    /// A hole that clips the plate's corner by 1e-6 mm (ten times the tolerance) is called no-effect: the face and the
    /// sliver of material it removes are neither built nor reported. OCCT builds the 7-face part.
    /// </summary>
    [Fact]
    public void Hole_clipping_a_corner_is_built_or_reported()
    {
        var r = Regenerate("""
            [
              {"id":"plate","type":"block","op":"new","origin":[0,0,0],"size":[10,29,10]},
              {"id":"hole1","type":"hole","center":[10,31.999999],"diameter":6}
            ]
            """);

        (Reported(r) || Counts(r) == (7, 15, 10, 1)).Should().BeTrue();
        r.Diagnostics.Should().NotContain(d => d.Code == Diagnostic.NoEffect, "the hole does reach into the part");
    }

    /// <summary>
    /// An oblique plane cut through a hole wall meets it in a thin wedge: the mesh gets non-manifold edges (four quads
    /// on one edge) near (19.4, 10.5, 1) at every cell size, while the topology is right. One vertex per cell could not
    /// separate the two sheets of the wedge; a cell cut into several pieces now gets a vertex per piece.
    /// </summary>
    [Fact]
    public void Oblique_cut_through_a_hole_meshes_watertight()
    {
        var r = Regenerate("""
            [
              {"id":"plate","type":"block","op":"new","origin":[0,0,0],"size":[30,30,2]},
              {"id":"hole1","type":"hole","center":[22,12],"diameter":6},
              {"id":"slice2","type":"plane-cut","point":[33,0,0],"normal":[-0.3,-0.451,0.5]}
            ]
            """);

        (Reported(r) || r.Mesh!.IsWatertight()).Should().BeTrue();
    }

    /// <summary>
    /// A plane cut leaves plate/x+ as a 0.1 mm² triangle; at 0.5 mm cells it disappears (5 faces), at 0.125 mm it is there
    /// (6 faces, as in OCCT). A face smaller than the cells is neither kept nor reported.
    /// </summary>
    [Fact]
    public void Small_corner_face_is_kept_or_reported()
    {
        var r = Regenerate("""
            [
              {"id":"plate","type":"block","op":"new","origin":[0,0,0],"size":[38,20,3]},
              {"id":"slice1","type":"plane-cut","point":[0,15,0.5],"normal":[0.16,0.35,-0.4]}
            ]
            """);

        (Reported(r) || Counts(r) == (6, 12, 8, 1)).Should().BeTrue();
    }

    /// <summary>A block 1e-6 mm off the plate's side is fused into it: one body instead of two, nothing reported.</summary>
    [Fact]
    public void Gap_of_a_micron_keeps_two_bodies_or_is_reported()
    {
        var r = Regenerate("""
            [
              {"id":"plate","type":"block","op":"new","origin":[0,0,0],"size":[30,36.5,2]},
              {"id":"boss1","type":"block","op":"join","origin":[10,36.500001,0],"size":[4,2,2]}
            ]
            """);

        (Reported(r) || Counts(r) == (12, 24, 16, 2)).Should().BeTrue();
    }

    /// <summary>
    /// A block on the plate shifted 1e-6 mm along x: the two 1e-6 mm steps are neither built (OCCT: 10 faces) nor reported,
    /// and the side faces are not merged either (8 faces).
    /// </summary>
    [Fact]
    public void Step_of_a_micron_is_built_or_reported()
    {
        var r = Regenerate("""
            [
              {"id":"plate","type":"block","op":"new","origin":[0,0,0],"size":[20,20,11.5]},
              {"id":"boss1","type":"block","op":"join","origin":[-1E-06,0,10],"size":[20,20,10]}
            ]
            """);

        (Reported(r) || Counts(r) == (10, 24, 16, 1)).Should().BeTrue();
    }

    /// <summary>
    /// A cut reaching 1e-9 mm into the side face (below the 1e-7 tolerance) splits plate/y- in two and adds a cut1/y+
    /// face: 8 faces where the part is the plain 6-face block with a coincident face, as OCCT builds it.
    /// </summary>
    [Fact]
    public void Cut_within_tolerance_of_a_face_adds_no_faces()
    {
        var r = Regenerate("""
            [
              {"id":"plate","type":"block","op":"new","origin":[0,0,0],"size":[40,20,4.473]},
              {"id":"cut1","type":"block","op":"cut","origin":[10,-13.999999999,0],"size":[20,14,10]}
            ]
            """);

        Counts(r).Should().Be((6, 12, 8, 1));
    }

    /// <summary>
    /// Two fillets of radius 2 on a 3.5 mm plate, on the bottom front and the top left edge: the second was refused as
    /// not fitting, because within 0.064 mm of y = 0 its ball touches plate/x- where the first round has already eaten
    /// that face's end. The rounds meet in a mitre there; OCCT builds that same solid (8 faces, 1365.66377182). The ball
    /// must still fit on the faces as they were: two rounds on one 3.5 mm face stay refused, as in OCCT.
    /// </summary>
    [Fact]
    public void Fillets_on_edges_round_one_corner_both_fit()
    {
        static string Plate(string first) => """
            [
              {"id":"plate","type":"block","op":"new","origin":[0,0,0],"size":[20,20,3.5]},
              {"id":"round1","type":"fillet","edge":["plate/FIRST","plate/z-"],"radius":2},
              {"id":"round2","type":"fillet","edge":["plate/x-","plate/z+"],"radius":2}
            ]
            """.Replace("FIRST", first);

        var r = Regenerate(Plate("y-"));
        r.Diagnostics.Should().BeEmpty();
        Counts(r).Should().Be((8, 18, 12, 1));
        // Each round takes (4 − π) mm² of section over 20 mm; near the corner both take the same material, which the
        // integral of (2 − √(4 − (2 − z)²))·(2 − √(4 − (z − 1.5)²)) over z in [1.5, 2] gives: 6.5694e-5 mm³.
        var exact = 20 * 20 * 3.5 - 2 * 20 * (4 - Math.PI) + 6.5694e-5;
        var bounds = MassProperties.VolumeBounds(r.Part.Field!, r.Part.Min, r.Part.Max, 0.1);
        exact.Should().BeInRange(bounds.Lo, bounds.Hi);
        MassProperties.Of(r.Part.Field!, r.Mesh!).Volume.Should().BeApproximately(exact, 1e-5 * exact);

        Regenerate(Plate("x-")).Diagnostics.Should().ContainSingle(d => d.Code == Diagnostic.FilletInvalid && d.FeatureId == "round2");
    }

    /// <summary>
    /// A plane cut whose trace on the top face misses it except at the corner (0, 0, 11): top and cut face share only that
    /// vertex, but the topology gets an edge between them (15 edges, 10 vertices; OCCT 14 and 9).
    /// </summary>
    [Fact]
    public void Cut_touching_a_face_at_a_vertex_adds_no_edge()
    {
        var r = Regenerate("""
            [
              {"id":"plate","type":"block","op":"new","origin":[0,0,0],"size":[40,30,11]},
              {"id":"slice1","type":"plane-cut","point":[0,0,11],"normal":[-0.1,-1,-1]}
            ]
            """);

        Counts(r).Should().Be((7, 14, 9, 1));
    }

    /// <summary>A loose 1e-6 × 1e-4 mm block beside the plate is dropped and called no-effect instead of reported.</summary>
    [Fact]
    public void Loose_sliver_body_is_reported_not_called_no_effect()
    {
        var r = Regenerate("""
            [
              {"id":"plate","type":"block","op":"new","origin":[0,0,0],"size":[40,16,10]},
              {"id":"boss1","type":"block","op":"join","origin":[0,20,0],"size":[1E-06,1E-04,4]}
            ]
            """);

        (Reported(r) || r.Topology!.BodyCount == 2).Should().BeTrue();
        r.Diagnostics.Should().NotContain(d => d.Code == Diagnostic.NoEffect);
    }

    /// <summary>
    /// A boss overhanging the plate's side by 0.03 mm: the 0.03 mm strip of its bottom face is missing (10 faces, OCCT 11)
    /// and nothing is reported, at 0.5 and at 0.125 mm cells alike.
    /// </summary>
    [Fact]
    public void Overhang_below_the_cells_is_built_or_reported()
    {
        var r = Regenerate("""
            [
              {"id":"plate","type":"block","op":"new","origin":[0,0.63,0],"size":[20,10,10]},
              {"id":"boss1","type":"block","op":"join","origin":[10,0.6,10],"size":[10,4,5]}
            ]
            """);

        (Reported(r) || Counts(r) == (11, 26, 17, 1)).Should().BeTrue();
    }

    /// <summary>A cut whose top is flush with the plate bottom splits that face in two (cut2/z+ beside plate/z-): 7 faces for a plain block.</summary>
    [Fact]
    public void Flush_cut_leaves_one_bottom_face()
    {
        var r = Regenerate("""
            [
              {"id":"plate","type":"block","op":"new","origin":[0.25,0,0.31],"size":[40,10,5]},
              {"id":"cut2","type":"block","op":"cut","origin":[10,0,-0.7],"size":[32,20,1.01]}
            ]
            """);

        Counts(r).Should().Be((6, 12, 8, 1));
    }

    /// <summary>
    /// F-rep only, the one crash in 3000 documents: a four-edge profile with two concave arcs, joined on the plate top,
    /// threw from DualContouring.Refine. The arcs into (8, 6.258) cut the corner's 68.6° by 70.4°: they cross 0.04 mm
    /// from it, inside the first of the 32 pieces the crossing test sees, and the field's corner sign was wrong there.
    /// The profile is now refused; with the second arc a little flatter the corner is open and the part meshes.
    /// </summary>
    [Fact]
    public void Profile_with_concave_arcs_does_not_crash()
    {
        static string Knob(string bulge) => """
            [
              {"id":"plate","type":"block","op":"new","origin":[0,0,0],"size":[20,20,14]},
              {"id":"knob1","type":"extrude","op":"join","plane":{"origin":[0,0,14],"x":[1,0,0],"normal":[0,0,1]},"profile":{"start":[8.358,7.732],"edges":[{"id":"e0","to":[7,10]},{"id":"e1","to":[6.518,7.3]},{"id":"e2","to":[8,6.258],"bulge":-0.238},{"id":"e3","to":[8.358,7.732],"bulge":BULGE}]},"start":0,"end":2}
            ]
            """.Replace("BULGE", bulge);

        var crossed = Regenerate(Knob("-0.4"));
        crossed.Diagnostics.Should().ContainSingle(d => d.Code == Diagnostic.InvalidParameter && d.Message.Contains("e2 and e3 cross"));

        var valid = Regenerate(Knob("-0.3"));
        valid.Diagnostics.Should().BeEmpty();
        valid.Mesh!.IsWatertight().Should().BeTrue();
        FieldProperties.Check(valid.Part);
    }

    /// <summary>
    /// F-rep only: a 1 mm shell of a 2.2 mm plate leaves a 0.2 mm cavity no grid edge crosses. The mesh was the solid
    /// plate (volume 1760 against a certified 1613–1629), the shell called no-effect, nothing reported. The compiler now
    /// reports a cavity under 2 cells; on a 3.2 mm plate the 1.2 mm cavity is resolved and built without a word.
    /// </summary>
    [Fact]
    public void Shell_with_a_cavity_below_the_cells_is_reported()
    {
        static string Plate(string height) => """
            [
              {"id":"plate","type":"block","op":"new","origin":[0,0,0],"size":[40,20,HEIGHT]},
              {"id":"shell2","type":"shell","thickness":1}
            ]
            """.Replace("HEIGHT", height);

        var r = Regenerate(Plate("2.2"));
        r.Diagnostics.Should().ContainSingle(d => d.Code == Diagnostic.BelowResolution && d.FeatureId == "shell2");

        var resolved = Regenerate(Plate("3.2"));
        resolved.Diagnostics.Should().BeEmpty();
        resolved.Topology!.Faces.Should().HaveCount(12);
    }

    /// <summary>F-rep only: a half revolve with a D-shaped boss joined on its top: cells on the axis hold two sheets, which shared edges.</summary>
    [Fact]
    public void Boss_on_a_half_revolve_meshes_watertight()
    {
        var r = Regenerate("""
            [
              {"id":"body","type":"revolve","op":"new","plane":{"origin":[0,0,0],"x":[1,0,0],"normal":[0,-1,0]},"profile":{"start":[0,0],"edges":[{"id":"base","to":[10,0]},{"id":"side0","to":[10,2.5]},{"id":"step0","to":[5,2.5]},{"id":"top","to":[0,2.5]},{"id":"axis","to":[0,0]}]},"angle":180},
              {"id":"knob1","type":"extrude","op":"join","plane":{"origin":[0,0,2.5],"x":[1,0,0],"normal":[0,0,1]},"profile":{"start":[-10.525,-3.5],"edges":[{"id":"a","to":[0,0],"bulge":1},{"id":"b","to":[-10.525,-3.5]}]},"start":0,"end":10}
            ]
            """);

        (Reported(r) || r.Mesh!.IsWatertight()).Should().BeTrue();
    }

    /// <summary>F-rep only: a blended block joined against the side of a 270° revolve: cells holding two sheets of the blend shared edges.</summary>
    [Fact]
    public void Blended_block_on_a_partial_revolve_meshes_watertight()
    {
        var r = Regenerate("""
            [
              {"id":"body","type":"revolve","op":"new","plane":{"origin":[0,0,0],"x":[1,0,0],"normal":[0,-1,0]},"profile":{"start":[0,0],"edges":[{"id":"base","to":[10,0]},{"id":"side0","to":[10.5,10]},{"id":"step0","to":[6,10]},{"id":"top","to":[0,10]},{"id":"axis","to":[0,0]}]},"angle":270},
              {"id":"boss1","type":"block","op":"join","origin":[-11,-11.5,3],"size":[7,1,5],"blend":2}
            ]
            """);

        (Reported(r) || r.Mesh!.IsWatertight()).Should().BeTrue();
    }

    /// <summary>
    /// A step narrower than a cell between two parallel faces (1e-6, 0.01 and 1e-4 mm): the strip of face between them
    /// was lost and the mesh joined the parallel faces with an edge, one edge, two vertices and one face short of
    /// OCCT, with nothing reported. An edge whose faces provably do not meet near it is now reported.
    /// </summary>
    [Theory]
    [InlineData("""{"id":"boss1","type":"block","op":"join","origin":[-1E-06,0,10],"size":[17,24,10]}""", """[0,0,0],"size":[20,20,12]""", 11, 27, 18)]
    [InlineData("""{"id":"boss1","type":"block","op":"join","origin":[-0.5,10.551,0],"size":[10,1.6389999999999993,2]}""", """[0,0.2,0],"size":[10,12,10]""", 10, 24, 16)]
    [InlineData("""{"id":"boss3","type":"block","op":"join","origin":[0.0001,0,-1],"size":[10,10,10]}""", """[0,0,0],"size":[20,10,10]""", 10, 24, 16)]
    public void Step_narrower_than_a_cell_is_built_or_reported(string boss, string plate, int faces, int edges, int vertices)
    {
        var r = Regenerate($$"""
            [
              {"id":"plate","type":"block","op":"new","origin":{{plate}}},
              {{boss}}
            ]
            """);

        (Reported(r) || Counts(r) == (faces, edges, vertices, 1)).Should().BeTrue($"got {Counts(r)}");
    }

    /// <summary>
    /// A hole wall that would meet the top face 0.07 mm beyond an oblique cut: the cells joined the wall to the top
    /// face, two edges and two vertices more than OCCT and than finer cells, with nothing reported.
    /// </summary>
    [Fact]
    public void Hole_just_clear_of_a_cut_corner_is_built_or_reported()
    {
        var r = Regenerate("""
            [
              {"id":"plate","type":"block","op":"new","origin":[0,0,-1],"size":[30,30,12]},
              {"id":"slice2","type":"plane-cut","point":[4.2,13.8,4.3],"normal":[-0.088,-0.4,0.453]},
              {"id":"hole3","type":"hole","center":[22.5,12.9],"diameter":8.5}
            ]
            """);

        (Reported(r) || Counts(r) == (7, 14, 8, 1)).Should().BeTrue($"got {Counts(r)}");
    }

    /// <summary>
    /// A plane cut that touches the plate only along its corner edge, or only at its corner, removes nothing. The search
    /// for a lost face ran out of budget along the contact (the margin is zero all along it) and reported
    /// below-resolution: the plate's two faces there are nested one level down in its intersection, so the cap for two
    /// planes whose gaps add up to a constant never applied.
    /// </summary>
    [Theory]
    [InlineData(0.7071067811865476, -0.7071067811865476, 0)]
    [InlineData(0.6, -0.8, 0)]
    [InlineData(0.6, -0.7, 0.2)]
    public void Plane_cut_touching_an_edge_has_no_effect(double nx, double ny, double nz)
    {
        // The tab's corner edge (115, 15) lies on the 45° plane too: there the plate's faces are a union away.
        var r = new FrepModeler(Settings).Regenerate(new PartDocument(
        [
            new Block("plate", BooleanOp.New, [0, 0, 0], [100, 50, 10]),
            new Hole("hole", [30, 25], 10),
            new Block("tab", BooleanOp.Join, [95, 15, 0], [20, 20, 6]),
            new PlaneCut("chamfer", [100, 0, 10], [nx, ny, nz]),
        ]));

        r.Diagnostics.Should().ContainSingle().Which.Code.Should().Be(Diagnostic.NoEffect, string.Join("; ", r.Diagnostics.Select(d => d.Message)));
    }

    /// <summary>The bench's 20-operation part has the same chamfer: it touches the plate and tab2 along their corner edges.</summary>
    [Fact]
    public void Bench_chamfer_has_no_effect()
    {
        var r = new FrepModeler(Settings).Regenerate(TapeTests.Twenty);

        r.Diagnostics.Where(d => d.FeatureId == "chamfer").Should().ContainSingle().Which.Code.Should().Be(Diagnostic.NoEffect);
    }

    /// <summary>
    /// Faces that meet across an edge shorter than a cell collapse into one vertex of four faces with no common point:
    /// a boss overlapping the plate by 0.02 mm (the edge between boss1/x- and plate/z- is 0.02 mm long), and two
    /// fillets whose faces just touch at a corner. Both were silent; the first only surfaced once refinement resolved
    /// the boss's 0.5 mm width, whose report had hidden it.
    /// </summary>
    [Theory]
    [InlineData("""[{"id":"plate","type":"block","op":"new","origin":[0,0.8,0],"size":[30,37,10]},{"id":"boss1","type":"block","op":"join","origin":[20,37.779999000000004,-2],"size":[10,0.5,10]}]""", 11, 27, 18)]
    [InlineData("""[{"id":"plate","type":"block","op":"new","origin":[0,0,0],"size":[20,20,3.5]},{"id":"round1","type":"fillet","edge":["plate/y-","plate/z-"],"radius":1.5},{"id":"round2","type":"fillet","edge":["plate/x-","plate/z+"],"radius":2.1}]""", 8, 18, 12)]
    public void Short_edge_collapsed_into_a_vertex_is_built_or_reported(string features, int faces, int edges, int vertices)
    {
        var r = Regenerate(features);

        (Reported(r) || Counts(r) == (faces, edges, vertices, 1)).Should().BeTrue($"got {Counts(r)}");
    }

    /// <summary>
    /// K7, 1000 seeds: the silent results left after local refinement, minimized; OCCT's counts. Two blocks overlapping
    /// by 1e-6 mm leave an edge as long, which the cells collapse into a vertex (654, 1037, 1116, 1133); a hole whose
    /// wall falls apart into two pieces loses one (1081); a face a cut leaves as a small triangle is lost (235); two
    /// blocks 1e-9 mm apart, within the tolerance, touch (390); the tip of a face two cuts run out in a sharp corner
    /// came out as a face of its own (1130). And one the fix for that made: a boss flush with the plate's side, standing
    /// out above and below it, has two faces there that the plate's face between them keeps apart (963).
    /// </summary>
    [Theory]
    [InlineData(654, """[{"id":"plate","type":"block","op":"new","origin":[0,0.78,0],"size":[30,37,10]},{"id":"boss1","type":"block","op":"join","origin":[20,37.779999000000004,-2],"size":[10,0.5,10]}]""", 11, 27, 18, 1)]
    [InlineData(1037, """[{"id":"plate","type":"block","op":"new","origin":[0,-0.46,0],"size":[30,30,10]},{"id":"boss1","type":"block","op":"join","origin":[19,5.5,0],"size":[20,10,2]},{"id":"boss2","type":"block","op":"join","origin":[36,3.000001,0],"size":[10,2.5,2.5]}]""", 15, 39, 26, 1)]
    [InlineData(1116, """[{"id":"plate","type":"block","op":"new","origin":[0,-0.91,0],"size":[50,35.159,3]},{"id":"boss2","type":"block","op":"join","origin":[40,34.248999000000005,0],"size":[3,20,10]}]""", 12, 30, 20, 1)]
    [InlineData(1133, """[{"id":"plate","type":"block","op":"new","origin":[1,-0.7000000000000001,0],"size":[42,31.5,10]},{"id":"boss1","type":"block","op":"join","origin":[40,30.799999,0],"size":[4,10,4]}]""", 11, 27, 18, 1)]
    [InlineData(1081, """[{"id":"plate","type":"block","op":"new","origin":[0,0,0],"size":[30,20,5.5]},{"id":"slice1","type":"plane-cut","point":[23.400000000000002,8.8,4.2],"normal":[0.30000000000000004,-0.725,-0.459]},{"id":"boss3","type":"block","op":"join","origin":[20,0,0],"size":[10,2,4]},{"id":"hole5","type":"hole","center":[20.8,1.859001],"diameter":9.5}]""", 14, 30, 20, 2)]
    [InlineData(235, """[{"id":"plate","type":"block","op":"new","origin":[0,0,0],"size":[35,10,10]},{"id":"slice2","type":"plane-cut","point":[24,10,5.5],"normal":[0.175,-0.2,0.827]},{"id":"boss3","type":"block","op":"join","origin":[34.5,6,0],"size":[10,10,2.5]}]""", 14, 36, 24, 1)]
    [InlineData(390, """[{"id":"plate","type":"block","op":"new","origin":[-0.8,-0.55,-0.8],"size":[30,21.200000000000003,5.5]},{"id":"boss3","type":"block","op":"join","origin":[0,20.650000001,4.669999999],"size":[30,4,1]}]""", 12, 28, 18, 1)]
    [InlineData(1130, """[{"id":"plate","type":"block","op":"new","origin":[0,0,0],"size":[10,30,7]},{"id":"slice1","type":"plane-cut","point":[10.764938591637154,19.400000000000002,3.1664890382282853],"normal":[0.9,0.478,1]},{"id":"slice2","type":"plane-cut","point":[-0.25,0,0.1],"normal":[-0.716,-0.1,0.997]}]""", 8, 18, 12, 1)]
    [InlineData(963, """[{"id":"plate","type":"block","op":"new","origin":[0,0,0],"size":[51,40,15]},{"id":"boss1","type":"block","op":"join","origin":[51,10,-0.2],"size":[2,1,16]}]""", 14, 32, 20, 1)]
    public void Refined_silent_result_is_built_or_reported(int seed, string features, int faces, int edges, int vertices, int bodies)
    {
        var r = Regenerate(features);

        var reported = r.Diagnostics.Any(d => d.Code is Diagnostic.BelowResolution or Diagnostic.TangentContact);
        (reported || Counts(r) == (faces, edges, vertices, bodies)).Should().BeTrue($"seed {seed}: got {Counts(r)}; {string.Join(" | ", r.Diagnostics.Select(d => d.Message))}");
    }

    /// <summary>
    /// K7, seeds 1150–2149 (1547): a hole clipping the plate's corner, 0.007 mm outside it, leaves a separate body of
    /// 0.012 × 0.009 mm there. The cells lose it whole: its faces are all pieces of faces the mesh carries elsewhere,
    /// and the corner is 5.8 mm from every edge the mesh made, where the search for lost pieces looked. It now also
    /// searches the body's box, down to the blocks where three surfaces may bound the part. OCCT builds the second body.
    /// </summary>
    [Fact]
    public void Sliver_body_at_a_clipped_corner_is_built_or_reported()
    {
        var r = Regenerate("""
            [
              {"id":"plate","type":"block","op":"new","origin":[0,0,0],"size":[55.900000000000006,10,10]},
              {"id":"hole3","type":"hole","center":[53,3.834],"diameter":9.600000000000001}
            ]
            """);

        (r.Diagnostics.Any(d => d.Code is Diagnostic.BelowResolution or Diagnostic.TangentContact) || Counts(r) == (12, 24, 16, 2)).Should().BeTrue($"got {Counts(r)}");
    }

    /// <summary>
    /// The same sliver body at the plate's other three corners (all silent before), and its mirror: a pin joined into
    /// an inside corner of the part, 0.007 mm short of it, leaves a through groove of the same section between the pin
    /// and the two walls (the faces the cells lose are pieces of the walls and the pin; 12 faces where the groove is
    /// built).
    /// </summary>
    [Theory]
    [InlineData("""[{"id":"plate","type":"block","op":"new","origin":[0,0,0],"size":[55.9,10,10]},{"id":"hole3","type":"hole","center":[2.9,3.834],"diameter":9.6}]""", 12, 24, 16, 2)]
    [InlineData("""[{"id":"plate","type":"block","op":"new","origin":[0,0,0],"size":[55.9,10,10]},{"id":"hole3","type":"hole","center":[2.9,6.166],"diameter":9.6}]""", 12, 24, 16, 2)]
    [InlineData("""[{"id":"plate","type":"block","op":"new","origin":[0,0,0],"size":[55.9,10,10]},{"id":"hole3","type":"hole","center":[53,6.166],"diameter":9.6}]""", 12, 24, 16, 2)]
    [InlineData("""[{"id":"plate","type":"block","op":"new","origin":[0,0,0],"size":[40,20,10]},{"id":"slot","type":"block","op":"cut","origin":[20,5,-1],"size":[30,30,12]},{"id":"pin","type":"extrude","op":"join","plane":{"origin":[0,0,0],"x":[1,0,0],"normal":[0,0,1]},"profile":{"start":[18.1,8.834],"edges":[{"id":"a","to":[27.7,8.834],"bulge":1},{"id":"b","to":[18.1,8.834],"bulge":1}]},"start":0,"end":10}]""", 12, 30, 20, 1)]
    public void Sliver_at_a_corner_is_built_or_reported(string features, int faces, int edges, int vertices, int bodies)
    {
        var r = Regenerate(features);

        (Reported(r) || Counts(r) == (faces, edges, vertices, bodies)).Should().BeTrue($"got {Counts(r)}");
    }

    /// <summary>
    /// The sliver body again, 0.007 mm outside a small hole (2.4 and 3.4 mm) at the corner, here and at the other
    /// corners: the hole's wall and the plate's faces are carried 1.7–2.5 mm away, within reach of the sliver, so the
    /// search for lost pieces takes it for those faces. No cell corner or edge reaches the sliver, so the cells round it
    /// have no vertex; the mesher looks for the other sign in those (a pocket) and the modeler reports one that no
    /// polygon comes nearer than the cell's corner.
    /// </summary>
    [Theory]
    [InlineData("""[{"id":"plate","type":"block","op":"new","origin":[0,0,0],"size":[55.9,10,10]},{"id":"hole3","type":"hole","center":[55.0465,0.8535],"diameter":2.4}]""")]
    [InlineData("""[{"id":"plate","type":"block","op":"new","origin":[0,0,0],"size":[55.9,10,10]},{"id":"hole3","type":"hole","center":[54.7,1.25],"diameter":3.4}]""")]
    [InlineData("""[{"id":"plate","type":"block","op":"new","origin":[0,0,0],"size":[55.9,10,10]},{"id":"hole3","type":"hole","center":[0.8535,0.8535],"diameter":2.4}]""")]
    [InlineData("""[{"id":"plate","type":"block","op":"new","origin":[0,0,0],"size":[55.9,10,10]},{"id":"hole3","type":"hole","center":[0.8535,9.1465],"diameter":2.4}]""")]
    [InlineData("""[{"id":"plate","type":"block","op":"new","origin":[0,0,0],"size":[55.9,10,10]},{"id":"hole3","type":"hole","center":[55.0465,9.1465],"diameter":2.4}]""")]
    [InlineData("""[{"id":"plate","type":"block","op":"new","origin":[0,0,0],"size":[55.9,10,10]},{"id":"hole3","type":"hole","center":[1.2,8.75],"diameter":3.4}]""")]
    [InlineData("""[{"id":"plate","type":"block","op":"new","origin":[0,0,0],"size":[55.9,10,10]},{"id":"hole3","type":"hole","center":[55.198,0.702],"diameter":1.98}]""")]
    public void Sliver_body_beside_its_own_faces_is_built_or_reported(string features)
    {
        var r = Regenerate(features);

        (Reported(r) || Counts(r) == (12, 24, 16, 2)).Should().BeTrue($"got {Counts(r)}");
    }

    /// <summary>
    /// A cap of a ball 0.007 mm high and 0.53 mm across, cut off by a slab whose top is flush with a shelf's underside:
    /// a separate body bounded by two surfaces, the ball and the slab's top, both carried elsewhere (the slab's top as
    /// the shelf's face). Fewer than three surfaces bound it, so no block of the lost-piece search is looked at there;
    /// the cells round it have no vertex, and the mesher finds it as a pocket. Built, it is two bodies. Also off the
    /// grid's lines and lower.
    /// </summary>
    [Theory]
    [InlineData(30, 10, 14.993)]
    [InlineData(30.17, 9.71, 14.993)]
    [InlineData(29.9, 10.2, 14.996)]
    public void Ball_cap_bounded_by_two_faces_is_built_or_reported(double x, double y, double top)
    {
        var r = Regenerate(FormattableString.Invariant($$"""
            [
              {"id":"plate","type":"block","op":"new","origin":[0,0,0],"size":[60,20,10]},
              {"id":"ball","type":"revolve","op":"join","plane":{"origin":[{{x}},{{y}},10],"x":[1,0,0],"normal":[0,-1,0]},"profile":{"start":[0,-5],"edges":[{"id":"arc","to":[0,5],"bulge":1},{"id":"axis","to":[0,-5]}]},"angle":360},
              {"id":"slab","type":"block","op":"cut","origin":[22,2,10.5],"size":[16,16,{{top - 10.5}}]},
              {"id":"pillar","type":"block","op":"join","origin":[50,0,10],"size":[10,20,6]},
              {"id":"shelf","type":"block","op":"join","origin":[45,0,{{top}}],"size":[5,20,{{16 - top}}]}
            ]
            """));

        (Reported(r) || r.Topology!.BodyCount == 2).Should().BeTrue($"got {Counts(r)}");
    }

    /// <summary>
    /// A fillet run ending at a reflex corner (SweptEdgeTests' riser): the foot's top gains a piece between the fillet
    /// and the end face, which meet tangentially, so it tapers to a cusp. Cells beside it fit no vertex on their
    /// crossings' surfaces and kept their crossings' mean, up to 0.1 mm off the surface with nothing reported; they now
    /// take the real edge of two of their surfaces. At several cell sizes and radii, every vertex is on the surface.
    /// </summary>
    [Theory]
    [InlineData(0.2, 1)]
    [InlineData(0.25, 1)]
    [InlineData(0.3, 1)]
    [InlineData(0.5, 1)]
    [InlineData(0.3, 2)]
    [InlineData(0.25, 1.5)]
    public void Vertices_beside_a_fillet_cusp_are_on_the_surface(double cell, double r)
    {
        var profile = new Profile([0, 0], [new("bottom", [40, 0]), new("tip", [40, 5]), new("inner", [5, 5]), new("riser", [5, 30]), new("top", [0, 30]), new("back", [0, 0])]);
        var result = new FrepModeler(new ModelingSettings { CellSize = cell }).Regenerate(new PartDocument(
        [
            new Extrude("bracket", BooleanOp.New, new SketchPlane([0, 0, 0], [1, 0, 0], [0, 0, 1]), profile, 0, 20),
            new Fillet("round", ["bracket/end", "bracket/riser"], r),
        ]));

        result.Diagnostics.Should().BeEmpty();
        result.Mesh!.Vertices.Max(p => Math.Abs(result.Part.Field!.Value(p))).Should().BeLessThan(1e-7);
    }

    /// <summary>
    /// A pocket with polygons beside it is not lost: where a hole runs out through a plate's side, the wedges of
    /// material between its wall and the side reach into cells whose corners are all in the hole, 0.002–0.06 mm from
    /// the polygons there. Nothing is reported.
    /// </summary>
    [Fact]
    public void Pocket_beside_polygons_is_not_reported()
    {
        var doc = RobustnessDocuments.Parse("""[{"id":"plate","type":"block","op":"new","origin":[0,0,0],"size":[40,20,10]},{"id":"h","type":"hole","center":[20,1.25],"diameter":3.4}]""");
        var body = new Compiler(Settings).Compile(doc).Bodies[0];

        DualContouring.Build(body.Field, body.Min, body.Max, Settings.CellSize, Settings.ChordTolerance).Pockets.Should().NotBeEmpty();
        new FrepModeler(Settings).Regenerate(doc).Diagnostics.Should().BeEmpty();
    }
}