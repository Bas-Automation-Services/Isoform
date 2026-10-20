using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Meshing;
using Isoform.Modeling;
using Xunit.Abstractions;

namespace Isoform.Tests;

/// <summary>
/// Region meshing: regions meshed apart, in any order and through bytes, combine into the single-pass mesh bit for
/// bit; a cache reuses exactly the regions an edit did not change.
/// </summary>
public class RegionTests(ITestOutputHelper output)
{
    // Parts of other test classes, by name only: an instance method, so the reflection that collects every test
    // document (DocumentDiffTests) finds them under their own classes.
    public static IEnumerable<object[]> Names =>
        new[] { "edit suite", "shelled" }
            .Concat(SketchFeatureTests.Parts().Select(p => $"sketch: {p.Name}"))
            .Concat(LoopFilletTests.Parts.Select(n => $"loop: {n[0]}"))
            .Concat(ConcaveFilletTests.Parts.Select(n => $"concave: {n[0]}"))
            .Concat(Isoform.Interactive.Examples.All.Select(e => $"example: {e.Name}"))
            .Select(n => new object[] { n });

    PartDocument Part(string name) => name.Split(": ", 2) switch
    {
        ["edit suite"] => EditSuite.Base,
        ["shelled"] => new([.. EditSuite.Base.Features, new Shell("shell", 1.5)]),
        ["sketch", var n] => SketchFeatureTests.Parts().Single(p => p.Name == n).Doc,
        ["loop", var n] => LoopFilletTests.Part(n),
        ["concave", var n] => ConcaveFilletTests.Part(n),
        [_, var n] => Isoform.Interactive.Examples.All.Single(e => e.Name == n).Doc,
        _ => throw new ArgumentException(name),
    };

    [Theory]
    [MemberData(nameof(Names))]
    public void Regions_meshed_apart_give_the_single_pass_mesh_bit_for_bit(string name)
    {
        var part = new Compiler(new ModelingSettings()).Compile(Part(name));
        foreach (var body in part.Bodies)
        foreach (var (h, snap) in new[] { (0.5, false), (0.3, false), (0.5, true) })
        {
            var single = DualContouring.Build(body.Field, body.Min, body.Max, h, 0.01, snapGrid: snap);
            var plan = DualContouring.Plan(body.Field, body.Min, body.Max, h, 0.01, snap);
            // As workers would: meshed in another order, written out and read back.
            var results = plan.Regions.Reverse().ToDictionary(r => r, r => RoundTrip(plan.Mesh(r)));
            var combined = plan.Assemble(plan.Regions.Select(r => results[r]).ToList());
            output.WriteLine($"{body.Name} h={h} snap={snap}: {plan.Regions.Count} regions, {combined.Quads.Count:N0} polygons, {combined.ThinSpots.Count} thin spots");

            Same(combined, single);
        }
    }

    // A plate with holes, like the benchmark's 20-op part but smaller.
    static PartDocument Plate(double holeX = 30, double tab = 30, Feature? first = null) => new(
    [
        new Block("plate", BooleanOp.New, [0, 0, 0], [80, 40, 8]),
        .. first is null ? Array.Empty<Feature>() : [first],
        new Hole("h1", [10, 10], 6), new Hole("h2", [holeX, 25], 8), new Hole("h3", [60, 12], 4),
        new Block("slot", BooleanOp.Cut, [45, 30, -1], [6, 20, 10]),
        new Block("tab", BooleanOp.Join, [-tab + 10, 10, 0], [tab, 20, 8]),
        new Fillet("f1", ["plate/z+", "plate/x+"], 2),
    ]);

    [Fact]
    public void An_edit_re_meshes_only_the_regions_it_changed()
    {
        var cache = new RegionCache();
        var modeler = new FrepModeler(new ModelingSettings(), cache);
        modeler.Regenerate(Plate());
        var cold = cache.Sweep();

        var edited = Plate(holeX: 34);
        var warm = modeler.Regenerate(edited);
        var (hits, misses) = cache.Sweep();
        output.WriteLine($"cold: {cold.Misses} regions meshed; after moving a hole: {hits} reused, {misses} meshed");

        Same(warm, new FrepModeler(new ModelingSettings()).Regenerate(edited));
        misses.Should().BeLessThan(cold.Misses / 3);
        hits.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Cached_owners_follow_their_surfaces_when_a_feature_shifts_the_ids()
    {
        var cache = new RegionCache();
        var modeler = new FrepModeler(new ModelingSettings(), cache);
        modeler.Regenerate(Plate());
        cache.Sweep();

        // A hole inserted second renumbers every later surface; the regions away from it are reused.
        var edited = Plate(first: new Hole("h0", [70, 30], 5));
        var warm = modeler.Regenerate(edited);
        var (hits, _) = cache.Sweep();

        hits.Should().BeGreaterThan(0);
        Same(warm, new FrepModeler(new ModelingSettings()).Regenerate(edited));
    }

    [Fact]
    public void A_moved_bounding_box_reuses_nothing_unless_the_grid_is_snapped()
    {
        foreach (var snap in new[] { false, true })
        {
            var settings = new ModelingSettings { SnapGrid = snap };
            var cache = new RegionCache();
            var modeler = new FrepModeler(settings, cache);
            modeler.Regenerate(Plate());
            cache.Sweep();

            var edited = Plate(tab: 33); // the tab sticks out further: the part's corner moves
            var warm = modeler.Regenerate(edited);
            var (hits, misses) = cache.Sweep();
            output.WriteLine($"snap={snap}: {hits} reused, {misses} meshed");

            Same(warm, new FrepModeler(settings).Regenerate(edited));
            if (snap) hits.Should().BeGreaterThan(misses);
            else hits.Should().Be(0);
        }
    }

    [Fact]
    public void Shares_meshed_in_separate_caches_let_one_regenerate_from_its_cache_alone()
    {
        const int count = 3;
        var doc = Plate();
        var caches = Enumerable.Range(0, count).Select(_ => new RegionCache()).ToArray();
        for (var i = 0; i < count; i++) new FrepModeler(new ModelingSettings(), caches[i]).MeshShare(doc, i, count);
        for (var i = 1; i < count; i++) caches[0].Import(caches[i].TakeComputed());
        caches[0].Sweep();

        var result = new FrepModeler(new ModelingSettings(), caches[0]).Regenerate(doc);

        caches[0].Sweep().Misses.Should().Be(0);
        caches.Skip(1).Should().OnlyContain(c => c.Count > 0);
        Same(result, new FrepModeler(new ModelingSettings()).Regenerate(doc));
    }

    static RegionMesh RoundTrip(RegionMesh mesh)
    {
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true)) mesh.Write(w);
        stream.Position = 0;
        return RegionMesh.Read(new BinaryReader(stream));
    }

    static void Same(FrepResult a, FrepResult b)
    {
        Same(a.Mesh!, b.Mesh!);
        a.Topology!.Faces.Should().BeEquivalentTo(b.Topology!.Faces, o => o.WithStrictOrdering());
        a.Topology.Edges.Should().Equal(b.Topology.Edges);
    }

    static void Same(Mesh a, Mesh b)
    {
        a.Vertices.Select(Bits).Should().Equal(b.Vertices.Select(Bits));
        a.Quads.Should().Equal(b.Quads);
        a.ThinSpots.Should().Equal(b.ThinSpots);
    }

    static (long, long, long) Bits(Point3D p) =>
        (BitConverter.DoubleToInt64Bits(p.X), BitConverter.DoubleToInt64Bits(p.Y), BitConverter.DoubleToInt64Bits(p.Z));
}
