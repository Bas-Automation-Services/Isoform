using BasAutomation.Geometry;

namespace Isoform.Brep;

/// <summary>
/// Solids of revolution: a region in the frame's (x, z) half-plane (x the distance from the axis, the frame's Z)
/// turned once round the axis. A line across the axis sweeps a flat ring, one along it a cylinder, an arc a torus; each
/// curved face has its seam where the region lies (u = 0). Regions that touch the axis or have lines at an angle to it
/// (cones) are refused.
/// </summary>
public static class Revolution
{
    /// <summary>The solid, with each face's origin (operand 0) the index of the profile segment that swept it, outer loop first.</summary>
    public static (Shaped? Result, string? Problem) Revolve(Region region, RigidTransform3D frame, double tolerance = Body.DefaultTolerance)
    {
        if (region.Problem(tolerance) is { } problem) return (null, problem);
        var patches = new List<Patch>();
        var index = 0;
        var loops = region.Loops.ToList();
        for (var l = 0; l < loops.Count; l++)
        {
            // Outer counter-clockwise, holes clockwise: material on the left of every segment.
            var reversed = loops[l].SignedArea > 0 != (l == 0);
            foreach (var s in reversed ? loops[l].Reversed().Segments : loops[l].Segments)
            {
                var (patch, why) = Sweep(s, frame, index++);
                if (patch is null) return (null, why);
                patches.Add(patch);
            }
        }
        try
        {
            return Sewing.Sew(patches, tolerance) is [var one] ? (one, null) : (null, "the region turns into more than one solid");
        }
        catch (BooleanFailure failure)
        {
            return (null, failure.Message);
        }
    }

    static (Patch?, string?) Sweep(Segment segment, RigidTransform3D frame, int index)
    {
        var (a, b) = (segment.Start, segment.End);
        if (Math.Min(a.X, b.X) <= Intersections.Snap || segment is ArcSegment arc0 && arc0.Center.X - arc0.Radius <= Intersections.Snap)
            return (null, "the region touches or crosses the axis it turns round");
        var z = frame.ZAxis;
        RigidTransform3D At(double height) => RigidTransform3D.FromFrame(frame.Apply(new Point3D(0, 0, height)), z, frame.XAxis);
        Point3D Meridian(Point2D p) => frame.Apply(new Point3D(p.X, 0, p.Y));
        Circle3 Ring(Point2D p) => new(At(p.Y), p.X);
        var origin = new HashSet<Origin> { new(0, new FaceId(index)) };

        switch (segment)
        {
            case LineSegment line when Math.Abs(a.Y - b.Y) <= Intersections.Snap:
            {
                // Across the axis: a flat ring, outer circle and inner. Material above when the line runs outwards.
                var plane = new Plane(At(a.Y));
                var outwards = b.X > a.X;
                var (outer, inner) = outwards ? (Ring(b), Ring(a)) : (Ring(a), Ring(b));
                IReadOnlyList<Use> Loop(Circle3 c, bool ccw) => [ccw ? new Use(c, 0, 2 * Math.PI, Uv.Pcurve(plane, c)) : new Use(c, 2 * Math.PI, 0, Uv.Pcurve(plane, c))];
                List<IReadOnlyList<Use>> uv = [Loop(outer, true), Loop(inner, false)];
                return (new Patch(plane, !outwards, [.. uv.Select(u => Arrangement.Sensed(u, !outwards))], origin), null);
            }
            case LineSegment line when Math.Abs(a.X - b.X) <= Intersections.Snap:
            {
                // Along the axis: a cylinder, material inside when the line runs up.
                var cylinder = new Cylinder(At(0), a.X);
                var length = Math.Abs(b.Y - a.Y);
                var seam = new Line3(Meridian(a), z * Math.Sign(b.Y - a.Y));
                return (Band(cylinder, Ring(a), Ring(b), seam, 0, length, b.Y > a.Y, origin), null);
            }
            case ArcSegment arc:
            {
                // An arc: a torus round the axis through its centre, material in the tube when it runs counter-clockwise.
                var torus = new Torus(At(arc.Center.Y), arc.Center.X, arc.Radius);
                var meridian = new Circle3(RigidTransform3D.FromFrame(Meridian(arc.Center), -frame.YAxis, frame.XAxis), arc.Radius);
                var (from, to) = (arc.StartAngle, arc.StartAngle + arc.Sweep);
                return (Band(torus, Ring(a), Ring(b), meridian, from, to, arc.Sweep > 0, origin), null);
            }
            default:
                return (null, "a line at an angle to the axis turns into a cone, which the managed kernel does not have yet");
        }
    }

    // A curved face turned from one segment: the circle at its start, the seam along it at u = 2π, the circle at its end
    // back, the seam back at u = 0; counter-clockwise in (u, v) when the seam runs up v, so with the face's sense.
    static Patch Band(Surface surface, Circle3 start, Circle3 end, Curve3 seam, double t0, double t1, bool sameSense, HashSet<Origin> origin)
    {
        var turn = 2 * Math.PI;
        var pcSeam = Uv.Pcurve(surface, seam);
        // The circles' v (round a torus's tube) taken to where the seam has it at each end.
        Curve2 Along(Circle3 c, double t)
        {
            var pc = Uv.Pcurve(surface, c);
            return surface.PeriodV is null ? pc : Uv.Shift(pc, 0, turn * Math.Round((pcSeam.At(t).Y - pc.At(0).Y) / turn));
        }
        IReadOnlyList<Use> uv =
        [
            new Use(start, 0, turn, Along(start, t0)),
            new Use(seam, t0, t1, Uv.Shift(pcSeam, turn)),
            new Use(end, turn, 0, Along(end, t1)),
            new Use(seam, t1, t0, pcSeam),
        ];
        var up = pcSeam.At(t1).Y > pcSeam.At(t0).Y;
        if (!up) uv = Arrangement.Sensed(uv, false);
        return new Patch(surface, sameSense, [Arrangement.Sensed(uv, sameSense)], origin);
    }
}
