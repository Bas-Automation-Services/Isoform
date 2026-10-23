using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Geometry;
using Isoform.Meshing;

namespace Isoform.Modeling;

/// <summary>
/// The channels a lattice's sheets leave against its skin. Where a sheet runs nearly parallel to the skin's inner face
/// without meeting it, the void between them is as thin as the distance there: a critical point of the wall's depth on
/// the sheet's surface, the sheet facing the wall. A saddle is a channel between two voids, closed on either side where
/// the sheet meets the wall; a bump towards the wall a neck; a dip a pocket the sheet seals. The lattice's own sizes
/// never see them, the mesher's thin-spot search takes a saddle's sides for wedges, and the cells fill it in.
/// <para>
/// Seeds are the centres of boxes an eighth of the lattice cell across where the bounds allow both the sheet's surface
/// and a depth below the reach; from each whose sheet already faces the wall, Newton on the sheet's value and the two
/// normals' tangential difference finds the critical point. The mesh is then judged along the line across the channel.
/// Struts are cylinders, convex across: against a wall they only make necks, two opposing surfaces that do not meet,
/// which the mesher's thin-spot search looks for.
/// </para>
/// </summary>
public static class LatticeChannels
{
    // How nearly a seed's sheet must face the wall (cos 60°) for a search from it.
    const double Facing = 0.5;

    // Seeds per lattice cell along each axis.
    const int SeedsPerCell = 8;

    /// <summary>The narrowest place of a channel: the point on the sheet, the foot on the wall across it, its width.</summary>
    public readonly record struct Channel(string Lattice, Point3D Sheet, Point3D Wall, double Width)
    {
        public Point3D Middle => Sheet + (Wall - Sheet) / 2;
    }

    /// <summary>The channels narrower than <paramref name="reach"/> of the skinned sheet lattices, by body.</summary>
    public static List<Channel>[] Find(PartDocument document, CompiledPart part, double reach, double tolerance)
    {
        var lattices = document.Features.OfType<Lattice>().Where(l => l is { Sheets: true, Skin: > 0 }).ToList();
        var result = part.Bodies.Select(_ => new List<Channel>()).ToArray();
        if (lattices.Count == 0) return result;
        for (var b = 0; b < part.Bodies.Count; b++)
        {
            var body = part.Bodies[b];
            foreach (var l in lattices)
            {
                // The skin: the shell whose cavity's inner face for the lattice is the lattice itself.
                if (FieldAlgebra.Descendants(body.Field).OfType<ShellField>()
                        .FirstOrDefault(s => s.InnerOf.Any(kv => kv.Key == kv.Value && part.Surfaces[kv.Key].Source == l.Face)) is not { } skin) continue;
                var sheet = new LatticeField(DocumentRules.LatticeKindOf(l.Kind)!.Value, l.Cell, l.Thickness, part.Surfaces.First(s => s.Source == l.Face));
                result[b].AddRange(Search(l.Id, body.Field, skin.Outer, skin.Thickness, sheet, new Box3D(body.Min, body.Max), reach, l.Cell / SeedsPerCell, tolerance));
            }
        }
        return result;
    }

    static List<Channel> Search(string id, Field body, Field outer, double skin, LatticeField sheet, Box3D box, double reach, double spacing, double tolerance)
    {
        // The depth into the cavity is −outer − skin; seeds where it may lie in [0, reach] and the sheet may pass, a
        // level of boxes at a time, each side halved until it is no longer than the spacing.
        var seeds = new List<Point3D>();
        for (var level = new List<(Box3D Box, Field Outer)> { (box, outer.Simplify(box)) }; level.Count > 0;)
        {
            var kept = level.AsParallel().AsOrdered().Where(x =>
            {
                var depth = x.Outer.Bounds(x.Box);
                return -depth.Hi - skin <= reach && -depth.Lo - skin >= 0 && sheet.Bounds(x.Box) is var f && f.Lo <= 0 && f.Hi >= 0;
            }).ToList();
            if (kept.Count > 0 && Halves(kept[0].Box, spacing).Count() == 1)
            {
                seeds.AddRange(kept.Select(x => x.Box.Center));
                break;
            }
            level = [.. kept.AsParallel().AsOrdered().SelectMany(x => { var o = x.Outer.Simplify(x.Box); return Halves(x.Box, spacing).Select(c => (c, o)); })];
        }

        // Each seed onto the sheet; only where the sheet already faces the wall within 60° is the critical point
        // looked for. The seeds that find one land on it to the last bits, so the first of each is kept, in seed order.
        var at = new Channel?[seeds.Count];
        Parallel.For(0, seeds.Count, i =>
        {
            var q = seeds[i];
            for (var k = 0; k < 2; k++)
            {
                var a = sheet.Sample(q);
                q -= a.Gradient * (a.Value / a.Gradient.LengthSquared);
            }
            if (!(sheet.Sample(q).Gradient.Normalized().Dot(outer.Sample(q).Gradient.Normalized()) >= Facing)) return;
            if (Critical(outer, sheet, q, spacing) is not { } p) return;
            // The sheet's void faces the wall: its normal is the outside's there.
            var n = outer.Sample(p).Gradient.Normalized();
            if (sheet.Sample(p).Gradient.Normalized().Dot(n) < 0.99) return;
            // Across to the wall, the inner offset −outer − skin = 0, along the normal.
            var s = 0.0;
            for (var it = 0; it < 4 && double.IsFinite(s); it++)
            {
                var o = outer.Sample(p + n * s);
                s += (-o.Value - skin) / o.Gradient.Dot(n);
            }
            if (!(s > tolerance && s < reach) || !Void(body, p, n, s)) return; // NaN too: a wall the line does not reach
            at[i] = new Channel(id, p, p + n * s, s);
        });
        var found = new List<Channel>();
        foreach (var c in at)
            if (c is { } x && !found.Any(f => (f.Sheet - x.Sheet).LengthSquared < 1e-12)) found.Add(x);
        return found;
    }

    /// <summary>The box with each side longer than <paramref name="spacing"/> halved.</summary>
    static IEnumerable<Box3D> Halves(Box3D b, double spacing)
    {
        var c = b.Center;
        (double, double)[] Side(double lo, double mid, double hi) => hi - lo > spacing ? [(lo, mid), (mid, hi)] : [(lo, hi)];
        foreach (var (x0, x1) in Side(b.Min.X, c.X, b.Max.X))
        foreach (var (y0, y1) in Side(b.Min.Y, c.Y, b.Max.Y))
        foreach (var (z0, z1) in Side(b.Min.Z, c.Z, b.Max.Z))
            yield return new Box3D(new Point3D(x0, y0, z0), new Point3D(x1, y1, z1));
    }

    /// <summary>
    /// Whether the part is void all the way across, from the sheet at <paramref name="p"/> to the wall
    /// <paramref name="width"/> along <paramref name="n"/>, but for a hundredth of it at either end: nothing else of the
    /// part between them. The field is 1-Lipschitz, so each step of its value stays in the void.
    /// </summary>
    static bool Void(Field body, Point3D p, Vector3D n, double width)
    {
        var (s, end) = (width / 100, 0.99 * width);
        while (s < end)
        {
            var v = body.Value(p + n * s);
            if (v <= 0) return false;
            s += Math.Max(v, width / 100);
        }
        return body.Value(p + n * end) > 0;
    }

    /// <summary>
    /// The point of the sheet's surface near <paramref name="p"/> whose normal is parallel to the outside's: Newton on
    /// the sheet's value over its gradient and the tangential part of the two unit normals' difference, with the
    /// Jacobian by differences. Null if it does not converge within a few spacings.
    /// </summary>
    static Point3D? Critical(Field outer, LatticeField sheet, Point3D p, double spacing)
    {
        var start = p;
        for (var it = 0; it < 30; it++)
        {
            var n = sheet.Sample(p).Gradient.Normalized();
            var e1 = (Math.Abs(n.X) < 0.6 ? new Vector3D(1, 0, 0) : new Vector3D(0, 1, 0)).Cross(n).Normalized();
            var e2 = n.Cross(e1);
            Vector3D R(Point3D q)
            {
                var a = sheet.Sample(q);
                var d = a.Gradient.Normalized() - outer.Sample(q).Gradient.Normalized();
                return new Vector3D(a.Value / a.Gradient.Length, e1.Dot(d), e2.Dot(d));
            }
            var r = R(p);
            if (!double.IsFinite(r.Length)) return null;
            if (r.Length < 1e-12) return p;
            const double h = 1e-7;
            var cx = (R(p + new Vector3D(h, 0, 0)) - r) / h;
            var cy = (R(p + new Vector3D(0, h, 0)) - r) / h;
            var cz = (R(p + new Vector3D(0, 0, h)) - r) / h;
            // Rows of the Jacobian are the residual's components; its columns cx, cy, cz. Cramer's rule for J d = −r.
            var det = cx.Dot(cy.Cross(cz));
            if (Math.Abs(det) < 1e-300) return null;
            var step = new Vector3D(-r.Dot(cy.Cross(cz)), -cx.Dot(r.Cross(cz)), -cx.Dot(cy.Cross(r))) / det;
            if (step.Length > spacing) step = step.Normalized() * spacing;
            p += step;
            if ((p - start).Length > 4 * spacing) return null;
            if (step.Length < 1e-12) return p;
        }
        return null;
    }

    /// <summary>
    /// The channels the mesh fills in: no polygon crosses the line across the channel between its middle and the
    /// sheet, or between its middle and the wall, within the width beyond either. A mesh that carries the channel
    /// parts its middle from both.
    /// </summary>
    public static List<Channel> Lost(IReadOnlyList<Channel> channels, Mesh mesh)
    {
        if (channels.Count == 0) return [];
        // The channels in bins over their box, at most 128 along a side: a polygon is tested against those of the bins
        // it comes within reach of.
        var reach = channels.Max(c => 1.5 * c.Width);
        var (lo, hi) = (channels[0].Middle, channels[0].Middle);
        foreach (var c in channels)
            (lo, hi) = (new(Math.Min(lo.X, c.Middle.X), Math.Min(lo.Y, c.Middle.Y), Math.Min(lo.Z, c.Middle.Z)), new(Math.Max(hi.X, c.Middle.X), Math.Max(hi.Y, c.Middle.Y), Math.Max(hi.Z, c.Middle.Z)));
        var bin = Math.Max(2 * reach, Math.Max(hi.X - lo.X, Math.Max(hi.Y - lo.Y, hi.Z - lo.Z)) / 127);
        var (nx, ny, nz) = ((int)((hi.X - lo.X) / bin) + 1, (int)((hi.Y - lo.Y) / bin) + 1, (int)((hi.Z - lo.Z) / bin) + 1);
        var bins = new List<int>?[nx * ny * nz];
        for (var i = 0; i < channels.Count; i++)
        {
            var m = channels[i].Middle;
            (bins[Index((int)((m.X - lo.X) / bin), (int)((m.Y - lo.Y) / bin), (int)((m.Z - lo.Z) / bin))] ??= []).Add(i);
        }
        int Index(int i, int j, int k) => (k * ny + j) * nx + i;
        int Clamp(double x, int n) => (int)Math.Clamp(Math.Floor(x / bin), -1, n);

        var (below, above) = (new bool[channels.Count], new bool[channels.Count]);
        var v = mesh.Vertices;
        foreach (var q in mesh.Quads)
        {
            var (a, b, c, d) = (v[q.A], v[q.B], v[q.C], v[q.D]);
            var (i0, i1) = (Clamp(Math.Min(Math.Min(a.X, b.X), Math.Min(c.X, d.X)) - reach - lo.X, nx), Clamp(Math.Max(Math.Max(a.X, b.X), Math.Max(c.X, d.X)) + reach - lo.X, nx));
            var (j0, j1) = (Clamp(Math.Min(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y)) - reach - lo.Y, ny), Clamp(Math.Max(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y)) + reach - lo.Y, ny));
            var (k0, k1) = (Clamp(Math.Min(Math.Min(a.Z, b.Z), Math.Min(c.Z, d.Z)) - reach - lo.Z, nz), Clamp(Math.Max(Math.Max(a.Z, b.Z), Math.Max(c.Z, d.Z)) + reach - lo.Z, nz));
            for (var k = Math.Max(k0, 0); k <= Math.Min(k1, nz - 1); k++)
            for (var j = Math.Max(j0, 0); j <= Math.Min(j1, ny - 1); j++)
            for (var i = Math.Max(i0, 0); i <= Math.Min(i1, nx - 1); i++)
            {
                if (bins[Index(i, j, k)] is not { } near) continue;
                foreach (var x in near)
                {
                    // Along the normal from the middle: the wall at +width/2, the sheet at −width/2.
                    var ch = channels[x];
                    var (m, n) = (ch.Middle, (ch.Wall - ch.Sheet) / ch.Width);
                    if ((Crossing(m, n, a, b, c) ?? (q.IsTriangle ? null : Crossing(m, n, a, c, d))) is { } s && Math.Abs(s) <= 1.5 * ch.Width)
                    {
                        if (s >= 0) above[x] = true;
                        else below[x] = true;
                    }
                }
            }
        }
        return [.. channels.Where((_, i) => !(below[i] && above[i]))];
    }

    /// <summary>Where the line m + s·n crosses triangle abc (Möller–Trumbore), or null.</summary>
    static double? Crossing(Point3D m, Vector3D n, Point3D a, Point3D b, Point3D c)
    {
        var (e1, e2) = (b - a, c - a);
        var h = n.Cross(e2);
        var det = e1.Dot(h);
        if (Math.Abs(det) < 1e-300) return null;
        var d = m - a;
        var u = d.Dot(h) / det;
        if (u < 0 || u > 1) return null;
        var q = d.Cross(e1);
        var w = n.Dot(q) / det;
        if (w < 0 || u + w > 1) return null;
        return e2.Dot(q) / det;
    }
}
