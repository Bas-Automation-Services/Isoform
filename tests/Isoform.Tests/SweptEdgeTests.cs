using System.Text.Json.Nodes;
using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Meshing;
using Isoform.Modeling;
using Xunit.Abstractions;

namespace Isoform.Tests;

/// <summary>
/// K4: single-edge fillets and chamfers on swept edges the profile is not behind, and on the rim of a swept cut. The
/// corner material along the edge is taken away; the edge runs on round tangent neighbours and ends at a corner on the
/// neighbour's face.
/// </summary>
public class SweptEdgeTests(ITestOutputHelper output)
{
    static readonly SketchPlane Xy = new([0, 0, 0], [1, 0, 0], [0, 0, 1]);
    static readonly Block Plate = new("plate", BooleanOp.New, [0, 0, 0], [100, 50, 10]);
    static readonly FrepModeler Modeler = new(new ModelingSettings { CellSize = 0.25 });
    const double VolumeTolerance = 1e-4;

    // The corner a rolling ball of radius r leaves out of a right-angled edge, and its centroid's distance from either face.
    static double Spandrel(double r) => r * r * (1 - Math.PI / 4);
    static double Centroid(double r) => r * (5.0 / 6 - Math.PI / 4) / (1 - Math.PI / 4);

    static Profile Loop(double[] start, params (string Id, double U, double V, double Bulge)[] edges) =>
        new(start, edges.Select(e => new ProfileEdge(e.Id, [e.U, e.V], e.Bulge)).ToList());

    // An L: a 40 × 5 foot and a 5 × 25 riser above it, 20 deep.
    static readonly Profile L = Loop([0, 0], ("bottom", 40, 0, 0), ("tip", 40, 5, 0), ("inner", 5, 5, 0), ("riser", 5, 30, 0), ("top", 0, 30, 0), ("back", 0, 0, 0));

    static PartDocument Riser(double r, Feature? edgeFeature = null) => new(
    [
        new Extrude("bracket", BooleanOp.New, Xy, L, 0, 20),
        edgeFeature ?? new Fillet("round", ["bracket/end", "bracket/riser"], r),
    ]);

    // The riser's top corner already rounded R3 in the profile: riser, bend and top are one tangent run.
    static PartDocument BentRiser(double r) => new(
    [
        new Extrude("bracket", BooleanOp.New, Xy, Loop([0, 0], ("bottom", 40, 0, 0), ("tip", 40, 5, 0), ("inner", 5, 5, 0), ("riser", 5, 27, 0),
            ("bend", 2, 30, Math.Tan(Math.PI / 8)), ("top", 0, 30, 0), ("back", 0, 0, 0)), 0, 20),
        new Fillet("round", ["bracket/end", "bracket/riser"], r),
    ]);

    // A stadium 20 long between two R5 ends, from (40, 20) to (60, 30), cut through the plate.
    static PartDocument SlotRim(double r) => new(
    [
        Plate,
        new Extrude("slot", BooleanOp.Cut, Xy, Loop([40, 20], ("a", 60, 20, 0), ("right", 60, 30, 1), ("b", 40, 30, 0), ("left", 40, 20, 1)), -1, 11),
        new Fillet("round", ["plate/z+", "slot/a"], r),
    ]);

    // A 40 × 20 window through the plate; one rim edge rounded or chamfered.
    static PartDocument WindowRim(double r, bool round = true) => new(
    [
        Plate,
        new Extrude("window", BooleanOp.Cut, Xy, Loop([30, 15], ("s", 70, 15, 0), ("e", 70, 35, 0), ("n", 30, 35, 0), ("w", 30, 15, 0)), -1, 11),
        round ? new Fillet("round", ["plate/z+", "window/s"], r) : new Chamfer("round", ["plate/z+", "window/s"], r),
    ]);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Riser_fillet_takes_away_the_rolling_ball_corner_and_ends_on_the_neighbours(double r)
    {
        var result = Modeler.Regenerate(Riser(r));

        result.Diagnostics.Should().BeEmpty();
        result.Mesh!.IsWatertight().Should().BeTrue();
        // The riser is 25 long: up to the top face at its convex end, the foot's top at its reflex end.
        var exact = (40 * 5 + 5 * 25) * 20 - Spandrel(r) * 25;
        var volume = SignedVolume(result.Mesh);
        output.WriteLine($"volume {volume:F4} vs {exact:F4}");
        volume.Should().BeApproximately(exact, exact * VolumeTolerance);
        Deviation(result, "round/face", p => Math.Sqrt(Sq(p.X - (5 - r)) + Sq(p.Z - (20 - r))) - r).Should().BeLessThan(1e-7);
        MaxVertexError(result).Should().BeLessThan(1e-7);
        // The foot's top gains the strip under the fillet's end, under its own name: no new face but the fillet.
        result.Topology!.Faces.SelectMany(f => f.Sources).Should().BeEquivalentTo(
            ["bracket/bottom", "bracket/tip", "bracket/inner", "bracket/riser", "bracket/top", "bracket/back", "bracket/start", "bracket/end", "round/face"]);
        var inner = result.Part.Surfaces.Single(s => s.Source == "bracket/inner").Id;
        result.Mesh.Quads.Where(q => q.Owner == inner).SelectMany(q => new[] { q.A, q.B, q.C, q.D }).Select(v => result.Mesh.Vertices[v].X)
            .Min().Should().BeLessThan(5 - r / 2);
    }

    [Fact]
    public void Riser_chamfer_cuts_the_corner_at_the_set_distance()
    {
        var result = Modeler.Regenerate(Riser(0, new Chamfer("round", ["bracket/end", "bracket/riser"], 2)));

        result.Diagnostics.Should().BeEmpty();
        SignedVolume(result.Mesh!).Should().BeApproximately((40 * 5 + 5 * 25) * 20 - 2 * 25, 1e-6);
        Deviation(result, "round/face", p => (p.X + p.Z - 23) / Math.Sqrt(2)).Should().BeLessThan(1e-7);
    }

    [Fact]
    public void Fillet_runs_on_round_tangent_neighbours()
    {
        const double r = 1;
        var result = Modeler.Regenerate(BentRiser(r));

        result.Diagnostics.Should().BeEmpty();
        result.Mesh!.IsWatertight().Should().BeTrue();
        // Straight along the riser (22) and the top (2), a quarter torus round the R3 bend (Pappus).
        var area = 40 * 5 + 5 * 22 + Math.PI * 9 / 4 + 2 * 3;
        var exact = area * 20 - Spandrel(r) * 24 - Math.PI / 2 * (3 - Centroid(r)) * Spandrel(r);
        var volume = SignedVolume(result.Mesh);
        output.WriteLine($"volume {volume:F4} vs {exact:F4}");
        volume.Should().BeApproximately(exact, exact * VolumeTolerance);
        Deviation(result, "round/face", p => Math.Sqrt(Sq(p.X - 4) + Sq(p.Z - 19)) - 1).Should().BeLessThan(1e-7);
        Deviation(result, "round/bracket/bend", p => Math.Sqrt(Sq(Math.Sqrt(Sq(p.X - 2) + Sq(p.Y - 27)) - 2) + Sq(p.Z - 19)) - 1).Should().BeLessThan(1e-7);
        Deviation(result, "round/bracket/top", p => Math.Sqrt(Sq(p.Y - 29) + Sq(p.Z - 19)) - 1).Should().BeLessThan(1e-7);
        MaxVertexError(result).Should().BeLessThan(1e-7);
    }

    [Fact]
    public void Slot_rim_edge_rounds_the_whole_tangent_rim()
    {
        const double r = 1;
        var result = Modeler.Regenerate(SlotRim(r));

        result.Diagnostics.Should().BeEmpty();
        result.Mesh!.IsWatertight().Should().BeTrue();
        // Two straight edges of 20 and a full circle round R5 (the spandrel's centroid outside the wall).
        var exact = 50000 - (200 + Math.PI * 25) * 10 - 40 * Spandrel(r) - 2 * Math.PI * (5 + Centroid(r)) * Spandrel(r);
        var volume = SignedVolume(result.Mesh);
        output.WriteLine($"volume {volume:F4} vs {exact:F4}");
        volume.Should().BeApproximately(exact, exact * VolumeTolerance);
        Deviation(result, "round/face", p => Math.Sqrt(Sq(p.Y - 19) + Sq(p.Z - 9)) - 1).Should().BeLessThan(1e-7);
        Deviation(result, "round/slot/right", p => Math.Sqrt(Sq(Math.Sqrt(Sq(p.X - 60) + Sq(p.Y - 25)) - 6) + Sq(p.Z - 9)) - 1).Should().BeLessThan(1e-7);
        result.Topology!.Faces.SelectMany(f => f.Sources).Should().Contain(["round/face", "round/slot/right", "round/slot/b", "round/slot/left"]);
    }

    [Fact]
    public void Window_rim_edge_ends_on_the_walls_beside_it()
    {
        const double r = 1;
        var result = Modeler.Regenerate(WindowRim(r));

        result.Diagnostics.Should().BeEmpty();
        result.Mesh!.IsWatertight().Should().BeTrue();
        // The edge is 40 long, between two reflex corners of the part: the walls beside it gain the fillet's ends.
        var exact = 50000 - 40 * 20 * 10 - Spandrel(r) * 40;
        var volume = SignedVolume(result.Mesh);
        output.WriteLine($"volume {volume:F4} vs {exact:F4}");
        volume.Should().BeApproximately(exact, exact * VolumeTolerance);
        Deviation(result, "round/face", p => Math.Sqrt(Sq(p.Y - 14) + Sq(p.Z - 9)) - 1).Should().BeLessThan(1e-7);
        MaxVertexError(result).Should().BeLessThan(1e-7);
        result.Topology!.Faces.Should().HaveCount(6 + 4 + 1);
    }

    [Fact]
    public void Window_rim_chamfer_is_flat()
    {
        var result = Modeler.Regenerate(WindowRim(1, round: false));

        result.Diagnostics.Should().BeEmpty();
        SignedVolume(result.Mesh!).Should().BeApproximately(50000 - 40 * 20 * 10 - 0.5 * 40, 1e-6);
        Deviation(result, "round/face", p => (p.Y + p.Z - 24) / Math.Sqrt(2)).Should().BeLessThan(1e-7);
    }

    [Fact]
    public void A_fillet_that_does_not_fit_is_rejected()
    {
        // The riser is 5 thick: a ball of 6 does not land on the end face.
        Modeler.Regenerate(Riser(6)).Diagnostics.Should().ContainSingle().Which.Code.Should().Be(Diagnostic.FilletInvalid);
        Modeler.Regenerate(WindowRim(12)).Diagnostics.Should().ContainSingle().Which.Code.Should().Be(Diagnostic.FilletInvalid);
    }

    [Fact]
    public void A_cap_across_the_sweep_at_an_angle_is_refused()
    {
        var result = Modeler.Regenerate(new PartDocument(
        [
            new Extrude("bracket", BooleanOp.New, Xy, L, 0, 20),
            new PlaneCut("slant", [0, 0, 18], [0, 0.2, 1]),
            new Fillet("round", ["slant/face", "bracket/riser"], 1),
        ]));

        result.Diagnostics.Should().ContainSingle().Which.Code.Should().Be(Diagnostic.UnsupportedFillet);
    }

    [Fact]
    public void A_run_ending_past_a_sharp_reflex_corner_is_refused()
    {
        // The riser leans out over the foot: at its foot the outline turns by more than a quarter, where taking the
        // corner away along the riser would cut into the foot.
        var result = Modeler.Regenerate(new PartDocument(
        [
            new Extrude("bracket", BooleanOp.New, Xy, Loop([0, 0], ("bottom", 40, 0, 0), ("tip", 40, 5, 0), ("inner", 5, 5, 0), ("riser", 15, 30, 0),
                ("top", 10, 30, 0), ("back", 0, 0, 0)), 0, 20),
            new Fillet("round", ["bracket/end", "bracket/riser"], 1),
        ]));

        result.Diagnostics.Should().ContainSingle().Which.Code.Should().Be(Diagnostic.UnsupportedFillet);
    }

    public static IEnumerable<object[]> Parts => [["riser"], ["riser chamfer"], ["bent riser"], ["slot rim"], ["window rim"]];

    static PartDocument Part(string name) => name switch
    {
        "riser" => Riser(1),
        "riser chamfer" => Riser(0, new Chamfer("round", ["bracket/end", "bracket/riser"], 2)),
        "bent riser" => BentRiser(1),
        "slot rim" => SlotRim(1),
        _ => WindowRim(1),
    };

    [Theory]
    [MemberData(nameof(Parts))]
    public void Field_is_1_lipschitz_bounded_and_simplifies_exactly(string name) =>
        FieldProperties.Check(new Compiler(new ModelingSettings()).Compile(Part(name)));

    [Fact]
    public void Faces_keep_their_names_when_sizes_change()
    {
        (PartDocument Doc, JsonNode Edit)[] cases =
        [
            (Riser(1), JsonNode.Parse("""{ "set": "round.radius", "value": 1.5 }""")!),
            (Riser(1), JsonNode.Parse("""{ "set": "bracket.end", "value": 25 }""")!),
            (BentRiser(1), JsonNode.Parse("""{ "set": "round.radius", "value": 0.5 }""")!),
            (SlotRim(1), JsonNode.Parse("""{ "set": "round.radius", "value": 2 }""")!),
            (WindowRim(1), JsonNode.Parse("""{ "set": "round.radius", "value": 2 }""")!),
        ];
        foreach (var (doc, edit) in cases)
        {
            var before = Modeler.Regenerate(doc).Topology!;
            var after = Modeler.Regenerate(DocumentEdits.Apply(doc, [edit]));
            after.Diagnostics.Should().BeEmpty();
            foreach (var source in before.Faces.SelectMany(f => f.Sources))
            {
                var reference = new FaceReference(source, source);
                ReferenceResolver.Resolve(reference, after.Topology!).Status
                    .Should().Be(ReferenceResolver.Resolve(reference, before).Status, $"{source} after {edit.ToJsonString()}");
            }
        }
    }

    [Fact]
    public void Regeneration_is_bit_identical()
    {
        var (a, b) = (Modeler.Regenerate(BentRiser(1)).Mesh!, Modeler.Regenerate(BentRiser(1)).Mesh!);
        a.Vertices.Should().Equal(b.Vertices);
    }

    /// <summary>Largest |distance| of the mesh vertices owned by one source face to its exact surface.</summary>
    static double Deviation(FrepResult r, string source, Func<Point3D, double> distance)
    {
        var owner = r.Part.Surfaces.Single(s => s.Source == source).Id;
        var mesh = r.Mesh!;
        var points = mesh.Quads.Where(q => q.Owner == owner).SelectMany(q => new[] { q.A, q.B, q.C, q.D }).Distinct()
            .Select(v => mesh.Vertices[v])
            .Where(p => r.Part.Field!.Sample(p) is var s && Math.Abs(s.Value) < 1e-9 && s.Owner == owner).ToList();
        points.Should().NotBeEmpty();
        return points.Max(p => Math.Abs(distance(p)));
    }

    /// <summary>
    /// Largest |field| at a mesh vertex. Where a run ends at a reflex corner the neighbour's face gains a piece between
    /// the fillet and the cap, which meet tangentially, so it tapers to a point thinner than any cell; the vertices of
    /// the cells there are on the surface too, if not on its corner (the exact B-rep has that: StepExportTests).
    /// </summary>
    static double MaxVertexError(FrepResult r) => r.Mesh!.Vertices.Max(p => Math.Abs(r.Part.Field!.Value(p)));

    static double Sq(double x) => x * x;

    static double SignedVolume(Mesh mesh)
    {
        var v = mesh.Vertices;
        return mesh.Triangles().Sum(t => v[t.A].AsVector().Dot(v[t.B].AsVector().Cross(v[t.C].AsVector()))) / 6;
    }
}
