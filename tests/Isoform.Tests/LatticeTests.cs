using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Geometry;
using Isoform.Modeling;
using Isoform.Step;

namespace Isoform.Tests;

/// <summary>
/// Lattices: the K1 guarantees for every kind, struts as the exact distance to their lines, the sheets' gradient bound
/// and gap law, the volume of a strut cube, resolution reports, the rules, and STEP naming the lattice unsupported.
/// </summary>
public class LatticeTests
{
    static readonly ModelingSettings Settings = new();
    static readonly Surface Face = new(0, "lattice/struts", SurfaceKind.Lattice, new Point3D(0, 0, 0), new Vector3D(0, 0, 1));
    static readonly Block Box = new("box", BooleanOp.New, [-3, -2, -1], [17, 13, 11]);

    public static IEnumerable<object[]> Kinds => Lattice.Kinds.Select(k => new object[] { k });

    static LatticeKind KindOf(string kind) => DocumentRules.LatticeKindOf(kind)!.Value;

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Every_kind_keeps_the_field_guarantees_open_in_a_skin_and_in_a_region(string kind)
    {
        foreach (var lattice in new[]
        {
            new Lattice("infill", kind, 6, 0.9),
            new Lattice("infill", kind, 6, 0.9, 1.2),
            new Lattice("infill", kind, 6, 0.9, 1.2, new LatticeRegion(null, [2, 1, -5], [7, 20, 8])),
        })
        {
            var part = new Compiler(Settings).Compile(new PartDocument([Box, lattice]));
            part.Diagnostics.Should().BeEmpty();
            FieldProperties.Check(part);
        }
    }

    [Theory]
    [InlineData("bcc")]
    [InlineData("octet")]
    [InlineData("cubic")]
    public void Struts_are_the_exact_distance_to_the_lattice_lines_and_the_gap_is_the_closest_pair_that_does_not_meet(string kind)
    {
        const double a = 7, t = 1.2;
        var field = new LatticeField(KindOf(kind), a, t, Face);
        var lines = Lines(kind, a);
        var rng = new Random(5);
        for (var i = 0; i < 1000; i++)
        {
            var p = new Point3D(a * (3 * rng.NextDouble() - 1), a * (3 * rng.NextDouble() - 1), a * (3 * rng.NextDouble() - 1));
            var nearest = lines.Min(l => ((p - l.At) - l.Along * (p - l.At).Dot(l.Along)).Length);
            field.Value(p).Should().BeApproximately(nearest - t / 2, 1e-9);
            var s = field.Sample(p);
            s.Value.Should().Be(field.Value(p));
            s.Gradient.Length.Should().BeApproximately(1, 1e-9);
        }

        // Lines through the origin against all others: the nearest that neither meets nor coincides with one.
        var gap = double.PositiveInfinity;
        foreach (var l in lines.Where(l => l.At.AsVector().Length < 1e-9))
        foreach (var m in lines)
        {
            var w = m.At - l.At;
            var n = l.Along.Cross(m.Along);
            var d = n.Length < 1e-9 ? (w - l.Along * w.Dot(l.Along)).Length : Math.Abs(w.Dot(n)) / n.Length;
            if (d > 1e-9) gap = Math.Min(gap, d);
        }
        LatticeField.Gap(KindOf(kind), a, t).Should().BeApproximately(gap - t, 1e-9);
    }

    // The lines of a strut lattice near the origin, built from the nodes and bond directions (not from the field's rows).
    static List<(Point3D At, Vector3D Along)> Lines(string kind, double a)
    {
        var (nodes, directions) = kind switch
        {
            "cubic" => (Grid(a, 5, _ => true), new[] { new Vector3D(1, 0, 0), new Vector3D(0, 1, 0), new Vector3D(0, 0, 1) }),
            "bcc" => (Grid(a, 5, _ => true), new[] { new Vector3D(1, 1, 1), new Vector3D(-1, 1, 1), new Vector3D(1, -1, 1), new Vector3D(1, 1, -1) }),
            _ => (Grid(a / 2, 10, n => (n.I + n.J + n.K) % 2 == 0),
                new[] { new Vector3D(1, 1, 0), new Vector3D(1, -1, 0), new Vector3D(1, 0, 1), new Vector3D(1, 0, -1), new Vector3D(0, 1, 1), new Vector3D(0, 1, -1) }),
        };
        return [.. from n in nodes from d in directions select (n, d.Normalized())];
    }

    static List<Point3D> Grid(double step, int reach, Func<(int I, int J, int K), bool> keep) =>
        [.. from i in Enumerable.Range(-reach, 2 * reach + 1) from j in Enumerable.Range(-reach, 2 * reach + 1) from k in Enumerable.Range(-reach, 2 * reach + 1)
            where keep((Math.Abs(i), Math.Abs(j), Math.Abs(k))) select new Point3D(i * step, j * step, k * step)];

    [Theory]
    [InlineData("gyroid")]
    [InlineData("schwarz-p")]
    [InlineData("diamond")]
    public void Sheets_are_scaled_by_a_gradient_bound_that_is_tight(string kind)
    {
        var field = new LatticeField(KindOf(kind), 8, 1, Face);
        var rng = new Random(2);
        var steepest = 0.0;
        for (var i = 0; i < 200_000; i++)
        {
            var g = field.Sample(new Point3D(8 * rng.NextDouble(), 8 * rng.NextDouble(), 8 * rng.NextDouble())).Gradient.Length;
            g.Should().BeLessThanOrEqualTo(1 + 1e-12);
            steepest = Math.Max(steepest, g);
        }
        steepest.Should().BeGreaterThan(0.99, "√3 is the gradient's maximum, not just a bound");
    }

    [Theory]
    [InlineData("gyroid")]
    [InlineData("schwarz-p")]
    [InlineData("diamond")]
    public void The_gap_law_agrees_with_a_caliper_over_the_cell(string kind)
    {
        const double a = 10;
        foreach (var t in new[] { 0.5, 1, 1.5 })
        {
            var field = new LatticeField(KindOf(kind), a, t, Face);
            var rng = new Random(11);
            var caliper = double.PositiveInfinity;
            for (var found = 0; found < 4000;)
            {
                // Onto the sheet's surface, then along its normal into the void to the next sheet.
                var p = new Point3D(a * rng.NextDouble(), a * rng.NextDouble(), a * rng.NextDouble());
                for (var k = 0; k < 30; k++)
                {
                    var s = field.Sample(p);
                    if (s.Gradient.LengthSquared < 1e-12) break;
                    p -= s.Gradient * (s.Value / s.Gradient.LengthSquared);
                }
                if (Math.Abs(field.Value(p)) > 1e-9) continue;
                found++;
                var n = field.Sample(p).Gradient.Normalized();
                var d = 1e-6;
                for (var f = field.Value(p + n * d); f >= 0 && d < a; f = field.Value(p + n * d)) d += Math.Max(f, 1e-4);
                caliper = Math.Min(caliper, d);
            }
            LatticeField.Gap(KindOf(kind), a, t).Should().BeInRange(0.93 * caliper, 1.03 * caliper, $"{kind} at t = {t}");
        }
    }

    [Fact]
    public void A_cubic_strut_cube_has_its_analytic_volume()
    {
        // Per cell three struts of length a, less the overlaps at the node: three bicylinders (16/3 r³) less the
        // tricylinder (8 (2 − √2) r³) they share, 8√2 r³. The cube is 27 whole cells.
        const double a = 10, r = 1;
        var exact = 27 * (3 * Math.PI * r * r * a - 8 * Math.Sqrt(2) * r * r * r);
        var result = new FrepModeler(Settings).Regenerate(new PartDocument([new Block("cube", BooleanOp.New, [0, 0, 0], [30, 30, 30]), new Lattice("lattice", "cubic", a, 2 * r)]));

        result.Diagnostics.Should().BeEmpty();
        result.Mesh!.IsWatertight().Should().BeTrue();
        var field = result.Part.Bodies[0].Field;
        MassProperties.Of(field, result.Mesh).Volume.Should().BeApproximately(exact, 1e-3 * exact);
        var certified = MassProperties.VolumeBounds(field, result.Part.Min, result.Part.Max, Settings.CellSize);
        certified.Lo.Should().BeLessThanOrEqualTo(exact);
        certified.Hi.Should().BeGreaterThanOrEqualTo(exact);
    }

    [Fact]
    public void Thin_walls_gaps_and_skins_are_below_resolution_with_the_cell_that_resolves_them()
    {
        var sheets = new FrepModeler(Settings).Regenerate(new PartDocument([new Block("box", BooleanOp.New, [0, 0, 0], [12, 12, 12]), new Lattice("infill", "gyroid", 6, 0.8, 0.6)]));
        var struts = new FrepModeler(Settings).Regenerate(new PartDocument([new Block("box", BooleanOp.New, [0, 0, 0], [12, 12, 12]), new Lattice("infill", "cubic", 2, 1.2)]));

        var below = sheets.Diagnostics.Where(d => d.Code == Diagnostic.BelowResolution && d.FeatureId == "infill").Select(d => d.Message).ToList();
        below.Should().Contain(m => m.StartsWith("infill (0.8 mm) is below the resolved size of 1 mm") && m.EndsWith("use a CellSize of 0.4 mm or less"));
        below.Should().Contain(m => m.StartsWith("infill skin (0.6 mm)") && m.EndsWith("use a CellSize of 0.3 mm or less"));
        struts.Diagnostics.Should().Contain(d => d.Code == Diagnostic.BelowResolution && d.Message.StartsWith("the struts of infill are 0.8 mm apart"));
        // At a cell that resolves them, none of the lattice's own reports is left.
        var fine = new FrepModeler(Settings with { CellSize = 0.4 }).Regenerate(new PartDocument([new Block("box", BooleanOp.New, [0, 0, 0], [12, 12, 12]), new Lattice("infill", "cubic", 2, 1.2)]));
        fine.Diagnostics.Should().NotContain(d => d.Message.Contains("struts of infill"));
    }

    [Fact]
    public void The_rules_reject_bad_lattices_and_reshaping_after_one()
    {
        string? Problem(params Feature[] features) => DocumentRules.FirstProblem(new PartDocument([Box, .. features]), 1e-7)?.Message;

        Problem(new Lattice("infill", "foam", 6, 1)).Should().Contain("lattice kind must be one of gyroid, schwarz-p");
        Problem(new Lattice("infill", "cubic", 6, 6)).Should().Contain("closes its voids").And.Contain("below 6 mm");
        Problem(new Lattice("infill", "gyroid", 6, 2)).Should().Contain("closes its voids");
        Problem(new Lattice("infill", "octet", 6, 1, -1)).Should().Contain("skin must not be negative");
        Problem(new Lattice("infill", "bcc", 6, 1, Region: new LatticeRegion("later"))).Should().Contain("'later' is not one");
        Problem(new Lattice("infill", "bcc", 6, 1, Region: new LatticeRegion(null, [0, 0, 0], [1, 0, 1]))).Should().Contain("positive size");

        var compiler = new Compiler(Settings);
        foreach (Feature after in new Feature[] { new Fillet("round", ["box/z+", "box/x+"], 1), new Shell("shell", 1), new Offset("grow", 1) })
            compiler.Compile(new PartDocument([Box, new Lattice("infill", "bcc", 6, 1, 1.5), after])).Diagnostics.Should()
                .ContainSingle(d => d.Code == Diagnostic.InvalidParameter && d.FeatureId == after.Id).Which.Message.Should().Contain("before the lattice");
    }

    [Fact]
    public void A_region_feature_keeps_the_body_solid_outside_it()
    {
        var doc = new PartDocument(
        [
            new Block("plate", BooleanOp.New, [0, 0, 0], [40, 20, 10]),
            new Block("boss", BooleanOp.Join, [10, 5, 10], [20, 10, 10]),
            new Lattice("infill", "cubic", 8, 1.5, Region: new LatticeRegion("boss")),
        ]);
        var result = new FrepModeler(Settings).Regenerate(doc);

        result.Diagnostics.Should().BeEmpty();
        var faces = result.Topology!.Faces.SelectMany(f => f.Sources).ToHashSet();
        // The boss is the region, so its faces carry the boundary; the lattice is cut by them.
        faces.Should().Contain(["infill/struts", "plate/x-", "boss/z+"]);
        // Solid outside the zone, lattice inside it.
        var field = result.Part.Field!;
        field.Value(new Point3D(5, 10, 5)).Should().BeLessThan(-2);
        field.Value(new Point3D(20, 12, 13)).Should().BeGreaterThan(0);
    }

    [Fact]
    public void Lattices_read_back_with_regions_skins_and_expressions()
    {
        var text = """
            {"format": "isoform/0", "units": "mm", "parameters": {"pitch": 8},
             "features": [
               {"id": "box", "type": "block", "op": "new", "origin": [0, 0, 0], "size": [20, 20, 20]},
               {"id": "infill", "type": "lattice", "kind": "schwarz-p", "cell": "=pitch", "thickness": 1, "skin": "=pitch / 4",
                "region": {"origin": [0, 0, 0], "size": [20, 20, "=pitch"]}},
               {"id": "more", "type": "lattice", "kind": "octet", "cell": 6, "thickness": 1, "region": "box"}]}
            """;
        var doc = CanonicalFormat.Read(text);
        var resolved = (Lattice)Parametric.Resolve(doc)["infill"];

        CanonicalFormat.Write(CanonicalFormat.Read(CanonicalFormat.Write(doc))).Should().Be(CanonicalFormat.Write(doc));
        (resolved.Cell, resolved.Skin, resolved.Region!.Size![2]).Should().Be((8, 2, 8));
        ((Lattice)doc["more"]).Region!.Feature.Should().Be("box");
        DocumentEdits.Apply(doc, [System.Text.Json.Nodes.JsonNode.Parse("""{"set": "infill.cell", "value": 10}""")!])["infill"].Should().BeOfType<Lattice>().Which.Cell.Should().Be(10);
    }

    [Fact]
    public void Step_export_names_the_lattice_face_unsupported()
    {
        var result = new FrepModeler(Settings).Regenerate(new PartDocument([new Block("cube", BooleanOp.New, [0, 0, 0], [20, 20, 20]), new Lattice("lattice", "octet", 10, 1.5, 2)]));

        var build = () => ExactBrep.Build(result, Settings);
        build.Should().Throw<NotSupportedException>().WithMessage("*lattice/struts (Lattice)*");
    }

    [Fact]
    public void A_mesh_the_cells_pinch_is_reported()
    {
        // A gyroid that grazes the corner of its skin's cavity within a cell: the mesher keeps one vertex for two pieces.
        var circle = new Profile([-10, 0], [new ProfileEdge("a", [10, 0], 1), new ProfileEdge("b", [-10, 0], 1)]);
        var result = new FrepModeler(Settings with { ChordTolerance = 0.05 }).Regenerate(new PartDocument(
            [new Extrude("can", BooleanOp.New, new SketchPlane([0, 0, 0], [1, 0, 0], [0, 0, 1]), circle, 0, 20), new Lattice("infill", "gyroid", 10, 1, 1.5)]));

        result.Mesh!.IsWatertight().Should().BeFalse("this is the case the report is for");
        result.Diagnostics.Should().Contain(d => d.Code == Diagnostic.BelowResolution && d.FeatureId == "infill" && d.Message.Contains("pinch the mesh"));
    }

    [Theory]
    [InlineData("gyroid", 6, 1, 1.5, 0.0852)]
    [InlineData("schwarz-p", 10, 1, 1.5, 0.0843)]
    [InlineData("diamond", 6, 1, 1.5, 0.0852)]
    [InlineData("schwarz-p", 8, 0.8, 1.2, 0.0674)]
    public void The_narrowest_channel_between_the_sheets_and_the_skin_is_as_wide_as_the_field_says(string kind, double cell, double thickness, double skin, double width)
    {
        // Where a sheet comes within a cell of the skin's inner face, facing it, the void between them is a channel; the
        // search finds its narrowest place. Across it, along the normal, the field is void for just that width.
        var doc = new PartDocument([new Block("box", BooleanOp.New, [0, 0, 0], [20, 20, 20]), new Lattice("infill", kind, cell, thickness, skin)]);
        var part = new Compiler(Settings).Compile(doc);
        var channel = LatticeChannels.Find(doc, part, 2 * Settings.CellSize, Settings.Tolerance)[0].MinBy(c => c.Width);

        channel.Width.Should().BeApproximately(width, 1e-4);
        var n = (channel.Wall - channel.Sheet) / channel.Width;
        var across = Enumerable.Range(-200, 1400).Select(i => i * 1e-4).Where(s => part.Field!.Value(channel.Sheet + n * s) > 0).ToList();
        across.Min().Should().BeApproximately(0, 2e-4);
        across.Max().Should().BeApproximately(channel.Width, 2e-4);
        across.Should().HaveCount((int)Math.Round((across.Max() - across.Min()) / 1e-4) + 1, "one void, from the sheet to the wall");
    }

    [Fact]
    public void A_channel_the_cells_fill_in_is_reported_and_meshes_at_a_cell_that_resolves_it()
    {
        // A gyroid of 6 mm cells in a 1.5 mm skin comes within 0.085 mm of the skin's inner faces at a saddle: a channel
        // between two voids, closed where the sheet meets the wall on either side. 0.5 mm cells fill it in.
        var doc = new PartDocument([new Block("box", BooleanOp.New, [0, 0, 0], [7.5, 7.5, 7.5]), new Lattice("infill", "gyroid", 6, 1, 1.5)]);
        var coarse = new FrepModeler(Settings).Regenerate(doc);
        var channel = LatticeChannels.Find(doc, coarse.Part, 2 * Settings.CellSize, Settings.Tolerance)[0].MinBy(c => c.Width);
        static double Distance(Isoform.Meshing.Mesh mesh, Point3D p) => new Isoform.Meshing.TriangleBvh(mesh.Vertices, mesh.Triangles().ToList()).Distance(p);

        Distance(coarse.Mesh!, channel.Middle).Should().BeGreaterThan(channel.Width, "no polygon runs through the channel: the cells filled it in");
        coarse.Diagnostics.Should().ContainSingle(d => d.Code == Diagnostic.BelowResolution && d.FeatureId == "infill").Which.Message.Should()
            .StartWith("the sheets of infill leave a channel 0.0852 mm wide against its skin near").And.EndWith("below the resolved size of 1 mm; use a CellSize of 0.0426 mm or less");

        // One more level of refinement resolves it: the zones round the channels carry them, and nothing is reported.
        var fine = new FrepModeler(Settings with { Refinement = 4 }).Regenerate(doc);
        fine.Diagnostics.Should().BeEmpty();
        Distance(fine.Mesh!, channel.Middle).Should().BeApproximately(channel.Width / 2, 1e-3, "the sheet and the wall each half the width away");
    }

    [Fact]
    public void A_sliver_the_sheets_cut_off_the_cavity_at_an_edge_is_reported()
    {
        // In an 8 mm box the gyroid passes 0.01 mm inside an edge of the skin's cavity and cuts a sliver of void off it
        // that no cell holds a vertex of: lost whole. The channels are refined away, as above.
        var doc = new PartDocument([new Block("box", BooleanOp.New, [0, 0, 0], [8, 8, 8]), new Lattice("infill", "gyroid", 6, 1, 1.5)]);
        var result = new FrepModeler(Settings with { Refinement = 4 }).Regenerate(doc);

        result.Diagnostics.Should().ContainSingle(d => d.Code == Diagnostic.BelowResolution && d.FeatureId == "infill").Which.Message.Should()
            .StartWith("infill leaves a gap 0.0127 mm across near (1.51, 6.36, 1.51) that the cells lose whole");
    }

    [Fact]
    public void A_channel_the_cells_carry_is_not_reported()
    {
        // A gyroid of 7 mm cells in a 1.3 mm skin leaves channels 0.68 mm wide, below the resolved size of 1 mm, but
        // 0.5 mm cells carry them.
        var doc = new PartDocument([new Block("box", BooleanOp.New, [0, 0, 0], [8, 8, 8]), new Lattice("infill", "gyroid", 7, 1, 1.3)]);
        var result = new FrepModeler(Settings).Regenerate(doc);

        LatticeChannels.Find(doc, result.Part, 2 * Settings.CellSize, Settings.Tolerance)[0].Should().NotBeEmpty().And.OnlyContain(c => c.Width < 1);
        result.Diagnostics.Should().NotContain(d => d.Message.Contains("channel"));
    }
}
