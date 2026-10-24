using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Geometry;
using Isoform.Meshing;

namespace Isoform.Modeling;

/// <summary>
/// The channels a lattice's sheets leave against the rest of the part: its skin's inner faces, a region's faces with
/// the solid beyond them, or both. Where a sheet runs nearly parallel to such a wall without meeting it, the void
/// between them is as thin as the wall's depth there: a critical point of that depth on the sheet's surface, the sheet
/// facing the wall. A saddle is a channel between two voids, closed on either side where the sheet meets the wall; a
/// bump towards the wall a neck; a dip a pocket the sheet seals. The lattice's own sizes never see them, the mesher's
/// thin-spot search takes a saddle's sides for wedges, and the cells fill it in.
/// <para>
/// The wall is the part with the sheets taken out (<see cref="Rest"/>), and its depth the value of that. Seeds are the
/// centres of boxes an eighth of the lattice cell across where the bounds allow the sheet's surface and a depth below
/// the reach; from each whose sheet already faces the wall, Newton on the sheet's value and the two normals' tangential
/// difference finds the critical point. Where two faces of the wall meet (an edge of the cavity, a region's face against
/// the skin) the depth has a crease, the lesser of the two, and the narrowest place of a channel over it lies on the
/// crease: Newton on the sheet's value, the two depths' difference and the sheet's normal in the plane of theirs. The
/// sheet's point must be on the part's boundary, and the wall material. The mesh is then judged along the line across the channel. Struts are cylinders, convex
/// across: against a wall they only make necks, two opposing surfaces that do not meet, which the mesher's thin-spot
/// search looks for.
/// </para>
/// </summary>
public static class LatticeChannels
{
    // How nearly a seed's sheet must face the wall (cos 60°) for a search from it.
    const double Facing = 0.5;

    // Seeds per lattice cell along each axis.
    const int SeedsPerCell = 8;

    /// <summary>
    /// The narrowest place of a channel: the point on the sheet, the foot on the wall across it, its width, and what
    /// the wall is (its skin, its region).
    /// </summary>
    public readonly record struct Channel(string Lattice, Point3D Sheet, Point3D Wall, double Width, string Against = "its skin")
    {
        public Point3D Middle => Sheet + (Wall - Sheet) / 2;
    }

    /// <summary>The channels narrower than <paramref name="reach"/> of the sheet lattices against their walls, by body.</summary>
    public static List<Channel>[] Find(PartDocument document, CompiledPart part, double reach, double tolerance)
    {
        var lattices = document.Features.OfType<Lattice>().Where(l => l.Sheets).ToList();
        var result = part.Bodies.Select(_ => new List<Channel>()).ToArray();
        if (lattices.Count == 0) return result;
        for (var b = 0; b < part.Bodies.Count; b++)
        {
            var body = part.Bodies[b];
            foreach (var l in lattices)
            {
                var face = part.Surfaces.First(s => s.Source == l.Face);
                // No wall where the lattice is open to the outside, or not in this body.
                if (!Holds(body.Field, face.Id) || Rest(body.Field, face.Id).F is not { } wall) continue;
                var sheet = new LatticeField(DocumentRules.LatticeKindOf(l.Kind)!.Value, l.Cell, l.Thickness, face);
                result[b].AddRange(Search(l.Id, part.Surfaces, body.Field, wall, sheet, new Box3D(body.Min, body.Max), reach, l.Cell / SeedsPerCell, tolerance));
            }
        }
        return result;
    }

    static bool Holds(Field f, int face) =>
        FieldAlgebra.Descendants(f).Any(n => n is LatticeField l && l.Surface.Id == face || n is ShellField s && Holds(s.Child, face));

    /// <summary>
    /// The part with the lattice's sheets taken out: the field of what is left, the wall the sheets face. Null where
    /// nothing is left; Inf says which: +1 nothing, −1 everything (the sheets' voids, a skin's child, become solid).
    /// </summary>
    static (Field? F, int Inf) Rest(Field f, int face)
    {
        switch (f)
        {
            case LatticeField l when l.Surface.Id == face:
                return (null, l.Complement ? -1 : 1);
            case NegateField n:
                return Rest(n.Child, face) is { F: { } c } ? (new NegateField(c), 0) : (null, -Rest(n.Child, face).Inf);
            case MaxField or MinField:
                // +∞ absorbs a ∩ and drops out of a ∪; −∞ the other way round.
                var max = f is MaxField;
                var kept = new List<Field>();
                foreach (var child in max ? ((MaxField)f).Children : ((MinField)f).Children)
                {
                    var (g, inf) = Rest(child, face);
                    if (inf == (max ? 1 : -1)) return (null, inf);
                    if (g is not null) kept.Add(g);
                }
                return kept.Count switch { 0 => (null, max ? -1 : 1), 1 => (kept[0], 0), _ => (max ? new MaxField(kept) : new MinField(kept), 0) };
            case ShellField s when Holds(s.Child, face):
                return Rest(s.Child, face) is { F: { } inside } ? (new ShellField(inside, s.Thickness, s.InnerOf).WithOuter(s.Outer), 0) : (null, 1);
            default:
                return (f, 0);
        }
    }

    static List<Channel> Search(string id, IReadOnlyList<Surface> surfaces, Field body, Field wall, LatticeField sheet, Box3D box, double reach, double spacing, double tolerance)
    {
        // The depth into the void is the wall's value; seeds where it may lie in [0, reach] and the sheet may pass, a
        // level of boxes at a time, each side halved until it is no longer than the spacing.
        var seeds = new List<Point3D>();
        for (var level = new List<(Box3D Box, Field Wall)> { (box, wall.Simplify(box)) }; level.Count > 0;)
        {
            var kept = level.AsParallel().AsOrdered().Where(x =>
            {
                var depth = x.Wall.Bounds(x.Box);
                return depth.Lo <= reach && depth.Hi >= 0 && sheet.Bounds(x.Box) is var f && f.Lo <= 0 && f.Hi >= 0;
            }).ToList();
            if (kept.Count > 0 && Halves(kept[0].Box, spacing).Count() == 1)
            {
                seeds.AddRange(kept.Select(x => x.Box.Center));
                break;
            }
            level = [.. kept.AsParallel().AsOrdered().SelectMany(x => { var w = x.Wall.Simplify(x.Box); return Halves(x.Box, spacing).Select(c => (c, w)); })];
        }

        // Each seed onto the sheet; only where the sheet already faces the wall within 60° is the critical point
        // looked for, on the wall's face and on each crease between two faces near it. The seeds that find one land on
        // it to the last bits, so the first of each is kept, in seed order.
        var at = new List<Channel>?[seeds.Count];
        Parallel.For(0, seeds.Count, i =>
        {
            var q = seeds[i];
            for (var k = 0; k < 2; k++)
            {
                var a = sheet.Sample(q);
                q -= a.Gradient * (a.Value / a.Gradient.LengthSquared);
            }
            if (!(sheet.Sample(q).Gradient.Normalized().Dot(-wall.Sample(q).Gradient.Normalized()) >= Facing)) return;
            var found = new List<Channel>();
            if (Critical(wall, sheet, q, spacing) is { } p && -wall.Sample(p).Gradient.Normalized() is var n && sheet.Sample(p).Gradient.Normalized().Dot(n) >= 0.99
                && Across(id, surfaces, body, wall, p, n, reach, tolerance) is { } c)
                found.Add(c);
            var pieces = new List<Piece>();
            Pieces(wall, q, spacing, 1, 0, pieces);
            for (var x = 0; x < pieces.Count; x++)
            for (var y = x + 1; y < pieces.Count; y++)
                if (Crease(wall, sheet, pieces[x], pieces[y], q, spacing) is { } e && Across(id, surfaces, body, wall, e.At, e.Towards, reach, tolerance) is { } d)
                    found.Add(d);
            at[i] = found;
        });
        var result = new List<Channel>();
        foreach (var c in at.Where(x => x is not null).SelectMany(x => x!))
            if (!result.Any(f => (f.Sheet - c.Sheet).LengthSquared < 1e-12)) result.Add(c);
        return result;
    }

    /// <summary>
    /// The channel from <paramref name="p"/> on the sheet across to the wall along <paramref name="n"/>, if it is one: p
    /// on the part's boundary, the wall within reach, void all the way and material beyond it.
    /// </summary>
    static Channel? Across(string id, IReadOnlyList<Surface> surfaces, Field body, Field wall, Point3D p, Vector3D n, double reach, double tolerance)
    {
        if (!(Math.Abs(body.Value(p)) <= tolerance)) return null;
        // Across to the wall, where its depth is 0, along the normal.
        var s = 0.0;
        for (var it = 0; it < 4 && double.IsFinite(s); it++)
        {
            var o = wall.Sample(p + n * s);
            s -= o.Value / o.Gradient.Dot(n);
        }
        // NaN too: a wall the line does not reach.
        if (!(s > tolerance && s < reach) || !Void(body, p, n, s) || !(body.Value(p + n * (1.01 * s)) < 0)) return null;
        var face = surfaces[wall.Sample(p + n * s).Owner].Source;
        return new Channel(id, p, p + n * s, s, face.StartsWith(id + "/inner:") ? "its skin" : face.StartsWith(id + "/region:") ? "its region" : face);
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
    /// The point of the sheet's surface near <paramref name="p"/> whose normal is opposite the wall's: the sheet's
    /// value over its gradient and the tangential part of the two unit normals' sum. Null if it does not converge
    /// within a few spacings.
    /// </summary>
    static Point3D? Critical(Field wall, LatticeField sheet, Point3D p, double spacing) => Newton(p, spacing, at =>
    {
        var n = sheet.Sample(at).Gradient.Normalized();
        var e1 = (Math.Abs(n.X) < 0.6 ? new Vector3D(1, 0, 0) : new Vector3D(0, 1, 0)).Cross(n).Normalized();
        var e2 = n.Cross(e1);
        return q =>
        {
            var a = sheet.Sample(q);
            var d = a.Gradient.Normalized() + wall.Sample(q).Gradient.Normalized();
            return new Vector3D(a.Value / a.Gradient.Length, e1.Dot(d), e2.Dot(d));
        };
    });

    /// <summary>A smooth piece of the wall's depth: <see cref="Sign"/> · F + <see cref="Offset"/>.</summary>
    readonly record struct Piece(Field F, double Sign, double Offset)
    {
        public double Value(Point3D p) => Sign * F.Value(p) + Offset;
        public Vector3D Gradient(Point3D p) => F.Sample(p).Gradient * Sign;
    }

    /// <summary>
    /// The smooth pieces of a depth near <paramref name="p"/>: the children of its ∩ and ∪ (a shell's outside and
    /// inside, a profile's edges) within <paramref name="within"/> of the one that decides there, down to the fields
    /// that are none.
    /// </summary>
    static void Pieces(Field f, Point3D p, double within, double sign, double offset, List<Piece> into)
    {
        switch (f)
        {
            case NegateField n:
                Pieces(n.Child, p, within, -sign, offset, into);
                return;
            case MaxField or MinField:
                var v = f.Value(p);
                foreach (var c in f is MaxField m ? m.Children : ((MinField)f).Children)
                    if (Math.Abs(c.Value(p) - v) <= within) Pieces(c, p, within, sign, offset, into);
                return;
            case ShellField s:
                var (outer, inner, value) = (s.Outer.Value(p), -s.Child.Value(p) - s.Thickness, s.Value(p));
                if (Math.Abs(outer - value) <= within) Pieces(s.Outer, p, within, sign, offset, into);
                if (Math.Abs(inner - value) <= within) Pieces(s.Child, p, within, -sign, offset - sign * s.Thickness, into);
                return;
            case ProfileField profile:
                // Where the nearest point lies inside an edge, the profile's value is that edge's surface's.
                var near = profile.Leaves.Where(l => Math.Abs(l.Value(p) - profile.Value(p)) <= within).ToList();
                if (near.Count == 0) into.Add(new Piece(f, sign, offset));
                foreach (var l in near) into.Add(new Piece(l, sign, offset));
                return;
            default:
                into.Add(new Piece(f, sign, offset));
                return;
        }
    }

    /// <summary>
    /// The narrowest place of a channel over the crease where two pieces of the wall's depth meet: the point of the
    /// sheet's surface near <paramref name="p"/> on the crease (the two depths equal) where the sheet's normal lies in
    /// the plane of theirs, and the sheet faces in between. It is a critical point of the depth, the lesser of the
    /// two, on the sheet. Null if there is none near, or the two pieces are not the wall there; else the point and
    /// the way to the first piece's face.
    /// </summary>
    static (Point3D At, Vector3D Towards)? Crease(Field wall, LatticeField sheet, Piece a, Piece b, Point3D p, double spacing)
    {
        if (Newton(p, spacing, _ => q =>
            {
                var s = sheet.Sample(q);
                return new Vector3D(s.Value / s.Gradient.Length, a.Value(q) - b.Value(q),
                    s.Gradient.Normalized().Dot(a.Gradient(q).Normalized().Cross(b.Gradient(q).Normalized())));
            }) is not { } at)
            return null;
        // The sheet faces in between: its normal a positive sum of the two ways to the wall, and the two are the wall.
        var (n, ga, gb) = (sheet.Sample(at).Gradient.Normalized(), -a.Gradient(at).Normalized(), -b.Gradient(at).Normalized());
        var c = ga.Dot(gb);
        var (u, v) = ((n.Dot(ga) - c * n.Dot(gb)) / (1 - c * c), (n.Dot(gb) - c * n.Dot(ga)) / (1 - c * c));
        if (!(u > 0 && v > 0 && (ga * u + gb * v - n).Length < 1e-6 && Math.Abs(wall.Value(at) - a.Value(at)) < 1e-9)) return null;
        return (at, ga);
    }

    /// <summary>
    /// Newton on a residual of three components with the Jacobian by differences, the residual set up afresh at each
    /// step (<paramref name="residual"/> of the point). Null if it does not converge within a few spacings.
    /// </summary>
    static Point3D? Newton(Point3D p, double spacing, Func<Point3D, Func<Point3D, Vector3D>> residual)
    {
        var start = p;
        for (var it = 0; it < 30; it++)
        {
            var R = residual(p);
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
