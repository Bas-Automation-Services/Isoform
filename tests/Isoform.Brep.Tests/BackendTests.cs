using BasAutomation.Geometry;
using Isoform.Brep.Modeling;
using Isoform.Document;
using Isoform.Modeling;
using Isoform.Step;
using Xunit.Abstractions;

namespace Isoform.Brep.Tests;

/// <summary>
/// The kernel behind Isoform's API, against the F-rep core on the same documents: the same faces with the same names
/// and neighbours, the same edges and corners, and an exact B-rep that agrees with the one Isoform derives from the
/// F-rep (<see cref="ExactBrep"/>).
/// </summary>
public class BackendTests(ITestOutputHelper output)
{
    static readonly ModelingSettings Settings = new() { CellSize = 1 };

    static List<string> Describe(Topology t) =>
        t.Faces.Select(f => $"{string.Join("+", f.Sources)} | {string.Join(",", t.Signature(f.Index))}").Order().ToList();

    [Theory]
    [MemberData(nameof(Documents.All), MemberType = typeof(Documents))]
    public void Volume_is_exact(string name, PartDocument document, double volume)
    {
        var result = new BrepModeler(Settings).Regenerate(document);
        result.Diagnostics.Should().BeEmpty(name);
        result.Bodies.Sum(b => MassProperties.Volume(b.Body)).Should().BeApproximately(volume, 1e-12 * volume);
    }

    [Theory]
    [MemberData(nameof(Documents.All), MemberType = typeof(Documents))]
    public void Topology_and_names_match_the_frep_core(string name, PartDocument document, double _)
    {
        var brep = new BrepModeler(Settings).Regenerate(document).Topology!;
        var frep = new FrepModeler(Settings).Regenerate(document).Topology!;
        output.WriteLine($"{name}: B-rep {brep.Faces.Count}F {brep.Edges.Count}E {brep.Vertices.Count}V {brep.BodyCount}B, " +
                         $"F-rep {frep.Faces.Count}F {frep.Edges.Count}E {frep.Vertices.Count}V {frep.BodyCount}B");

        Describe(brep).Should().Equal(Describe(frep));
        brep.Edges.Should().HaveSameCount(frep.Edges);
        brep.Vertices.Should().HaveSameCount(frep.Vertices);
        brep.BodyCount.Should().Be(frep.BodyCount);
    }

    [Theory]
    [MemberData(nameof(Documents.All), MemberType = typeof(Documents))]
    public void The_exact_brep_agrees_with_the_one_derived_from_the_frep(string name, PartDocument document, double _)
    {
        var mine = StepConversion.ToStep(new BrepModeler(Settings).Regenerate(document).Bodies);
        // The F-rep side's own exact B-rep needs its finer mesh to cut some fillet faces open.
        var fine = new ModelingSettings { CellSize = 0.5 };
        var frep = (FrepResult)new FrepModeler(fine).Regenerate(document);
        var theirs = ExactBrep.Build(frep, fine);

        // Faces by name: each lies on the surface of a face with that name (bodies of one feature share names).
        var faces = mine.Solids.SelectMany(s => s.Faces).ToList();
        var other = theirs.Solids.SelectMany(s => s.Faces).ToList();
        string Name(BrepFace f) => string.Join("+", f.Sources.Order(StringComparer.Ordinal));
        faces.Select(Name).Order().Should().Equal(other.Select(Name).Order(), name);
        foreach (var f in faces)
            other.Where(o => Name(o) == Name(f)).Should().Contain(o => Same(f.Surface, o.Surface), Name(f));

        // Corners (where three or more edges other than seams end) within 1e-9.
        var corners = Corners(mine);
        var theirCorners = Corners(theirs);
        corners.Should().HaveSameCount(theirCorners);
        corners.Where(c => theirCorners.Min(t => (t - c).Length) >= 1e-9).Should().BeEmpty();
        output.WriteLine($"{name}: {faces.Count} faces, {corners.Count} corners agree");
    }

    // The same kind of surface, radius and axis, and the other's origin on mine: on the plane, or on the cylinder's axis.
    // A cone is its apex, opening and angle. Planes and cones have no radius (the F-rep side's chamfer faces keep the
    // chamfer's size in it).
    static bool Same(Isoform.Geometry.Surface s, Isoform.Geometry.Surface g)
    {
        var d = g.Origin - s.Origin;
        if (s.Kind == Isoform.Geometry.SurfaceKind.Cone)
            return g.Kind == s.Kind && d.Length < 1e-9 && Math.Abs(s.Angle - g.Angle) <= 1e-12 && s.Axis.Dot(g.Axis) >= 1 - 1e-12;
        var radius = s.Kind == Isoform.Geometry.SurfaceKind.Plane || Math.Abs(s.Radius - g.Radius) <= 1e-12;
        return s.Kind == g.Kind && radius && Math.Abs(Math.Abs(s.Axis.Dot(g.Axis)) - 1) <= 1e-12
               && (s.Kind == Isoform.Geometry.SurfaceKind.Plane ? Math.Abs(s.Axis.Dot(d)) : (d - s.Axis * s.Axis.Dot(d)).Length) < 1e-9;
    }

    // Seams (an edge one face uses twice) lie wherever each B-rep cuts its periodic faces open, so they make no corners.
    static List<Point3D> Corners(Step.Brep b)
    {
        var seams = b.Solids.SelectMany(s => s.Faces).SelectMany(f => f.Loops.SelectMany(l => l.Coedges).GroupBy(c => c.Edge).Where(g => g.Count() == 2).Select(g => g.Key)).ToHashSet();
        return [.. b.Vertices.Where(v => b.Edges.Count(e => !seams.Contains(e.Index) && (e.Start == v.Index || e.End == v.Index)) >= 3).Select(v => v.Point)];
    }

    [Fact]
    public void Each_body_is_a_solid_of_its_own()
    {
        var result = new BrepModeler(Settings).Regenerate(Documents.TwoPads);
        result.Bodies.Should().HaveCount(2);
        result.Topology!.Faces.Select(f => f.Body).Distinct().Should().Equal(0, 1);
    }

    [Fact]
    public void A_picked_point_finds_the_nearest_face_with_the_name()
    {
        var result = new BrepModeler(Settings).Regenerate(Documents.MountingPlate);
        var top = result.FaceNear("plate/end", 50, 50, 8)!.Value;
        result.Topology!.Faces[top].Sources.Should().Equal("plate/end");
        result.FaceNear("plate/bore", 100, 80, 4).Should().NotBeNull();
        result.FaceNear("plate/nothing", 0, 0, 0).Should().BeNull();
    }

    [Fact]
    public void A_grid_of_holes_from_one_sketch_keeps_each_hole_by_name()
    {
        // 100 holes cut in one boolean: the plate less their volume, each wall named by its point.
        var points = (from i in Enumerable.Range(0, 10) from j in Enumerable.Range(0, 10) select ($"p{i}{j}", new double[] { 10 + 10 * i, 10 + 10 * j })).ToDictionary();
        var document = new PartDocument(
        [
            new Block("plate", BooleanOp.New, [0, 0, 0], [110, 110, 5]),
            new Sketch("grid", new SketchPlacement(new SketchPlane([0, 0, 0], [1, 0, 0], [0, 0, 1])), points, [], []),
            new Hole("holes", [], 4) { Sketch = "grid", Points = [.. points.Keys] },
        ]);
        var result = new BrepModeler(Settings).Regenerate(document);
        result.Diagnostics.Should().BeEmpty();
        MassProperties.Volume(result.Bodies.Single().Body).Should().BeApproximately(110 * 110 * 5 - 100 * Math.PI * 4 * 5, 1e-6);
        result.Topology!.Faces.SelectMany(f => f.Sources).Where(n => n.EndsWith(":wall")).Should().HaveCount(100).And.Contain("holes/p37:wall");
    }

    [Fact]
    public void What_the_kernel_cannot_do_yet_is_refused_by_name()
    {
        var withCut = new PartDocument([.. Documents.TwoPads.Features, new Isoform.Document.Shell("h", 1)]);
        var result = new BrepModeler(Settings).Regenerate(withCut);
        result.Topology.Should().BeNull();
        result.Diagnostics.Should().ContainSingle(d => d.Code == Diagnostic.BackendFailed && d.FeatureId == "h");
    }
}
