using BasAutomation.Geometry;
using Isoform.Geometry;
using Isoform.Meshing;

namespace Isoform.Modeling;

/// <summary>The thinnest wall found: a chord through material from <see cref="From"/> along its inward normal to <see cref="To"/>.</summary>
public readonly record struct WallResult(double Thickness, Point3D From, Point3D To, int FromOwner, int ToOwner);

public static partial class Queries
{
    // Chords start this far inside, and only where the field there says the point is that deep: a face, not an edge.
    const double Inset = 1e-5;

    /// <summary>
    /// Minimum wall thickness, measured as the normal chord: from a boundary point p, the distance along the inward normal
    /// to where the ray leaves the material. That is the wall a caliper square to the face measures; on a web between a
    /// hole and a face it is the web, on a shell its thickness. A convex edge of 90° or more never reads thin: the chord
    /// from beside it runs along or away from the other face. A sharper edge is thin material and reads so towards its tip.
    /// <para>
    /// A wall reads thin from both sides: a chord counts as the longer of itself and the chord back from its far end.
    /// Where faces face each other (a plate, a web, a shell, the thin end of a tapering wall) the two agree. A chord
    /// beside a convex edge that runs along the other face up to a fillet or chamfer on the far edge reads that face's
    /// height, the plate minus the fillet at the edge itself; the chord back from the fillet runs into the part, so a
    /// filleted plate reads its thickness. At a knife edge both chords shrink towards the tip.
    /// </para>
    /// <para>
    /// A wall reads thin from both sides: the chord counts as the longer of itself and the chord back from its far end.
    /// Where faces face each other (a plate, a web, a shell, the minimum of a tapering wall) the two agree. A chord
    /// beside a convex edge that runs along the other face and ends on a fillet or chamfer of the far edge reads that
    /// face's height, down to the plate minus the fillet at the edge itself; the chord back from the fillet runs into
    /// the part, so a filleted plate still reads its thickness. At a knife edge both chords shrink towards the tip.
    /// </para>
    /// <para>
    /// Seeds are taken on every mesh polygon (its centre and its corners pulled halfway in, projected onto the polygon's
    /// exact surface). The thinnest are refined by a pattern search over the surface, so a minimum inside a face or across
    /// a smooth patch is found to ~1e-9. Not certified: a spot thinner than its surroundings and narrower than the
    /// polygons can fall between seeds.
    /// </para>
    /// </summary>
    public static WallResult? MinimumWall(Field field, Mesh mesh)
    {
        if (mesh.Quads.Count == 0) return null;
        var v = mesh.Vertices;
        var (lo, hi) = (v[0], v[0]);
        foreach (var p in v)
        {
            lo = new(Math.Min(lo.X, p.X), Math.Min(lo.Y, p.Y), Math.Min(lo.Z, p.Z));
            hi = new(Math.Max(hi.X, p.X), Math.Max(hi.Y, p.Y), Math.Max(hi.Z, p.Z));
        }
        var reach = (hi - lo).Length * 1.01 + 1;

        var seeds = new (WallResult Wall, Point3D At, int Owner, double Size)[mesh.Quads.Count];
        Parallel.For(0, mesh.Quads.Count, i =>
        {
            var q = mesh.Quads[i];
            Point3D[] corners = q.IsTriangle ? [v[q.A], v[q.B], v[q.C]] : [v[q.A], v[q.B], v[q.C], v[q.D]];
            var sum = new Vector3D(0, 0, 0);
            foreach (var c in corners) sum += c.AsVector();
            var centre = new Point3D(sum.X / corners.Length, sum.Y / corners.Length, sum.Z / corners.Length);
            var (best, at) = (new WallResult(double.PositiveInfinity, centre, centre, q.Owner, q.Owner), centre);
            foreach (var start in corners.Select(c => centre + (c - centre) / 2).Prepend(centre))
                if (Onto(field, q.Owner, start) is var x && Wall(field, x, q.Owner, reach, best.Thickness) is { } w && w.Thickness < best.Thickness) (best, at) = (w, x);
            seeds[i] = (best, at, q.Owner, corners.Max(c => (c - centre).Length));
        });

        WallResult? result = null;
        foreach (var (wall, at, owner, size) in seeds.Where(s => !double.IsPositiveInfinity(s.Wall.Thickness)).OrderBy(s => s.Wall.Thickness).Take(8))
        {
            var refined = Refine(field, at, owner, wall, Math.Max(size, 1e-3), reach);
            if (result is null || refined.Thickness < result.Value.Thickness) result = refined;
        }
        return result;
    }

    /// <summary>Minimum wall of a regenerated part, seeded from its mesh.</summary>
    public static WallResult? MinimumWall(FrepResult result) => MinimumWall(result.Part.Field!, result.Mesh!);

    /// <summary>
    /// The wall at a boundary point: the longer of its normal chord and the chord back from the far end (that one when
    /// it can start there). A chord no shorter than <paramref name="below"/> is returned as is: the back chord cannot
    /// make it shorter.
    /// </summary>
    static WallResult? Wall(Field field, Point3D p, int owner, double reach, double below)
    {
        var chord = Chord(field, p, owner, reach);
        if (chord is not { } w || w.Thickness >= below) return chord;
        return Chord(field, w.To, w.ToOwner, reach) is { } back && back.Thickness > w.Thickness ? back : w;
    }

    /// <summary>The normal chord at a boundary point of <paramref name="owner"/>; null if the point is not on a clean face.</summary>
    static WallResult? Chord(Field field, Point3D p, int owner, double reach)
    {
        if (Math.Abs(field.Value(p)) > 1e-9) return null;
        var n = field.SampleSurface(owner, p)!.Value.Gradient.Normalized();
        var start = p - n * Inset;
        if (field.Value(start) > -Inset / 2) return null;
        return Raycast(field, start, -n, reach) is { } hit ? new WallResult(Inset + hit.Distance, p, hit.Point, owner, hit.Owner) : null;
    }

    /// <summary>Pattern search over the surface of <paramref name="owner"/> from <paramref name="at"/>, halving the step until it is below 1e-10.</summary>
    static WallResult Refine(Field field, Point3D at, int owner, WallResult best, double step, double reach)
    {
        for (var it = 0; it < 2000 && step > 1e-10; it++)
        {
            var n = field.SampleSurface(owner, at)!.Value.Gradient.Normalized();
            var e1 = (Math.Abs(n.X) < 0.9 ? new Vector3D(1, 0, 0) : new Vector3D(0, 1, 0)).Cross(n).Normalized();
            var e2 = n.Cross(e1);
            var moved = false;
            foreach (var dir in new[] { e1, -e1, e2, -e2 })
                if (Onto(field, owner, at + dir * step) is var x && Wall(field, x, owner, reach, best.Thickness) is { } w && w.Thickness < best.Thickness)
                {
                    (best, at, moved) = (w, x, true);
                    break;
                }
            if (!moved) step /= 2;
        }
        return best;
    }

    /// <summary>Newton steps onto one owner's surface.</summary>
    static Point3D Onto(Field field, int owner, Point3D p)
    {
        for (var it = 0; it < 6; it++)
        {
            var s = field.SampleSurface(owner, p)!.Value;
            p -= s.Gradient * (s.Value / s.Gradient.LengthSquared);
        }
        return p;
    }
}
