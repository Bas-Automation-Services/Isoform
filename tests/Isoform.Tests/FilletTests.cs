using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Modeling;
using Xunit.Abstractions;

namespace Isoform.Tests;

/// <summary>H4: fillets are true constant-radius rolling-ball surfaces, not smooth-min blends.</summary>
public class FilletTests(ITestOutputHelper output)
{
    const double Tolerance = 1e-7;
    const double R = 2;

    [Theory]
    [InlineData(90)]
    [InlineData(60)]
    [InlineData(120)]
    public void Plane_plane_fillet_is_a_cylinder_of_radius_r_around_the_ball_centre_line(double dihedral)
    {
        // A plane cut through the top edge at x = 90 makes the requested angle with the top face.
        var tilt = (180 - dihedral) * Math.PI / 180;
        double[] normal = [Math.Sin(tilt), 0, Math.Cos(tilt)];
        var doc = new PartDocument(
        [
            new Block("plate", BooleanOp.New, [0, 0, 0], [100, 50, 10]),
            new PlaneCut("cut", [90, 0, 10], normal),
            new Fillet("fillet", ["plate/z+", "cut/face"], R),
        ]);

        var result = Regenerate(doc);

        // Ball centre line: inside both planes at depth r. It runs along y.
        var nTop = new Vector3D(0, 0, 1);
        var nCut = new Vector3D(normal[0], normal[1], normal[2]);
        var c = nTop.Dot(nCut);
        var edgePoint = new Point3D(90, 0, 10);
        var centre = edgePoint - (nTop + nCut) * (R / (1 + c));
        nTop.Dot(centre - edgePoint).Should().BeApproximately(-R, 1e-12, "the ball centre is at depth r below the top");
        nCut.Dot(centre - edgePoint).Should().BeApproximately(-R, 1e-12, "and at depth r behind the cut face");
        var deviation = FilletVertices(result).Max(p => Math.Abs(Math.Sqrt(Sq(p.X - centre.X) + Sq(p.Z - centre.Z)) - R));

        output.WriteLine($"{dihedral}°: max deviation from rolling-ball surface {deviation:E2} mm");
        deviation.Should().BeLessThan(Tolerance);
        result.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Hole_edge_fillet_is_a_torus_of_tube_radius_r()
    {
        var doc = new PartDocument(
        [
            new Block("plate", BooleanOp.New, [0, 0, 0], [100, 50, 10]),
            new Hole("hole", [30, 25], 10),
            new Fillet("fillet", ["plate/z+", "hole/wall"], R),
        ]);

        var result = Regenerate(doc);

        // Tube centre circle: radius 5 + r around the hole axis, at depth r below the top.
        var deviation = FilletVertices(result).Max(p =>
        {
            var rho = Math.Sqrt(Sq(p.X - 30) + Sq(p.Y - 25));
            return Math.Abs(Math.Sqrt(Sq(rho - (5 + R)) + Sq(p.Z - (10 - R))) - R);
        });

        output.WriteLine($"torus: max deviation {deviation:E2} mm");
        deviation.Should().BeLessThan(Tolerance);
        result.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Fillet_meets_its_faces_tangentially()
    {
        var result = Regenerate(EditSuite.Base);
        var field = result.Part.Field!;
        var fillet = result.Part.Surfaces.Single(s => s.Source == "fillet/face").Id;
        var top = result.Part.Surfaces.Single(s => s.Source == "plate/z+").Id;

        // Just on the fillet side of the tangent line (x = 98, z = 10), normals match the top face's.
        var onFillet = field.SampleSurface(fillet, new Point3D(98.0001, 25, 10))!.Value.Gradient;
        var onTop = field.SampleSurface(top, new Point3D(97.9999, 25, 10))!.Value.Gradient;

        onFillet.Dot(onTop).Should().BeApproximately(1, 1e-6);
    }

    static FrepResult Regenerate(PartDocument doc) => new FrepModeler(new ModelingSettings { CellSize = 0.25 }).Regenerate(doc);

    /// <summary>Vertices whose quads all belong to the fillet, so none sits on a neighbouring face.</summary>
    static List<Point3D> FilletVertices(FrepResult result)
    {
        var fillet = result.Part.Surfaces.Single(s => s.Source == "fillet/face").Id;
        var owners = new Dictionary<int, HashSet<int>>();
        foreach (var q in result.Mesh!.Quads)
            foreach (var v in new[] { q.A, q.B, q.C, q.D })
                (owners.TryGetValue(v, out var s) ? s : owners[v] = []).Add(q.Owner);
        var vertices = owners.Where(kv => kv.Value.All(o => o == fillet)).Select(kv => result.Mesh.Vertices[kv.Key]).ToList();
        vertices.Should().NotBeEmpty();
        return vertices;
    }

    static double Sq(double x) => x * x;
}
