using System.Text.Json;
using System.Text.Json.Nodes;
using Isoform.Document;
using Isoform.Mcp;
using ModelContextProtocol;

namespace Isoform.Mcp.Tests;

/// <summary>The tool handlers called directly: pure functions from arguments to JSON.</summary>
public class ToolTests
{
    static readonly Block Plate = FormatGuide.Plate;

    static string Doc(params Feature[] features) => CanonicalFormat.Write(new PartDocument(features));

    static JsonNode Parse(string json) => JsonNode.Parse(json)!;

    static JsonElement[] Edits(params string[] edits) => edits.Select(e => JsonDocument.Parse(e).RootElement).ToArray();

    static string[] Strings(JsonNode? array) =>
        array!.AsArray().Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n!.ToJsonString()).ToArray();

    static string Temp(string name) => Path.Combine(Directory.CreateTempSubdirectory("isoform-mcp-").FullName, name);

    // ---- regenerate ----

    [Fact]
    public void Regenerate_names_every_face_and_measures_the_part()
    {
        var r = Parse(IsoformTools.Regenerate(Doc(Plate, new Hole("hole", [30, 25], 10), new Fillet("round", ["plate/z+", "plate/x+"], 2)), density: 2));

        r["ok"]!.GetValue<bool>().Should().BeTrue();
        r["hash"]!.GetValue<string>().Should().HaveLength(64);
        r["faces"]!.GetValue<int>().Should().Be(8);
        var body = r["bodies"]![0]!;
        body["name"]!.GetValue<string>().Should().Be("plate");
        Strings(body["faces"]).Should().BeEquivalentTo("plate/x-", "plate/x+", "plate/y-", "plate/y+", "plate/z-", "plate/z+", "hole/wall", "round/face");
        // The plate less the hole and the material the fillet rounds off.
        var volume = 100 * 50 * 10 - Math.PI * 25 * 10 - (4 - Math.PI) * 50;
        // At the default cells the mesh-based estimate is within ~2e-7 of the exact volume.
        body["volume"]!.GetValue<double>().Should().BeApproximately(volume, 0.02);
        body["mass"]!.GetValue<double>().Should().BeApproximately(2 * volume, 0.04);
        Strings(r["box"]!["min"]).Should().Equal("0", "0", "0");
        Strings(r["box"]!["max"]).Should().Equal("100", "50", "10");
        r["ms"]!["regenerate"].Should().NotBeNull();
    }

    [Fact]
    public void Regenerate_reports_an_invalid_document_as_diagnostics()
    {
        var r = Parse(IsoformTools.Regenerate(Doc(Plate, new Hole("hole", [30, 25], 0))));

        r["ok"]!.GetValue<bool>().Should().BeFalse();
        r["diagnostics"]![0]!["code"]!.GetValue<string>().Should().Be("invalid-parameter");
        r["diagnostics"]![0]!["feature"]!.GetValue<string>().Should().Be("hole");
        r["bodies"].Should().BeNull();
    }

    [Fact]
    public void Regenerate_reads_a_path_and_certifies_the_volume()
    {
        var path = Temp("plate.json");
        File.WriteAllText(path, Doc(Plate));

        var body = Parse(IsoformTools.Regenerate(path, certifyVolume: true))["bodies"]![0]!;

        var bounds = body["volumeBounds"]!.AsArray().Select(n => n!.GetValue<double>()).ToArray();
        bounds[0].Should().BeLessThanOrEqualTo(50_000);
        bounds[1].Should().BeGreaterThanOrEqualTo(50_000);
    }

    [Fact]
    public void Bad_input_is_a_tool_error_with_the_reason()
    {
        var notJson = () => IsoformTools.Regenerate("{ not json");
        var unknownType = () => IsoformTools.Regenerate("""{"format":"isoform/0","units":"mm","features":[{"id":"a","type":"sphere"}]}""");
        var assembly = () => IsoformTools.Regenerate(CanonicalFormat.Write(FormatGuide.BundleExample));

        notJson.Should().Throw<McpException>();
        unknownType.Should().Throw<McpException>().WithMessage("*Unknown feature type 'sphere'*");
        assembly.Should().Throw<McpException>().WithMessage("*assembly tool*");
    }

    // ---- apply_edits ----

    [Fact]
    public void A_parameter_edit_keeps_every_face_name()
    {
        var r = Parse(IsoformTools.ApplyEdits(Doc(Plate, new Hole("hole", [30, 25], 10)), Edits("""{"set": "hole.diameter", "value": 12}""")));

        r["hash"]!.GetValue<string>().Should().NotBe(r["previousHash"]!.GetValue<string>());
        // The compact document reads back to the document the hash is of.
        var edited = CanonicalFormat.Read(r["document"]!.ToJsonString());
        ((Hole)edited["hole"]).Diameter.Should().Be(12);
        CanonicalFormat.Hash(edited).Should().Be(r["hash"]!.GetValue<string>());
        r["result"]!["ok"]!.GetValue<bool>().Should().BeTrue();
        r["result"]!["bodies"]![0]!["volume"]!.GetValue<double>().Should().BeApproximately(50_000 - Math.PI * 36 * 10, 1e-3);
        r["references"]!["kept"]!.GetValue<int>().Should().Be(7);
        r["references"]!["appeared"]!.AsArray().Should().BeEmpty();
        r["references"]!["gone"]!.AsArray().Should().BeEmpty();
        r["references"]!["changed"]!.AsObject().Should().BeEmpty();
    }

    [Fact]
    public void Inserting_and_removing_features_reports_what_appeared_split_and_went()
    {
        var doc = Doc(Plate, new Fillet("round", ["plate/z+", "plate/x+"], 2));
        // A slot across the top splits plate/z+ in two; the fillet goes.
        var r = Parse(IsoformTools.ApplyEdits(doc, Edits(
            """{"insert": {"id": "slot", "type": "block", "op": "cut", "origin": [45, -1, 5], "size": [10, 52, 10]}}""",
            """{"remove": "round"}""")));

        Strings(r["references"]!["appeared"]).Should().Equal("slot/x+", "slot/x-", "slot/z-");
        Strings(r["references"]!["gone"]).Should().Equal("round/face");
        r["references"]!["changed"]!["plate/z+"]!.GetValue<string>().Should().Be("resolved -> split");
    }

    [Fact]
    public void Edits_can_write_the_document_to_a_file_and_skip_regeneration()
    {
        var path = Temp("edited.json");

        var r = Parse(IsoformTools.ApplyEdits(Doc(Plate), Edits("""{"set": "plate.size.2", "value": 12}"""), regenerate: false, outputPath: path));

        r["document"].Should().BeNull();
        r["result"].Should().BeNull();
        ((Block)CanonicalFormat.Read(File.ReadAllText(path))["plate"]).Size.Should().Equal(100, 50, 12);
        CanonicalFormat.Hash(CanonicalFormat.Read(File.ReadAllText(path))).Should().Be(r["hash"]!.GetValue<string>());
    }

    [Fact]
    public void A_failing_edit_is_a_tool_error_naming_it_and_nothing_is_written()
    {
        var path = Temp("edited.json");
        var edit = () => IsoformTools.ApplyEdits(Doc(Plate), Edits("""{"set": "plate.size.2", "value": 12}""", """{"set": "hole.diameter", "value": 12}"""), outputPath: path);

        edit.Should().Throw<McpException>().WithMessage("edit 2 *'hole' is not a feature id*");
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public void Edits_on_a_bundle_change_its_assembly_and_its_parts()
    {
        var r = Parse(IsoformTools.ApplyEdits(CanonicalFormat.Write(FormatGuide.BundleExample), Edits(
            """{"remove": "mates.stop"}""",
            """{"document": "bracket.json", "set": "hole.diameter", "value": 10.5}""")));

        var bundle = CanonicalFormat.ReadBundle(r["document"]!.ToJsonString());
        bundle.Assembly.Mates.Should().ContainSingle();
        ((Hole)CanonicalFormat.Read(bundle.Documents["bracket.json"])["hole"]).Diameter.Should().Be(10.5);
        r["result"]!["ok"]!.GetValue<bool>().Should().BeTrue();
        r["result"]!["freeDegrees"]!.GetValue<int>().Should().Be(2, "without its stop the pin slides as well as turns");
        r["references"].Should().BeNull("face stability is a part's");
    }

    [Fact]
    public void Edits_on_an_assembly_file_regenerate_it_with_the_parts_beside_it()
    {
        var path = AssemblyFolder();

        var r = Parse(IsoformTools.ApplyEdits(path, Edits("""{"set": "components.pin.parameters.length", "value": 30}""")));

        CanonicalFormat.ReadAssembly(r["document"]!.ToJsonString()).Components[1].Parameters!["length"].Should().Be("30");
        r["result"]!["ok"]!.GetValue<bool>().Should().BeTrue();
        r["result"]!["instances"]!.GetValue<int>().Should().Be(2);
    }

    // ---- query ----

    [Fact]
    public void Queries_answer_rays_distances_and_walls_with_face_names()
    {
        // A hole 1 mm from the y- face: the thinnest wall is that web.
        var doc = Doc(Plate, new Hole("hole", [30, 6], 10));
        var r = Parse(IsoformTools.Query(doc,
        [
            new("ray", Origin: [50, 25, 20], Direction: [0, 0, -1]),
            new("ray", Origin: [50, 25, 20], Direction: [0, 0, 1]),
            new("distance", Point: [50, 25, 20]),
            new("wall"),
        ]));

        var (ray, miss, distance, wall) = (r["answers"]![0]!, r["answers"]![1]!, r["answers"]![2]!, r["answers"]![3]!);
        ray["face"]!.GetValue<string>().Should().Be("plate/z+");
        ray["distance"]!.GetValue<double>().Should().BeApproximately(10, 1e-9);
        Strings(ray["normal"]).Should().Equal("0", "0", "1");
        miss["hit"]!.GetValue<bool>().Should().BeFalse();
        distance["upper"]!.GetValue<double>().Should().BeApproximately(10, 1e-6);
        distance["lower"]!.GetValue<double>().Should().BeApproximately(10, 1e-6);
        distance["inside"]!.GetValue<bool>().Should().BeFalse();
        wall["thickness"]!.GetValue<double>().Should().BeApproximately(1, 1e-6);
        new[] { wall["fromFace"]!.GetValue<string>(), wall["toFace"]!.GetValue<string>() }.Should().BeEquivalentTo("hole/wall", "plate/y-");
    }

    [Fact]
    public void A_clearance_query_measures_the_gap_between_two_bodies()
    {
        var doc = Doc(Plate, new Block("lid", BooleanOp.New, [0, 0, 12], [10, 10, 5]));

        var c = Parse(IsoformTools.Query(doc, [new("clearance")]))["answers"]![0]!;

        c["a"]!.GetValue<string>().Should().Be("plate");
        c["b"]!.GetValue<string>().Should().Be("lid");
        c["upper"]!.GetValue<double>().Should().BeApproximately(2, 1e-4);
        c["lower"]!.GetValue<double>().Should().BeApproximately(2, 1e-4);
        c["interferes"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public void A_query_on_an_unknown_body_or_kind_is_a_tool_error()
    {
        var body = () => IsoformTools.Query(Doc(Plate), [new("distance", Point: [0, 0, 20], Body: "nope")]);
        var kind = () => IsoformTools.Query(Doc(Plate), [new("volume")]);

        body.Should().Throw<McpException>().WithMessage("*no body 'nope'*plate*");
        kind.Should().Throw<McpException>().WithMessage("*unknown query kind*");
    }

    // ---- validate ----

    [Fact]
    public void Validate_checks_rules_and_compiles_without_meshing()
    {
        var good = Parse(IsoformTools.Validate(Doc(Plate, new Hole("hole", [30, 25], 10))));
        var bad = Parse(IsoformTools.Validate(Doc(Plate, new Shell("skin", -1))));
        var broken = Parse(IsoformTools.Validate("""{"format":"isoform/0","units":"mm","features":[{"id":"plate","type":"block"}]}"""));

        good["ok"]!.GetValue<bool>().Should().BeTrue();
        Strings(good["bodies"]).Should().Equal("plate");
        bad["ok"]!.GetValue<bool>().Should().BeFalse();
        bad["diagnostics"]![0]!["message"]!.GetValue<string>().Should().Contain("shell thickness must be positive");
        broken["diagnostics"]![0]!["code"]!.GetValue<string>().Should().Be("parse-error");
    }

    // ---- export_step ----

    [Fact]
    public void Export_writes_exact_step_or_names_what_has_no_closed_form()
    {
        var path = Temp("plate.step");

        var r = Parse(IsoformTools.ExportStep(Doc(Plate, new Hole("hole", [30, 25], 10)), path, "plate"));
        var blend = Parse(IsoformTools.ExportStep(Doc(Plate, new Block("boss", BooleanOp.Join, [40, 15, 10], [20, 20, 10], Blend: 3))));
        var text = Parse(IsoformTools.ExportStep(Doc(Plate)));

        r["ok"]!.GetValue<bool>().Should().BeTrue();
        r["solids"]!.GetValue<int>().Should().Be(1);
        r["faces"]!.GetValue<int>().Should().Be(7);
        File.ReadAllText(path).Should().StartWith("ISO-10303-21;").And.Contain("CYLINDRICAL_SURFACE");
        blend["ok"]!.GetValue<bool>().Should().BeFalse();
        blend["unsupported"]!.GetValue<string>().Should().Contain("boss/blend");
        text["step"]!.GetValue<string>().Should().Contain("MANIFOLD_SOLID_BREP");
    }

    [Fact]
    public void Export_writes_a_bundle_or_assembly_file_as_placed_parts()
    {
        var bundle = Parse(IsoformTools.ExportStep(CanonicalFormat.Write(FormatGuide.BundleExample)));
        var file = Parse(IsoformTools.ExportStep(AssemblyFolder(), name: "bracket-and-pin"));
        var text = () => IsoformTools.ExportStep(CanonicalFormat.Write(FormatGuide.AssemblyExample));

        bundle["ok"]!.GetValue<bool>().Should().BeTrue(bundle.ToJsonString());
        bundle["instances"]!.GetValue<int>().Should().Be(2);
        bundle["parts"]!.GetValue<int>().Should().Be(2);
        bundle["step"]!.GetValue<string>().Should().Contain("NEXT_ASSEMBLY_USAGE_OCCURRENCE('pin'");
        file["step"]!.GetValue<string>().Should().Contain("'bracket-and-pin'");
        text.Should().Throw<McpException>().WithMessage("*no folder*");
    }

    // ---- assembly ----

    [Fact]
    public void An_assembly_solves_its_mates_and_measures_its_clearances()
    {
        var r = Parse(IsoformTools.Assembly(CanonicalFormat.Write(FormatGuide.BundleExample)));

        r["ok"]!.GetValue<bool>().Should().BeTrue(r.ToJsonString());
        r["mates"]!.GetValue<int>().Should().Be(2);
        r["freeDegrees"]!.GetValue<int>().Should().Be(1, "the pin may still turn in its bore");
        var pin = r["instances"]!.AsArray().Single(i => i!["id"]!.GetValue<string>() == "pin")!;
        pin["origin"]![0]!.GetValue<double>().Should().BeApproximately(30, 1e-6);
        pin["origin"]![1]!.GetValue<double>().Should().BeApproximately(25, 1e-6);
        pin["origin"]![2]!.GetValue<double>().Should().BeApproximately(0, 1e-6);
        var gap = r["clearances"]!.AsArray().Single()!;
        gap["upper"]!.GetValue<double>().Should().BeApproximately(0.1, 1e-4);
        gap["interferes"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public void An_assembly_file_finds_its_parts_next_to_it()
    {
        var r = Parse(IsoformTools.Assembly(AssemblyFolder(), clearances: false));

        r["ok"]!.GetValue<bool>().Should().BeTrue();
        r["instances"]!.AsArray().Should().HaveCount(2);
        r["clearances"].Should().BeNull();
    }

    /// <summary>The example assembly written as files: its parts, and the assembly beside them, whose path this is.</summary>
    static string AssemblyFolder()
    {
        var folder = Directory.CreateTempSubdirectory("isoform-mcp-").FullName;
        foreach (var (name, text) in FormatGuide.BundleExample.Documents) File.WriteAllText(Path.Combine(folder, name), text);
        var path = Path.Combine(folder, "assembly.json");
        File.WriteAllText(path, CanonicalFormat.Write(FormatGuide.AssemblyExample));
        return path;
    }
}
