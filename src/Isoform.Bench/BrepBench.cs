using System.Diagnostics;
using Isoform.Brep.Modeling;
using Isoform.Document;
using Isoform.Interactive;
using Isoform.Modeling;

/// <summary>
/// The managed B-rep kernel on parts that grow (a plate with a grid of holes, a polygon with its top rounded all
/// round, a row of holes drilled one by one, a row of blocks joined onto a base) and on the playground's examples. Wall time, and the process's
/// processor time, which a busy machine disturbs less.
/// </summary>
static class BrepBench
{
    static readonly SketchPlane Xy = new([0, 0, 0], [1, 0, 0], [0, 0, 1]);

    public static void Run(string[] args)
    {
        var which = args.Length > 0 ? args[0] : "all";
        var sizes = args.Skip(1).Select(int.Parse).ToList();
        if (which is "all" or "holes")
            foreach (var n in sizes.Count > 0 ? sizes : [8, 16, 24])
                Time($"plate with {n}x{n} holes", Holes(n));
        if (which is "all" or "loop")
            foreach (var n in sizes.Count > 0 ? sizes : [48, 96, 192])
                Time($"{n}-gon with a loop round", Loop(n));
        if (which is "all" or "join")
            foreach (var n in sizes.Count > 0 ? sizes : [48, 96, 192])
                Time($"{n} blocks joined", Join(n));
        if (which is "all" or "drill")
            foreach (var n in sizes.Count > 0 ? sizes : [48, 96, 192])
                Time($"{n} holes one by one", Drill(n));
        if (which is "all" or "examples")
            foreach (var (name, doc) in Examples.All)
                Time(name.Length > 28 ? name[..28] : name, doc);
    }

    static void Time(string name, PartDocument document)
    {
        var cpu = Process.GetCurrentProcess().TotalProcessorTime;
        var watch = Stopwatch.StartNew();
        var result = new BrepModeler(new ModelingSettings()).Regenerate(document);
        watch.Stop();
        cpu = Process.GetCurrentProcess().TotalProcessorTime - cpu;
        var faces = result.Bodies.Sum(b => b.Body.Faces.Length);
        var problem = result.Diagnostics.FirstOrDefault()?.Message;
        Console.WriteLine($"  {name,-28} {watch.Elapsed.TotalSeconds,7:F2} s ({cpu.TotalSeconds:F2} s processor), {faces} faces{(problem is null ? "" : $"; failed: {problem}")}");
    }

    static PartDocument Holes(int n)
    {
        var points = new Dictionary<string, double[]>();
        for (var i = 0; i < n; i++)
        for (var j = 0; j < n; j++)
            points[$"p{i}_{j}"] = [10 + 10 * i, 10 + 10 * j];
        return new PartDocument(
        [
            new Block("plate", BooleanOp.New, [0, 0, 0], [10 * n + 10, 10 * n + 10, 5]),
            new Sketch("grid", new SketchPlacement(Xy), points, [], []),
            new Hole("holes", [0, 0], 4) { Sketch = "grid", Points = [.. points.Keys] },
        ]);
    }

    static PartDocument Loop(int n)
    {
        double[] At(int k) => [50 * Math.Cos(2 * Math.PI * k / n), 50 * Math.Sin(2 * Math.PI * k / n)];
        var profile = new Profile(At(0), [.. Enumerable.Range(1, n).Select(k => new ProfileEdge($"s{k}", At(k % n)))]);
        return new PartDocument([new Extrude("poly", BooleanOp.New, Xy, profile, 0, 10), new Fillet("round", [], 1, Loop: "poly/end")]);
    }

    // A row of holes, each its own feature: every one a boolean against the whole plate so far.
    static PartDocument Drill(int n) => new(
    [
        new Block("plate", BooleanOp.New, [0, 0, 0], [10 * n + 10, 20, 5]),
        .. Enumerable.Range(0, n).Select(k => new Hole($"hole{k}", [10 + 10 * k, 10], 4)),
    ]);

    static PartDocument Join(int n) => new(
    [
        new Block("base", BooleanOp.New, [0, 0, 0], [10 * n + 5, 20, 5]),
        .. Enumerable.Range(0, n).Select(k => new Block($"rib{k}", BooleanOp.Join, [5 + 10 * k, 0, 5], [5, 20, 10])),
    ]);
}
