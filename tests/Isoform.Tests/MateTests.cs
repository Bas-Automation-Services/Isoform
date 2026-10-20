using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Modeling;

namespace Isoform.Tests;

/// <summary>Mates by face name: solved poses, free degrees of freedom, conflicts.</summary>
public class MateTests
{
    static readonly AssemblyModeler Modeler = new(new ModelingSettings { CellSize = 0.5 });

    static readonly PartDocument Bracket = new([new Block("plate", BooleanOp.New, [0, 0, 0], [40, 20, 5]), new Hole("bore", [10, 10], 6)]);

    // Shank Ø5.8 for 24 mm, then a Ø10 head 3 mm tall: "head" is the flat underside of the head, facing down.
    static readonly PartDocument Pin = new([new Revolve("pin", BooleanOp.New, new SketchPlane([0, 0, 0], [1, 0, 0], [0, -1, 0]), new Profile([0, 0],
    [
        new ProfileEdge("tip", [2.9, 0]), new ProfileEdge("shank", [2.9, 24]), new ProfileEdge("head", [5, 24]),
        new ProfileEdge("collar", [5, 27]), new ProfileEdge("cap", [0, 27]), new ProfileEdge("axis", [0, 0]),
    ]))]);

    static Func<string, string?> Files => path => path switch
    {
        "bracket.json" => CanonicalFormat.Write(Bracket),
        "pin.json" => CanonicalFormat.Write(Pin),
        _ => null,
    };

    // Deliberately wrong starting poses: the solver has to bring them home.
    static readonly Placement Askew = new([31, 47, 22], [0.8, 0.6, 0], [0.6, -0.8, 0.0]);

    [Fact]
    public void A_pin_drops_into_the_bore_and_keeps_its_spin()
    {
        var assembly = new AssemblyDocument(
            [new Component("bracket", "bracket.json", Placement.Identity), new Component("pin", "pin.json", Askew)],
            [new Mate("in", MateKind.Axis, "pin/pin/shank", "bracket/bore/wall"), new Mate("seat", MateKind.Mate, "pin/pin/head", "bracket/plate/z+")]);

        var result = Modeler.Regenerate(assembly, Files);

        result.Diagnostics.Should().BeEmpty();
        var pin = result.Instances.Single(i => i.Id == "pin").Placement;
        // Axis on (10, 10), head underside (local z = 24) on the bracket top (z = 5): the pin's origin at z = −19.
        (pin.Origin - new Point3D(10, 10, -19)).Length.Should().BeLessThan(1e-9);
        Math.Abs(pin.Z.Dot(new Vector3D(0, 0, 1))).Should().BeApproximately(1, 1e-12, "the pin's axis is its own z");
        result.FreeDegrees.Should().Be(1, "it may still spin round its axis");
    }

    [Fact]
    public void Three_mates_hold_a_bracket_fully()
    {
        var assembly = new AssemblyDocument(
            [new Component("lower", "bracket.json", Placement.Identity), new Component("upper", "bracket.json", Askew)],
            [
                new Mate("gap", MateKind.Mate, "upper/plate/z-", "lower/plate/z+", Offset: 10),
                new Mate("bores", MateKind.Axis, "upper/bore/wall", "lower/bore/wall"),
                new Mate("side", MateKind.Flush, "upper/plate/x+", "lower/plate/x+"),
            ]);

        var result = Modeler.Regenerate(assembly, Files);

        result.Diagnostics.Should().BeEmpty();
        result.FreeDegrees.Should().Be(0);
        var upper = result.Instances.Single(i => i.Id == "upper").Placement;
        // Same orientation as the lower one, lifted to sit 10 mm above its top.
        (upper.Origin - new Point3D(0, 0, 15)).Length.Should().BeLessThan(1e-9);
        (upper.X - new Vector3D(1, 0, 0)).Length.Should().BeLessThan(1e-9);
        (upper.Z - new Vector3D(0, 0, 1)).Length.Should().BeLessThan(1e-9);
    }

    [Fact]
    public void Mates_that_cannot_all_hold_are_reported()
    {
        var assembly = new AssemblyDocument(
            [new Component("bracket", "bracket.json", Placement.Identity), new Component("pin", "pin.json", Askew)],
            [new Mate("top", MateKind.Mate, "pin/pin/head", "bracket/plate/z+"), new Mate("bottom", MateKind.Mate, "pin/pin/head", "bracket/plate/z-")]);

        Modeler.Regenerate(assembly, Files).Diagnostics.Should().ContainSingle().Which.Code.Should().Be(Diagnostic.MatesConflict);
    }

    [Fact]
    public void Mates_need_existing_faces_of_the_right_kind()
    {
        var unknown = new AssemblyDocument(
            [new Component("bracket", "bracket.json", Placement.Identity), new Component("pin", "pin.json", Askew)],
            [new Mate("in", MateKind.Axis, "pin/pin/nothing", "bracket/bore/wall")]);
        Modeler.Regenerate(unknown, Files).Diagnostics.Should().ContainSingle().Which.Message.Should().Contain("is not a face");

        var wrongKind = new AssemblyDocument(
            [new Component("bracket", "bracket.json", Placement.Identity), new Component("pin", "pin.json", Askew)],
            [new Mate("in", MateKind.Axis, "pin/pin/head", "bracket/bore/wall")]);
        Modeler.Regenerate(wrongKind, Files).Diagnostics.Should().ContainSingle().Which.Message.Should().Contain("cylinders or cones");
    }

    [Fact]
    public void Mates_round_trip_through_the_canonical_format()
    {
        var assembly = new AssemblyDocument(
            [new Component("lower", "bracket.json", Placement.Identity, Grounded: true), new Component("upper", "bracket.json", Askew)],
            [new Mate("gap", MateKind.Mate, "upper/plate/z-", "lower/plate/z+", Offset: 10), new Mate("bores", MateKind.Axis, "upper/bore/wall", "lower/bore/wall")]);

        var text = CanonicalFormat.Write(assembly);
        text.Should().Contain("\"kind\": \"axis\"").And.Contain("\"grounded\": true");
        CanonicalFormat.Write(CanonicalFormat.ReadAssembly(text)).Should().Be(text);
        CanonicalFormat.Write(new AssemblyDocument([new Component("a", "bracket.json", Placement.Identity)])).Should().NotContain("mates");
    }
}
