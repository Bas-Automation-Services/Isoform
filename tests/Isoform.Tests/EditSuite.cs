using System.Text.Json.Nodes;
using Isoform.Document;
using Isoform.Modeling;

namespace Isoform.Tests;

/// <summary>Loads Cases/edit-suite.json: one base part, its references, and edits with expected outcomes.</summary>
public static class EditSuite
{
    static readonly Lazy<JsonObject> Root = new(() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Cases", "edit-suite.json")))!.AsObject());

    public static PartDocument Base => CanonicalFormat.FromNode(Root.Value["base"]!.DeepClone());

    public static IEnumerable<Reference> References => Root.Value["references"]!.AsArray().Select(n => n!) .Select(n =>
        (Reference)(n["face"] is { } f ? new FaceReference((string)n["id"]!, (string)f!)
            : n["edge"] is { } e ? new EdgeReference((string)n["id"]!, (string)e[0]!, (string)e[1]!)
            : new VertexReference((string)n["id"]!, n["vertex"]!.AsArray().Select(s => (string)s!).ToList())));

    public static IEnumerable<object[]> CaseIds => Cases.Select(c => new object[] { (string)c["id"]! });

    public static JsonObject Case(string id) => Cases.Single(c => (string)c["id"]! == id);

    static IEnumerable<JsonObject> Cases => Root.Value["cases"]!.AsArray().Select(c => c!.AsObject());

    public static IEnumerable<JsonNode> Edits(JsonObject c, string key) =>
        c[key]?.AsArray().Select(n => n!) ?? [];

    public static (ReferenceStatus Status, int? Count) ParseExpected(string text)
    {
        var parts = text.Split(':');
        return (Enum.Parse<ReferenceStatus>(parts[0], ignoreCase: true), parts.Length > 1 ? int.Parse(parts[1]) : null);
    }
}

/// <summary>The edit suite, run unchanged against each backend.</summary>
public abstract class EditSuiteTests
{
    protected abstract IModeler CreateModeler();

    [Fact]
    public void Base_part_regenerates_cleanly_and_every_reference_resolves()
    {
        var result = CreateModeler().Regenerate(EditSuite.Base);

        result.Diagnostics.Should().BeEmpty();
        foreach (var reference in EditSuite.References)
            ReferenceResolver.Resolve(reference, result.Topology!).Status
                .Should().Be(ReferenceStatus.Resolved, $"'{reference.Id}' must resolve on the base part");
    }

    [Theory]
    [MemberData(nameof(EditSuite.CaseIds), MemberType = typeof(EditSuite))]
    public void Edit_case(string id)
    {
        var c = EditSuite.Case(id);
        var modeler = CreateModeler();

        var before = DocumentEdits.Apply(EditSuite.Base, EditSuite.Edits(c, "setup"));
        var beforeResult = modeler.Regenerate(before);
        beforeResult.Diagnostics.Should().BeEmpty("the state before the edit must be valid");

        var references = EditSuite.References.ToList();
        foreach (var p in EditSuite.Edits(c, "pieces"))
        {
            var near = p["near"]!.AsArray().Select(v => (double)v!).ToArray();
            references.Add(ReferenceResolver.CapturePiece((string)p["id"]!, (string)p["face"]!, beforeResult, near[0], near[1], near[2]));
        }

        var after = modeler.Regenerate(DocumentEdits.Apply(before, EditSuite.Edits(c, "edit")));

        after.Diagnostics.Select(d => d.Code).Distinct().Should()
            .BeEquivalentTo(c["diagnostics"]!.AsArray().Select(d => (string)d!), $"{id}: {c["title"]}");

        if ((bool?)c["noGeometry"] == true)
        {
            after.Topology.Should().BeNull();
            return;
        }

        foreach (var (refId, expectedNode) in c["expect"]!.AsObject())
        {
            var (status, count) = EditSuite.ParseExpected((string)expectedNode!);
            var resolution = ReferenceResolver.Resolve(references.Single(r => r.Id == refId), after.Topology!);
            resolution.Status.Should().Be(status, $"{id}: reference '{refId}'");
            if (count is not null) resolution.Targets.Should().HaveCount(count.Value, $"{id}: reference '{refId}'");
        }
    }
}
