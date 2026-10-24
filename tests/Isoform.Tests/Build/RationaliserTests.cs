using System.Text.Json.Nodes;
using BasAutomation.Geometry;
using Isoform.Build;
using Isoform.Document;
using Isoform.Modeling;

namespace Isoform.Tests.Build;

/// <summary>
/// The rationaliser with fake build methods: sections in priority order, each against the design minus the hulls of
/// the parts before it, names bound to design faces, and the bundle the parts leave in.
/// </summary>
public class RationaliserTests
{
    static readonly FrepModeler Modeler = new(new ModelingSettings { CellSize = 2 });

    // A slab with three posts on it (a pattern), and a keep-out body above them.
    static readonly PartDocument Frame = new(
    [
        new Block("slab", BooleanOp.New, [0, 0, 0], [100, 40, 10]) { Body = "frame" },
        new Block("post", BooleanOp.Join, [10, 10, 10], [20, 20, 50]) { Body = "frame" },
        new Pattern("posts", ["post"], new LinearMotion([1, 0, 0], 30, 3)),
        new Block("box", BooleanOp.New, [0, 0, 80], [100, 40, 20]) { Body = "keep" },
    ]);

    static readonly FrepResult Regenerated = Modeler.Regenerate(Frame);

    static readonly BuildDocument Build = new("frame.json",
    [
        new PlateSection("caps", 5, Face: "post/z+"),
        new TubeSection("posts", 20, 2, Along: "post/x+"),
        new PlateSection("slab", 10, Face: "slab/z-"),
    ], Keepouts: [new Keepout("keep", 5)], Interfaces: ["slab/z-"]);

    static PartDocument Plate(double x, double y, double t) => new([new Block("plate", BooleanOp.New, [0, 0, 0], [x, y, t])]);

    static Placement At(double x, double y, double z) => new([x, y, z], [1, 0, 0], [0, 0, 1]);

    /// <summary>A method that answers from a function and remembers what it was given.</summary>
    sealed class Fake(Func<BuildSection, BuildContext, SectionResult> build) : IBuildMethod
    {
        public List<(string Section, BuildContext Context)> Calls { get; } = [];

        public SectionResult Build(BuildSection section, BuildContext context)
        {
            Calls.Add((section.Id, context));
            return build(section, context);
        }
    }

    // A cap on each post top, the posts as plain bars below the caps, the slab as one plate.
    static Fake Plates() => new((s, _) => s.Id == "caps"
        ? new([.. Enumerable.Range(0, 3).Select(k => BuiltPart.Of("cap", Plate(20, 20, 5), At(10 + 30 * k, 10, 55)))], [])
        : new([BuiltPart.Of("slab", Plate(100, 40, 10), At(0, 0, 0))], []));

    static Fake Tubes() => new((s, _) => new([.. Enumerable.Range(0, 3).Select(k =>
        BuiltPart.Of("post", Plate(20, 20, 45 - k), At(10 + 30 * k, 10, 10), hull: Plate(20, 20, 45)))], []));

    [Fact]
    public void Sections_run_in_order_each_cut_by_the_parts_before_it()
    {
        var (plates, tubes) = (Plates(), Tubes());

        new Rationaliser(plates, tubes).Run(Build, Frame, Regenerated).Diagnostics.Should().BeEmpty();

        var calls = plates.Calls.Concat(tubes.Calls).OrderBy(c => c.Context.Earlier.Count).ToList();
        calls.Select(c => c.Section).Should().Equal("caps", "posts", "slab");
        calls.Select(c => c.Context.Earlier.Count).Should().Equal(0, 3, 6);
        calls[2].Context.Earlier.Select(p => p.Name).Should().Equal("cap", "cap", "cap", "post", "post", "post");

        // The top of a post is in the design until the caps take it; the slab is left to the last section.
        var (capped, post, slab) = (new Point3D(20, 20, 57), new Point3D(50, 20, 30), new Point3D(50, 20, 5));
        calls[0].Context.Remaining.Value(capped).Should().BeNegative();
        calls[1].Context.Remaining.Value(capped).Should().BePositive();
        calls[1].Context.Remaining.Value(post).Should().BeNegative();
        calls[2].Context.Remaining.Value(post).Should().BePositive();
        calls[2].Context.Remaining.Value(slab).Should().BeNegative();
    }

    [Fact]
    public void Moving_a_section_first_gives_it_priority()
    {
        var (plates, tubes) = (Plates(), Tubes());
        var reordered = DocumentEdits.Apply(Build, [JsonNode.Parse("""{ "remove": "sections.posts" }""")!,
            JsonNode.Parse("""{ "insert": { "id": "posts", "tube": { "along": "post/x+" }, "outside": 20, "wall": 2 }, "before": "caps" }""")!]);

        new Rationaliser(plates, tubes).Run(reordered, Frame, Regenerated);

        tubes.Calls.Single().Context.Earlier.Should().BeEmpty();
        plates.Calls.First().Context.Earlier.Select(p => p.Name).Should().Equal("post", "post", "post");
        plates.Calls.First().Context.Remaining.Value(new Point3D(20, 20, 57)).Should().BeNegative("the bars' hulls stop below the post tops");
    }

    [Fact]
    public void The_bundle_holds_the_design_the_build_and_each_distinct_part_once()
    {
        var result = new Rationaliser(Plates(), Tubes()).Run(Build, Frame, Regenerated);
        var bundle = result.Bundle;

        bundle.Documents.Keys.Order(StringComparer.Ordinal).Should().Equal("build.json", "cap.json", "frame.json", "post-2.json", "post-3.json", "post.json", "slab.json");
        bundle.Documents["frame.json"].Should().Be(CanonicalFormat.Write(Frame));
        bundle.Documents["build.json"].Should().Be(CanonicalFormat.Write(Build));
        result.Parts.Keys.Should().BeEquivalentTo("cap.json", "post.json", "post-2.json", "post-3.json", "slab.json");

        var components = bundle.Assembly.Components;
        components.Select(c => c.Id).Should().Equal("caps-1", "caps-2", "caps-3", "posts-1", "posts-2", "posts-3", "slab");
        components.Select(c => c.Part).Should().Equal("cap.json", "cap.json", "cap.json", "post.json", "post-2.json", "post-3.json", "slab.json");
        components.Should().OnlyContain(c => c.Hash == CanonicalFormat.Hash(CanonicalFormat.Read(bundle.Documents[c.Part])));
        components[2].Placement.Origin.Should().Equal(70, 10, 55);
        result.Instances.Select(i => i.Section).Should().Equal("caps", "caps", "caps", "posts", "posts", "posts", "slab");

        // The bundle regenerates as an assembly: every part found, every pin holds.
        var assembled = new AssemblyModeler(new ModelingSettings { CellSize = 2 }).Regenerate(bundle.Assembly, p => bundle.Documents.GetValueOrDefault(p));
        assembled.Diagnostics.Should().BeEmpty();
        assembled.Instances.Should().HaveCount(7);
        assembled.Instances[2].Field!.Value(new Point3D(80, 20, 57)).Should().BeNegative("the third cap sits on the third post");
    }

    [Fact]
    public void The_same_inputs_give_the_same_bundle_byte_for_byte()
    {
        var text = CanonicalFormat.Write(new Rationaliser(Plates(), Tubes()).Run(Build, Frame, Regenerated).Bundle);

        CanonicalFormat.Write(new Rationaliser(Plates(), Tubes()).Run(Build, Frame, Regenerated).Bundle).Should().Be(text);
        CanonicalFormat.Write(CanonicalFormat.ReadBundle(text)).Should().Be(text);
        DocumentEdits.Apply(text, []).Should().Be(text, "every document in it is canonical, the build document too");
    }

    [Fact]
    public void Names_bind_faces_and_their_pattern_copies()
    {
        var design = new Design(Frame, Regenerated, ["keep"]);

        design.Faces("post/z+").Should().HaveCount(3);
        design.Faces("posts/2:post/z+").Should().ContainSingle();
        design.Faces("slab/z-").Should().ContainSingle();
        design.Faces("box/z+").Should().ContainSingle("a keep-out's faces are still the design's");
        design.Faces("post/top").Should().BeEmpty();

        // The keep-out is not part of the design's solid.
        var inBox = new Point3D(50, 20, 90);
        Regenerated.Part.Field!.Value(inBox).Should().BeNegative();
        design.Field.Value(inBox).Should().BePositive();
        design.Field.Value(new Point3D(50, 20, 5)).Should().BeNegative();
    }

    [Fact]
    public void Missing_names_and_empty_sections_are_reported_by_section()
    {
        var (plates, tubes) = (Plates(), new Fake((s, _) => new([], [])));
        var build = Build with
        {
            Sections = [new PlateSection("caps", 5, Face: "pillar/z+"), .. Build.Sections.Skip(1)],
            Keepouts = [new Keepout("crane")],
            Interfaces = ["slab/z-", "bore/wall"],
        };

        var result = new Rationaliser(plates, tubes).Run(build, Frame, Regenerated);

        result.Diagnostics.Select(d => (d.Code, d.FeatureId)).Should().Equal(
            (BuildDiagnostic.FaceMissing, null), (BuildDiagnostic.FaceMissing, null), (BuildDiagnostic.FaceMissing, "caps"), (BuildDiagnostic.RegionEmpty, "posts"));
        result.Diagnostics.Select(d => d.Message).Should().Contain(m => m.Contains("'crane'")).And.Contain(m => m.Contains("'bore/wall'")).And.Contain(m => m.Contains("'pillar/z+'"));
        plates.Calls.Select(c => c.Section).Should().Equal(["slab"], "a section bound to a missing face is not built");
        result.Bundle.Assembly.Components.Select(c => c.Id).Should().Equal("slab");
    }

    [Fact]
    public void A_method_says_why_it_made_nothing()
    {
        var refusal = new Diagnostic(BuildDiagnostic.TubeMeetsTube, "posts", "an end of posts meets another tube");
        var tubes = new Fake((s, _) => new([], [refusal]));

        var result = new Rationaliser(Plates(), tubes).Run(Build, Frame, Regenerated);

        result.Diagnostics.Should().Equal(refusal);
    }

    [Fact]
    public void A_hull_can_be_more_than_its_part()
    {
        // A tube's hull is its outer solid with the bore filled: here a block with a hole, whose hull is the block.
        var bored = new PartDocument([new Block("bar", BooleanOp.New, [0, 0, 0], [20, 20, 10]), new Hole("bore", [10, 10], 6)]);
        var part = BuiltPart.Of("bar", bored, At(100, 0, 0), hull: Plate(20, 20, 10));
        var plain = BuiltPart.Of("bar", bored, At(100, 0, 0));

        part.Hull.Value(new Point3D(110, 10, 5)).Should().BeNegative();
        plain.Hull.Value(new Point3D(110, 10, 5)).Should().BePositive();
        plain.Hull.Value(new Point3D(102, 2, 5)).Should().BeNegative();
        part.Document.Should().BeSameAs(bored);
    }
}
