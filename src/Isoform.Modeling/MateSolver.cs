using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Geometry;

namespace Isoform.Modeling;

/// <summary>
/// One mate in the solver's terms: two faces, each on a top-level component and given in that component's
/// coordinates as a point and a direction (a flat face's outward normal, or an axis).
/// </summary>
public sealed record MateTerm(MateKind Kind, int ComponentA, Point3D PointA, Vector3D DirectionA, int ComponentB, Point3D PointB, Vector3D DirectionB, double Offset);

/// <summary>
/// Rigid poses of the top-level components that make every mate hold: damped Gauss-Newton on a small rotation and
/// shift per free component, applied and folded back into the poses each step. The step is a least-squares
/// pseudo-inverse, so a direction no mate fixes stays where it started, and the rank left over is the number of free
/// degrees of freedom.
/// </summary>
public static class MateSolver
{
    public sealed record Solution(IReadOnlyList<Frame> Poses, double Residual, int FreeDegrees);

    public static Solution Solve(IReadOnlyList<Frame> start, IReadOnlyList<bool> grounded, IReadOnlyList<MateTerm> mates)
    {
        var poses = start.ToArray();
        var free = Enumerable.Range(0, poses.Length).Where(i => !grounded[i]).ToArray();
        var n = 6 * free.Length;
        if (n == 0 || mates.Count == 0) return new(poses, Norm(Residuals(poses, mates)), n);

        var rank = 0;
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var r = Residuals(poses, mates);
            var norm = Norm(r);
            var jacobian = Jacobian(poses, free, mates, r.Length);
            var (step, stepRank) = LeastSquares(jacobian, r, n);
            rank = stepRank;
            if (norm < 1e-13) break;
            // Halve the step until the residual drops: the mates are nonlinear in the rotation.
            var moved = poses;
            for (var scale = 1.0; scale > 1e-6; scale /= 2)
            {
                moved = Apply(poses, free, step, scale);
                if (Norm(Residuals(moved, mates)) < norm) break;
            }
            if (Norm(Residuals(moved, mates)) >= norm) break;
            poses = moved;
        }
        return new(poses, Norm(Residuals(poses, mates)), n - rank);
    }

    static double[] Residuals(IReadOnlyList<Frame> poses, IReadOnlyList<MateTerm> mates)
    {
        var r = new List<double>();
        foreach (var m in mates)
        {
            var (pa, pb) = (poses[m.ComponentA], poses[m.ComponentB]);
            var (oa, ob) = (pa.ToWorld(m.PointA), pb.ToWorld(m.PointB));
            var (da, db) = (pa.ToWorld(m.DirectionA), pb.ToWorld(m.DirectionB));
            switch (m.Kind)
            {
                case MateKind.Mate:
                case MateKind.Flush:
                    var turn = m.Kind == MateKind.Mate ? da + db : da - db;
                    r.AddRange([turn.X, turn.Y, turn.Z, da.Dot(ob - oa) - m.Offset]);
                    break;
                default:
                    var cross = da.Cross(db);
                    var d = ob - oa;
                    var off = d - da * d.Dot(da);
                    r.AddRange([cross.X, cross.Y, cross.Z, off.X, off.Y, off.Z]);
                    break;
            }
        }
        return r.ToArray();
    }

    static double[,] Jacobian(Frame[] poses, int[] free, IReadOnlyList<MateTerm> mates, int rows)
    {
        const double h = 1e-6;
        var j = new double[rows, 6 * free.Length];
        var dx = new double[6 * free.Length];
        for (var c = 0; c < dx.Length; c++)
        {
            dx[c] = h;
            var plus = Residuals(Apply(poses, free, dx, 1), mates);
            dx[c] = -h;
            var minus = Residuals(Apply(poses, free, dx, 1), mates);
            dx[c] = 0;
            for (var r = 0; r < rows; r++) j[r, c] = (plus[r] - minus[r]) / (2 * h);
        }
        return j;
    }

    /// <summary>Each free pose turned by its small rotation (about its own origin) and shifted.</summary>
    static Frame[] Apply(Frame[] poses, int[] free, double[] x, double scale)
    {
        var moved = poses.ToArray();
        for (var k = 0; k < free.Length; k++)
        {
            var p = poses[free[k]];
            var w = new Vector3D(x[6 * k], x[6 * k + 1], x[6 * k + 2]) * scale;
            var shift = new Vector3D(x[6 * k + 3], x[6 * k + 4], x[6 * k + 5]) * scale;
            moved[free[k]] = new Frame(p.Origin + shift, Rotate(p.X, w), Rotate(p.Y, w), Rotate(p.Z, w));
        }
        return moved;
    }

    static Vector3D Rotate(Vector3D v, Vector3D w)
    {
        var angle = w.Length;
        if (angle == 0) return v;
        var k = w / angle;
        return v * Math.Cos(angle) + k.Cross(v) * Math.Sin(angle) + k * (k.Dot(v) * (1 - Math.Cos(angle)));
    }

    /// <summary>Minimum-norm least-squares step −J⁺r through the eigenvalues of JᵀJ, and the rank of J.</summary>
    static (double[] Step, int Rank) LeastSquares(double[,] j, double[] r, int n)
    {
        var rows = r.Length;
        var jtj = new double[n, n];
        var jtr = new double[n];
        for (var a = 0; a < n; a++)
        {
            for (var k = 0; k < rows; k++) jtr[a] += j[k, a] * r[k];
            for (var b = 0; b < n; b++)
                for (var k = 0; k < rows; k++) jtj[a, b] += j[k, a] * j[k, b];
        }
        var (values, vectors) = Eigen(jtj);
        var max = values.Max(Math.Abs);
        var step = new double[n];
        var rank = 0;
        for (var e = 0; e < n; e++)
        {
            if (max == 0 || Math.Abs(values[e]) < 1e-9 * max) continue;
            rank++;
            var projection = 0.0;
            for (var a = 0; a < n; a++) projection += vectors[a, e] * jtr[a];
            for (var a = 0; a < n; a++) step[a] -= vectors[a, e] * projection / values[e];
        }
        return (step, rank);
    }

    /// <summary>Cyclic Jacobi eigendecomposition of a small symmetric matrix.</summary>
    static (double[] Values, double[,] Vectors) Eigen(double[,] input)
    {
        var n = input.GetLength(0);
        var a = (double[,])input.Clone();
        var v = new double[n, n];
        for (var i = 0; i < n; i++) v[i, i] = 1;
        for (var sweep = 0; sweep < 100; sweep++)
        {
            var off = 0.0;
            for (var p = 0; p < n; p++)
                for (var q = p + 1; q < n; q++) off += a[p, q] * a[p, q];
            if (off < 1e-30) break;
            for (var p = 0; p < n; p++)
            for (var q = p + 1; q < n; q++)
            {
                if (Math.Abs(a[p, q]) < 1e-300) continue;
                var theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                var t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                if (theta == 0) t = 1;
                var c = 1 / Math.Sqrt(t * t + 1);
                var s = t * c;
                for (var k = 0; k < n; k++)
                {
                    var (akp, akq) = (a[k, p], a[k, q]);
                    a[k, p] = c * akp - s * akq;
                    a[k, q] = s * akp + c * akq;
                }
                for (var k = 0; k < n; k++)
                {
                    var (apk, aqk) = (a[p, k], a[q, k]);
                    a[p, k] = c * apk - s * aqk;
                    a[q, k] = s * apk + c * aqk;
                }
                for (var k = 0; k < n; k++)
                {
                    var (vkp, vkq) = (v[k, p], v[k, q]);
                    v[k, p] = c * vkp - s * vkq;
                    v[k, q] = s * vkp + c * vkq;
                }
            }
        }
        return (Enumerable.Range(0, n).Select(i => a[i, i]).ToArray(), v);
    }

    static double Norm(double[] r) => Math.Sqrt(r.Sum(x => x * x));
}
