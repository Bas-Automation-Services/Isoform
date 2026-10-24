using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using BasAutomation.Geometry;
using Isoform.Geometry;

namespace Isoform.Build;

/// <summary>
/// A verification written out: JSON for agents (canonical: fixed key order, LF newlines, shortest round-trip numbers,
/// no date, so the same inputs give the same bytes) and markdown for people, which says plainly what it does not check.
/// </summary>
public static class Report
{
    public const string FormatVersion = "isoform-verification/0";

    static readonly JsonWriterOptions Options = new()
    {
        Indented = true,
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Json(Verification v)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, Options))
        {
            w.WriteStartObject();
            w.WriteString("format", FormatVersion);
            w.WriteString("verdict", Name(v.Verdict));
            w.WriteString("design", v.DesignHash);
            w.WriteString("build", v.BuildHash);
            w.WriteString("bundle", v.BundleHash);
            w.WriteStartObject("settings");
            Number(w, "cellSize", v.Settings.CellSize);
            Number(w, "tolerance", v.Settings.Tolerance);
            Number(w, "searchTolerance", v.Settings.SearchTolerance);
            Number(w, "interfaceTolerance", v.Settings.InterfaceTolerance);
            if (v.Settings.Material is { } m)
            {
                w.WriteStartObject("material");
                w.WriteString("name", m.Name);
                Number(w, "density", m.Density);
                w.WriteEndObject();
            }
            w.WriteEndObject();

            Objects(w, "checks", v.Checks, c =>
            {
                w.WriteString("check", c.Check);
                w.WriteString("verdict", Name(c.Verdict));
            });

            var vol = v.Volumes;
            w.WriteStartObject("volumes");
            Number(w, "design", vol.Design);
            Range(w, "designRange", vol.DesignRange);
            Number(w, "parts", vol.Parts);
            Range(w, "partsRange", vol.PartsRange);
            Number(w, "partsOutside", vol.PartsOutside);
            Number(w, "designUncovered", vol.DesignUncovered);
            Number(w, "designMass", vol.DesignMass);
            Number(w, "partsMass", vol.PartsMass);
            Point(w, "designCentroid", vol.DesignCentroid);
            Point(w, "partsCentroid", vol.PartsCentroid);
            w.WriteEndObject();

            Objects(w, "parts", v.Parts, p =>
            {
                w.WriteString("part", p.Part);
                w.WriteNumber("count", p.Count);
                Number(w, "volume", p.Volume);
                Range(w, "volumeRange", p.Range);
                Number(w, "mass", p.Mass);
            });
            Objects(w, "outside", v.Outside, o =>
            {
                w.WriteString("instance", o.Instance);
                w.WriteString("part", o.Part);
                WriteDeviation(w, "excess", o.Excess);
                w.WriteString("designFace", o.DesignFace);
                w.WriteString("partFace", o.PartFace);
                Number(w, "mean", o.Mean);
                w.WriteString("verdict", Name(o.Verdict));
            });
            Objects(w, "uncovered", v.Uncovered, f =>
            {
                w.WriteString("face", f.Face);
                w.WriteNumber("copies", f.Copies);
                WriteDeviation(w, "excess", f.Excess);
                w.WriteString("partFace", f.PartFace);
                if (f.Distance is { } d) Range(w, "distance", d);
                Number(w, "mean", f.Mean);
            });
            Objects(w, "contacts", v.Contacts, c =>
            {
                w.WriteString("a", c.A);
                w.WriteString("b", c.B);
                Range(w, "clearance", new Interval(c.Lower, c.Upper));
                Point(w, "onA", c.OnA);
                Point(w, "onB", c.OnB);
                w.WriteString("kind", c.Kind.ToString().ToLowerInvariant());
                w.WriteString("verdict", Name(c.Verdict));
            });
            Objects(w, "keepouts", v.Keepouts, k =>
            {
                w.WriteString("body", k.Body);
                Number(w, "required", k.Required);
                w.WriteString("nearest", k.Instance);
                Range(w, "clearance", new Interval(k.Lower, k.Upper));
                w.WriteString("verdict", Name(k.Verdict));
            });
            Objects(w, "interfaces", v.Interfaces, i =>
            {
                w.WriteString("face", i.Face);
                if (i.Uncovered is { } u) WriteDeviation(w, "uncovered", u);
                w.WriteNumber("vertices", i.Vertices);
                Number(w, "worst", i.Worst);
                w.WriteNumber("unmatched", i.Unmatched);
                w.WriteStartArray("partFaces");
                foreach (var f in i.PartFaces) w.WriteStringValue(f);
                w.WriteEndArray();
                w.WriteString("verdict", Name(i.Verdict));
            });
            Objects(w, "walls", v.Walls, x =>
            {
                w.WriteString("part", x.Part);
                Number(w, "thickness", x.Thickness);
                if (x.Thickness is null) return;
                Point(w, "from", x.From);
                Point(w, "to", x.To);
                w.WriteString("fromFace", x.FromFace);
                w.WriteString("toFace", x.ToFace);
            });
            if (v.Fits is { } fits)
                Objects(w, "fits", fits, f =>
                {
                    w.WriteString("part", f.Part);
                    w.WriteString("edge", f.Edge);
                    w.WriteNumber("arcs", f.Arcs);
                    Number(w, "error", f.Error);
                });
            Objects(w, "diagnostics", v.Diagnostics, d =>
            {
                w.WriteString("from", d.From);
                w.WriteString("code", d.Diagnostic.Code);
                if (d.Diagnostic.FeatureId is { } id) w.WriteString("feature", id);
                w.WriteString("message", d.Diagnostic.Message);
            });
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    public static string Markdown(Verification v)
    {
        var s = new StringBuilder();
        void Line(FormattableString text) => s.Append(FormattableString.Invariant(text)).Append('\n');
        void Table(params string[] headers)
        {
            s.Append("| ").AppendJoin(" | ", headers).Append(" |\n|").Append(string.Concat(headers.Select(_ => "---|"))).Append('\n');
        }
        void Row(params string[] cells) => s.Append("| ").AppendJoin(" | ", cells.Select(c => c.Replace("|", "\\|"))).Append(" |\n");
        var tol = v.Settings.Tolerance;
        var vol = v.Volumes;

        Line($"# Verification: {Name(v.Verdict).ToUpperInvariant()}");
        Line($"");
        Line($"Design `{v.DesignHash[..12]}`, build `{v.BuildHash[..12]}`, bundle `{v.BundleHash[..12]}`. Cell {v.Settings.CellSize} mm; build tolerance {tol} mm; deviations certified to {v.Settings.SearchTolerance} mm (a settled maximum is shown at its attained value, an unsettled one as its interval); interface surfaces to {v.Settings.InterfaceTolerance:G} mm.{(v.Settings.Material is { } m ? FormattableString.Invariant($" Material {m.Name}, {m.Density:G} kg/mm³.") : " No material: no masses.")}");
        Line($"");

        Line($"## Checks");
        Line($"");
        Table("Check", "Verdict", "Result");
        foreach (var c in v.Checks) Row(Title(c.Check), Name(c.Verdict), Summary(v, c.Check));
        Line($"");

        Line($"## Parts");
        Line($"");
        Table("Part", "Qty", "Volume each (cm³)", "Mass each (kg)", "Mass (kg)");
        foreach (var p in v.Parts) Row(p.Part, Num(p.Count), Num(p.Volume / 1000, 1), Kg(p.Mass), Kg(p.Mass * p.Count));
        Row("**All**", Num(v.Parts.Sum(p => p.Count)), Num(vol.Parts / 1000, 1), "", Kg(vol.PartsMass));
        Line($"");
        Line($"Volumes from the meshes; certified ranges: parts {Num(vol.PartsRange.Lo / 1000, 1)} to {Num(vol.PartsRange.Hi / 1000, 1)} cm³, design {Num(vol.DesignRange.Lo / 1000, 1)} to {Num(vol.DesignRange.Hi / 1000, 1)} cm³. Design {Num(vol.Design / 1000, 1)} cm³ ({Kg(vol.DesignMass)} kg); parts outside the design {Num(vol.PartsOutside / 1000, 1)} cm³, design not covered {Num(vol.DesignUncovered / 1000, 1)} cm³. Centroid of the parts {Pt(vol.PartsCentroid)}, of the design {Pt(vol.DesignCentroid)}.");
        Line($"");

        Line($"## Deviation per design face");
        Line($"");
        Line($"How far each face of the design is from the parts where they leave it uncovered (negative: covered that deep), merged over its pattern copies. It is the parts' field, at most the true distance; the certified distance at the worst point is given where it exceeds the tolerance.");
        Line($"");
        Table("Design face", "Not covered (mm)", "Mean (mm)", "Part face nearest", "At", "Distance there (mm)");
        foreach (var f in v.Uncovered.OrderByDescending(f => f.Excess.Lower).ThenBy(f => f.Face, StringComparer.Ordinal))
            Row(f.Face + (f.Copies > 1 ? $" ×{f.Copies}" : ""), Max(f.Excess), Num(f.Mean), f.PartFace, Pt(f.Excess.At), f.Distance is { } d ? Span(d) : "");
        Line($"");

        Line($"## Parts outside the design");
        Line($"");
        Table("Instance", "Part", "Outside (mm)", "Mean (mm)", "Part face", "Design face", "At", "Verdict");
        foreach (var o in v.Outside) Row(o.Instance, o.Part, Max(o.Excess), Num(o.Mean), o.PartFace, o.DesignFace, Pt(o.Excess.At), Name(o.Verdict));
        Line($"");

        Line($"## Contacts");
        Line($"");
        Line($"Certified clearance between instances whose boxes meet. A joint is an area contact: the upper end reads it exactly, the lower end stays a little below zero. Interference only beyond the tolerance.");
        Line($"");
        Table("A", "B", "Clearance (mm)", "Kind", "Verdict");
        foreach (var c in v.Contacts) Row(c.A, c.B, Span(new(c.Lower, c.Upper)), c.Kind.ToString().ToLowerInvariant(), Name(c.Verdict));
        Line($"");

        if (v.Keepouts.Count > 0)
        {
            Line($"## Keep-outs");
            Line($"");
            Table("Body", "Required (mm)", "Clearance (mm)", "Nearest", "Verdict");
            foreach (var k in v.Keepouts) Row(k.Body, Num(k.Required), Span(new(k.Lower, k.Upper)), k.Instance, Name(k.Verdict));
            Line($"");
        }

        if (v.Interfaces.Count > 0)
        {
            Line($"## Interfaces");
            Line($"");
            Line($"Each interface face of the design must lie on a part face of the same surface (to {v.Settings.InterfaceTolerance:G} mm) at every mesh vertex, and be covered to the tolerance (certified).");
            Line($"");
            Table("Face", "Vertices", "Worst at vertices (mm)", "Not covered (mm)", "Unmatched", "Part faces", "Verdict");
            foreach (var i in v.Interfaces)
                Row(i.Face, Num(i.Vertices), Num(i.Worst, 7), i.Uncovered is { } u ? Max(u) : "", Num(i.Unmatched), string.Join(", ", i.PartFaces.Take(4)) + (i.PartFaces.Count > 4 ? $" (+{i.PartFaces.Count - 4})" : ""), Name(i.Verdict));
            Line($"");
        }

        Line($"## Minimum wall");
        Line($"");
        Line($"Sampled from each part's mesh. A sharp edge (an oblique cut, a mitre) reads thin towards its tip: that is the edge, not a wall.");
        Line($"");
        Table("Part", "Wall (mm)", "Between");
        foreach (var x in v.Walls) Row(x.Part, Num(x.Thickness), x.Thickness is null ? "" : $"{x.FromFace} and {x.ToFace}");
        Line($"");

        if (v.Fits is { Count: > 0 } fits)
        {
            Line($"## Fitted edges");
            Line($"");
            Table("Part", "Fitted edges", "Arcs", "Largest error (mm)", "On");
            foreach (var g in fits.GroupBy(f => f.Part).OrderBy(g => g.Key, StringComparer.Ordinal))
                Row(g.Key, Num(g.Count()), Num(g.Sum(f => f.Arcs)), Num(g.Max(f => f.Error), 4), g.MaxBy(f => f.Error)!.Edge);
            Line($"");
        }

        Line($"## Diagnostics");
        Line($"");
        if (v.Diagnostics.Count == 0) Line($"None: the design, the parts, the assembly and the build regenerate cleanly.");
        foreach (var d in v.Diagnostics) Line($"- {d.From}: `{d.Diagnostic.Code}`{(d.Diagnostic.FeatureId is { } id ? $" ({id})" : "")}: {d.Diagnostic.Message}");
        Line($"");

        Line($"## Not checked");
        Line($"");
        Line($"- **Welds.** Joints are reported as contacts between parts; no weld is modelled, sized or listed.");
        Line($"- **Fit-up gaps.** Parts are checked as drawn, touching exactly; cutting and fit-up tolerances are not.");
        Line($"- **Strength and stiffness.** Nothing is sized by calculation: no loads, stresses, deflections or buckling.");
        Line($"- **Sampled, not certified:** the minimum wall (seeded from the mesh), the mesh-vertex means, and the interface surface match (at mesh vertices; the coverage is certified). Volumes and masses are mesh estimates; their certified ranges are above.");
        return s.ToString();
    }

    static string Summary(Verification v, string check)
    {
        var tol = v.Settings.Tolerance;
        switch (check)
        {
            case Verifier.Checks.Outside when v.Outside.MaxBy(o => o.Excess.Upper) is { } o:
                return Invariant($"at most {Max(o.Excess)} mm ({o.PartFace} at design {o.DesignFace}); limit {tol} mm");
            case Verifier.Checks.Uncovered when v.Uncovered.MaxBy(f => f.Excess.Upper) is { } f:
                return Invariant($"at most {Max(f.Excess)} mm on {f.Face} ({f.PartFace} nearest); no limit");
            case Verifier.Checks.Volumes:
                return v.Volumes.PartsMass is { } mass
                    ? Invariant($"parts {Kg(mass)} kg, design {Kg(v.Volumes.DesignMass)} kg; design not covered {Num(v.Volumes.DesignUncovered / 1000, 1)} cm³")
                    : Invariant($"parts {Num(v.Volumes.Parts / 1000, 1)} cm³, design {Num(v.Volumes.Design / 1000, 1)} cm³");
            case Verifier.Checks.Contacts:
                return Invariant($"{v.Contacts.Count(c => c.Kind == ContactKind.Contact)} contacts, {v.Contacts.Count(c => c.Kind == ContactKind.Interference)} interfering, {v.Contacts.Count(c => c.Verdict == Verdict.Unknown)} unknown");
            case Verifier.Checks.Keepouts:
                return string.Join("; ", v.Keepouts.Select(k => Invariant($"{k.Body} {Span(new(k.Lower, k.Upper))} (≥ {k.Required})")));
            case Verifier.Checks.Interfaces:
                return Invariant($"{v.Interfaces.Count(i => i.Verdict == Verdict.Pass)} of {v.Interfaces.Count} reproduced") +
                    string.Concat(v.Interfaces.Where(i => i.Verdict != Verdict.Pass).Select(i => $"; {i.Face} {Name(i.Verdict)}"));
            case Verifier.Checks.Walls when v.Walls.Where(w => w.Thickness is not null).MinBy(w => w.Thickness) is { } w:
                return Invariant($"thinnest {Num(w.Thickness)} mm ({w.Part}); no limit");
            case Verifier.Checks.Fits:
                return v.Fits is null ? "no fit errors given" : v.Fits.Count == 0 ? "no fitted edges" : Invariant($"{v.Fits.Count} fitted edges, largest error {Num(v.Fits.Max(f => f.Error), 4)} mm; limit {tol} mm");
            case Verifier.Checks.Diagnostics:
                return v.Diagnostics.Count == 0 ? "none" : Invariant($"{v.Diagnostics.Count}, listed below");
            default:
                return "";
        }
    }

    static string Title(string check) => check switch
    {
        Verifier.Checks.Outside => "Parts outside the design",
        Verifier.Checks.Uncovered => "Design not covered",
        Verifier.Checks.Volumes => "Volume and mass",
        Verifier.Checks.Contacts => "Contacts and interference",
        Verifier.Checks.Keepouts => "Keep-outs",
        Verifier.Checks.Interfaces => "Interfaces",
        Verifier.Checks.Walls => "Minimum wall",
        Verifier.Checks.Fits => "Fitted edges",
        Verifier.Checks.Diagnostics => "Diagnostics",
        _ => check,
    };

    static string Name(Verdict v) => v switch
    {
        Verdict.Pass => "pass",
        Verdict.Fail => "fail",
        Verdict.Unknown => "unknown",
        Verdict.Reported => "reported",
        _ => "not checked",
    };

    static string Invariant(FormattableString s) => FormattableString.Invariant(s);

    /// <summary>A certified maximum: its attained value when settled, else its interval rounded outwards.</summary>
    static string Max(Deviation d) => d.Settled ? Num(d.Lower) : Span(new(d.Lower, d.Upper));

    /// <summary>An interval rounded outwards to 0.001; an open upper end as ≥.</summary>
    static string Span(Interval i) => double.IsPositiveInfinity(i.Hi)
        ? $"≥ {Num(Math.Floor(i.Lo * 1000) / 1000)}"
        : $"[{Num(Math.Floor(i.Lo * 1000) / 1000)}, {Num(Math.Ceiling(i.Hi * 1000) / 1000)}]";

    static string Num(double? x, int decimals = 3) => x is not { } v || double.IsNaN(v) ? "" :
        Math.Round(v, decimals) is var r && r == 0 ? 0.0.ToString("F" + decimals, CultureInfo.InvariantCulture) : r.ToString("F" + decimals, CultureInfo.InvariantCulture);

    static string Num(int x) => x.ToString(CultureInfo.InvariantCulture);

    static string Kg(double? x) => x is { } v ? Num(v, 2) : "";

    static string Pt(Point3D p) => $"({Num(p.X, 1)}, {Num(p.Y, 1)}, {Num(p.Z, 1)})";

    static void Objects<T>(Utf8JsonWriter w, string name, IEnumerable<T> items, Action<T> write)
    {
        w.WriteStartArray(name);
        foreach (var item in items)
        {
            w.WriteStartObject();
            write(item);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    /// <summary>A number, or null when there is none (NaN, infinite): JSON has no infinity.</summary>
    static void Number(Utf8JsonWriter w, string name, double? v)
    {
        if (v is { } x && double.IsFinite(x)) w.WriteNumber(name, x == 0 ? 0 : x);
        else w.WriteNull(name);
    }

    static void Range(Utf8JsonWriter w, string name, Interval i)
    {
        w.WriteStartArray(name);
        foreach (var x in new[] { i.Lo, i.Hi })
            if (double.IsFinite(x)) w.WriteNumberValue(x == 0 ? 0 : x);
            else w.WriteNullValue();
        w.WriteEndArray();
    }

    static void Point(Utf8JsonWriter w, string name, Point3D p)
    {
        w.WriteStartArray(name);
        foreach (var x in new[] { p.X, p.Y, p.Z }) w.WriteNumberValue(x == 0 ? 0 : x);
        w.WriteEndArray();
    }

    static void WriteDeviation(Utf8JsonWriter w, string name, Deviation d)
    {
        w.WriteStartObject(name);
        Range(w, "bounds", new Interval(d.Lower, d.Upper));
        w.WriteBoolean("settled", d.Settled);
        Point(w, "at", d.At);
        w.WriteEndObject();
    }
}
