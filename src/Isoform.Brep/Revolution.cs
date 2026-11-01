using BasAutomation.Geometry;

namespace Isoform.Brep;

/// <summary>
/// Solids of revolution: a region in the frame's (x, z) half-plane (x the distance from the axis, the frame's Z)
/// turned round the axis, once or through an angle from the frame's X. A line across the axis sweeps a flat ring (or
/// sector), one along it a cylinder, one at an angle a cone, an arc a torus; a whole turn gives each curved face a seam
/// where the region lies (u = 0), a part turn closes the ends with the region itself. The region may have edges on the
/// axis (which sweep nothing) and lines across it that end on it (a disc, or a sector); a cone or torus reaching the
/// axis (which needs an apex) is refused, as is a region crossing it.
/// </summary>
public static class Revolution
{
    /// <summary>
    /// The solid, with each face's origin (operand 0) the index of the profile segment that swept it, outer loop first
    /// (a segment on the axis sweeps none), and for a part turn the caps after them (the start, then the end).
    /// </summary>
    public static (Shaped? Result, string? Problem) Revolve(Region region, RigidTransform3D frame, double tolerance = Body.DefaultTolerance, double angle = 2 * Math.PI)
    {
        if (region.Problem(tolerance) is { } problem) return (null, problem);
        var whole = angle >= 2 * Math.PI - 1e-12;
        if (!whole && !(angle > 0)) return (null, "the turn is not above zero");
        var patches = new List<Patch>();
        var index = 0;
        var loops = region.Loops.Select((loop, l) => loop.SignedArea > 0 != (l == 0) ? loop.Reversed() : loop).ToList();
        foreach (var s in loops.SelectMany(l => l.Segments))
        {
            var (patch, why) = Sweep(s, frame, whole ? 2 * Math.PI : angle, whole, index++);
            if (why is not null) return (null, why);
            if (patch is not null) patches.Add(patch);
        }
        if (!whole)
        {
            patches.Add(Cap(loops, frame, 0, start: true, index++));
            patches.Add(Cap(loops, frame, angle, start: false, index));
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

    // The frame turned by u round the axis.
    static RigidTransform3D Turned(RigidTransform3D frame, double u)
    {
        var (s, c) = Math.SinCos(u);
        return RigidTransform3D.FromFrame(frame.Origin, frame.ZAxis, frame.XAxis * c + frame.YAxis * s);
    }

    // A profile point in space at angle u, and a profile segment there as a curve along it.
    static Point3D Meridian(RigidTransform3D frame, double u, Point2D p) => Turned(frame, u).Apply(new Point3D(p.X, 0, p.Y));

    static Curve3 MeridianCurve(RigidTransform3D frame, double u, Segment s)
    {
        var at = Turned(frame, u);
        return s switch
        {
            LineSegment l => new Line3(Meridian(frame, u, l.From), (Meridian(frame, u, l.To) - Meridian(frame, u, l.From)).Normalized()),
            ArcSegment a => new Circle3(RigidTransform3D.FromFrame(Meridian(frame, u, a.Center), -at.YAxis, at.XAxis), a.Radius),
            _ => throw new NotSupportedException(s.GetType().Name),
        };
    }

    // A segment's parameters along its meridian curve: a line from 0 to its length, an arc by its angles.
    static (double, double) Span(Segment s) => s switch
    {
        LineSegment l => (0, l.Length),
        ArcSegment a => (a.StartAngle, a.StartAngle + a.Sweep),
        _ => throw new NotSupportedException(s.GetType().Name),
    };

    static (Patch?, string?) Sweep(Segment segment, RigidTransform3D frame, double angle, bool whole, int index)
    {
        var (a, b) = (segment.Start, segment.End);
        bool OnAxis(Point2D p) => Math.Abs(p.X) <= Intersections.Snap;
        if (segment is LineSegment && OnAxis(a) && OnAxis(b)) return (null, null);
        // The segment's least distance from the axis: an arc's ends, or its leftmost point where it passes it.
        var least = segment is ArcSegment bend && bend.Covers(Math.PI) ? bend.Center.X - bend.Radius : Math.Min(a.X, b.X);
        if (least < -Intersections.Snap) return (null, "the region crosses the axis it turns round");
        var across = segment is LineSegment && Math.Abs(a.Y - b.Y) <= Intersections.Snap;
        if (!across && least <= Intersections.Snap)
            return (null, "a cone or arc reaching the axis needs an apex there, which the managed kernel does not make yet");
        var z = frame.ZAxis;
        RigidTransform3D At(double height) => RigidTransform3D.FromFrame(frame.Apply(new Point3D(0, 0, height)), z, frame.XAxis);
        Circle3 Ring(Point2D p) => new(At(p.Y), p.X);
        var origin = new HashSet<Origin> { new(0, new FaceId(index)) };
        var (t0, t1) = Span(segment);
        var (seam0, seamEnd) = (MeridianCurve(frame, 0, segment), MeridianCurve(frame, angle, segment));

        switch (segment)
        {
            case LineSegment when across:
            {
                // Across the axis: a flat ring (or sector), a disc where it starts on the axis. Material above when the
                // line runs outwards.
                var plane = new Plane(At(a.Y));
                var outwards = b.X > a.X;
                var (outer, inner) = outwards ? (Ring(b), OnAxis(a) ? null : Ring(a)) : (Ring(a), OnAxis(b) ? null : Ring(b));
                List<IReadOnlyList<Use>> uv;
                if (whole)
                    uv = inner is null ? [[new Use(outer, 0, angle, Uv.Pcurve(plane, outer))]]
                        : [[new Use(outer, 0, angle, Uv.Pcurve(plane, outer))], [new Use(inner, angle, 0, Uv.Pcurve(plane, inner))]];
                else
                {
                    // Counter-clockwise round the sector: out along the outer arc, in along the end, back along the
                    // inner arc (none at the axis), out along the start. The meridian runs with the segment, outwards or
                    // inwards.
                    var (lo, hi) = outwards ? (t0, t1) : (t1, t0);
                    List<Use> loop = [new Use(outer, 0, angle, Uv.Pcurve(plane, outer)), new Use(seamEnd, hi, lo, Uv.Pcurve(plane, seamEnd))];
                    if (inner is not null) loop.Add(new Use(inner, angle, 0, Uv.Pcurve(plane, inner)));
                    loop.Add(new Use(seam0, lo, hi, Uv.Pcurve(plane, seam0)));
                    uv = [loop];
                }
                return (new Patch(plane, !outwards, [.. uv.Select(u => Arrangement.Sensed(u, !outwards))], origin), null);
            }
            case LineSegment when Math.Abs(a.X - b.X) <= Intersections.Snap:
                // Along the axis: a cylinder, material inside when the line runs up.
                return (Band(new Cylinder(At(0), a.X), Ring(a), Ring(b), seam0, seamEnd, t0, t1, angle, b.Y > a.Y, origin), null);
            case LineSegment:
                // At an angle: a cone, its frame at the line's start; material inside when the line runs up, as for a
                // cylinder.
                return (Band(new Cone(At(a.Y), a.X, (b.X - a.X) / (b.Y - a.Y)), Ring(a), Ring(b), seam0, seamEnd, t0, t1, angle, b.Y > a.Y, origin), null);
            case ArcSegment arc:
                // An arc: a torus round the axis through its centre, material in the tube when it runs counter-clockwise.
                return (Band(new Torus(At(arc.Center.Y), arc.Center.X, arc.Radius), Ring(a), Ring(b), seam0, seamEnd, t0, t1, angle, arc.Sweep > 0, origin), null);
            default:
                throw new NotSupportedException(segment.GetType().Name);
        }
    }

    // A curved face turned from one segment: the circle (or arc) at its start, the meridian along it at u = angle,
    // the circle at its end back, the meridian back at u = 0; counter-clockwise in (u, v) when the meridian runs up v.
    // A whole turn's two meridians are one seam.
    static Patch Band(Surface surface, Circle3 start, Circle3 end, Curve3 seam0, Curve3 seamEnd, double t0, double t1, double angle, bool sameSense, HashSet<Origin> origin)
    {
        var turn = 2 * Math.PI;
        Curve2 At(Curve3 meridian, double u)
        {
            var pc = Uv.Pcurve(surface, meridian);
            return Uv.Shift(pc, u - pc.At(t0).X);
        }
        var (pc0, pcEnd) = (At(seam0, 0), At(seamEnd, angle));
        // The circles' v (round a torus's tube) taken to where the meridian has it at each end.
        Curve2 Along(Circle3 c, double t)
        {
            var pc = Uv.Pcurve(surface, c);
            return surface.PeriodV is null ? pc : Uv.Shift(pc, 0, turn * Math.Round((pc0.At(t).Y - pc.At(0).Y) / turn));
        }
        IReadOnlyList<Use> uv =
        [
            new Use(start, 0, angle, Along(start, t0)),
            new Use(angle >= turn - 1e-12 ? seam0 : seamEnd, t0, t1, angle >= turn - 1e-12 ? Uv.Shift(pc0, turn) : pcEnd),
            new Use(end, angle, 0, Along(end, t1)),
            new Use(seam0, t1, t0, pc0),
        ];
        var up = pc0.At(t1).Y > pc0.At(t0).Y;
        if (!up) uv = Arrangement.Sensed(uv, false);
        return new Patch(surface, sameSense, [Arrangement.Sensed(uv, sameSense)], origin);
    }

    // A part turn's end: the region itself in the half-plane at u, facing back along the turn at the start and on
    // along it at the end. In the plane's (x, z) the region's loops run as given (outer counter-clockwise).
    static Patch Cap(List<ProfileLoop> loops, RigidTransform3D frame, double u, bool start, int index)
    {
        var at = Turned(frame, u);
        // Normal −e_θ: (x, z) on the plane are the region's own, counter-clockwise seen from −e_θ.
        var plane = new Plane(RigidTransform3D.FromFrame(frame.Origin, -at.YAxis, at.XAxis));
        var uv = loops.Select(loop => (IReadOnlyList<Use>)[.. loop.Segments.Select(s =>
        {
            var curve = MeridianCurve(frame, u, s);
            var (from, to) = Span(s);
            return new Use(curve, from, to, Uv.Pcurve(plane, curve));
        })]).ToList();
        // Outwards is −e_θ at the start (the plane's normal), +e_θ at the end.
        return new Patch(plane, start, [.. uv.Select(l => Arrangement.Sensed(l, start))], new HashSet<Origin> { new(0, new FaceId(index)) });
    }
}
