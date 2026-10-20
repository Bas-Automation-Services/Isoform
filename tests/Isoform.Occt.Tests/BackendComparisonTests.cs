using System.Text;
using Isoform.Document;
using Isoform.Modeling;
using Isoform.Occt;
using Isoform.Tests;
using Xunit.Abstractions;

namespace Isoform.Occt.Tests;

/// <summary>H2 and H3b: the same documents and references on the F-rep core and the OCCT B-rep baseline.</summary>
public class BackendComparisonTests(ITestOutputHelper output)
{
    static readonly ModelingSettings Settings = new();

    [Fact]
    public void Native_library_is_available()
    {
        OcctModeler.IsAvailable.Should().BeTrue("build native/Isoform.Occt first (see README)");
    }

    [Fact]
    public void Base_part_topology_is_identical_on_both_backends()
    {
        var frep = new FrepModeler(Settings).Regenerate(EditSuite.Base).Topology!;
        var occt = new OcctModeler(Settings).Regenerate(EditSuite.Base).Topology!;

        Describe(occt).Should().BeEquivalentTo(Describe(frep));
        occt.Edges.Should().HaveSameCount(frep.Edges);
        occt.Vertices.Should().HaveSameCount(frep.Vertices);
    }

    /// <summary>Every case except E03, where the backends knowingly differ (see the test below).</summary>
    public static IEnumerable<object[]> ComparableCases => EditSuite.CaseIds.Where(c => (string)c[0] != "E03");

    [Theory]
    [MemberData(nameof(ComparableCases))]
    public void Topology_after_each_edit_matches_on_both_backends(string id)
    {
        var c = EditSuite.Case(id);
        var doc = DocumentEdits.Apply(DocumentEdits.Apply(EditSuite.Base, EditSuite.Edits(c, "setup")), EditSuite.Edits(c, "edit"));
        var frep = new FrepModeler(Settings).Regenerate(doc);
        var occt = new OcctModeler(Settings).Regenerate(doc);

        output.WriteLine($"F-rep: {string.Join("; ", frep.Diagnostics.Select(d => $"{d.Code} {d.Message}"))}");
        output.WriteLine($"OCCT:  {string.Join("; ", occt.Diagnostics.Select(d => $"{d.Code} {d.Message}"))}");
        if (frep.Topology is null || occt.Topology is null)
        {
            (frep.Topology is null).Should().Be(occt.Topology is null);
            return;
        }
        output.WriteLine($"F-rep: {frep.Topology.Faces.Count}F {frep.Topology.Edges.Count}E {frep.Topology.Vertices.Count}V {frep.Topology.BodyCount}B");
        output.WriteLine($"OCCT:  {occt.Topology.Faces.Count}F {occt.Topology.Edges.Count}E {occt.Topology.Vertices.Count}V {occt.Topology.BodyCount}B");
        Describe(occt.Topology).Should().BeEquivalentTo(Describe(frep.Topology), id);
    }

    /// <summary>
    /// E03, hole tangent to a side face: the F-rep compiler flags the zero-thickness contact. OCCT builds a
    /// pinched solid without any warning, splitting the hole wall into two faces, and calls the shape valid.
    /// </summary>
    [Fact]
    public void Tangent_contact_is_flagged_by_frep_and_silently_pinched_by_occt()
    {
        var doc = DocumentEdits.Apply(EditSuite.Base, EditSuite.Edits(EditSuite.Case("E03"), "edit"));
        var frep = new FrepModeler(Settings).Regenerate(doc);
        var occt = new OcctModeler(Settings).Regenerate(doc);

        frep.Diagnostics.Select(d => d.Code).Should().Equal(Diagnostic.TangentContact);
        occt.Diagnostics.Should().BeEmpty();
        occt.Shape!.IsValid.Should().BeTrue();
        occt.Topology!.Faces.Count(f => f.Sources.Contains("hole/wall")).Should().Be(2);
        frep.Topology!.Faces.Count(f => f.Sources.Contains("hole/wall")).Should().Be(1);
    }

    [Fact]
    public void Base_part_exports_to_step()
    {
        var result = new OcctModeler(Settings).Regenerate(EditSuite.Base);
        var path = Path.Combine(Path.GetTempPath(), $"isoform-base-{Guid.NewGuid():N}.step");

        result.Shape!.WriteStep(path);

        var text = File.ReadAllText(path);
        File.Delete(path);
        text.Should().StartWith("ISO-10303-21;");
        text.Should().Contain("CYLINDRICAL_SURFACE");
    }

    /// <summary>Writes the reference-status table for every case and backend to the test output and docs/.</summary>
    [Fact]
    public void Report_reference_resolution_per_backend()
    {
        var sb = new StringBuilder();
        sb.AppendLine("| Case | Reference | Expected | F-rep | OCCT |");
        sb.AppendLine("|---|---|---|---|---|");
        var mismatches = new Dictionary<string, int> { ["F-rep"] = 0, ["OCCT"] = 0 };

        foreach (var id in EditSuite.CaseIds.Select(x => (string)x[0]))
        {
            var c = EditSuite.Case(id);
            var results = new Dictionary<string, (ModelResult After, List<Reference> Refs)>();
            foreach (var (name, modeler) in new (string, IModeler)[] { ("F-rep", new FrepModeler(Settings)), ("OCCT", new OcctModeler(Settings)) })
            {
                var before = DocumentEdits.Apply(EditSuite.Base, EditSuite.Edits(c, "setup"));
                var beforeResult = modeler.Regenerate(before);
                var refs = EditSuite.References.ToList();
                foreach (var p in EditSuite.Edits(c, "pieces"))
                {
                    var near = p["near"]!.AsArray().Select(v => (double)v!).ToArray();
                    refs.Add(ReferenceResolver.CapturePiece((string)p["id"]!, (string)p["face"]!, beforeResult, near[0], near[1], near[2]));
                }
                results[name] = (modeler.Regenerate(DocumentEdits.Apply(before, EditSuite.Edits(c, "edit"))), refs);
            }

            string Diags(ModelResult r) => r.Diagnostics.Count == 0 ? "–" : string.Join(", ", r.Diagnostics.Select(d => d.Code).Distinct());
            var expectedDiags = c["diagnostics"]!.AsArray().Select(d => (string)d!).ToList();
            sb.AppendLine($"| **{id}** | *diagnostics* | {(expectedDiags.Count == 0 ? "–" : string.Join(", ", expectedDiags))} | {Diags(results["F-rep"].After)} | {Diags(results["OCCT"].After)} |");

            foreach (var (refId, expectedNode) in c["expect"]!.AsObject())
            {
                var expected = (string)expectedNode!;
                var cells = new Dictionary<string, string>();
                foreach (var (name, (after, refs)) in results)
                {
                    var actual = after.Topology is null ? "no geometry" : Format(ReferenceResolver.Resolve(refs.Single(r => r.Id == refId), after.Topology));
                    var ok = Matches(expected, actual);
                    if (!ok) mismatches[name]++;
                    cells[name] = ok ? actual : $"**{actual}** ✗";
                }
                sb.AppendLine($"| {id} | {refId} | {expected} | {cells["F-rep"]} | {cells["OCCT"]} |");
            }
        }
        sb.AppendLine();
        sb.AppendLine($"Reference mismatches against expected: F-rep {mismatches["F-rep"]}, OCCT {mismatches["OCCT"]}.");

        output.WriteLine(sb.ToString());
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "reference-comparison.md"), sb.ToString());
    }

    static string Format(Resolution r) => r.Status switch
    {
        ReferenceStatus.Split => $"split:{r.Targets.Count}",
        var s => s.ToString().ToLowerInvariant(),
    };

    static bool Matches(string expected, string actual) =>
        expected.Contains(':') ? expected == actual : actual.Split(':')[0] == expected;

    /// <summary>Order-independent description: each face's sources and its neighbours' sources.</summary>
    static List<string> Describe(Topology t) =>
        t.Faces.Select(f => $"{string.Join("+", f.Sources)} | {string.Join(",", t.Signature(f.Index))}").Order().ToList();
}
