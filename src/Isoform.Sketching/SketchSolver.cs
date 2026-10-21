using System.Globalization;
using Isoform.Document;

namespace Isoform.Sketching;

/// <summary>A sketch problem: what it is (a diagnostic code), what it says, and the constraints it is about.</summary>
public sealed record SketchProblem(string Code, string Message, IReadOnlyList<int> Constraints);

/// <summary>
/// A solved sketch: every point and circle radius, how many degrees of freedom are left, which points can still move,
/// the residual left (mm), and the problems found. Solving never fails silently: a sketch whose constraints cannot all
/// hold has a <c>sketch-conflict</c> problem naming them.
/// </summary>
public sealed record SketchSolution(
    IReadOnlyDictionary<string, (double X, double Y)> Points,
    IReadOnlyDictionary<string, double> Radii,
    int FreeDegrees,
    IReadOnlySet<string> Free,
    double Residual,
    IReadOnlyList<SketchProblem> Problems)
{
    public const string Conflict = "sketch-conflict";
    public const string Redundant = "sketch-redundant";
    public const string Invalid = "sketch-invalid";

    /// <summary>No problem but redundancy, which is only a warning.</summary>
    public bool Solved => Problems.All(p => p.Code == Redundant);
}

/// <summary>
/// The 2D constraint solver. The unknowns are the coordinates of every point and the radius of every circle. Every
/// constraint gives residuals in millimetres, zero where it holds. Gauss-Newton steps with the least-squares
/// pseudo-inverse move the unknowns as little as possible, so what no constraint fixes stays where it was stored, and
/// the rank of the Jacobian at the solution gives the degrees of freedom left. Constraints that pick a side (which
/// side of a line, internal or external tangency) keep the side the stored coordinates are on.
/// </summary>
public static class SketchSolver
{
    /// <summary>A solved sketch holds every constraint to this, in mm.</summary>
    public const double Tolerance = 1e-9;

    /// <summary>What is wrong with the sketch's structure (names, references, constraint arity); null when it can be solved.</summary>
    public static string? Problem(Sketch sketch) => Model.Problem(sketch);

    /// <summary>Solves the sketch from its stored coordinates; a dragged point starts at its target instead.</summary>
    public static SketchSolution Solve(Sketch sketch, (string Point, double X, double Y)? drag = null)
    {
        if (Model.Problem(sketch) is { } invalid) return Unsolved(sketch, invalid);
        var model = new Model(sketch);
        var x = model.Start();
        var all = Enumerable.Range(0, sketch.Constraints.Count).ToArray();
        if (drag is { } d && model.PointIndex.TryGetValue(d.Point, out var at))
        {
            // From the target, with a weak pull towards it: the constraints win, and the point stays as near as they let it.
            (x[at], x[at + 1]) = (d.X, d.Y);
            model.Pull(d);
            Run(model, all, x);
            model.Pull(null);
        }
        var residual = Run(model, all, x);
        var problems = new List<SketchProblem>();
        if (residual > Tolerance)
        {
            // Every constraint without which the rest can hold is part of the conflict.
            var culprits = all.Where(c => Run(model, all.Where(k => k != c).ToArray(), model.Start()) <= Tolerance).ToList();
            problems.Add(new SketchProblem(SketchSolution.Conflict,
                culprits.Count > 0
                    ? $"constraints {string.Join(", ", culprits.Select(c => Describe(sketch, c)))} cannot all hold; removing any one of them solves the sketch"
                    : FormattableString.Invariant($"the constraints cannot all hold (residual {residual:G3} mm)"),
                culprits));
        }

        var (rank, nullspace) = Rank(model, all, x);
        if (problems.Count == 0 && model.Rows(all) > rank)
            foreach (var c in all)
                if (model.Rows([c]) > 0 && Rank(model, all.Where(k => k != c).ToArray(), x).Rank == rank)
                    problems.Add(new SketchProblem(SketchSolution.Redundant, $"constraint {Describe(sketch, c)} is implied by the others", [c]));

        var free = model.Free(nullspace);
        return new SketchSolution(model.Points(x), model.Radii(x), model.Unknowns - rank, free, residual, problems);
    }

    /// <summary>The sketch with its points and radii moved to the solution, as a document stores it.</summary>
    public static Sketch Settle(Sketch sketch, SketchSolution solution) => sketch with
    {
        Points = sketch.Points.ToDictionary(p => p.Key, p => solution.Points.TryGetValue(p.Key, out var q) ? new[] { q.X, q.Y } : p.Value),
        Entities = [.. sketch.Entities.Select(e => e is SketchCircle c && solution.Radii.TryGetValue(c.Id, out var r) ? c with { Radius = r } : e)],
    };

    static SketchSolution Unsolved(Sketch sketch, string message) => new(
        sketch.Points.ToDictionary(p => p.Key, p => (p.Value[0], p.Value[1])),
        sketch.Entities.OfType<SketchCircle>().ToDictionary(c => c.Id, c => c.Radius),
        0, new HashSet<string>(), double.PositiveInfinity, [new SketchProblem(SketchSolution.Invalid, message, [])]);

    /// <summary>"length bottom = 40" for constraint k.</summary>
    public static string Describe(Sketch sketch, int k)
    {
        var c = sketch.Constraints[k];
        var value = c.Value is { } v ? " = " + v.ToString("G6", CultureInfo.InvariantCulture) : "";
        return $"#{k} ({c.Kind} {string.Join(" ", c.Refs)}{value})";
    }

    /// <summary>Gauss-Newton with a halving line search; returns the residual norm left. Moves <paramref name="x"/>.</summary>
    static double Run(Model model, int[] constraints, double[] x)
    {
        var norm = Norm(model.Residuals(constraints, x));
        for (var iteration = 0; iteration < 200 && norm > 1e-15; iteration++)
        {
            var r = model.Residuals(constraints, x);
            var j = model.Jacobian(constraints, x);
            var step = LeastNorm(j, r, model.Unknowns);
            var moved = false;
            for (var scale = 1.0; scale > 1e-8 && !moved; scale /= 2)
            {
                var trial = (double[])x.Clone();
                for (var i = 0; i < trial.Length; i++) trial[i] -= scale * step[i];
                var n = Norm(model.Residuals(constraints, trial));
                if (n >= norm) continue;
                Array.Copy(trial, x, x.Length);
                (norm, moved) = (n, true);
            }
            if (!moved) break;
        }
        return norm;
    }

    /// <summary>The least-norm solution of J·d = r, by the eigenvectors of JᵀJ above the rank threshold.</summary>
    static double[] LeastNorm(double[,] j, double[] r, int n)
    {
        var (values, vectors) = SymmetricEigen.Of(Normal(j, n));
        var jtr = new double[n];
        for (var row = 0; row < r.Length; row++)
            for (var c = 0; c < n; c++) jtr[c] += j[row, c] * r[row];
        var limit = Threshold(values);
        var step = new double[n];
        for (var k = 0; k < n; k++)
        {
            if (values[k] <= limit) continue;
            double along = 0;
            for (var c = 0; c < n; c++) along += vectors[c, k] * jtr[c];
            along /= values[k];
            for (var c = 0; c < n; c++) step[c] += vectors[c, k] * along;
        }
        return step;
    }

    /// <summary>The rank of the Jacobian, and the directions it leaves free (unit vectors over the unknowns).</summary>
    static (int Rank, List<double[]> Nullspace) Rank(Model model, int[] constraints, double[] x)
    {
        var n = model.Unknowns;
        if (n == 0) return (0, []);
        var (values, vectors) = SymmetricEigen.Of(Normal(model.Jacobian(constraints, x), n));
        var limit = Threshold(values);
        var nullspace = new List<double[]>();
        var rank = 0;
        for (var k = 0; k < n; k++)
        {
            if (values[k] > limit) rank++;
            else nullspace.Add(Enumerable.Range(0, n).Select(c => vectors[c, k]).ToArray());
        }
        return (rank, nullspace);
    }

    // Singular values below 1e-6 of the largest count as zero (their squares, the eigenvalues, below 1e-12).
    static double Threshold(double[] values) => 1e-12 * Math.Max(values.Length == 0 ? 0 : values.Max(), 1e-300);

    static double[,] Normal(double[,] j, int n)
    {
        var a = new double[n, n];
        for (var row = 0; row < j.GetLength(0); row++)
            for (var p = 0; p < n; p++)
            {
                var jp = j[row, p];
                if (jp == 0) continue;
                for (var q = 0; q < n; q++) a[p, q] += jp * j[row, q];
            }
        return a;
    }

    static double Norm(double[] r) => Math.Sqrt(r.Sum(v => v * v));
}
