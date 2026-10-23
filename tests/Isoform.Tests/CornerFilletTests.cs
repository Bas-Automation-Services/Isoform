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

    [Fact]
    public void Every_corner_of_a_fully_rounded_block_is_a_sphere()
    {
        string[] faces = ["x-", "x+", "y-", "y+", "z-", "z+"];
        var edges = from i in Enumerable.Range(0, 6) from j in Enumerable.Range(i + 1, 5 - i)
                    where faces[i][0] != faces[j][0] select (faces[i], faces[j]);
        var doc = new PartDocument([new Block("box", BooleanOp.New, [0, 0, 0], [20, 16, 12]),
            .. edges.Select((e, k) => new Fillet($"r{k}", [$"box/{e.Item1}", $"box/{e.Item2}"], 2))]);

        var result = Modeler.Regenerate(doc);

        result.Diagnostics.Should().BeEmpty();
        result.Mesh!.IsWatertight().Should().BeTrue();
        result.Topology!.Faces.Count(f => f.Sources.Any(s => s.Contains("/corner:", StringComparison.Ordinal))).Should().Be(8);
        // Edges of 20 − 4, 16 − 4 and 12 − 4 between the corners, four of each; a whole ball of r at the corners.
        var exact = 20 * 16 * 12 - 2 * 2 * (1 - Math.PI / 4) * 4 * (16 + 12 + 8) - 8 * 8 * (1 - Math.PI / 6);
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

    public static IEnumerable<object[]> Parts => [["box"], ["slanted"], ["unequal"]];

    [Theory]
    [MemberData(nameof(Parts))]
    public void Field_is_1_lipschitz_bounded_and_simplifies_exactly(string name) =>
        FieldProperties.Check(new Compiler(new ModelingSettings()).Compile(name switch
        {
            "box" => Box(3),
            "slanted" => Slanted(1.5),
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
