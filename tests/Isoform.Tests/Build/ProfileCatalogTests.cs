using BasAutomation.Geometry;
using Isoform.Build;
using Isoform.Document;
using Isoform.Geometry;
using Isoform.Modeling;

namespace Isoform.Tests.Build;

/// <summary>
/// Profiles by name: a catalog gives a beam section's profile as exact lines and arcs, a beam method given to the
/// rationaliser builds the section from it with the members' shared runs, ends and document; without one a beam
/// section is reported and the rest is built.
/// </summary>
public class ProfileCatalogTests
{
    static readonly FrepModeler Modeler = new(new ModelingSettings { CellSize = 2 });

    static OutlineEdge Line(double x0, double y0, double x1, double y1) => new(OutlineEdgeKind.Line, new(x0, y0), new(x1, y1), default, 0, 0);

    static OutlineEdge Circle(double r) => new(OutlineEdgeKind.Circle, default, default, default, r, 2 * Math.PI);

    /// <summary>A rectangle <paramref name="w"/> × <paramref name="h"/> round the origin with corners of radius <paramref name="r"/>.</summary>
    static IReadOnlyList<OutlineEdge> Rounded(double w, double h, double r)
    {
        var (x, y) = (w / 2 - r, h / 2 - r);
        OutlineEdge Corner(double cx, double cy, double from) => new(OutlineEdgeKind.Arc, new(cx + r * Math.Cos(from), cy + r * Math.Sin(from)),
            new(cx + r * Math.Cos(from + Math.PI / 2), cy + r * Math.Sin(from + Math.PI / 2)), new(cx, cy), r, Math.PI / 2);
        return [Line(-x, -h / 2, x, -h / 2), Corner(x, -y, -Math.PI / 2), Line(w / 2, -y, w / 2, y), Corner(x, y, 0),
            Line(x, h / 2, -x, h / 2), Corner(-x, y, Math.PI / 2), Line(-w / 2, y, -w / 2, -y), Corner(-x, -y, Math.PI)];
    }

    /// <summary>Two profiles: a hollow section 40 × 20 × 2 (corners 2t outside, t inside) and a tube Ø48.3 × 2.9.</summary>
    public sealed class Catalog : IProfileCatalog
    {
        public CatalogProfile? Find(string id) => id switch
        {
            "RHS40x20x2" => new(id, "RHS 40×20×2", Rounded(40, 20, 4), [Rounded(36, 16, 2)]),
            "CHS48.3x2.9" => new(id, "CHS 48.3×2.9", [Circle(24.15)], [[Circle(21.25)]]),
            _ => null,
        };
    }

    /// <summary>
    /// A beam method as small as the seam allows: each run's ends from <see cref="Members.Ends"/>, its frame z along the
    /// axis and y the section's up, its part <see cref="Members.Document"/> of the catalog's profile.
    /// </summary>
    public sealed class StraightBeams(IProfileCatalog catalog) : IBuildMethod
    {
        public SectionResult Build(BuildSection section, BuildContext context)
        {
            var beam = (BeamSection)section;
            if (catalog.Find(beam.Profile) is not { } profile)
                return new([], [new(BuildDiagnostic.ProfileMissing, section.Id, $"the catalog has no profile '{beam.Profile}'")]);
            var (parts, diagnostics) = (new List<BuiltPart>(), new List<Diagnostic>());
            foreach (var run in Members.Runs(context, section.Id, beam.Along, beam.Lines ?? [], diagnostics))
            {
                if (Members.Ends(context, section.Id, run, diagnostics) is not { } ends) continue;
                var (z, up) = (run.Axis, new Vector3D(beam.Up![0], beam.Up[1], beam.Up[2]));
                var y = (up - z * z.Dot(up)).Normalized();
                var frame = new Frame(ends.A.At, y.Cross(z), y, z);
                var (a, b, length) = (frame.ToLocal(ends.A.Normal), frame.ToLocal(ends.B.Normal), z.Dot(ends.B.At - ends.A.At));
                var placement = new Placement([frame.Origin.X, frame.Origin.Y, frame.Origin.Z], [frame.X.X, frame.X.Y, frame.X.Z], [z.X, z.Y, z.Z]);
                parts.Add(BuiltPart.Of(section.Id, Members.Document(profile, length, a, b), placement, Members.Document(profile, length, a, b, bored: false)));
            }
            return new(parts, diagnostics);
        }
    }

    static readonly double Tilt = 20 * Math.PI / 180;

    /// <summary>A post 40 × 20 from z = 0 to 100 between a floor and a ceiling 10 thick, the ceiling tilted 20° round x.</summary>
    public static readonly PartDocument Post = new(
    [
        new Block("floor", BooleanOp.New, [-50, -50, -10], [100, 100, 10]),
        new Block("post", BooleanOp.Join, [-20, -10, -8], [40, 20, 112]),
        new Extrude("ceiling", BooleanOp.Join, new SketchPlane([0, 0, 100], [1, 0, 0], [0, -Math.Sin(Tilt), Math.Cos(Tilt)]), new Profile([-50, -50],
            [new ProfileEdge("s", [50, -50]), new ProfileEdge("e", [50, 50]), new ProfileEdge("n", [-50, 50]), new ProfileEdge("w", [-50, -50])]), 0, 10),
    ]);

    public static readonly BuildDocument PostBuild = new("design.json",
    [
        new PlateSection("floor", 10, Face: "floor/z-"),
        new PlateSection("ceiling", 10, Face: "ceiling/end"),
        new BeamSection("post", "RHS40x20x2", Lines: [[[0, 0, 10], [0, 0, 90]]], Up: [0, 1, 0]),
    ]);

    public static BuildResult BuildPost(FrepModeler? modeler = null) =>
        Rationaliser.PlatesAndTubes().WithBeams(new StraightBeams(new Catalog())).Run(PostBuild, Post, (modeler ?? Modeler).Regenerate(Post));

    [Fact]
    public void A_catalog_profile_is_a_sketch_of_its_exact_lines_and_arcs()
    {
        var (rhs, chs) = (new Catalog().Find("RHS40x20x2")!, new Catalog().Find("CHS48.3x2.9")!);

        var sketch = rhs.Sketch("section");

        sketch.Entities.Select(e => e.Id).Should().Equal([.. Enumerable.Range(1, 8).Select(i => $"outer-{i}"), .. Enumerable.Range(1, 8).Select(i => $"bore-{i}")]);
        sketch.Entities.OfType<SketchArc>().Should().HaveCount(8);
        sketch.Points.Should().HaveCount(20, "eight ends a loop and four centres the two loops share, each once");
        rhs.Sketch("hull", bored: false).Entities.Should().OnlyContain(e => e.Id.StartsWith("outer-"));
        rhs.Area.Should().BeApproximately(40 * 20 - (4 - Math.PI) * 16 - (36 * 16 - (4 - Math.PI) * 4), 1e-9);
        rhs.Box.Should().Be((-20, -10, 20, 10));
        (rhs.Round, chs.Round).Should().Be((false, true));
        new Catalog().Find("IPE200").Should().BeNull();

        // A round profile's member reads back as a tube: what the cut list and same parts know of tubes holds for it.
        var tube = TubeShape.Of(Members.Document(chs, 500, new Vector3D(0, 0, -1), new Vector3D(0, 0, 1)))!;
        (tube.Outside, tube.Wall, tube.Length).Should().Be((48.3, 2.9, 500));
    }

    [Fact]
    public void Without_a_beam_method_a_beam_section_is_reported_and_the_rest_built()
    {
        var result = Rationaliser.PlatesAndTubes().Run(PostBuild, Post, Modeler.Regenerate(Post));

        result.Diagnostics.Should().ContainSingle().Which.Should().Match<Diagnostic>(d => d.Code == BuildDiagnostic.NoMethod && d.FeatureId == "post");
        result.Diagnostics[0].Message.Should().Contain("'RHS40x20x2'");
        result.Bundle.Assembly.Components.Select(c => c.Id).Should().Equal("floor", "ceiling");
    }

    [Fact]
    public void A_beam_method_given_to_the_rationaliser_builds_the_profile_between_the_plates()
    {
        var result = BuildPost();

        result.Diagnostics.Should().BeEmpty();
        result.Parts.Keys.Should().Equal("floor.json", "post.json");
        result.Bundle.Assembly.Components.Select(c => (c.Id, c.Part)).Should().Equal(("floor", "floor.json"), ("ceiling", "floor.json"), ("post", "post.json"));
        var post = result.Parts["post.json"];
        post.Features.Select(f => f.GetType().Name).Should().Equal("Sketch", "Extrude", "PlaneCut", "PlaneCut");
        // From the floor's top (z = 0) to the ceiling's underside, which the axis meets at z = 100.
        ((PlaneCut)post.Features[3]).Point[2].Should().BeApproximately(100, 1e-9);
        ((PlaneCut)post.Features[3]).Normal.Zip([0, -Math.Sin(Tilt), Math.Cos(Tilt)]).Should().OnlyContain(x => Math.Abs(x.First - x.Second) < 1e-12);
        ((PlaneCut)post.Features[2]).Normal.Zip([0, 0, -1.0]).Should().OnlyContain(x => Math.Abs(x.First - x.Second) < 1e-12);

        // Placed, the post stands on the floor, its walls 2 thick, its top under the ceiling's tilted underside.
        var component = result.Bundle.Assembly.Components.Single(c => c.Id == "post");
        var placed = new PlacedField(new Compiler(new ModelingSettings()).Compile(post).Field!, AssemblyModeler.FrameOf(component.Placement));
        placed.Value(new Point3D(19.9, 9.9 - 4, 0.001)).Should().BeNegative();
        placed.Value(new Point3D(19.9, 9.9 - 4, -0.001)).Should().BePositive();
        placed.Value(new Point3D(0, 0, 50)).Should().BePositive("the bore is open");
        placed.Value(new Point3D(0, 9, 100 + 9 * Math.Tan(Tilt) - 0.001)).Should().BeNegative();
        placed.Value(new Point3D(0, 9, 100 + 9 * Math.Tan(Tilt) + 0.001)).Should().BePositive();
        placed.Value(new Point3D(19, 0, 50)).Should().BeNegative();
        result.Instances.Single(i => i.Id == "post").Built.Hull.Value(new Point3D(0, 0, 50)).Should().BeNegative("the hull is the section's envelope");
    }
}
