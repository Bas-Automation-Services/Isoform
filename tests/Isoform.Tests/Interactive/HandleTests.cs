using System.Text.Json;
using System.Text.Json.Nodes;
using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Geometry;
using Isoform.Interactive;
using Isoform.Modeling;

namespace Isoform.Tests.Interactive;

/// <summary>
/// The handle contract over every example and the edit-suite parts: each handle drags by t = 0.5 (edits set
/// <c>path = base + per·t</c>), and the faces it names move with it, measured on the regenerated exact surfaces.
/// </summary>
public class HandleTests
{
    // The page's default settings.
    static readonly ModelingSettings Settings = new() { CellSize = 0.5, ChordTolerance = 0.05 };
    const double T = 0.5;

    // Bare references to constant parameters are edited through the parameter; others are expressions.
    const string ParametricPlate = """
        {"format": "isoform/0", "units": "mm", "parameters": {"depth": "=width / 10", "r": 2, "width": 100},
         "features": [
           {"id": "plate", "type": "block", "op": "new", "origin": [0, 0, 0], "size": ["=width", 50, "=depth"]},
           {"id": "hole", "type": "hole", "center": ["=width / 2", 25], "diameter": 8},
           {"id": "fillet", "type": "fillet", "edge": ["plate/z+", "plate/x+"], "radius": "=r"}]}
        """;

    static readonly Lazy<Dictionary<string, string>> Documents = new(() =>
    {
        var documents = Examples.All.ToDictionary(e => e.Name, e => CanonicalFormat.Write(e.Doc));
        documents["edit suite: base"] = CanonicalFormat.Write(EditSuite.Base);
        foreach (var id in EditSuite.CaseIds.Select(c => (string)c[0]))
        {
            var c = EditSuite.Case(id);
            if (c["diagnostics"]!.AsArray().Count > 0) continue;
            var before = DocumentEdits.Apply(EditSuite.Base, EditSuite.Edits(c, "setup"));
            documents[$"edit suite: {id}"] = CanonicalFormat.Write(DocumentEdits.Apply(before, EditSuite.Edits(c, "edit")));
        }
        documents["parameters"] = DocumentEdits.Apply(ParametricPlate, []);
        return documents;
    });

    public static IEnumerable<object[]> Parts => Documents.Value.Keys.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(Parts))]
    public void Every_handle_moves_its_faces(string name)
    {
        var session = new Session();
        var before = session.Regenerate(Documents.Value[name], "[]", Settings);
        before.Error.Should().BeNull();
        var part = before.View!.Part!;
        var stored = CanonicalFormat.Read(before.Text!);
        var resolved = JsonNode.Parse(CanonicalFormat.Write(Parametric.Resolve(stored)))!;
        var handles = Handles.Of(before);

        handles.Select(h => h.Id).Should().BeEquivalentTo(Expected(Parametric.Resolve(stored)), "every feature gets its handles");
        foreach (var h in handles)
        {
            Shape(h);
            // A planar handle sits on a document point: a hole's axis, a profile corner (inside the part after an offset).
            if (h.Kind != HandleKind.Planar)
                (Math.Abs(part.Part.Field!.Value(h.Anchor)) < 1e-4 || h.Faces.Any(f => Exact(part, f) is { } s && Analytic(s) && Math.Abs(s.Distance(h.Anchor)) < 1e-6))
                    .Should().BeTrue($"{h.Id}'s anchor {h.Anchor} lies on the part or on its face");
            if (h.ReadOnly)
            {
                h.Edits.Should().BeEmpty();
                continue;
            }
            foreach (var e in h.Edits)
                e.Base.Should().BeApproximately(e.Path.StartsWith("parameters.") ? double.Parse(stored.Parameters[e.Path[11..]]) : Field(resolved, e.Path), 1e-9,
                    $"{h.Id}: the base of {e.Path} is its value");
            h.Value[0].Should().Be(h.Edits[0].Base);

            double[] t = h.Kind == HandleKind.Planar ? [T, T] : [T];
            if (h.Max is { } max && h.Value[0] + h.Edits[0].Per[0] * T > max) t = [-T];
            var edits = new JsonArray([.. h.Edits.Select(e => (JsonNode)new JsonObject { ["set"] = e.Path, ["value"] = e.Base + e.Per.Zip(t, (p, x) => p * x).Sum() })]);
            var after = session.Regenerate(before.Text!, edits.ToJsonString(), Settings, "probe");
            after.Error.Should().BeNull(h.Id);
            var now = after.View!.Part!;
            var moved = Move(h, h.Anchor, t);
            foreach (var face in h.Faces)
            {
                var (s0, s1) = (Exact(part, face), Exact(now, face));
                s1.Should().NotBeNull($"{h.Id}: {face} is still there");
                if (part.Topology!.Faces.Any(f => f.Sources.Contains(face)))
                    now.Topology!.Faces.Should().Contain(f => f.Sources.Contains(face), $"{h.Id}: {face} is still in the result");
                if (Analytic(s1!))
                    s1!.Distance(moved).Should().BeApproximately(s0 is { } s && Analytic(s) ? s.Distance(h.Anchor) : 0, 1e-6, $"{h.Id}: {face} moved with the handle");
                else
                {
                    // A fillet face has no distance of its own: the ray along the axis crosses the boundary where it went.
                    var hit = Queries.Raycast(now.Part.Field!, moved - h.Axis * 0.25, h.Axis, 0.5);
                    hit.Should().NotBeNull($"{h.Id}: {face} moved with the handle");
                    hit!.Value.Distance.Should().BeApproximately(0.25, 1e-4, $"{h.Id}: {face} moved with the handle");
                    now.Part.Surfaces[hit.Value.Owner].Source.Should().Be(face);
                }
            }
            if (h.Count is { Path: { } path } count)
            {
                var more = session.Regenerate(before.Text!, new JsonObject { ["set"] = path, ["value"] = count.Value + 1 }.ToJsonString(), Settings, "probe");
                more.View!.Part!.Part.Surfaces.Should().Contain(s => s.Source.StartsWith($"{h.Feature}/{count.Value}:"), $"{h.Id}: one more copy");
            }
        }
    }

    [Fact]
    public void Parameters_drive_handles_and_expressions_make_them_read_only()
    {
        var handles = Handles.Of(new Session().Regenerate(ParametricPlate, "[]", Settings)).ToDictionary(h => h.Id);

        handles["plate:x+"].Edits.Should().BeEquivalentTo([new HandleEdit("parameters.width", 100, [1])]);
        handles["plate:x-"].Edits.Should().BeEquivalentTo([new HandleEdit("parameters.width", 100, [1]), new HandleEdit("plate.origin.0", 0, [-1])]);
        handles["plate:y+"].Edits.Should().BeEquivalentTo([new HandleEdit("plate.size.1", 50, [1])]);
        handles["fillet:radius"].Edits.Should().ContainSingle().Which.Path.Should().Be("parameters.r");
        // depth is a parameter, but an expression itself: dragging must not overwrite it.
        foreach (var (id, expression) in new[] { ("plate:z+", "depth"), ("plate:z-", "depth"), ("hole:center", "width / 2") })
        {
            handles[id].ReadOnly.Should().BeTrue(id);
            handles[id].Expression.Should().Be(expression);
            handles[id].Edits.Should().BeEmpty();
        }
        handles["hole:diameter"].ReadOnly.Should().BeFalse();
    }

    [Fact]
    public void Handles_json_follows_the_contract()
    {
        var session = new Session();
        var r = session.Regenerate(CanonicalFormat.Write(Examples.All[0].Doc), "[]", Settings);

        using var json = JsonDocument.Parse(HandleJson.Write(Session.Main, r));
        var o = json.RootElement;
        o.GetProperty("slot").GetString().Should().Be("main");
        o.GetProperty("hash").GetString().Should().Be(r.Hash);
        var handles = o.GetProperty("handles").EnumerateArray().ToDictionary(h => h.GetProperty("id").GetString()!);
        handles.Should().HaveCount(Handles.Of(r).Count);

        var x = handles["plate:x+"];
        x.GetProperty("feature").GetString().Should().Be("plate");
        x.GetProperty("kind").GetString().Should().Be("linear");
        x.GetProperty("axis").EnumerateArray().Select(v => v.GetDouble()).Should().Equal(1, 0, 0);
        x.GetProperty("anchor")[0].GetDouble().Should().Be(100);
        x.GetProperty("value").GetDouble().Should().Be(100);
        x.GetProperty("min").GetDouble().Should().Be(0.001);
        x.GetProperty("faces").EnumerateArray().Select(f => f.GetString()).Should().Equal("plate/x+");
        var edit = x.GetProperty("edits")[0];
        edit.GetProperty("path").GetString().Should().Be("plate.size.0");
        edit.GetProperty("base").GetDouble().Should().Be(100);
        edit.GetProperty("per").EnumerateArray().Select(v => v.GetDouble()).Should().Equal(1);
        x.TryGetProperty("readOnly", out _).Should().BeFalse();

        var centre = handles["hole:center"];
        centre.GetProperty("kind").GetString().Should().Be("planar");
        centre.GetProperty("value").EnumerateArray().Select(v => v.GetDouble()).Should().Equal(30, 25);
        centre.GetProperty("u").GetArrayLength().Should().Be(3);
        centre.GetProperty("edits")[1].GetProperty("per").EnumerateArray().Select(v => v.GetDouble()).Should().Equal(0, 1);

        using var empty = JsonDocument.Parse(HandleJson.Write("preview", session[Session.Preview]));
        empty.RootElement.GetProperty("handles").GetArrayLength().Should().Be(0);
        empty.RootElement.TryGetProperty("hash", out _).Should().BeFalse();
    }

    [Fact]
    public void An_assembly_has_a_frame_per_component_that_moves_its_instance()
    {
        var (_, bundle) = Examples.Bundles[0];
        var session = new Session();
        var r = session.Regenerate(CanonicalFormat.Write(bundle), "[]", Settings);
        var handles = Handles.Of(r);

        handles.Select(h => h.Feature).Should().Equal(bundle.Assembly.Components.Select(c => c.Id));
        handles.Should().OnlyContain(h => h.Kind == HandleKind.Frame);
        var upper = handles.Single(h => h.Feature == "upper");
        upper.Anchor.Should().Be(new Point3D(0, 20, 15));
        upper.Axis.Should().Be(new Vector3D(0, 0, -1));

        using var json = JsonDocument.Parse(HandleJson.Write(Session.Main, r));
        var frame = json.RootElement.GetProperty("handles").EnumerateArray().Single(h => h.GetProperty("feature").GetString() == "upper");
        frame.GetProperty("matrix").EnumerateArray().Select(v => v.GetDouble()).Should().Equal(1, 0, 0, 0, 0, -1, 0, 0, 0, 0, -1, 0, 0, 20, 15, 1);
        var edits = frame.GetProperty("edits").EnumerateArray().ToList();
        edits.Select(e => e.GetProperty("path").GetString()).Should().Equal("components.upper.placement.origin", "components.upper.placement.x", "components.upper.placement.normal");

        // Setting what the edits name moves the instance.
        var moved = session.Regenerate(r.Text!, """[{"set": "components.upper.placement.origin", "value": [0.5, 20, 15]}]""", Settings, "probe");
        Handles.Of(moved).Single(h => h.Feature == "upper").Anchor.Should().Be(new Point3D(0.5, 20, 15));
    }

    /// <summary>The handle ids the contract gives a document's features.</summary>
    static IEnumerable<string> Expected(PartDocument doc) => doc.Features.SelectMany(f => (f switch
    {
        Block => ["x-", "x+", "y-", "y+", "z-", "z+"],
        Hole => ["center", "diameter"],
        Extrude e => new[] { "start", "end" }.Concat(e.Profile.Edges.Select(x => $"corner:{x.Id}")),
        Revolve r => r.Profile.Edges.Select(x => $"corner:{x.Id}").Append("angle"),
        Fillet => ["radius"],
        Chamfer => ["distance"],
        Shell => ["thickness"],
        Offset => ["distance"],
        Pattern { Motion: LinearMotion } => ["spacing"],
        Pattern { Motion: CircularMotion { Angle: < 360 } } => ["angle"],
        Pattern { Motion: MirrorMotion } => ["plane"],
        Lattice l => l.Skin > 0 ? ["thickness", "cell", "skin"] : ["thickness", "cell"],
        _ => Array.Empty<string>(),
    }).Select(name => $"{f.Id}:{name}"));

    static void Shape(Handle h)
    {
        h.Axis.Length.Should().BeApproximately(1, 1e-9, h.Id);
        double.IsFinite(h.Anchor.X + h.Anchor.Y + h.Anchor.Z).Should().BeTrue(h.Id);
        var dimensions = h.Kind == HandleKind.Planar ? 2 : 1;
        h.Value.Should().HaveCount(dimensions, h.Id);
        h.Edits.All(e => e.Per.Length == dimensions).Should().BeTrue(h.Id);
        h.Faces.Should().NotBeNull();
        if (h.Kind == HandleKind.Planar)
        {
            h.U.Length.Should().BeApproximately(1, 1e-9);
            h.V.Length.Should().BeApproximately(1, 1e-9);
            h.U.Dot(h.Axis).Should().BeApproximately(0, 1e-9);
            h.V.Dot(h.Axis).Should().BeApproximately(0, 1e-9);
        }
    }

    /// <summary>Where a point on the handle goes when it is dragged by t.</summary>
    static Point3D Move(Handle h, Point3D p, double[] t)
    {
        switch (h.Kind)
        {
            case HandleKind.Planar:
                return p + h.U * t[0] + h.V * t[1];
            case HandleKind.Angle:
                var (a, v, angle) = (h.Axis, p - h.Center, t[0] * Math.PI / 180);
                return h.Center + v * Math.Cos(angle) + a.Cross(v) * Math.Sin(angle) + a * (a.Dot(v) * (1 - Math.Cos(angle)));
            default:
                return p + h.Axis * t[0];
        }
    }

    // The surface a face name stands for, as the handles take it: the one most quads use, else the last registered.
    static Surface? Exact(FrepResult r, string face)
    {
        var used = r.Mesh!.Quads.GroupBy(q => q.Owner).ToDictionary(g => g.Key, g => g.Count());
        return r.Part.Surfaces.Where(s => s.Source == face).OrderBy(s => used.GetValueOrDefault(s.Id)).ThenBy(s => s.Id).LastOrDefault();
    }

    static bool Analytic(Surface s) => s.Analytic;

    static double Field(JsonNode root, string path)
    {
        var keys = path.Split('.');
        var node = root["features"]!.AsArray().First(f => (string)f!["id"]! == keys[0])!;
        foreach (var k in keys[1..]) node = node is JsonArray a ? a[int.Parse(k)]! : node[k]!;
        return (double)node;
    }
}
