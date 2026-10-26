using BasAutomation.Geometry;

namespace Isoform.Brep;

public enum BlendKind { Fillet, Chamfer }

/// <summary>
/// Fillets and chamfers. On the straight edge between two planar faces a convex edge loses the prism between the
/// edge and the ball (or the chamfer's plane) rolled along it; a concave edge gains it. The prism runs past the edge's
/// ends, so the faces the edge ends on cut the new face off (an arc or an ellipse on each). On the circle where a flat
/// face meets a cylinder square to it (a hole's rim, a boss's foot) the ball rolls round the axis: the ring it leaves
/// out (or fills) is the spandrel turned round the axis, its new face a torus. This is the rounded edge only where the
/// prism or ring touches nothing but the two faces and the faces at the edge's ends: any other face it changes (a
/// radius too large for a face, an edge that ends inside the material) refuses the blend.
/// </summary>
public static class Blends
{
    /// <summary>
    /// The body with the edge between faces <paramref name="a"/> and <paramref name="b"/> rounded to radius
    /// <paramref name="size"/> or chamfered back by it on both faces. Origins: operand 0 the body's faces, operand 1
    /// the blend's; <c>Face</c> is the blend's own face among operand 1's (the arc or the chamfer's slope).
    /// </summary>
    public static (Shaped? Result, FaceId Face, string? Problem) Apply(Body body, FaceId a, FaceId b, double size, BlendKind kind) =>
        (body[a].Surface, body[b].Surface) switch
        {
            (Plane pa, Plane pb) => Straight(body, a, b, pa, pb, size, kind),
            (Plane, Cylinder) or (Cylinder, Plane) => Rim(body, a, b, size, kind),
            _ => (null, default, "only an edge between two flat faces, or a flat face and a cylinder, can be rounded or chamfered yet"),
        };

    static (Shaped?, FaceId, string?) Straight(Body body, FaceId a, FaceId b, Plane pa, Plane pb, double size, BlendKind kind)
    {
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
        // Where the edge ends against two earlier fillets, three meet at a corner: Isoform rounds that with a ball,
        // which these prisms do not make.
        foreach (var t in new[] { ts.Min(), ts.Max() })
        {
            var tip = line.Origin + d * t;
            var curved = body.Coedges.Select((_, i) => new CoedgeId(i))
                .Where(co => (body[body.StartOf(co)].Point - tip).Length <= Intersections.Snap)
                .Select(body.FaceOf).Distinct().Count(f => f != a && f != b && body[f].Surface is not Plane);
            if (curved >= 2) return (null, default, "three fillets meeting at a corner need a ball there, which the managed kernel does not make yet");
        }
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
        if (tool is null) return (null, default, $"the {Name(kind)} has no room: {why}");

        var ends = new[] { ts.Min(), ts.Max() }.Select(t => line.Origin + d * t).ToList();
        var allowed = new HashSet<FaceId> { a, b };
        foreach (var co in body.Coedges.Select((_, i) => new CoedgeId(i)))
            if (ends.Any(e => (body[body.StartOf(co)].Point - e).Length <= Intersections.Snap)) allowed.Add(body.FaceOf(co));
        // The profile's second segment is the arc (or the slope): its side face is the new face.
        return Settle(body, tool.Body, tool.Sides[0][1], convex, allowed, kind);
    }

    // The circle where a flat face meets a cylinder square to it: the spandrel in the half-plane through the axis
    // (distance from the axis, height above the face), turned round the axis.
    static (Shaped?, FaceId, string?) Rim(Body body, FaceId a, FaceId b, double size, BlendKind kind)
    {
        var (fp, fc) = body[a].Surface is Plane ? (a, b) : (b, a);
        var (plane, cylinder) = ((Plane)body[fp].Surface, (Cylinder)body[fc].Surface);
        var axis = cylinder.Frame.ZAxis;
        if (plane.Frame.ZAxis.Cross(axis).Length >= 1e-9)
            return (null, default, "the flat face is at an angle to the cylinder, so the edge is an ellipse, which has no exact fillet");
        if (kind == BlendKind.Chamfer) return (null, default, "a chamfer round a circle is a cone, which the managed kernel does not have yet");
        var edges = Enumerable.Range(0, body.Edges.Length).Select(e => new EdgeId(e))
            .Where(e => body.UsesOf(e).Select(body.FaceOf).ToHashSet().SetEquals([a, b])).ToList();
        if (edges is not [var edge] || body[edge].Curve is not Circle3 || !body[edge].IsClosed)
            return (null, default, "only a whole circle between a flat face and a cylinder can be rounded yet");

        var origin = cylinder.Frame.Origin + axis * (plane.Frame.Origin - cylinder.Frame.Origin).Dot(axis);
        var frame = RigidTransform3D.FromFrame(origin, axis, cylinder.Frame.XAxis);
        // At the edge's start, in the half-plane: x away from the axis, y along it.
        var e0 = body[edge];
        var at = e0.Curve.At(e0.T0);
        var radial = (at - origin).Normalized();
        Vector2D Flat(Vector3D v) => new(v.Dot(radial), v.Dot(axis));
        var nP = plane.Frame.ZAxis * (body[fp].SameSense ? 1 : -1);
        var nC = radial * (body[fc].SameSense ? 1 : -1);
        Vector3D Into(FaceId f, Vector3D n)
        {
            var use = body.UsesOf(edge).First(u => body.FaceOf(u) == f);
            return n.Cross(e0.Curve.Derivative(e0.T0) * (body[use].Forward ? 1 : -1)).Normalized();
        }
        var (np, nc, inP, inC) = (Flat(nP), Flat(nC), Flat(Into(fp, nP)), Flat(Into(fc, nC)));
        var convex = np.Dot(inC) < 0;

        // The ball's centre is `size` from both, inside the material for a convex edge, outside for a concave one.
        var side = convex ? -1 : 1;
        var corner = new Point2D(cylinder.Radius, 0);
        var center = corner + (np + nc) * (side * size);
        var (ta, tb) = (center - np * (side * size), center - nc * (side * size));
        var start = Math.Atan2(ta.Y - center.Y, ta.X - center.X);
        var sweep = Math.IEEERemainder(Math.Atan2(tb.Y - center.Y, tb.X - center.X) - start, 2 * Math.PI);
        var profile = new ProfileLoop([new LineSegment(corner, ta), new ArcSegment(center, size, start, sweep), new LineSegment(tb, corner)]);
        var (ring, why) = Revolution.Revolve(new Region(profile), frame, body.Tolerance);
        if (ring is null) return (null, default, $"the fillet has no room: {why}");
        var face = new FaceId(Enumerable.Range(0, ring.Body.Faces.Length).First(f => ring.Origins[f].Contains(new Origin(0, new FaceId(1)))));
        return Settle(body, ring.Body, face, convex, [a, b], kind);
    }

    // The blend applied: the tool taken away from a convex edge or added to a concave one, refused if any face but the
    // allowed ones comes out changed.
    static (Shaped?, FaceId, string?) Settle(Body body, Body tool, FaceId face, bool convex, HashSet<FaceId> allowed, BlendKind kind)
    {
        var (result, problem) = convex ? Booleans.Subtract(body, tool) : Booleans.Unite(body, tool);
        if (problem is not null) return (null, default, problem);
        if (result is not [var one]) return (null, default, $"the {Name(kind)} parts the body");
        for (var f = 0; f < body.Faces.Length; f++)
        {
            var id = new FaceId(f);
            if (allowed.Contains(id)) continue;
            var after = Enumerable.Range(0, one.Body.Faces.Length).Where(g => one.Origins[g].Contains(new Origin(0, id))).ToList();
            var before = MassProperties.Area(body, id);
            if (after.Any(g => one.Origins[g].Count > 1) || Math.Abs(after.Sum(g => MassProperties.Area(one.Body, new FaceId(g))) - before) > 1e-9 * (1 + before))
                return (null, default, $"the {Name(kind)} reaches other faces than the two it runs between and those at its ends (a size too large, or an edge that ends inside the material)");
        }
        return (one, face, null);
    }

    static string Name(BlendKind kind) => kind == BlendKind.Fillet ? "fillet" : "chamfer";

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
