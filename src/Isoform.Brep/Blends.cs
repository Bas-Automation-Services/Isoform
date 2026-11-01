using BasAutomation.Geometry;

namespace Isoform.Brep;

public enum BlendKind { Fillet, Chamfer }

/// <summary>
/// Fillets and chamfers. On the straight edge between two planar faces a convex edge loses the prism between the
/// edge and the ball (or the chamfer's plane) rolled along it; a concave edge gains it. The prism runs past the edge's
/// ends, so the faces the edge ends on cut the new face off (an arc or an ellipse on each). On the circle where a flat
/// face meets a cylinder square to it (a hole's rim, a boss's foot) the ball rolls round the axis: the ring it leaves
/// out (or fills) is the spandrel turned round the axis, its new face a torus (a cone for a chamfer). This is the
/// rounded edge only where the
/// prism or ring touches nothing but the two faces and the faces at the edge's ends: any other face it changes (a
/// radius too large for a face, an edge that ends inside the material) refuses the blend.
/// </summary>
public static class Blends
{
    /// <summary>
    /// The body with the edge between faces <paramref name="a"/> and <paramref name="b"/> rounded to radius
    /// <paramref name="size"/> or chamfered back by it on both faces. Origins: operand 0 the body's faces, operand 1
    /// the blend's; <c>Face</c> is the blend's own face among operand 1's (the arc or the chamfer's slope). Where a
    /// fillet's edge ends between two earlier fillets of its radius at a square corner, a ball rounds the corner:
    /// <c>Corners</c> gives the two fillets' faces of each, and operand 2 + k holds the k-th ball's face.
    /// </summary>
    public static (Shaped? Result, FaceId Face, IReadOnlyList<FaceId[]> Corners, string? Problem) Apply(Body body, FaceId a, FaceId b, double size, BlendKind kind)
    {
        if ((body[a].Surface, body[b].Surface) is (Plane pa, Plane pb)) return Straight(body, a, b, pa, pb, size, kind);
        var (result, face, problem) = (body[a].Surface, body[b].Surface) switch
        {
            (Plane, Cylinder) or (Cylinder, Plane) => Rim(body, a, b, size, kind),
            _ => (null, default, "only an edge between two flat faces, or a flat face and a cylinder, can be rounded or chamfered yet"),
        };
        return (result, face, [], problem);
    }

    static (Shaped?, FaceId, IReadOnlyList<FaceId[]>, string?) Straight(Body body, FaceId a, FaceId b, Plane pa, Plane pb, double size, BlendKind kind)
    {
        var edges = Enumerable.Range(0, body.Edges.Length).Select(e => new EdgeId(e))
            .Where(e => body.UsesOf(e).Select(body.FaceOf).ToHashSet().SetEquals([a, b])).ToList();
        if (edges.Count == 0) return (null, default, [], "the two faces share no edge");
        if (edges.Any(e => body[e].Curve is not Line3)) return (null, default, [], "the edge between the faces is not straight");
        var line = (Line3)body[edges[0]].Curve;
        var d = line.Direction;
        if (edges.Any(e => !Collinear(line, (Line3)body[e].Curve))) return (null, default, [], "the faces meet in edges that are not on one line");

        var nA = pa.Frame.ZAxis * (body[a].SameSense ? 1 : -1);
        var nB = pb.Frame.ZAxis * (body[b].SameSense ? 1 : -1);
        var c = nA.Dot(nB);
        if (Math.Abs(c) >= 1 - 1e-9) return (null, default, [], "the two faces are parallel along the edge");
        // Into each face, square to the edge: to the left of the edge as the face's loop runs it, seen from outside.
        var (inA, inB) = (Into(body, edges[0], a, nA), Into(body, edges[0], b, nB));
        var convex = nA.Dot(inB) < 0;
        if (!convex && kind == BlendKind.Chamfer) return (null, default, [], "only a convex edge can be chamfered");

        // The edge's extent along its line, and a point on it before its start. A convex edge's prism runs on past its
        // ends, where it cuts nothing; a concave edge's fills, so it stops on the faces the edge ends on.
        var ts = edges.SelectMany(e => new[] { body[e].T0, body[e].T1 }.Select(t => (body[e].Curve.At(t) - line.Origin).Dot(d))).ToList();
        var margin = convex ? 2 * size + 1 : 0;
        // Where the edge ends against two earlier fillets, three meet at a corner: Isoform rounds that with a ball, its
        // centre r inside all three faces. The kernel makes it where the fillets are of one radius and the corner square:
        // each fillet's axis then runs through the centre, square to the other two.
        var corners = new List<(Point3D Centre, Vector3D Out, FaceId[] Fillets)>();
        foreach (var t in new[] { ts.Min(), ts.Max() })
        {
            var tip = line.Origin + d * t;
            var curved = body.Coedges.Select((_, i) => new CoedgeId(i))
                .Where(co => (body[body.StartOf(co)].Point - tip).Length <= Intersections.Snap)
                .Select(body.FaceOf).Distinct().Where(f => f != a && f != b && body[f].Surface is not Plane).ToArray();
            if (curved.Length < 2) continue;
            var centre = tip - (nA + nB) * (size / (1 + c));
            bool Fits(FaceId f) => body[f].Surface is Cylinder k && Math.Abs(k.Radius - size) <= 1e-9 && Math.Abs(k.Frame.ZAxis.Dot(d)) <= 1e-9
                                   && Math.Abs(k.Frame.ZAxis.Dot(nA)) + Math.Abs(k.Frame.ZAxis.Dot(nB)) >= 1 - 1e-9 && Off(k, centre) <= Intersections.Snap;
            if (curved.Length > 2 || kind != BlendKind.Fillet || !convex || Math.Abs(c) > 1e-9 || !curved.All(Fits))
                return (null, default, [], "three blends meeting at a corner need a ball there, which the managed kernel makes only for three fillets of one radius at a square corner");
            corners.Add((centre, d * (t == ts.Max() ? 1 : -1), curved));
        }
        if (!convex)
        {
            var tips = new[] { ts.Min(), ts.Max() }.Select(t => line.Origin + d * t).ToList();
            var square = body.Coedges.Select((_, i) => new CoedgeId(i))
                .Where(co => tips.Any(e => (body[body.StartOf(co)].Point - e).Length <= Intersections.Snap))
                .Select(body.FaceOf).Where(f => f != a && f != b)
                .All(f => body[f].Surface is Plane end && end.Frame.ZAxis.Cross(d).Length < 1e-9);
            if (!square) return (null, default, [], "a concave edge can only be rounded where it ends on faces square to it");
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
        if (tool is null) return (null, default, [], $"the {Name(kind)} has no room: {why}");

        var ends = new[] { ts.Min(), ts.Max() }.Select(t => line.Origin + d * t).ToList();
        var allowed = new HashSet<FaceId> { a, b };
        foreach (var co in body.Coedges.Select((_, i) => new CoedgeId(i)))
            if (ends.Any(e => (body[body.StartOf(co)].Point - e).Length <= Intersections.Snap)) allowed.Add(body.FaceOf(co));
        // The profile's second segment is the arc (or the slope): its side face is the new face.
        var (settled, face, problem) = Settle(body, tool.Body, tool.Sides[0][1], convex, allowed, kind);
        if (problem is not null) return (null, default, [], problem);

        // The three fillets run on past the corner and cross there in a mitre, which holds the ball: what lies beyond
        // the ball's centre outside it goes.
        var result = settled!;
        foreach (var (centre, ahead, _) in corners)
        {
            var (cap, failed) = Cap(centre, nA, nB, ahead, size, body.Tolerance);
            if (cap is null) return (null, default, [], $"the ball at the corner cannot be made: {failed}");
            var (cut, why2) = Booleans.Subtract(result.Body, cap.Body);
            if (why2 is not null) return (null, default, [], $"the ball at the corner cannot be cut: {why2}");
            if (cut is not [var one]) return (null, default, [], "the ball at the corner parts the body");
            // Faces of the body so far keep their origins; the ball's (operand 1 of the cap) becomes its own operand.
            var operand = 2 + corners.FindIndex(k => k.Centre == centre);
            result = new Shaped(one.Body, [.. one.Origins.Select(os => (IReadOnlyList<Origin>)[.. os.SelectMany(o => o.Operand == 0
                ? result.Origins[o.Face.Index]
                : cap.Origins[o.Face.Index].Where(x => x.Operand == 1).Select(x => new Origin(operand, x.Face)))])]);
        }
        return (result, face, [.. corners.Select(k => k.Fillets)], null);
    }

    // How far a point lies from a cylinder's axis.
    static double Off(Cylinder k, Point3D p)
    {
        var w = p - k.Frame.Origin;
        return (w - k.Frame.ZAxis * w.Dot(k.Frame.ZAxis)).Length;
    }

    /// <summary>
    /// The corner a ball of radius r round <paramref name="centre"/> takes off a square corner whose faces look along
    /// x, y and z (unit, square to each other): the cube from the centre 3r/2 along each, less the ball. The ball's
    /// axis is square to the corner's diagonal and in none of the cube's sides, so neither pole nor its seam (opposite
    /// the diagonal) lies on the corner's piece of it. Origins: operand 0 the cube's faces, 1 the ball's.
    /// </summary>
    static (Shaped?, string?) Cap(Point3D centre, Vector3D x, Vector3D y, Vector3D z, double r, double tolerance)
    {
        var side = 1.5 * r;
        var frame = RigidTransform3D.FromFrame(centre, z, x);
        var turn = frame.YAxis.Dot(y) > 0 ? 1 : -1;
        Point2D Q(double u, double v) => new(u, v * turn);
        var square = new ProfileLoop([new LineSegment(Q(0, 0), Q(side, 0)), new LineSegment(Q(side, 0), Q(side, side)),
            new LineSegment(Q(side, side), Q(0, side)), new LineSegment(Q(0, side), Q(0, 0))]);
        var (cube, why) = Extrusion.Extrude(new Region(turn > 0 ? square : square.Reversed()), frame, 0, side, tolerance);
        if (cube is null) return (null, why);
        var diagonal = (x + y + z).Normalized();
        var half = new ProfileLoop([new ArcSegment(new Point2D(0, 0), r, -Math.PI / 2, Math.PI), new LineSegment(new Point2D(0, r), new Point2D(0, -r))]);
        var (ball, whyBall) = Revolution.Revolve(new Region(half), RigidTransform3D.FromFrame(centre, (x - y * 2 + z).Normalized(), -diagonal), tolerance);
        if (ball is null) return (null, whyBall);
        var (left, failed) = Booleans.Subtract(cube.Body, ball.Body);
        if (failed is not null) return (null, failed);
        return left is [var one] ? (one, null) : (null, "the cube less the ball is not one piece");
    }

    // The circle where a flat face meets a cylinder square to it: the spandrel (or the chamfer's triangle) in the
    // half-plane through the axis (distance from the axis, height above the face), turned round the axis.
    static (Shaped?, FaceId, string?) Rim(Body body, FaceId a, FaceId b, double size, BlendKind kind)
    {
        var (fp, fc) = body[a].Surface is Plane ? (a, b) : (b, a);
        var (plane, cylinder) = ((Plane)body[fp].Surface, (Cylinder)body[fc].Surface);
        var axis = cylinder.Frame.ZAxis;
        if (plane.Frame.ZAxis.Cross(axis).Length >= 1e-9)
            return (null, default, "the flat face is at an angle to the cylinder, so the edge is an ellipse, which has no exact fillet");
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
        if (!convex && kind == BlendKind.Chamfer) return (null, default, "only a convex edge can be chamfered");

        var corner = new Point2D(cylinder.Radius, 0);
        ProfileLoop profile;
        if (kind == BlendKind.Chamfer)
            profile = new ProfileLoop([new LineSegment(corner, corner + inP * size), new LineSegment(corner + inP * size, corner + inC * size), new LineSegment(corner + inC * size, corner)]);
        else
        {
            // The ball's centre is `size` from both, inside the material for a convex edge, outside for a concave one.
            var side = convex ? -1 : 1;
            var center = corner + (np + nc) * (side * size);
            var (ta, tb) = (center - np * (side * size), center - nc * (side * size));
            var start = Math.Atan2(ta.Y - center.Y, ta.X - center.X);
            var sweep = Math.IEEERemainder(Math.Atan2(tb.Y - center.Y, tb.X - center.X) - start, 2 * Math.PI);
            profile = new ProfileLoop([new LineSegment(corner, ta), new ArcSegment(center, size, start, sweep), new LineSegment(tb, corner)]);
        }
        var (ring, why) = Revolution.Revolve(new Region(profile), frame, body.Tolerance);
        if (ring is null) return (null, default, $"the {Name(kind)} has no room: {why}");
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

    /// <summary>
    /// Every edge round a flat face <paramref name="f"/> rounded (or chamfered): one piece per edge, each the spandrel
    /// along it, a prism along a line, the spandrel turned round the axis along an arc. A piece stops exactly where
    /// its edge runs on tangentially into the next and runs on past a convex corner, so straight pieces mitre there. A
    /// chamfer's piece along an arc is a cone.
    /// Reflex corners, sides not square to the face, and arcs meeting their neighbours at a corner are refused.
    /// Origins: operand 0 the body's faces, k + 1 the k-th piece's; each piece's own face and the side it runs along.
    /// </summary>
    public static (Shaped? Result, IReadOnlyList<(FaceId Blend, FaceId Side)>? Pieces, string? Problem) Loop(Body body, FaceId f, double size, BlendKind kind)
    {
        if (body[f].Surface is not Plane pf) return (null, null, "only the loops of a flat face can be rounded yet");
        var n = pf.Frame.ZAxis * (body[f].SameSense ? 1 : -1);
        var tools = new List<(Body Tool, FaceId Blend, FaceId Side, bool Convex)>();
        var sides = new HashSet<FaceId>();
        foreach (var loop in body[f].Loops)
        {
            var cs = body.CoedgesOf(loop).ToList();
            for (var i = 0; i < cs.Count; i++)
            {
                var (piece, why) = Piece(body, f, n, cs, i, size, kind);
                if (piece is not { } p) return (null, null, why);
                tools.Add(p);
                sides.Add(p.Side);
            }
        }

        // The pieces applied, every face's origins carried back to the body's faces or a piece's. Pieces of one kind
        // that do not touch go in one boolean; a loop of both kinds goes piece by piece, in order.
        var current = body;
        var origins = Enumerable.Range(0, body.Faces.Length).Select(g => (IReadOnlyList<Origin>)[new Origin(0, new FaceId(g))]).ToList();
        var groups = tools.All(t => t.Convex == tools[0].Convex)
            ? Booleans.Apart([.. tools.Select(t => t.Tool)])
            : [.. Enumerable.Range(0, tools.Count).Select(k => new List<int> { k })];
        foreach (var group in groups)
        {
            var (tool, offsets) = Booleans.Lumps([.. group.Select(k => tools[k].Tool)]);
            var (result, problem) = tools[group[0]].Convex ? Booleans.Subtract(current, tool) : Booleans.Unite(current, tool);
            if (problem is not null) return (null, null, $"{problem} (pieces {string.Join(", ", group.Select(k => k + 1))} of {tools.Count})");
            if (result is not [var one]) return (null, null, $"the {Name(kind)} parts the body");
            // A face of the lumps back to its piece (operand k + 1) and the piece's own face.
            Origin Piece(Origin o)
            {
                var i = Array.FindLastIndex(offsets, at => at <= o.Face.Index);
                return new Origin(group[i] + 1, new FaceId(o.Face.Index - offsets[i]));
            }
            origins = [.. one.Origins.Select(os => (IReadOnlyList<Origin>)[.. os.SelectMany(o => o.Operand == 0 ? origins[o.Face.Index] : [Piece(o)]).Distinct()])];
            current = one.Body;
        }

        // Faces other than the face and its sides must come through unchanged.
        for (var g = 0; g < body.Faces.Length; g++)
        {
            var id = new FaceId(g);
            if (id == f || sides.Contains(id)) continue;
            var after = Enumerable.Range(0, current.Faces.Length).Where(h => origins[h].Contains(new Origin(0, id))).ToList();
            var before = MassProperties.Area(body, id);
            if (after.Any(h => origins[h].Count > 1) || Math.Abs(after.Sum(h => MassProperties.Area(current, new FaceId(h))) - before) > 1e-9 * (1 + before))
                return (null, null, $"the {Name(kind)} round the face reaches other faces than the face and its sides (a size too large)");
        }
        return (new Shaped(current, origins), [.. tools.Select(t => (t.Blend, t.Side))], null);
    }

    enum Corner { Tangent, Convex, Reflex }

    // The piece along the i-th coedge of a loop of f (face normal n): its tool, own face, side and which way it goes.
    static ((Body Tool, FaceId Blend, FaceId Side, bool Convex)?, string?) Piece(Body body, FaceId f, Vector3D n, List<CoedgeId> cs, int i, double size, BlendKind kind)
    {
        var c = cs[i];
        var edge = body[body[c].Edge];
        var side = body.UsesOf(body[c].Edge).Select(body.FaceOf).First(g => g != f);
        var (from, to) = body.ParametersOf(c);
        Vector3D Travel(CoedgeId co, bool atEnd)
        {
            var (a, b) = body.ParametersOf(co);
            return (body[body[co].Edge].Curve.Derivative(atEnd ? b : a) * Math.Sign(b - a)).Normalized();
        }
        Corner Turn(Vector3D into, Vector3D outOf)
        {
            var cross = into.Cross(outOf);
            if (cross.Length < 1e-9 && into.Dot(outOf) > 0) return Corner.Tangent;
            return cross.Dot(n) > 0 ? Corner.Convex : Corner.Reflex;
        }
        var single = cs.Count == 1;
        var start = single ? Corner.Tangent : Turn(Travel(cs[(i + cs.Count - 1) % cs.Count], true), Travel(c, false));
        var end = single ? Corner.Tangent : Turn(Travel(c, true), Travel(cs[(i + 1) % cs.Count], false));
        if (start == Corner.Reflex || end == Corner.Reflex) return (null, "a reflex corner of the face, round which the ball would need a torus no edge names");

        // Square sides only: a plane square to the face along a line, the cylinder round the face's normal along an arc.
        var p0 = edge.Curve.At(from);
        var t0 = Travel(c, false);
        Vector3D sideNormal;
        switch (edge.Curve, body[side].Surface)
        {
            case (Line3, Plane ps) when Math.Abs(ps.Frame.ZAxis.Dot(n)) < 1e-9:
                sideNormal = ps.Frame.ZAxis * (body[side].SameSense ? 1 : -1);
                break;
            case (Circle3 k, Cylinder cy) when k.Frame.ZAxis.Cross(n).Length < 1e-9 && cy.Frame.ZAxis.Cross(n).Length < 1e-9 && Math.Abs(cy.Radius - k.Radius) <= Intersections.Snap:
                sideNormal = (p0 - k.Frame.Origin - n * (p0 - k.Frame.Origin).Dot(n)).Normalized() * (body[side].SameSense ? 1 : -1);
                break;
            default:
                return (null, "a side of the face that is not square to it, or not a plane or cylinder");
        }
        var inF = n.Cross(t0).Normalized();
        var inSide = sideNormal.Cross(-t0).Normalized();
        var convex = n.Dot(inSide) < 0;
        if (!convex && kind == BlendKind.Chamfer) return (null, "only a convex edge can be chamfered");

        // The section square to the edge: w into the face, h along its normal.
        var h = convex ? -size : size;
        var corner = new Point2D(0, 0);
        var (ta, tb) = (new Point2D(size, 0), new Point2D(0, h));
        Segment middle = kind == BlendKind.Chamfer ? new LineSegment(ta, tb)
            : new ArcSegment(new Point2D(size, h), size, convex ? Math.PI / 2 : -Math.PI / 2, convex ? Math.PI / 2 : -Math.PI / 2);
        var section = new ProfileLoop([new LineSegment(corner, ta), middle, new LineSegment(tb, corner)]);

        if (edge.Curve is Line3)
        {
            // Run on past a convex corner (to mitre with the next piece), stop exactly where the next runs on
            // tangentially; a concave edge's piece fills, so it stops at the sides it ends on.
            var margin = 2 * size + 1;
            var (m0, m1) = (convex && start == Corner.Convex ? margin : 0, convex && end == Corner.Convex ? margin : 0);
            var length = (edge.Curve.At(to) - p0).Length;
            var frame = RigidTransform3D.FromFrame(p0 - t0 * m0, t0, inF);
            var (tool, why) = Extrusion.Extrude(new Region(section), frame, 0, length + m0 + m1, body.Tolerance);
            if (tool is null) return (null, $"the {Name(kind)} has no room: {why}");
            return ((tool.Body, tool.Sides[0][1], side, convex), null);
        }

        // An arc: the section turned round the arc's axis, from end to end (whole for a loop of one circle).
        if (!single && (start != Corner.Tangent || end != Corner.Tangent)) return (null, "an arc meeting the next edge at a corner, where its torus would have to mitre");
        var circle = (Circle3)edge.Curve;
        var center = circle.Frame.Origin;
        Vector3D Radial(Point3D p) => (p - center - n * (p - center).Dot(n)).Normalized();
        var sigma = inF.Dot(Radial(p0)) > 0 ? 1 : -1;
        var ring = new ProfileLoop([.. section.Segments.Select(s => Placed(s, circle.Radius, sigma))]);
        var ccw = Radial(p0).Cross(t0).Dot(n) > 0;
        var p1 = edge.Curve.At(to);
        var (first, last) = ccw ? (p0, p1) : (p1, p0);
        var angle = single ? 2 * Math.PI : Intersections.Wrap(Math.Atan2(Radial(first).Cross(Radial(last)).Dot(n), Radial(first).Dot(Radial(last))));
        if (angle <= 0) angle = 2 * Math.PI;
        var axis = RigidTransform3D.FromFrame(center, n, Radial(first));
        var (turned, problem) = Revolution.Revolve(new Region(ring), axis, body.Tolerance, angle);
        if (turned is null) return (null, $"the {Name(kind)} has no room: {problem}");
        var blend = new FaceId(Enumerable.Range(0, turned.Body.Faces.Length).First(g => turned.Origins[g].Contains(new Origin(0, new FaceId(1)))));
        return ((turned.Body, blend, side, convex), null);
    }

    // A section segment (w into the face, h along its normal) in the half-plane round an arc's axis: distance from
    // the axis R + sigma * w (sigma = +1 where the face lies outside the arc), height h.
    static Segment Placed(Segment s, double radius, int sigma) => s switch
    {
        LineSegment l => new LineSegment(new Point2D(radius + sigma * l.From.X, l.From.Y), new Point2D(radius + sigma * l.To.X, l.To.Y)),
        ArcSegment a => sigma > 0
            ? new ArcSegment(new Point2D(radius + a.Center.X, a.Center.Y), a.Radius, a.StartAngle, a.Sweep)
            : new ArcSegment(new Point2D(radius - a.Center.X, a.Center.Y), a.Radius, Math.PI - a.StartAngle, -a.Sweep),
        _ => throw new NotSupportedException(s.GetType().Name),
    };

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
