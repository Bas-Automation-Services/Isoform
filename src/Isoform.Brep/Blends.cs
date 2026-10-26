using BasAutomation.Geometry;

namespace Isoform.Brep;

public enum BlendKind { Fillet, Chamfer }

/// <summary>
/// Fillets and chamfers on the straight edge between two planar faces. A convex edge loses the prism between the
/// edge and the ball (or the chamfer's plane) rolled along it; a concave edge gains it. The prism runs past the edge's
/// ends, so the faces the edge ends on cut the new face off (an arc or an ellipse on each). This is the rounded edge
/// only where the prism touches nothing but the two faces and the faces at the edge's ends: any other face it changes
/// (a radius too large for a face, an edge that ends inside the material) refuses the blend.
/// </summary>
public static class Blends
{
    /// <summary>
    /// The body with the edge between faces <paramref name="a"/> and <paramref name="b"/> rounded to radius
    /// <paramref name="size"/> or chamfered back by it on both faces. Origins: operand 0 the body's faces, operand 1
    /// the blend's; <c>Face</c> is the blend's own face among operand 1's (the arc or the chamfer's slope).
    /// </summary>
    public static (Shaped? Result, FaceId Face, string? Problem) Apply(Body body, FaceId a, FaceId b, double size, BlendKind kind)
    {
        if (body[a].Surface is not Plane pa || body[b].Surface is not Plane pb)
            return (null, default, "only an edge between two flat faces can be rounded or chamfered yet");
        var edges = Enumerable.Range(0, body.Edges.Length).Select(e => new EdgeId(e))
            .Where(e => body.UsesOf(e).Select(body.FaceOf).ToHashSet().SetEquals([a, b])).ToList();
        if (edges.Count == 0) return (null, default, "the two faces share no edge");
        if (edges.Any(e => body[e].Curve is not Line3)) return (null, default, "the edge between the faces is not straight");
        var line = (Line3)body[edges[0]].Curve;
        var d = line.Direction;
        if (edges.Any(e => !Collinear(line, (Line3)body[e].Curve))) return (null, default, "the faces meet in edges that are not on one line");

        var nA = pa.Frame.ZAxis * (body[a].SameSense ? 1 : -1);
        var nB = pb.Frame.ZAxis * (body[b].SameSense ? 1 : -1);
        var c = nA.Dot(nB);
        if (Math.Abs(c) >= 1 - 1e-9) return (null, default, "the two faces are parallel along the edge");
        // Into each face, square to the edge: to the left of the edge as the face's loop runs it, seen from outside.
        var (inA, inB) = (Into(body, edges[0], a, nA), Into(body, edges[0], b, nB));
        var convex = nA.Dot(inB) < 0;
        if (!convex && kind == BlendKind.Chamfer) return (null, default, "only a convex edge can be chamfered");

        // The edge's extent along its line, and a point on it before its start. A convex edge's prism runs on past its
        // ends, where it cuts nothing; a concave edge's fills, so it stops on the faces the edge ends on.
        var ts = edges.SelectMany(e => new[] { body[e].T0, body[e].T1 }.Select(t => (body[e].Curve.At(t) - line.Origin).Dot(d))).ToList();
        var margin = convex ? 2 * size + 1 : 0;
        if (!convex)
        {
            var tips = new[] { ts.Min(), ts.Max() }.Select(t => line.Origin + d * t).ToList();
            var square = body.Coedges.Select((_, i) => new CoedgeId(i))
                .Where(co => tips.Any(e => (body[body.StartOf(co)].Point - e).Length <= Intersections.Snap))
                .Select(body.FaceOf).Where(f => f != a && f != b)
                .All(f => body[f].Surface is Plane end && end.Frame.ZAxis.Cross(d).Length < 1e-9);
            if (!square) return (null, default, "a concave edge can only be rounded where it ends on faces square to it");
        }
        var origin = line.Origin + d * (ts.Min() - margin);
        var frame = RigidTransform3D.FromFrame(origin, d, nA);
        Point2D Flat(Vector3D v) => new(v.Dot(frame.XAxis), v.Dot(frame.YAxis));

        ProfileLoop profile;
        var p = new Point2D(0, 0);
        if (kind == BlendKind.Chamfer)
            profile = new ProfileLoop([new LineSegment(p, Flat(inA * size)), new LineSegment(Flat(inA * size), Flat(inB * size)), new LineSegment(Flat(inB * size), p)]);
        else
        {
            // The ball's centre is `size` from both planes, inside the material for a convex edge, outside for a concave one.
            var side = convex ? -1 : 1;
            var center = (nA + nB) * (side * size / (1 + c));
            var (ta, tb) = (center - nA * (side * size), center - nB * (side * size));
            var (o, fa, fb) = (Flat(center), Flat(ta), Flat(tb));
            var start = Math.Atan2(fa.Y - o.Y, fa.X - o.X);
            var sweep = Math.Atan2(fb.Y - o.Y, fb.X - o.X) - start;
            sweep = Math.IEEERemainder(sweep, 2 * Math.PI);
            profile = new ProfileLoop([new LineSegment(p, fa), new ArcSegment(o, size, start, sweep), new LineSegment(fb, p)]);
        }
        var (tool, why) = Extrusion.Extrude(new Region(profile), frame, 0, ts.Max() - ts.Min() + 2 * margin, body.Tolerance);
        if (tool is null) return (null, default, $"the {(kind == BlendKind.Fillet ? "fillet" : "chamfer")} has no room: {why}");

        var (result, problem) = convex ? Booleans.Subtract(body, tool.Body) : Booleans.Unite(body, tool.Body);
        if (problem is not null) return (null, default, problem);
        if (result is not [var one]) return (null, default, $"the {(kind == BlendKind.Fillet ? "fillet" : "chamfer")} parts the body");

        // Faces other than the two and those at the edge's ends must come through unchanged.
        var ends = new[] { ts.Min(), ts.Max() }.Select(t => line.Origin + d * t).ToList();
        var allowed = new HashSet<FaceId> { a, b };
        foreach (var co in body.Coedges.Select((_, i) => new CoedgeId(i)))
            if (ends.Any(e => (body[body.StartOf(co)].Point - e).Length <= Intersections.Snap)) allowed.Add(body.FaceOf(co));
        for (var f = 0; f < body.Faces.Length; f++)
        {
            var id = new FaceId(f);
            if (allowed.Contains(id)) continue;
            var after = Enumerable.Range(0, one.Body.Faces.Length).Where(g => one.Origins[g].Contains(new Origin(0, id))).ToList();
            var before = MassProperties.Area(body, id);
            if (after.Any(g => one.Origins[g].Count > 1) || Math.Abs(after.Sum(g => MassProperties.Area(one.Body, new FaceId(g))) - before) > 1e-9 * (1 + before))
                return (null, default, $"the {(kind == BlendKind.Fillet ? "fillet" : "chamfer")} reaches other faces than the two it runs between and those at its ends (a size too large, or an edge that ends inside the material)");
        }
        // The profile's second segment is the arc (or the slope): its side face is the new face.
        return (one, tool.Sides[0][1], null);
    }

    static bool Collinear(Line3 p, Line3 q) => p.Direction.Cross(q.Direction).Length < 1e-9 &&
        (q.Origin - p.Origin - p.Direction * (q.Origin - p.Origin).Dot(p.Direction)).Length <= Intersections.Snap;

    // The direction into face f square to the edge: left of the edge as f's loop runs it, seen from outside (along n).
    static Vector3D Into(Body body, EdgeId e, FaceId f, Vector3D n)
    {
        var use = body.UsesOf(e).First(u => body.FaceOf(u) == f);
        var along = ((Line3)body[e].Curve).Direction * (body[use].Forward ? 1 : -1);
        return n.Cross(along).Normalized();
    }
}
