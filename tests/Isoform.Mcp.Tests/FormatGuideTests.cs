using System.Text.Json;
using System.Text.Json.Nodes;
using Isoform.Document;
using Isoform.Mcp;
using Isoform.Modeling;

namespace Isoform.Mcp.Tests;

/// <summary>describe_format must stay true to the kernel: every example is regenerated and checked here.</summary>
public class FormatGuideTests
{
    [Fact]
    public void Every_feature_type_has_an_example()
    {
        var types = typeof(Feature).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(Feature)) && !t.IsAbstract);

        types.Should().BeSubsetOf(FormatGuide.Examples.Select(e => e.Feature.GetType()), "a new feature type needs an example in FormatGuide");
    }

    [Fact]
    public void Every_example_regenerates_cleanly_with_the_faces_it_lists()
    {
        var modeler = new FrepModeler(new ModelingSettings());
        foreach (var e in FormatGuide.Examples)
        {
            var doc = new PartDocument([FormatGuide.Plate, .. e.Before, e.Feature]);
            var result = modeler.Regenerate(doc);

            result.Diagnostics.Should().BeEmpty(e.Feature.Id);
            result.Topology!.Faces.SelectMany(f => f.Sources).Should().Contain(e.Faces, e.Feature.Id);
            // The JSON shown is the canonical form and reads back to the same feature.
            CanonicalFormat.Write(CanonicalFormat.Read(CanonicalFormat.Write(doc))).Should().Be(CanonicalFormat.Write(doc));
        }
    }

    [Fact]
    public void Every_edit_example_applies_and_regenerates()
    {
        foreach (var edit in FormatGuide.EditExamples)
        {
            var r = JsonNode.Parse(IsoformTools.ApplyEdits(CanonicalFormat.Write(FormatGuide.EditTarget), [JsonDocument.Parse(edit).RootElement]))!;

            r["result"]!["ok"]!.GetValue<bool>().Should().BeTrue(edit);
            r["hash"]!.GetValue<string>().Should().NotBe(r["previousHash"]!.GetValue<string>(), edit);
        }
    }

    [Fact]
    public void The_parametric_example_follows_its_parameters()
    {
        var doc = CanonicalFormat.Write(FormatGuide.EditTarget);
        var wider = JsonNode.Parse(IsoformTools.ApplyEdits(doc, [JsonDocument.Parse("""{"set": "parameters.width", "value": 120}""").RootElement]))!;

        JsonNode.Parse(IsoformTools.Regenerate(doc))!["box"]!["max"]![0]!.GetValue<double>().Should().Be(100);
        wider["result"]!["box"]!["max"]![0]!.GetValue<double>().Should().Be(120);
        wider["references"]!["gone"]!.AsArray().Should().BeEmpty("names never depend on parameters");
        ((string)wider["document"]!["features"]![1]!["diameter"]!).Should().Be("=bore", "the expression is the design intent and stays");
    }

    [Fact]
    public void The_text_shows_every_type_every_diagnostic_code_and_valid_documents()
    {
        var text = IsoformTools.DescribeFormat();

        foreach (var e in FormatGuide.Examples) text.Should().Contain(FormatGuide.Feature(e.Feature).ToJsonString(IsoformTools.Compact));
        foreach (var code in FormatGuide.DiagnosticCodes()) text.Should().Contain(code);
        text.Should().Contain(PartDocument.FormatVersion).And.Contain(AssemblyDocument.FormatVersion).And.Contain(Bundle.FormatVersion);
        // Short enough to read once per session.
        text.Length.Should().BeLessThan(12_000);
    }
}
