using BasAutomation.Geometry;
using Xunit.Abstractions;
using static Isoform.Brep.Tests.Shapes;

namespace Isoform.Brep.Tests;

/// <summary>Fillets and chamfers on the kernel alone, checked by their exact volume.</summary>
public class BlendTests(ITestOutputHelper output)
{
    static Body Block() => Extrusion.Extrude(new Region(Rectangle(0, 0, 100, 50)), RigidTransform3D.Identity, 0, 10).Result!.Body;

    static double Spandrel(double r) => r * r * (1 - Math.PI / 4);

    // The face of a body whose plane has this outward normal and passes through p.
    static FaceId Face(Body body, Vector3D normal, Point3D p) => new(Enumerable.Range(0, body.Faces.Length).Single(f =>
        body.Faces[f].Surface is Plane pl && (pl.Frame.ZAxis * (body.Faces[f].SameSense ? 1 : -1)).Dot(normal) > 0.999 && Math.Abs((p - pl.Frame.Origin).Dot(pl.Frame.ZAxis)) < 1e-9));

    [Theory]
    [InlineData(BlendKind.Fillet, 2)]
    [InlineData(BlendKind.Chamfer, 3)]
    public void A_top_edge(BlendKind kind, double size)
    {
        var body = Block();
        var (r, _, problem) = Blends.Apply(body, Face(body, new Vector3D(0, 0, 1), new Point3D(0, 0, 10)), Face(body, new Vector3D(1, 0, 0), new Point3D(100, 0, 0)), size, kind);
        output.WriteLine(problem ?? "");
        problem.Should().BeNull();
        var removed = kind == BlendKind.Fillet ? Spandrel(size) : size * size / 2;
        MassProperties.Volume(r!.Body).Should().BeApproximately(50000 - removed * 50, 1e-9);
        r.Body.Faces.Should().HaveCount(7);
    }

    [Fact]
    public void An_inside_corner()
    {
        var l = new ProfileLoop([new LineSegment(P(0, 0), P(40, 0)), new LineSegment(P(40, 0), P(40, 5)), new LineSegment(P(40, 5), P(5, 5)),
            new LineSegment(P(5, 5), P(5, 30)), new LineSegment(P(5, 30), P(0, 30)), new LineSegment(P(0, 30), P(0, 0))]);
        var body = Extrusion.Extrude(new Region(l), RigidTransform3D.Identity, 0, 20).Result!.Body;
        var (r, _, problem) = Blends.Apply(body, Face(body, new Vector3D(0, 1, 0), new Point3D(20, 5, 0)), Face(body, new Vector3D(1, 0, 0), new Point3D(5, 20, 0)), 3, BlendKind.Fillet);
        output.WriteLine(problem ?? "");
        problem.Should().BeNull();
        MassProperties.Volume(r!.Body).Should().BeApproximately((40 * 5 + 5 * 25 + Spandrel(3)) * 20, 1e-9);
        r.Body.Faces.Should().HaveCount(9);
    }

    [Fact]
    public void A_radius_too_large_for_the_plate_is_refused()
    {
        var body = Block();
        var (r, _, problem) = Blends.Apply(body, Face(body, new Vector3D(0, 0, 1), new Point3D(0, 0, 10)), Face(body, new Vector3D(1, 0, 0), new Point3D(100, 0, 0)), 20, BlendKind.Fillet);
        r.Should().BeNull();
        problem.Should().Contain("reaches other faces");
    }

    // Rounds the edge between the faces facing n1 and n2 through p1 and p2.
    static Body Round(Body body, Vector3D n1, Point3D p1, Vector3D n2, Point3D p2, double r)
    {
        var (result, _, problem) = Blends.Apply(body, Face(body, n1, p1), Face(body, n2, p2), r, BlendKind.Fillet);
        problem.Should().BeNull();
        return result!.Body;
    }

    [Fact]
    public void Two_fillets_meeting_at_a_corner_mitre()
    {
        var (z, x, y) = (new Vector3D(0, 0, 1), new Vector3D(1, 0, 0), new Vector3D(0, 1, 0));
        var once = Round(Block(), z, new Point3D(0, 0, 10), x, new Point3D(100, 0, 0), 2);
        var twice = Round(once, z, new Point3D(0, 0, 10), y, new Point3D(0, 50, 0), 2);
        // Each edge loses its spandrel along its whole length; the corner, where both would, gives back r³(5/3 − π/2).
        MassProperties.Volume(twice).Should().BeApproximately(50000 - Spandrel(2) * 150 + 8 * (5.0 / 3 - Math.PI / 2), 1e-9);
        twice.Edges.Select(e => e.Curve).OfType<Ellipse3>().Should().ContainSingle();
    }

    [Fact]
    public void Three_fillets_at_a_corner_are_refused()
    {
        var (z, x, y) = (new Vector3D(0, 0, 1), new Vector3D(1, 0, 0), new Vector3D(0, 1, 0));
        var body = Round(Round(Block(), z, new Point3D(0, 0, 10), x, new Point3D(100, 0, 0), 2), z, new Point3D(0, 0, 10), y, new Point3D(0, 50, 0), 2);
        var (r, _, problem) = Blends.Apply(body, Face(body, x, new Point3D(100, 0, 0)), Face(body, y, new Point3D(0, 50, 0)), 2, BlendKind.Fillet);
        r.Should().BeNull();
        problem.Should().Contain("three fillets");
    }

    static double Centroid(double r) => r * (5.0 / 6 - Math.PI / 4) / (1 - Math.PI / 4);

    static FaceId Wall(Body body) => new(Enumerable.Range(0, body.Faces.Length).Single(f => body.Faces[f].Surface is Cylinder));

    [Fact]
    public void A_hole_rim_is_rounded_by_a_torus()
    {
        var body = Extrusion.Extrude(new Region(Rectangle(0, 0, 100, 50), [Circle(30, 25, 5)]), RigidTransform3D.Identity, 0, 10).Result!.Body;
        var (r, face, problem) = Blends.Apply(body, Face(body, new Vector3D(0, 0, 1), new Point3D(0, 0, 10)), Wall(body), 2, BlendKind.Fillet);
        output.WriteLine(problem ?? "");
        problem.Should().BeNull();
        // Pappus: the spandrel round the hole, its centroid that far outside the wall.
        MassProperties.Volume(r!.Body).Should().BeApproximately(50000 - Math.PI * 250 - 2 * Math.PI * (5 + Centroid(2)) * Spandrel(2), 1e-9);
        r.Body.Faces.Should().Contain(f => f.Surface is Torus);
    }

    [Fact]
    public void A_boss_foot_is_filled_by_a_torus()
    {
        var plate = Extrusion.Extrude(new Region(Rectangle(0, 0, 100, 50)), RigidTransform3D.Identity, 0, 10).Result!.Body;
        var boss = Extrusion.Extrude(new Region(Circle(50, 25, 8)), RigidTransform3D.Identity, 10, 25).Result!.Body;
        var body = Booleans.Merge(Booleans.Unite(plate, boss).Bodies![0].Body).Body;
        var (r, _, problem) = Blends.Apply(body, Face(body, new Vector3D(0, 0, 1), new Point3D(0, 0, 10)), Wall(body), 3, BlendKind.Fillet);
        output.WriteLine(problem ?? "");
        problem.Should().BeNull();
        MassProperties.Volume(r!.Body).Should().BeApproximately(50000 + Math.PI * 64 * 15 + 2 * Math.PI * (8 + Centroid(3)) * Spandrel(3), 1e-9);
    }

    [Fact]
    public void A_boss_top_rim_is_rounded_by_a_torus()
    {
        // One torus face; Isoform's F-rep core rounds a swept profile's cap edge along the sweep and names it per side.
        var boss = Extrusion.Extrude(new Region(Circle(50, 25, 8)), RigidTransform3D.Identity, 10, 25).Result!.Body;
        var (r, _, problem) = Blends.Apply(boss, Face(boss, new Vector3D(0, 0, 1), new Point3D(0, 0, 25)), Wall(boss), 2, BlendKind.Fillet);
        problem.Should().BeNull();
        MassProperties.Volume(r!.Body).Should().BeApproximately(Math.PI * 64 * 15 - 2 * Math.PI * (8 - Centroid(2)) * Spandrel(2), 1e-9);
        r.Body.Faces.Should().HaveCount(4);
    }

    [Fact]
    public void A_stadium_top_is_rounded_all_round()
    {
        var stadium = new ProfileLoop([new LineSegment(P(40, 20), P(60, 20)), new ArcSegment(P(60, 25), 5, -Math.PI / 2, Math.PI),
            new LineSegment(P(60, 30), P(40, 30)), new ArcSegment(P(40, 25), 5, Math.PI / 2, Math.PI)]);
        var boss = Extrusion.Extrude(new Region(stadium), RigidTransform3D.Identity, 10, 18).Result!.Body;
        var (r, pieces, problem) = Blends.Loop(boss, Face(boss, new Vector3D(0, 0, 1), new Point3D(0, 0, 18)), 1, BlendKind.Fillet);
        output.WriteLine(problem ?? "");
        problem.Should().BeNull();
        pieces.Should().HaveCount(4);
        MassProperties.Volume(r!.Body).Should().BeApproximately((200 + Math.PI * 25) * 8 - 40 * Spandrel(1) - 2 * Math.PI * (5 - Centroid(1)) * Spandrel(1), 1e-9);
    }

    [Fact]
    public void A_loop_chamfer_mitres_at_the_corners()
    {
        // Each corner gives back the overlap of the two prisms, d³/3. (Isoform's F-rep core splits the corner point,
        // where four faces meet, into an edge; the solid is the same.)
        var outline = Extrusion.Extrude(new Region(Rectangle(0, 0, 60, 40)), RigidTransform3D.Identity, 0, 10).Result!.Body;
        var (r, pieces, problem) = Blends.Loop(outline, Face(outline, new Vector3D(0, 0, -1), new Point3D(0, 0, 0)), 1, BlendKind.Chamfer);
        problem.Should().BeNull();
        pieces.Should().HaveCount(4);
        MassProperties.Volume(r!.Body).Should().BeApproximately(60 * 40 * 10 - 200 * 0.5 + 4.0 / 3, 1e-9);
        r.Body.Faces.Should().HaveCount(10);
    }
}
