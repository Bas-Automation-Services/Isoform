using System.Diagnostics;
using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Meshing;
using Isoform.Modeling;
using Xunit.Abstractions;

namespace Isoform.Tests;

/// <summary>K5: queries on a single part, measured against analytic answers.</summary>
public class QueryTests(ITestOutputHelper output)
{
    static readonly SketchPlane Xz = new([0, 0, 0], [1, 0, 0], [0, -1, 0]);

    static CompiledPart Compile(params Feature[] features) => new Compiler(new ModelingSettings()).Compile(new PartDocument(features));

    static Block Box(string id, double x, double y, double z, double w, double l, double h) => new(id, BooleanOp.New, [x, y, z], [w, l, h]);

    static Block Plate => Box("plate", 0, 0, 0, 100, 50, 10);

    static Profile Loop(double[] start, params (string Id, double U, double V, double Bulge)[] edges) =>
        new(start, edges.Select(e => new ProfileEdge(e.Id, [e.U, e.V], e.Bulge)).ToList());

    // Revolved about world Z through the sketch origin.
    static Revolve Sphere(string id, double r, double[] centre) =>
        new(id, BooleanOp.New, Xz with { Origin = centre }, Loop([0, -r], ("arc", 0, r, 1), ("axis", 0, -r, 0)));

    static Revolve Torus => new("ring", BooleanOp.New, Xz, Loop([20, -3], ("outer", 20, 3, 1), ("inner", 20, -3, 1)));

    static string Face(CompiledPart part, RayHit hit) => part.Surfaces[hit.Owner].Source;

    static Point3D P(double x, double y, double z) => new(x, y, z);

    static Mesh MeshOf(CompiledPart part, double cell = 0.25, double chord = 0.002) =>
        DualContouring.Build(part.Field!, part.Min, part.Max, cell, chord);

    // ---- Ray hits ----

    [Fact]
    public void Ray_hits_name_the_face_and_lie_on_it()
    {
        var part = Compile(Plate, new Hole("hole", [30, 25], 10), new Fillet("fillet", ["plate/z+", "plate/x+"], 2));
        (Point3D From, Vector3D Dir, Point3D At, string Face)[] cases =
        [
            (P(-5, 25, 5), new(1, 0, 0), P(0, 25, 5), "plate/x-"),
            (P(50, 25, 20), new(1, -1, -2), P(55, 20, 10), "plate/z+"),
            (P(50, 25, 5), new(0, 0, 1), P(50, 25, 10), "plate/z+"), // from inside: the exit
            (P(30, 10, 5), new(0, 1, 0), P(30, 20, 5), "hole/wall"), // exit into the hole
            (P(30, 25, 5), new(1, 0, 0), P(35, 25, 5), "hole/wall"), // from inside the hole: the entry
            (P(99.5, 25, 20), new(0, 0, -1), P(99.5, 25, 8 + Math.Sqrt(4 - 1.5 * 1.5)), "fillet/face"),
        ];
        foreach (var (from, dir, at, face) in cases)
        {
            var hit = Queries.Raycast(part, from, dir)!.Value;
            output.WriteLine($"{face}: error {(hit.Point - at).Length:E2}");
            Face(part, hit).Should().Be(face);
            (hit.Point - at).Length.Should().BeLessThan(1e-12);
            hit.Distance.Should().BeApproximately((at - from).Length, 1e-12);
        }

        Queries.Raycast(part, P(30, 25, 20), new(0, 0, -1)).Should().BeNull("the ray runs down the hole");
        Queries.Raycast(part, P(50, 25, 20), new(0, 0, 1)).Should().BeNull();
    }

    [Fact]
    public void Ray_hits_a_revolved_torus_on_its_inner_and_outer_side()
    {
        var part = Compile(Torus);
        Queries.Raycast(part, P(0, 0, 0), new(1, 0, 0))!.Value.Point.X.Should().BeApproximately(17, 1e-12);
        var exit = Queries.Raycast(part, P(20, 0, 0), new(0.6, 0, 0.8))!.Value; // from the tube's centre circle
        (exit.Point - P(20 + 1.8, 0, 2.4)).Length.Should().BeLessThan(1e-12);
        Face(part, exit).Should().StartWith("ring/");
    }

    // ---- Distance ----

    [Fact]
    public void Distance_is_certified_and_exact_inside_outside_and_beyond_edges_and_corners()
    {
        var part = Compile(Plate, new Hole("hole", [30, 25], 10), new Block("pocket", BooleanOp.Cut, [60, 20, 6], [20, 10, 5]),
            new Fillet("fillet", ["plate/z+", "plate/x+"], 2));
        // Beside an edge or corner the field underestimates the distance; the search closes the gap.
        (Point3D P, double Expected, string? Face, bool Inside)[] cases =
        [
            (P(50, 25, 3), 3, "plate/z-", true),
            (P(50, 25, 13), 3, "plate/z+", false),
            (P(30, 25, 5), 5, "hole/wall", false), // on the hole's axis
            (P(50, 40, 5), 5, "plate/z-", true), // as far from the top as from the bottom
            (P(-5, 25, 15), Math.Sqrt(50), null, false), // beyond an edge
            (P(-3, -4, 22), 13, null, false), // beyond a corner
            (P(82, 32, 5), 3, null, true), // inside, beside the pocket's corner
            (P(32, 25, 14), 5, null, false), // above the hole, nearest its rim
            (P(101, 25, 11), Math.Sqrt(18) - 2, "fillet/face", false),
            (P(99.5, -3, 10.5), Math.Sqrt(Math.Pow(Math.Sqrt(1.5 * 1.5 + 2.5 * 2.5) - 2, 2) + 9), null, false), // beyond the fillet's end
        ];
        foreach (var (p, expected, face, inside) in cases)
        {
            var sw = Stopwatch.StartNew();
            var d = Queries.Distance(part.Field!, p, 1e-9);
            output.WriteLine($"{p}: [{d.Lower:F12}, {d.Upper:F12}] vs {expected:F12}, {sw.Elapsed.TotalMilliseconds:F1} ms, |f| = {Math.Abs(part.Field!.Value(p)):F6}");
            d.Lower.Should().BeLessThanOrEqualTo(expected + 1e-12);
            d.Upper.Should().BeGreaterThanOrEqualTo(expected - 1e-12);
            (d.Upper - d.Lower).Should().BeLessThan(1e-9);
            (d.Closest - p).Length.Should().BeApproximately(d.Upper, 1e-12);
            Math.Abs(part.Field!.Value(d.Closest)).Should().BeLessThan(1e-12);
            d.Inside.Should().Be(inside);
            if (face is not null) part.Surfaces[d.Owner].Source.Should().Be(face);
        }
    }

    // ---- Clearance ----

    [Theory]
    [InlineData(12, 0, 0, 2)] // facing faces
    [InlineData(13, 14, 0, 5)] // edge to edge: √(3² + 4²)
    [InlineData(13, 14, 22, 13)] // corner to corner: √(3² + 4² + 12²)
    [InlineData(10, 3, 0, 0)] // touching
    public void Clearance_between_blocks(double x, double y, double z, double expected)
    {
        var a = Compile(Box("a", 0, 0, 0, 10, 10, 10));
        var b = Compile(Box("b", x, y, z, 10, 10, 10));

        var c = Queries.Clearance(a, b);

        output.WriteLine($"[{c.Lower:F12}, {c.Upper:F12}] vs {expected}");
        c.Interferes.Should().BeFalse();
        c.Upper.Should().BeApproximately(expected, 1e-9);
        c.Lower.Should().BeLessThanOrEqualTo(expected + 1e-12).And.BeGreaterThanOrEqualTo(expected - 1e-6);
        (c.OnB - c.OnA).Length.Should().BeApproximately(c.Upper, 1e-12);
    }

    [Fact]
    public void Overlapping_blocks_interfere_by_the_overlap()
    {
        var c = Queries.Clearance(Compile(Box("a", 0, 0, 0, 10, 10, 10)), Compile(Box("b", 8, 0, 0, 10, 10, 10)));

        c.Interferes.Should().BeTrue();
        c.Upper.Should().BeApproximately(-2, 1e-12, "a 2 mm overlap: the deepest point is 1 mm inside both");
        c.Lower.Should().BeApproximately(-2, 1e-12);
        c.OnA.X.Should().BeApproximately(9, 1e-12);
    }

    [Fact]
    public void Clearance_between_curved_solids()
    {
        var sphere = Compile(Sphere("ball", 5, [0, 0, 0]));
        (CompiledPart Other, double Expected)[] cases =
        [
            (Compile(Sphere("other", 5, [10, 10, 10])), Math.Sqrt(300) - 10),
            (Compile(Box("face", 8, -5, -5, 10, 10, 10)), 3),
            (Compile(Box("edge", 6, 6, -5, 10, 10, 10)), Math.Sqrt(72) - 5),
            (Compile(Box("corner", 4, 4, 4, 10, 10, 10)), Math.Sqrt(48) - 5),
        ];
        foreach (var (other, expected) in cases)
        {
            var sw = Stopwatch.StartNew();
            var c = Queries.Clearance(sphere, other);
            output.WriteLine($"[{c.Lower:F12}, {c.Upper:F12}] vs {expected:F12}, {sw.Elapsed.TotalMilliseconds:F0} ms");
            c.Interferes.Should().BeFalse();
            c.Upper.Should().BeApproximately(expected, 1e-9);
            c.Lower.Should().BeLessThanOrEqualTo(expected + 1e-12).And.BeGreaterThanOrEqualTo(expected - 1e-6);
        }
    }

    // ---- Minimum wall ----

    [Fact]
    public void Minimum_wall_finds_the_web_between_a_hole_and_a_face()
    {
        var part = Compile(Plate, new Hole("hole", [100 - 0.8 - 5, 25], 10));

        var wall = Queries.MinimumWall(part.Field!, MeshOf(part, 0.35))!.Value;

        output.WriteLine($"{wall.Thickness:F12} from {part.Surfaces[wall.FromOwner].Source} {wall.From} to {part.Surfaces[wall.ToOwner].Source} {wall.To}");
        wall.Thickness.Should().BeApproximately(0.8, 1e-8);
        wall.From.Y.Should().BeApproximately(25, 1e-3);
        new[] { wall.FromOwner, wall.ToOwner }.Select(o => part.Surfaces[o].Source).Should().BeEquivalentTo("hole/wall", "plate/x+");
    }

    [Fact]
    public void Minimum_wall_of_a_shell_is_its_thickness()
    {
        var result = new FrepModeler(new ModelingSettings()).Regenerate(new PartDocument([.. EditSuite.Base.Features, new Shell("shell", 1.5)]));

        var wall = Queries.MinimumWall(result)!.Value;

        output.WriteLine($"{wall.Thickness:F12} from {result.Part.Surfaces[wall.FromOwner].Source} to {result.Part.Surfaces[wall.ToOwner].Source}");
        wall.Thickness.Should().BeApproximately(1.5, 1e-8);
    }

    [Fact]
    public void Minimum_wall_of_a_plain_plate_is_its_thickness_not_zero_at_its_edges()
    {
        var part = Compile(Plate);
        Queries.MinimumWall(part.Field!, MeshOf(part, 0.5))!.Value.Thickness.Should().BeApproximately(10, 1e-9);
    }

    // ---- Mass properties ----

    [Fact]
    public void Mass_properties_of_a_block_are_exact()
    {
        var part = Compile(Plate);

        var m = MassProperties.Of(part.Field!, MeshOf(part, 0.5), density: 2);

        m.Volume.Should().BeApproximately(50000, 1e-8);
        m.Mass.Should().BeApproximately(100000, 1e-8);
        m.Area.Should().BeApproximately(2 * (5000 + 1000 + 500), 1e-8);
        (m.Centroid - P(50, 25, 5)).Length.Should().BeLessThan(1e-11);
        var mass = 100000.0;
        Same(m.Inertia, new InertiaTensor(mass * (2500 + 100) / 12, mass * (10000 + 100) / 12, mass * (10000 + 2500) / 12, 0, 0, 0), 1e-10);
        var bounds = MassProperties.VolumeBounds(part.Field!, part.Min, part.Max, 0.1);
        output.WriteLine($"bounds [{bounds.Lo:F6}, {bounds.Hi:F6}]");
        50000.0.Should().BeInRange(bounds.Lo, bounds.Hi);
    }

    [Fact]
    public void Mass_properties_of_a_plate_with_a_hole()
    {
        var part = Compile(Plate, new Hole("hole", [30, 25], 10));

        var m = MassProperties.Of(part.Field!, MeshOf(part));

        // Block minus a cylinder of radius 5 and height 10, both about the composite centroid.
        var (mb, mh) = (50000.0, Math.PI * 25 * 10);
        var volume = mb - mh;
        var x = (mb * 50 - mh * 30) / volume;
        var (db, dh) = (50 - x, 30 - x);
        var expected = new InertiaTensor(
            mb * (2500 + 100) / 12 - mh * (3 * 25 + 100) / 12,
            mb * (10000 + 100) / 12 + mb * db * db - (mh * (3 * 25 + 100) / 12 + mh * dh * dh),
            mb * (10000 + 2500) / 12 + mb * db * db - (mh * 25 / 2 + mh * dh * dh),
            0, 0, 0);
        output.WriteLine($"volume {m.Volume:F6} vs {volume:F6} ({(m.Volume - volume) / volume:E2}), area {m.Area:F6} vs {13000 - 2 * 25 * Math.PI + 100 * Math.PI:F6}");
        output.WriteLine($"centroid x {m.Centroid.X:F9} vs {x:F9}; Izz {m.Inertia.Zz:F3} vs {expected.Zz:F3}");
        m.Volume.Should().BeApproximately(volume, volume * 1e-7);
        m.Area.Should().BeApproximately(13000 - 2 * 25 * Math.PI + 100 * Math.PI, 13000 * 1e-6);
        (m.Centroid - P(x, 25, 5)).Length.Should().BeLessThan(1e-6);
        Same(m.Inertia, expected, 1e-6);
    }

    [Fact]
    public void Volume_of_the_edit_suite_base_is_bracketed_and_estimated()
    {
        var part = new Compiler(new ModelingSettings()).Compile(EditSuite.Base);
        var exact = 100 * 50 * 10 - Math.PI * 25 * 10 - 4 * (1 - Math.PI / 4) * 50;

        var sw = Stopwatch.StartNew();
        var bounds = MassProperties.VolumeBounds(part.Field!, part.Min, part.Max, 0.1);
        var octree = sw.Elapsed.TotalMilliseconds;
        var estimate = MassProperties.Of(part.Field!, MeshOf(part)).Volume;

        output.WriteLine($"bounds [{bounds.Lo:F4}, {bounds.Hi:F4}] (width {(bounds.Hi - bounds.Lo) / exact:E2} relative, {octree:F0} ms), estimate {estimate:F6} vs {exact:F6} ({(estimate - exact) / exact:E2})");
        exact.Should().BeInRange(bounds.Lo, bounds.Hi);
        (bounds.Hi - bounds.Lo).Should().BeLessThan(exact * 1e-4);
        estimate.Should().BeApproximately(exact, exact * 1e-7);
    }

    [Fact]
    public void Mass_properties_of_a_revolved_torus()
    {
        var part = Compile(Torus);
        var (r, a) = (20.0, 3.0);
        var volume = 2 * Math.PI * Math.PI * r * a * a;

        var m = MassProperties.Of(part.Field!, MeshOf(part, 0.5, 0.005));
        var bounds = MassProperties.VolumeBounds(part.Field!, part.Min, part.Max, 0.2);

        output.WriteLine($"volume {m.Volume:F6} vs {volume:F6} ({(m.Volume - volume) / volume:E2}), bounds [{bounds.Lo:F4}, {bounds.Hi:F4}]");
        m.Volume.Should().BeApproximately(volume, volume * 1e-6);
        volume.Should().BeInRange(bounds.Lo, bounds.Hi);
        m.Centroid.AsVector().Length.Should().BeLessThan(1e-6);
        Same(m.Inertia, new InertiaTensor(volume * (r * r / 2 + 5 * a * a / 8), volume * (r * r / 2 + 5 * a * a / 8), volume * (r * r + 3 * a * a / 4), 0, 0, 0), 1e-5);
    }

    static void Same(InertiaTensor actual, InertiaTensor expected, double relative)
    {
        var scale = Math.Max(expected.Xx, Math.Max(expected.Yy, expected.Zz)) * relative;
        actual.Xx.Should().BeApproximately(expected.Xx, scale);
        actual.Yy.Should().BeApproximately(expected.Yy, scale);
        actual.Zz.Should().BeApproximately(expected.Zz, scale);
        actual.Xy.Should().BeApproximately(expected.Xy, scale);
        actual.Yz.Should().BeApproximately(expected.Yz, scale);
        actual.Zx.Should().BeApproximately(expected.Zx, scale);
    }
}
