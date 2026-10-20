using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Isoform.Document;
using Isoform.Interactive;

namespace Isoform.Tests;

/// <summary>
/// Every document the tests can reach: the edit suite's base and its edited states, the documents the test classes
/// and the playground examples build (found by reflection, so a new test part is covered without listing it here).
/// </summary>
static class TestDocuments
{
    const BindingFlags Statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    static readonly Type[] DocumentTypes = [typeof(PartDocument), typeof(AssemblyDocument), typeof(Bundle)];

    public static IReadOnlyList<(string Name, string Text)> All { get; } = [.. EditSuiteStates(), .. Built().DistinctBy(d => d.Text)];

    static IEnumerable<(string Name, string Text)> EditSuiteStates()
    {
        yield return ("edit suite base", CanonicalFormat.Write(EditSuite.Base));
        foreach (var id in EditSuite.CaseIds.Select(c => (string)c[0]))
        {
            var c = EditSuite.Case(id);
            var before = DocumentEdits.Apply(EditSuite.Base, EditSuite.Edits(c, "setup"));
            yield return ($"{id} before", CanonicalFormat.Write(before));
            yield return ($"{id} after", CanonicalFormat.Write(DocumentEdits.Apply(before, EditSuite.Edits(c, "edit"))));
        }
    }

    static IEnumerable<(string Name, string Text)> Built()
    {
        // The examples live with the interactive session, outside this assembly.
        foreach (var type in typeof(TestDocuments).Assembly.GetTypes().Append(typeof(Examples)).Where(t => !t.IsDefined(typeof(CompilerGeneratedAttribute)) && !t.ContainsGenericParameters))
        {
            var features = new List<Feature>();
            foreach (var member in type.GetMembers(Statics).Where(m => !m.Name.Contains('<')))
            {
                if (member is FieldInfo { IsLiteral: true } literal && literal.GetRawConstantValue() is string text && text.TrimStart().StartsWith('{')
                    && CanonicalFormat.FormatOf(text) == PartDocument.FormatVersion)
                    yield return ($"{type.Name}.{member.Name}", CanonicalFormat.Write(CanonicalFormat.Read(text)));
                if (member is FieldInfo { IsLiteral: false } field && typeof(Feature).IsAssignableFrom(field.FieldType))
                    features.Add((Feature)field.GetValue(null)!);
                foreach (var (name, value) in Values(type, member))
                foreach (var document in Documents(value))
                    yield return ($"{type.Name}.{name}", document);
            }
            // A class's shared features (a base plate, a post) make one more document.
            if (features.Count > 0) yield return ($"{type.Name} features", CanonicalFormat.Write(new PartDocument(features)));
        }
    }

    // What a static member gives, when its type can hold documents: fields, properties and methods with no arguments,
    // numeric arguments (a size), or one name from the class's theory data.
    static IEnumerable<(string, object?)> Values(Type type, MemberInfo member)
    {
        switch (member)
        {
            case FieldInfo { IsLiteral: false } f when MayHold(f.FieldType):
                yield return (f.Name, f.GetValue(null));
                break;
            case PropertyInfo p when MayHold(p.PropertyType) && p.GetIndexParameters().Length == 0:
                yield return (p.Name, p.GetValue(null));
                break;
            case MethodInfo m when MayHold(m.ReturnType) && !m.IsGenericMethodDefinition && !m.IsSpecialName:
                var parameters = m.GetParameters();
                if (parameters.All(p => p.ParameterType == typeof(double) || p.ParameterType == typeof(int)))
                    yield return (parameters.Length == 0 ? m.Name : $"{m.Name}(2)", m.Invoke(null, parameters.Select(p => Convert.ChangeType(2, p.ParameterType)).ToArray()));
                else if (parameters is [{ ParameterType: var s }] && s == typeof(string))
                    foreach (var name in Names(type))
                        yield return ($"{m.Name}({name})", m.Invoke(null, [name]));
                break;
        }
    }

    static IEnumerable<string> Names(Type type) => type.GetProperties(Statics)
        .Where(p => p.PropertyType.IsAssignableTo(typeof(IEnumerable<object[]>)))
        .SelectMany(p => ((IEnumerable<object[]>)p.GetValue(null)!).Select(row => row[0]).OfType<string>());

    static bool MayHold(Type type) => DocumentTypes.Contains(type) || type != typeof(string) && type.IsAssignableTo(typeof(IEnumerable))
        && type.GetInterfaces().Append(type).Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>) && Holds(i.GetGenericArguments()[0]));

    static bool Holds(Type element) => DocumentTypes.Contains(element)
        || element.GetProperties().Any(p => DocumentTypes.Contains(p.PropertyType)) || element.GetFields().Any(f => DocumentTypes.Contains(f.FieldType));

    static IEnumerable<string> Documents(object? value) => value switch
    {
        PartDocument p => [CanonicalFormat.Write(p)],
        AssemblyDocument a => [CanonicalFormat.Write(a)],
        Bundle b => [CanonicalFormat.Write(b), .. b.Documents.Values.Select(d => DocumentEdits.Apply(d, []))],
        IEnumerable items and not string => items.Cast<object?>().SelectMany(item => item is null || DocumentTypes.Contains(item.GetType()) ? Documents(item)
            : item.GetType().GetProperties().Where(p => DocumentTypes.Contains(p.PropertyType)).Select(p => p.GetValue(item))
                .Concat(item.GetType().GetFields().Where(f => DocumentTypes.Contains(f.FieldType)).Select(f => f.GetValue(item))).SelectMany(Documents)),
        _ => [],
    };
}

/// <summary>DocumentDiff: exact (applying the diff gives the new text) and minimal (the natural edits).</summary>
public class DocumentDiffTests
{
    static readonly JsonSerializerOptions Plain = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static string Diff(string old, string @new) => string.Join('\n', DocumentDiff.Between(old, @new).Select(e => e.ToJsonString(Plain)));

    static string Edited(PartDocument doc, string edits) => CanonicalFormat.Write(DocumentEdits.Apply(doc, JsonNode.Parse(edits)!.AsArray().Select(e => e!)));

    static void RoundTrips(string old, string @new, string because)
    {
        var edits = DocumentDiff.Between(old, @new);
        DocumentEdits.Apply(old, edits).Should().Be(DocumentEdits.Apply(@new, []), because);
        if (Reachable(old) && Reachable(@new))
            edits.Should().NotContain(e => (string?)e["set"] == "", $"{because}: the diff finds field edits, not whole documents");
    }

    // Some rule tests use ids that edit paths cannot tell apart; their diffs are whole documents by design.
    static bool Reachable(string text)
    {
        var root = JsonNode.Parse(text)!;
        IEnumerable<JsonArray> Lists(JsonNode doc) => new[] { doc["features"], doc["components"], doc["mates"] }.OfType<JsonArray>();
        IEnumerable<JsonNode> docs = root["documents"] is JsonObject d ? [root["assembly"]!, .. d.Select(kv => kv.Value!)] : [root];
        return docs.SelectMany(Lists).All(list =>
        {
            var ids = list.Select(n => (string)n!["id"]!).ToList();
            return ids.Distinct().Count() == ids.Count && ids.All(id => id.Length > 0 && !id.Contains('.') && id != "parameters");
        });
    }

    [Fact]
    public void The_suite_reaches_every_kind_of_document()
    {
        var formats = TestDocuments.All.GroupBy(d => CanonicalFormat.FormatOf(d.Text)).ToDictionary(g => g.Key!, g => g.Count());

        formats[PartDocument.FormatVersion].Should().BeGreaterThan(80);
        formats.Should().ContainKeys(AssemblyDocument.FormatVersion, Bundle.FormatVersion);
        TestDocuments.All.Should().Contain(d => d.Name.StartsWith("SketchFeatureTests.")).And.Contain(d => d.Name.StartsWith("Examples."))
            .And.Contain(d => d.Name.StartsWith("PatternTests.")).And.Contain(d => d.Name.StartsWith("ParameterTests."));
    }

    [Fact]
    public void Every_document_diffs_exactly_against_the_next_and_against_itself()
    {
        var all = TestDocuments.All;
        for (var i = 0; i < all.Count; i++)
        {
            DocumentDiff.Between(all[i].Text, all[i].Text).Should().BeEmpty(all[i].Name);
            var next = all[(i + 1) % all.Count];
            if (CanonicalFormat.FormatOf(next.Text) == CanonicalFormat.FormatOf(all[i].Text)) RoundTrips(all[i].Text, next.Text, $"{all[i].Name} -> {next.Name}");
        }
    }

    [Fact]
    public void Edit_suite_changes_diff_back_to_their_edits()
    {
        foreach (var id in EditSuite.CaseIds.Select(c => (string)c[0]))
        {
            var c = EditSuite.Case(id);
            var before = DocumentEdits.Apply(EditSuite.Base, EditSuite.Edits(c, "setup"));
            var edits = DocumentDiff.Between(before, DocumentEdits.Apply(before, EditSuite.Edits(c, "edit")));
            CanonicalFormat.Write(DocumentEdits.Apply(before, edits)).Should().Be(CanonicalFormat.Write(DocumentEdits.Apply(before, EditSuite.Edits(c, "edit"))), id);
            edits.Count.Should().BeLessThanOrEqualTo(EditSuite.Edits(c, "edit").Count(), $"{id}: no more edits than were made");
        }
    }

    [Fact]
    public void Random_mutations_of_every_document_diff_exactly_both_ways()
    {
        var all = TestDocuments.All;
        var count = 0;
        for (var i = 0; i < all.Count; i++)
        {
            var rng = new Random(i);
            for (var k = 0; k < 12; k++)
            {
                var mutated = Mutate(all[i].Text, rng);
                if (mutated is null) continue;
                RoundTrips(all[i].Text, mutated, $"{all[i].Name}, mutation {k}");
                RoundTrips(mutated, all[i].Text, $"{all[i].Name}, mutation {k} reversed");
                count++;
            }
        }
        count.Should().BeGreaterThan(1000);
    }

    // One to three random changes on the JSON: numbers moved or zeroed (a zero blend disappears), a number made a
    // parameter or an expression, and features, components or mates copied, removed or moved. Null when it no longer reads.
    static string? Mutate(string text, Random rng)
    {
        var root = JsonNode.Parse(text)!.AsObject();
        // In a bundle: its assembly or one of its documents.
        var targets = root["documents"] is JsonObject documents ? [root["assembly"]!.AsObject(), .. documents.Select(kv => kv.Value!.AsObject())] : new[] { root };
        var part = targets[rng.Next(targets.Length)];
        for (var m = rng.Next(1, 4); m > 0; m--)
        {
            var lists = new[] { part["features"], part["components"], part["mates"] }.OfType<JsonArray>().Where(l => l.Count > 0).ToList();
            var numbers = Numbers(part).ToList();
            if (numbers.Count == 0) break;
            var list = lists[rng.Next(lists.Count)];
            var (parent, key) = numbers[rng.Next(numbers.Count)];
            switch (rng.Next(8))
            {
                case 0:
                    var copy = list[rng.Next(list.Count)]!.DeepClone();
                    copy["id"] = $"copy{rng.Next(1000)}";
                    list.Insert(rng.Next(list.Count + 1), copy);
                    break;
                case 1 when list.Count > 1:
                    list.RemoveAt(rng.Next(list.Count));
                    break;
                case 2:
                    var moved = list[rng.Next(list.Count)]!;
                    list.Remove(moved);
                    list.Insert(rng.Next(list.Count + 1), moved);
                    break;
                case 3:
                    Put(parent, key, 0.0);
                    break;
                case 4 when part.ContainsKey("features"):
                    var name = $"p{rng.Next(100)}";
                    (part["parameters"] ??= new JsonObject())[name] = (double)Get(parent, key)! + 1;
                    Put(parent, key, $"={name} * 2");
                    break;
                case 5 when part.ContainsKey("features"):
                    Put(parent, key, $"={(double)Get(parent, key)!} + 1");
                    break;
                case 6 when part["parameters"] is JsonObject { Count: > 0 } parameters:
                    parameters.Remove(parameters.ElementAt(rng.Next(parameters.Count)).Key);
                    break;
                default:
                    // Whole numbers keep counts counts; several at once move points.
                    for (var n = rng.Next(1, 3); n > 0; n--, (parent, key) = numbers[rng.Next(numbers.Count)])
                        Put(parent, key, (double)Get(parent, key)! + rng.Next(1, 20));
                    break;
            }
        }
        try { return DocumentEdits.Apply(root.ToJsonString(), []); }
        catch (Exception e) when (e is FormatException or InvalidOperationException) { return null; }
    }

    static IEnumerable<(JsonNode Parent, string Key)> Numbers(JsonNode? node) => node switch
    {
        JsonObject o => o.SelectMany(kv => kv.Value is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number ? [(o, kv.Key)] : Numbers(kv.Value)),
        JsonArray a => a.SelectMany((n, i) => n is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number ? [(a, i.ToString())] : Numbers(n)),
        _ => [],
    };

    static JsonNode? Get(JsonNode parent, string key) => parent is JsonArray a ? a[int.Parse(key)] : parent[key];

    static void Put(JsonNode parent, string key, JsonNode value)
    {
        if (parent is JsonArray a) a[int.Parse(key)] = value;
        else parent[key] = value;
    }

    static readonly string Base = CanonicalFormat.Write(EditSuite.Base);

    [Fact]
    public void A_changed_number_is_one_set_on_its_field()
    {
        Diff(Base, Edited(EditSuite.Base, """[{ "set": "hole.diameter", "value": 12 }]""")).Should().Be("""{"set":"hole.diameter","value":12}""");
        Diff(Base, Edited(EditSuite.Base, """[{ "set": "plate.size.0", "value": 120 }]""")).Should().Be("""{"set":"plate.size.0","value":120}""");
        // Two coordinates of a point: one change, so one set of the list.
        Diff(Base, Edited(EditSuite.Base, """[{ "set": "hole.center", "value": [40, 20] }]""")).Should().Be("""{"set":"hole.center","value":[40,20]}""");
    }

    [Fact]
    public void Added_removed_and_moved_features_are_inserts_and_removes()
    {
        const string Slot = """{ "id": "slot", "type": "block", "op": "cut", "origin": [60, 30, -1], "size": [10, 30, 12] }""";
        Diff(Base, Edited(EditSuite.Base, $$"""[{ "insert": {{Slot}}, "before": "fillet" }]"""))
            .Should().Be("""{"insert":{"id":"slot","type":"block","op":"cut","origin":[60,30,-1],"size":[10,30,12]},"before":"fillet"}""");
        Diff(Base, Edited(EditSuite.Base, $$"""[{ "insert": {{Slot}} }]""")).Should().StartWith("""{"insert":{"id":"slot",""").And.NotContain("before");
        Diff(Base, Edited(EditSuite.Base, """[{ "remove": "hole" }]""")).Should().Be("""{"remove":"hole"}""");

        var moved = Edited(EditSuite.Base, """[{ "remove": "hole" }, { "insert": { "id": "hole", "type": "hole", "center": [30, 25], "diameter": 10 } }]""");
        Diff(Base, moved).Should().Be("""
            {"remove":"hole"}
            {"insert":{"id":"hole","type":"hole","center":[30,25],"diameter":10}}
            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Parameters_and_expressions_are_sets_and_removes()
    {
        var a = Edited(EditSuite.Base, """[{ "set": "parameters.width", "value": 100 }, { "set": "parameters.d", "value": 10 }, { "set": "hole.diameter", "value": "=d" }]""");
        var b = Edited(CanonicalFormat.Read(a), """[{ "set": "parameters.width", "value": 120 }, { "remove": "parameters.d" }, { "set": "hole.diameter", "value": "=width / 12" }, { "set": "plate.size.0", "value": "=width" }]""");

        Diff(a, b).Should().Be("""
            {"remove":"parameters.d"}
            {"set":"parameters.width","value":120}
            {"set":"plate.size.0","value":"=width"}
            {"set":"hole.diameter","value":"=width / 12"}
            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void A_fillet_trades_its_edge_for_a_loop_and_a_pattern_its_motion()
    {
        var loop = Edited(EditSuite.Base, """[{ "remove": "fillet.edge" }, { "set": "fillet.loop", "value": "hole/wall" }]""");
        Diff(Base, loop).Should().Be("{\"remove\":\"fillet.edge\"}\n{\"set\":\"fillet.loop\",\"value\":\"hole/wall\"}");
        Diff(loop, Base).Should().Be("{\"remove\":\"fillet.loop\"}\n{\"set\":\"fillet.edge\",\"value\":[\"plate/z+\",\"plate/x+\"]}");

        var row = Edited(EditSuite.Base, """[{ "insert": { "id": "row", "type": "pattern", "features": ["hole"], "linear": { "direction": [1, 0, 0], "spacing": 20, "count": 3 } } }]""");
        var ring = Edited(EditSuite.Base, """[{ "insert": { "id": "row", "type": "pattern", "features": ["hole"], "circular": { "origin": [50, 25, 0], "axis": [0, 0, 1], "count": 4, "angle": 360 } } }]""");
        Diff(row, ring).Should().StartWith("""{"set":"row","value":{"id":"row","type":"pattern",""");
        Diff(row, Edited(CanonicalFormat.Read(row), """[{ "set": "row.linear.count", "value": 5 }]""")).Should().Be("""{"set":"row.linear.count","value":5}""");
    }

    [Fact]
    public void Assemblies_and_bundles_diff_by_component_mate_and_document()
    {
        var bundle = Examples.Bundles[0].Bundle;
        var edited = DocumentEdits.Apply(bundle, JsonNode.Parse("""
            [
              { "set": "components.upper.placement.origin.2", "value": 16 },
              { "set": "components.pin.parameters.length", "value": 30 },
              { "insert": { "id": "axis", "kind": "axis", "a": "pin/pin/shank", "b": "lower/bore/wall" } },
              { "document": "bracket.json", "set": "plate.size.0", "value": 50 }
            ]
            """)!.AsArray().Select(e => e!));

        Diff(CanonicalFormat.Write(bundle), CanonicalFormat.Write(edited)).Should().Be("""
            {"set":"components.upper.placement.origin.2","value":16}
            {"set":"components.pin.parameters.length","value":30}
            {"insert":{"id":"axis","kind":"axis","a":"pin/pin/shank","b":"lower/bore/wall"}}
            {"document":"bracket.json","set":"plate.size.0","value":50}
            """.ReplaceLineEndings("\n"));
        DocumentDiff.Between(bundle.Assembly, edited.Assembly).Should().HaveCount(3);
    }

    [Fact]
    public void Ids_that_paths_cannot_reach_still_diff_exactly()
    {
        var twice = new PartDocument([.. EditSuite.Base.Features, new Hole("hole", [70, 25], 6)]);
        var dotted = new PartDocument([.. EditSuite.Base.Features, new Hole("a.b", [70, 25], 6)]);

        foreach (var doc in new[] { twice, dotted })
        {
            var edits = DocumentDiff.Between(EditSuite.Base, doc);
            edits.Should().ContainSingle().Which["set"]!.GetValue<string>().Should().Be("");
            CanonicalFormat.Write(DocumentEdits.Apply(EditSuite.Base, edits)).Should().Be(CanonicalFormat.Write(doc));
        }
    }
}
