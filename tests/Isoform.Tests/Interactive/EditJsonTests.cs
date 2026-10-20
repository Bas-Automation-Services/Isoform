using System.Text.Json;
using Isoform.Document;
using Isoform.Interactive;

namespace Isoform.Tests.Interactive;

/// <summary>The Apply and Diff exports: <c>Apply(old, Diff(old, new))</c> gives the canonical text of new, over the examples.</summary>
public class EditJsonTests
{
    static readonly IReadOnlyList<string> Parts = Examples.All.Select(e => CanonicalFormat.Write(e.Doc)).ToList();

    public static IEnumerable<object[]> PartPairs =>
        from a in Enumerable.Range(0, Examples.All.Count) from b in Enumerable.Range(0, Examples.All.Count) where a != b select new object[] { a, b };

    [Theory]
    [MemberData(nameof(PartPairs))]
    public void Apply_of_diff_gives_the_new_document(int a, int b) => RoundTrip(Parts[a], Parts[b]);

    [Fact]
    public void Apply_of_diff_gives_the_new_bundle()
    {
        var old = CanonicalFormat.Write(Examples.Bundles[0].Bundle);
        // Move an instance and change a part inside the bundle.
        var edited = Applied(old, """
            [{"set": "components.upper.placement.origin", "value": [0, 30, 15]},
             {"document": "bracket.json", "set": "bore.diameter", "value": 5}]
            """);

        RoundTrip(old, edited);
        RoundTrip(edited, old);
    }

    [Fact]
    public void A_handle_drag_diffs_to_one_set()
    {
        var old = Parts[0];
        var edited = Applied(old, """{"set": "plate.size.0", "value": 120}""");

        Edits(old, edited).Should().Be("""[{"set":"plate.size.0","value":120}]""");
    }

    [Fact]
    public void Problems_come_back_as_data()
    {
        using var apply = JsonDocument.Parse(ViewJson.Apply(Parts[0], """[{"remove": "slot"}]"""));
        apply.RootElement.GetProperty("problem").GetString().Should().Contain("slot");
        apply.RootElement.GetProperty("failedEdit").GetInt32().Should().Be(0);
        apply.RootElement.TryGetProperty("text", out _).Should().BeFalse();

        using var diff = JsonDocument.Parse(ViewJson.Diff(Parts[0], "{\"format\": "));
        diff.RootElement.GetProperty("error").GetString().Should().NotBeNullOrEmpty();
    }

    static void RoundTrip(string old, string @new)
    {
        var applied = Applied(old, Edits(old, @new));
        applied.Should().Be(DocumentEdits.TryApply(@new, "[]").Text);
    }

    static string Edits(string old, string @new)
    {
        using var diff = JsonDocument.Parse(ViewJson.Diff(old, @new));
        return diff.RootElement.GetProperty("edits").GetRawText();
    }

    static string Applied(string document, string edits)
    {
        using var r = JsonDocument.Parse(ViewJson.Apply(document, edits));
        r.RootElement.TryGetProperty("problem", out var problem).Should().BeFalse(problem.ToString());
        var text = r.RootElement.GetProperty("text").GetString()!;
        r.RootElement.GetProperty("hash").GetString().Should().Be(DocumentEdits.TryApply(text, "[]").Hash);
        return text;
    }
}
