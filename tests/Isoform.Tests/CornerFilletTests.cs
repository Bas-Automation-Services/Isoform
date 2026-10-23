using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Modeling;
using Xunit.Abstractions;

namespace Isoform.Tests;

/// <summary>
/// K4: corners where three plane–plane fillets meet. With one radius the ball rolls round the corner on a sphere, as
/// in CAD; with unequal radii there is no exact corner patch, so the fillets keep their mitre and a diagnostic says so.
/// </summary>
public class CornerFilletTests(ITestOutputHelper output)
{
    static readonly FrepModeler Modeler = new(new ModelingSettings { CellSize = 0.25 });
    const double VolumeTolerance = 1e-6;

    // The three edges round the box's corner at its far end.
    public static PartDocument Box(double r, double sx = 20, double sy = 16, double sz = 12, double? r3 = null) => new(
    [
        new Block("box", BooleanOp.New, [0, 0, 0], [sx, sy, sz]),
        new Fillet("f1", ["box/z+", "box/x+"], r),
        new Fillet("f2", ["box/z+", "box/y+"], r),
        new Fillet("f3", ["box/x+", "box/y+"], r3 ?? r),
    ]);

    /// <summary>
    /// The box less what the three fillets take: along each edge, away from the corner cube of side r, the corner of a
    /// right angle less a quarter disc; in the corner cube, the cube less the ball's octant.
    /// </summary>
    public static double BoxVolume(double r, double sx = 20, double sy = 16, double sz = 12) =>
        sx * sy * sz - r * r * (1 - Math.PI / 4) * (sx + sy + sz - 3 * r) - r * r * r * (1 - Math.PI / 6);

    [Theory]
    [InlineData(1.5)]
    [InlineData(3)]
    public void Three_equal_fillets_meet_in_a_sphere(double r)
    {
        var result = Modeler.Regenerate(Box(r));

        result.Diagnostics.Should().BeEmpty();
        result.Mesh!.IsWatertight().Should().BeTrue();
        result.Topology!.Faces.SelectMany(f => f.Sources).Should().Contain("f3/corner:f1+f2+f3");
        var centre = new Point3D(20 - r, 16 - r, 12 - r);
        Deviation(result, "f3/corner:f1+f2+f3", p => (p - centre).Length - r).Should().BeLessThan(1e-7);
        Deviation(result, "f3/face", p => Math.Sqrt(Sq(p.X - centre.X) + Sq(p.Y - centre.Y)) - r).Should().BeLessThan(1e-7);
        var exact = BoxVolume(r);
        var volume = MassProperties.Of(result.Part.Field!, result.Mesh!).Volume;
        output.WriteLine($"volume {volume:F6} vs {exact:F6}");
        volume.Should().BeApproximately(exact, exact * VolumeTolerance);
    }

    [Fact]
    public void The_corner_does_not_depend_on_which_fillet_closes_it()
    {
        var doc = Box(2);
        var reordered = new PartDocument([doc.Features[0], doc.Features[3], doc.Features[1], doc.Features[2]]);

        var result = Modeler.Regenerate(reordered);

        result.Diagnostics.Should().BeEmpty();
        result.Topology!.Faces.SelectMany(f => f.Sources).Should().Contain("f2/corner:f1+f2+f3");
        var exact = BoxVolume(2);
        MassProperties.Of(result.Part.Field!, result.Mesh!).Volume.Should().BeApproximately(exact, exact * VolumeTolerance);
    }

    [Fact]
    public void Corner_faces_keep_their_names_when_the_radius_or_the_block_changes()
    {
        static string[] Names(FrepResult r) => r.Topology!.Faces.SelectMany(f => f.Sources).Order(StringComparer.Ordinal).ToArray();
        var before = Modeler.Regenerate(Box(2));
        var expected = new[] { "box/x+", "box/x-", "box/y+", "box/y-", "box/z+", "box/z-", "f1/face", "f2/face", "f3/corner:f1+f2+f3", "f3/face" };

        Names(before).Should().Equal(expected);
        foreach (var edited in new[] { Box(1.25), Box(3.5), Box(2, sx: 30, sz: 8), Box(2, sy: 9) })
        {
            var after = Modeler.Regenerate(edited);
            after.Diagnostics.Should().BeEmpty();
            Names(after).Should().Equal(expected);
        }
    }

    [Fact]
    public void Unequal_radii_keep_the_mitre_and_say_so()
    {
        var result = Modeler.Regenerate(Box(2, r3: 3));

        var d = result.Diagnostics.Should().ContainSingle().Which;
        d.Code.Should().Be(Diagnostic.CornerMitred);
        d.FeatureId.Should().Be("f3");
        output.WriteLine(d.Message);
        result.Topology!.Faces.SelectMany(f => f.Sources).Should().NotContain(s => s.Contains("corner", StringComparison.Ordinal));
        // Each fillet stays on its own cylinder right into the corner.
        Deviation(result, "f3/face", p => Math.Sqrt(Sq(p.X - 17) + Sq(p.Y - 13)) - 3).Should().BeLessThan(1e-7);
    }

    /// <summary>A 20 × 16 × 12 block with all twelve edges rounded.</summary>
    public static PartDocument Rounded(double r)
    {
        string[] faces = ["x-", "x+", "y-", "y+", "z-", "z+"];
        var edges = from i in Enumerable.Range(0, 6) from j in Enumerable.Range(i + 1, 5 - i)
                    where faces[i][0] != faces[j][0] select (faces[i], faces[j]);
        return new PartDocument([new Block("box", BooleanOp.New, [0, 0, 0], [20, 16, 12]),
            .. edges.Select((e, k) => new Fillet($"r{k}", [$"box/{e.Item1}", $"box/{e.Item2}"], r))]);
    }

    /// <summary>Four edges of each length between the corners, less 2r; a whole ball's worth of corners.</summary>
    public static double RoundedVolume(double r) =>
        20 * 16 * 12 - r * r * (1 - Math.PI / 4) * 4 * (20 + 16 + 12 - 6 * r) - 8 * r * r * r * (1 - Math.PI / 6);

    [Fact]
    public void Every_corner_of_a_fully_rounded_block_is_a_sphere()
    {
        var result = Modeler.Regenerate(Rounded(2));

        result.Diagnostics.Should().BeEmpty();
        result.Mesh!.IsWatertight().Should().BeTrue();
        result.Topology!.Faces.Count(f => f.Sources.Any(s => s.Contains("/corner:", StringComparison.Ordinal))).Should().Be(8);
        var exact = RoundedVolume(2);
        var volume = MassProperties.Of(result.Part.Field!, result.Mesh!).Volume;
        output.WriteLine($"volume {volume:F6} vs {exact:F6}");
        // Curved all round: the mesh is 2e-6 off at 0.25 mm cells (mitres at the corners would be 1e-3 off).
        volume.Should().BeApproximately(exact, exact * 5 * VolumeTolerance);
    }

    // A box whose end leans back: the corner of the slanted face and two box faces is not square.
    static PartDocument Slanted(double r) => new(
    [
        new Block("box", BooleanOp.New, [0, 0, 0], [20, 16, 12]),
        new PlaneCut("cut", [20.5, 0, 0], [1, 0, 0.3]),
        new Fillet("f1", ["cut/face", "box/y+"], r),
        new Fillet("f2", ["box/y+", "box/z+"], r),
        new Fillet("f3", ["box/z+", "cut/face"], r),
    ]);

    [Fact]
    public void A_corner_that_is_not_square_is_a_sphere_too()
    {
        var result = Modeler.Regenerate(Slanted(1.5));

        result.Diagnostics.Should().BeEmpty();
        var sphere = result.Part.Surfaces.Single(s => s.Source == "f3/corner:f1+f2+f3");
        Deviation(result, sphere.Source, p => (p - sphere.Origin).Length - 1.5).Should().BeLessThan(1e-7);
    }

    // A 20 × 16 × 8 boss joined on a plate, the inside edges round its foot rounded: two meeting at its corner at
    // (20, 10, 10), or all four.
    public static PartDocument Boss(double r, double? r2 = null, bool all = false) => new(
    [
        new Block("plate", BooleanOp.New, [0, 0, 0], [60, 40, 10]),
        new Block("boss", BooleanOp.Join, [20, 10, 10], [20, 16, 8]),
        new Fillet("f1", ["plate/z+", "boss/x-"], r),
        new Fillet("f2", ["plate/z+", "boss/y-"], r2 ?? r),
        .. all ? new Feature[] { new Fillet("f3", ["plate/z+", "boss/x+"], r), new Fillet("f4", ["plate/z+", "boss/y+"], r) } : [],
    ]);

    /// <summary>
    /// Plate, boss and the fillers: along each rounded edge the corner less a quarter disc, and at each corner where
    /// two meet a quarter of the spandrel swept round the boss's edge (Pappus: its ∫ρ dA is r³(5/6 − π/4)).
    /// </summary>
    public static double BossVolume(double r, bool all = false) =>
        60 * 40 * 10 + 20 * 16 * 8 + r * r * (1 - Math.PI / 4) * (all ? 72 : 36) + (all ? 4 : 1) * Math.PI / 2 * r * r * r * (5.0 / 6 - Math.PI / 4);

    [Theory]
    [InlineData(1.5)]
    [InlineData(3)]
    public void Two_inside_fillets_meet_round_the_boss_edge_in_a_horn_torus(double r)
    {
        var result = Modeler.Regenerate(Boss(r));

        result.Diagnostics.Should().BeEmpty();
        result.Mesh!.IsWatertight().Should().BeTrue();
        // Round the boss's edge at (20, 10): the ball centre circle of radius r at height 10 + r, a tube of r.
        Deviation(result, "f2/corner:f1+f2", p => Math.Sqrt(Sq(Math.Sqrt(Sq(p.X - 20) + Sq(p.Y - 10)) - r) + Sq(p.Z - 10 - r)) - r).Should().BeLessThan(1e-7);
        var exact = BossVolume(r);
        var volume = MassProperties.Of(result.Part.Field!, result.Mesh!).Volume;
        output.WriteLine($"volume {volume:F6} vs {exact:F6}");
        volume.Should().BeApproximately(exact, exact * VolumeTolerance);
    }

    [Fact]
    public void Four_inside_fillets_round_a_boss_close_all_four_corners()
    {
        var result = Modeler.Regenerate(Boss(2, all: true));

        result.Diagnostics.Should().BeEmpty();
        result.Mesh!.IsWatertight().Should().BeTrue();
        result.Topology!.Faces.SelectMany(f => f.Sources).Where(s => s.Contains("/corner:", StringComparison.Ordinal)).Should()
            .BeEquivalentTo(["f2/corner:f1+f2", "f3/corner:f2+f3", "f4/corner:f1+f4", "f4/corner:f3+f4"]);
        var exact = BossVolume(2, all: true);
        MassProperties.Of(result.Part.Field!, result.Mesh!).Volume.Should().BeApproximately(exact, exact * VolumeTolerance);
    }

    [Fact]
    public void Horn_corner_keeps_its_name_when_the_radius_changes()
    {
        static string[] Names(FrepResult r) => r.Topology!.Faces.SelectMany(f => f.Sources).Order(StringComparer.Ordinal).ToArray();
        var before = Names(Modeler.Regenerate(Boss(2)));

        before.Should().Contain("f2/corner:f1+f2");
        foreach (var r in new[] { 1.25, 3.5 })
            Names(Modeler.Regenerate(Boss(r))).Should().Equal(before);
    }

    [Fact]
    public void Inside_fillets_of_unequal_radii_leave_the_corner_open_and_say_so()
    {
        var result = Modeler.Regenerate(Boss(2, r2: 3));

        var d = result.Diagnostics.Should().ContainSingle().Which;
        d.Code.Should().Be(Diagnostic.CornerOpen);
        output.WriteLine(d.Message);
        result.Topology!.Faces.SelectMany(f => f.Sources).Should().NotContain(s => s.Contains("corner", StringComparison.Ordinal));
    }

    // An L of two joined blocks on the plate: the corner at (20, 10) is a convex edge between faces of different solids.
    static PartDocument Ell(double r) => new(
    [
        new Block("plate", BooleanOp.New, [0, 0, 0], [60, 40, 10]),
        new Block("boss", BooleanOp.Join, [20, 10, 10], [20, 6, 8]),
        new Block("arm", BooleanOp.Join, [20, 10, 10], [6, 20, 8]),
        new Fillet("f1", ["plate/z+", "boss/y-"], r),
        new Fillet("f2", ["plate/z+", "arm/x-"], r),
    ]);

    [Fact]
    public void Inside_fillets_round_faces_of_two_joined_solids_meet_in_a_horn_torus()
    {
        const double r = 2;
        var result = Modeler.Regenerate(Ell(r));

        result.Diagnostics.Should().BeEmpty();
        result.Mesh!.IsWatertight().Should().BeTrue();
        Deviation(result, "f2/corner:f1+f2", p => Math.Sqrt(Sq(Math.Sqrt(Sq(p.X - 20) + Sq(p.Y - 10)) - r) + Sq(p.Z - 10 - r)) - r).Should().BeLessThan(1e-7);
        // Plate, the L, a fillet 20 long on each face and the horn's quarter spandrel round the edge.
        var exact = 60 * 40 * 10 + (20 * 6 + 6 * 20 - 6 * 6) * 8 + r * r * (1 - Math.PI / 4) * 40 + Math.PI / 2 * r * r * r * (5.0 / 6 - Math.PI / 4);
        MassProperties.Of(result.Part.Field!, result.Mesh!).Volume.Should().BeApproximately(exact, exact * VolumeTolerance);
    }

    static readonly SketchPlane OnPlate = new([0, 0, 10], [1, 0, 0], [0, 0, 1]);

    static PartDocument Extruded(Profile profile, string a, string b, SketchPlane? plane = null, double start = 0) => new(
    [
        new Block("plate", BooleanOp.New, [0, 0, 0], [60, 40, 10]),
        new Extrude("boss", BooleanOp.Join, plane ?? OnPlate, profile, start, 8),
        new Fillet("f1", ["plate/z+", $"boss/{a}"], 1.5),
        new Fillet("f2", ["plate/z+", $"boss/{b}"], 1.5),
    ]);

    // A boss of a straight edge and a major arc: two convex kinks between a plane and a cylinder.
    static PartDocument DShape() => Extruded(new Profile([35, 10], [new ProfileEdge("a", [45, 10]), new ProfileEdge("b", [35, 10], 2.5)]), "a", "b");

    // A hexagon (corners of 120°) and an equilateral triangle (60°).
    static PartDocument Polygon(int sides)
    {
        var (side, turn) = (sides == 6 ? 10.0 : 20.0, 2 * Math.PI / sides);
        var points = Enumerable.Range(1, sides - 1).Select(i => new[] { 25 + side * Enumerable.Range(0, i).Sum(k => Math.Cos(k * turn)), 10 + side * Enumerable.Range(0, i).Sum(k => Math.Sin(k * turn)) })
            .Append([25, 10]);
        return Extruded(new Profile([25, 10], points.Select((p, i) => new ProfileEdge(((char)('a' + i)).ToString(), p)).ToList()), "a", "b");
    }

    [Theory]
    [InlineData("curved", "boss/b is curved")]
    [InlineData("hexagon", "boss/b and boss/a do not meet at a right angle")]
    [InlineData("triangle", "boss/b and boss/a do not meet at a right angle")]
    [InlineData("leaning", "the edge between boss/y- and boss/x- is not square to cut/face")]
    public void Inside_fillets_meeting_at_a_corner_with_no_exact_filler_leave_it_open_and_say_so(string name, string why)
    {
        var result = Modeler.Regenerate(name switch
        {
            "curved" => DShape(),
            "hexagon" => Polygon(6),
            "triangle" => Polygon(3),
            // An upright boss on a sloping top, running off the plate's low end: its edge leans on the top.
            _ => new PartDocument(
            [
                new Block("plate", BooleanOp.New, [0, 0, 0], [60, 40, 10]),
                new PlaneCut("cut", [0, 0, 10], [0.1, 0, 1]),
                new Block("boss", BooleanOp.Join, [20, 10, 0], [50, 16, 18]),
                new Fillet("f1", ["cut/face", "boss/x-"], 1.5),
                new Fillet("f2", ["cut/face", "boss/y-"], 1.5),
            ]),
        });

        var d = result.Diagnostics.Where(d => d.Code != Diagnostic.BelowResolution).Should().ContainSingle().Which;
        output.WriteLine(d.Message);
        d.Code.Should().Be(Diagnostic.CornerOpen);
        d.FeatureId.Should().Be("f2");
        d.Message.Should().Contain(why);
        result.Topology!.Faces.SelectMany(f => f.Sources).Should().Contain(["f1/face", "f2/face"]).And.NotContain(s => s.Contains("corner", StringComparison.Ordinal));
    }

    public static IEnumerable<object[]> Misfits =>
    [
        // A rounded rectangle: the straight edges run on smoothly into the arcs at the corners.
        ["tangent arc", Extruded(new Profile([25, 10], [new ProfileEdge("a", [35, 10]), new ProfileEdge("ab", [40, 15], 0.41421356237309503),
            new ProfileEdge("b", [40, 25]), new ProfileEdge("bc", [35, 30], 0.41421356237309503), new ProfileEdge("c", [25, 30]),
            new ProfileEdge("cd", [20, 25], 0.41421356237309503), new ProfileEdge("d", [20, 15]), new ProfileEdge("da", [25, 10], 0.41421356237309503)]), "a", "b"), new[] { "f1", "f2" }],
        // A minor arc at a kink: its whole circle would cut the straight edge's filler short.
        ["arc kink", Extruded(new Profile([20, 10], [new ProfileEdge("a", [40, 10]), new ProfileEdge("b", [40, 26], 0.5), new ProfileEdge("c", [20, 26]), new ProfileEdge("d", [20, 10])]), "d", "a"), new[] { "f2" }],
        // The boss's own edge rounded first: its sides end in the round, short of the sharp edge.
        ["rounded edge", new PartDocument([.. Boss(1.5).Features.Take(2), new Fillet("v", ["boss/x-", "boss/y-"], 3), .. Boss(1.5).Features.Skip(2)]), new[] { "f1", "f2" }],
        // The boss's own edge rounded after: the fillers lean on the sharp sides.
        ["rounded after", new PartDocument([.. Boss(1.5).Features, new Fillet("v", ["boss/x-", "boss/y-"], 3)]), new[] { "v" }],
        // Half a revolve: its start and end faces are one plane, so the filler on one has nothing to end at.
        ["half revolve", new PartDocument([new Block("plate", BooleanOp.New, [0, 0, 0], [60, 40, 10]),
            new Revolve("boss", BooleanOp.Join, new SketchPlane([30, 20, 10], [1, 0, 0], [0, -1, 0]),
                new Profile([0, 0], [new ProfileEdge("bottom", [8, 0]), new ProfileEdge("side", [8, 8]), new ProfileEdge("top", [0, 8]), new ProfileEdge("axis", [0, 0])]), 180),
            new Fillet("f1", ["plate/z+", "boss/start"], 1.5)]), new[] { "f1" }],
    ];

    [Theory]
    [MemberData(nameof(Misfits))]
    public void An_inside_fillet_that_cannot_end_where_its_edge_does_is_refused(string name, PartDocument doc, string[] refused)
    {
        var result = Modeler.Regenerate(doc);

        output.WriteLine(name);
        foreach (var d in result.Diagnostics) output.WriteLine($"{d.Code} {d.FeatureId}: {d.Message}");
        result.Diagnostics.Where(d => d.Code != Diagnostic.BelowResolution).Should().HaveCount(refused.Length)
            .And.OnlyContain(d => d.Code == Diagnostic.UnsupportedFillet && refused.Contains(d.FeatureId));
        result.Topology!.Faces.SelectMany(f => f.Sources).Should().NotContain(refused.Select(f => $"{f}/face"));
    }

    public static IEnumerable<object[]> Parts => [["box"], ["slanted"], ["unequal"], ["boss"], ["boss all round"], ["ell"]];

    [Theory]
    [MemberData(nameof(Parts))]
    public void Field_is_1_lipschitz_bounded_and_simplifies_exactly(string name) =>
        FieldProperties.Check(new Compiler(new ModelingSettings()).Compile(name switch
        {
            "box" => Box(3),
            "slanted" => Slanted(1.5),
            "boss" => Boss(3),
            "boss all round" => Boss(2, all: true),
            "ell" => Ell(2),
            _ => Box(2, r3: 3),
        }));

    /// <summary>Largest |distance| of the vertices a face owns on the solid.</summary>
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

    static double Sq(double x) => x * x;
}
