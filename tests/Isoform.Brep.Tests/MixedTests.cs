using Isoform.Brep.Modeling;
using Isoform.Document;
using Isoform.Meshing;
using Isoform.Modeling;
using Xunit.Abstractions;

namespace Isoform.Brep.Tests;

/// <summary>
/// Documents on both kernels: the managed one as far as it goes, the F-rep core from there on. The result is the F-rep
/// core's kind, with the same faces, names and neighbours as the F-rep core alone and its mesh the same volume.
/// </summary>
public class MixedTests(ITestOutputHelper output)
{
    static readonly ModelingSettings Settings = new();
    static readonly SketchPlane Xy = new([0, 0, 0], [1, 0, 0], [0, 0, 1]);
    static readonly SketchPlane Top = new([0, 0, 10], [1, 0, 0], [0, 0, 1]);
    static readonly Block Plate = new("plate", BooleanOp.New, [0, 0, 0], [100, 50, 10]);

    static Profile Circle(double cx, double cy, double r) => new([cx - r, cy], [new ProfileEdge("a", [cx + r, cy], 1), new ProfileEdge("b", [cx - r, cy], 1)]);

    public static IEnumerable<object[]> Parts =>
    [
        // All the kernel's: the F-rep core only meshes it. A circle of two named halves is one face of both names.
        ["a plate with a boss and a hole", new PartDocument([Plate, new Extrude("boss", BooleanOp.Join, Top, Circle(30, 25, 10), 0, 8), new Hole("bore", [70, 25], 6)])],
        // The kernel's loop fillet round a slot's ends, meshed with the grid on the chord of half an end round.
        ["a rounded outline with a slot", new PartDocument(
        [
            new Extrude("outline", BooleanOp.New, Xy, new Profile([0, 0],
                [new ProfileEdge("s", [80, 0]), new ProfileEdge("e", [80, 50]), new ProfileEdge("n", [0, 50]), new ProfileEdge("w", [0, 0])]), 0, 8),
            new Fillet("corner", ["outline/s", "outline/e"], 10),
            new Fillet("corner2", ["outline/n", "outline/w"], 10),
            new Extrude("slot", BooleanOp.Cut, Xy, new Profile([25, 20],
                [new ProfileEdge("a", [55, 20]), new ProfileEdge("right", [55, 30], 1), new ProfileEdge("b", [25, 30]), new ProfileEdge("left", [25, 20], 1)]), -1, 9),
            new Fillet("top", [], 1.5, Loop: "outline/end"),
        ])],
        // The playground's: the plate is the kernel's, the blended knob and groove and the shell the F-rep core's.
        ["smooth blends and a shell", new PartDocument(
        [
            Plate,
            new Extrude("knob", BooleanOp.Join, Top, Circle(30, 25, 10), 0, 15, Blend: 4),
            new Block("groove", BooleanOp.Cut, [60, -1, 6], [12, 52, 10], Blend: 3),
            new Isoform.Document.Shell("shell", 2),
        ])],
        // A lattice in the kernel's cube.
        ["a strut lattice in a cube", new PartDocument([new Block("cube", BooleanOp.New, [0, 0, 0], [30, 30, 30]), new Lattice("lattice", "bcc", 10, 1.5)])],
    ];

    static List<string> Describe(Topology t) =>
        [.. t.Faces.Select(f => $"{string.Join("+", f.Sources)} | {string.Join(",", t.Signature(f.Index))}").Order()];

    static double Volume(Mesh m) => m.Triangles().Sum(t => m.Vertices[t.A].AsVector().Dot(m.Vertices[t.B].AsVector().Cross(m.Vertices[t.C].AsVector()))) / 6;

    [Theory]
    [MemberData(nameof(Parts))]
    public void A_mixed_part_is_the_frep_cores_part(string name, PartDocument document)
    {
        var mixed = new MixedModeler(Settings).Regenerate(document);
        var frep = new FrepModeler(Settings).Regenerate(document);
        output.WriteLine($"{name}: {mixed.Topology!.Faces.Count} faces, volume {Volume(mixed.Mesh!):F3} against {Volume(frep.Mesh!):F3}");
        mixed.Diagnostics.Should().BeEmpty();
        Describe(mixed.Topology).Should().Equal(Describe(frep.Topology!), name);
        Volume(mixed.Mesh!).Should().BeApproximately(Volume(frep.Mesh!), 1e-5 * Volume(frep.Mesh!));
    }

    [Fact]
    public void How_far_the_kernel_gets_is_the_head()
    {
        var document = (PartDocument)Parts.Single(p => (string)p[0] == "smooth blends and a shell")[1];
        var (count, solids, bodyOf) = new BrepModeler(Settings).Head(document);
        count.Should().Be(1, "the blended knob is the F-rep core's");
        solids.Should().ContainSingle();
        bodyOf.Should().ContainKey("plate");
    }
}
