using Isoform.Document;
using Isoform.Modeling;
using Xunit.Abstractions;

namespace Isoform.Tests;

/// <summary>
/// K3: walls, gaps and holes from 0.2 to 3 cells, in several orientations and grid alignments. At 2 cells and up
/// the topology must be exactly right with nothing reported; below that it must be right or reported, never
/// silently wrong.
/// </summary>
public class TortureTests(ITestOutputHelper output)
{
    const double H = 0.5;
    static readonly double[] Shifts = [0, 0.13, 0.29, 0.41];
    static readonly double[] Sizes = [0.2, 0.5, 0.8, 1.2, 1.6, 2, 2.5, 3];

    sealed record Case(string Name, double Size, PartDocument Doc, (int Faces, int Edges, int Vertices) Expected);

    static Block Plate(double w, double l, double t = 5, double z = 0) => new("plate", BooleanOp.New, [0, 0, z], [w, l, t - z]);

    [Fact]
    public void Walls_between_a_pocket_and_a_face()
    {
        // Plate 6 + pocket 5 faces; 12 + 12 edges; 8 + 8 corners.
        var expected = (11, 24, 16);
        Check(from f in Sizes
              from s in Shifts
              let t = f * H
              from c in new[]
              {
                  new Case($"x wall t={t} shift={s}", t, new([Plate(20 + s, 10), Pocket([20 + s - t - 6, 2, 2], [6, 6, 4])]), expected),
                  new Case($"y wall t={t} shift={s}", t, new([Plate(20, 10 + s), Pocket([7, 10 + s - t - 6, 2], [6, 6, 4])]), expected),
                  new Case($"z floor t={t} shift={s}", t, new([Plate(20, 10, z: -s), Pocket([7, 2, t - s], [6, 6, 6])]), expected),
              }
              select c);

        static Block Pocket(double[] origin, double[] size) => new("pocket", BooleanOp.Cut, origin, size);
    }

    [Fact]
    public void Walls_between_a_hole_and_a_slanted_face()
    {
        // The slanted cut leaves 5 side faces round the top and bottom: 8 faces, 15 + 2 hole edges, 10 corners.
        var expected = (8, 17, 10);
        Check(from f in Sizes
              from s in Shifts.Take(2)
              from deg in new[] { 30.0, 45.0 }
              let t = f * H
              let a = deg * Math.PI / 180
              let n = (X: Math.Cos(a), Y: Math.Sin(a))
              let onFace = (X: 16 + s + 2.5 * n.Y, Y: 8 - 2.5 * n.X)
              select new Case($"hole/{deg}° face t={t} shift={s}", t, new(
              [
                  Plate(20 + s, 10),
                  new PlaneCut("cut", [16 + s, 8, 0], [n.X, n.Y, 0]),
                  new Hole("hole", [onFace.X - n.X * (2 + t), onFace.Y - n.Y * (2 + t)], 4),
              ]), expected));
    }

    [Fact]
    public void Walls_between_two_holes()
    {
        var expected = (8, 16, 8);
        Check(from f in Sizes
              from s in Shifts.Take(2)
              from deg in new[] { 0.0, 30.0 }
              let t = f * H
              let a = deg * Math.PI / 180
              select new Case($"two holes {deg}° t={t} shift={s}", t, new(
              [
                  Plate(20 + s, 14),
                  new Hole("h1", [7 + s, 5], 4),
                  new Hole("h2", [7 + s + (4 + t) * Math.Cos(a), 5 + (4 + t) * Math.Sin(a)], 4),
              ]), expected));
    }

    [Fact]
    public void Gaps_of_a_slot()
    {
        // The slot splits the top in two: 10 faces, 24 edges, 16 corners.
        var expected = (10, 24, 16);
        Check(from f in Sizes
              from s in Shifts
              let g = f * H
              select new Case($"slot g={g} shift={s}", g, new(
              [
                  Plate(20, 10),
                  new Block("slot", BooleanOp.Cut, [10 + s, -1, 2], [g, 12, 4]),
              ]), expected));
    }

    [Fact]
    public void Small_holes()
    {
        var expected = (7, 14, 8);
        Check(from f in Sizes
              from s in Shifts
              let d = f * H
              select new Case($"hole d={d} shift={s}", d, new([Plate(20, 10), new Hole("hole", [10 + s, 5 + s], d)]), expected));
    }

    [Fact]
    public void Thin_plates()
    {
        var expected = (6, 12, 8);
        Check(from f in new[] { 0.2, 0.35, 0.5, 0.7, 0.85, 1.0, 1.2, 1.5, 1.8, 2, 2.5, 3 }
              let t = f * H
              select new Case($"plate t={t}", t, new([Plate(20, 10, t)]), expected));
    }

    void Check(IEnumerable<Case> cases)
    {
        var failures = new List<string>();
        foreach (var c in cases)
        {
            var result = new FrepModeler(new ModelingSettings { CellSize = H }).Regenerate(c.Doc);
            var topology = result.Topology!;
            var got = (topology.Faces.Count, topology.Edges.Count, topology.Vertices.Count);
            var correct = got == c.Expected && topology.BodyCount == 1 && result.Mesh!.IsWatertight();
            var reported = result.Diagnostics.Any(d => d.Code == Diagnostic.BelowResolution);
            var ok = c.Size >= 2 * H ? correct && !reported : correct || reported;
            output.WriteLine($"{(ok ? "  " : "!!")} {c.Name,-34} {c.Size / H,4:F1} cells  "
                + (correct ? "correct" : $"wrong {got}, {topology.BodyCount} bodies") + (reported ? ", reported" : ""));
            if (!ok) failures.Add(c.Name);
        }
        failures.Should().BeEmpty();
    }
}
