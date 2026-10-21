using BasAutomation.Geometry;

namespace Isoform.Geometry;

public enum LatticeKind { Gyroid, SchwarzP, Diamond, Bcc, Octet, Cubic }

/// <summary>
/// A periodic lattice on the world grid, cell <see cref="Cell"/> (the solid where the field is negative); one owner,
/// <see cref="Surface"/>.
/// <para>
/// <b>Struts</b> (bcc, octet, cubic) are cylinders of diameter <see cref="Thickness"/> round the lattice's lines: each
/// strut of these lattices continues straight through the nodes, so the struts of all cells are infinite lines, and
/// capsules round them are cylinders. The lines of one direction cross a plane square to them on a rectangular grid (or
/// two, one shifted by half a step), so the nearest one comes from rounding. The field is the exact distance to the
/// nearest line minus the radius, so 1-Lipschitz; its bounds are the distance at a box's centre ± the half diagonal,
/// never below −radius, and a box keeps only the line families that can be nearest in it.
/// </para>
/// <para>
/// <b>Sheets</b> (gyroid, Schwarz P, diamond) are |g(kp)| / (√3 k) − t/2 for the TPMS implicit g, k = 2π / cell. The
/// gradient of each g in kp is at most √3 in norm (Cauchy–Schwarz on each component; see <see cref="SheetBound"/>), so
/// the field is 1-Lipschitz and every point within t/2 of the mid-surface g = 0 lies in the sheet: the wall is never
/// thinner than t. It is not a distance: where |∇g| is below √3 the sheet is thicker, up to √3/|∇g| × t on the
/// mid-surface (1.22 t for gyroid and diamond, 1.73 t for Schwarz P), and |f| underestimates the distance by that
/// factor at most near it. Bounds and certified queries only need |f| ≤ distance, so they stay conservative.
/// </para>
/// </summary>
public sealed class LatticeField : Field
{
    /// <summary>The lines of one direction: on a rectangular grid of steps (Su, Sv) along U and V, shifted by (Ou, Ov).</summary>
    readonly record struct Rows(Vector3D U, Vector3D V, double Su, double Sv, double Ou, double Ov);

    const double Pad = 1e-12;
    static readonly double Root2 = Math.Sqrt(2), Root3 = Math.Sqrt(3), Root6 = Math.Sqrt(6);

    readonly Rows[] _rows;    // every line family of a strut lattice
    readonly int[] _active;   // the ones that can be nearest in the box simplified for
    readonly int _sign;       // sheets: the sign g has throughout that box, or 0
    readonly double _k, _norm, _half; // k = 2π / cell; norm = √3 k, the bound on |∇g| in p

    public LatticeField(LatticeKind kind, double cell, double thickness, Surface surface)
        : this(kind, cell, thickness, surface, RowsOf(kind, cell), null, 0, false, 0) { }

    LatticeField(LatticeKind kind, double cell, double thickness, Surface surface, Rows[] rows, int[]? active, int sign, bool complement, double grow)
    {
        (Kind, Cell, Thickness, Surface, _rows, _sign, Complement, Grow) = (kind, cell, thickness, surface, rows, sign, complement, grow);
        _active = active ?? Enumerable.Range(0, rows.Length).ToArray();
        _k = 2 * Math.PI / cell;
        (_norm, _half) = (Root3 * _k, thickness / 2);
    }

    LatticeField With(int[] active, int sign) => new(Kind, Cell, Thickness, Surface, _rows, active, sign, Complement, Grow);

    public LatticeKind Kind { get; }
    public double Cell { get; }
    public double Thickness { get; }
    public Surface Surface { get; }

    public bool Sheets => Kind is LatticeKind.Gyroid or LatticeKind.SchwarzP or LatticeKind.Diamond;

    /// <summary>The voids instead, grown by <see cref="Grow"/>: −f − grow, still 1-Lipschitz (see <see cref="Outside"/>).</summary>
    public bool Complement { get; }
    public double Grow { get; }

    /// <summary>
    /// −f − <paramref name="grow"/>: what a shell of wall <paramref name="grow"/> whose child is max(solid, this) keeps in its
    /// cavity is this lattice again, since −max(s, −f − t) − t = min(−s − t, f).
    /// </summary>
    public LatticeField Outside(double grow) => new(Kind, Cell, Thickness, Surface, _rows, _active, _sign, !Complement, grow);

    /// <summary>What a simplified copy evaluates (strut families kept, sheet sign), for content hashes.</summary>
    public IReadOnlyList<int> Active => _active;
    public int Sign => _sign;

    /// <summary>
    /// |∇g| ≤ √3 in kp, the Lipschitz bound the sheets are scaled by. Gyroid: each component is (cos x, −sin x)·(cos y,
    /// sin z), at most √(cos²y + sin²z) by Cauchy–Schwarz, and the three such bounds sum to 3 in square. Schwarz P:
    /// |(sin x, sin y, sin z)| ≤ √3. Diamond, as sin x cos(y − z) + cos x sin(y + z): |∇g|² = 2 + F with F a quadratic
    /// form in (cos(y − z), sin(y + z)) ∈ [−1, 1]² that is indefinite or negative, so its maximum, 1, lies on the square's
    /// edges. All three reach √3 (at the origin for gyroid and diamond, on the diagonal for P).
    /// </summary>
    public const double SheetBound = 1.7320508075688772;

    /// <summary>
    /// The level g takes on the sheet's surface, c = √3 k t / 2, and the saddle value of g where the voids pinch off:
    /// the sheet closes them for c ≥ that (gyroid √2, Schwarz P and diamond 1).
    /// </summary>
    public static (double Level, double Saddle) Levels(LatticeKind kind, double cell, double thickness) =>
        (Root3 * Math.PI * thickness / cell, kind == LatticeKind.Gyroid ? Root2 : 1);

    /// <summary>The thickness at which the lattice closes its voids: <see cref="Gap"/> reaches 0.</summary>
    public static double MaxThickness(LatticeKind kind, double cell) => kind switch
    {
        LatticeKind.Cubic => cell,
        LatticeKind.Bcc => cell / Root2,
        LatticeKind.Octet => cell / 2,
        _ => Levels(kind, cell, 0).Saddle * cell / (Root3 * Math.PI),
    };

    /// <summary>
    /// The narrowest gap between sheets or struts, or ≤ 0 when the lattice closes its voids. Struts: the closest lines
    /// that do not meet, less the diameter (cubic a, bcc a/√2, octet a/2). Sheets: across the necks of the voids, where
    /// they close as the level c reaches the saddle; for Schwarz P the neck is exact, 2√2 arccos((1 + c)/2) / k, and the
    /// gyroid and diamond follow the same law scaled to their own necks: against a caliper over the cell the gyroid is
    /// within 1% and the diamond up to 4% low, which reports it a little early (LatticeTests).
    /// </summary>
    public static double Gap(LatticeKind kind, double cell, double thickness)
    {
        switch (kind)
        {
            case LatticeKind.Cubic: return cell - thickness;
            case LatticeKind.Bcc: return cell / Root2 - thickness;
            case LatticeKind.Octet: return cell / 2 - thickness;
        }
        var (c, saddle) = Levels(kind, cell, thickness);
        if (c >= saddle) return 0;
        var neck = kind switch { LatticeKind.Gyroid => 0.4107, LatticeKind.SchwarzP => Root2 / Math.PI, _ => 0.320 };
        return neck * cell * Math.Acos((1 + c / saddle) / 2);
    }

    // ---- Struts ----

    static Rows[] RowsOf(LatticeKind kind, double a)
    {
        Vector3D x = new(1, 0, 0), y = new(0, 1, 0), z = new(0, 0, 1);
        switch (kind)
        {
            case LatticeKind.Cubic:
                // Cube edges: lines along each axis through the integer grid.
                return [new(y, z, a, a, 0, 0), new(z, x, a, a, 0, 0), new(x, y, a, a, 0, 0)];
            case LatticeKind.Bcc:
            {
                // Body diagonals (±1, ±1, ±1) through the corners (and so the centres). Seen along (1, 1, 1), the grid
                // aZ³ is the points (a/√2 i, a/√6 j) with i ≡ j mod 2 on u = (1, −1, 0)/√2, v = (1, 1, −2)/√6; the
                // other diagonals are its mirror images.
                var rows = new List<Rows>();
                foreach (var (sx, sy, sz) in new[] { (1, 1, 1), (-1, 1, 1), (1, -1, 1), (1, 1, -1) })
                {
                    var u = new Vector3D(sx, -sy, 0) / Root2;
                    var v = new Vector3D(sx, sy, -2 * sz) / Root6;
                    rows.Add(new(u, v, a * Root2, 2 * a / Root6, 0, 0));
                    rows.Add(new(u, v, a * Root2, 2 * a / Root6, a / Root2, a / Root6));
                }
                return [.. rows];
            }
            case LatticeKind.Octet:
            {
                // Face diagonals: the nearest-neighbour bonds of the face-centred grid. Along (1, 1, 0) its nodes
                // (a/2)(i, j, k), i + j + k even, sit at (a/(2√2) (i − j), a/2 k) with i − j ≡ k mod 2.
                var rows = new List<Rows>();
                foreach (var (u, v) in new[]
                {
                    (new Vector3D(1, -1, 0) / Root2, z), (new Vector3D(1, 1, 0) / Root2, z),
                    (new Vector3D(1, 0, -1) / Root2, y), (new Vector3D(1, 0, 1) / Root2, y),
                    (new Vector3D(0, 1, -1) / Root2, x), (new Vector3D(0, 1, 1) / Root2, x),
                })
                {
                    rows.Add(new(u, v, a / Root2, a, 0, 0));
                    rows.Add(new(u, v, a / Root2, a, a / (2 * Root2), a / 2));
                }
                return [.. rows];
            }
            default:
                return [];
        }
    }

    // Offset of p from the nearest line of one family, in its (U, V) plane.
    static (double Du, double Dv) Across(in Rows r, Point3D p)
    {
        var pu = r.U.X * p.X + r.U.Y * p.Y + r.U.Z * p.Z - r.Ou;
        var pv = r.V.X * p.X + r.V.Y * p.Y + r.V.Z * p.Z - r.Ov;
        return (pu - r.Su * Math.Round(pu / r.Su), pv - r.Sv * Math.Round(pv / r.Sv));
    }

    // The nearest line: squared distance and its family; the first of equals wins.
    (double D2, int Row, double Du, double Dv) Nearest(Point3D p)
    {
        var (best, row, bu, bv) = (double.PositiveInfinity, -1, 0.0, 0.0);
        foreach (var i in _active)
        {
            var (du, dv) = Across(in _rows[i], p);
            var d2 = du * du + dv * dv;
            if (d2 < best) (best, row, bu, bv) = (d2, i, du, dv);
        }
        return (best, row, bu, bv);
    }

    /// <summary>
    /// For a point near the struts: the foot of the nearest line, the line's direction, and the distance to the next
    /// nearest line of another family or row. Null for sheets.
    /// </summary>
    public (Point3D Foot, Vector3D Direction, double Next)? Strut(Point3D p)
    {
        if (Sheets) return null;
        var (_, row, du, dv) = Nearest(p);
        var next = double.PositiveInfinity;
        foreach (var i in _active)
        {
            if (i == row) continue;
            var (u, v) = Across(in _rows[i], p);
            next = Math.Min(next, Math.Sqrt(u * u + v * v));
        }
        // The other lines of the same family are a grid step away at least.
        next = Math.Min(next, Math.Min(_rows[row].Su, _rows[row].Sv) - Math.Sqrt(du * du + dv * dv));
        var r = _rows[row];
        return (p - r.U * du - r.V * dv, r.U.Cross(r.V), next);
    }

    // ---- Sheets ----

    /// <summary>g at kp and its gradient in kp. Value and Sample both come through here, so they agree bit for bit.</summary>
    double G(Point3D p, out Vector3D gradient)
    {
        var (sx, cx) = Math.SinCos(_k * p.X);
        switch (Kind)
        {
            case LatticeKind.Gyroid:
            {
                var (sy, cy) = Math.SinCos(_k * p.Y);
                var (sz, cz) = Math.SinCos(_k * p.Z);
                gradient = new(cx * cy - sz * sx, cy * cz - sx * sy, cz * cx - sy * sz);
                return sx * cy + sy * cz + sz * cx;
            }
            case LatticeKind.SchwarzP:
            {
                var (sy, cy) = Math.SinCos(_k * p.Y);
                var (sz, cz) = Math.SinCos(_k * p.Z);
                gradient = new(-sx, -sy, -sz);
                return cx + cy + cz;
            }
            default:
            {
                // Diamond, sin x sin y sin z + sin x cos y cos z + cos x sin y cos z + cos x cos y sin z, in two terms.
                var (y, z) = (_k * p.Y, _k * p.Z);
                var (sm, cm) = Math.SinCos(y - z);
                var (sp, cp) = Math.SinCos(y + z);
                gradient = new(cx * cm - sx * sp, cx * cp - sx * sm, cx * cp + sx * sm);
                return sx * cm + cx * sp;
            }
        }
    }

    // |g| scaled to a 1-Lipschitz field; in a box where g keeps one sign, that sign instead of the absolute value.
    double Level(double g) => (_sign == 0 ? Math.Abs(g) : _sign * g) / _norm - _half;

    // ---- Field ----

    public override double Value(Point3D p) => Turned(Sheets ? Level(G(p, out _)) : Math.Sqrt(Nearest(p).D2) - _half);

    public override FieldSample Sample(Point3D p)
    {
        if (Sheets)
        {
            var g = G(p, out var gradient);
            var side = _sign != 0 ? _sign : g < 0 ? -1 : 1;
            return new(Turned(Level(g)), Surface.Id, gradient * ((Complement ? -side : side) / Root3));
        }
        var (d2, row, du, dv) = Nearest(p);
        var d = Math.Sqrt(d2);
        var r = _rows[row];
        var normal = d > 0 ? (r.U * du + r.V * dv) / d : r.U;
        return new(Turned(d - _half), Surface.Id, Complement ? -normal : normal);
    }

    double Turned(double f) => Complement ? -f - Grow : f;

    public override FieldSample? SampleSurface(int owner, Point3D p) => owner == Surface.Id ? Sample(p) : null;

    public override Interval Bounds(Box3D box)
    {
        var lipschitz = LipschitzBounds(box);
        Interval f;
        if (!Sheets) f = Complement ? new(lipschitz.Lo, Math.Min(lipschitz.Hi, _half - Grow)) : new(Math.Max(lipschitz.Lo, -_half), lipschitz.Hi); // a distance is never negative
        else
        {
            var g = Range(box);
            var abs = _sign > 0 ? g : _sign < 0 ? g.Negate() : g.Lo >= 0 ? g : g.Hi <= 0 ? g.Negate() : new Interval(0, Math.Max(-g.Lo, g.Hi));
            var level = new Interval(abs.Lo / _norm - _half - Pad, abs.Hi / _norm - _half + Pad);
            f = (Complement ? new Interval(-level.Hi - Grow, -level.Lo - Grow) : level).Intersect(lipschitz);
        }
        return f;
    }

    /// <summary>
    /// Struts: only the families that can hold the nearest line somewhere in the box (a line family whose distance at
    /// the centre, less the half diagonal, exceeds another's plus it is farther everywhere in the box). Sheets: where g
    /// keeps one sign, that sign instead of |g|. Value, owner and gradient are bit for bit those of the whole lattice.
    /// </summary>
    public override Field Simplify(Box3D box)
    {
        if (Sheets)
        {
            if (_sign != 0) return this;
            var g = Range(box);
            return g.Lo > 0 || g.Hi < 0 ? With(_active, g.Lo > 0 ? 1 : -1) : this;
        }
        var (c, h) = (box.Center, HalfDiagonal(box));
        Span<double> d = stackalloc double[_active.Length];
        var nearest = double.PositiveInfinity;
        for (var i = 0; i < _active.Length; i++)
        {
            var (du, dv) = Across(in _rows[_active[i]], c);
            d[i] = Math.Sqrt(du * du + dv * dv);
            nearest = Math.Min(nearest, d[i] + h);
        }
        var kept = new List<int>(_active.Length);
        for (var i = 0; i < _active.Length; i++)
            if (d[i] - h <= nearest + 1e-9) kept.Add(_active[i]);
        return kept.Count == _active.Length ? this : With([.. kept], 0);
    }

    // ---- Interval arithmetic for g over a box: a true enclosure of the computed values ----

    Interval Range(Box3D box)
    {
        // k·x rounds monotonically, so the arguments of a box lie between those of its corners.
        var (x0, x1) = (_k * box.Min.X, _k * box.Max.X);
        var (y0, y1) = (_k * box.Min.Y, _k * box.Max.Y);
        var (z0, z1) = (_k * box.Min.Z, _k * box.Max.Z);
        Interval g;
        switch (Kind)
        {
            case LatticeKind.Gyroid:
                g = Add(Add(Mul(Sin(x0, x1), Cos(y0, y1)), Mul(Sin(y0, y1), Cos(z0, z1))), Mul(Sin(z0, z1), Cos(x0, x1)));
                break;
            case LatticeKind.SchwarzP:
                g = Add(Add(Cos(x0, x1), Cos(y0, y1)), Cos(z0, z1));
                break;
            default:
                g = Add(Mul(Sin(x0, x1), Cos(y0 - z1, y1 - z0)), Mul(Cos(x0, x1), Sin(y0 + z0, y1 + z1)));
                break;
        }
        return new Interval(g.Lo - Pad, g.Hi + Pad);
    }

    static Interval Add(Interval a, Interval b) => new(a.Lo + b.Lo - Pad, a.Hi + b.Hi + Pad);

    static Interval Mul(Interval a, Interval b)
    {
        var (p, q, r, s) = (a.Lo * b.Lo, a.Lo * b.Hi, a.Hi * b.Lo, a.Hi * b.Hi);
        return new(Math.Min(Math.Min(p, q), Math.Min(r, s)) - Pad, Math.Max(Math.Max(p, q), Math.Max(r, s)) + Pad);
    }

    static Interval Sin(double lo, double hi) => Trig(lo, hi, Math.Sin, Math.PI / 2);

    static Interval Cos(double lo, double hi) => Trig(lo, hi, Math.Cos, 0);

    // The range of a sine-like function with its maximum at top + 2πn and minimum half a turn on, padded for rounding.
    static Interval Trig(double lo, double hi, Func<double, double> f, double top)
    {
        if (hi - lo >= 2 * Math.PI) return new(-1, 1);
        var (a, b) = (f(lo), f(hi));
        var max = Hits(lo, hi, top) ? 1 : Math.Max(a, b);
        var min = Hits(lo, hi, top + Math.PI) ? -1 : Math.Min(a, b);
        return new(min - Pad, max + Pad);
    }

    static bool Hits(double lo, double hi, double theta) => theta + 2 * Math.PI * Math.Ceiling((lo - theta) / (2 * Math.PI)) <= hi;
}
