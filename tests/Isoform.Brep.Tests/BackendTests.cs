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
        var frep = (FrepResult)new FrepModeler(Settings).Regenerate(document);
        var theirs = ExactBrep.Build(frep, Settings);

        // Faces by name: each lies on the surface of a face with that name (bodies of one feature share names).
        var faces = mine.Solids.SelectMany(s => s.Faces).ToList();
        var other = theirs.Solids.SelectMany(s => s.Faces).ToList();
        string Name(BrepFace f) => string.Join("+", f.Sources.Order(StringComparer.Ordinal));
        faces.Select(Name).Order().Should().Equal(other.Select(Name).Order(), name);
        foreach (var f in faces)
            other.Where(o => Name(o) == Name(f)).Should().Contain(o => Same(f.Surface, o.Surface), Name(f));

        // Corners (where three or more edges end; seam vertices lie wherever each B-rep puts its seam) within 1e-9.
        var corners = Corners(mine);
        var theirCorners = Corners(theirs);
        corners.Should().HaveSameCount(theirCorners);
        corners.Max(c => theirCorners.Min(t => (t - c).Length)).Should().BeLessThan(1e-9);
        output.WriteLine($"{name}: {faces.Count} faces, {corners.Count} corners agree");
    }

    // The same kind of surface, radius and axis, and the other's origin on mine: on the plane, or on the cylinder's axis.
    static bool Same(Isoform.Geometry.Surface s, Isoform.Geometry.Surface g)
    {
        var d = g.Origin - s.Origin;
        return s.Kind == g.Kind && Math.Abs(s.Radius - g.Radius) <= 1e-12 && Math.Abs(Math.Abs(s.Axis.Dot(g.Axis)) - 1) <= 1e-12
               && (s.Kind == Isoform.Geometry.SurfaceKind.Plane ? Math.Abs(s.Axis.Dot(d)) : (d - s.Axis * s.Axis.Dot(d)).Length) < 1e-9;
    }

    static List<Point3D> Corners(Step.Brep b) =>
        [.. b.Vertices.Where(v => b.Edges.Count(e => e.Start == v.Index || e.End == v.Index) >= 3).Select(v => v.Point)];

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
    public void What_the_kernel_cannot_do_yet_is_refused_by_name()
    {
        var withCut = new PartDocument([.. Documents.TwoPads.Features, new PlaneCut("h", [10, 10, 4], [0, 0, 1])]);
        var result = new BrepModeler(Settings).Regenerate(withCut);
        result.Topology.Should().BeNull();
        result.Diagnostics.Should().ContainSingle(d => d.Code == Diagnostic.BackendFailed && d.FeatureId == "h");
    }
}
