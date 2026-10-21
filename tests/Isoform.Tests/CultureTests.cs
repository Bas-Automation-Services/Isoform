using System.Globalization;
using Isoform.Document;
using Isoform.Modeling;

namespace Isoform.Tests;

/// <summary>
/// The kernel runs in hosts of any culture (an add-in in a Dutch CAD system): its text, documents and hashes must
/// not follow the host's decimal comma.
/// </summary>
public class CultureTests
{
    static T In<T>(string culture, Func<T> f)
    {
        var saved = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo(culture);
        try { return f(); }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    [Fact]
    public void Diagnostics_use_a_decimal_point_in_a_decimal_comma_culture()
    {
        // The hole moved to a 0.6 mm wall from the plate's edge: below the 1 mm that 0.5 mm cells resolve, unrefined.
        var doc = new PartDocument([.. EditSuite.Base.Features.Select(f => f is Hole h ? h with { Center = [30, 44.4] } : f)]);

        var result = In("nl-NL", () => new FrepModeler(new ModelingSettings { CellSize = 0.5, Refinement = 0 }).Regenerate(doc));

        var message = result.Diagnostics.Single(d => d.Code == Diagnostic.BelowResolution && d.Message.Contains("apart")).Message;
        message.Should().MatchRegex(@"\d\.\d+ mm apart near \(\d+\.\d+, \d+\.\d+, \d+\.\d+\)").And.NotMatchRegex(@"\d,\d");
    }

    [Fact]
    public void Canonical_text_and_hash_do_not_depend_on_the_culture()
    {
        var doc = new PartDocument([new Block("b", BooleanOp.New, [-0.5, 0, 0], [12.25, 1.0 / 3.0, 0.1 + 0.2]), new Hole("h", [2.5, 0.125], 0.75)]);

        In("nl-NL", () => CanonicalFormat.Write(doc)).Should().Be(In("", () => CanonicalFormat.Write(doc))).And.Contain("12.25");
        In("nl-NL", () => CanonicalFormat.Hash(doc)).Should().Be(In("", () => CanonicalFormat.Hash(doc)));
    }
}
