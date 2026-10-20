namespace Isoform.Meshing;

/// <summary>Least-squares solve of a symmetric 3×3 system through its eigendecomposition (cyclic Jacobi).</summary>
public static class SymmetricSolver
{
    /// <summary>
    /// Solves A·x = b with a pseudo-inverse: eigenvalues below <paramref name="relativeCutoff"/> × the largest are
    /// treated as zero, so flat and edge cells keep their tangential position instead of drifting along the surface.
    /// </summary>
    public static double[] PseudoSolve(double[,] a, double[] b, double relativeCutoff)
    {
        var (values, vectors) = Eigen(a);
        var max = values.Max(Math.Abs);
        var x = new double[3];
        if (max == 0) return x;
        for (var e = 0; e < 3; e++)
        {
            if (Math.Abs(values[e]) < relativeCutoff * max) continue;
            var proj = 0.0;
            for (var r = 0; r < 3; r++) proj += vectors[r, e] * b[r];
            for (var r = 0; r < 3; r++) x[r] += vectors[r, e] * proj / values[e];
        }
        return x;
    }

    public static (double[] Values, double[,] Vectors) Eigen(double[,] input)
    {
        var a = (double[,])input.Clone();
        var v = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        for (var sweep = 0; sweep < 32; sweep++)
        {
            var off = a[0, 1] * a[0, 1] + a[0, 2] * a[0, 2] + a[1, 2] * a[1, 2];
            if (off < 1e-30) break;
            for (var p = 0; p < 2; p++)
            for (var q = p + 1; q < 3; q++)
            {
                if (a[p, q] == 0) continue;
                var theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                var t = Math.Sign(theta == 0 ? 1 : theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                var c = 1 / Math.Sqrt(t * t + 1);
                var s = t * c;
                for (var k = 0; k < 3; k++)
                {
                    var akp = a[k, p];
                    var akq = a[k, q];
                    a[k, p] = c * akp - s * akq;
                    a[k, q] = s * akp + c * akq;
                }
                for (var k = 0; k < 3; k++)
                {
                    var apk = a[p, k];
                    var aqk = a[q, k];
                    a[p, k] = c * apk - s * aqk;
                    a[q, k] = s * apk + c * aqk;
                }
                for (var k = 0; k < 3; k++)
                {
                    var vkp = v[k, p];
                    var vkq = v[k, q];
                    v[k, p] = c * vkp - s * vkq;
                    v[k, q] = s * vkp + c * vkq;
                }
            }
        }
        return ([a[0, 0], a[1, 1], a[2, 2]], v);
    }
}
