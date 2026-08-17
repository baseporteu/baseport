using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Baseport;

public static class JsExpr
{
    private enum Tok { Num, Str, Id, Punc }
    private sealed class Token { public Tok Kind; public string Text = ""; public double Num; public int Pos; }

    private static readonly string[] MultiOps = { "===", "!==", "&&", "||", "<=", ">=", "==", "!=", "+=", "-=", "*=", "/=", "=" };

    private abstract class Node { }
    private sealed class NumN : Node { public double V; }
    private sealed class StrN : Node { public string V = ""; }
    private sealed class IdN : Node { public string Name = ""; }
    private sealed class MemberN : Node { public Node Base = null!; public string Prop = ""; }
    private sealed class IndexN : Node { public Node Base = null!; public Node Idx = null!; }
    private sealed class CallN : Node { public Node Callee = null!; public List<Node> Args = new(); }
    private sealed class UnaryN : Node { public string Op = ""; public Node Operand = null!; }
    private sealed class BinaryN : Node { public string Op = ""; public Node L = null!; public Node R = null!; }
    private sealed class TernaryN : Node { public Node C = null!; public Node T = null!; public Node E = null!; }
    private sealed class MethodVal { public object Target = null!; public string Name = ""; }

    private static readonly HashSet<string> Builtins = new()
        { "round", "abs", "floor", "ceil", "sqrt", "pow", "min", "max", "sign", "trunc", "Number", "String", "GETDATE", "encodeURIComponent", "SUM" };

    public sealed class ValidationResult
    {
        public bool Valid;
        public readonly List<string> Errors = new();
        public readonly List<string> ReferencedFields = new();
    }

    private static List<Token> Tokenize(string src)
    {
        var toks = new List<Token>();
        int i = 0;
        while (i < src.Length)
        {
            char c = src[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (char.IsDigit(c) || (c == '.' && i + 1 < src.Length && char.IsDigit(src[i + 1])))
            {
                int start = i;
                while (i < src.Length && char.IsDigit(src[i])) i++;
                if (i < src.Length && src[i] == '.') { i++; while (i < src.Length && char.IsDigit(src[i])) i++; }
                if (i < src.Length && (src[i] == 'e' || src[i] == 'E'))
                {
                    int j = i + 1;
                    if (j < src.Length && (src[j] == '+' || src[j] == '-')) j++;
                    if (j < src.Length && char.IsDigit(src[j]))
                    {
                        i = j;
                        while (i < src.Length && char.IsDigit(src[i])) i++;
                    }
                }
                toks.Add(new Token { Kind = Tok.Num, Num = double.Parse(src[start..i], CultureInfo.InvariantCulture), Pos = start });
                continue;
            }

            if (c == '\'' || c == '"' || c == '`')
            {
                char q = c;
                int start = i;
                i++;
                var sb = new StringBuilder();
                while (i < src.Length && src[i] != q)
                {
                    if (src[i] == '\\' && i + 1 < src.Length) { sb.Append(src[i + 1]); i += 2; }
                    else { sb.Append(src[i]); i++; }
                }
                if (i < src.Length) i++;
                toks.Add(new Token { Kind = Tok.Str, Text = sb.ToString(), Pos = start });
                continue;
            }

            if (char.IsLetter(c) || c == '_' || c == '$')
            {
                int start = i;
                while (i < src.Length && (char.IsLetterOrDigit(src[i]) || src[i] == '_' || src[i] == '$')) i++;
                toks.Add(new Token { Kind = Tok.Id, Text = src[start..i], Pos = start });
                continue;
            }

            bool matched = false;
            foreach (var op in MultiOps)
            {
                if (i + op.Length <= src.Length && src.Substring(i, op.Length) == op)
                {
                    toks.Add(new Token { Kind = Tok.Punc, Text = op, Pos = i });
                    i += op.Length;
                    matched = true;
                    break;
                }
            }
            if (matched) continue;

            if ("+-*/%()[].,?!:".IndexOf(c) >= 0)
            {
                toks.Add(new Token { Kind = Tok.Punc, Text = c.ToString(), Pos = i });
                i++;
                continue;
            }
            toks.Add(new Token { Kind = Tok.Punc, Text = c.ToString(), Pos = i });
            i++;
        }
        return toks;
    }

    private sealed class Parser
    {
        private readonly List<Token> _toks;
        private int _pos;
        public Parser(List<Token> toks) { _toks = toks; }
        public int Pos => _pos;
        private bool At(string t) => _pos < _toks.Count && _toks[_pos].Kind == Tok.Punc && _toks[_pos].Text == t;
        private void Expect(string t) { if (!At(t)) throw new FormatException($"Expected '{t}'"); _pos++; }

        public Node ParseExpression() => ParseTernary();

        private Node ParseTernary()
        {
            var c = ParseLogical();
            if (At("?"))
            {
                _pos++;
                var t = ParseTernary();
                Expect(":");
                var e = ParseTernary();
                return new TernaryN { C = c, T = t, E = e };
            }
            return c;
        }

        private Node ParseLogical() => ParseBinaryLevel(ParseEquality, new[] { "&&", "||" });
        private Node ParseEquality() => ParseBinaryLevel(ParseRelational, new[] { "==", "!=", "===", "!==" });
        private Node ParseRelational() => ParseBinaryLevel(ParseAdditive, new[] { "<", "<=", ">", ">=" });
        private Node ParseAdditive() => ParseBinaryLevel(ParseMultiplicative, new[] { "+", "-" });
        private Node ParseMultiplicative() => ParseBinaryLevel(ParseUnary, new[] { "*", "/", "%" });

        private Node ParseBinaryLevel(Func<Node> next, string[] ops)
        {
            var l = next();
            while (_pos < _toks.Count && _toks[_pos].Kind == Tok.Punc && ops.Contains(_toks[_pos].Text))
            {
                var op = _toks[_pos].Text;
                _pos++;
                var r = next();
                l = new BinaryN { Op = op, L = l, R = r };
            }
            return l;
        }

        private Node ParseUnary()
        {
            if (At("!")) { _pos++; return new UnaryN { Op = "!", Operand = ParseUnary() }; }
            if (At("-")) { _pos++; return new UnaryN { Op = "-", Operand = ParseUnary() }; }
            return ParsePostfix();
        }

        private Node ParsePostfix()
        {
            var e = ParsePrimary();
            while (true)
            {
                if (At("."))
                {
                    _pos++;
                    if (_pos >= _toks.Count || _toks[_pos].Kind != Tok.Id) throw new FormatException("Expected property name after '.'");
                    e = new MemberN { Base = e, Prop = _toks[_pos].Text };
                    _pos++;
                }
                else if (At("["))
                {
                    _pos++;
                    var idx = ParseExpression();
                    Expect("]");
                    e = new IndexN { Base = e, Idx = idx };
                }
                else if (At("("))
                {
                    _pos++;
                    var args = new List<Node>();
                    if (!At(")"))
                    {
                        args.Add(ParseExpression());
                        while (At(",")) { _pos++; args.Add(ParseExpression()); }
                    }
                    Expect(")");
                    e = new CallN { Callee = e, Args = args };
                }
                else break;
            }
            return e;
        }

        private Node ParsePrimary()
        {
            if (_pos >= _toks.Count) throw new FormatException("Unexpected end of expression");
            var t = _toks[_pos];
            if (t.Kind == Tok.Num) { _pos++; return new NumN { V = t.Num }; }
            if (t.Kind == Tok.Str) { _pos++; return new StrN { V = t.Text }; }
            if (t.Kind == Tok.Id) { _pos++; return new IdN { Name = t.Text }; }
            if (At("(")) { _pos++; var e = ParseExpression(); Expect(")"); return e; }
            throw new FormatException($"Unexpected token '{t.Text}'");
        }
    }

    private static Node? ParseTree(string src)
    {
        src = (src ?? "").Trim();
        if (src.Length == 0) return null;
        var toks = Tokenize(src);
        var p = new Parser(toks);
        var node = p.ParseExpression();
        if (p.Pos < toks.Count) throw new FormatException($"Unexpected token '{toks[p.Pos].Text}'");
        return node;
    }

    public static ValidationResult Validate(string expr, IReadOnlyCollection<string> fieldNames)
    {
        var r = new ValidationResult { Valid = true };
        Node? tree;
        try { tree = ParseTree(expr); }
        catch (FormatException ex) { r.Errors.Add(ex.Message); r.Valid = false; return r; }
        if (tree == null) { r.Errors.Add("Expression is empty."); r.Valid = false; return r; }
        Walk(tree, null, r, fieldNames);
        r.Valid = r.Errors.Count == 0;
        return r;
    }

    private static void Walk(Node n, Node? parent, ValidationResult r, IReadOnlyCollection<string> fieldNames)
    {
        switch (n)
        {
            case IdN id:
                if (id.Name == "data")
                {
                    if (parent is not (MemberN or IndexN or CallN)) r.Errors.Add("'data' must be accessed as data.Field");
                    return;
                }
                if (fieldNames.Contains(id.Name))
                {
                    if (!r.ReferencedFields.Contains(id.Name)) r.ReferencedFields.Add(id.Name);
                    return;
                }
                if (parent is CallN c && ReferenceEquals(c.Callee, n) && Builtins.Contains(id.Name)) return;
                r.Errors.Add($"Unknown identifier '{id.Name}'.");
                return;
            case MemberN m:
                Walk(m.Base, m, r, fieldNames);
                if (m.Base is IdN b && b.Name == "data")
                {
                    if (!fieldNames.Contains(m.Prop)) r.Errors.Add($"Unknown field '{m.Prop}'.");
                    else if (!r.ReferencedFields.Contains(m.Prop)) r.ReferencedFields.Add(m.Prop);
                }
                return;
            case IndexN ix:
                Walk(ix.Base, ix, r, fieldNames);
                Walk(ix.Idx, ix, r, fieldNames);
                if (ix.Base is IdN ib && ib.Name == "data" && ix.Idx is StrN s)
                {
                    if (!fieldNames.Contains(s.V)) r.Errors.Add($"Unknown field '{s.V}'.");
                    else if (!r.ReferencedFields.Contains(s.V)) r.ReferencedFields.Add(s.V);
                }
                return;
            case CallN call:
                if (call.Callee is IdN idc)
                {
                    if (!Builtins.Contains(idc.Name)) { r.Errors.Add($"Unknown function '{idc.Name}'."); return; }
                    // SUM iterates a line-items array field directly, so it takes the field itself, not data.Field, and a
                    // literal column name, not an expression - the same narrow shape every other JsExpr array access uses.
                    if (idc.Name == "SUM" && (call.Args.Count != 2 || call.Args[0] is not IdN || call.Args[1] is not StrN))
                    {
                        r.Errors.Add("SUM expects SUM(field, 'column').");
                        return;
                    }
                }
                else Walk(call.Callee, call, r, fieldNames);
                foreach (var a in call.Args) Walk(a, call, r, fieldNames);
                return;
            case UnaryN u: Walk(u.Operand, u, r, fieldNames); return;
            case BinaryN bn: Walk(bn.L, bn, r, fieldNames); Walk(bn.R, bn, r, fieldNames); return;
            case TernaryN t: Walk(t.C, t, r, fieldNames); Walk(t.T, t, r, fieldNames); Walk(t.E, t, r, fieldNames); return;
        }
    }

    public static object Evaluate(string expr, Func<string, JsonNode?> get)
    {
        var tree = ParseTree(expr) ?? throw new FormatException("Expression is empty.");
        return Eval(tree, get);
    }

    private static object Eval(Node n, Func<string, JsonNode?> get)
    {
        switch (n)
        {
            case NumN x: return x.V;
            case StrN x: return x.V;
            case IdN id:
                if (id.Name == "data") throw new FormatException("'data' must be accessed as data.Field");
                return Coerce(get(id.Name));
            case MemberN m:
                if (m.Base is IdN db && db.Name == "data") return Coerce(get(m.Prop));
                return new MethodVal { Target = Eval(m.Base, get), Name = m.Prop };
            case IndexN ix:
                if (ix.Base is IdN di && di.Name == "data") return Coerce(get(AsStr(Eval(ix.Idx, get))));
                throw new FormatException("Only data[...] may be indexed.");
            case CallN call:
                if (call.Callee is IdN idc)
                {
                    // SUM reads the field's raw JsonArray straight from `get`, bypassing Coerce (which flattens
                    // a non-scalar to 0) and Builtin's eager numeric arg evaluation.
                    if (idc.Name == "SUM" && call.Args.Count == 2 && call.Args[0] is IdN sumField && call.Args[1] is StrN sumCol)
                        return SumArrayColumn(get(sumField.Name), sumCol.V);
                    return Builtin(idc.Name, call.Args.Select(a => Eval(a, get)).ToList());
                }
                return InvokeMethod(Eval(call.Callee, get), call.Args.Select(a => Eval(a, get)).ToList());
            case UnaryN u:
                var v = Eval(u.Operand, get);
                return u.Op == "!" ? !AsBool(v) : -AsNum(v);
            case BinaryN b: return Binary(b.Op, Eval(b.L, get), Eval(b.R, get));
            case TernaryN t: return AsBool(Eval(t.C, get)) ? Eval(t.T, get) : Eval(t.E, get);
        }
        throw new FormatException("Unsupported expression.");
    }

    private static object Coerce(JsonNode? v)
    {
        if (v is JsonValue jv && jv.GetValueKind() != JsonValueKind.Null)
        {
            switch (jv.GetValueKind())
            {
                case JsonValueKind.Number: return jv.GetValue<double>();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                case JsonValueKind.String:
                    var s = jv.GetValue<string>();
                    return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : s;
            }
        }
        return 0.0;
    }

    private static double SumArrayColumn(JsonNode? field, string column)
    {
        if (field is not JsonArray rows) return 0.0;
        double total = 0;
        foreach (var row in rows)
            if (row is JsonObject obj && obj.TryGetPropertyValue(column, out var cell))
                total += AsNum(Coerce(cell));
        return total;
    }

    private static object InvokeMethod(object m, List<object> args)
    {
        if (m is not MethodVal mv) throw new FormatException("Only a field value may be called as a method.");
        return mv.Name switch
        {
            "toFixed" => AsNum(mv.Target).ToString("F" + (args.Count > 0 ? (int)AsNum(args[0]) : 2), CultureInfo.InvariantCulture),
            "toString" => AsStr(mv.Target),
            "toUpperCase" => AsStr(mv.Target).ToUpperInvariant(),
            "toLowerCase" => AsStr(mv.Target).ToLowerInvariant(),
            "trim" => AsStr(mv.Target).Trim(),
            "includes" => AsStr(mv.Target).Contains(AsStr(args.Count > 0 ? args[0] : ""), StringComparison.Ordinal),
            "startsWith" => AsStr(mv.Target).StartsWith(AsStr(args.Count > 0 ? args[0] : ""), StringComparison.Ordinal),
            "endsWith" => AsStr(mv.Target).EndsWith(AsStr(args.Count > 0 ? args[0] : ""), StringComparison.Ordinal),
            "replace" => AsStr(mv.Target).Replace(AsStr(args.Count > 0 ? args[0] : ""), AsStr(args.Count > 1 ? args[1] : "")),
            _ => throw new FormatException($"Unsupported method '{mv.Name}'.")
        };
    }

    private static object Builtin(string name, List<object> args)
    {
        var nums = args.Select(AsNum).ToList();
        switch (name)
        {
            case "GETDATE": return DateTime.UtcNow.ToString("yyyy-MM-dd");
            case "round": return nums.Count > 0 ? Math.Round(nums[0]) : 0.0;
            case "abs": return Math.Abs(nums[0]);
            case "floor": return Math.Floor(nums[0]);
            case "ceil": return Math.Ceiling(nums[0]);
            case "sqrt": return Math.Sqrt(nums[0]);
            case "pow": return Math.Pow(nums[0], nums.Count > 1 ? nums[1] : 2);
            case "min": return nums.Count > 0 ? nums.Min() : 0.0;
            case "max": return nums.Count > 0 ? nums.Max() : 0.0;
            case "sign": return Math.Sign(nums[0]);
            case "trunc": return Math.Truncate(nums[0]);
            case "Number": return nums.Count > 0 ? nums[0] : 0.0;
            case "String": return args.Count > 0 ? AsStr(args[0]) : "";
            case "encodeURIComponent": return Uri.EscapeDataString(args.Count > 0 ? AsStr(args[0]) : "");
        }
        throw new FormatException($"Unknown function '{name}'.");
    }

    private static object Binary(string op, object l, object r)
    {
        switch (op)
        {
            case "+": return (l is string or MethodVal || r is string or MethodVal) ? AsStr(l) + AsStr(r) : AsNum(l) + AsNum(r);
            case "-": return AsNum(l) - AsNum(r);
            case "*": return AsNum(l) * AsNum(r);
            case "/": return AsNum(r) == 0 ? 0.0 : AsNum(l) / AsNum(r);
            case "%": return AsNum(r) == 0 ? 0.0 : AsNum(l) % AsNum(r);
            case "<":
            case "<=":
            case ">":
            case ">=":
                if (l is string && r is string)
                {
                    int c = string.CompareOrdinal(AsStr(l), AsStr(r));
                    return op == "<" ? c < 0 : op == "<=" ? c <= 0 : op == ">" ? c > 0 : c >= 0;
                }
                double a = AsNum(l), b = AsNum(r);
                return op == "<" ? a < b : op == "<=" ? a <= b : op == ">" ? a > b : a >= b;
            case "==": case "===": return op == "===" ? StrictEq(l, r) : AsStr(l) == AsStr(r);
            case "!=": case "!==": return op == "!==" ? !StrictEq(l, r) : AsStr(l) != AsStr(r);
            case "&&": return AsBool(l) ? r : l;
            case "||": return AsBool(l) ? l : r;
        }
        throw new FormatException($"Unsupported operator '{op}'.");
    }

    private static bool StrictEq(object l, object r) =>
        (l is double && r is double) ? AsNum(l) == AsNum(r)
        : (l is string && r is string) ? AsStr(l) == AsStr(r)
        : (l is bool && r is bool) ? AsBool(l) == AsBool(r)
        : AsStr(l) == AsStr(r);

    private static double AsNum(object o) => o switch
    {
        double d => d,
        int i => i,
        long l => l,
        bool b => b ? 1 : 0,
        string s => double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0,
        MethodVal mv => AsNum(mv.Target),
        _ => 0
    };

    private static string AsStr(object o) => o switch
    {
        double d => d.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        string s => s,
        MethodVal mv => AsStr(mv.Target),
        _ => o?.ToString() ?? ""
    };

    private static bool AsBool(object o) => o switch
    {
        bool b => b,
        double d => d != 0,
        string s => !string.IsNullOrEmpty(s) && s != "false" && s != "0",
        MethodVal mv => AsBool(mv.Target),
        _ => false
    };

    // type-aware placeholder value used to preview what a calculated/derived expression would produce on real data.
    public static JsonNode SampleValue(string dataType, string optionsJson)
    {
        switch (FieldValidation.NormalizeType(dataType))
        {
            case "number":
            case "currency":
                return JsonValue.Create(100);
            case "boolean":
                return JsonValue.Create(true);
            case "date":
                return JsonValue.Create("2026-01-05");
            case "datetime":
                return JsonValue.Create("2026-01-05T10:00:00");
            case "systemid":
                return JsonValue.Create("AB12CD34");
            case "select":
            case "multiselect":
                try
                {
                    var opts = JsonNode.Parse(string.IsNullOrWhiteSpace(optionsJson) ? "[]" : optionsJson) as JsonArray;
                    if (opts is { Count: > 0 } && opts[0] is JsonValue ov && ov.TryGetValue<string>(out var o))
                        return JsonValue.Create(o);
                }
                catch { }
                return JsonValue.Create("Sample");
            default:
                return JsonValue.Create("Sample");
        }
    }

    public static string FormatSample(object? val)
    {
        switch (val)
        {
            case null:
                return "null";
            case double d:
                return d == Math.Floor(d) ? d.ToString("0.####", CultureInfo.InvariantCulture) : Math.Round(d, 2).ToString("0.##", CultureInfo.InvariantCulture);
            case bool b:
                return b ? "true" : "false";
            case string s:
                return s;
            case JsonNode j:
                return j.ToJsonString();
            case MethodVal mv:
                return FormatSample(mv.Target);
            default:
                return Convert.ToString(val, CultureInfo.InvariantCulture) ?? "";
        }
    }
}

