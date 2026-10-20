using BasAutomation.Geometry;
using Isoform.Geometry;
using Isoform.Step;

namespace Isoform.Tests;

/// <summary>K6: the closed-form surface intersections behind exact edges and vertices.</summary>
public class IntersectionTests
{
    static Surface Plane(double x, double y, double z, double nx, double ny, double nz) =>
        new(0, "p", SurfaceKind.Plane, new Point3D(x, y, z), new Vector3D(nx, ny, nz).Normalized());

    static Surface Cylinder(double x, double y, double z, double ax, double ay, double az, double r) =>
        new(0, "c", SurfaceKind.Cylinder, new Point3D(x, y, z), new Vector3D(ax, ay, az).Normalized(), r);

    public static IEnumerable<object[]> Pairs()
    {
        // Plane–plane, plane along a cylinder (two lines), tangent, perpendicular (circle), oblique (ellipse),
        // parallel cylinders, equal cylinders crossing (mitre), and coaxial revolution pairs.
        yield return ["planes", Plane(0, 0, 10, 0, 0, 1), Plane(100, 0, 0, 1, 0, 0), new Point3D(100, 20, 10.3)];
        yield return ["plane along cylinder", Plane(0, 2, 0, 0, 1, 0), Cylinder(20, 5, 0, 0, 0, 1, 5), new Point3D(24, 2, 3)];
        yield return ["tangent plane", Plane(0, 0, 0, 0, -1, 0), Cylinder(20, 5, 0, 0, 0, 1, 5), new Point3D(20.4, 0.01, 2)];
        yield return ["tangent cylinders", Cylinder(0, 0, 0, 0, 0, 1, 5), Cylinder(8, 0, 0, 0, 0, 1, 3), new Point3D(5, 0.1, 1)];
        yield return ["disc on a shaft", Plane(0, 0, 20, 0, 0, 1), Cylinder(0, 0, 0, 0, 0, 1, 10), new Point3D(7, 7, 20)];
        yield return ["oblique cut", Plane(20, 25, 10, Math.Tan(Math.PI / 6), 0, 1), Cylinder(30, 25, 0, 0, 0, 1, 5), new Point3D(35, 25, 1.4)];
        yield return ["mitre", Cylinder(0, 3, 3, 1, 0, 0, 3), Cylinder(3, 0, 3, 0, 1, 0, 3), new Point3D(1, 1, 1)];
        yield return ["cone and cylinder", new Surface(0, "k", SurfaceKind.Cone, new Point3D(0, 0, 30), new Vector3D(0, 0, -1), Angle: Math.Atan(0.5)),
            Cylinder(0, 0, 0, 0, 0, 1, 5), new Point3D(5, 0, 20)];
        yield return ["torus and plane", new Surface(0, "t", SurfaceKind.Torus, new Point3D(0, 0, 8), new Vector3D(0, 0, 1), 7, 2),
            Plane(0, 0, 10, 0, 0, 1), new Point3D(7, 0, 10)];
        yield return ["sphere and cylinder", new Surface(0, "s", SurfaceKind.Sphere, new Point3D(0, 0, 20), new Vector3D(0, 0, 1), 5),
            Cylinder(0, 0, 0, 0, 0, 1, 4), new Point3D(4, 0, 23)];
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Curve_lies_on_both_surfaces_through_the_seed_branch(string name, Surface a, Surface b, Point3D seed)
    {
        var curve = SurfaceIntersection.Curve(a, b, seed);

        curve.Should().NotBeNull(name);
        var t0 = curve!.Parameter(seed);
        foreach (var dt in new[] { -0.3, -0.1, 0, 0.1, 0.3 })
        {
            var p = curve.At(t0 + dt);
            Math.Abs(a.Distance(p)).Should().BeLessThan(1e-12, name);
            Math.Abs(b.Distance(p)).Should().BeLessThan(1e-12, name);
        }
        (curve.At(t0) - seed).Length.Should().BeLessThan(1, $"{name}: the branch through the seed");
    }

    [Fact]
    public void A_tangent_edge_is_the_contact_line_exactly()
    {
        var line = (Line)SurfaceIntersection.Curve(Plane(0, 0, 0, 0, -1, 0), Cylinder(20, 5, 0, 0, 0, 1, 5), new Point3D(20.4, 0.01, 2))!;

        line.Origin.X.Should().BeApproximately(20, 1e-12);
        line.Origin.Y.Should().BeApproximately(0, 1e-12);
        Math.Abs(line.Direction.Z).Should().BeApproximately(1, 1e-15);
    }

    [Fact]
    public void A_vertex_is_where_an_edge_crosses_a_third_surface()
    {
        var edge = SurfaceIntersection.Curve(Plane(0, 0, 10, 0, 0, 1), Cylinder(30, 25, 0, 0, 0, 1, 5), new Point3D(35, 25, 10))!;

        var hit = SurfaceIntersection.Cross(edge, Plane(0, 25, 0, 0, 1, 0), new Point3D(34.8, 25.2, 10))!.Value;

        (hit.Point - new Point3D(35, 25, 10)).Length.Should().BeLessThan(1e-12);
        hit.Transversality.Should().BeApproximately(1, 1e-12);
    }

    [Fact]
    public void Skew_or_unequal_cylinders_have_no_closed_form()
    {
        SurfaceIntersection.Curve(Cylinder(0, 0, 0, 1, 0, 0, 3), Cylinder(0, 0, 1, 0, 1, 0, 3), new Point3D(0, 0, 3)).Should().BeNull();
        SurfaceIntersection.Curve(Cylinder(0, 0, 0, 1, 0, 0, 3), Cylinder(0, 0, 0, 0, 1, 0, 2), new Point3D(0, 2, 2)).Should().BeNull();
    }
}
