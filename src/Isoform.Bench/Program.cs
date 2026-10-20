using System.Diagnostics;
using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Geometry;
using Isoform.Meshing;
using Isoform.Modeling;
using Isoform.Occt;

// Baseline measurements for K2. Run with: dotnet run -c Release --project src/Isoform.Bench
var settings = new ModelingSettings();
Console.WriteLine($"Isoform bench — {Environment.ProcessorCount} logical cores, .NET {Environment.Version}");

var plate = new Block("plate", BooleanOp.New, [0, 0, 0], [100, 50, 10]);
var parts = new (string Name, PartDocument Doc)[]
{
    ("base", new PartDocument([plate, new Hole("hole", [30, 25], 10), new Fillet("fillet", ["plate/z+", "plate/x+"], 2)])),
    ("base + shell", new PartDocument([plate, new Hole("hole", [30, 25], 10), new Fillet("fillet", ["plate/z+", "plate/x+"], 2), new Shell("shell", 1.5)])),
    ("20 operations", new PartDocument(
    [
        plate,
        new Hole("h1", [10, 10], 6), new Hole("h2", [10, 40], 6), new Hole("h3", [30, 25], 10),
        new Hole("h4", [50, 12], 4), new Hole("h5", [50, 38], 4), new Hole("h6", [80, 25], 8),
        new Block("slot1", BooleanOp.Cut, [60, 30, -1], [6, 30, 12]),
        new Block("slot2", BooleanOp.Cut, [60, -10, -1], [6, 30, 12]),
        new Block("pocket", BooleanOp.Cut, [20, 5, 6], [20, 10, 5]),
        new Block("tab1", BooleanOp.Join, [-20, 10, 0], [30, 30, 10]),
        new Block("tab2", BooleanOp.Join, [95, 15, 0], [20, 20, 6]),
        new Block("boss", BooleanOp.Join, [40, 30, 9], [10, 10, 8]),
        new PlaneCut("chamfer", [100, 0, 10], [0.7071067811865476, -0.7071067811865476, 0]),
        new Hole("h7", [-10, 25], 6), new Hole("h8", [105, 25], 5),
        new Fillet("f1", ["plate/z+", "plate/x+"], 2),
        new Fillet("f2", ["plate/z-", "plate/y-"], 1.5),
        new Fillet("f3", ["plate/y+", "plate/x-"], 3),
        new Block("cut3", BooleanOp.Cut, [70, 20, 8], [8, 10, 5]),
    ])),
};

foreach (var (name, doc) in parts)
{
    var part = new Compiler(settings).Compile(doc);
    if (part.Diagnostics.Count > 0) Console.WriteLine($"  diagnostics: {string.Join("; ", part.Diagnostics.Select(d => d.Message))}");
    var field = part.Field!;
    Console.WriteLine($"\n== {name}: {part.Surfaces.Count} surfaces");

    var rng = new Random(1);
    var points = Enumerable.Range(0, 2_000_000).Select(_ =>
        new Point3D(part.Min.X - 5 + rng.NextDouble() * (part.Max.X - part.Min.X + 10),
                    part.Min.Y - 5 + rng.NextDouble() * (part.Max.Y - part.Min.Y + 10),
                    part.Min.Z - 5 + rng.NextDouble() * (part.Max.Z - part.Min.Z + 10))).ToArray();

    Measure("Value, 1 thread", points.Length, () => { var s = 0.0; foreach (var p in points) s += field.Value(p); return s; });
    Measure("Value, all threads", points.Length, () =>
    {
        var s = new double[points.Length];
        Parallel.For(0, points.Length, i => s[i] = field.Value(points[i]));
        return s[0];
    });
    Measure("Bounds (box 1 mm), 1 thread", points.Length / 10, () =>
    {
        var s = 0.0;
        for (var i = 0; i < points.Length / 10; i++) s += field.Bounds(new Box3D(points[i], points[i] + new Vector3D(1, 1, 1))).Hi;
        return s;
    });

    foreach (var h in new[] { 0.5, 0.25, 0.1 })
    {
        var total = Stopwatch.StartNew();
        var stages = new List<string>();
        var mesh = DualContouring.Build(field, part.Min, part.Max, h, (stage, t) => stages.Add($"{stage}: {t.TotalMilliseconds:N0} ms"));
        var meshMs = total.Elapsed.TotalMilliseconds;
        TopologyBuilder.Build(mesh, part.Surfaces, settings.Tolerance);
        Console.WriteLine($"  mesh h={h}: {meshMs:N0} ms + topology {total.Elapsed.TotalMilliseconds - meshMs:N0} ms, {mesh.Quads.Count:N0} quads, {GC.GetTotalMemory(false) / 1e6:N0} MB live");
        foreach (var s in stages) Console.WriteLine($"      {s}");
    }
}

// The same parts on OCCT: regenerate from the document, then tessellate at the chord accuracy our mesh
// reaches at 0.5 / 0.25 / 0.1 mm cells. Best of 3 runs (the first includes warm-up).
if (OcctModeler.IsAvailable)
{
    Console.WriteLine();
    Console.WriteLine("== OCCT 8.0.0-rc2 (same parts, single-threaded booleans)");
    foreach (var (name, doc) in parts.Where(p => !p.Doc.Features.OfType<Shell>().Any()))
    {
        foreach (var deflection in new[] { 0.02, 0.0054, 0.0013 })
        {
            double regen = double.MaxValue, mesh = double.MaxValue;
            var triangles = 0;
            for (var run = 0; run < 3; run++)
            {
                var sw = Stopwatch.StartNew();
                var result = new OcctModeler(settings).Regenerate(doc);
                regen = Math.Min(regen, sw.Elapsed.TotalMilliseconds);
                sw.Restart();
                triangles = result.Shape!.Tessellate(deflection);
                mesh = Math.Min(mesh, sw.Elapsed.TotalMilliseconds);
            }
            Console.WriteLine($"  {name}, deflection {deflection} mm: regenerate {regen:N0} ms + tessellate {mesh:N0} ms, {triangles:N0} triangles");
        }
    }
}

static void Measure(string label, int count, Func<double> run)
{
    run(); // warm-up
    var sw = Stopwatch.StartNew();
    var sink = run();
    var rate = count / sw.Elapsed.TotalSeconds;
    Console.WriteLine($"  {label}: {rate / 1e6:N1} M/s ({sink:E1})");
}
