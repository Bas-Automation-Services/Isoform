using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Meshing;
using Isoform.Modeling;
using Xunit.Abstractions;

namespace Isoform.Tests;

/// <summary>H3a: the mesh is a faithful, measurable approximation of the exact implicit solid.</summary>
public class MeshQualityTests(ITestOutputHelper output)
{
    // Small part so fine grids stay cheap: 20×10×5 plate, Ø6 hole, R2 fillet.
    static readonly PartDocument Small = new(
    [
        new Block("plate", BooleanOp.New, [0, 0, 0], [20, 10, 5]),
        new Hole("hole", [8, 5], 6),
        new Fillet("fillet", ["plate/z+", "plate/x+"], 2),
    ]);

    static double SmallVolume => 20 * 10 * 5 - Math.PI * 9 * 5 - 2 * 2 * (1 - Math.PI / 4) * 10;

    [Fact]
    public void Mesh_is_watertight_and_encloses_the_exact_volume()
    {
        var result = Regenerate(Small, 0.25);

        result.Mesh!.IsWatertight().Should().BeTrue();
        var volume = SignedVolume(result.Mesh);
        output.WriteLine($"volume {volume:F6} exact {SmallVolume:F6} rel {(volume - SmallVolume) / SmallVolume:E2}");
        volume.Should().BeApproximately(SmallVolume, SmallVolume * 1e-3, "positive volume also proves outward winding");
    }

    [Fact]
    public void With_a_tight_chord_tolerance_error_shrinks_quadratically_with_the_cell_size()
    {
        var errors = new[] { 0.5, 0.25, 0.125 }.Select(h =>
        {
            var r = new FrepModeler(new ModelingSettings { CellSize = h, ChordTolerance = 1e-6 }).Regenerate(Small);
            var (vertex, chord) = Errors(r);
            output.WriteLine($"h {h}: max vertex error {vertex:E2}, max chord error {chord:E2}");
            return (vertex, chord);
        }).ToList();

        errors[1].chord.Should().BeLessThan(errors[0].chord / 3, "halving h should cut chord error ~4×");
        errors[2].chord.Should().BeLessThan(errors[1].chord / 3);
        errors.Should().OnlyContain(e => e.vertex <= 1e-7, "vertices lie on the exact surface within the modeling tolerance");
    }

    [Fact]
    public void Coarse_cells_on_curved_surfaces_keep_the_chord_tolerance()
    {
        // Cells on a hole wall may grow to √(4rε), up to 8 times the 0.25 mm finest cell. R25 at 0.04 hits that limit.
        foreach (var (d, chord) in new[] { (50.0, 0.04), (30, 0.04), (30, 0.01), (30, 0.0025) })
        {
            var doc = new PartDocument([new Block("plate", BooleanOp.New, [0, 0, 0], [d + 10, d + 10, 10]), new Hole("hole", [d / 2 + 5, d / 2 + 5], d)]);
            var r = new FrepModeler(new ModelingSettings { CellSize = 0.25, ChordTolerance = chord }).Regenerate(doc);
            var (vertex, error) = Errors(r);
            output.WriteLine($"Ø{d} at chord tolerance {chord}: max chord error {error:E2}, {r.Mesh!.Quads.Count:N0} polygons");
            error.Should().BeLessThanOrEqualTo(chord);
            vertex.Should().BeLessThanOrEqualTo(1e-7);
        }
    }

    [Fact]
    public void Adaptive_mesh_has_the_uniform_topology_with_far_fewer_polygons()
    {
        foreach (var doc in new[] { EditSuite.Base, new([.. EditSuite.Base.Features, new Shell("shell", 1.5)]) })
        {
            var part = new Compiler(new ModelingSettings()).Compile(doc);
            var adaptive = DualContouring.Build(part.Field!, part.Min, part.Max, 0.25, 0.01);
            var uniform = DualContouring.Build(part.Field!, part.Min, part.Max, 0.25, 0.01, adaptive: false);
            var (a, u) = (TopologyBuilder.Build(adaptive, part.Surfaces, 1e-7).Topology, TopologyBuilder.Build(uniform, part.Surfaces, 1e-7).Topology);
            output.WriteLine($"{adaptive.Quads.Count:N0} adaptive vs {uniform.Quads.Count:N0} uniform polygons");

            adaptive.IsWatertight().Should().BeTrue();
            a.Faces.Select(f => string.Join(",", f.Sources)).Should().Equal(u.Faces.Select(f => string.Join(",", f.Sources)));
            a.Edges.Count.Should().Be(u.Edges.Count);
            a.Vertices.Select(v => v.Faces.Count).Should().Equal(u.Vertices.Select(v => v.Faces.Count));
            adaptive.Quads.Count.Should().BeLessThan(uniform.Quads.Count / 3);
            SignedVolume(adaptive).Should().BeApproximately(SignedVolume(uniform), SignedVolume(uniform) * 1e-4);
        }
    }

    [Fact]
    public void Regeneration_is_bit_for_bit_deterministic()
    {
        var a = Regenerate(EditSuite.Base, 0.5);
        var b = Regenerate(EditSuite.Base, 0.5);

        b.Mesh!.Vertices.Select(Bits).Should().Equal(a.Mesh!.Vertices.Select(Bits));
        b.Mesh.Quads.Should().Equal(a.Mesh.Quads);
        b.Topology!.Faces.Select(f => string.Join(",", f.Sources)).Should().Equal(a.Topology!.Faces.Select(f => string.Join(",", f.Sources)));
        b.Topology.Edges.Should().Equal(a.Topology.Edges);
    }

    /// <summary>On uniform cells pruning is a pure speed-up: certified-empty cells and dropped surfaces change nothing.</summary>
    [Fact]
    public void Interval_pruning_gives_the_bit_identical_mesh()
    {
        PartDocument[] docs =
        [
            EditSuite.Base,
            new([.. EditSuite.Base.Features, new Shell("shell", 1.5)]),
            new(
            [
                new Block("plate", BooleanOp.New, [0, 0, 0], [100, 50, 10]),
                new Hole("hole", [30, 25], 10),
                new Block("tab", BooleanOp.Join, [-20, 10, 0], [30, 30, 10]),
                new Block("slot", BooleanOp.Cut, [60, 30, -1], [10, 30, 12]),
                new PlaneCut("cut", [100, 0, 10], [0.7071067811865476, 0, 0.7071067811865476]),
                new Fillet("fillet", ["plate/z-", "plate/y-"], 2),
            ]),
        ];
        foreach (var doc in docs)
        {
            var part = new Compiler(new ModelingSettings()).Compile(doc);
            var pruned = DualContouring.Build(part.Field!, part.Min, part.Max, 1.0, 0.01, adaptive: false, prune: true);
            var dense = DualContouring.Build(part.Field!, part.Min, part.Max, 1.0, 0.01, adaptive: false, prune: false);

            pruned.Quads.Should().Equal(dense.Quads);
            pruned.Vertices.Select(Bits).Should().Equal(dense.Vertices.Select(Bits));
        }
    }

    static FrepResult Regenerate(PartDocument doc, double h) =>
        new FrepModeler(new ModelingSettings { CellSize = h }).Regenerate(doc);

    static (long, long, long) Bits(Point3D p) =>
        (BitConverter.DoubleToInt64Bits(p.X), BitConverter.DoubleToInt64Bits(p.Y), BitConverter.DoubleToInt64Bits(p.Z));

    /// <summary>
    /// |f| at vertices and at triangle centroids / edge midpoints. Every leaf is an exact distance, so away from
    /// sharp edges |f| is the distance to the surface; at edges it is a lower bound.
    /// </summary>
    static (double Vertex, double Chord) Errors(FrepResult r)
    {
        var field = r.Part.Field!;
        var v = r.Mesh!.Vertices;
        var vertex = v.Max(p => Math.Abs(field.Value(p)));
        var chord = 0.0;
        foreach (var t in r.Mesh.Triangles())
        {
            Point3D Mid(Point3D a, Point3D b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2, (a.Z + b.Z) / 2);
            var c = new Point3D((v[t.A].X + v[t.B].X + v[t.C].X) / 3, (v[t.A].Y + v[t.B].Y + v[t.C].Y) / 3, (v[t.A].Z + v[t.B].Z + v[t.C].Z) / 3);
            foreach (var p in new[] { c, Mid(v[t.A], v[t.B]), Mid(v[t.B], v[t.C]), Mid(v[t.C], v[t.A]) })
                chord = Math.Max(chord, Math.Abs(field.Value(p)));
        }
        return (vertex, chord);
    }

    static double SignedVolume(Mesh mesh)
    {
        var v = mesh.Vertices;
        return mesh.Triangles().Sum(t => v[t.A].AsVector().Dot(v[t.B].AsVector().Cross(v[t.C].AsVector()))) / 6;
    }
}

