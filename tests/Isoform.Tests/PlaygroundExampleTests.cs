using Isoform.Modeling;
using Isoform.Playground;

namespace Isoform.Tests;

/// <summary>Every playground example regenerates cleanly: each body one watertight solid.</summary>
public class PlaygroundExampleTests
{
    public static IEnumerable<object[]> Names => Examples.All.Select(e => new object[] { e.Name });

    [Theory]
    [MemberData(nameof(Names))]
    public void Example_regenerates_cleanly(string name)
    {
        var doc = Examples.All.Single(e => e.Name == name).Doc;

        var result = new FrepModeler(new ModelingSettings()).Regenerate(doc);

        result.Diagnostics.Should().BeEmpty();
        result.Topology!.BodyCount.Should().Be(result.Bodies.Count);
        result.Mesh!.IsWatertight().Should().BeTrue();
    }
}
