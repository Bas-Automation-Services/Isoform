using System.Runtime.CompilerServices;
using BasAutomation.Geometry;

namespace Isoform.Geometry;

/// <summary>Instruction codes of a <see cref="Tape"/>. The five leaf kinds come first.</summary>
enum TapeOp : byte { Plane, Cylinder, Sphere, Cone, Torus, Max, Min, Negate, Rounded, Shell, Blend, Loop, Call }

/// <summary>
/// One instruction; its result is the register with the instruction's own index. A and B are operand registers,
/// except for Max and Min, whose B operands start at A in the argument list. Data indexes the table of the
/// instruction's kind: leaves, rounded edges, shells, blends, loops or called objects.
/// </summary>
readonly record struct TapeInstr(TapeOp Op, int A, int B, int Data)
{
    public bool IsLeaf => Op <= TapeOp.Torus;
}

/// <summary>A leaf surface unpacked into plain numbers. One per <see cref="LeafField"/> object: its index is the leaf's identity.</summary>
readonly struct LeafData
{
    public LeafData(LeafField leaf)
    {
        var s = leaf.Surface;
        (Field, Surface, Complement, Id, R, Minor) = (leaf, s, leaf.Complement, s.Id, s.Radius, s.Minor);
        (Ox, Oy, Oz, Ax, Ay, Az) = (s.Origin.X, s.Origin.Y, s.Origin.Z, s.Axis.X, s.Axis.Y, s.Axis.Z);
        (Cos, Sin) = (Math.Cos(s.Angle), Math.Sin(s.Angle));
        var a = s.Axis;
        AlignedAxis = s.Kind != SurfaceKind.Cylinder ? -1
            : a.Y == 0 && a.Z == 0 ? 0 : a.X == 0 && a.Z == 0 ? 1 : a.X == 0 && a.Y == 0 ? 2 : -1;
    }

    public readonly LeafField Field;
    public readonly Surface Surface;
    public readonly bool Complement;
    public readonly int Id;
    public readonly double Ox, Oy, Oz, Ax, Ay, Az, R, Minor, Cos, Sin;

    /// <summary>The axis of an axis-aligned cylinder, whose bounds are exact; otherwise −1.</summary>
    public readonly int AlignedAxis;

    public Point3D Origin => new(Ox, Oy, Oz);
    public Vector3D Axis => new(Ax, Ay, Az);
}

readonly record struct RoundData(double Radius, double C, int Fillet);

sealed record ShellData(double Thickness, IReadOnlyDictionary<int, int> InnerOf, Dictionary<int, int> OuterOf);

readonly record struct BlendData(double K, int Id, bool Union);

sealed record LoopData(double Size, bool Round, IReadOnlyDictionary<int, Surface> FaceOf, int Fallback, Dictionary<int, int> SideOf);

/// <summary>
/// A field tree compiled to a flat instruction list in postfix order, evaluated in one loop over registers without
/// virtual calls or allocation. Nodes shared in the tree are evaluated once. Every result is bit-identical to the
/// tree's: each instruction performs the same floating-point operations in the same order as its node.
/// <para>
/// Leaves, intersections, unions, complements, rounded edges, shells, blends and loop edges are native; other
/// fields (profiles) are kept as calls to the object. Simplifying for a box gives a shorter tape that makes the same
/// decisions as <see cref="Field.Simplify"/>, so bounds, patches and meshes come out exactly as from the tree.
/// </para>
/// </summary>
public sealed partial class Tape
{
    static readonly double Root2 = Math.Sqrt(2);

    readonly TapeInstr[] _code;
    readonly int[] _args;
    readonly Field[] _objects;

    // Shared by a tape and every tape simplified from it.
    readonly LeafData[] _leaves;
    readonly RoundData[] _rounds;
    readonly ShellData[] _shells;
    readonly BlendData[] _blends;
    readonly LoopData[] _loops;
    readonly Dictionary<Field, int> _leafOf;

    // Instructions a loop edge needs with owner and gradient even when only the value is asked: it has no value
    // of its own without them. Null when there is none.
    readonly bool[]? _sampled;

    // Only leaves and complements: nothing to decide, so simplifying changes nothing.
    readonly bool _settled;

    Tape(TapeInstr[] code, int[] args, Field[] objects, LeafData[] leaves, RoundData[] rounds, ShellData[] shells,
        BlendData[] blends, LoopData[] loops, Dictionary<Field, int> leafOf)
    {
        (_code, _args, _objects) = (code, args, objects);
        (_leaves, _rounds, _shells, _blends, _loops, _leafOf) = (leaves, rounds, shells, blends, loops, leafOf);
        _sampled = Sampled(code, args);
        _settled = true;
        foreach (var i in code) _settled &= i.IsLeaf || i.Op == TapeOp.Negate;
    }

    Tape(Tape from, TapeInstr[] code, int[] args, Field[] objects)
        : this(code, args, objects, from._leaves, from._rounds, from._shells, from._blends, from._loops, from._leafOf) { }

    /// <summary>Number of instructions.</summary>
    public int Length => _code.Length;

    /// <summary>The tree's own field when the tape is one leaf or one called object: alone, it is quicker as itself.</summary>
    public Field? Single => _code is not [var only] ? null : only.IsLeaf ? _leaves[only.Data].Field : only.Op == TapeOp.Call ? _objects[only.Data] : null;

    /// <summary>Instructions that call a field object instead of being evaluated natively.</summary>
    public int Calls => _code.Count(i => i.Op == TapeOp.Call);

    /// <summary>Instruction counts by kind, e.g. "Plane 40, Max 12, Call 1".</summary>
    public override string ToString() =>
        string.Join(", ", _code.GroupBy(i => i.Op).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}"));

    /// <summary>Compiles a field tree. The tree is not changed and stays the reference for every result.</summary>
    public static Tape Compile(Field root)
    {
        var b = new Builder();
        b.Emit(root);
        return new([.. b.Code], [.. b.Args], [.. b.Objects], [.. b.Leaves], [.. b.Rounds], [.. b.Shells], [.. b.Blends], [.. b.Loops], b.LeafOf);
    }

    sealed class Builder
    {
        readonly Dictionary<Field, int> _register = new(ReferenceEqualityComparer.Instance);
        public readonly List<TapeInstr> Code = [];
        public readonly List<int> Args = [];
        public readonly List<Field> Objects = [];
        public readonly List<LeafData> Leaves = [];
        public readonly List<RoundData> Rounds = [];
        public readonly List<ShellData> Shells = [];
        public readonly List<BlendData> Blends = [];
        public readonly List<LoopData> Loops = [];
        public readonly Dictionary<Field, int> LeafOf = new(ReferenceEqualityComparer.Instance);

        public int Emit(Field f)
        {
            if (_register.TryGetValue(f, out var r)) return r;
            var ins = f switch
            {
                LeafField l => new TapeInstr(LeafOp(l), 0, 0, Leaf(l)),
                MaxField m => Nary(TapeOp.Max, m.Children),
                MinField m => Nary(TapeOp.Min, m.Children),
                NegateField n => new(TapeOp.Negate, Emit(n.Child), 0, 0),
                // The rounded edge's constant gA·gB, computed as its constructor does.
                RoundedEdgeField e => new(TapeOp.Rounded, Emit(e.A), Emit(e.B), Add(Rounds,
                    new RoundData(e.Radius, e.A.Sample(new Point3D(0, 0, 0)).Gradient.Dot(e.B.Sample(new Point3D(0, 0, 0)).Gradient), e.Fillet.Id))),
                ShellField s => new(TapeOp.Shell, Emit(s.Child), Emit(s.Outer), Add(Shells,
                    new ShellData(s.Thickness, s.InnerOf, s.InnerOf.ToDictionary(kv => kv.Value, kv => kv.Key)))),
                BlendField b => new(TapeOp.Blend, Emit(b.A), Emit(b.B), Add(Blends, new BlendData(b.K, b.Blend.Id, b.Union))),
                LoopEdgeField l => new(TapeOp.Loop, Emit(l.Cap), Emit(l.Side), Add(Loops,
                    new LoopData(l.Size, l.Round, l.FaceOf, l.FaceOf.Values.First().Id, l.FaceOf.ToDictionary(kv => kv.Value.Id, kv => kv.Key)))),
                _ => Call(f),
            };
            Code.Add(ins);
            return _register[f] = Code.Count - 1;
        }

        TapeInstr Nary(TapeOp op, IReadOnlyList<Field> children)
        {
            // Children first: a nested intersection appends its own arguments before these.
            var regs = children.Select(Emit).ToArray();
            Args.AddRange(regs);
            return new(op, Args.Count - regs.Length, regs.Length, 0);
        }

        TapeInstr Call(Field f)
        {
            // A profile simplifies to one of its leaves: have them ready, so that result is native again.
            if (f is ProfileField p)
                foreach (var l in p.Leaves) Leaf(l);
            return new(TapeOp.Call, 0, 0, Add(Objects, f));
        }

        int Leaf(LeafField l)
        {
            if (!LeafOf.TryGetValue(l, out var i)) LeafOf[l] = i = Add(Leaves, new LeafData(l));
            return i;
        }

        static int Add<T>(List<T> list, T item)
        {
            list.Add(item);
            return list.Count - 1;
        }
    }

    static TapeOp LeafOp(LeafField l) => l.Surface.Kind switch
    {
        SurfaceKind.Plane => TapeOp.Plane,
        SurfaceKind.Cylinder => TapeOp.Cylinder,
        SurfaceKind.Sphere => TapeOp.Sphere,
        SurfaceKind.Cone => TapeOp.Cone,
        SurfaceKind.Torus => TapeOp.Torus,
        var k => throw new InvalidOperationException($"{k} surfaces have no standalone distance."),
    };

    static bool[]? Sampled(TapeInstr[] code, int[] args)
    {
        bool[]? s = null;
        // Operands come before their users, so one backward pass reaches every descendant.
        for (var i = code.Length - 1; i >= 0; i--)
        {
            var ins = code[i];
            if (ins.Op == TapeOp.Loop) (s ??= new bool[code.Length])[ins.A] = s[ins.B] = true;
            if (s is null || !s[i] || ins.IsLeaf || ins.Op == TapeOp.Call) continue;
            if (ins.Op is TapeOp.Max or TapeOp.Min)
                for (var k = 0; k < ins.B; k++) s[args[ins.A + k]] = true;
            else
                s[ins.A] = s[ins.B] = true;
        }
        return s;
    }

    // ---- Point evaluation ----

    /// <summary>The field value at <paramref name="p"/>.</summary>
    public double Value(Point3D p)
    {
        var n = _code.Length;
        Span<double> v = n <= 512 ? stackalloc double[n] : new double[n];
        Run(p, v, _sampled is null ? default : n <= 128 ? stackalloc FieldSample[n] : new FieldSample[n]);
        return v[n - 1];
    }

    /// <summary>Value, owner and gradient at <paramref name="p"/>.</summary>
    public FieldSample Sample(Point3D p)
    {
        var n = _code.Length;
        Span<FieldSample> s = n <= 128 ? stackalloc FieldSample[n] : new FieldSample[n];
        Run(p, s);
        return s[n - 1];
    }

    /// <summary>Every register's value at p; the loop-edge subtrees, if any, also get their full samples in s.</summary>
    void Run(Point3D p, Span<double> v, Span<FieldSample> s)
    {
        var (x, y, z) = (p.X, p.Y, p.Z);
        var (code, leaves, args, sampled) = (_code, _leaves, _args, _sampled);
        for (var i = 0; i < code.Length; i++)
        {
            var ins = code[i];
            if (sampled is not null && sampled[i])
            {
                s[i] = SampleAt(i, s, p);
                v[i] = s[i].Value;
                continue;
            }
            switch (ins.Op)
            {
                case TapeOp.Plane:
                    v[i] = Plane(in leaves[ins.Data], x, y, z);
                    break;
                case TapeOp.Cylinder:
                case TapeOp.Cone:
                case TapeOp.Torus:
                    v[i] = Radial(in leaves[ins.Data], ins.Op, x, y, z);
                    break;
                case TapeOp.Sphere:
                    v[i] = Sphere(in leaves[ins.Data], x, y, z);
                    break;
                case TapeOp.Max:
                {
                    var best = v[args[ins.A]];
                    for (var k = 1; k < ins.B; k++)
                    {
                        var c = v[args[ins.A + k]];
                        if (c > best) best = c;
                    }
                    v[i] = best;
                    break;
                }
                case TapeOp.Min:
                {
                    var best = v[args[ins.A]];
                    for (var k = 1; k < ins.B; k++)
                    {
                        var c = v[args[ins.A + k]];
                        if (c < best) best = c;
                    }
                    v[i] = best;
                    break;
                }
                case TapeOp.Negate:
                    v[i] = -v[ins.A];
                    break;
                default:
                    v[i] = Composite(ins, v, s, p);
                    break;
            }
        }
    }

    /// <summary>Every register's sample at p.</summary>
    void Run(Point3D p, Span<FieldSample> s)
    {
        var (code, leaves) = (_code, _leaves);
        for (var i = 0; i < code.Length; i++)
        {
            var ins = code[i];
            if (ins.Op != TapeOp.Plane)
            {
                s[i] = SampleAt(i, s, p);
                continue;
            }
            // Planes, the most common instruction, without the two calls down to LeafSample.
            ref readonly var l = ref leaves[ins.Data];
            s[i] = new(Plane(in l, p.X, p.Y, p.Z), l.Id, l.Complement ? -l.Axis : l.Axis);
        }
    }

    double Composite(TapeInstr ins, ReadOnlySpan<double> v, ReadOnlySpan<FieldSample> s, Point3D p) => ins.Op switch
    {
        TapeOp.Rounded => RoundedValue(ins, v[ins.A], v[ins.B], p),
        TapeOp.Shell => ShellValue(ins, v[ins.A], v[ins.B]),
        TapeOp.Blend => BlendValue(_blends[ins.Data], v[ins.A], v[ins.B]),
        TapeOp.Loop => LoopSample(_loops[ins.Data], s[ins.A], s[ins.B]).Value,
        _ => _objects[ins.Data].Value(p),
    };

    FieldSample SampleAt(int i, ReadOnlySpan<FieldSample> s, Point3D p)
    {
        var ins = _code[i];
        switch (ins.Op)
        {
            case TapeOp.Max:
            case TapeOp.Min:
            {
                // Pick the winner by index: one copy of the sample instead of one per child.
                var best = _args[ins.A];
                for (var k = 1; k < ins.B; k++)
                {
                    var c = _args[ins.A + k];
                    if (ins.Op == TapeOp.Max ? s[c].Value > s[best].Value : s[c].Value < s[best].Value) best = c;
                }
                return s[best];
            }
            case TapeOp.Negate:
                var n = s[ins.A];
                return new(-n.Value, n.Owner, -n.Gradient);
            case TapeOp.Rounded:
                return RoundedSample(_rounds[ins.Data], s[ins.A], s[ins.B]);
            case TapeOp.Shell:
                return ShellSample(ins, s[ins.A], s[ins.B]);
            case TapeOp.Blend:
                return BlendSample(_blends[ins.Data], s[ins.A], s[ins.B]);
            case TapeOp.Loop:
                return LoopSample(_loops[ins.Data], s[ins.A], s[ins.B]);
            case TapeOp.Call:
                return _objects[ins.Data].Sample(p);
            default:
                return LeafSample(in _leaves[ins.Data], ins.Op, p);
        }
    }

    // ---- Leaves: the operations of Surface.Distance and Surface.Gradient, in the same order ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static double Plane(in LeafData l, double x, double y, double z)
    {
        var d = l.Ax * (x - l.Ox) + l.Ay * (y - l.Oy) + l.Az * (z - l.Oz);
        return l.Complement ? -d : d;
    }

    static double Sphere(in LeafData l, double x, double y, double z)
    {
        double vx = x - l.Ox, vy = y - l.Oy, vz = z - l.Oz;
        var d = Math.Sqrt(vx * vx + vy * vy + vz * vz) - l.R;
        return l.Complement ? -d : d;
    }

    /// <summary>Cylinder, cone and torus: all measured from the distance to the axis and the height along it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static double Radial(in LeafData l, TapeOp op, double x, double y, double z)
    {
        double vx = x - l.Ox, vy = y - l.Oy, vz = z - l.Oz;
        var axial = vx * l.Ax + vy * l.Ay + vz * l.Az;
        double rx = vx - l.Ax * axial, ry = vy - l.Ay * axial, rz = vz - l.Az * axial;
        var len = Math.Sqrt(rx * rx + ry * ry + rz * rz);
        double d;
        if (op == TapeOp.Cylinder) d = len - l.R;
        else if (op == TapeOp.Cone) d = len * l.Cos - axial * l.Sin;
        else
        {
            var rho = len - l.R;
            d = Math.Sqrt(rho * rho + axial * axial) - l.Minor;
        }
        return l.Complement ? -d : d;
    }

    static FieldSample LeafSample(in LeafData l, TapeOp op, Point3D p)
    {
        double d;
        Vector3D g;
        if (op == TapeOp.Plane)
        {
            d = Plane(in l, p.X, p.Y, p.Z);
            return new(d, l.Id, l.Complement ? -l.Axis : l.Axis);
        }
        var v = p - l.Origin;
        if (op == TapeOp.Sphere)
        {
            var len = v.Length;
            d = len - l.R;
            g = len > 0 ? v / len : l.Axis;
        }
        else
        {
            var axial = v.Dot(l.Axis);
            var r = v - l.Axis * axial;
            var len = r.Length;
            var (rho, tube) = (len - l.R, 0.0);
            if (op == TapeOp.Cylinder) d = rho;
            else if (op == TapeOp.Cone) d = len * l.Cos - axial * l.Sin;
            else d = (tube = Math.Sqrt(rho * rho + axial * axial)) - l.Minor;
            if (!(len > 0)) g = l.Surface.Gradient(p); // on the axis: the surface picks a direction
            else
            {
                var u = r / len;
                g = op == TapeOp.Cylinder ? u
                    : op == TapeOp.Cone ? u * l.Cos - l.Axis * l.Sin
                    : tube > 0 ? (u * rho + l.Axis * axial) / tube : l.Axis;
            }
        }
        return l.Complement ? new(-d, l.Id, -g) : new(d, l.Id, g);
    }

    double LeafValue(int register, Point3D p)
    {
        var ins = _code[register];
        ref readonly var l = ref _leaves[ins.Data];
        return ins.Op switch
        {
            TapeOp.Plane => Plane(in l, p.X, p.Y, p.Z),
            TapeOp.Sphere => Sphere(in l, p.X, p.Y, p.Z),
            _ => Radial(in l, ins.Op, p.X, p.Y, p.Z),
        };
    }

    Vector3D LeafGradient(int register, Point3D p)
    {
        var ins = _code[register];
        ref readonly var l = ref _leaves[ins.Data];
        if (ins.Op != TapeOp.Plane) return LeafSample(in l, ins.Op, p).Gradient;
        return l.Complement ? -l.Axis : l.Axis;
    }

    // ---- Combinations: the operations of the field classes, in the same order ----

    /// <summary>
    /// RoundedSample(...).Value from the leaf values: a rounded edge has no value without its leaves' gradients
    /// (RoundedEdgeField.Value is Sample(p).Value).
    /// </summary>
    double RoundedValue(TapeInstr ins, double va, double vb, Point3D p)
    {
        var r = _rounds[ins.Data];
        var sharp = va >= vb ? va : vb;
        var (ga, gb) = (LeafGradient(ins.A, p), LeafGradient(ins.B, p));
        var c = ga.Dot(gb);
        var denom = 1 - c * c;
        if (denom < 1e-12) return sharp;
        var a = va + r.Radius;
        var b = vb + r.Radius;
        var alpha = (a - b * c) / denom;
        var beta = (b - a * c) / denom;
        if (alpha < 0 || beta < 0) return sharp;
        var len = (ga * alpha + gb * beta).Length;
        return len > 0 ? len - r.Radius : -r.Radius;
    }

    static FieldSample RoundedSample(RoundData r, FieldSample sa, FieldSample sb)
    {
        var sharp = sa.Value >= sb.Value ? sa : sb;
        var c = sa.Gradient.Dot(sb.Gradient);
        var denom = 1 - c * c;
        if (denom < 1e-12) return sharp;
        var a = sa.Value + r.Radius;
        var b = sb.Value + r.Radius;
        var alpha = (a - b * c) / denom;
        var beta = (b - a * c) / denom;
        if (alpha < 0 || beta < 0) return sharp;
        return Rounded(r, sa, sb, alpha, beta);
    }

    static FieldSample Rounded(RoundData r, FieldSample sa, FieldSample sb, double alpha, double beta)
    {
        var w = sa.Gradient * alpha + sb.Gradient * beta;
        var len = w.Length;
        return len > 0 ? new(len - r.Radius, r.Fillet, w / len) : new(-r.Radius, r.Fillet, sa.Gradient);
    }

    // A shell whose outer is its child reads the same register twice.
    double ShellValue(TapeInstr ins, double child, double outer) => Math.Max(outer, -child - _shells[ins.Data].Thickness);

    FieldSample ShellSample(TapeInstr ins, FieldSample c, FieldSample o)
    {
        var sh = _shells[ins.Data];
        var inner = -c.Value - sh.Thickness;
        return o.Value >= inner ? o : new(inner, sh.InnerOf[c.Owner], -c.Gradient);
    }

    static double BlendValue(BlendData bl, double a, double b)
    {
        var h = Math.Max(bl.K - Math.Abs(a - b), 0) / bl.K;
        if (h == 0) return bl.Union ? (b < a ? b : a) : (b > a ? b : a);
        return Blended(bl, a, b, h);
    }

    static FieldSample BlendSample(BlendData bl, FieldSample sa, FieldSample sb)
    {
        var diff = sa.Value - sb.Value;
        var h = Math.Max(bl.K - Math.Abs(diff), 0) / bl.K;
        if (h == 0) return bl.Union ? (sb.Value < sa.Value ? sb : sa) : (sb.Value > sa.Value ? sb : sa);
        var wa = bl.Union ? 0.5 - diff / (2 * bl.K) : 0.5 + diff / (2 * bl.K);
        return new(Blended(bl, sa.Value, sb.Value, h), bl.Id, sa.Gradient * wa + sb.Gradient * (1 - wa));
    }

    static double Blended(BlendData bl, double a, double b, double h) =>
        bl.Union ? Math.Min(a, b) - h * h * bl.K / 4 : Math.Max(a, b) + h * h * bl.K / 4;

    static FieldSample LoopSample(LoopData l, FieldSample sa, FieldSample sb)
    {
        var sharp = sa.Value >= sb.Value ? sa : sb;
        var face = l.FaceOf.TryGetValue(sb.Owner, out var f) ? f.Id : l.Fallback;
        return Edge(l, sa, sb, face, always: false) is { } e && e.Value > sharp.Value ? e : sharp;
    }

    static FieldSample? Edge(LoopData l, FieldSample sa, FieldSample sb, int owner, bool always)
    {
        if (!l.Round)
            return new((sa.Value + sb.Value + l.Size) / Root2, owner, (sa.Gradient + sb.Gradient) / Root2);
        var (alpha, beta) = (sa.Value + l.Size, sb.Value + l.Size);
        if (!always && (alpha < 0 || beta < 0)) return null;
        var w = sa.Gradient * alpha + sb.Gradient * beta;
        var len = w.Length;
        return len > 0 ? new(len - l.Size, owner, w / len) : new(-l.Size, owner, sa.Gradient);
    }
}
