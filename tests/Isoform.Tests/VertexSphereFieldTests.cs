using BasAutomation.Geometry;
using Isoform.Geometry;

namespace Isoform.Tests;

/// <summary>K1 for the corner sphere: the exact rounding of a corner of three planes, as its own field.</summary>
public class VertexSphereFieldTests
{
    static readonly Surface Fillet = new(10, "f/face", SurfaceKind.Fillet, new Point3D(0, 0, 0), new Vector3D(0, 0, 1), 2);

    // The corner x, y, z ≤ 0 rounded by r = 2, and its three fillets.
    static (VertexSphereField Corner, MaxField Mitre) Corner(double r = 2)
    {
        LeafField Plane(int id, Vector3D n) => new(new Surface(id, "", SurfaceKind.Plane, new Point3D(0, 0, 0), n));
        var (x, y, z) = (Plane(1, new(1, 0, 0)), Plane(2, new(0, 1, 0)), Plane(3, new(0, 0, 1)));
        RoundedEdgeField[] edges = [new(x, y, r, Fillet with { Id = 4 }), new(y, z, r, Fillet with { Id = 5 }), new(z, x, r, Fillet with { Id = 6 })];
        var ball = VertexSphereField.Ball(edges, 7, "f/corner")!;
        return (new VertexSphereField(edges, ball), new MaxField(edges));
    }

    [Fact]
    public void Ball_sits_r_inside_all_three_planes()
    {
        var (corner, _) = Corner();
        (corner.Sphere.Origin - new Point3D(-2, -2, -2)).Length.Should().BeLessThan(1e-15);
        corner.Sphere.Radius.Should().Be(2);
    }

    [Fact]
    public void Field_is_the_distance_to_the_corner_moved_in_by_r_less_r()
    {
        var (corner, mitre) = Corner();
        var rng = new Random(5);
        for (var i = 0; i < 20_000; i++)
        {
            var p = new Point3D(rng.NextDouble() * 10 - 6, rng.NextDouble() * 10 - 6, rng.NextDouble() * 10 - 6);
            // Outside the corner moved in by 2 the distance to it; inside it the planes' max.
            double Gap(double c) => Math.Max(0, c + 2);
            var (gx, gy, gz) = (Gap(p.X), Gap(p.Y), Gap(p.Z));
            var exact = gx + gy + gz > 0 ? Math.Sqrt(gx * gx + gy * gy + gz * gz) - 2 : Math.Max(p.X, Math.Max(p.Y, p.Z));
            corner.Value(p).Should().BeApproximately(exact, 1e-12);
            corner.Value(p).Should().BeGreaterThanOrEqualTo(mitre.Value(p));
        }
    }

    [Fact]
    public void Field_is_1_lipschitz_bounded_and_simplifies_exactly_in_an_intersection()
    {
        var (corner, mitre) = Corner();
        // The block −10 ≤ x, y, z ≤ 0 with the far corner rounded, the sphere after the fillets.
        var walls = new[] { new Vector3D(-1, 0, 0), new Vector3D(0, -1, 0), new Vector3D(0, 0, -1) }
            .Select((n, i) => (Field)new LeafField(new Surface(20 + i, "", SurfaceKind.Plane, new Point3D(-10, -10, -10), n)));
        var field = new MaxField([.. walls, .. mitre.Children, corner]);
        FieldProperties.Check(new Modeling.CompiledPart(field, [], new Point3D(-10, -10, -10), new Point3D(0, 0, 0), []));
    }
}
