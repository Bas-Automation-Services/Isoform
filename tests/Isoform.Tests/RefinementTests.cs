using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Geometry;
using Isoform.Meshing;
using Isoform.Modeling;

namespace Isoform.Tests;

/// <summary>
/// Local refinement: boxes of the part meshed with a finer cell than the rest. Inside them walls and gaps down to two
/// of the finer cells are resolved; away from them the mesh is the one without them; across their border it stays
/// closed.
/// </summary>
public class RefinementTests
{
    static readonly ModelingSettings Settings = new();

    // A 20 × 20 × 10 box with a pocket that leaves walls of 0.3 mm and a floor of 1 mm.
    static readonly PartDocument Pocketed = new(
    [
        new Block("box", BooleanOp.New, [0, 0, 0], [20, 20, 10]),
        new Block("pocket", BooleanOp.Cut, [0.3, 0.3, 1], [19.4, 19.4, 10]),
    ]);

    static (Mesh Mesh, Topology Topology) Mesh(PartDocument doc, double cell, IReadOnlyList<(Box3D, double)>? refine = null)
    {
        var part = new Compiler(Settings).Compile(doc);
        var body = part.Bodies.Single();
        var mesh = DualContouring.Plan(body.Field, body.Min, body.Max, cell, Settings.ChordTolerance, refine: refine).Build();
        return (mesh, TopologyBuilder.Build(mesh, TopologyBuilder.Oriented(part.Surfaces, body.Field), Settings.Tolerance, body.Field).Topology);
    }

    static (int, int, int, int) Counts(Topology t) => (t.Faces.Count, t.Edges.Count, t.Vertices.Count, t.BodyCount);

    [Fact]
    public void A_zone_resolves_walls_two_of_its_cells_thick()
    {
        var everywhere = new Box3D(new Point3D(-1, -1, -1), new Point3D(21, 21, 11));
        var coarse = Mesh(Pocketed, 0.5);
        var refined = Mesh(Pocketed, 0.5, [(everywhere, 0.125)]);
        var uniform = Mesh(Pocketed, 0.125);

        coarse.Mesh.ThinSpots.Should().NotBeEmpty("0.3 mm walls are below two 0.5 mm cells");
        refined.Mesh.ThinSpots.Should().BeEmpty();
        refined.Mesh.IsWatertight().Should().BeTrue();
        Counts(refined.Topology).Should().Be((11, 24, 16, 1)).And.Be(Counts(uniform.Topology));
    }

    [Fact]
    public void Away_from_a_zone_the_mesh_is_unchanged_and_across_it_closed()
    {
        var corner = new Box3D(new Point3D(-1, -1, -1), new Point3D(4, 4, 11));
        var coarse = Mesh(Pocketed, 0.5);
        var refined = Mesh(Pocketed, 0.5, [(corner, 0.125)]);

        refined.Mesh.IsWatertight().Should().BeTrue();
        // A vertex depends on the cells round it only: three coarse cells clear of the zone nothing moved.
        static bool Far(Point3D p) => p.X > 4 + 1.5 || p.Y > 4 + 1.5;
        var before = coarse.Mesh.Vertices.Where(Far).Select(p => (p.X, p.Y, p.Z)).Order().ToList();
        var after = refined.Mesh.Vertices.Where(Far).Select(p => (p.X, p.Y, p.Z)).Order().ToList();
        after.Should().Equal(before);
        refined.Mesh.ThinSpots.Should().NotContain(s => s.Location.X < 4 - 0.5 && s.Location.Y < 4 - 0.5, "inside the zone the walls are resolved");
        refined.Mesh.ThinSpots.Any(s => Far(s.Location)).Should().BeTrue("outside it they are not");
    }

    [Fact]
    public void Walls_below_the_resolution_are_refined_and_built()
    {
        var coarse = new FrepModeler(Settings with { Refinement = 0 }).Regenerate(Pocketed);
        var refined = new FrepModeler(Settings).Regenerate(Pocketed);

        coarse.Diagnostics.Should().Contain(d => d.Code == Diagnostic.BelowResolution);
        refined.Diagnostics.Should().BeEmpty();
        Counts(refined.Topology!).Should().Be((11, 24, 16, 1));
        refined.Mesh!.IsWatertight().Should().BeTrue();
        MassProperties.Of(refined.Part.Field!, refined.Mesh).Volume.Should().BeApproximately(20 * 20 * 10 - 19.4 * 19.4 * 9, 1e-6);
    }

    [Fact]
    public void A_small_hole_is_refined_and_built()
    {
        var doc = new PartDocument([new Block("plate", BooleanOp.New, [0, 0, 0], [20, 10, 4]), new Hole("pin", [10, 5], 0.6)]);

        var coarse = new FrepModeler(Settings with { Refinement = 0 }).Regenerate(doc);
        var refined = new FrepModeler(Settings).Regenerate(doc);

        coarse.Diagnostics.Should().ContainSingle(d => d.FeatureId == "pin" && d.Code == Diagnostic.BelowResolution);
        refined.Diagnostics.Should().BeEmpty();
        Counts(refined.Topology!).Should().Be((7, 14, 8, 1));
    }

    [Fact]
    public void What_needs_more_than_the_refinement_allows_stays_reported()
    {
        var doc = new PartDocument(
        [
            new Block("box", BooleanOp.New, [0, 0, 0], [20, 20, 10]),
            new Block("pocket", BooleanOp.Cut, [0.01, 0.3, 1], [19.69, 19.4, 10]),
        ]);

        var r = new FrepModeler(Settings).Regenerate(doc);

        r.Diagnostics.Should().Contain(d => d.Code == Diagnostic.BelowResolution && d.Message.Contains("0.01 mm apart"));
    }

    [Fact]
    public void Regions_with_zones_mesh_apart_bit_for_bit()
    {
        var part = new Compiler(Settings).Compile(Pocketed);
        var body = part.Bodies.Single();
        var plan = DualContouring.Plan(body.Field, body.Min, body.Max, 0.5, Settings.ChordTolerance,
            refine: [(new Box3D(new Point3D(-1, -1, -1), new Point3D(4, 4, 11)), 0.125), (new Box3D(new Point3D(15, 15, 5), new Point3D(21, 21, 11)), 0.25)]);
        var once = plan.Build();
        var apart = plan.Assemble(plan.Regions.Reverse().Select(plan.Mesh).Reverse().ToList());

        apart.Vertices.Should().Equal(once.Vertices);
        apart.Quads.Should().Equal(once.Quads);
    }
}
