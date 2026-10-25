using BasAutomation.Geometry;
using static Isoform.Brep.Tests.Shapes;

namespace Isoform.Brep.Tests;

/// <summary>Valid bodies broken in one way each: the check must name exactly that.</summary>
public class BodyCheckTests
{
    static Body Plate() =>
        Extrusion.Extrude(new Region(Rectangle(0, 0, 100, 50), [Circle(30, 25, 10)]), RigidTransform3D.Identity, 0, 10).Result!.Body;

    /// <summary>A copy of the body through the builder, with faces, senses, uses and p-curves changed on the way.</summary>
    static Body Copy(Body body, Func<FaceId, bool>? keepFace = null, Func<FaceId, bool, bool>? sense = null,
        Func<CoedgeId, BodyBuilder.Use, BodyBuilder.Use>? use = null, bool reverseLoops = false)
    {
        var b = new BodyBuilder(body.Tolerance);
        foreach (var v in body.Vertices) b.AddVertex(v.Point);
        foreach (var e in body.Edges) b.AddEdge(e.Curve, e.T0, e.T1, e.Start, e.End);
        foreach (var _ in body.Shells) b.AddShell();
        var faces = new Dictionary<FaceId, FaceId>();
        for (var f = 0; f < body.Faces.Length; f++)
        {
            var id = new FaceId(f);
            if (keepFace?.Invoke(id) == false) continue;
            faces[id] = b.AddFace(body[id].Shell, body[id].Surface, sense?.Invoke(id, body[id].SameSense) ?? body[id].SameSense);
        }
        foreach (var (old, copy) in faces)
        foreach (var loop in body[old].Loops)
        {
            var uses = body.CoedgesOf(loop).Select(c => new BodyBuilder.Use(body[c].Edge, body[c].Forward, body[c].Pcurve))
                .Select((u, i) => use?.Invoke(body.CoedgesOf(loop).ElementAt(i), u) ?? u).ToList();
            if (reverseLoops) uses = [.. uses.AsEnumerable().Reverse().Select(u => u with { Forward = !u.Forward })];
            b.AddLoop(copy, uses);
        }
        return b.Build();
    }

    static IEnumerable<ProblemKind> Kinds(Body body) => BodyCheck.Problems(body).Select(p => p.Kind).Distinct();

    [Fact]
    public void An_unchanged_copy_is_valid()
    {
        BodyCheck.Problems(Copy(Plate())).Should().BeEmpty();
    }

    [Fact]
    public void A_missing_face_leaves_its_edges_used_once()
    {
        var problems = BodyCheck.Problems(Copy(Plate(), keepFace: f => f.Index != 0));
        problems.Should().Contain(p => p.Kind == ProblemKind.EdgeUses);
        problems.Should().NotContain(p => p.Kind == ProblemKind.PcurveOffEdge);
    }

    [Fact]
    public void A_face_against_its_sense_is_reported()
    {
        Kinds(Copy(Plate(), sense: (f, s) => f.Index == 0 ? !s : s)).Should().Equal(ProblemKind.FaceOrientation);
    }

    [Fact]
    public void A_seam_taken_on_one_side_twice_opens_the_loop_in_uv()
    {
        var plate = Plate();
        var wall = plate.Faces.Select((_, i) => new FaceId(i)).Single(f => plate[f].Surface is Cylinder);
        var seam = plate.Edges.Select((_, i) => new EdgeId(i)).Single(e => plate.UsesOf(e).All(c => plate.FaceOf(c) == wall));
        var down = plate.UsesOf(seam).Single(c => !plate[c].Forward);
        var up = plate.UsesOf(seam).Single(c => plate[c].Forward);

        var broken = Copy(plate, use: (c, u) => c == down ? u with { Pcurve = plate[up].Pcurve } : u);

        Kinds(broken).Should().Contain(ProblemKind.LoopOpenInUv).And.NotContain(ProblemKind.PcurveOffEdge);
    }

    [Fact]
    public void A_pcurve_off_its_edge_is_reported()
    {
        var plate = Plate();
        var broken = Copy(plate, use: (c, u) => c.Index == 0 && u.Pcurve is Line2 l ? u with { Pcurve = l with { Origin = l.Origin + new Vector2D(0, 1e-6) } } : u);
        Kinds(broken).Should().Contain(ProblemKind.PcurveOffEdge);
    }

    [Fact]
    public void A_use_flipped_runs_both_uses_one_way_and_breaks_the_loop()
    {
        var broken = Copy(Plate(), use: (c, u) => c.Index == 0 ? u with { Forward = !u.Forward } : u);
        Kinds(broken).Should().Contain(ProblemKind.EdgeSenses).And.Contain(ProblemKind.LoopBroken);
    }

    [Fact]
    public void A_body_turned_inside_out_is_consistent_face_by_face_but_encloses_negative_volume()
    {
        var inside = Copy(Plate(), sense: (_, s) => !s, reverseLoops: true);
        Kinds(inside).Should().Equal(ProblemKind.ShellOrientation);
        MassProperties.Volume(inside).Should().BeApproximately(-(5_000 - Math.PI * 100) * 10, 1e-9);
    }

    [Fact]
    public void A_curve_end_away_from_its_vertex_is_reported()
    {
        var plate = Plate();
        var b = new BodyBuilder();
        for (var v = 0; v < plate.Vertices.Length; v++)
            b.AddVertex(v == 0 ? plate.Vertices[v].Point + new Vector3D(1e-6, 0, 0) : plate.Vertices[v].Point);
        foreach (var e in plate.Edges) b.AddEdge(e.Curve, e.T0, e.T1, e.Start, e.End);
        b.AddShell();
        for (var f = 0; f < plate.Faces.Length; f++)
        {
            var face = b.AddFace(new ShellId(0), plate.Faces[f].Surface, plate.Faces[f].SameSense);
            foreach (var loop in plate.Faces[f].Loops)
                b.AddLoop(face, [.. plate.CoedgesOf(loop).Select(c => new BodyBuilder.Use(plate[c].Edge, plate[c].Forward, plate[c].Pcurve))]);
        }
        Kinds(b.Build()).Should().Equal(ProblemKind.EdgeEnds);
    }
}

