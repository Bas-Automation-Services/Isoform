using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Meshing;
using Isoform.Modeling;
using Xunit.Abstractions;

namespace Isoform.Tests;

/// <summary>G1: a shell of wall t is at least t thick everywhere, because every CSG node is 1-Lipschitz.</summary>
public class ShellTests(ITestOutputHelper output)
{
    const double T = 1.5;
    const double CellSize = 0.25;
    // Chord error of the tightest inner surface: the inner fillet has radius r − t = 0.5 mm.
    static readonly double ChordBound = CellSize * CellSize / (8 * 0.5);

    static PartDocument Shelled(PartDocument doc) => new([.. doc.Features, new Shell("shell", T)]);

    [Fact]
    public void Field_is_1_lipschitz_so_it_never_overestimates_distance()
    {
        foreach (var doc in new[] { EditSuite.Base, WithTab(EditSuite.Base), Shelled(EditSuite.Base) })
        {
            var field = new Compiler(new ModelingSettings()).Compile(doc).Field!;
            var rng = new Random(1);
            Point3D Random() => new(rng.NextDouble() * 140 - 20, rng.NextDouble() * 90 - 20, rng.NextDouble() * 50 - 20);
            for (var i = 0; i < 200_000; i++)
            {
                var p = Random();
                var q = rng.NextDouble() < 0.5 ? Random() : p + new Vector3D(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5, rng.NextDouble() - 0.5);
                Math.Abs(field.Value(p) - field.Value(q)).Should().BeLessThanOrEqualTo((p - q).Length * (1 + 1e-12) + 1e-12);
            }
        }
    }

    [Fact]
    public void Shell_wall_is_never_thinner_than_t_and_exactly_t_on_flat_faces()
    {
        var result = new FrepModeler(new ModelingSettings { CellSize = CellSize }).Regenerate(Shelled(EditSuite.Base));
        result.Diagnostics.Should().BeEmpty();
        var mesh = result.Mesh!;
        var surfaces = result.Part.Surfaces;
        bool IsInner(int owner) => surfaces[owner].Source.StartsWith("shell/inner:");

        var outer = Side(mesh, q => !IsInner(q.Owner));
        var inner = Side(mesh, q => IsInner(q.Owner));
        var innerBvh = new TriangleBvh(mesh.Vertices, mesh.Triangles().Where(t => IsInner(t.Owner)).ToList());
        var outerBvh = new TriangleBvh(mesh.Vertices, mesh.Triangles().Where(t => !IsInner(t.Owner)).ToList());

        var outerToInner = outer.Select(v => innerBvh.Distance(mesh.Vertices[v])).ToList();
        var innerToOuter = inner.Select(v => outerBvh.Distance(mesh.Vertices[v])).ToList();
        output.WriteLine($"outer→inner min {outerToInner.Min():F6}, inner→outer min {innerToOuter.Min():F6}, chord bound {ChordBound:F6}");
        outerToInner.Min().Should().BeGreaterThanOrEqualTo(T - ChordBound);
        innerToOuter.Min().Should().BeGreaterThanOrEqualTo(T - ChordBound);

        // Well inside the top face, the wall is a plane pair exactly t apart.
        var top = surfaces.Single(s => s.Source == "plate/z+").Id;
        var owners = Owners(mesh);
        var flat = outer.Where(v => owners[v].All(o => o == top) && Inside(mesh.Vertices[v], 50, 60, 5, 45)).ToList();
        flat.Should().NotBeEmpty();
        flat.Max(v => Math.Abs(innerBvh.Distance(mesh.Vertices[v]) - T)).Should().BeLessThan(1e-9);
    }

    [Fact]
    public void Shell_keeps_one_body_with_an_internal_void_and_names_inner_faces_after_their_outer_face()
    {
        var result = new FrepModeler(new ModelingSettings()).Regenerate(Shelled(EditSuite.Base));
        var t = result.Topology!;

        t.BodyCount.Should().Be(1, "a hollow part is one body with a void, not two bodies");
        foreach (var reference in EditSuite.References)
            ReferenceResolver.Resolve(reference, t).Status.Should().Be(ReferenceStatus.Resolved, reference.Id);
        t.Faces.Where(f => f.Sources.Any(s => s.StartsWith("shell/inner:"))).Select(f => f.Sources.Single())
            .Should().BeEquivalentTo(t.Faces.Where(f => !f.Sources.Any(s => s.StartsWith("shell/"))).Select(f => $"shell/inner:{f.Sources.Single()}"));
    }

    [Fact]
    public void Coplanar_outer_faces_give_one_merged_inner_face()
    {
        var t = new FrepModeler(new ModelingSettings()).Regenerate(Shelled(WithTab(EditSuite.Base))).Topology!;

        t.Faces.Single(f => f.Sources.Contains("shell/inner:plate/z+")).Sources
            .Should().Equal("shell/inner:plate/z+", "shell/inner:tab/z+");
    }

    [Fact]
    public void Shell_thicker_than_the_part_has_no_effect()
    {
        var result = new FrepModeler(new ModelingSettings()).Regenerate(new PartDocument([.. EditSuite.Base.Features, new Shell("shell", 6)]));

        result.Diagnostics.Select(d => d.Code).Should().Equal(Diagnostic.NoEffect);
    }

    static PartDocument WithTab(PartDocument doc) =>
        new([doc.Features[0], doc.Features[1], new Block("tab", BooleanOp.Join, [-20, 10, 0], [30, 30, 10]), doc.Features[2]]);

    static List<int> Side(Mesh mesh, Func<Quad, bool> pick) =>
        mesh.Quads.Where(pick).SelectMany(q => new[] { q.A, q.B, q.C, q.D }).Distinct().Order().ToList();

    static Dictionary<int, HashSet<int>> Owners(Mesh mesh)
    {
        var owners = new Dictionary<int, HashSet<int>>();
        foreach (var q in mesh.Quads)
            foreach (var v in new[] { q.A, q.B, q.C, q.D })
                (owners.TryGetValue(v, out var set) ? set : owners[v] = []).Add(q.Owner);
        return owners;
    }

    static bool Inside(Point3D p, double x0, double x1, double y0, double y1) => p.X > x0 && p.X < x1 && p.Y > y0 && p.Y < y1;
}
