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
    ];

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
