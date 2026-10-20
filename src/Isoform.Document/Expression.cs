using System.Globalization;

namespace Isoform.Document;

/// <summary>
/// Units-free arithmetic over named parameters: <c>+ - * / ^</c>, parentheses, unary minus, <c>pi</c>, and the
/// functions min, max, abs, sqrt, sin, cos, tan (degrees), round, floor and ceil. <c>^</c> binds tighter than
/// unary minus and to the right, as in mathematics: <c>-2^2</c> is −4, <c>2^3^2</c> is 512.
/// </summary>
public static class Expression
{
    /// <summary>Function and constant names, which parameters cannot take.</summary>
    public static readonly IReadOnlySet<string> Reserved = new HashSet<string>(
        ["pi", "min", "max", "abs", "sqrt", "sin", "cos", "tan", "round", "floor", "ceil"], StringComparer.Ordinal);

    /// <summary>The value of <paramref name="text"/>; <paramref name="lookup"/> gives a name's value or throws.</summary>
    /// <exception cref="FormatException">The text is not an expression, or its value is not a finite number.</exception>
    public static double Evaluate(string text, Func<string, double> lookup)
    {
        var parser = new Parser(text, lookup);
        var value = parser.Sum();
        if (parser.Peek() is { } c) throw parser.Error($"unexpected '{c}'");
        return double.IsFinite(value) ? value : throw new FormatException($"'{text}' is not a finite number");
    }

    /// <summary>True for a name a parameter may have: a letter or '_', then letters, digits or '_'.</summary>
    public static bool IsName(string name) =>
        name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] == '_') && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    sealed class Parser(string text, Func<string, double> lookup)
    {
        int _at;

        public char? Peek()
        {
            while (_at < text.Length && char.IsWhiteSpace(text[_at])) _at++;
            return _at < text.Length ? text[_at] : null;
        }

        bool Take(char c)
        {
            if (Peek() != c) return false;
            _at++;
            return true;
        }

        public FormatException Error(string message) => new($"{message} at position {_at + 1} of '{text}'");

        public double Sum()
        {
            var value = Product();
            while (true)
            {
                if (Take('+')) value += Product();
                else if (Take('-')) value -= Product();
                else return value;
            }
        }

        double Product()
        {
            var value = Unary();
            while (true)
            {
                if (Take('*')) value *= Unary();
                else if (Take('/'))
                {
                    var divisor = Unary();
                    value = divisor != 0 ? value / divisor : throw new FormatException($"division by zero in '{text}'");
                }
                else return value;
            }
        }

        double Unary() => Take('-') ? -Unary() : Take('+') ? Unary() : Power();

        // Right-associative, and the exponent may carry its own sign: 2^-1.
        double Power()
        {
            var value = Primary();
            return Take('^') ? Math.Pow(value, Unary()) : value;
        }

        double Primary()
        {
            var c = Peek() ?? throw Error("expression ends early");
            if (Take('('))
            {
                var inner = Sum();
                return Take(')') ? inner : throw Error("missing ')'");
            }
            if (char.IsAsciiDigit(c) || c == '.') return Number();
            if (!char.IsAsciiLetter(c) && c != '_') throw Error($"unexpected '{c}'");
            var start = _at;
            while (_at < text.Length && (char.IsAsciiLetterOrDigit(text[_at]) || text[_at] == '_')) _at++;
            var name = text[start.._at];
            if (!Take('(')) return name == "pi" ? Math.PI : lookup(name);

            var args = new List<double>();
            if (!Take(')'))
            {
                do args.Add(Sum());
                while (Take(','));
                if (!Take(')')) throw Error("missing ')'");
            }
            return Call(name, args);
        }

        double Number()
        {
            var start = _at;
            while (_at < text.Length && (char.IsAsciiDigit(text[_at]) || text[_at] == '.')) _at++;
            // An exponent only when digits follow, so "2e" stays an error rather than a silent 2.
            if (_at < text.Length && text[_at] is 'e' or 'E')
            {
                var e = _at + 1;
                if (e < text.Length && text[e] is '+' or '-') e++;
                if (e < text.Length && char.IsAsciiDigit(text[e]))
                {
                    _at = e;
                    while (_at < text.Length && char.IsAsciiDigit(text[_at])) _at++;
                }
            }
            var token = text[start.._at];
            return double.TryParse(token, NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var v)
                ? v : throw Error($"'{token}' is not a number");
        }

        double Call(string name, List<double> args)
        {
            double One() => args.Count == 1 ? args[0] : throw new FormatException($"{name} takes one argument, got {args.Count}, in '{text}'");
            return name switch
            {
                "min" or "max" when args.Count == 0 => throw new FormatException($"{name} needs at least one argument in '{text}'"),
                "min" => args.Min(),
                "max" => args.Max(),
                "abs" => Math.Abs(One()),
                "sqrt" => One() is >= 0 and var x ? Math.Sqrt(x) : throw new FormatException($"sqrt of a negative number in '{text}'"),
                "sin" => Trig(One(), Math.Sin, [0, 1, 0, -1]),
                "cos" => Trig(One(), Math.Cos, [1, 0, -1, 0]),
                "tan" => Tangent(One()),
                "round" => Math.Round(One(), MidpointRounding.AwayFromZero),
                "floor" => Math.Floor(One()),
                "ceil" => Math.Ceiling(One()),
                _ => throw new FormatException($"unknown function '{name}' in '{text}'"),
            };
        }

        // Exact at multiples of 90°, so a point placed with cos(90) or sin(180) lies exactly on its axis.
        static double Trig(double degrees, Func<double, double> f, double[] quarters)
        {
            var r = degrees % 360;
            return r % 90 == 0 ? quarters[((int)(r / 90) + 4) % 4] : f(r * Math.PI / 180);
        }

        double Tangent(double degrees) => Trig(degrees, Math.Cos, [1, 0, -1, 0]) is var cos && cos != 0
            ? Trig(degrees, Math.Sin, [0, 1, 0, -1]) / cos
            : throw new FormatException($"tan({degrees}) is undefined in '{text}'");
    }
}
