using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Geometry;
using Isoform.Sketching;
using static System.FormattableString;

namespace Isoform.Modeling;

/// <summary>
/// A sweep's pieces from its path and its profile, the checks that keep it exact, and its field (see
/// <see cref="SweepField"/>). What would not be exact is refused: a corner in the path, a profile not square to the path
/// at its start, an arc tighter than the profile reaches towards its centre.
/// </summary>
static class Sweeps
{
    // A point lies on the profile's plane within 1e-6 mm (sketch points join as closely); square and tangent within 1e-6 rad.
    const double Near = 1e-6, Square = 1e-6;

    /// <summary>The swept solid and the corners of a box round it, or why it cannot be built.</summary>
    public static (Field? Field, Point3D[] Box, string? Problem) Of(Sweep sw, SketchPlane profilePlane, IReadOnlyList<SketchRegion> regions,
        SketchPlane pathPlane, Profile path, bool closed, Frame placement, Func<Surface, Surface> register)
    {
        var sketch = Profiles.FrameOf(pathPlane).Then(placement);
        var section = Profiles.FrameOf(profilePlane).Then(placement);
        var edges = Profiles.Edges(path);
        var n = edges.Count;
        var normal = sketch.Z;

        for (var k = 0; k < (closed ? n : n - 1); k++)
        {
            var (a, b) = (edges[k].Edge.EndTangent, edges[(k + 1) % n].Edge.StartTangent);
            var turn = Degrees(Math.Atan2(a.U * b.V - a.V * b.U, a.U * b.U + a.V * b.V));
            if (Math.Abs(turn) > Degrees(Square))
                return Refused(closed && k == n - 1
                    ? Invariant($"the path closes at an angle of {turn:G4}° where {edges[k].Id} meets {edges[0].Id}; a closed path runs on smoothly (tangent) through its start")
                    : Invariant($"the path turns {turn:G4}° where {edges[k].Id} meets {edges[k + 1].Id}; a sweep path runs on smoothly (tangent) from each entity to the next"));
        }

        // Where it starts: an end of an open path or a vertex of a closed one, where the profile's plane crosses it square.
        List<SweepPiece>? pieces = null;
        if (closed && edges.All(e => e.Id == edges[0].Id))
        {
            // A circle: the profile's plane holds its axis, and the sweep starts on the profile's side of it.
            var e = edges[0].Edge;
            var centre = sketch.ToWorld(new Point3D(e.Cu, e.Cv, 0));
            if (Math.Abs(normal.Dot(section.Z)) > Square || Math.Abs((centre - section.Origin).Dot(section.Z)) > Near)
                return Refused($"the profile's plane must hold the axis of circle {edges[0].Id}, crossing the circle square");
            var across = section.Z.Cross(normal).Normalized();
            var outer = regions[0].Outer.Start;
            var side = (section.ToWorld(new Point3D(outer[0], outer[1], 0)) - centre).Dot(across) >= 0 ? 1 : -1;
            var start = sketch.ToLocal(centre + across * (side * e.R));
            var half = new ProfileEdge2D(start.X, start.Y, 2 * e.Cu - start.X, 2 * e.Cv - start.Y, 1);
            pieces = [SweepPiece.Of(edges[0].Id, half, sketch, 2 * Math.PI)];
        }
        else
        {
            var candidates = closed
                ? Enumerable.Range(0, n).Select(k => edges.Skip(k).Concat(edges.Take(k)).ToList())
                : [edges, [.. Enumerable.Reverse(edges).Select(e => (e.Id, e.Edge.Reversed()))]];
            // Where the plane crosses the path at more than one such place, the one nearest the profile.
            var (u0, v0, u1, v1) = Profiles.Bounds(regions[0].Outer);
            var middle = section.ToWorld(new Point3D((u0 + u1) / 2, (v0 + v1) / 2, 0));
            (double Off, double Angle) best = (double.PositiveInfinity, 0);
            var nearest = double.PositiveInfinity;
            foreach (var c in candidates)
            {
                var (at, along) = SweepPiece.Of(c[0].Id, c[0].Edge, sketch).Start;
                var off = Math.Abs((at - section.Origin).Dot(section.Z));
                var angle = Math.Atan2(along.Cross(section.Z).Length, Math.Abs(along.Dot(section.Z)));
                if (off <= Near && angle <= Square && (at - middle).Length < nearest)
                {
                    pieces = [.. c.Select(e => SweepPiece.Of(e.Id, e.Edge, sketch))];
                    nearest = (at - middle).Length;
                }
                if (off < best.Off) best = (off, angle);
            }
            if (pieces is null)
                return Refused(best.Off <= Near
                    ? Invariant($"the profile's plane meets the path at {Degrees(best.Angle):G4}° from square; it must be perpendicular to the path where the sweep starts")
                    : closed ? "the profile's plane does not cross the path at one of its vertices; the sweep starts where the profile is, square to the path"
                    : "the profile's plane does not cross an end of the path; the sweep starts where the profile is, square to the path");
        }

        // The profile in section coordinates at the start: a to the left of travel, b along the path plane's normal.
        var (s0, t0) = pieces[0].Start;
        var left = normal.Cross(t0);
        (double, double) ToSection(double x, double y)
        {
            var q = section.ToWorld(new Point3D(x, y, 0)) - s0;
            return (q.Dot(left), q.Dot(normal));
        }
        var flip = section.X.Dot(left) * section.Y.Dot(normal) - section.Y.Dot(left) * section.X.Dot(normal) < 0;
        var sections = regions.Select(r => new SketchRegion(Map(r.Outer, ToSection, flip), [.. r.Holes.Select(h => Map(h, ToSection, flip))])).ToList();

        var outline = sections.SelectMany(r => Profiles.Edges(r.Outer)).Select(e => Extent(e.Edge)).ToList();
        var (aLo, aHi) = (outline.Min(x => x.Lo), outline.Max(x => x.Hi));
        foreach (var arc in pieces.Where(p => p.Arc))
            if ((arc.Turn > 0 ? aHi : -aLo) is var reach && reach > arc.Radius + 1e-9)
                return Refused(Invariant($"path arc {arc.Id} has a radius of {arc.Radius:G4} mm, but the profile reaches {reach:G4} mm towards its centre: the sweep would run through itself there"));

        Field Section(SweepPiece piece)
        {
            Field Loop(Profile p)
            {
                var placed = Profiles.CounterClockwise(Map(p, piece.Section, piece.Arc));
                var leaves = placed.Select(x => ProfileField.EdgeLeaf(x.Edge, piece.Frame, piece.Arc,
                    (k, o, a, r, m, ang) => register(new Surface(0, $"{sw.Id}/{piece.Id}:{x.Id}", k, o, a, r, m, ang)))).ToList();
                return new ProfileField(placed.Select(x => x.Edge).ToList(), leaves, piece.Frame, piece.Arc);
            }
            var fields = sections.Select(r => r.Holes.Count == 0 ? Loop(r.Outer) : (Field)new MaxField([Loop(r.Outer), .. r.Holes.Select(h => (Field)new NegateField(Loop(h)))])).ToList();
            return fields.Count == 1 ? fields[0] : new MinField(fields);
        }
        var field = SweepField.Of(pieces, [.. pieces.Select(Section)], closed,
            (name, o, axis) => new LeafField(register(new Surface(0, $"{sw.Id}/{name}", SurfaceKind.Plane, o, axis))));

        // A box: the section's box at both ends of a line, a disc of the largest radius at both heights of an arc.
        var (a0, b0, a1, b1) = sections.Select(r => Profiles.Bounds(r.Outer)).Aggregate((x, y) => (Math.Min(x.U0, y.U0), Math.Min(x.V0, y.V0), Math.Max(x.U1, y.U1), Math.Max(x.V1, y.V1)));
        var box = new List<Point3D>();
        foreach (var piece in pieces)
        {
            if (!piece.Arc)
            {
                foreach (var (at, along) in new[] { piece.Start, piece.End })
                    box.AddRange(from a in new[] { a0, a1 } from b in new[] { b0, b1 } select at + normal.Cross(along) * a + normal * b);
                continue;
            }
            var rho = piece.Radius + (piece.Turn > 0 ? -a0 : a1);
            var extent = new Vector3D(rho * Math.Sqrt(Math.Max(0, 1 - normal.X * normal.X)), rho * Math.Sqrt(Math.Max(0, 1 - normal.Y * normal.Y)), rho * Math.Sqrt(Math.Max(0, 1 - normal.Z * normal.Z)));
            foreach (var b in new[] { b0, b1 })
                box.AddRange([piece.Frame.Origin + normal * b - extent, piece.Frame.Origin + normal * b + extent]);
        }
        return (field, [.. box], null);
    }

    static (Field?, Point3D[], string?) Refused(string why) => (null, [], why);

    static double Degrees(double radians) => radians * 180 / Math.PI;

    /// <summary>The profile through a rigid map of its plane; a mirroring map turns its arcs the other way.</summary>
    static Profile Map(Profile p, Func<double, double, (double U, double V)> to, bool mirror)
    {
        double[] At(double[] x)
        {
            var (u, v) = to(x[0], x[1]);
            return [u, v];
        }
        return new(At(p.Start), [.. p.Edges.Select(e => e with { To = At(e.To), Bulge = mirror ? -e.Bulge : e.Bulge })]);
    }

    /// <summary>How far the edge reaches along u, arcs exactly.</summary>
    static (double Lo, double Hi) Extent(ProfileEdge2D e)
    {
        var (lo, hi) = (Math.Min(e.U0, e.U1), Math.Max(e.U0, e.U1));
        if (e.IsArc)
            foreach (var u in new[] { e.Cu - e.R, e.Cu + e.R })
                if (e.Closest(u, e.Cv).Where < 0) (lo, hi) = (Math.Min(lo, u), Math.Max(hi, u));
        return (lo, hi);
    }
}
