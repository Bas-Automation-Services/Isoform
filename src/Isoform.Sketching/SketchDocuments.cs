using Isoform.Document;

namespace Isoform.Sketching;

/// <summary>Sketches in a document: solving them with their parameters, and storing the solutions.</summary>
public static class SketchDocuments
{
    /// <summary>A sketch as regeneration sees it: parameters and expressions in its values resolved.</summary>
    public static Sketch Resolved(PartDocument doc, string id) =>
        Parametric.Resolve(doc).Features.FirstOrDefault(f => f.Id == id) as Sketch ?? throw new ArgumentException($"'{id}' is not a sketch");

    /// <summary>
    /// The document with every sketch's stored points and circle radii moved to its solution, so the text holds what
    /// regeneration builds; a sketch that does not solve keeps what it had. Values given by expressions stay
    /// expressions. <paramref name="solve"/> replaces the plain solve for a sketch (a drag).
    /// </summary>
    public static PartDocument Settle(PartDocument doc, Func<Sketch, SketchSolution?>? solve = null)
    {
        var resolved = Parametric.Resolve(doc);
        var features = doc.Features.Select((f, i) =>
        {
            if (f is not Sketch stored || resolved.Features[i] is not Sketch sketch) return f;
            var solution = solve?.Invoke(sketch) ?? SketchSolver.Solve(sketch);
            if (!solution.Solved) return f;
            bool Free(string path) => !doc.Expressions.ContainsKey(path);
            return stored with
            {
                Points = stored.Points.ToDictionary(p => p.Key, p =>
                {
                    var (x, y) = solution.Points[p.Key];
                    return new[] { Free($"{f.Id}.points.{p.Key}.0") ? x : p.Value[0], Free($"{f.Id}.points.{p.Key}.1") ? y : p.Value[1] };
                }),
                Entities = [.. stored.Entities.Select((e, k) => e is SketchCircle c && Free($"{f.Id}.entities.{k}.circle.radius") ? c with { Radius = solution.Radii[c.Id] } : e)],
            };
        }).ToList();
        return doc with { Features = features };
    }
}
