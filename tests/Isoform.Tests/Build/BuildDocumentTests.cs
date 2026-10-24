using System.Text.Json.Nodes;
using Isoform.Document;

namespace Isoform.Tests.Build;

/// <summary>The build document (isoform-build/0): canonical text, hash, edits, diffs and the errors it names.</summary>
public class BuildDocumentTests
{
    /// <summary>The pump stand's build (demo plan §2.1).</summary>
    internal static readonly BuildDocument Stand = new("stand.json",
    [
        new PlateSection("top", 12, Face: "cap/face"),
        new PlateSection("feet", 12, Face: "foot/z+"),
        new TubeSection("legs", 48.3, 3.2, Along: "leg/a"),
    ], 0.05, new Material("S235", 7.85e-6), [new Keepout("pump"), new Keepout("hose", 5), new Keepout("sockets")],
        ["cap/face", "mount/wall", "bore/wall", "foot/z-", "anchor/wall"]);

    static readonly string StandText = """
        {
          "format": "isoform-build/0",
          "units": "mm",
          "design": "stand.json",
          "material": {
            "name": "S235",
            "density": 7.85E-06
          },
          "tolerance": 0.05,
          "sections": [
            {
              "id": "top",
              "plate": {
                "face": "cap/face"
              },
              "thickness": 12
            },
            {
              "id": "feet",
              "plate": {
                "face": "foot/z+"
              },
              "thickness": 12
            },
            {
              "id": "legs",
              "tube": {
                "along": "leg/a"
              },
              "outside": 48.3,
              "wall": 3.2
            }
          ],
          "keepouts": [
            {
              "body": "pump",
              "clearance": 0
            },
            {
              "body": "hose",
              "clearance": 5
            },
            {
              "body": "sockets",
              "clearance": 0
            }
          ],
          "interfaces": [
            "cap/face",
            "mount/wall",
            "bore/wall",
            "foot/z-",
            "anchor/wall"
          ]
        }

        """.ReplaceLineEndings("\n");

    static string Text(BuildDocument build) => CanonicalFormat.Write(build);

    static JsonNode[] Edits(string json) => JsonNode.Parse(json)!.AsArray().Select(e => e!).ToArray();

    static string Problem(string document, string edits)
    {
        var result = DocumentEdits.TryApply(document, edits);
        result.Text.Should().BeNull();
        return result.Problem!;
    }

    [Fact]
    public void The_stand_has_one_canonical_text()
    {
        Text(Stand).Should().Be(StandText);
        CanonicalFormat.FormatOf(StandText).Should().Be(BuildDocument.FormatVersion);
        Text(CanonicalFormat.ReadBuild(StandText)).Should().Be(StandText);
        CanonicalFormat.ReadBuild(StandText).Sections.Should().BeEquivalentTo(Stand.Sections, o => o.RespectingRuntimeTypes());
    }

    [Fact]
    public void Key_order_and_layout_of_the_input_do_not_matter()
    {
        // The plan's example, keys in another order, on one line, tolerance left to its default.
        var written = """
            {"design": "stand.json", "units": "mm", "format": "isoform-build/0",
             "sections": [{"thickness": 12, "id": "top", "plate": {"face": "cap/face"}}, {"id": "feet", "plate": {"face": "foot/z+"}, "thickness": 12},
                          {"wall": 3.2, "outside": 48.3, "tube": {"along": "leg/a"}, "id": "legs"}],
             "interfaces": ["cap/face", "mount/wall", "bore/wall", "foot/z-", "anchor/wall"],
             "keepouts": [{"body": "pump", "clearance": 0}, {"body": "hose", "clearance": 5}, {"body": "sockets"}],
             "material": {"density": 7.85e-6, "name": "S235"}}
            """;

        DocumentEdits.Apply(written, []).Should().Be(StandText);
    }

    [Fact]
    public void Planes_lines_and_near_points_round_trip()
    {
        var build = new BuildDocument("frame.json",
        [
            new PlateSection("spider", 8, Plane: new SketchPlane([0, 0, 300], [1, 0, 0], [0, 0, 1]), Near: [0.1 + 0.2, 0, 300]),
            new TubeSection("brace", 33.7, 2.6, Line: [[0, 0, 0], [100, -0.0, 1.0 / 3.0]]),
        ]);

        var text = Text(build);
        var back = CanonicalFormat.ReadBuild(text);

        Text(back).Should().Be(text);
        back.Tolerance.Should().Be(BuildDocument.DefaultTolerance);
        ((PlateSection)back.Sections[0]).Near.Should().Equal(0.1 + 0.2, 0, 300);
        ((TubeSection)back.Sections[1]).Line![1].Should().Equal(100, 0, 1.0 / 3.0);
        text.Should().NotContain("material").And.NotContain("keepouts").And.NotContain("interfaces").And.NotContain("-0");
    }

    [Fact]
    public void Hash_of_the_stand_is_stable()
    {
        // Golden value: if this changes, every stored build document's identity changes with it.
        CanonicalFormat.Hash(Stand).Should().Be(GoldenStandHash);
        CanonicalFormat.Hash(CanonicalFormat.ReadBuild(StandText)).Should().Be(GoldenStandHash);
        CanonicalFormat.Hash(Stand with { Tolerance = 0.02 }).Should().NotBe(GoldenStandHash);
    }

    const string GoldenStandHash = "47abc29c1db3542d2284a196f79f2eb3386db99b56b511105e4d8b03210b830d";

    [Fact]
    public void Sections_are_edited_by_id_and_order_is_priority()
    {
        var build = DocumentEdits.Apply(Stand, Edits("""
            [
              { "set": "sections.legs.wall", "value": 4 },
              { "set": "sections.top.plate.face", "value": "under/face" },
              { "set": "sections.feet.plate.near", "value": [200, 200, 6] },
              { "remove": "sections.legs" },
              { "insert": { "id": "legs", "tube": { "along": "leg/a" }, "outside": 48.3, "wall": 4 }, "before": "top" },
              { "insert": { "id": "spider", "plate": { "plane": { "origin": [0, 0, 300], "x": [1, 0, 0], "normal": [0, 0, 1] } }, "thickness": 8 } },
              { "set": "tolerance", "value": 0.02 },
              { "set": "keepouts.1.clearance", "value": 8 },
              { "set": "material", "value": { "name": "S355", "density": 7.85e-6 } },
              { "set": "interfaces.0", "value": "under/face" },
              { "set": "design", "value": "stand-2.json" }
            ]
            """));

        build.Sections.Select(s => s.Id).Should().Equal("legs", "top", "feet", "spider");
        build.Sections[0].Should().Be(new TubeSection("legs", 48.3, 4, Along: "leg/a"));
        ((PlateSection)build.Sections[1]).Face.Should().Be("under/face");
        ((PlateSection)build.Sections[2]).Near.Should().Equal(200, 200, 6);
        ((PlateSection)build.Sections[3]).Plane!.Origin.Should().Equal(0, 0, 300);
        build.Tolerance.Should().Be(0.02);
        build.Keepouts![1].Should().Be(new Keepout("hose", 8));
        build.Material.Should().Be(new Material("S355", 7.85e-6));
        build.Interfaces![0].Should().Be("under/face");
        build.Design.Should().Be("stand-2.json");

        // A plate made a tube: the section is set whole.
        var tube = DocumentEdits.Apply(Stand, Edits("""[{ "set": "sections.top", "value": { "id": "top", "tube": { "line": [[0, 0, 0], [0, 0, 600]] }, "outside": 60.3, "wall": 4 } }]"""));
        tube.Sections[0].Should().BeOfType<TubeSection>().Which.Line![1].Should().Equal(0, 0, 600);

        var bare = DocumentEdits.Apply(Stand, Edits("""[{ "remove": "material" }, { "remove": "keepouts" }, { "remove": "interfaces" }, { "remove": "tolerance" }]"""));
        Text(bare).Should().Be(Text(new BuildDocument("stand.json", Stand.Sections)));
    }

    [Fact]
    public void The_diff_between_two_builds_gives_the_new_one_exactly()
    {
        var moved = Stand with
        {
            Sections = [Stand.Sections[2], Stand.Sections[0], new PlateSection("feet", 15, Face: "foot/z+")],
            Keepouts = [new Keepout("pump"), new Keepout("hose", 8)],
            Interfaces = Stand.Interfaces!.Select(i => i == "cap/face" ? "under/face" : i).ToList(),
            Material = null,
        };

        var edits = DocumentDiff.Between(Stand, moved);

        DocumentEdits.Apply(Text(Stand), edits).Should().Be(Text(moved));
        edits.Select(e => e.ToJsonString()).Should().Contain("""{"set":"sections.feet.thickness","value":15}""")
            .And.Contain("""{"remove":"material"}""").And.Contain("""{"set":"interfaces.0","value":"under/face"}""");
        edits.Should().NotContain(e => (string?)e["set"] == "", "a reorder is a move, not a new document");
        DocumentDiff.Between(Stand, Stand).Should().BeEmpty();
    }

    [Fact]
    public void A_bundle_carries_a_build_and_edits_it_by_name()
    {
        var bundle = new Bundle(new AssemblyDocument([new Component("top", "top.json", Placement.Identity)]),
            new Dictionary<string, string> { ["build.json"] = Text(Stand), ["top.json"] = CanonicalFormat.Write(new PartDocument([new Block("plate", BooleanOp.New, [0, 0, 0], [10, 10, 12])])) });
        var text = CanonicalFormat.Write(bundle);

        var edited = DocumentEdits.Apply(CanonicalFormat.ReadBundle(text), Edits("""[{ "document": "build.json", "set": "sections.feet.thickness", "value": 15 }]"""));

        ((PlateSection)CanonicalFormat.ReadBuild(edited.Documents["build.json"]).Sections[1]).Thickness.Should().Be(15);
        DocumentDiff.Between(CanonicalFormat.ReadBundle(text), edited).Select(e => e.ToJsonString())
            .Should().Equal("""{"document":"build.json","set":"sections.feet.thickness","value":15}""");
    }

    [Theory]
    [InlineData("""{"format": "isoform-build/0", "units": "inch", "design": "d.json", "sections": []}""", "Units must be mm")]
    [InlineData("""{"format": "isoform-build/0", "units": "mm", "sections": []}""", "needs 'design'")]
    [InlineData("""{"format": "isoform-build/0", "units": "mm", "design": "d.json"}""", "needs its sections")]
    [InlineData("""{"format": "isoform-build/0", "units": "mm", "design": "d.json", "tolerance": 0, "sections": []}""", "tolerance must be positive")]
    [InlineData("""{"format": "isoform-build/0", "units": "mm", "design": "d.json", "material": {"name": "S235", "density": -1}, "sections": []}""", "density must be positive")]
    [InlineData("""{"format": "isoform-build/0", "units": "mm", "design": "d.json", "sections": [], "keepouts": [{"body": "pump", "clearance": -1}]}""", "Keep-out 'pump'")]
    [InlineData("""{"format": "isoform-build/0", "units": "mm", "design": "d.json", "sections": [{"id": "a", "plate": {"face": "f"}, "thickness": 1}, {"id": "a", "plate": {"face": "g"}, "thickness": 1}]}""", "'a' is used twice")]
    [InlineData("""{"format": "isoform-build/0", "units": "mm", "design": "d.json", "sections": [{"id": "a.b", "plate": {"face": "f"}, "thickness": 1}]}""", "without '.' or '/'")]
    [InlineData("""{"format": "isoform-build/0", "units": "mm", "design": "d.json", "sections": [{"id": "a", "thickness": 1}]}""", "Section 'a': a section is a plate or a tube")]
    [InlineData("""{"format": "isoform-build/0", "units": "mm", "design": "d.json", "sections": [{"id": "a", "plate": {}, "thickness": 1}]}""", "Section 'a': a plate is on a face or on a plane")]
    [InlineData("""{"format": "isoform-build/0", "units": "mm", "design": "d.json", "sections": [{"id": "a", "plate": {"face": "f"}}]}""", "Section 'a': a plate needs a thickness")]
    [InlineData("""{"format": "isoform-build/0", "units": "mm", "design": "d.json", "sections": [{"id": "a", "plate": {"face": "f"}, "thickness": 0}]}""", "Section 'a': the thickness must be positive")]
    [InlineData("""{"format": "isoform-build/0", "units": "mm", "design": "d.json", "sections": [{"id": "a", "tube": {"along": "f", "line": [[0, 0, 0], [0, 0, 1]]}, "outside": 10, "wall": 1}]}""", "Section 'a': a tube runs along a cylinder or a line")]
    [InlineData("""{"format": "isoform-build/0", "units": "mm", "design": "d.json", "sections": [{"id": "a", "tube": {"line": [[0, 0, 0]]}, "outside": 10, "wall": 1}]}""", "Section 'a': a tube's line is two points")]
    [InlineData("""{"format": "isoform-build/0", "units": "mm", "design": "d.json", "sections": [{"id": "a", "tube": {"along": "f"}, "wall": 1}]}""", "Section 'a': a tube needs its outside diameter and wall")]
    [InlineData("""{"format": "isoform-build/0", "units": "mm", "design": "d.json", "sections": [{"id": "a", "tube": {"along": "f"}, "outside": 10, "wall": 6}]}""", "Section 'a': the wall must be positive")]
    public void Documents_that_cannot_be_built_from_say_why(string text, string problem)
    {
        FluentActions.Invoking(() => CanonicalFormat.ReadBuild(text)).Should().Throw<FormatException>().WithMessage($"*{problem}*");
        Problem(text, "[]").Should().Contain(problem);
    }

    [Fact]
    public void Edits_that_cannot_be_applied_are_named()
    {
        var stand = Text(Stand);

        Problem(stand, """[{ "set": "sections.arms.wall", "value": 4 }]""").Should().Contain("'arms' is not a section id");
        Problem(stand, """[{ "set": "sections.legs.wal", "value": 4 }]""").Should().Contain("'sections.legs.wal' is not a field");
        Problem(stand, """[{ "set": "tolerence", "value": 0.1 }]""").Should().Contain("'tolerence' is not a field of a build document");
        Problem(stand, """[{ "set": "sections.legs.wall", "value": 30 }]""").Should().Contain("Section 'legs': the wall must be positive");
        Problem(stand, """[{ "set": "sections.top.tube", "value": { "along": "leg/a" } }]""").Should().Contain("a plate or a tube, one of the two");
        Problem(stand, """[{ "insert": { "id": "top", "plate": { "face": "x" }, "thickness": 1 } }]""").Should().Contain("'top' is used twice");
        Problem(stand, """[{ "insert": { "plate": { "face": "x" }, "thickness": 1 } }]""").Should().Contain("with an id");
        Problem(stand, """[{ "insert": { "id": "x", "plate": { "face": "x" }, "thickness": 1 }, "before": "arms" }]""").Should().Contain("'arms' is not a section id");
        Problem(stand, """[{ "remove": "design" }]""").Should().Contain("needs 'design'");
        Problem(stand, """[{ "set": "keepouts.7.clearance", "value": 1 }]""").Should().Contain("no entry '7'");
    }

    [Fact]
    public void Random_edits_never_throw()
    {
        var rng = new Random(11);
        var stand = Text(Stand);
        string[] heads = ["sections", "sections.top", "sections.legs", "sections.nope", "design", "material", "tolerance", "keepouts", "interfaces", "", "units", "x"];
        string[] tails = ["plate", "tube", "face", "along", "line", "thickness", "outside", "wall", "near", "0", "1", "7", "name", "density", "clearance", "id"];
        JsonNode?[] values = [null, 0, 1, -5, 1e300, "abc", "leg/a", true, new JsonArray(1, 2), new JsonArray(1, 2, 3), new JsonObject(), new JsonObject { ["id"] = "q" },
            new JsonObject { ["id"] = "q", ["plate"] = new JsonObject { ["face"] = "f" }, ["thickness"] = 3 }, new JsonArray()];

        for (var k = 0; k < 2000; k++)
        {
            var path = string.Join('.', new[] { heads[rng.Next(heads.Length)] }.Concat(Enumerable.Range(0, rng.Next(3)).Select(_ => tails[rng.Next(tails.Length)]))).Trim('.');
            var value = values[rng.Next(values.Length)]?.DeepClone();
            JsonObject edit = rng.Next(3) switch
            {
                0 => new() { ["set"] = path, ["value"] = value },
                1 => new() { ["insert"] = value, ["before"] = new[] { "top", "legs", "nope" }[rng.Next(3)] },
                _ => new() { ["remove"] = path },
            };
            var result = DocumentEdits.TryApply(stand, [edit]);
            (result.Text is null).Should().Be(result.Problem is not null, edit.ToJsonString());
        }
    }
}
