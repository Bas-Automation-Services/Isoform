using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Modeling;
using Xunit.Abstractions;

namespace Isoform.Tests;

/// <summary>
/// A body that touches itself in a point: a corner on one of its faces or edges, or two of its edges crossing. Each
/// stretch hangs from a roof that a pillar joins to the plate, so the part is one piece; at the point the material is
/// two pieces that meet in nothing but it, a contact of zero thickness. Moved a clear margin either way, it is not.
/// </summary>
public class SelfContactTests(ITestOutputHelper output)
{
    static readonly ModelingSettings Settings = new();

    FrepResult Regenerate(PartDocument doc)
    {
        var r = new FrepModeler(Settings).Regenerate(doc);
        foreach (var d in r.Diagnostics) output.WriteLine($"{d.Code} {d.FeatureId}: {d.Message}");
        return r;
    }

    static Feature[] Frame =>
    [
        new Block("base", BooleanOp.New, [0, 0, 0], [20, 20, 2]),
        new Block("pillar", BooleanOp.Join, [0, 0, 0], [2, 20, 16]),
        new Block("roof", BooleanOp.Join, [0, 0, 12], [20, 20, 4]),
    ];

    /// <summary>A cube of side 6 standing on one corner at <paramref name="at"/>, its diagonal upright, up into the roof.</summary>
    static Feature Cube(Point3D at)
    {
        var (a, c) = (Math.Sqrt(2.0 / 3), Math.Sqrt(1.0 / 3));
        return new Extrude("cube", BooleanOp.Join, new SketchPlane([at.X, at.Y, at.Z], [a, 0, c], [-a / 2, -a * Math.Sqrt(3) / 2, c]),
            new Profile([0, 0], [new ProfileEdge("a", [6, 0]), new ProfileEdge("b", [6, 6]), new ProfileEdge("c", [0, 6]), new ProfileEdge("d", [0, 0])]), 0, 6);
    }

    /// <summary>A ridge along y on the plate, its top edge at x = 10, z = 5.</summary>
    static Feature Ridge => new Extrude("ridge", BooleanOp.Join, new SketchPlane([0, 20, 0], [1, 0, 0], [0, -1, 0]),
        new Profile([6, 1], [new ProfileEdge("a", [14, 1]), new ProfileEdge("b", [10, 5]), new ProfileEdge("c", [6, 1])]), 0, 20);

    /// <summary>The repros, each touching where <paramref name="gap"/> is 0, clear of it above and running into it below.</summary>
    public static PartDocument Document(string name, double gap = 0) => name switch
    {
        "corner on a face" => new([.. Frame, Cube(new(10, 10, 2 + gap))]),
        "corner on an edge" => new([.. Frame, Ridge, Cube(new(10, 10, 5 + gap))]),
        // 0.6 mm from where the ridge's edge ends at the plate's side: within two cells of it.
        "corner on an edge by its end" => new([.. Frame, Ridge, Cube(new(10, 0.6, 5 + gap))]),
        // The ridge's edge rounded with radius 2: the round's top is 2(√2 − 1) below the edge.
        "corner on a fillet" => new([.. Frame, Ridge, new Fillet("round", ["ridge/b", "ridge/c"], 2), Cube(new(10, 10, 5 - 2 * (Math.Sqrt(2) - 1) + gap))]),
        // A square bar along x on its edge, over the ridge: the two edges cross at (10, 10, 5).
        "edges crossing" => new([.. Frame, Ridge, new Extrude("bar", BooleanOp.Join, new SketchPlane([1, 0, gap], [0, 1, 0], [1, 0, 0]),
            new Profile([10, 5], [new ProfileEdge("a", [14, 9]), new ProfileEdge("b", [10, 13]), new ProfileEdge("c", [6, 9]), new ProfileEdge("d", [10, 5])]), 0, 18)]),
        _ => throw new ArgumentException(name),
    };

    [Theory]
    [InlineData("corner on a face", "(10, 10, 2): the corner of cube/d, cube/a, cube/start lies on base/z+")]
    [InlineData("corner on an edge", "(10, 10, 5): the corner of cube/a, cube/d, cube/start lies on the edge between ridge/c and ridge/b")]
    [InlineData("corner on an edge by its end", "(10, 0.6, 5): the corner of cube/a, cube/d, cube/start lies on the edge between ridge/c and ridge/b")]
    [InlineData("corner on a fillet", "(10, 10, 4.17157): the corner of cube/start, cube/a, cube/d lies on round/face")]
    [InlineData("edges crossing", "(10, 10, 5): the edge between ridge/c and ridge/b crosses the edge between bar/d and bar/a")]
    public void A_body_that_touches_itself_in_a_point_is_reported(string name, string where)
    {
        var r = Regenerate(Document(name));

        r.Diagnostics.Should().ContainSingle().Which.Should().Be(new Diagnostic(Diagnostic.TangentContact, name == "edges crossing" ? "bar" : "cube",
            $"the solid touches itself at {where}, a contact of zero thickness"));
    }

    [Theory]
    [InlineData("corner on a face", 0.05)]
    [InlineData("corner on a face", -0.05)]
    [InlineData("corner on an edge", 0.05)]
    [InlineData("corner on an edge", -0.05)]
    [InlineData("corner on an edge by its end", 0.05)]
    [InlineData("corner on an edge by its end", -0.05)]
    [InlineData("corner on a fillet", 0.05)]
    [InlineData("corner on a fillet", -0.05)]
    [InlineData("edges crossing", 0.05)]
    [InlineData("edges crossing", -0.05)]
    public void Clear_of_the_touch_or_into_it_is_not_reported(string name, double gap) =>
        Regenerate(Document(name, gap)).Diagnostics.Should().NotContain(d => d.Code == Diagnostic.TangentContact);
}
