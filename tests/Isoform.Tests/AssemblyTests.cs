using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Geometry;
using Isoform.Meshing;
using Isoform.Modeling;

namespace Isoform.Tests;

/// <summary>Assemblies: placed occurrences of parts, regenerated once per distinct part, named by occurrence.</summary>
public class AssemblyTests
{
    static readonly AssemblyModeler Modeler = new(new ModelingSettings { CellSize = 0.5 });

    static readonly PartDocument Bracket = new([new Block("plate", BooleanOp.New, [0, 0, 0], [40, 20, 5]), new Hole("bore", [10, 10], 6)]);
    static readonly PartDocument Pin = new([new Revolve("pin", BooleanOp.New, new SketchPlane([0, 0, 0], [1, 0, 0], [0, -1, 0]), new Profile([0, 0],
        [new ProfileEdge("bottom", [3, 0]), new ProfileEdge("shank", [3, 12]), new ProfileEdge("top", [0, 12]), new ProfileEdge("axis", [0, 0])]))]);

    static readonly Placement Shifted = new([0, 0, 20], [1, 0, 0], [0, 0, 1]);
    static readonly Placement Turned = new([100, 0, 0], [0, 1, 0], [0, 0, 1]);

    static Func<string, string?> Files(params (string Path, string Text)[] files) =>
        path => files.FirstOrDefault(f => f.Path == path).Text;

    static AssemblyDocument TwoBrackets => new(
    [
        new Component("left", "bracket.json", Placement.Identity),
        new Component("right", "bracket.json", Turned),
        new Component("pin", "pin.json", Shifted),
    ]);

    static (string, string)[] Parts => [("bracket.json", CanonicalFormat.Write(Bracket)), ("pin.json", CanonicalFormat.Write(Pin))];

    [Fact]
    public void Instances_place_their_part_exactly_and_share_its_regeneration()
    {
        var result = Modeler.Regenerate(TwoBrackets, Files(Parts));

        result.Diagnostics.Should().BeEmpty();
        result.Instances.Select(i => i.Id).Should().Equal("left", "right", "pin");
        result.Instances[0].Result.Should().BeSameAs(result.Instances[1].Result, "identical parts regenerate once");

        var right = result.Instances[1];
        var part = right.Result.Part.Field!;
        var rng = new Random(1);
        for (var k = 0; k < 2000; k++)
        {
            var local = new Point3D(rng.NextDouble() * 60 - 10, rng.NextDouble() * 40 - 10, rng.NextDouble() * 20 - 5);
            right.Field!.Value(right.Placement.ToWorld(local)).Should().BeApproximately(part.Value(local), 1e-12);
        }
        // Turned 90° about z and moved to x = 100: the bracket's far corner (40, 20) lands at (80, 40).
        right.Placement.ToWorld(new Point3D(40, 20, 0)).Should().Match<Point3D>(p => (p - new Point3D(80, 40, 0)).Length < 1e-12);
        right.Name("bore/wall").Should().Be("right/bore/wall");
    }

    [Fact]
    public void Placed_fields_keep_the_kernel_guarantees()
    {
        var instance = Modeler.Regenerate(TwoBrackets, Files(Parts)).Instances[1];
        var field = instance.Field!;
        var rng = new Random(2);
        for (var b = 0; b < 1500; b++)
        {
            var size = Math.Pow(10, rng.NextDouble() * 2.5 - 1);
            var min = new Point3D(rng.NextDouble() * 60 + 50, rng.NextDouble() * 60 - 10, rng.NextDouble() * 20 - 5);
            var box = new Box3D(min, new Point3D(min.X + size, min.Y + size, min.Z + size));
            var (bounds, simple) = (field.Bounds(box), field.Simplify(box));
            for (var i = 0; i < 10; i++)
            {
                var p = new Point3D(min.X + size * rng.NextDouble(), min.Y + size * rng.NextDouble(), min.Z + size * rng.NextDouble());
                var q = p + new Vector3D(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5, rng.NextDouble() - 0.5);
                var (full, reduced) = (field.Sample(p), simple.Sample(p));
                full.Value.Should().BeInRange(bounds.Lo - 1e-12, bounds.Hi + 1e-12);
                (reduced.Value, reduced.Owner).Should().Be((full.Value, full.Owner));
                (reduced.Gradient - full.Gradient).Length.Should().Be(0);
                field.Value(p).Should().Be(full.Value);
                Math.Abs(field.Value(p) - field.Value(q)).Should().BeLessThanOrEqualTo((p - q).Length * (1 + 1e-12) + 1e-12);
            }
        }
    }

    [Fact]
    public void Sub_assemblies_nest_their_placements_and_names()
    {
        var pair = new AssemblyDocument([new Component("a", "bracket.json", Placement.Identity), new Component("b", "bracket.json", Shifted)]);
        var top = new AssemblyDocument([new Component("stack", "pair.json", Turned)]);

        var result = Modeler.Regenerate(top, Files([.. Parts, ("pair.json", CanonicalFormat.Write(pair))]));

        result.Diagnostics.Should().BeEmpty();
        result.Instances.Select(i => i.Id).Should().Equal("stack/a", "stack/b");
        // b is shifted 20 up inside the pair, and the pair is turned and moved: its origin lands at (100, 0, 20).
        (result.Instances[1].Placement.ToWorld(new Point3D(0, 0, 0)) - new Point3D(100, 0, 20)).Length.Should().BeLessThan(1e-12);
    }

    [Fact]
    public void World_mesh_holds_every_instance()
    {
        var result = Modeler.Regenerate(TwoBrackets, Files(Parts));
        var (mesh, _) = result.WorldMesh();

        var bracket = 40 * 20 * 5 - Math.PI * 9 * 5;
        var pin = Math.PI * 9 * 12;
        SignedVolume(mesh).Should().BeApproximately(2 * bracket + pin, (2 * bracket + pin) * 2e-3);
        mesh.IsWatertight().Should().BeTrue();
    }

    [Fact]
    public void Changed_missing_and_circular_documents_are_reported()
    {
        var pinned = new AssemblyDocument([new Component("left", "bracket.json", Placement.Identity, Hash: "0000")]);
        Modeler.Regenerate(pinned, Files(Parts)).Diagnostics.Should().ContainSingle().Which.Code.Should().Be(Diagnostic.PartChanged);

        var current = new AssemblyDocument([new Component("left", "bracket.json", Placement.Identity, Hash: CanonicalFormat.Hash(Bracket))]);
        Modeler.Regenerate(current, Files(Parts)).Diagnostics.Should().BeEmpty();

        var missing = new AssemblyDocument([new Component("ghost", "nowhere.json", Placement.Identity)]);
        Modeler.Regenerate(missing, Files(Parts)).Diagnostics.Should().ContainSingle().Which.Code.Should().Be(Diagnostic.PartMissing);

        var loop = new AssemblyDocument([new Component("self", "loop.json", Placement.Identity)]);
        Modeler.Regenerate(loop, Files(("loop.json", CanonicalFormat.Write(loop)))).Diagnostics.Should().ContainSingle()
            .Which.Message.Should().Contain("contains itself");
    }

    [Fact]
    public void Assemblies_and_bundles_round_trip_through_the_canonical_format()
    {
        var text = CanonicalFormat.Write(TwoBrackets);
        CanonicalFormat.Write(CanonicalFormat.ReadAssembly(text)).Should().Be(text);

        var bundle = new Bundle(TwoBrackets, Parts.ToDictionary(p => p.Item1, p => p.Item2));
        var packed = CanonicalFormat.Write(bundle);
        var back = CanonicalFormat.ReadBundle(packed);
        CanonicalFormat.Write(back).Should().Be(packed);
        CanonicalFormat.Read(back.Documents["pin.json"]).Should().BeEquivalentTo(Pin, o => o.RespectingRuntimeTypes());
    }

    static double SignedVolume(Mesh mesh)
    {
        var v = mesh.Vertices;
        return mesh.Triangles().Sum(t => v[t.A].AsVector().Dot(v[t.B].AsVector().Cross(v[t.C].AsVector()))) / 6;
    }
}
