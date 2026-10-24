using System.Globalization;
using System.Text;
using Isoform.Document;
using Isoform.Modeling;
using Isoform.Sketching;
using Isoform.Step;

namespace Isoform.Build;

/// <summary>A distinct tube to saw: lengths along the axis in mm, end angles off square and the turn between them in degrees.</summary>
public sealed record CutListRow(string Part, int Quantity, double Outside, double Wall, string Material, double LongPoint, double ShortPoint,
    double AngleA, double AngleB, double Turn);

/// <summary>A distinct part of the build: its kind (plate, tube), how many, what it is, and its mass in kg each.</summary>
public sealed record BomRow(string Part, string Kind, int Quantity, string Description, double MassEach)
{
    public double Mass => MassEach * Quantity;
}

/// <summary>
/// What a build leaves in: one STEP file with the assembly's structure (one product per distinct part, an occurrence
/// per instance), a DXF per distinct plate from its sketch (lines, arcs and circles, exact as the sketch has them), a
/// cut list of the tubes and a bill of materials, each as CSV and markdown. Parts are named after their documents.
/// </summary>
public static class Outputs
{
    static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>The build's assembly as STEP AP214: every distinct part regenerated once and written exactly, then placed.</summary>
    public static string Step(BuildResult result, ModelingSettings settings, string name = "assembly")
    {
        var bundle = result.Bundle;
        var assembly = new AssemblyModeler(settings).Regenerate(bundle.Assembly, p => bundle.Documents.GetValueOrDefault(p));
        return StepWriter.Write(assembly, settings, name);
    }

    /// <summary>A DXF per distinct plate in its own XY, by file name (<c>top.dxf</c>).</summary>
    public static IReadOnlyDictionary<string, string> Dxf(BuildResult result) =>
        result.Parts.Where(p => PlateShape.Of(p.Value) is not null).ToDictionary(p => Name(p.Key) + ".dxf", p =>
        {
            var sketch = PlateShape.Of(p.Value)!.Sketch;
            // The sketch's points are its solution: no constraints, nothing to solve.
            var solution = new SketchSolution(sketch.Points.ToDictionary(q => q.Key, q => (q.Value[0], q.Value[1])),
                sketch.Entities.OfType<SketchCircle>().ToDictionary(c => c.Id, c => c.Radius), 0, new HashSet<string>(), 0, []);
            return DxfWriter.Write(DxfWriter.Sketch(sketch, solution));
        });

    public static IReadOnlyList<CutListRow> CutList(BuildResult result, Material? material) =>
    [
        .. result.Parts.Select(p => (p.Key, Tube: TubeShape.Of(p.Value))).Where(p => p.Tube is not null).Select(p => new CutListRow(Name(p.Key), Quantity(result, p.Key),
            p.Tube!.Outside, p.Tube.Wall, material?.Name ?? "", p.Tube.LongPoint, p.Tube.ShortPoint, p.Tube.AngleA, p.Tube.AngleB, p.Tube.Turn)),
    ];

    public static IReadOnlyList<BomRow> Bom(BuildResult result, Material? material)
    {
        var (name, density) = (material is null ? "" : " " + material.Name, material?.Density ?? 0);
        return [.. result.Parts.Select(p =>
        {
            var (part, quantity) = (Name(p.Key), Quantity(result, p.Key));
            if (PlateShape.Of(p.Value) is { } plate)
            {
                var box = plate.Box;
                return new BomRow(part, "plate", quantity, $"Plate {N(plate.Thickness)}{name} {N(box.MaxX - box.MinX)} × {N(box.MaxY - box.MinY)}", plate.Area * plate.Thickness * density);
            }
            if (TubeShape.Of(p.Value) is { } tube)
                return new BomRow(part, "tube", quantity, $"Tube Ø{N(tube.Outside)} × {N(tube.Wall)}{name} L {N(tube.LongPoint)}", tube.Volume * density);
            return new BomRow(part, "part", quantity, part, 0);
        })];
    }

    public static string Csv(IReadOnlyList<CutListRow> rows) => Csv(CutHeader, rows.Select(Cells));
    public static string Markdown(IReadOnlyList<CutListRow> rows) => Markdown(CutHeader, rows.Select(Cells));

    public static string Csv(IReadOnlyList<BomRow> rows) => Csv(BomHeader, [.. rows.Select(Cells), BomTotal(rows)]);
    public static string Markdown(IReadOnlyList<BomRow> rows) => Markdown(BomHeader, [.. rows.Select(Cells), BomTotal(rows)]);

    static readonly string[] CutHeader = ["part", "quantity", "size", "material", "long point", "short point", "end A", "end B", "turn"];
    static readonly string[] BomHeader = ["part", "kind", "quantity", "description", "mass each (kg)", "mass (kg)"];

    static string[] Cells(CutListRow r) =>
        [r.Part, r.Quantity.ToString(Invariant), $"Ø{N(r.Outside)} × {N(r.Wall)}", r.Material, F(r.LongPoint, 1), F(r.ShortPoint, 1), $"{F(r.AngleA, 1)}°", $"{F(r.AngleB, 1)}°", $"{F(r.Turn, 0)}°"];

    static string[] Cells(BomRow r) => [r.Part, r.Kind, r.Quantity.ToString(Invariant), r.Description, F(r.MassEach, 2), F(r.Mass, 2)];

    static string[] BomTotal(IReadOnlyList<BomRow> rows) => ["total", "", rows.Sum(r => r.Quantity).ToString(Invariant), "", "", F(rows.Sum(r => r.Mass), 2)];

    static string Csv(string[] header, IEnumerable<string[]> rows)
    {
        static string Cell(string s) => s.Contains(',') || s.Contains('"') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
        var w = new StringBuilder();
        foreach (var row in rows.Prepend(header)) w.Append(string.Join(",", row.Select(Cell))).Append('\n');
        return w.ToString();
    }

    static string Markdown(string[] header, IEnumerable<string[]> rows)
    {
        var w = new StringBuilder();
        w.Append("| ").Append(string.Join(" | ", header)).Append(" |\n");
        w.Append('|').Append(string.Concat(header.Select(_ => " --- |"))).Append('\n');
        foreach (var row in rows) w.Append("| ").Append(string.Join(" | ", row)).Append(" |\n");
        return w.ToString();
    }

    static int Quantity(BuildResult result, string path) => result.Bundle.Assembly.Components.Count(c => c.Part == path);

    static string Name(string path) => Path.GetFileNameWithoutExtension(path);

    // A size as a person writes it: 12, 48.3, 160 (to 0.1 mm).
    static string N(double x) => Math.Round(x, 1).ToString("0.#", Invariant);

    static string F(double x, int decimals) => x.ToString("F" + decimals, Invariant);
}
