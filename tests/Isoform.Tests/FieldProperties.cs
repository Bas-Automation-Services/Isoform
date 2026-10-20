using BasAutomation.Geometry;
using Isoform.Modeling;

namespace Isoform.Tests;

/// <summary>
/// The K1 guarantees every operation must keep: the field is 1-Lipschitz, Value matches Sample, Bounds encloses
/// every value in a box, and Simplify gives the same value, owner and gradient bit for bit inside its box.
/// </summary>
public static class FieldProperties
{
    public static void Check(CompiledPart part, int seed = 3)
    {
        var field = part.Field!;
        var (lo, hi) = (part.Min - new Vector3D(5, 5, 5), part.Max + new Vector3D(5, 5, 5));
        var rng = new Random(seed);
        Point3D Random() => new(lo.X + rng.NextDouble() * (hi.X - lo.X), lo.Y + rng.NextDouble() * (hi.Y - lo.Y), lo.Z + rng.NextDouble() * (hi.Z - lo.Z));

        for (var i = 0; i < 50_000; i++)
        {
            var p = Random();
            var q = rng.NextDouble() < 0.5 ? Random() : p + new Vector3D(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5, rng.NextDouble() - 0.5);
            Math.Abs(field.Value(p) - field.Value(q)).Should().BeLessThanOrEqualTo((p - q).Length * (1 + 1e-12) + 1e-12);
            field.Value(p).Should().Be(field.Sample(p).Value);
        }

        for (var b = 0; b < 2000; b++)
        {
            var size = Math.Pow(10, rng.NextDouble() * 3 - 1.5);
            var min = Random();
            var box = new Box3D(min, new Point3D(min.X + size, min.Y + size, min.Z + size));
            var bounds = field.Bounds(box);
            var simple = field.Simplify(box);
            for (var i = 0; i < 20; i++)
            {
                var p = new Point3D(min.X + size * rng.NextDouble(), min.Y + size * rng.NextDouble(), min.Z + size * rng.NextDouble());
                var (full, reduced) = (field.Sample(p), simple.Sample(p));
                full.Value.Should().BeInRange(bounds.Lo - 1e-12, bounds.Hi + 1e-12);
                reduced.Value.Should().Be(full.Value);
                reduced.Owner.Should().Be(full.Owner);
                (reduced.Gradient - full.Gradient).Length.Should().Be(0);
            }
        }
    }
}
