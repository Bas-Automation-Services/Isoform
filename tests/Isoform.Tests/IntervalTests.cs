using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Geometry;
using Isoform.Modeling;

namespace Isoform.Tests;

/// <summary>K1: Bounds(box) is a safe enclosure of the field over the whole box.</summary>
public class IntervalTests
{
    static IEnumerable<PartDocument> Documents =>
    [
        EditSuite.Base,
        new([.. EditSuite.Base.Features, new Shell("shell", 1.5)]),
        new(
        [
            new Block("plate", BooleanOp.New, [0, 0, 0], [100, 50, 10]),
            new PlaneCut("cut", [90, 0, 10], [Math.Sin(2 * Math.PI / 3), 0, Math.Cos(2 * Math.PI / 3)]),
            new Fillet("fillet", ["plate/z+", "cut/face"], 2),
            new Block("tab", BooleanOp.Join, [-20, 10, 0], [30, 30, 10]),
            new Block("slot", BooleanOp.Cut, [60, 30, -1], [10, 30, 12]),
        ]),
        // Blends inside blends, as the pump stand's top and legs have them.
        new(
        [
            new Block("plate", BooleanOp.New, [0, 0, 0], [100, 50, 10]),
            new Extrude("knob", BooleanOp.Join, new SketchPlane([0, 0, 10], [1, 0, 0], [0, 0, 1]), Circle(30, 25, 10), 0, 15, Blend: 4),
            new Extrude("lobe", BooleanOp.Join, new SketchPlane([0, 0, 10], [1, 0, 0], [0, 0, 1]), Circle(42, 25, 7), 0, 12, Blend: 6),
            new Block("groove", BooleanOp.Cut, [60, -1, 6], [12, 52, 10], Blend: 3),
        ]),
    ];

    static Profile Circle(double cx, double cy, double r) => new([cx - r, cy], [new ProfileEdge("a", [cx + r, cy], 1), new ProfileEdge("b", [cx - r, cy], 1)]);

    [Fact]
    public void Blend_bounds_are_the_blend_of_the_bounds()
    {
        // The smooth min rises with both arguments: over a box its lowest and highest are at the corners where both are.
        Surface Plane(int id, Vector3D normal) => new(id, $"p{id}", SurfaceKind.Plane, new Point3D(0, 0, 0), normal);
        var blend = new BlendField(new LeafField(Plane(0, new Vector3D(0, 0, 1))), new LeafField(Plane(1, new Vector3D(1, 0, 0))), 10, Plane(2, new Vector3D(0, 1, 0)), union: true);

        var bounds = blend.Bounds(new Box3D(new Point3D(5, 0, 0.3), new Point3D(6, 1, 1.3)));

        bounds.Lo.Should().BeApproximately(blend.Value(new Point3D(5, 0, 0.3)), 1e-12);
        bounds.Hi.Should().BeApproximately(blend.Value(new Point3D(6, 0, 1.3)), 1e-12);
        bounds.Lo.Should().BeApproximately(0.3 - 5.3 * 5.3 / 40, 1e-12, "k/4 below the plain min would be −2.2");
    }

    [Fact]
    public void Bounds_contain_every_value_inside_the_box()
    {
        var rng = new Random(7);
        foreach (var doc in Documents)
        {
            var field = new Compiler(new ModelingSettings()).Compile(doc).Field!;
            for (var b = 0; b < 4000; b++)
            {
                var size = Math.Pow(10, rng.NextDouble() * 3.7 - 2); // 0.01 .. 50 mm
                var min = new Point3D(rng.NextDouble() * 140 - 30, rng.NextDouble() * 90 - 20, rng.NextDouble() * 40 - 15);
                var box = new Box3D(min, new Point3D(min.X + size * rng.NextDouble(), min.Y + size * rng.NextDouble(), min.Z + size * rng.NextDouble()));
                var bounds = field.Bounds(box);
                for (var i = 0; i < 50; i++)
                {
                    var p = new Point3D(
                        box.Min.X + (box.Max.X - box.Min.X) * rng.NextDouble(),
                        box.Min.Y + (box.Max.Y - box.Min.Y) * rng.NextDouble(),
                        box.Min.Z + (box.Max.Z - box.Min.Z) * rng.NextDouble());
                    var v = field.Value(p);
                    v.Should().BeInRange(bounds.Lo - 1e-12, bounds.Hi + 1e-12);
                }
            }
        }
    }

    [Fact]
    public void Simplified_field_is_exactly_the_full_field_inside_its_box()
    {
        var rng = new Random(11);
        foreach (var doc in Documents)
        {
            var field = new Compiler(new ModelingSettings()).Compile(doc).Field!;
            for (var b = 0; b < 3000; b++)
            {
                var size = Math.Pow(10, rng.NextDouble() * 3 - 1.5); // 0.03 .. 30 mm
                var min = new Point3D(rng.NextDouble() * 140 - 30, rng.NextDouble() * 90 - 20, rng.NextDouble() * 40 - 15);
                var box = new Box3D(min, new Point3D(min.X + size, min.Y + size, min.Z + size));
                var simple = field.Simplify(box);
                for (var i = 0; i < 30; i++)
                {
                    var p = new Point3D(box.Min.X + size * rng.NextDouble(), box.Min.Y + size * rng.NextDouble(), box.Min.Z + size * rng.NextDouble());
                    var (full, reduced) = (field.Sample(p), simple.Sample(p));
                    reduced.Value.Should().Be(full.Value);
                    reduced.Owner.Should().Be(full.Owner);
                    (reduced.Gradient - full.Gradient).Length.Should().Be(0);
                    simple.Value(p).Should().Be(full.Value, "the value-only path matches Sample bit for bit");
                }
            }
        }
    }

    [Fact]
    public void Plane_bounds_are_exact()
    {
        var plane = new LeafField(new Surface(0, "p", SurfaceKind.Plane, new Point3D(0, 0, 0), new Vector3D(0, 0, 1)));

        var bounds = plane.Bounds(new Box3D(new Point3D(-1, -1, 2), new Point3D(1, 1, 5)));

        (bounds.Lo, bounds.Hi).Should().Be((2.0, 5.0));
    }

    [Fact]
    public void Boxes_far_from_the_surface_are_certified_inside_or_outside()
    {
        var field = new Compiler(new ModelingSettings()).Compile(EditSuite.Base).Field!;

        field.Bounds(new Box3D(new Point3D(40, 5, 2), new Point3D(60, 15, 8))).Hi.Should().BeNegative("solid material, away from the hole");
        field.Bounds(new Box3D(new Point3D(28, 23, 2), new Point3D(32, 27, 8))).Lo.Should().BePositive("inside the hole");
        field.Bounds(new Box3D(new Point3D(110, 0, 0), new Point3D(120, 50, 10))).Lo.Should().BePositive("beyond the part");
    }
}
