using System.Globalization;
using System.Text;

namespace Spotlight.Calc;

/// <summary>
/// Evaluates calculator input such as "2(3+4)", "15% of" style percentages ("200 + 10%"), "5!", "sqrt 16",
/// "sin(90°)" or "2^10". Only real calculations produce a result: a lone number or word never does.
/// </summary>
public static class MathEvaluator
{
    private enum Kind { Number, Ident, Op, Open, Close }

    private readonly record struct Token(Kind Kind, string Text, double Value = 0);

    private readonly record struct Operand(double Value, bool IsPercent);

    private static readonly HashSet<string> Functions = new(StringComparer.Ordinal)
    {
        "sqrt", "cbrt", "abs", "sin", "cos", "tan", "asin", "acos", "atan", "sinh", "cosh", "tanh",
        "ln", "log", "log2", "log10", "exp", "round", "floor", "ceil",
    };

    private static readonly Dictionary<string, double> Constants = new(StringComparer.Ordinal)
    {
        ["pi"] = Math.PI, ["π"] = Math.PI, ["e"] = Math.E, ["tau"] = Math.Tau, ["τ"] = Math.Tau,
        ["phi"] = (1 + Math.Sqrt(5)) / 2, ["φ"] = (1 + Math.Sqrt(5)) / 2,
    };

    public static bool TryEvaluate(string input, CultureInfo culture, out CalcResult result)
    {
        result = null!;
        try
        {
            string text = input.Trim();
            if (text.StartsWith('=')) text = text[1..].TrimStart();
            if (text.Length == 0 || text.Length > 200 || !Tokenize(text, culture, out var tokens)) return false;

            var parser = new Parser(tokens);
            double value = parser.ParseExpression().Value;
            if (!parser.AtEnd || parser.Operations == 0 || double.IsNaN(value) || double.IsInfinity(value)) return false;

            result = new CalcResult(
                CalcKind.Math,
                Render(tokens),
                NumberParsing.Format(value, culture, grouping: true),
                NumberParsing.Format(value, culture, grouping: false),
                null,
                null);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ---- tokenizer ------------------------------------------------------------------------------

    private static bool Tokenize(string s, CultureInfo culture, out List<Token> tokens)
    {
        tokens = [];
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (char.IsAsciiDigit(c) || ((c is '.' or ',') && i + 1 < s.Length && char.IsAsciiDigit(s[i + 1])))
            {
                int start = i;
                if (!NumberParsing.TryRead(s, ref i, culture, out double v)) return false;
                tokens.Add(new Token(Kind.Number, s[start..i], v));
                continue;
            }

            if (char.IsLetter(c))
            {
                int start = i;
                while (i < s.Length && char.IsLetter(s[i])) i++;
                string word = s[start..i].ToLowerInvariant();
                if (word == "log" && i < s.Length && char.IsAsciiDigit(s[i]))
                {
                    string digits = s[i..].StartsWith("10", StringComparison.Ordinal) ? "10" : s[i] == '2' ? "2" : "";
                    if (digits.Length > 0 && (i + digits.Length >= s.Length || !char.IsAsciiDigit(s[i + digits.Length])))
                    {
                        word += digits;
                        i += digits.Length;
                    }
                }
                bool afterValue = tokens.Count > 0 && tokens[^1].Kind is Kind.Number or Kind.Close;
                bool afterPercent = tokens.Count > 0 && tokens[^1] is { Kind: Kind.Op, Text: "%" };
                // "3x4" and "15% of 200" / "15% von 200" read as multiplication.
                if ((word == "x" && afterValue) || (word is "of" or "von" && afterPercent))
                {
                    tokens.Add(new Token(Kind.Op, "*"));
                    continue;
                }
                if (!Functions.Contains(word) && !Constants.ContainsKey(word) && word is not ("mod" or "deg")) return false;
                tokens.Add(new Token(Kind.Ident, word));
                continue;
            }

            string? op = c switch
            {
                '+' => "+",
                '-' or '−' or '–' => "-",
                '*' when i + 1 < s.Length && s[i + 1] == '*' => "^",
                '*' or '×' or '·' or '⋅' => "*",
                '/' or '÷' or ':' => "/",
                '^' => "^",
                '%' => "%",
                '!' => "!",
                '°' => "°",
                '√' => "√",
                '²' => "²",
                '³' => "³",
                _ => null,
            };
            if (c is '(' or '[') { tokens.Add(new Token(Kind.Open, "(")); i++; continue; }
            if (c is ')' or ']') { tokens.Add(new Token(Kind.Close, ")")); i++; continue; }
            if (op == null) return false;
            tokens.Add(new Token(Kind.Op, op));
            i += c == '*' && op == "^" ? 2 : 1;
        }
        return tokens.Count > 0;
    }

    private static bool StartsOperand(Token t) =>
        t.Kind is Kind.Number or Kind.Open
        || (t.Kind == Kind.Ident && t.Text is not ("mod" or "deg"))
        || (t.Kind == Kind.Op && t.Text == "√");

    // ---- parser (recursive descent, evaluates while parsing) -------------------------------------

    private sealed class Parser(List<Token> tokens)
    {
        private int _pos;

        /// <summary>Real operations seen; unary minus and a lone "e" don't count.</summary>
        public int Operations { get; private set; }

        public bool AtEnd => _pos >= tokens.Count;

        private Token Peek(int ahead = 0) =>
            _pos + ahead < tokens.Count ? tokens[_pos + ahead] : new Token(Kind.Close, "<end>");

        private bool PeekOp(string op, int ahead = 0) => _pos + ahead < tokens.Count && Peek(ahead) is { Kind: Kind.Op } t && t.Text == op;

        public Operand ParseExpression()
        {
            var left = ParseProduct();
            while (PeekOp("+") || PeekOp("-"))
            {
                string op = Peek().Text;
                _pos++;
                var right = ParseProduct();
                Operations++;
                // Pocket-calculator percentages: 200 + 10% = 220, 200 - 10% = 180.
                double amount = right.IsPercent ? left.Value * right.Value : right.Value;
                left = new Operand(op == "+" ? left.Value + amount : left.Value - amount, false);
            }
            return left;
        }

        private Operand ParseProduct()
        {
            var left = ParseUnary();
            while (true)
            {
                var t = Peek();
                if (PeekOp("*") || PeekOp("/"))
                {
                    _pos++;
                    var right = ParseUnary();
                    Operations++;
                    left = new Operand(t.Text == "*" ? left.Value * right.Value : left.Value / right.Value, false);
                }
                else if ((t.Kind == Kind.Ident && t.Text == "mod") || (PeekOp("%") && StartsOperand(Peek(1))))
                {
                    _pos++;
                    var right = ParseUnary();
                    Operations++;
                    left = new Operand(left.Value % right.Value, false);
                }
                else if (_pos < tokens.Count && ImplicitMultiplication(t))
                {
                    var right = ParseUnary();
                    Operations++;
                    left = new Operand(left.Value * right.Value, false);
                }
                else
                {
                    return left;
                }
            }
        }

        // "2pi", "2(3+4)", "(1+2)(3+4)", "3 sqrt 4" — but never two plain numbers ("2 3").
        private bool ImplicitMultiplication(Token next)
        {
            if (!StartsOperand(next)) return false;
            if (next.Kind != Kind.Number) return true;
            return _pos > 0 && tokens[_pos - 1].Kind == Kind.Close;
        }

        private Operand ParseUnary()
        {
            if (PeekOp("-") || PeekOp("+"))
            {
                bool negate = Peek().Text == "-";
                _pos++;
                var v = ParseUnary();
                return new Operand(negate ? -v.Value : v.Value, v.IsPercent);
            }
            if (PeekOp("√"))
            {
                _pos++;
                var v = ParseUnary();
                Operations++;
                return new Operand(Math.Sqrt(v.Value), false);
            }
            return ParsePower();
        }

        private Operand ParsePower()
        {
            var b = ParsePostfix();
            if (!PeekOp("^")) return b;
            _pos++;
            var exponent = ParseUnary(); // right-associative: 2^3^2 = 2^9
            Operations++;
            return new Operand(Math.Pow(b.Value, exponent.Value), false);
        }

        private Operand ParsePostfix()
        {
            var v = ParsePrimary();
            while (true)
            {
                var t = Peek();
                if (PeekOp("!"))
                {
                    _pos++;
                    Operations++;
                    v = new Operand(Factorial(v.Value), false);
                }
                else if (PeekOp("%") && !StartsOperand(Peek(1)))
                {
                    _pos++;
                    Operations++;
                    v = new Operand(v.Value / 100, true);
                }
                else if (PeekOp("°") || (t.Kind == Kind.Ident && t.Text == "deg"))
                {
                    _pos++; // degrees → radians; only meaningful inside a calculation, so not counted
                    v = new Operand(v.Value * Math.PI / 180, false);
                }
                else if (PeekOp("²") || PeekOp("³"))
                {
                    _pos++;
                    Operations++;
                    v = new Operand(Math.Pow(v.Value, t.Text == "²" ? 2 : 3), false);
                }
                else
                {
                    return v;
                }
            }
        }

        private Operand ParsePrimary()
        {
            var t = Peek();
            switch (t.Kind)
            {
                case Kind.Number:
                    _pos++;
                    return new Operand(t.Value, false);
                case Kind.Open:
                {
                    _pos++;
                    var inner = ParseExpression();
                    if (Peek().Kind != Kind.Close || _pos >= tokens.Count) throw new FormatException("missing )");
                    _pos++;
                    return new Operand(inner.Value, false);
                }
                case Kind.Ident when Constants.TryGetValue(t.Text, out double constant):
                    _pos++;
                    if (t.Text != "e") Operations++; // "pi" alone is worth showing, a lone "e" is just a letter
                    return new Operand(constant, false);
                case Kind.Ident when Functions.Contains(t.Text):
                {
                    _pos++;
                    var argument = Peek().Kind == Kind.Open ? ParsePrimary() : ParsePostfix();
                    Operations++;
                    return new Operand(Apply(t.Text, argument.Value), false);
                }
                default:
                    throw new FormatException("operand expected");
            }
        }
    }

    private static double Apply(string function, double x) => function switch
    {
        "sqrt" => Math.Sqrt(x),
        "cbrt" => Math.Cbrt(x),
        "abs" => Math.Abs(x),
        "sin" => Clean(Math.Sin(x)),
        "cos" => Clean(Math.Cos(x)),
        "tan" => Clean(Math.Tan(x)),
        "asin" => Math.Asin(x),
        "acos" => Math.Acos(x),
        "atan" => Math.Atan(x),
        "sinh" => Math.Sinh(x),
        "cosh" => Math.Cosh(x),
        "tanh" => Math.Tanh(x),
        "ln" => Math.Log(x),
        "log" or "log10" => Math.Log10(x),
        "log2" => Math.Log2(x),
        "exp" => Math.Exp(x),
        "round" => Math.Round(x, MidpointRounding.AwayFromZero),
        "floor" => Math.Floor(x),
        "ceil" => Math.Ceiling(x),
        _ => double.NaN,
    };

    // sin(180°) is 1.2e-16 in floating point; show 0 like a calculator does.
    private static double Clean(double v) => Math.Abs(v) < 1e-12 ? 0 : v;

    private static double Factorial(double n)
    {
        if (n < 0 || n > 170 || Math.Abs(n - Math.Round(n)) > 1e-9) return double.NaN;
        double result = 1;
        for (int i = 2; i <= (int)Math.Round(n); i++) result *= i;
        return result;
    }

    // ---- canonical rendering: "2*(3+4)" → "2 × (3 + 4)" ------------------------------------------

    private static string Render(List<Token> tokens)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            var prev = i > 0 ? tokens[i - 1] : (Token?)null;
            bool prevIsValue = prev is { Kind: Kind.Number or Kind.Close }
                || (prev is { Kind: Kind.Ident } p && Constants.ContainsKey(p.Text))
                || (prev is { Kind: Kind.Op } po && po.Text is "!" or "°" or "²" or "³" || (prev is { Kind: Kind.Op, Text: "%" } && !(i < tokens.Count && StartsOperand(t))));

            switch (t.Kind)
            {
                case Kind.Op when t.Text is "+" or "-" && prevIsValue:
                case Kind.Op when t.Text is "*" or "/" or "^":
                case Kind.Op when t.Text == "%" && i + 1 < tokens.Count && StartsOperand(tokens[i + 1]):
                    sb.Append(' ').Append(t.Text switch { "*" => "×", "/" => "÷", "%" => "mod", _ => t.Text }).Append(' ');
                    break;
                case Kind.Ident when t.Text == "mod":
                    sb.Append(" mod ");
                    break;
                case Kind.Number when prev is { Kind: Kind.Ident } f && Functions.Contains(f.Text):
                    sb.Append(' ').Append(t.Text);
                    break;
                default:
                    sb.Append(t.Kind == Kind.Ident && t.Text == "deg" ? "°" : t.Text);
                    break;
            }
        }
        return sb.ToString().Trim();
    }
}
