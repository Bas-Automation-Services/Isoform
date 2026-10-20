using Isoform.Document;
using Isoform.Modeling;
using Isoform.Step;
using Isoform.Tests;
using Xunit.Abstractions;

namespace Isoform.Occt.Tests;

/// <summary>
/// K6: Isoform's own exact B-rep, written as STEP by Isoform and read back by OCCT. OCCT must find a valid solid
/// with Isoform's face, edge and vertex counts and the analytic volume; where the OCCT baseline can model the same
/// document, every vertex and edge must also lie within the modelling tolerance of OCCT's own.
/// </summary>
public class StepExportTests(ITestOutputHelper output)
{
    static readonly SketchPlane Xy = new([0, 0, 0], [1, 0, 0], [0, 0, 1]);
    static readonly SketchPlane Xz = new([0, 0, 0], [1, 0, 0], [0, -1, 0]);
    static readonly Block Plate = new("plate", BooleanOp.New, [0, 0, 0], [100, 50, 10]);

    static Profile Loop(double[] start, params (string Id, double U, double V, double Bulge)[] edges) =>
        new(start, edges.Select(e => new ProfileEdge(e.Id, [e.U, e.V], e.Bulge)).ToList());

    /// <summary>
    /// A part and its analytic volume. OCCT's volume of the read-back file agrees to round-off, except where the
    /// reader has to rebuild a non-circular closed curve's parameter-space image (the file carries 3D curves only):
    /// that integration loosens the volume to about 1e-7 while the geometry itself matches OCCT's to 1e-13.
    /// </summary>
    public sealed record Part(string Name, PartDocument Doc, double Volume, double VolumeTolerance = 1e-9);

    public static IEnumerable<Part> Parts()
    {
        yield return new("block", new([new Block("box", BooleanOp.New, [0, 0, 0], [40, 30, 20])]), 40 * 30 * 20);
        yield return new("plate with hole", new([Plate, new Hole("hole", [30, 25], 10)]), 50000 - Math.PI * 25 * 10);
        yield return new("two-hole plate", new([Plate, new Hole("hole", [30, 25], 10), new Hole("hole2", [70, 25], 8)]),
            50000 - Math.PI * (25 + 16) * 10);
        // A groove across the top and a 45° cut off the x+ top edge: 50000 − 20·50·4 − ½·10·10·50.
        yield return new("slot and plane cut", new(
            [
                Plate,
                new Block("slot", BooleanOp.Cut, [40, -1, 6], [20, 52, 5]),
                new PlaneCut("cut", [90, 25, 10], [1, 0, 1]),
            ]), 50000 - 4000 - 2500);
        yield return new("L bracket", new([new Extrude("bracket", BooleanOp.New, Xy, Loop([0, 0],
            ("bottom", 40, 0, 0), ("tip", 40, 5, 0), ("inner", 5, 5, 0), ("riser", 5, 30, 0), ("top", 0, 30, 0), ("back", 0, 0, 0)), 0, 20)]),
            (40 * 5 + 5 * 25) * 20);
        yield return new("stadium", new([new Extrude("slot", BooleanOp.New, Xy, Loop([0, 0],
            ("a", 20, 0, 0), ("right", 20, 10, 1), ("b", 0, 10, 0), ("left", 0, 0, 1)), 0, 5)]),
            (20 * 10 + Math.PI * 25) * 5);
        yield return new("stepped shaft", new([new Revolve("shaft", BooleanOp.New, Xz, Loop([0, 0],
            ("bottom", 10, 0, 0), ("big", 10, 20, 0), ("step", 6, 20, 0), ("small", 6, 40, 0), ("top", 0, 40, 0), ("axis", 0, 0, 0)))]),
            Math.PI * (100 * 20 + 36 * 20));

        // Beyond planes and cylinders: cones, spheres, tori, ellipses, a cylinder merged from two arcs.
        yield return new("cone with a dome", new([new Revolve("nose", BooleanOp.New, Xz, Loop([0, 0],
            ("base", 10, 0, 0), ("side", 10, 10, 0), ("cone", 5, 20, 0), ("dome", 0, 25, Math.Tan(Math.PI / 8)), ("axis", 0, 0, 0)))]),
            Math.PI * (1000 + 10.0 / 3 * (100 + 50 + 25) + 2.0 / 3 * 125));
        yield return new("torus", new([new Revolve("ring", BooleanOp.New, Xz, Loop([20, -3], ("outer", 20, 3, 1), ("inner", 20, -3, 1)))]),
            2 * Math.PI * Math.PI * 20 * 9);
        yield return new("quarter turn", new([new Revolve("elbow", BooleanOp.New, Xz, Loop([5, 0],
            ("low", 10, 0, 0), ("outer", 10, 4, 0), ("high", 5, 4, 0), ("inner", 5, 0, 0)), 90)]),
            0.25 * 2 * Math.PI * 7.5 * 20);
        yield return new("disc from two arcs", new([new Extrude("disc", BooleanOp.New, Xy, Loop([-20, 0], ("a", 20, 0, 1), ("b", -20, 0, 1)), 0, 8)]),
            Math.PI * 400 * 8);
        // The cut plane z = 10 − (x − 20)·tan 30° crosses the hole wall in an ellipse; the hole stays clear of the
        // plate's edges, so its volume is the plate's minus a cylinder averaging the cut's height at x = 30.
        var slope = Math.Tan(Math.PI / 6);
        yield return new("oblique cut through a hole", new(
            [
                Plate,
                new Hole("hole", [30, 25], 10),
                new PlaneCut("cut", [20, 25, 10], [slope, 0, 1]),
            ]), Prism(slope) - Math.PI * 25 * (10 - 10 * slope), VolumeTolerance: 1e-6);

        // Rolling-ball fillets between planes are cylinders, between a plane and a perpendicular cylinder tori.
        var corner = 1 - Math.PI / 4; // the section a fillet of radius 1 cuts off a right-angled edge
        yield return new("edit-suite base", EditSuite.Base, 50000 - Math.PI * 25 * 10 - corner * 4 * 50);
        // Pappus: the cut-off section, of area corner·r², sits x̄ outside the hole wall.
        var centroid = 2 * (5.0 / 6 - Math.PI / 4) / corner;
        yield return new("filleted hole edge", new([Plate, new Hole("hole", [30, 25], 10), new Fillet("round", ["plate/z+", "hole/wall"], 2)]),
            50000 - Math.PI * 25 * 10 - 2 * Math.PI * (5 + centroid) * corner * 4);
        yield return new("L bracket with inside fillet", new(
            [
                new Extrude("bracket", BooleanOp.New, Xy, Loop([0, 0],
                    ("bottom", 40, 0, 0), ("tip", 40, 5, 0), ("inner", 5, 5, 0), ("riser", 5, 30, 0), ("top", 0, 30, 0), ("back", 0, 0, 0)), 0, 20),
                new Fillet("inside", ["bracket/inner", "bracket/riser"], 3),
            ]), (40 * 5 + 5 * 25 + corner * 9) * 20);
    }

    /// <summary>The plate below z = 10 − (x − 20)·slope, clipped at z = 0.</summary>
    static double Prism(double slope)
    {
        var zeroAt = 20 + 10 / slope; // where the cut reaches the bottom
        return 50 * (20 * 10 + 0.5 * (zeroAt - 20) * 10);
    }

    public static IEnumerable<object[]> PartNames => Parts().Select(p => new object[] { p.Name });

    [Theory]
    [MemberData(nameof(PartNames))]
    public void Exact_step_reads_back_in_occt_as_the_same_solid(string name)
    {
        var part = Parts().Single(p => p.Name == name);
        var settings = new ModelingSettings { CellSize = 0.5 };
        var frep = new FrepModeler(settings).Regenerate(part.Doc);
        frep.Diagnostics.Should().BeEmpty();
        var t = frep.Topology!;

        var (brep, shape) = RoundTrip(frep, settings, name);
        var counts = Counts(shape);
        output.WriteLine($"Isoform: {t.Faces.Count}F {t.Edges.Count}E {t.Vertices.Count}V; B-rep {brep.Edges.Count} edges, {brep.Vertices.Count} vertices");
        output.WriteLine($"OCCT:    {counts.Faces}F {counts.Edges}E {counts.Vertices}V (all: {shape.EdgeCount} edges, {shape.VertexCount} vertices), valid {shape.IsValid}, {shape.SolidCount} solid(s), max tolerance {shape.MaxTolerance:E2}");
        output.WriteLine($"volume {shape.Volume:R} vs {part.Volume:R}: relative error {Math.Abs(shape.Volume - part.Volume) / part.Volume:E2}");

        shape.IsValid.Should().BeTrue();
        shape.SolidCount.Should().Be(1);
        counts.Should().Be((t.Faces.Count, t.Edges.Count, t.Vertices.Count));

        // The OCCT baseline models blocks, holes, plane cuts and fillets.
        if (part.Doc.Features.All(f => f is Block or Hole or PlaneCut or Fillet))
        {
            var occt = new OcctModeler(settings).Regenerate(part.Doc).Shape!;
            var (vertexGap, edgeGap) = Gaps(shape, occt);
            output.WriteLine($"against OCCT's own model: vertices within {vertexGap:E2}, edges within {edgeGap:E2}; its volume {occt.Volume:R}");
            vertexGap.Should().BeLessThan(1e-7);
            edgeGap.Should().BeLessThan(1e-7);
        }
        shape.Volume.Should().BeApproximately(part.Volume, part.Volume * part.VolumeTolerance);
    }

    [Fact]
    public void Unsupported_faces_are_named()
    {
        // A smooth blend has no closed-form surface.
        var doc = new PartDocument([Plate, new Block("boss", BooleanOp.Join, [40, 15, 10], [20, 20, 10], Blend: 3)]);
        var settings = new ModelingSettings { CellSize = 0.5 };

        var build = () => ExactBrep.Build(new FrepModeler(settings).Regenerate(doc), settings);

        build.Should().Throw<NotSupportedException>().WithMessage("*boss/blend (Blend)*");
    }

    /// <summary>
    /// Writes what exports today: every playground example, every edit-suite result and a two-body part, with
    /// OCCT's verdict on the read-back file or the reason the export stopped.
    /// </summary>
    [Fact]
    public void Report_export_coverage()
    {
        var settings = new ModelingSettings { CellSize = 0.5 };
        var parts = Playground.Examples.All.ToList();
        foreach (var id in EditSuite.CaseIds.Select(c => (string)c[0]))
        {
            var c = EditSuite.Case(id);
            parts.Add(($"{id}: {c["title"]}", DocumentEdits.Apply(DocumentEdits.Apply(EditSuite.Base, EditSuite.Edits(c, "setup")), EditSuite.Edits(c, "edit"))));
        }
        parts.Add(("two overlapping bodies", new PartDocument([Plate, new Block("post", BooleanOp.New, [40, 15, 5], [20, 20, 30])])));

        output.WriteLine("| Part | Isoform F/E/V | OCCT read-back | Volume vs mesh |");
        output.WriteLine("|---|---|---|---|");
        foreach (var (name, doc) in parts)
        {
            var frep = new FrepModeler(settings).Regenerate(doc);
            if (frep.Topology is not { } t)
            {
                output.WriteLine($"| {name} | no geometry | – | – |");
                continue;
            }
            try
            {
                var (_, shape) = RoundTrip(frep, settings, name);
                var counts = Counts(shape);
                var same = counts == (t.Faces.Count, t.Edges.Count, t.Vertices.Count) ? "same F/E/V" : $"{counts.Faces}/{counts.Edges}/{counts.Vertices}";
                var mesh = MeshVolume(frep.Mesh!);
                output.WriteLine($"| {name} | {t.Faces.Count}/{t.Edges.Count}/{t.Vertices.Count} | {(shape.IsValid ? "valid" : "**invalid**")}, {shape.SolidCount} solid(s), {same}, tolerance {shape.MaxTolerance:G2} | {(shape.Volume - mesh) / mesh:E1} |");
            }
            catch (Exception e) when (e is NotSupportedException or OcctException)
            {
                output.WriteLine($"| {name} | {t.Faces.Count}/{t.Edges.Count}/{t.Vertices.Count} | not exported: {e.Message} | – |");
            }
        }
    }

    static (Brep Brep, OcctShape Shape) RoundTrip(FrepResult frep, ModelingSettings settings, string name)
    {
        var brep = ExactBrep.Build(frep, settings);
        var path = Path.Combine(Path.GetTempPath(), $"isoform-{Guid.NewGuid():N}.step");
        StepWriter.Write(brep, path, name);
        var shape = OcctShape.ReadStep(path);
        File.Delete(path);
        return (brep, shape);
    }

    static double MeshVolume(Meshing.Mesh mesh)
    {
        var v = mesh.Vertices;
        return mesh.Triangles().Sum(t => v[t.A].AsVector().Dot(v[t.B].AsVector().Cross(v[t.C].AsVector()))) / 6;
    }

    /// <summary>Counts as Isoform's topology counts them: seams (one face) are not edges, corners are where 3+ faces meet.</summary>
    static (int Faces, int Edges, int Vertices) Counts(OcctShape s) => (
        s.FaceCount,
        Enumerable.Range(0, s.EdgeCount).Count(e => s.EdgeFaces(e).Length == 2),
        Enumerable.Range(0, s.VertexCount).Count(v => s.VertexFaces(v).Length >= 3));

    /// <summary>
    /// The largest distance from a corner of <paramref name="a"/> to the nearest corner of <paramref name="b"/> (and
    /// back, so the sets match), and from points along every edge of <paramref name="a"/> to the nearest edge of <paramref name="b"/>.
    /// </summary>
    static (double Vertices, double Edges) Gaps(OcctShape a, OcctShape b)
    {
        double[][] Corners(OcctShape s) => Enumerable.Range(0, s.VertexCount).Where(v => s.VertexFaces(v).Length >= 3).Select(s.VertexPoint).ToArray();
        static double Dist(double[] p, double[] q) => Math.Sqrt(Enumerable.Range(0, 3).Sum(i => (p[i] - q[i]) * (p[i] - q[i])));
        var (ca, cb) = (Corners(a), Corners(b));
        ca.Length.Should().Be(cb.Length);
        var vertices = ca.Length == 0 ? 0 : Math.Max(ca.Max(p => cb.Min(q => Dist(p, q))), cb.Max(p => ca.Min(q => Dist(p, q))));

        var edges = 0.0;
        // Seams sit wherever each modeller chose to cut a periodic face open; only real edges are compared.
        for (var e = 0; e < a.EdgeCount; e++)
            if (a.EdgeFaces(e).Length == 2)
            foreach (var s in new[] { 0.0, 0.25, 0.5, 0.75 })
            {
                var p = a.EdgePoint(e, s);
                edges = Math.Max(edges, Enumerable.Range(0, b.EdgeCount).Min(f => b.EdgeDistance(f, p[0], p[1], p[2])));
            }
        return (vertices, edges);
    }
}
