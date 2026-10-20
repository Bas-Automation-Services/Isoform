using Isoform.Document;
using Isoform.Meshing;
using Isoform.Modeling;

namespace Isoform.Tests;

/// <summary>Parts with several named solid bodies: each its own solid, features aimed at one of them.</summary>
public class MultiBodyTests
{
    static readonly FrepModeler Modeler = new(new ModelingSettings { CellSize = 0.5 });
    static readonly Block Base = new("base", BooleanOp.New, [0, 0, 0], [100, 50, 10]);
    static readonly Block Post = new("post", BooleanOp.New, [40, 15, 5], [20, 20, 30]);

    [Fact]
    public void Overlapping_bodies_stay_separate_solids()
    {
        var result = Modeler.Regenerate(new PartDocument([Base, Post]));

        result.Diagnostics.Should().BeEmpty();
        result.Bodies.Select(b => b.Name).Should().Equal("base", "post");
        result.Topology!.Faces.Should().HaveCount(12);
        result.Topology.BodyCount.Should().Be(2);
        // Each body keeps the material they share: the overlap counts twice.
        SignedVolume(result.Mesh!).Should().BeApproximately(100 * 50 * 10 + 20 * 20 * 30, 1e-6);
        result.Bodies.Single(b => b.Name == "post").Faces.Select(f => result.Topology.Faces[f].Sources[0]).Should().OnlyContain(s => s.StartsWith("post/"));
    }

    [Fact]
    public void Features_work_on_the_current_body_unless_they_name_one()
    {
        var result = Modeler.Regenerate(new PartDocument(
        [
            Base,
            Post,
            new Hole("through-post", [50, 25], 6),                         // the current body: post
            new Hole("through-base", [10, 25], 6) { Body = "base" },       // names base, which becomes current
            new Hole("also-base", [90, 25], 6),
        ]));

        result.Diagnostics.Should().BeEmpty();
        string BodyOf(string source) => result.Bodies.Single(b => b.Faces.Any(f => result.Topology!.Faces[f].Sources.Contains(source))).Name;
        BodyOf("through-post/wall").Should().Be("post");
        BodyOf("through-base/wall").Should().Be("base");
        BodyOf("also-base/wall").Should().Be("base");
    }

    [Fact]
    public void Fillets_find_their_body_from_their_faces()
    {
        var result = Modeler.Regenerate(new PartDocument([Base, Post, new Fillet("round", ["base/z+", "base/x+"], 2)]));

        result.Diagnostics.Should().BeEmpty();
        result.Bodies.Single(b => b.Name == "base").Faces.Select(f => result.Topology!.Faces[f].Sources[0]).Should().Contain("round/face");
    }

    [Fact]
    public void Combine_joins_or_cuts_one_body_with_another()
    {
        var joined = Modeler.Regenerate(new PartDocument([Base, Post, new CombineBodies("weld", "post", BooleanOp.Join) { Body = "base" }]));
        joined.Diagnostics.Should().BeEmpty();
        joined.Bodies.Select(b => b.Name).Should().Equal("base");
        SignedVolume(joined.Mesh!).Should().BeApproximately(100 * 50 * 10 + 20 * 20 * 25, 1e-6);
        joined.Topology!.Faces.SelectMany(f => f.Sources).Should().Contain("post/z+", "the tool's faces keep their names");

        var cut = Modeler.Regenerate(new PartDocument([Base, Post, new CombineBodies("socket", "post", BooleanOp.Cut, Keep: true) { Body = "base" }]));
        cut.Diagnostics.Should().BeEmpty();
        cut.Bodies.Select(b => b.Name).Should().Equal("base", "post");
        SignedVolume(cut.Mesh!).Should().BeApproximately(100 * 50 * 10 - 20 * 20 * 5 + 20 * 20 * 30, 1e-6);
    }

    [Fact]
    public void Pattern_copies_go_into_the_body_of_what_they_copy()
    {
        var result = Modeler.Regenerate(new PartDocument(
        [
            Base,
            new Hole("h", [10, 25], 4),
            Post,
            new Pattern("row", ["h"], new LinearMotion([1, 0, 0], 20, 3)),
        ]));

        result.Diagnostics.Should().BeEmpty();
        var baseFaces = result.Bodies.Single(b => b.Name == "base").Faces.SelectMany(f => result.Topology!.Faces[f].Sources);
        baseFaces.Should().Contain(["row/1:h/wall", "row/2:h/wall"]);
    }

    [Theory]
    [InlineData("twice", "already is a body named 'base'")]
    [InlineData("unknown", "no body named 'frame'")]
    [InlineData("used up", "no body named 'post'")]
    public void Bad_body_references_are_rejected(string kind, string message)
    {
        Feature[] features = kind switch
        {
            "twice" => [Base, Base with { Id = "again", Body = "base" }],
            "unknown" => [Base, new Hole("h", [10, 25], 4) { Body = "frame" }],
            _ => [Base, Post, new CombineBodies("weld", "post", BooleanOp.Join) { Body = "base" }, new Hole("h", [50, 25], 4) { Body = "post" }],
        };

        var result = Modeler.Regenerate(new PartDocument(features));

        result.Diagnostics.Should().ContainSingle().Which.Message.Should().Contain(message);
    }

    [Fact]
    public void Bodies_round_trip_and_single_body_documents_keep_their_text()
    {
        var doc = new PartDocument([Base, Post with { Body = "column" }, new CombineBodies("weld", "column", BooleanOp.Cut, Keep: true) { Body = "base" }]);
        var text = CanonicalFormat.Write(doc);
        text.Should().Contain("\"body\": \"column\"").And.Contain("\"keep\": true");
        CanonicalFormat.Write(CanonicalFormat.Read(text)).Should().Be(text);
        CanonicalFormat.Write(EditSuite.Base).Should().NotContain("\"body\"");
    }

    static double SignedVolume(Mesh mesh)
    {
        var v = mesh.Vertices;
        return mesh.Triangles().Sum(t => v[t.A].AsVector().Dot(v[t.B].AsVector().Cross(v[t.C].AsVector()))) / 6;
    }
}
