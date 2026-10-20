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

        var seeds = new (WallResult Wall, double Size)[mesh.Quads.Count];
        Parallel.For(0, mesh.Quads.Count, i =>
        {
            var q = mesh.Quads[i];
            Point3D[] corners = q.IsTriangle ? [v[q.A], v[q.B], v[q.C]] : [v[q.A], v[q.B], v[q.C], v[q.D]];
            var sum = new Vector3D(0, 0, 0);
            foreach (var c in corners) sum += c.AsVector();
            var centre = new Point3D(sum.X / corners.Length, sum.Y / corners.Length, sum.Z / corners.Length);
            var best = new WallResult(double.PositiveInfinity, centre, centre, q.Owner, q.Owner);
            foreach (var start in corners.Select(c => centre + (c - centre) / 2).Prepend(centre))
                if (Chord(field, Onto(field, q.Owner, start), q.Owner, reach) is { } w && w.Thickness < best.Thickness) best = w;
            seeds[i] = (best, corners.Max(c => (c - centre).Length));
        });

        WallResult? result = null;
        foreach (var (wall, size) in seeds.Where(s => !double.IsPositiveInfinity(s.Wall.Thickness)).OrderBy(s => s.Wall.Thickness).Take(8))
        {
            var refined = Refine(field, wall, Math.Max(size, 1e-3), reach);
            if (result is null || refined.Thickness < result.Value.Thickness) result = refined;
        }
        return result;
    }

    /// <summary>Minimum wall of a regenerated part, seeded from its mesh.</summary>
    public static WallResult? MinimumWall(FrepResult result) => MinimumWall(result.Part.Field!, result.Mesh!);

    /// <summary>The normal chord at a boundary point of <paramref name="owner"/>; null if the point is not on a clean face.</summary>
    static WallResult? Chord(Field field, Point3D p, int owner, double reach)
    {
        if (Math.Abs(field.Value(p)) > 1e-9) return null;
        var n = field.SampleSurface(owner, p)!.Value.Gradient.Normalized();
        var start = p - n * Inset;
        if (field.Value(start) > -Inset / 2) return null;
        return Raycast(field, start, -n, reach) is { } hit ? new WallResult(Inset + hit.Distance, p, hit.Point, owner, hit.Owner) : null;
    }

    /// <summary>Pattern search over the surface of the chord's start face, halving the step until it is below 1e-10.</summary>
    static WallResult Refine(Field field, WallResult best, double step, double reach)
    {
        for (var it = 0; it < 2000 && step > 1e-10; it++)
        {
            var n = field.SampleSurface(best.FromOwner, best.From)!.Value.Gradient.Normalized();
            var e1 = (Math.Abs(n.X) < 0.9 ? new Vector3D(1, 0, 0) : new Vector3D(0, 1, 0)).Cross(n).Normalized();
            var e2 = n.Cross(e1);
            var moved = false;
            foreach (var dir in new[] { e1, -e1, e2, -e2 })
                if (Chord(field, Onto(field, best.FromOwner, best.From + dir * step), best.FromOwner, reach) is { } w && w.Thickness < best.Thickness)
                {
                    (best, moved) = (w, true);
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
