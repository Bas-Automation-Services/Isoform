using System.Text.Json;
using Isoform.Document;
using Isoform.Interactive;
using Isoform.Modeling;

namespace Isoform.Tests.Interactive;

/// <summary>The session behind the playground: every example regenerates to a well-formed result v2 with consistent buffers.</summary>
public class SessionTests
{
    // The page's default settings.
    static readonly ModelingSettings Settings = new() { CellSize = 0.5, ChordTolerance = 0.05 };

    public static IEnumerable<object[]> Names =>
        Examples.All.Select(e => new object[] { e.Name }).Concat(Examples.Bundles.Select(e => new object[] { e.Name }));

    static string Text(string name) =>
        Examples.All.Where(e => e.Name == name).Select(e => CanonicalFormat.Write(e.Doc))
            .Concat(Examples.Bundles.Where(e => e.Name == name).Select(e => CanonicalFormat.Write(e.Bundle))).Single();

    [Theory]
    [MemberData(nameof(Names))]
    public void Example_regenerates_to_result_v2(string name)
    {
        var session = new Session();
        var document = Text(name);

        var r = session.Regenerate(document, "[]", Settings);

        r.Error.Should().BeNull();
        r.Text.Should().Be(DocumentEdits.TryApply(document, "[]").Text);
        session[Session.Main].Should().BeSameAs(r);
        using var json = JsonDocument.Parse(ViewJson.Write(r));
        var o = json.RootElement;
        o.GetProperty("version").GetInt32().Should().Be(2);
        o.GetProperty("slot").GetString().Should().Be("main");
        o.GetProperty("hash").GetString().Should().Be(r.Hash).And.HaveLength(64);
        o.GetProperty("text").GetString().Should().Be(r.Text);
        o.GetProperty("diagnostics").GetArrayLength().Should().Be(0);

        var faces = o.GetProperty("faces").GetArrayLength();
        faces.Should().BePositive();
        o.GetProperty("bodies").EnumerateArray().SelectMany(b => b.GetProperty("faces").EnumerateArray().Select(f => f.GetInt32()))
            .Should().BeEquivalentTo(Enumerable.Range(0, faces), "every face belongs to one body");

        // Buffers agree with the counts: nine coordinates and one face per triangle, six coordinates and one pair per segment.
        var b = session.Buffers;
        b.Should().BeSameAs(r.Buffers);
        o.GetProperty("triangles").GetInt32().Should().Be(b.Faces.Length).And.BePositive();
        b.Positions.Should().HaveCount(9 * b.Faces.Length);
        b.Faces.Should().OnlyContain(f => f >= 0 && f < faces);
        o.GetProperty("lines").GetInt32().Should().Be(b.LineEdges.Length).And.BePositive();
        o.GetProperty("smoothLines").GetInt32().Should().Be(b.SmoothLineEdges.Length);
        b.Lines.Should().HaveCount(6 * b.LineEdges.Length);
        b.SmoothLines.Should().HaveCount(6 * b.SmoothLineEdges.Length);
        var pairs = o.GetProperty("lineFaces").EnumerateArray().Select(p => (p[0].GetInt32(), p[1].GetInt32())).ToList();
        pairs.Should().OnlyHaveUniqueItems().And.OnlyContain(p => p.Item1 < p.Item2 && p.Item2 < faces);
        b.LineEdges.Concat(b.SmoothLineEdges).Should().OnlyContain(k => k >= 0 && k < pairs.Count);

        // The lines draw exactly the topology's edges: the face pairs they separate are its edges' pairs.
        pairs.ToHashSet().Should().BeEquivalentTo(TopologyPairs(r.View!));

        var instances = o.GetProperty("instances");
        if (r.Format == Bundle.FormatVersion)
        {
            instances.GetArrayLength().Should().Be(o.GetProperty("bodies").GetArrayLength());
            foreach (var i in instances.EnumerateArray())
            {
                i.GetProperty("hash").GetString().Should().HaveLength(64);
                i.GetProperty("matrix").GetArrayLength().Should().Be(16);
            }
        }
        else
        {
            instances.GetArrayLength().Should().Be(0);
            r.Hash.Should().Be(CanonicalFormat.Hash(CanonicalFormat.Read(document)));
        }
        o.GetProperty("timing").GetProperty("regenerate").GetDouble().Should().BePositive();
    }

    static HashSet<(int, int)> TopologyPairs(View view)
    {
        var tops = view.Part is { } part ? [part.Topology!] : view.Assembly!.Instances.Select(i => i.Result.Topology!).ToList();
        var (pairs, f0) = (new HashSet<(int, int)>(), 0);
        foreach (var t in tops)
        {
            foreach (var e in t.Edges) pairs.Add((Math.Min(e.FaceA, e.FaceB) + f0, Math.Max(e.FaceA, e.FaceB) + f0));
            f0 += t.Faces.Count;
        }
        return pairs;
    }

    [Fact]
    public void Edits_are_applied_before_regenerating_and_the_cache_meshes_only_what_changed()
    {
        var session = new Session();
        var document = Text(Examples.All[0].Name);
        var first = session.Regenerate(document, "[]", Settings);

        var again = session.Regenerate(document, "[]", Settings);
        again.Meshed.Should().Be(0);
        again.Reused.Should().Be(first.Meshed);

        var edited = session.Regenerate(document, """[{"set": "hole.diameter", "value": 12}]""", Settings);
        edited.Error.Should().BeNull();
        edited.Hash.Should().NotBe(first.Hash).And.Be(DocumentEdits.TryApply(document, """{"set": "hole.diameter", "value": 12}""").Hash);
        edited.Text.Should().Contain("12");
        edited.Meshed.Should().BePositive().And.BeLessThan(first.Meshed, "regions away from the hole are reused");
    }

    [Fact]
    public void A_failed_edit_names_the_edit_and_leaves_the_slot_as_it_was()
    {
        var session = new Session();
        var document = Text(Examples.All[0].Name);
        var good = session.Regenerate(document, "[]", Settings);

        var bad = session.Regenerate(document, """[{"set": "hole.diameter", "value": 12}, {"remove": "slot"}]""", Settings);

        bad.Error.Should().Contain("edit 2");
        bad.FailedEdit.Should().Be(1);
        session[Session.Main].Should().BeSameAs(good);
        session.Buffers.Faces.Should().BeEmpty();
        using var json = JsonDocument.Parse(ViewJson.Write(bad));
        json.RootElement.GetProperty("error").GetString().Should().Be(bad.Error);
        json.RootElement.GetProperty("failedEdit").GetInt32().Should().Be(1);
        json.RootElement.TryGetProperty("faces", out _).Should().BeFalse();
    }

    [Fact]
    public void A_broken_document_gives_an_error()
    {
        var r = new Session().Regenerate("""{"format": "isoform/""", "[]", Settings);

        r.Error.Should().NotBeNullOrEmpty();
        r.FailedEdit.Should().BeNull();
        JsonDocument.Parse(ViewJson.Write(r)).RootElement.GetProperty("error").GetString().Should().Be(r.Error);
    }

    [Fact]
    public void Slots_keep_their_own_last_result()
    {
        var session = new Session();
        var document = Text(Examples.All[0].Name);
        var main = session.Regenerate(document, "[]", Settings);

        var preview = session.Regenerate(document, """{"set": "plate.size.0", "value": 120}""", Settings with { CellSize = 1 }, Session.Preview);

        session[Session.Main].Should().BeSameAs(main);
        session[Session.Preview].Should().BeSameAs(preview);
        preview.Hash.Should().NotBe(main.Hash);
        session.Buffers.Should().BeSameAs(preview.Buffers);
    }

    [Fact]
    public void Thin_spots_name_the_two_faces()
    {
        // A hole 0.2 mm from the plate's edge: a wall below the resolved size of 1 mm.
        var document = Text(Examples.All[0].Name);
        var r = new Session().Regenerate(document, """{"set": "hole.center", "value": [5.2, 25]}""", Settings);

        r.View!.ThinSpots.Should().NotBeEmpty();
        using var json = JsonDocument.Parse(ViewJson.Write(r));
        var spot = json.RootElement.GetProperty("thinSpots")[0];
        spot.GetProperty("at").GetArrayLength().Should().Be(3);
        spot.GetProperty("thickness").GetDouble().Should().BeLessThan(1);
        spot.GetProperty("faces").EnumerateArray().Select(f => f.GetString()).Should().BeEquivalentTo("plate/x-", "hole/wall");
    }

    [Fact]
    public void A_pool_regenerates_from_the_shares_of_its_sessions()
    {
        var document = Text(Examples.All[0].Name);
        var (coordinator, helper) = (new Session(), new Session());

        var regions = helper.Share(document, "[]", Settings, 1, 2);
        helper.Sweep();
        coordinator.Share(document, "[]", Settings, 0, 2);
        coordinator.Import(regions);
        var r = coordinator.Regenerate(document, "[]", Settings);

        regions.Should().NotBeEmpty();
        r.Error.Should().BeNull();
        r.Reused.Should().Be(0, "every region was meshed for this regeneration, by one session or the other");
        r.Meshed.Should().BePositive();
    }
}
