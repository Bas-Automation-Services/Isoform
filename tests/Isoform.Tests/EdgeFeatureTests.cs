using System.Text.Json.Nodes;
using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Meshing;
using Isoform.Modeling;
using Xunit.Abstractions;

namespace Isoform.Tests;

/// <summary>K4: fillets sharing a face, chamfers, edges of swept profiles, and fillets after a shell.</summary>
public class EdgeFeatureTests(ITestOutputHelper output)
{
    static readonly Block Plate = new("plate", BooleanOp.New, [0, 0, 0], [100, 50, 10]);
    static readonly SketchPlane Top = new([0, 0, 10], [1, 0, 0], [0, 0, 1]);
    static readonly FrepModeler Modeler = new(new ModelingSettings { CellSize = 0.25 });

    // Mesh volume misses curved faces by their area times the chord error: tight enough to catch a missing or wrong
    // feature, while the exact-surface checks below hold every vertex to 1e-7.
    const double VolumeTolerance = 1e-4;

    static PartDocument SharedFillets(double r) => new(
    [
        Plate,
        new Fillet("f1", ["plate/z+", "plate/x+"], r),
        new Fillet("f2", ["plate/z+", "plate/y+"], r),
    ]);

    static PartDocument Countersink(double d) => new(
    [
        Plate,
        new Hole("hole", [30, 25], 10),
        new Chamfer("sink", ["plate/z+", "hole/wall"], d),
    ]);

    static PartDocument Boss(double d) => new(
    [
        Plate,
        new Extrude("boss", BooleanOp.Join, Top, new Profile([70, 25], [new ProfileEdge("a", [80, 25], 1), new ProfileEdge("b", [70, 25], 1)]), 0, 8),
        new Chamfer("rim", ["boss/end", "boss/a"], d),
    ]);

    static PartDocument BracketFillet(string side) => new(
    [
        new Extrude("bracket", BooleanOp.New, new SketchPlane([0, 0, 0], [1, 0, 0], [0, 0, 1]), new Profile([0, 0],
        [
            new ProfileEdge("bottom", [40, 0]), new ProfileEdge("tip", [40, 5]), new ProfileEdge("inner", [5, 5]),
            new ProfileEdge("riser", [5, 30]), new ProfileEdge("top", [0, 30]), new ProfileEdge("back", [0, 0]),
        ]), 0, 20),
        new Fillet("round", ["bracket/end", $"bracket/{side}"], 1),
    ]);

    static PartDocument ShellThenFillet(double r) => new([.. EditSuite.Base.Features, new Shell("shell", 1.5), new Fillet("outer", ["plate/z+", "plate/y-"], r)]);

    [Fact]
    public void Fillets_sharing_a_face_meet_in_a_mitre()
    {
        const double r = 2;
        var result = Modeler.Regenerate(SharedFillets(r));

        result.Diagnostics.Should().BeEmpty();
        // Each fillet removes r²(1 − π/4) per mm of edge; at the corner the two removals overlap by ∫ w(h)² dh.
        var overlap = Integrate(h => Math.Pow(r - Math.Sqrt(r * r - h * h), 2), 0, r);
        var exact = 100 * 50 * 10 - r * r * (1 - Math.PI / 4) * (50 + 100) + overlap;
        var volume = SignedVolume(result.Mesh!);
        output.WriteLine($"volume {volume:F4} vs {exact:F4}");
        volume.Should().BeApproximately(exact, exact * VolumeTolerance);
        ReferenceResolver.Resolve(new EdgeReference("mitre", "f1/face", "f2/face"), result.Topology!).Status.Should().Be(ReferenceStatus.Resolved);
        // A mitre is where the two fillet cylinders cross: each face stays on its own cylinder right into the corner.
        Deviation(result, "f1/face", p => Math.Sqrt(Sq(p.X - 98) + Sq(p.Z - 8)) - r).Should().BeLessThan(1e-7);
        Deviation(result, "f2/face", p => Math.Sqrt(Sq(p.Y - 48) + Sq(p.Z - 8)) - r).Should().BeLessThan(1e-7);
    }

    [Fact]
    public void Plane_chamfer_is_flat_at_the_set_distance()
    {
        var result = Modeler.Regenerate(new PartDocument([Plate, new Chamfer("c", ["plate/z+", "plate/x+"], 2)]));

        result.Diagnostics.Should().BeEmpty();
        result.Topology!.Faces.Should().HaveCount(7);
        SignedVolume(result.Mesh!).Should().BeApproximately(100 * 50 * 10 - 2 * 50, 1e-6);
        Deviation(result, "c/face", p => (p.X + p.Z - 108) / Math.Sqrt(2)).Should().BeLessThan(1e-7);
    }

    [Fact]
    public void Hole_chamfer_is_a_45_degree_countersink()
    {
        const double d = 1;
        var result = Modeler.Regenerate(Countersink(d));

        result.Diagnostics.Should().BeEmpty();
        // Frustum from radius 5 to 6 over depth 1, less the hole's own cylinder there.
        var sink = Math.PI * d / 3 * (25 + 30 + 36) - Math.PI * 25 * d;
        var exact = 100 * 50 * 10 - Math.PI * 25 * 10 - sink;
        SignedVolume(result.Mesh!).Should().BeApproximately(exact, exact * VolumeTolerance);
        Deviation(result, "sink/face", p => (Math.Sqrt(Sq(p.X - 30) + Sq(p.Y - 25)) - p.Z + 4) / Math.Sqrt(2)).Should().BeLessThan(1e-7);
    }

    [Fact]
    public void Boss_chamfer_rounds_the_whole_rim()
    {
        const double d = 1;
        var result = Modeler.Regenerate(Boss(d));

        result.Diagnostics.Should().BeEmpty();
        var removed = Math.PI * 25 * d - Math.PI * d / 3 * (25 + 20 + 16);
        var exact = 100 * 50 * 10 + Math.PI * 25 * 8 - removed;
        SignedVolume(result.Mesh!).Should().BeApproximately(exact, exact * VolumeTolerance);
        Deviation(result, "rim/face", p => (Math.Sqrt(Sq(p.X - 75) + Sq(p.Y - 25)) + p.Z - 22) / Math.Sqrt(2)).Should().BeLessThan(1e-7);
    }

    [Fact]
    public void Fillet_on_a_swept_profile_edge_the_profile_lies_behind()
    {
        var result = Modeler.Regenerate(BracketFillet("bottom"));

        result.Diagnostics.Should().BeEmpty();
        var exact = (40 * 5 + 5 * 25) * 20 - (1 - Math.PI / 4) * 40;
        SignedVolume(result.Mesh!).Should().BeApproximately(exact, exact * VolumeTolerance);
        Deviation(result, "round/face", p => Math.Sqrt(Sq(p.Y - 1) + Sq(p.Z - 19)) - 1).Should().BeLessThan(1e-7);
    }

    [Fact]
    public void Profile_edges_the_profile_is_not_behind_are_reported_unsupported()
    {
        var result = Modeler.Regenerate(BracketFillet("riser"));

        result.Diagnostics.Should().ContainSingle().Which.Code.Should().Be(Diagnostic.UnsupportedFillet);
    }

    [Fact]
    public void Fillet_after_a_shell_rounds_the_outside_and_keeps_the_cavity()
    {
        var result = Modeler.Regenerate(ShellThenFillet(1));

        result.Diagnostics.Should().BeEmpty();
        var sources = result.Topology!.Faces.SelectMany(f => f.Sources).ToHashSet();
        sources.Should().Contain(["outer/face", "shell/inner:plate/y-", "shell/inner:plate/z+"]);
        result.Topology.Edges.Select(e => (result.Topology.Faces[e.FaceA].Sources, result.Topology.Faces[e.FaceB].Sources))
            .Should().Contain(e => e.Item1.Contains("shell/inner:plate/y-") && e.Item2.Contains("shell/inner:plate/z+")
                || e.Item2.Contains("shell/inner:plate/y-") && e.Item1.Contains("shell/inner:plate/z+"), "the cavity keeps its sharp edge");
        var wall = MinWallAtFillet(result);
        output.WriteLine($"thinnest wall at the rounded edge {wall:F4}");
        wall.Should().BeGreaterThan(1.5 - 0.01);
    }

    [Fact]
    public void Fillet_after_a_shell_that_cuts_through_the_wall_is_rejected()
    {
        var result = Modeler.Regenerate(ShellThenFillet(6));

        result.Diagnostics.Should().ContainSingle().Which.Message.Should().Contain("cuts through the shell wall");
    }

    public static IEnumerable<object[]> Parts =>
    [
        ["shared fillets"], ["countersink"], ["boss chamfer"], ["bracket fillet"], ["shell then fillet"],
    ];

    static PartDocument Part(string name) => name switch
    {
        "shared fillets" => SharedFillets(2),
        "countersink" => Countersink(1),
        "boss chamfer" => Boss(1),
        "bracket fillet" => BracketFillet("bottom"),
        _ => ShellThenFillet(1),
    };

    [Theory]
    [MemberData(nameof(Parts))]
    public void Field_is_1_lipschitz_bounded_and_simplifies_exactly(string name) =>
        FieldProperties.Check(new Compiler(new ModelingSettings()).Compile(Part(name)));

    [Fact]
    public void Edge_faces_keep_their_names_when_sizes_change()
    {
        (PartDocument Doc, JsonNode Edit)[] cases =
        [
            (SharedFillets(2), JsonNode.Parse("""{ "set": "f1.radius", "value": 3 }""")!),
            (Countersink(1), JsonNode.Parse("""{ "set": "sink.distance", "value": 1.5 }""")!),
            (Boss(1), JsonNode.Parse("""{ "set": "boss.end", "value": 12 }""")!),
        ];
        foreach (var (doc, edit) in cases)
        {
            var before = Modeler.Regenerate(doc).Topology!;
            var after = Modeler.Regenerate(DocumentEdits.Apply(doc, [edit]));
            after.Diagnostics.Should().BeEmpty();
            foreach (var source in before.Faces.SelectMany(f => f.Sources))
            {
                var reference = new FaceReference(source, source);
                ReferenceResolver.Resolve(reference, after.Topology!).Status
                    .Should().Be(ReferenceResolver.Resolve(reference, before).Status, $"{source} after {edit.ToJsonString()}");
            }
        }
    }

    [Fact]
    public void Chamfers_round_trip_through_the_canonical_format()
    {
        var text = CanonicalFormat.Write(Countersink(1));
        CanonicalFormat.Write(CanonicalFormat.Read(text)).Should().Be(text);
    }

    /// <summary>Largest |distance| of the mesh vertices owned by one source face to its exact surface.</summary>
    static double Deviation(FrepResult r, string source, Func<Point3D, double> distance, Func<Point3D, bool>? where = null)
    {
        var owner = r.Part.Surfaces.Single(s => s.Source == source).Id;
        var mesh = r.Mesh!;
        var points = mesh.Quads.Where(q => q.Owner == owner).SelectMany(q => new[] { q.A, q.B, q.C, q.D }).Distinct()
            .Select(v => mesh.Vertices[v])
            // Vertices on the solid that this face owns there; at a tangent junction a vertex may sit just over
            // the line on the neighbouring face.
            .Where(p => r.Part.Field!.Sample(p) is var s && Math.Abs(s.Value) < 1e-9 && s.Owner == owner && (where?.Invoke(p) ?? true)).ToList();
        points.Should().NotBeEmpty();
        return points.Max(p => Math.Abs(distance(p)));
    }

    static double MinWallAtFillet(FrepResult r)
    {
        var mesh = r.Mesh!;
        var surfaces = r.Part.Surfaces;
        var fillet = surfaces.Single(s => s.Source == "outer/face").Id;
        var inner = new TriangleBvh(mesh.Vertices, mesh.Triangles().Where(t => surfaces[t.Owner].Source.StartsWith("shell/inner:")).ToList());
        return mesh.Quads.Where(q => q.Owner == fillet).Select(q => mesh.Vertices[q.A]).Min(inner.Distance);
    }

    static double Integrate(Func<double, double> f, double a, double b, int n = 2000)
    {
        var h = (b - a) / n;
        var sum = f(a) + f(b);
        for (var i = 1; i < n; i++) sum += f(a + i * h) * (i % 2 == 1 ? 4 : 2);
        return sum * h / 3;
    }

    static double Sq(double x) => x * x;

    static double SignedVolume(Mesh mesh)
    {
        var v = mesh.Vertices;
        return mesh.Triangles().Sum(t => v[t.A].AsVector().Dot(v[t.B].AsVector().Cross(v[t.C].AsVector()))) / 6;
    }
}
