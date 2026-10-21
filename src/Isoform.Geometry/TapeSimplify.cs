using System.Runtime.CompilerServices;
using BasAutomation.Geometry;

namespace Isoform.Geometry;

public sealed partial class Tape
{
    // ---- Interval bounds ----

    /// <summary>Safe bounds of the field over a box, equal to the tree's.</summary>
    public Interval Bounds(Box3D box)
    {
        var n = _code.Length;
        Span<Interval> iv = n <= 256 ? stackalloc Interval[n] : new Interval[n];
        Intervals(box, iv, default);
        return iv[n - 1];
    }

    /// <summary>
    /// Bounds of every register over the box, and in <paramref name="single"/> (when given) each profile's single
    /// edge there or −1, which simplifying then uses instead of deciding again.
    /// </summary>
    void Intervals(Box3D box, Span<Interval> iv, Span<int> single)
    {
        var n = _code.Length;
        // A loop edge's Lipschitz bound needs its value at the centre, which needs its subtrees' gradients there:
        // those registers only, as Run computes them.
        var sampled = _sampled;
        Span<double> centre = sampled is null ? default : n <= 256 ? stackalloc double[n] : new double[n];
        if (sampled is not null)
        {
            Span<FieldSample> s = n <= 128 ? stackalloc FieldSample[n] : new FieldSample[n];
            var c = box.Center;
            for (var i = 0; i < n; i++)
                if (sampled[i]) centre[i] = (s[i] = SampleAt(i, s, c)).Value;
                else if (_code[i].Op == TapeOp.Loop) centre[i] = LoopSample(_loops[_code[i].Data], s[_code[i].A], s[_code[i].B]).Value;
        }
        var b = new BoxData(box);
        for (var i = 0; i < n; i++)
        {
            var ins = _code[i];
            switch (ins.Op)
            {
                case TapeOp.Max:
                {
                    var r = iv[_args[ins.A]];
                    for (var k = 1; k < ins.B; k++) r = Interval.Max(r, iv[_args[ins.A + k]]);
                    iv[i] = r;
                    break;
                }
                case TapeOp.Min:
                {
                    var r = iv[_args[ins.A]];
                    for (var k = 1; k < ins.B; k++) r = Interval.Min(r, iv[_args[ins.A + k]]);
                    iv[i] = r;
                    break;
                }
                case TapeOp.Negate:
                    iv[i] = iv[ins.A].Negate();
                    break;
                case TapeOp.Rounded:
                    iv[i] = RoundedBounds(ins, iv[ins.A], iv[ins.B], in b);
                    break;
                case TapeOp.Shell:
                    var c = iv[ins.A];
                    var t = _shells[ins.Data].Thickness;
                    iv[i] = Interval.Max(iv[ins.B], new Interval(-c.Hi - t, -c.Lo - t));
                    break;
                case TapeOp.Blend:
                    iv[i] = BlendBounds(_blends[ins.Data], iv[ins.A], iv[ins.B]);
                    break;
                case TapeOp.Loop:
                    iv[i] = LoopBounds(_loops[ins.Data], iv[ins.A], iv[ins.B], centre[i], b.H);
                    break;
                case TapeOp.Profile:
                {
                    var pd = _profiles[ins.Data];
                    var k = pd.Field.SingleEdge(box);
                    if (!single.IsEmpty) single[i] = k;
                    if (k >= 0)
                    {
                        // ProfileField.Bounds: the single edge's leaf bounds, or Lipschitz from the centre.
                        ref readonly var e = ref pd.Edges[k];
                        iv[i] = LeafBounds(in _leaves[e.Leaf], e.Op, in b);
                        break;
                    }
                    // Inside a loop edge its value at the centre is already there.
                    var (v, h) = (sampled is not null && sampled[i] ? centre[i] : ProfileValue(pd, b.Centre), b.H);
                    iv[i] = new(v - h, v + h);
                    break;
                }
                case TapeOp.Call:
                    iv[i] = _objects[ins.Data].Bounds(box);
                    break;
                case TapeOp.Plane:
                    iv[i] = PlaneBounds(in _leaves[ins.Data], in b);
                    break;
                default:
                    iv[i] = LeafBounds(in _leaves[ins.Data], ins.Op, in b);
                    break;
            }
        }
    }

    /// <summary>A box's corners, centre and half diagonal, computed once for every instruction.</summary>
    readonly struct BoxData
    {
        public BoxData(Box3D box)
        {
            Box = box;
            (X0, Y0, Z0, X1, Y1, Z1) = (box.Min.X, box.Min.Y, box.Min.Z, box.Max.X, box.Max.Y, box.Max.Z);
            (Cx, Cy, Cz) = ((X0 + X1) / 2, (Y0 + Y1) / 2, (Z0 + Z1) / 2); // as Box3D.Center
        }

        public readonly Box3D Box;
        public readonly double X0, Y0, Z0, X1, Y1, Z1, Cx, Cy, Cz;
        public Point3D Centre => new(Cx, Cy, Cz);

        /// <summary>Half the diagonal, for Lipschitz bounds; planes and exact leaves do without the square root.</summary>
        public double H => Field.HalfDiagonal(Box);
    }

    /// <summary>LeafField.Bounds: exact for planes, spheres and axis-aligned cylinders, Lipschitz otherwise.</summary>
    static Interval LeafBounds(in LeafData l, TapeOp op, in BoxData b)
    {
        if (op == TapeOp.Plane) return PlaneBounds(in l, in b);
        Interval r;
        if (op == TapeOp.Cylinder && l.AlignedAxis >= 0 || op == TapeOp.Sphere)
        {
            static double Gap(double lo, double hi, double o) => Math.Max(0, Math.Max(lo - o, o - hi));
            static double Far(double lo, double hi, double o) => Math.Max(Math.Abs(lo - o), Math.Abs(hi - o));
            var (gx, gy, gz) = (Gap(b.X0, b.X1, l.Ox), Gap(b.Y0, b.Y1, l.Oy), Gap(b.Z0, b.Z1, l.Oz));
            var (fx, fy, fz) = (Far(b.X0, b.X1, l.Ox), Far(b.Y0, b.Y1, l.Oy), Far(b.Z0, b.Z1, l.Oz));
            // The two axes across the cylinder, in the order (axis + 1, axis + 2).
            r = l.AlignedAxis switch
            {
                0 => new(Math.Sqrt(gy * gy + gz * gz) - l.R, Math.Sqrt(fy * fy + fz * fz) - l.R),
                1 => new(Math.Sqrt(gz * gz + gx * gx) - l.R, Math.Sqrt(fz * fz + fx * fx) - l.R),
                2 => new(Math.Sqrt(gx * gx + gy * gy) - l.R, Math.Sqrt(fx * fx + fy * fy) - l.R),
                _ => new(Math.Sqrt(gx * gx + gy * gy + gz * gz) - l.R, Math.Sqrt(fx * fx + fy * fy + fz * fz) - l.R),
            };
        }
        else
        {
            // Lipschitz: the value at the centre, complement included, ± half the diagonal.
            var (v, h) = (Radial(in l, op, b.Cx, b.Cy, b.Cz), b.H);
            return new(v - h, v + h);
        }
        return l.Complement ? r.Negate() : r;
    }

    /// <summary>Linear: exact over the box.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Interval PlaneBounds(in LeafData l, in BoxData b)
    {
        var centre = l.Ax * (b.Cx - l.Ox) + l.Ay * (b.Cy - l.Oy) + l.Az * (b.Cz - l.Oz);
        var spread = (Math.Abs(l.Ax) * (b.X1 - b.X0) + Math.Abs(l.Ay) * (b.Y1 - b.Y0) + Math.Abs(l.Az) * (b.Z1 - b.Z0)) / 2;
        return l.Complement ? new(-(centre + spread), -(centre - spread)) : new(centre - spread, centre + spread);
    }

    Interval RoundedBounds(TapeInstr ins, Interval ia, Interval ib, in BoxData box)
    {
        var r = _rounds[ins.Data];
        var sharp = Interval.Max(ia, ib);
        var denom = 1 - r.C * r.C;
        if (denom < 1e-12) return sharp;
        var (alphaHi, betaHi) = SectorReach(r, ia, ib, denom);
        if (alphaHi < 0 || betaHi < 0) return sharp;
        var (am, bm) = (Math.Max(alphaHi, 0), Math.Max(betaHi, 0));
        var wMax = Math.Sqrt(am * am + bm * bm + 2 * am * bm * Math.Max(r.C, 0));
        var c = box.Centre;
        var v = RoundedValue(ins, LeafValue(ins.A, c), LeafValue(ins.B, c), c);
        var h = box.H;
        return new Interval(sharp.Lo, Math.Max(sharp.Hi, wMax - r.Radius)).Intersect(new(v - h, v + h));
    }

    static (double AlphaHi, double BetaHi) SectorReach(RoundData r, Interval ia, Interval ib, double denom)
    {
        var (aLo, aHi) = (ia.Lo + r.Radius, ia.Hi + r.Radius);
        var (bLo, bHi) = (ib.Lo + r.Radius, ib.Hi + r.Radius);
        return ((aHi - Math.Min(r.C * bLo, r.C * bHi)) / denom, (bHi - Math.Min(r.C * aLo, r.C * aHi)) / denom);
    }

    static Interval BlendBounds(BlendData bl, Interval ia, Interval ib)
    {
        var plain = bl.Union ? Interval.Min(ia, ib) : Interval.Max(ia, ib);
        if (Apart(bl, ia, ib)) return plain;
        return bl.Union ? new(plain.Lo - bl.K / 4, plain.Hi) : new(plain.Lo, plain.Hi + bl.K / 4);
    }

    static bool Apart(BlendData bl, Interval a, Interval b) => a.Lo - b.Hi >= bl.K || b.Lo - a.Hi >= bl.K;

    static Interval LoopBounds(LoopData l, Interval ia, Interval ib, double centre, double h)
    {
        var sharp = Interval.Max(ia, ib);
        if (!l.Round) return Interval.Max(sharp, Chamfer(l, ia, ib));
        var (alphaHi, betaHi) = (ia.Hi + l.Size, ib.Hi + l.Size);
        if (alphaHi < 0 || betaHi < 0) return sharp;
        var wMax = Math.Sqrt(alphaHi * alphaHi + betaHi * betaHi);
        return new Interval(sharp.Lo, Math.Max(sharp.Hi, wMax - l.Size)).Intersect(new(centre - h, centre + h));
    }

    static Interval Chamfer(LoopData l, Interval a, Interval b) => new((a.Lo + b.Lo + l.Size) / Root2, (a.Hi + b.Hi + l.Size) / Root2);

    // ---- Simplification ----

    /// <summary>
    /// The tape for points inside <paramref name="box"/>: instructions that can never decide the result there are
    /// dropped, exactly where <see cref="Field.Simplify"/> drops the tree's nodes. Returns this tape when nothing changes.
    /// </summary>
    public Tape Simplify(Box3D box)
    {
        if (_settled) return this;
        var (n, a) = (_code.Length, _args.Length + 2 * _code.Length);
        Span<Interval> iv = n <= 256 ? stackalloc Interval[n] : new Interval[n];
        Span<int> single = n <= 256 ? stackalloc int[n] : new int[n];
        Intervals(box, iv, single);
        var s = new Simplifier(this, iv, single, box, n <= 256 ? stackalloc int[n] : new int[n],
            n <= 128 ? stackalloc TapeInstr[n] : new TapeInstr[n], a <= 512 ? stackalloc int[a] : new int[a]);
        return s.Visit(n - 1).Changed ? s.Result() : this;
    }

    /// <summary>Mirrors the Simplify of each field class over the tape, one decision per instruction and box.</summary>
    ref struct Simplifier
    {
        readonly Tape _tape;
        readonly ReadOnlySpan<Interval> _iv;
        readonly ReadOnlySpan<int> _single;
        readonly Box3D _box;
        readonly Span<int> _memo; // new register + 1, negated when it changed; 0 when not visited
        readonly Span<TapeInstr> _code;
        readonly Span<int> _args;
        int _count, _argCount;
        List<Field>? _objects;

        /// <param name="code">Room for the new tape: nothing grows, each instruction gives at most one.</param>
        /// <param name="args">Room for its arguments: the old ones plus two for each edge or blend that collapses.</param>
        public Simplifier(Tape tape, ReadOnlySpan<Interval> iv, ReadOnlySpan<int> single, Box3D box, Span<int> memo, Span<TapeInstr> code, Span<int> args)
        {
            _tape = tape;
            _iv = iv;
            _single = single;
            _box = box;
            _memo = memo;
            _memo.Clear();
            _code = code;
            _args = args;
        }

        public readonly Tape Result() =>
            new(_tape, _code[.._count].ToArray(), _args[.._argCount].ToArray(), _objects is null ? [] : _objects.ToArray());

        public readonly TapeInstr Out(int register) => _code[register];

        /// <summary>The instruction's replacement in the new tape, and whether it differs from the original.</summary>
        public (int Register, bool Changed) Visit(int i)
        {
            if (_memo[i] != 0) return (Math.Abs(_memo[i]) - 1, _memo[i] < 0);
            var t = _tape;
            var ins = t._code[i];
            var iv = _iv;
            (int, bool) result;
            switch (ins.Op)
            {
                case TapeOp.Max:
                case TapeOp.Min:
                    result = Keep(t._args.AsSpan(ins.A, ins.B), ins.Op == TapeOp.Max, collapsed: false);
                    break;
                case TapeOp.Negate:
                {
                    var (c, changed) = Visit(ins.A);
                    result = (Emit(ins with { A = c }), changed);
                    break;
                }
                case TapeOp.Rounded:
                {
                    // Outside the fillet sector the edge is the sharp intersection of its two faces.
                    var r = t._rounds[ins.Data];
                    var denom = 1 - r.C * r.C;
                    if (denom < 1e-12 || SectorReach(r, iv[ins.A], iv[ins.B], denom) is var (alphaHi, betaHi) && (alphaHi < 0 || betaHi < 0))
                        result = Keep([ins.A, ins.B], intersection: true, collapsed: true);
                    else
                        result = (Emit(ins with { A = Visit(ins.A).Register, B = Visit(ins.B).Register }), false);
                    break;
                }
                case TapeOp.Shell:
                {
                    // Where the cavity cannot reach, the shell is just its outside.
                    var (c, o) = (iv[ins.A], iv[ins.B]);
                    if (-c.Lo - t._shells[ins.Data].Thickness < o.Lo)
                    {
                        result = (Visit(ins.B).Register, true);
                        break;
                    }
                    var (child, c1) = Visit(ins.A);
                    var (outer, c2) = Visit(ins.B);
                    result = (Emit(ins with { A = child, B = outer }), c1 || c2);
                    break;
                }
                case TapeOp.Blend:
                {
                    var bl = t._blends[ins.Data];
                    if (Apart(bl, iv[ins.A], iv[ins.B]))
                    {
                        result = Keep([ins.A, ins.B], intersection: !bl.Union, collapsed: true);
                        break;
                    }
                    var (a, c1) = Visit(ins.A);
                    var (b, c2) = Visit(ins.B);
                    result = (Emit(ins with { A = a, B = b }), c1 || c2);
                    break;
                }
                case TapeOp.Loop:
                {
                    var l = t._loops[ins.Data];
                    var (ia, ib) = (iv[ins.A], iv[ins.B]);
                    var gone = l.Round ? ia.Hi + l.Size < 0 || ib.Hi + l.Size < 0 : Chamfer(l, ia, ib).Hi < Math.Max(ia.Lo, ib.Lo);
                    if (gone)
                    {
                        result = Keep([ins.A, ins.B], intersection: true, collapsed: true);
                        break;
                    }
                    var cap = Visit(ins.A).Register;
                    var (side, changed) = Visit(ins.B);
                    result = (Emit(ins with { A = cap, B = side }), changed);
                    break;
                }
                case TapeOp.Profile:
                {
                    // ProfileField.Simplify: the leaf of the one edge nearest to the whole box, else the profile.
                    var k = _single[i];
                    if (k < 0) result = (Emit(ins), false);
                    else
                    {
                        ref readonly var e = ref t._profiles[ins.Data].Edges[k];
                        result = (Emit(new(e.Op, 0, 0, e.Leaf)), true);
                    }
                    break;
                }
                case TapeOp.Call:
                {
                    var obj = t._objects[ins.Data];
                    var simple = obj.Simplify(_box);
                    if (ReferenceEquals(simple, obj)) result = (Emit(ins with { Data = Object(obj) }), false);
                    else if (simple is LeafField leaf && t._leafOf.TryGetValue(leaf, out var data)) result = (Emit(new(LeafOp(leaf), 0, 0, data)), true);
                    else result = (Emit(new(TapeOp.Call, 0, 0, Object(simple))), true);
                    break;
                }
                default:
                    result = (Emit(ins), false);
                    break;
            }
            _memo[i] = result.Item2 ? -(result.Item1 + 1) : result.Item1 + 1;
            return result;
        }

        /// <summary>
        /// FieldAlgebra.Keep: drops the children whose bounds show they never decide the intersection or union, and
        /// in a changed intersection the faces a rounded or loop edge beside them already holds. A collapsed edge or
        /// blend builds a new intersection or union of its two operands, so it is changed whatever happens here.
        /// </summary>
        (int, bool) Keep(scoped ReadOnlySpan<int> children, bool intersection, bool collapsed)
        {
            var n = children.Length;
            var limit = intersection ? double.NegativeInfinity : double.PositiveInfinity;
            foreach (var c in children) limit = intersection ? Math.Max(limit, _iv[c].Lo) : Math.Min(limit, _iv[c].Hi);
            Span<int> kept = n <= 64 ? stackalloc int[n] : new int[n];
            var (count, changed) = (0, false);
            foreach (var c in children)
            {
                if (!(intersection ? _iv[c].Hi >= limit : _iv[c].Lo <= limit))
                {
                    changed = true;
                    continue;
                }
                var (r, ch) = Visit(c);
                kept[count++] = r;
                changed |= ch;
            }
            if (!changed) return (EmitNary(intersection, kept[..count]), collapsed);
            if (intersection)
                for (var k = count - 1; k >= 0; k--)
                    if (_code[kept[k]].IsLeaf && Held(kept[..count], _code[kept[k]].Data))
                    {
                        kept[(k + 1)..count].CopyTo(kept[k..]);
                        count--;
                    }
            return (count == 1 ? kept[0] : EmitNary(intersection, kept[..count]), true);
        }

        readonly bool Held(ReadOnlySpan<int> kept, int leaf)
        {
            foreach (var f in kept)
            {
                var ins = _code[f];
                if (ins.Op == TapeOp.Rounded && (_code[ins.A].Data == leaf || _code[ins.B].Data == leaf)
                    || ins.Op == TapeOp.Loop && _code[ins.A].Data == leaf) return true;
            }
            return false;
        }

        int EmitNary(bool intersection, scoped ReadOnlySpan<int> registers)
        {
            registers.CopyTo(_args[_argCount..]);
            _argCount += registers.Length;
            return Emit(new(intersection ? TapeOp.Max : TapeOp.Min, _argCount - registers.Length, registers.Length, 0));
        }

        int Emit(TapeInstr ins)
        {
            _code[_count] = ins;
            return _count++;
        }

        int Object(Field f)
        {
            (_objects ??= []).Add(f);
            return _objects.Count - 1;
        }
    }

    // ---- Single patch ----

    /// <summary>The tree's SinglePatch: the smallest curvature radius if one analytic surface is all the box holds.</summary>
    public double? SinglePatch(Box3D box) => Patch(_code.Length - 1, box);

    double? Patch(int i, Box3D box)
    {
        var ins = _code[i];
        switch (ins.Op)
        {
            case TapeOp.Negate:
                return Patch(ins.A, box);
            case TapeOp.Rounded:
            {
                // A box entirely inside the fillet sector holds only the fillet.
                var r = _rounds[ins.Data];
                var denom = 1 - r.C * r.C;
                if (denom < 1e-12) return null;
                var bd = new BoxData(box);
                var (a, b) = (_leaves[_code[ins.A].Data], _leaves[_code[ins.B].Data]);
                var (ia, ib) = (LeafBounds(in a, _code[ins.A].Op, in bd), LeafBounds(in b, _code[ins.B].Op, in bd));
                var (aLo, aHi, bLo, bHi) = (ia.Lo + r.Radius, ia.Hi + r.Radius, ib.Lo + r.Radius, ib.Hi + r.Radius);
                var alphaLo = (aLo - Math.Max(r.C * bLo, r.C * bHi)) / denom;
                var betaLo = (bLo - Math.Max(r.C * aLo, r.C * aHi)) / denom;
                if (alphaLo < 1e-9 || betaLo < 1e-9) return null;
                var cylinder = a.Surface.Kind == SurfaceKind.Cylinder ? a.R : b.Surface.Kind == SurfaceKind.Cylinder ? b.R : double.PositiveInfinity;
                return Math.Min(r.Radius, cylinder);
            }
            case TapeOp.Shell:
            {
                // Where only the inner surface can win, it is the offset of a single child surface or unknown.
                var n = _code.Length;
                Span<Interval> iv = n <= 256 ? stackalloc Interval[n] : new Interval[n];
                Span<int> single = n <= 256 ? stackalloc int[n] : new int[n];
                Intervals(box, iv, single);
                var t = _shells[ins.Data].Thickness;
                if (-iv[ins.A].Hi - t <= iv[ins.B].Hi + 1e-9) return null;
                var a = _args.Length + 2 * n;
                var s = new Simplifier(this, iv, single, box, n <= 256 ? stackalloc int[n] : new int[n],
                    n <= 128 ? stackalloc TapeInstr[n] : new TapeInstr[n], a <= 512 ? stackalloc int[a] : new int[a]);
                var leaf = s.Out(s.Visit(ins.A).Register);
                if (!leaf.IsLeaf) return null;
                var l = _leaves[leaf.Data];
                if (leaf.Op == TapeOp.Plane) return double.PositiveInfinity;
                var radius = l.Complement ? l.R + t : l.R - t;
                return radius > 0 ? radius : null;
            }
            case TapeOp.Call:
                return _objects[ins.Data].SinglePatch(box);
            case TapeOp.Plane:
                return double.PositiveInfinity;
            case TapeOp.Torus:
            {
                var l = _leaves[ins.Data];
                var r = Math.Min(l.Minor, Math.Abs(l.R - l.Minor));
                return r > 1e-9 ? r : null;
            }
            case TapeOp.Cone:
            {
                // Curvature radius ρ / cos α across the cone: unbounded towards the apex.
                var l = _leaves[ins.Data];
                var axial = l.Axis.Dot(box.Center - l.Origin);
                var rho = ((box.Center - l.Origin) - l.Axis * axial).Length - Field.HalfDiagonal(box);
                return rho > 1e-9 ? rho / l.Cos : null;
            }
            case TapeOp.Cylinder:
            case TapeOp.Sphere:
                return _leaves[ins.Data].R;
            default:
                return null;
        }
    }

    // ---- One surface alone ----

    /// <summary>The field of one owner surface alone, as the tree's SampleSurface gives it; null if it is not in this tape.</summary>
    public FieldSample? SampleSurface(int owner, Point3D p)
    {
        var n = _code.Length;
        Span<FieldSample> s = n <= 128 ? stackalloc FieldSample[n] : new FieldSample[n];
        Run(p, s);
        return Surface(n - 1, owner, s, p);
    }

    FieldSample? Surface(int i, int owner, ReadOnlySpan<FieldSample> s, Point3D p)
    {
        var ins = _code[i];
        switch (ins.Op)
        {
            case TapeOp.Max:
            case TapeOp.Min:
                for (var k = 0; k < ins.B; k++)
                    if (Surface(_args[ins.A + k], owner, s, p) is { } found) return found;
                return null;
            case TapeOp.Negate:
                return Surface(ins.A, owner, s, p) is { } n ? new(-n.Value, n.Owner, -n.Gradient) : null;
            case TapeOp.Rounded:
            {
                var r = _rounds[ins.Data];
                if (owner != r.Fillet) return Surface(ins.A, owner, s, p) ?? Surface(ins.B, owner, s, p);
                var (sa, sb) = (s[ins.A], s[ins.B]);
                var c = sa.Gradient.Dot(sb.Gradient);
                var denom = 1 - c * c;
                var (a, b) = (sa.Value + r.Radius, sb.Value + r.Radius);
                return Rounded(r, sa, sb, (a - b * c) / denom, (b - a * c) / denom);
            }
            case TapeOp.Shell:
            {
                var sh = _shells[ins.Data];
                if (!sh.OuterOf.TryGetValue(owner, out var outer)) return Surface(ins.B, owner, s, p);
                return Surface(ins.A, outer, s, p) is { } inner ? new(-inner.Value - sh.Thickness, owner, -inner.Gradient) : null;
            }
            case TapeOp.Blend:
                return owner == _blends[ins.Data].Id ? s[i] : Surface(ins.A, owner, s, p) ?? Surface(ins.B, owner, s, p);
            case TapeOp.Loop:
            {
                var l = _loops[ins.Data];
                if (!l.SideOf.TryGetValue(owner, out var side)) return Surface(ins.A, owner, s, p) ?? Surface(ins.B, owner, s, p);
                return Surface(ins.B, side, s, p) is { } sb ? Edge(l, s[ins.A], sb, owner, always: true) : null;
            }
            case TapeOp.Profile:
                return ProfileSurface(_profiles[ins.Data], owner, p);
            case TapeOp.Call:
                return _objects[ins.Data].SampleSurface(owner, p);
            default:
                return _leaves[ins.Data].Id == owner ? s[i] : null;
        }
    }

    /// <summary>
    /// Appends the owner surfaces in the tape, depth first in tree order, as the mesher lists a tree's: a shell gives
    /// its outside only, a profile all its leaves; with <paramref name="cavity"/> the inner faces of shells holding a
    /// lattice instead.
    /// </summary>
    public void Surfaces(List<int> owners, bool cavity = false) => Surfaces(_code.Length - 1, owners, cavity);

    void Surfaces(int i, List<int> owners, bool cavity)
    {
        void Add(int id)
        {
            if (!cavity && !owners.Contains(id)) owners.Add(id);
        }
        var ins = _code[i];
        switch (ins.Op)
        {
            case TapeOp.Max:
            case TapeOp.Min:
                for (var k = 0; k < ins.B; k++) Surfaces(_args[ins.A + k], owners, cavity);
                break;
            case TapeOp.Negate:
                Surfaces(ins.A, owners, cavity);
                break;
            case TapeOp.Shell when !cavity:
                Surfaces(ins.B, owners, false);
                break;
            case TapeOp.Shell:
                var sh = _shells[ins.Data];
                if (!sh.InnerOf.Any(kv => kv.Key == kv.Value)) break;
                var child = new List<int>();
                Surfaces(ins.A, child, false);
                foreach (var o in child)
                    if (sh.InnerOf.TryGetValue(o, out var inner) && !owners.Contains(inner)) owners.Add(inner);
                break;
            case TapeOp.Rounded:
            case TapeOp.Blend:
            case TapeOp.Loop:
                Surfaces(ins.A, owners, cavity);
                Surfaces(ins.B, owners, cavity);
                break;
            case TapeOp.Profile:
                foreach (var (data, _) in _profiles[ins.Data].Leaves) Add(_leaves[data].Id);
                break;
            case TapeOp.Call:
                if (_objects[ins.Data] is LatticeField l) Add(l.Surface.Id);
                break;
            default:
                Add(_leaves[ins.Data].Id);
                break;
        }
    }
}
