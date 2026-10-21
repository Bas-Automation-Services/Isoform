using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Isoform.Document;

/// <summary>A parameter or field expression that does not give a value: unknown name, bad syntax, a cycle.</summary>
/// <param name="featureId">The feature whose field failed, or null for the parameter table.</param>
public sealed class ParameterException(string? featureId, string message) : FormatException(message)
{
    public string? FeatureId { get; } = featureId;
}

/// <summary>
/// Part parameters. A stored document keeps its parameter table and the expressions in its fields; before
/// regeneration it is resolved to an ordinary document of plain numbers, which the compiler, references and edits
/// treat like any other.
/// </summary>
public static class Parametric
{
    static readonly IReadOnlyDictionary<string, string> None = ReadOnlyDictionary<string, string>.Empty;

    /// <summary>True when the document has parameters or expressions, so its stored and resolved forms differ.</summary>
    public static bool IsParametric(PartDocument doc) => doc.Parameters.Count > 0 || doc.Expressions.Count > 0;

    /// <summary>
    /// The document as plain numbers: every parameter evaluated (all of them, used or not), every field expression
    /// replaced by its value. Ids and face names are untouched. A document without parameters comes back as it is.
    /// </summary>
    /// <exception cref="ParameterException">The first parameter (by name) or field (in document order) that fails.</exception>
    public static PartDocument Resolve(PartDocument doc)
    {
        if (!IsParametric(doc)) return doc;
        var values = Values(doc.Parameters);
        var root = CanonicalFormat.ToNode(doc with { Parameters = None, Expressions = None });
        var features = root["features"]!.AsArray();
        var position = doc.Features.Select((f, i) => (f.Id, i)).DistinctBy(x => x.Id).ToDictionary(x => x.Id, x => x.i);
        foreach (var (path, text) in doc.Expressions.OrderBy(e => position.GetValueOrDefault(Id(e.Key))).ThenBy(e => e.Key, StringComparer.Ordinal))
        {
            double value;
            try { value = Expression.Evaluate(text, name => values.TryGetValue(name, out var v) ? v : throw Unknown(name, text)); }
            catch (FormatException e) { throw new ParameterException(Id(path), $"{path}: {e.Message}"); }
            // A count stays a whole number; round() makes one of any expression.
            if (path.EndsWith(".count") && (value != Math.Floor(value) || Math.Abs(value) > int.MaxValue))
                throw new ParameterException(Id(path), FormattableString.Invariant($"{path} must be a whole number, got {value} from '{text}'"));
            DocumentEdits.Set(features, path, JsonNode.Parse(value.ToString("R", CultureInfo.InvariantCulture)));
        }
        return CanonicalFormat.FromNode(root);
    }

    /// <summary>The resolved document, or null when it does not resolve (regenerating it then says why).</summary>
    public static PartDocument? TryResolve(PartDocument doc)
    {
        try { return Resolve(doc); }
        catch (ParameterException) { return null; }
    }

    /// <summary>
    /// The document with some parameters set to other expressions, as an assembly component sets them. A name the
    /// document has no parameter for is an error: a typo must not silently keep the default.
    /// </summary>
    public static PartDocument Override(PartDocument doc, IReadOnlyDictionary<string, string>? values)
    {
        if (values is not { Count: > 0 }) return doc;
        if (values.Keys.Order(StringComparer.Ordinal).FirstOrDefault(k => !doc.Parameters.ContainsKey(k)) is { } unknown)
            throw new ParameterException(null, $"the part has no parameter '{unknown}'");
        var merged = new Dictionary<string, string>(doc.Parameters);
        foreach (var (name, expression) in values) merged[name] = expression;
        return doc with { Parameters = merged };
    }

    /// <summary>Every parameter's value, evaluated on demand so the table may be in any order; cycles are named.</summary>
    static Dictionary<string, double> Values(IReadOnlyDictionary<string, string> parameters)
    {
        var names = parameters.Keys.Order(StringComparer.Ordinal).ToList();
        if (names.FirstOrDefault(n => !Expression.IsName(n) || Expression.Reserved.Contains(n)) is { } bad)
            throw new ParameterException(null, Expression.IsName(bad)
                ? $"'{bad}' is a function or constant and cannot name a parameter"
                : $"'{bad}' cannot name a parameter: use a letter or '_', then letters, digits or '_'");

        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        var open = new List<string>();
        double Value(string name)
        {
            if (values.TryGetValue(name, out var known)) return known;
            if (open.Contains(name))
                throw new ParameterException(null, $"parameters depend on each other in a cycle: {string.Join(" -> ", open.Skip(open.IndexOf(name)).Append(name))}");
            var text = parameters[name];
            open.Add(name);
            double value;
            try { value = Expression.Evaluate(text, n => parameters.ContainsKey(n) ? Value(n) : throw Unknown(n, text)); }
            catch (FormatException e) when (e is not ParameterException) { throw new ParameterException(null, $"parameter '{name}': {e.Message}"); }
            open.RemoveAt(open.Count - 1);
            return values[name] = value;
        }
        foreach (var name in names) Value(name);
        return values;
    }

    static FormatException Unknown(string name, string text) => new($"unknown parameter '{name}' in '{text}'");

    static string Id(string path) => path.Split('.')[0];
}
