using System.Reflection;
using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Geometry;
using Isoform.Meshing;
using Isoform.Modeling;
using Isoform.Interactive;

namespace Isoform.Tests;

/// <summary>
/// The compiled tape against the tree it came from, on every test part: values, samples, bounds, simplification,
/// single patches and per-surface samples bit for bit, batched values too, and the same mesh.
/// </summary>
public class TapeTests
{
    static readonly Block Plate = new("plate", BooleanOp.New, [0, 0, 0], [100, 50, 10]);

    // The bench's 20-operation part.
    static readonly PartDocument Twenty = new(
    [
        Plate,
        new Hole("h1", [10, 10], 6), new Hole("h2", [10, 40], 6), new Hole("h3", [30, 25], 10),
        new Hole("h4", [50, 12], 4), new Hole("h5", [50, 38], 4), new Hole("h6", [80, 25], 8),
        new Block("slot1", BooleanOp.Cut, [60, 30, -1], [6, 30, 12]),
        new Block("slot2", BooleanOp.Cut, [60, -10, -1], [6, 30, 12]),
        new Block("pocket", BooleanOp.Cut, [20, 5, 6], [20, 10, 5]),
        new Block("tab1", BooleanOp.Join, [-20, 10, 0], [30, 30, 10]),
        new Block("tab2", BooleanOp.Join, [95, 15, 0], [20, 20, 6]),
        new Block("boss", BooleanOp.Join, [40, 30, 9], [10, 10, 8]),
        new PlaneCut("chamfer", [100, 0, 10], [0.7071067811865476, -0.7071067811865476, 0]),
        new Hole("h7", [-10, 25], 6), new Hole("h8", [105, 25], 5),
        new Fillet("f1", ["plate/z+", "plate/x+"], 2),
        new Fillet("f2", ["plate/z-", "plate/y-"], 1.5),
        new Fillet("f3", ["plate/y+", "plate/x-"], 3),
        new Block("cut3", BooleanOp.Cut, [70, 20, 8], [8, 10, 5]),
    ]);

    static readonly Lazy<Dictionary<string, PartDocument>> All = new(() =>
    {
        var parts = new Dictionary<string, PartDocument>
        {
            ["bench/20 operations"] = Twenty,
            ["bench/base + shell"] = new([Plate, new Hole("hole", [30, 25], 10), new Fillet("fillet", ["plate/z+", "plate/x+"], 2), new Shell("shell", 1.5)]),
            ["edit suite/base"] = EditSuite.Base,
            ["multi body"] = new([Plate, new Block("post", BooleanOp.New, [40, 15, 5], [20, 20, 30]), new Hole("through-post", [50, 25], 6),
                new Hole("through-base", [10, 25], 6) { Body = "plate" }, new CombineBodies("weld", "post", BooleanOp.Join) { Body = "plate" }]),
        };
        foreach (var p in SketchFeatureTests.Parts()) parts[$"sketch/{p.Name}"] = p.Doc;
        // Offset profiles: a shrunk reflex corner and grown revolve corners turn round, with faces of their own.
        foreach (var (p, d) in new[] { ("L bracket", -1.0), ("stepped shaft", 1.0), ("cone with a dome", -1.0) })
            parts[$"sketch/{p} offset {d}"] = new([.. parts[$"sketch/{p}"].Features, new Offset("grow", d)]);
        foreach (var (name, doc) in Examples.All) parts[$"playground/{name}"] = doc;
        foreach (var t in new[] { typeof(EdgeFeatureTests), typeof(ConcaveFilletTests), typeof(LoopFilletTests), typeof(BlendOffsetTests) })
        {
            // Each suite lists its part names publicly and builds them in a private Part(name).
            var build = t.GetMethod("Part", BindingFlags.NonPublic | BindingFlags.Static)!;
            foreach (var row in (IEnumerable<object[]>)t.GetProperty("Parts")!.GetValue(null)!)
                parts[$"{t.Name}/{row[0]}"] = (PartDocument)build.Invoke(null, [row[0]])!;
        }
        foreach (var id in EditSuite.CaseIds.Select(r => (string)r[0]))
        {
            var c = EditSuite.Case(id);
            var before = DocumentEdits.Apply(EditSuite.Base, EditSuite.Edits(c, "setup"));
            parts[$"edit suite/{id}"] = DocumentEdits.Apply(before, EditSuite.Edits(c, "edit"));
        }
        return parts;
    });

    public static IEnumerable<object[]> Parts => All.Value.Keys.Select(k => new object[] { k });

    static CompiledPart Compile(string name) => new Compiler(new ModelingSettings()).Compile(All.Value[name]);

    [Theory]
    [MemberData(nameof(Parts))]
    public void Tape_is_bit_identical_to_the_tree(string name)
    {
        var part = Compile(name);
        if (part.Field is null) return; // the edit leaves no geometry
        foreach (var tree in part.Bodies.Select(b => b.Field).Prepend(part.Field).Distinct())
            Check(tree, part.Min - new Vector3D(5, 5, 5), part.Max + new Vector3D(5, 5, 5));
    }

    static void Check(Field tree, Point3D lo, Point3D hi)
    {
        var tape = TapeField.Compile(tree);
        var rng = new Random(11);
        Point3D Random() => new(lo.X + rng.NextDouble() * (hi.X - lo.X), lo.Y + rng.NextDouble() * (hi.Y - lo.Y), lo.Z + rng.NextDouble() * (hi.Z - lo.Z));

        // Random points, and points pulled onto the surface, where edges, fillets and blends decide.
        var points = new List<Point3D>();
        for (var i = 0; i < 4000; i++)
        {
            var p = Random();
            points.Add(p);
            var s = tree.Sample(p);
            points.Add(p - s.Gradient * s.Value);
        }
        // Points on each revolve axis, where the distance from the axis is exactly zero and has no direction.
        foreach (var pf in FieldAlgebra.Descendants(tree).OfType<ProfileField>().Where(pf => pf.Revolved))
        {
            var (o, y) = (pf.Frame.Origin, pf.Frame.Y);
            var reach = (hi - lo).Length;
            for (var t = -reach; t <= reach; t += reach / 200) points.Add(o + y * t);
        }
        foreach (var p in points) Same(tree.Sample(p), tape.Sample(p), tree.Value(p), tape.Value(p), p);
        SameBatch(tree, tape.Tape, points);

        for (var b = 0; b < 600; b++)
        {
            var size = Math.Pow(10, rng.NextDouble() * 3 - 1.5);
            var min = rng.NextDouble() < 0.5 ? Random() : points[rng.Next(points.Count)] - new Vector3D(size, size, size) * rng.NextDouble();
            var box = new Box3D(min, new Point3D(min.X + size, min.Y + size, min.Z + size));
            SameBox(tree, tape, box, rng, depth: 0);
        }
    }

    /// <summary>The mesher's way of using a field: bounds, simplify, single patch, then the same on a sub-box.</summary>
    static void SameBox(Field tree, Field tape, Box3D box, Random rng, int depth)
    {
        Bits(tree.Bounds(box).Lo, tape.Bounds(box).Lo, "bounds lo", box.Center);
        Bits(tree.Bounds(box).Hi, tape.Bounds(box).Hi, "bounds hi", box.Center);
        var (simpleTree, simpleTape) = (tree.Simplify(box), tape.Simplify(box));
        // A tape hands back the tree's own field once that is all it holds.
        // Or the object a call stands for: a lattice.
        if (simpleTape is not TapeField && simpleTape is not (LeafField or ProfileField or LatticeField)) Assert.Fail($"simplified to a {simpleTape.GetType().Name}");
        if (simpleTape is TapeField && ReferenceEquals(simpleTree, tree) != ReferenceEquals(simpleTape, tape)) Assert.Fail($"simplify changed one and not the other at {box.Center}");
        if (simpleTree.SinglePatch(box) is var a && simpleTape.SinglePatch(box) is var b && !Nullable.Equals(a, b))
            Assert.Fail($"single patch {a} vs {b} at {box.Center}");
        var (treeOwners, tapeOwners) = (new List<int>(), new List<int>());
        Owners(simpleTree, treeOwners);
        if (simpleTape is TapeField t) t.Tape.Surfaces(tapeOwners);
        else Owners(simpleTape, tapeOwners); // a single leaf of the tree
        Assert.Equal(treeOwners, tapeOwners);

        var (min, size) = (box.Min, box.Max.X - box.Min.X);
        for (var i = 0; i < 8; i++)
        {
            var p = new Point3D(min.X + size * rng.NextDouble(), min.Y + size * rng.NextDouble(), min.Z + size * rng.NextDouble());
            Same(tree.Sample(p), simpleTape.Sample(p), tree.Value(p), simpleTape.Value(p), p);
            Same(simpleTree.Sample(p), simpleTape.Sample(p), simpleTree.Value(p), simpleTape.Value(p), p);
            foreach (var owner in treeOwners.Append(-1))
            {
                var (st, sp) = (simpleTree.SampleSurface(owner, p), simpleTape.SampleSurface(owner, p));
                if (st.HasValue != sp.HasValue) Assert.Fail($"surface {owner} present in one only at {p}");
                if (st is { } x && sp is { } y) Same(x, y, x.Value, y.Value, p);
            }
        }
        if (depth >= 2) return;
        var half = size / 2;
        var corner = new Point3D(min.X + half * rng.Next(2), min.Y + half * rng.Next(2), min.Z + half * rng.Next(2));
        SameBox(simpleTree, simpleTape, new Box3D(corner, new Point3D(corner.X + half, corner.Y + half, corner.Z + half)), rng, depth + 1);
    }

    static void SameBatch(Field tree, Tape tape, List<Point3D> points)
    {
        var (x, y, z) = (points.Select(p => p.X).ToArray(), points.Select(p => p.Y).ToArray(), points.Select(p => p.Z).ToArray());
        var values = new double[points.Count];
        tape.Values(x, y, z, values);
        for (var i = 0; i < points.Count; i++) Bits(tree.Value(points[i]), values[i], "batched value", points[i]);
    }

    static void Same(FieldSample tree, FieldSample tape, double treeValue, double tapeValue, Point3D p)
    {
        Bits(treeValue, tapeValue, "value", p);
        Bits(tree.Value, tape.Value, "sample value", p);
        if (tree.Owner != tape.Owner) Assert.Fail($"owner {tree.Owner} vs {tape.Owner} at {p}");
        Bits(tree.Gradient.X, tape.Gradient.X, "gradient x", p);
        Bits(tree.Gradient.Y, tape.Gradient.Y, "gradient y", p);
        Bits(tree.Gradient.Z, tape.Gradient.Z, "gradient z", p);
    }

    static void Bits(double tree, double tape, string what, Point3D p)
    {
        if (BitConverter.DoubleToInt64Bits(tree) != BitConverter.DoubleToInt64Bits(tape)) Assert.Fail($"{what}: tree {tree:R}, tape {tape:R} at {p}");
    }

    // The mesher's list of a tree's surfaces, which the tape must reproduce in the same order.
    static void Owners(Field f, List<int> owners)
    {
        switch (f)
        {
            case LeafField l:
                if (!owners.Contains(l.Surface.Id)) owners.Add(l.Surface.Id);
                break;
            case MaxField m:
                foreach (var c in m.Children) Owners(c, owners);
                break;
            case MinField m:
                foreach (var c in m.Children) Owners(c, owners);
                break;
            case NegateField n:
                Owners(n.Child, owners);
                break;
            case RoundedEdgeField r:
                Owners(r.A, owners);
                Owners(r.B, owners);
                break;
            case ShellField s:
                Owners(s.Outer, owners);
                break;
            case LatticeField l:
                if (!owners.Contains(l.Surface.Id)) owners.Add(l.Surface.Id);
                break;
            case ProfileField p:
                foreach (var l in p.Leaves) Owners(l, owners);
                break;
            case BlendField b:
                Owners(b.A, owners);
                Owners(b.B, owners);
                break;
            case LoopEdgeField l:
                Owners(l.Cap, owners);
                Owners(l.Side, owners);
                break;
        }
    }

    [Theory]
    [MemberData(nameof(Parts))]
    public void Tape_meshes_exactly_like_the_tree(string name)
    {
        var part = Compile(name);
        if (part.Field is null) return;
        foreach (var body in part.Bodies)
        {
            var tree = DualContouring.Build(body.Field, body.Min, body.Max, 0.5, 0.01);
            var tape = DualContouring.Build(TapeField.Compile(body.Field), body.Min, body.Max, 0.5, 0.01);
            tape.Vertices.Should().Equal(tree.Vertices, (a, b) => a.X.Equals(b.X) && a.Y.Equals(b.Y) && a.Z.Equals(b.Z));
            tape.Quads.Should().Equal(tree.Quads);
            tape.ThinSpots.Should().Equal(tree.ThinSpots);
        }
    }

    [Fact]
    public void Simplified_tapes_are_short()
    {
        var tree = new Compiler(new ModelingSettings()).Compile(Twenty).Field!;
        var tape = TapeField.Compile(tree).Tape;
        tape.Calls.Should().Be(0, "the bench part compiles natively");
        // A small box on one face of the plate, away from every feature: one plane decides it.
        var box = new Box3D(new Point3D(31, 41, 9.8), new Point3D(32, 42, 10.2));
        tape.Simplify(box).Length.Should().Be(1);
        TapeField.Compile(tree).Simplify(box).Should().BeOfType<LeafField>("one leaf is quicker as the tree's own");
    }
}
