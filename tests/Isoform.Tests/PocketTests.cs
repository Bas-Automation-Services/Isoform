using BasAutomation.Geometry;
using Isoform.Meshing;
using Isoform.Modeling;

namespace Isoform.Tests;

/// <summary>
/// K7: pockets, the points of cells without a vertex where the field has the other sign than at the cell's corners.
/// The whole octree and its regions find the same ones, bit for bit, and they survive the region cache.
/// </summary>
public class PocketTests
{
    static readonly ModelingSettings Settings = new();

    // The sliver body beside a small hole at the plate's corner, and the ball's cap cut off by a slab.
    const string Sliver = """[{"id":"plate","type":"block","op":"new","origin":[0,0,0],"size":[55.9,10,10]},{"id":"hole3","type":"hole","center":[55.0465,0.8535],"diameter":2.4}]""";
    const string Cap = """[{"id":"plate","type":"block","op":"new","origin":[0,0,0],"size":[60,20,10]},{"id":"ball","type":"revolve","op":"join","plane":{"origin":[30,10,10],"x":[1,0,0],"normal":[0,-1,0]},"profile":{"start":[0,-5],"edges":[{"id":"arc","to":[0,5],"bulge":1},{"id":"axis","to":[0,-5]}]},"angle":360},{"id":"slab","type":"block","op":"cut","origin":[22,2,10.5],"size":[16,16,4.493]}]""";

    static CompiledBody Body(string features) => new Compiler(Settings).Compile(RobustnessDocuments.Parse(features)).Bodies[0];

    [Theory]
    [InlineData(Sliver, false)]
    [InlineData(Sliver, true)]
    [InlineData(Cap, false)]
    public void Regions_find_the_pockets_of_the_whole_octree(string features, bool snap)
    {
        var b = Body(features);
        var whole = DualContouring.Build(b.Field, b.Min, b.Max, Settings.CellSize, Settings.ChordTolerance, snapGrid: snap);
        var regions = DualContouring.Plan(b.Field, b.Min, b.Max, Settings.CellSize, Settings.ChordTolerance, snap).Build();

        whole.Pockets.Should().Contain(p => p.Value < -Settings.Tolerance);
        regions.Pockets.Select(Bits).Should().Equal(whole.Pockets.Select(Bits));
    }

    [Fact]
    public void Pockets_survive_the_region_cache()
    {
        var b = Body(Sliver);
        var plan = DualContouring.Plan(b.Field, b.Min, b.Max, Settings.CellSize, Settings.ChordTolerance);
        var cache = new RegionCache();
        var direct = plan.Build(cache);
        var other = new RegionCache();
        other.Import(cache.TakeComputed());

        plan.Build(other).Pockets.Select(Bits).Should().Equal(direct.Pockets.Select(Bits));
    }

    static (long, long, long, long, long) Bits(Pocket p) =>
        (BitConverter.DoubleToInt64Bits(p.Location.X), BitConverter.DoubleToInt64Bits(p.Location.Y), BitConverter.DoubleToInt64Bits(p.Location.Z),
            BitConverter.DoubleToInt64Bits(p.Value), BitConverter.DoubleToInt64Bits(p.Corner));
}
