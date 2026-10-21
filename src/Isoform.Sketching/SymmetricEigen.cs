namespace Isoform.Sketching;

/// <summary>
/// Eigenvalues and eigenvectors of a small symmetric matrix by cyclic Jacobi rotations: slow beyond a few hundred
/// unknowns, exact enough for a sketch, and deterministic.
/// </summary>
static class SymmetricEigen
{
    /// <summary>Eigenvalues, and eigenvectors as the columns of <c>Vectors</c> (Vectors[row, k] for value k).</summary>
    public static (double[] Values, double[,] Vectors) Of(double[,] matrix)
    {
        var n = matrix.GetLength(0);
        var a = (double[,])matrix.Clone();
        var v = new double[n, n];
        for (var i = 0; i < n; i++) v[i, i] = 1;
        for (var sweep = 0; sweep < 100; sweep++)
        {
            double off = 0, diagonal = 0;
            for (var i = 0; i < n; i++)
            {
                diagonal += a[i, i] * a[i, i];
                for (var j = i + 1; j < n; j++) off += a[i, j] * a[i, j];
            }
            if (off <= 1e-30 * Math.Max(diagonal, 1e-300)) break;
            for (var p = 0; p < n; p++)
            for (var q = p + 1; q < n; q++)
            {
                if (a[p, q] == 0) continue;
                // The rotation that zeroes a[p, q] (Golub & Van Loan 8.5.2).
                var theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                var t = Math.Sign(theta == 0 ? 1 : theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
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
        var values = new double[n];
        for (var i = 0; i < n; i++) values[i] = a[i, i];
        return (values, v);
    }
}
