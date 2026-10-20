using Isoform.Modeling;
using Xunit.Abstractions;

namespace Isoform.Tests;

public class FrepTopologyTests(ITestOutputHelper output)
{
    [Fact]
    public void Base_part_has_the_brep_face_edge_and_vertex_counts()
    {
        var result = new FrepModeler(new ModelingSettings()).Regenerate(EditSuite.Base);
        var t = result.Topology!;
        Dump(t);

        // Block: 6 faces + hole wall + fillet. Edges: 12 box edges − 1 filleted + 4 around the fillet + 2 hole circles.
        // Vertices: 8 corners, the 2 at the filleted edge's ends each become 2.
        t.Faces.Should().HaveCount(8);
        t.Edges.Should().HaveCount(17);
        t.Vertices.Should().HaveCount(10);
        t.BodyCount.Should().Be(1);
    }

    void Dump(Topology t)
    {
        foreach (var f in t.Faces) output.WriteLine($"F{f.Index} [{string.Join(", ", f.Sources)}] body {f.Body}");
        foreach (var e in t.Edges) output.WriteLine($"E{e.Index} F{e.FaceA}–F{e.FaceB}");
        foreach (var v in t.Vertices) output.WriteLine($"V{v.Index} faces {string.Join(",", v.Faces)}");
    }
}
