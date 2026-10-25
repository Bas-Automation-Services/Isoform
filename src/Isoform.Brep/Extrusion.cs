using BasAutomation.Geometry;

namespace Isoform.Brep;

/// <summary>
/// The faces of an extruded body by where they came from: the two caps, and a side face per profile segment
/// (<see cref="Sides"/>[loop][segment], loop 0 the outer loop, then the holes, in the region's own order).
/// </summary>
public sealed record ExtrudeResult(Body Body, FaceId StartCap, FaceId EndCap, IReadOnlyList<IReadOnlyList<FaceId>> Sides);

public static class Extrusion
{
    /// <summary>
    /// The solid swept by <paramref name="region"/> (in the frame's XY plane) along the frame's Z from
    /// <paramref name="start"/> to <paramref name="end"/>. A line becomes a planar side face, an arc a cylindrical one;
    /// a full circle a cylinder with one seam edge. Loops are taken counter-clockwise (outer) and clockwise (holes)
    /// whichever way they were given. Null with the reason when the region or the range cannot make a solid.
    /// </summary>
    public static (ExtrudeResult? Result, string? Problem) Extrude(Region region, RigidTransform3D frame, double start, double end,
        double tolerance = Body.DefaultTolerance)
    {
        if (!(end - start > tolerance)) return (null, $"the extrusion runs from {start} to {end}, which is not above the tolerance");
        if (region.Problem(tolerance) is { } problem) return (null, problem);

        var builder = new BodyBuilder(tolerance);
        var shell = builder.AddShell();
        var height = end - start;
        var up = frame.ZAxis;
        Point3D At(Point2D p, double z) => frame.Apply(new Point3D(p.X, p.Y, z));

        var loops = region.Loops.ToList();
        var sides = new List<IReadOnlyList<FaceId>>();
        var bottoms = new List<List<BodyBuilder.Use>>(); // cap uses per loop, along the loop
        var tops = new List<List<BodyBuilder.Use>>();
        for (var l = 0; l < loops.Count; l++)
        {
            // Outer counter-clockwise, holes clockwise: material on the left of every segment.
            var wantCounterClockwise = l == 0;
            var reversed = loops[l].SignedArea > 0 != wantCounterClockwise;
            var segments = reversed ? loops[l].Reversed().Segments : loops[l].Segments;
            var n = segments.Count;

            var bottomVertices = segments.Select(s => builder.AddVertex(At(s.Start, start))).ToList();
            var topVertices = segments.Select(s => builder.AddVertex(At(s.Start, end))).ToList();
            var verticals = Enumerable.Range(0, n).Select(i =>
                builder.AddEdge(new Line3(At(segments[i].Start, start), up), 0, height, bottomVertices[i], topVertices[i])).ToList();

            var faces = new FaceId[n];
            var bottom = new List<BodyBuilder.Use>();
            var top = new List<BodyBuilder.Use>();
            for (var i = 0; i < n; i++)
            {
                var (s, next) = (segments[i], (i + 1) % n);
                var (curveBottom, curveTop, t1, side, sameSense, uvBottom, uvTop, uvStart, uvEnd, capPcurve) = s switch
                {
                    LineSegment line => Line(line),
                    ArcSegment arc => Arc(arc),
                    _ => throw new NotSupportedException(s.GetType().Name),
                };
                var edgeBottom = builder.AddEdge(curveBottom, 0, t1, bottomVertices[i], bottomVertices[next]);
                var edgeTop = builder.AddEdge(curveTop, 0, t1, topVertices[i], topVertices[next]);

                var face = builder.AddFace(shell, side, sameSense);
                // Along the bottom, up the far end, back along the top, down the near end: counter-clockwise from outside.
                builder.AddLoop(face,
                [
                    new(edgeBottom, true, uvBottom),
                    new(verticals[next], true, uvEnd),
                    new(edgeTop, false, uvTop),
                    new(verticals[i], false, uvStart),
                ]);
                faces[reversed ? n - 1 - i : i] = face;
                bottom.Add(new(edgeBottom, false, capPcurve));
                top.Add(new(edgeTop, true, capPcurve));
            }
            sides.Add(faces);
            bottoms.Add(bottom);
            tops.Add(top);
        }

        // The caps share the profile's (x, y) as (u, v). The start cap faces −Z, so it runs its loops backwards.
        var startCap = builder.AddFace(shell, new Plane(Translated(frame, start)), sameSense: false);
        var endCap = builder.AddFace(shell, new Plane(Translated(frame, end)), sameSense: true);
        foreach (var uses in bottoms) builder.AddLoop(startCap, [.. Enumerable.Reverse(uses)]);
        foreach (var uses in tops) builder.AddLoop(endCap, uses);

        return (new ExtrudeResult(builder.Build(), startCap, endCap, sides), null);

        // A line from A to B: a plane through A spanned by the line and Z, (u, v) = (distance along the line, height),
        // normal along line × Z, to the right of the line: out of the material.
        (Curve3, Curve3, double, Surface, bool, Curve2, Curve2, Curve2, Curve2, Curve2) Line(LineSegment line)
        {
            var along = frame.Apply(new Vector3D(line.To.X - line.From.X, line.To.Y - line.From.Y, 0)) / line.Length;
            var side = new Plane(RigidTransform3D.FromFrame(At(line.From, 0), along.Cross(up), along));
            var direction2 = (line.To - line.From) / line.Length;
            return (new Line3(At(line.From, start), along), new Line3(At(line.From, end), along), line.Length, side, true,
                new Line2(new Point2D(0, start), new Vector2D(1, 0)), new Line2(new Point2D(0, end), new Vector2D(1, 0)),
                new Line2(new Point2D(0, start), new Vector2D(0, 1)), new Line2(new Point2D(line.Length, start), new Vector2D(0, 1)),
                new Line2(line.From, direction2));
        }

        // An arc round C: a cylinder on the frame's axes through C, (u, v) = (angle, height). The edge circles run with
        // the arc, so their frames' Z is ±Z. Counter-clockwise arcs are convex, their cylinder's normal points out;
        // clockwise ones (holes, concave corners) use the cylinder against its sense.
        (Curve3, Curve3, double, Surface, bool, Curve2, Curve2, Curve2, Curve2, Curve2) Arc(ArcSegment arc)
        {
            var sign = Math.Sign(arc.Sweep);
            var radial = frame.Apply(new Vector3D(Math.Cos(arc.StartAngle), Math.Sin(arc.StartAngle), 0));
            Circle3 CircleAt(double z) => new(RigidTransform3D.FromFrame(At(arc.Center, z), up * sign, radial), arc.Radius);
            var side = new Cylinder(RigidTransform3D.FromFrame(At(arc.Center, 0), up, frame.XAxis), arc.Radius);
            var (u0, u1) = (arc.StartAngle, arc.StartAngle + arc.Sweep);
            return (CircleAt(start), CircleAt(end), Math.Abs(arc.Sweep), side, sign > 0,
                new Line2(new Point2D(u0, start), new Vector2D(sign, 0)), new Line2(new Point2D(u0, end), new Vector2D(sign, 0)),
                new Line2(new Point2D(u0, start), new Vector2D(0, 1)), new Line2(new Point2D(u1, start), new Vector2D(0, 1)),
                new Circle2(arc.Center, arc.Radius, arc.StartAngle, sign));
        }
    }

    static RigidTransform3D Translated(RigidTransform3D frame, double z) =>
        RigidTransform3D.FromFrame(frame.Apply(new Point3D(0, 0, z)), frame.ZAxis, frame.XAxis);
}
