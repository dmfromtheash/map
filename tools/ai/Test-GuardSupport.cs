// Shared by the PowerShell 5.1 guards. C# 5 / .NET Framework BCL only; no Roslyn/package dependency.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace MapTestGuards
{
    public sealed class Finding
    {
        public string Key;
        public string Detail;
        public override string ToString() { return Key + " :: " + Detail; }
    }

    public static class Guard
    {
        sealed class Token
        {
            public string Text, Literal;
            public int Line, Pair = -1;
        }
        sealed class Source
        {
            public string Path;
            public List<Token> Tokens;
        }
        sealed class AttributeType
        {
            public Source File;
            public string Name;
            public List<string> Bases = new List<string>();
            public int Start, End;
        }
        static bool Defect(string reason)
        {
            return reason != null && reason.StartsWith("defect:", StringComparison.Ordinal)
                && reason.Substring(7).Trim().Length > 0;
        }
        static void Add(List<Finding> result, string kind, Source file, string scope, int index, string detail)
        {
            result.Add(new Finding { Key = kind + " " + file.Path + " :: " + scope,
                Detail = file.Path + ":" + file.Tokens[index].Line + " " + detail });
        }
        static string Simple(string name)
        {
            int dot = name.LastIndexOf('.');
            if (dot >= 0) name = name.Substring(dot + 1);
            return name.EndsWith("Attribute", StringComparison.Ordinal) ? name.Substring(0, name.Length - 9) : name;
        }
        static void SkipHole(string text, ref int i, ref int line)
        {
            int depth = 1; i++;
            while (i < text.Length && depth > 0)
            {
                string nested;
                if (ReadLiteral(text, ref i, ref line, out nested)) continue;
                if (i + 1 < text.Length && text.Substring(i, 2) == "//")
                { while (i < text.Length && text[i] != '\n') i++; continue; }
                if (i + 1 < text.Length && text.Substring(i, 2) == "/*")
                {
                    i += 2;
                    while (i < text.Length && !(text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/'))
                    { if (text[i++] == '\n') line++; }
                    i = Math.Min(text.Length, i + 2); continue;
                }
                if (text[i] == '{') depth++;
                if (text[i] == '}') depth--;
                if (text[i++] == '\n') line++;
            }
        }
        static bool ReadLiteral(string text, ref int i, ref int line, out string literal)
        {
            literal = null;
            int q = i;
            while (q < text.Length && (text[q] == '$' || text[q] == '@')) q++;
            if (q >= text.Length || (text[q] != '"' && !(q == i && text[q] == '\''))) return false;
            char quote = text[q];
            bool verbatim = text.Substring(i, q - i).Contains("@");
            bool interpolated = text.Substring(i, q - i).Contains("$");
            int quotes = 1;
            while (q + quotes < text.Length && text[q + quotes] == quote) quotes++;
            bool raw = quote == '"' && quotes >= 3;
            int delimiter = raw ? quotes : 1;
            i = q + delimiter;
            var value = new StringBuilder();
            while (i < text.Length)
            {
                if (raw && i + delimiter <= text.Length && text.Substring(i, delimiter) == new string(quote, delimiter))
                { i += delimiter; break; }
                if (!raw && text[i] == quote)
                {
                    if (verbatim && i + 1 < text.Length && text[i + 1] == quote) { value.Append(quote); i += 2; continue; }
                    i++; break;
                }
                if (!raw && !verbatim && text[i] == '\\' && i + 1 < text.Length)
                {
                    i++; char escaped = text[i++];
                    value.Append(escaped == 'n' ? '\n' : escaped == 'r' ? '\r' : escaped == 't' ? '\t' : escaped);
                    continue;
                }
                if (interpolated && !raw && text[i] == '{')
                {
                    if (i + 1 < text.Length && text[i + 1] == '{') { value.Append("{{"); i += 2; continue; }
                    value.Append('\0'); SkipHole(text, ref i, ref line); continue;
                }
                if (text[i] == '\n') line++;
                if (interpolated && raw && text[i] == '{') value.Append('\0');
                value.Append(text[i++]);
            }
            literal = quote == '"' ? value.ToString() : null;
            int interpolation = literal == null ? -1 : literal.IndexOf('\0');
            if (interpolation >= 0) literal = literal.Substring(0, interpolation);
            return true;
        }
        // Comments and string contents cannot become C# statements. Keep strings as single tokens,
        // including raw/verbatim/interpolated forms, and pair delimiters once for multiline constructs.
        static List<Token> Lex(string text)
        {
            var tokens = new List<Token>();
            int i = 0, line = 1;
            while (i < text.Length)
            {
                char c = text[i];
                if (Char.IsWhiteSpace(c)) { if (c == '\n') line++; i++; continue; }
                if (i + 1 < text.Length && text.Substring(i, 2) == "//")
                { while (i < text.Length && text[i] != '\n') i++; continue; }
                if (i + 1 < text.Length && text.Substring(i, 2) == "/*")
                {
                    i += 2;
                    while (i < text.Length && !(text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/'))
                    { if (text[i++] == '\n') line++; }
                    i = Math.Min(text.Length, i + 2); continue;
                }
                int start = i, tokenLine = line;
                string literal;
                if (ReadLiteral(text, ref i, ref line, out literal))
                {
                    tokens.Add(new Token { Text = "<literal>", Literal = literal, Line = tokenLine });
                    continue;
                }
                if (Char.IsLetter(c) || c == '_' || c == '@')
                { i++; while (i < text.Length && (Char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++; }
                else if (Char.IsDigit(c))
                { i++; while (i < text.Length && (Char.IsLetterOrDigit(text[i]) || text[i] == '_' || text[i] == '.')) i++; }
                else if (i + 1 < text.Length && new[] { "=>", ">=", "<=", "==", "!=", "++", "--", "&&", "||", "??", "::" }.Contains(text.Substring(i, 2))) i += 2;
                else i++;
                tokens.Add(new Token { Text = text.Substring(start, i - start), Line = tokenLine });
            }
            var stack = new Stack<int>();
            for (i = 0; i < tokens.Count; i++)
            {
                string t = tokens[i].Text;
                if (t == "(" || t == "[" || t == "{") stack.Push(i);
                else if ((t == ")" || t == "]" || t == "}") && stack.Count > 0)
                {
                    int open = stack.Peek();
                    if ((tokens[open].Text == "(" && t == ")") || (tokens[open].Text == "[" && t == "]") || (tokens[open].Text == "{" && t == "}"))
                    { stack.Pop(); tokens[open].Pair = i; tokens[i].Pair = open; }
                }
            }
            return tokens;
        }
        static List<int> Arguments(List<Token> t, int open)
        {
            var args = new List<int>();
            int end = t[open].Pair;
            if (end <= open + 1) return args;
            args.Add(open + 1);
            for (int i = open + 1; i < end; i++)
            {
                if (t[i].Text == ",") args.Add(i + 1);
                else if (t[i].Pair > i) i = t[i].Pair;
            }
            return args;
        }
        static bool Reason(List<Token> t, int index)
        {
            if (index < 0 || index >= t.Count) return false;
            if (index + 2 < t.Count && (t[index].Text == "reason" || t[index].Text == "message") && t[index + 1].Text == ":") index += 2;
            int end = index;
            while (end < t.Count && !new[] { ",", ";", ")", "]", "}" }.Contains(t[end].Text))
            { if (t[end].Pair > end) end = t[end].Pair; end++; }
            while (index < end && t[index].Text == "(" && t[index].Pair == end - 1) { index++; end--; }
            if (index >= end || !Defect(t[index].Literal)) return false;
            if (index + 1 == end) return true;
            // A literal prefix followed by concatenation is provable; a transformation or a
            // top-level conditional/comparison can discard that prefix and must be reviewed.
            if (t[index + 1].Text != "+") return false;
            for (int i = index + 2; i < end; i++)
            {
                if (t[i].Pair > i) { i = t[i].Pair; continue; }
                if (new[] { "?", "??", "==", "!=", "&&", "||", ">", "<", ">=", "<=", "is", "as" }.Contains(t[i].Text)) return false;
            }
            return true;
        }
        static bool SkipCall(List<Token> t, int i, out int reason)
        {
            reason = -1;
            if (i + 3 < t.Count && t[i + 1].Text == "." && t[i + 3].Text == "(")
            {
                string owner = t[i].Text, method = t[i + 2].Text;
                if ((owner == "Assert" && method.StartsWith("Skip", StringComparison.Ordinal)) ||
                    (owner == "Skip" && (method.StartsWith("If", StringComparison.Ordinal) || method == "Unless")) ||
                    (owner == "SkipException" && method == "ForSkip"))
                {
                    var args = Arguments(t, i + 3);
                    if (args.Count > 0) reason = args[args.Count - 1];
                    foreach (int a in args) if (t[a].Text == "reason" || t[a].Text == "message") reason = a;
                    return true;
                }
            }
            if (t[i].Text == "SkipException" && i + 1 < t.Count && t[i + 1].Text == "(")
            { var args = Arguments(t, i + 1); if (args.Count > 0) reason = args[0]; return true; }
            return false;
        }
        static string Scope(List<Token> t, int index, List<AttributeType> types)
        {
            foreach (var type in types) if (index >= type.Start && index <= type.End) return type.Name;
            string enclosing = null;
            for (int i = 1; i + 1 < index; i++)
            {
                if (t[i].Text != "(" || t[i].Pair <= i) continue;
                if (!Regex.IsMatch(t[i - 1].Text, @"^[A-Za-z_]\w*$") ||
                    new[] { "if", "for", "foreach", "while", "switch", "catch", "when", "using", "lock", "fixed" }.Contains(t[i - 1].Text)) continue;
                int body = t[i].Pair + 1;
                if (body < index && t[body].Text == "{" && t[body].Pair >= index) enclosing = t[i - 1].Text;
                if (body < index && t[body].Text == "=>")
                {
                    int end = body;
                    while (end < t.Count && t[end].Text != ";") { if (t[end].Pair > end) end = t[end].Pair; end++; }
                    if (end >= index) enclosing = t[i - 1].Text;
                }
            }
            if (enclosing != null) return enclosing;
            // Static Skip belongs to the FOLLOWING test, not the previous method.
            for (int i = index; i + 1 < t.Count; i++)
            {
                if (t[i].Text == "class") return i + 1 < t.Count ? t[i + 1].Text : "<file>";
                if (t[i + 1].Text == "(" && t[i].Literal == null && t[i + 1].Pair > i)
                {
                    int end = t[i + 1].Pair;
                    if (end + 1 < t.Count && (t[end + 1].Text == "{" || t[end + 1].Text == "=>")) return t[i].Text;
                }
            }
            return "<file>";
        }
        public static Finding[] ScanSources(string[] paths, string[] texts)
        {
            var files = new List<Source>();
            var types = new List<AttributeType>();
            var testNames = new HashSet<string>(StringComparer.Ordinal) { "Fact", "Theory" };
            for (int f = 0; f < paths.Length; f++)
            {
                var file = new Source { Path = paths[f], Tokens = Lex(texts[f]) };
                files.Add(file); var t = file.Tokens;
                for (int i = 0; i + 2 < t.Count; i++)
                {
                    if (t[i].Text != "class") continue;
                    var type = new AttributeType { File = file, Name = t[i + 1].Text, Start = i };
                    int j = i + 2;
                    while (j < t.Count && t[j].Text != "{" && t[j].Text != ";")
                    { if (t[j].Text == ":" || t[j].Text == "," || t[j].Text == ".") { if (j + 1 < t.Count) type.Bases.Add(Simple(t[j + 1].Text)); } j++; }
                    type.End = j < t.Count && t[j].Pair > j ? t[j].Pair : j;
                    types.Add(type);
                }
            }
            bool changed;
            do
            {
                changed = false;
                foreach (var type in types) if (type.Bases.Any(b => testNames.Contains(b))) changed |= testNames.Add(Simple(type.Name));
            } while (changed);
            var result = new List<Finding>();
            foreach (var file in files)
            {
                var t = file.Tokens;
                var custom = types.Where(x => x.File == file && testNames.Contains(Simple(x.Name))).ToList();
                foreach (var type in custom)
                {
                    bool hasSkip = false, unsafeSkip = false;
                    for (int i = type.Start; i + 2 <= type.End && i + 2 < t.Count; i++)
                    {
                        if (t[i].Text == "Skip" && t[i + 1].Text == "=" && t[i + 2].Text != "null")
                        { hasSkip = true; unsafeSkip |= !Reason(t, i + 2); }
                    }
                    // Unknown attribute behavior also requires a named review; inheritance is not a free pass.
                    if (!hasSkip || unsafeSkip) Add(result, "custom-test-attribute", file, type.Name, type.Start, "derived Fact/Theory needs a defect reason or a named baseline entry");
                }
                for (int i = 0; i < t.Count; i++)
                {
                    if (i + 2 < t.Count && t[i].Text == "Skip" && t[i + 1].Text == "=" && t[i + 2].Text != "null" && !Reason(t, i + 2))
                        Add(result, "skip-without-defect", file, Scope(t, i, custom), i, "Skip reason is not a literal starting with defect:");
                    int reason;
                    if (SkipCall(t, i, out reason) && !Reason(t, reason))
                        Add(result, "runtime-skip", file, Scope(t, i, custom), i, "skip call needs a literal reason starting with defect:");
                    if (i + 2 < t.Count && t[i].Text == "Assert" && t[i + 1].Text == "." && t[i + 2].Text == "True" && i + 3 < t.Count && t[i + 3].Text == "(")
                    {
                        var args = Arguments(t, i + 3);
                        if (args.Count > 0)
                        {
                            int arg = 0;
                            for (int a = 0; a < args.Count; a++) if (t[args[a]].Text == "condition") arg = a;
                            int value = args[arg];
                            int limit = arg + 1 < args.Count ? args[arg + 1] - 1 : t[i + 3].Pair;
                            if (value + 1 < t.Count && t[value].Text == "condition" && t[value + 1].Text == ":") value += 2;
                            while (t[value].Text == "(" && t[value].Pair == limit - 1) { value++; limit--; }
                            if (t[value].Text == "true" && value + 1 == limit)
                                Add(result, "assert-true", file, Scope(t, i, custom), i, "Assert.True(true) checks no behavior");
                        }
                    }
                    if (t[i].Text != "[" || t[i].Pair <= i) continue;
                    int close = t[i].Pair;
                    bool test = false;
                    for (int a = i + 1; a < close; a++) if (testNames.Contains(Simple(t[a].Text))) test = true;
                    if (!test) continue;
                    int declaration = close + 1;
                    while (declaration < t.Count && t[declaration].Text == "[" && t[declaration].Pair > declaration) declaration = t[declaration].Pair + 1;
                    int p = declaration;
                    while (p < t.Count && t[p].Text != "(" && t[p].Text != "{" && t[p].Text != ";" && t[p].Text != "class") p++;
                    if (p >= t.Count || t[p].Text != "(" || t[p].Pair < 0) continue;
                    string method = t[p - 1].Text;
                    int body = t[p].Pair + 1;
                    if (body >= t.Count) continue;
                    int end = body;
                    if (t[body].Text == "{") end = t[body].Pair;
                    else if (t[body].Text == "=>") { while (end < t.Count && t[end].Text != ";") { if (t[end].Pair > end) end = t[end].Pair; end++; } }
                    else continue;
                    if (end < body || end >= t.Count) continue;
                    CheckBody(result, file, method, body, end);
                    // Continue token by token: Skip properties INSIDE this attribute must also be checked.
                }
                CheckCatches(result, file, custom);
            }
            return result.ToArray();
        }
        static int AssertOpen(List<Token> t, int i)
        {
            if (i + 3 >= t.Count || t[i].Text != "Assert" || t[i + 1].Text != ".") return -1;
            int open = i + 3;
            if (t[open].Text == "<")
            {
                int depth = 0;
                do { if (t[open].Text == "<") depth++; if (t[open].Text == ">") depth--; open++; }
                while (open < t.Count && depth > 0);
            }
            return open < t.Count && t[open].Text == "(" ? open : -1;
        }
        static bool AssertAt(List<Token> t, int i)
        { return AssertOpen(t, i) >= 0 && !t[i + 2].Text.StartsWith("Skip", StringComparison.Ordinal); }
        static void CheckBody(List<Finding> result, Source file, string method, int body, int end)
        {
            var t = file.Tokens;
            var asserts = new List<int>();
            int firstAssert = end;
            var nested = new HashSet<int>();
            for (int i = body + 1; i < end; i++)
            {
                // A return inside a callback/local function does not return from the test.
                bool lambda = (t[i].Text == "=>" || t[i].Text == "delegate") && i + 1 < end && t[i + 1].Text == "{";
                bool function = t[i].Text == ")" && i + 1 < end && t[i + 1].Text == "{" && t[i].Pair > body
                    && t[i].Pair > 0 && Regex.IsMatch(t[t[i].Pair - 1].Text, @"^[A-Za-z_]\w*$")
                    && !new[] { "if", "for", "foreach", "while", "switch", "catch", "when", "using", "lock", "fixed" }.Contains(t[t[i].Pair - 1].Text);
                if ((lambda || function) && t[i + 1].Pair > i)
                    for (int j = i + 1; j <= t[i + 1].Pair; j++) nested.Add(j);
                if (AssertAt(t, i)) { asserts.Add(i); if (!nested.Contains(i)) firstAssert = Math.Min(firstAssert, i); }
            }
            for (int i = body + 1; i < firstAssert; i++)
                if (t[i].Text == "return" && !nested.Contains(i))
                    Add(result, "silent-return", file, method, i, "test can return before its first Assert (lexical check)");
            if (end == body + 1) Add(result, "empty-test", file, method, body, "test has an empty body");
            if (asserts.Count == 1)
            {
                int a = asserts[0];
                if (t[a + 2].Text == "NotNull")
                {
                    var args = Arguments(t, AssertOpen(t, a));
                    if (args.Count > 0)
                    {
                        int value = args[0];
                        bool fresh = t[value].Text == "new";
                        // Only a direct local, not a product result/property, and not reassigned since new.
                        if (value + 1 < t.Count && t[value + 1].Text == ")")
                            for (int j = body + 1; j + 2 < a; j++)
                                if (t[j].Text == t[value].Text && t[j + 1].Text == "=") fresh = t[j + 2].Text == "new";
                        if (fresh) Add(result, "notnull-created", file, method, a, "only assertion is NotNull of a value constructed in this test");
                    }
                }
            }
        }
        static void CheckCatches(List<Finding> result, Source file, List<AttributeType> types)
        {
            var t = file.Tokens;
            for (int i = 0; i + 1 < t.Count; i++)
            {
                if (t[i].Text != "try" || t[i + 1].Text != "{" || t[i + 1].Pair <= i) continue;
                int tryEnd = t[i + 1].Pair;
                bool containsAssert = false;
                for (int a = i + 2; a < tryEnd; a++) containsAssert |= AssertAt(t, a);
                if (!containsAssert) continue;
                int next = tryEnd + 1;
                while (next + 1 < t.Count && t[next].Text == "catch")
                {
                    int c = next++;
                    if (t[next].Text == "(") next = t[next].Pair + 1;
                    if (next + 1 < t.Count && t[next].Text == "when" && t[next + 1].Text == "(") next = t[next + 1].Pair + 1;
                    if (next >= t.Count || t[next].Text != "{" || t[next].Pair < next) break;
                    if (t[next].Pair == next + 1) Add(result, "empty-catch-assert", file, Scope(t, i, types), c, "empty catch can swallow an assertion failure");
                    next = t[next].Pair + 1;
                }
            }
        }

        static List<decimal> Numbers(string text)
        {
            var result = new List<decimal>();
            foreach (Match m in Regex.Matches(text, @"(?<![\w.])-?\d[\d_]*(?:\.\d[\d_]*)?(?:[eE][+-]?\d+)?"))
            {
                decimal value;
                if (Decimal.TryParse(m.Value.Replace("_", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out value)) result.Add(value);
            }
            return result;
        }
        static string Shape(string text)
        { return Regex.Replace(Regex.Replace(text, @"(?<![\w.])-?\d[\d_]*(?:\.\d[\d_]*)?(?:[eE][+-]?\d+)?", "#"), @"\s+", ""); }
        static string Code(string text)
        { return String.Join(" ", Lex(text).Select(t => t.Text == "<literal>" ? "\"" + t.Literal + "\"" : t.Text)); }
        public static Finding[] ScanDiff(string diff)
        {
            var result = new List<Finding>();
            string file = "";
            var removed = new List<string>();
            var deletedAsserts = new List<Tuple<string, string>>();
            var addedAsserts = new Dictionary<string, int>(StringComparer.Ordinal);
            string[] lines = diff.Replace("\r", "").Split('\n');
            bool hunkThreshold = false;
            const string threshold = @"Assert|[Bb]udget|[Tt]imeout|TimeSpan|Milliseconds|Seconds|[Rr]atio|<=|>=|Max|[Mm]in|[Ff][Pp][Ss]|[Ll]ower|[Uu]pper|Limit|Within";
            for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                string raw = lines[lineIndex];
                if (raw.StartsWith("+++ b/", StringComparison.Ordinal)) { file = raw.Substring(6); removed.Clear(); continue; }
                if (raw.StartsWith("@@", StringComparison.Ordinal))
                {
                    removed.Clear(); hunkThreshold = false;
                    for (int h = lineIndex + 1; h < lines.Length && !lines[h].StartsWith("@@", StringComparison.Ordinal) && !lines[h].StartsWith("diff --git", StringComparison.Ordinal) && !lines[h].StartsWith("+++ b/", StringComparison.Ordinal); h++)
                        if (lines[h].Length > 0 && (lines[h][0] == '+' || lines[h][0] == '-' || lines[h][0] == ' ')) hunkThreshold |= Regex.IsMatch(Code(lines[h].Substring(1)), threshold);
                    continue;
                }
                if (raw.Length == 0 || raw.StartsWith("---", StringComparison.Ordinal) || raw.StartsWith("+++", StringComparison.Ordinal)) continue;
                if (raw[0] == '-')
                {
                    string old = Code(raw.Substring(1)); removed.Add(old);
                    if (old.Contains("Assert .")) deletedAsserts.Add(Tuple.Create(file, old));
                }
                if (raw[0] != '+') continue;
                string text = Code(raw.Substring(1));
                string key = file + " :: " + text;
                if (text.Contains("Assert .")) { if (!addedAsserts.ContainsKey(key)) addedAsserts[key] = 0; addedAsserts[key]++; }
                if (Regex.IsMatch(text, @"\bSkip\s*=|\bAssert\s*\.\s*Skip\w*|\bSkip\s*\.\s*(If\w*|Unless)|\bSkipException\b|\bSkip(When|Unless|Exceptions)\s*="))
                    result.Add(new Finding { Key = "skip-added " + file, Detail = text });
                if (Regex.IsMatch(raw, @"\[Collection\(.*(Slow|Starved|Serial|RealTime)"))
                    result.Add(new Finding { Key = "moved-to-collection " + file, Detail = text });
                if (!hunkThreshold && !Regex.IsMatch(text, threshold)) continue;
                foreach (string old in removed)
                {
                    if (Shape(old) != Shape(text)) continue;
                    var a = Numbers(old); var b = Numbers(text);
                    bool raised = false, lowered = false;
                    for (int k = 0; k < Math.Min(a.Count, b.Count); k++) { raised |= b[k] > a[k]; lowered |= b[k] < a[k]; }
                    // Conservatively require review for either direction: >= / reversed <= / InRange
                    // have lower bounds; <= / time budgets / InRange also have upper bounds.
                    if (raised || lowered) result.Add(new Finding { Key = (lowered ? "lower-bound-or-number-lowered " : "number-raised ") + file, Detail = old + " -> " + text });
                }
            }
            foreach (var old in deletedAsserts)
            {
                string key = old.Item1 + " :: " + old.Item2;
                if (addedAsserts.ContainsKey(key) && addedAsserts[key] > 0) { addedAsserts[key]--; continue; }
                result.Add(new Finding { Key = "assert-removed " + old.Item1, Detail = old.Item2 });
            }
            return result.ToArray();
        }

        public static Dictionary<string, string> ReadBaseline(string text)
        {
            var known = new Dictionary<string, string>(StringComparer.Ordinal);
            int lineNumber = 0;
            foreach (string line in text.Replace("\r", "").Split('\n'))
            {
                lineNumber++;
                if (line.Trim().Length == 0 || line.TrimStart().StartsWith("#", StringComparison.Ordinal)) continue;
                string[] entry = new Regex(@"\s+\|\s+").Split(line, 2);
                if (entry.Length != 2 || entry[0].Trim().Length == 0 || entry[1].Trim().Length == 0 ||
                    entry[0].IndexOfAny(new[] { '*', '?' }) >= 0 || known.ContainsKey(entry[0]))
                    throw new InvalidDataException("Invalid/duplicate named baseline entry at line " + lineNumber + ": expected exact key | reason");
                known.Add(entry[0], entry[1]);
            }
            return known;
        }
        public static bool HasJustification(string messages)
        { return Regex.IsMatch(messages, @"(?m)test-change:[ \t]*\S[^\r\n]*"); }

        public static Finding[] ScanTrx(string xml, string path)
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            var doc = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(new StringReader(xml), settings)) doc.Load(reader);
            if (doc.DocumentElement == null || doc.DocumentElement.LocalName != "TestRun") throw new InvalidDataException("Not a TRX TestRun: " + path);
            var results = doc.SelectNodes("/*[local-name()='TestRun']/*[local-name()='Results']//*[local-name()='UnitTestResult']");
            if (results.Count == 0) throw new InvalidDataException("TRX has no test results: " + path);
            var findings = new List<Finding>();
            foreach (XmlElement test in results)
            {
                string outcome = test.GetAttribute("outcome");
                if (!String.Equals(outcome, "NotExecuted", StringComparison.OrdinalIgnoreCase) &&
                    !String.Equals(outcome, "Skipped", StringComparison.OrdinalIgnoreCase)) continue;
                // xUnit/VSTest uses ErrorInfo/Message; some adapters use Output/StdOut instead.
                // A present bad/empty Message must not be rescued by unrelated defect text in stdout.
                var message = test.SelectSingleNode("*[local-name()='Output']/*[local-name()='ErrorInfo']/*[local-name()='Message']");
                if (message == null) message = test.SelectSingleNode("*[local-name()='Output']/*[local-name()='StdOut']");
                string reason = message == null ? "" : message.InnerText;
                if (!Defect(reason)) findings.Add(new Finding { Key = "trx-skip " + path + " :: " + test.GetAttribute("testName"), Detail = "outcome=" + outcome + " reason=" + reason.Replace("\r", " ").Replace("\n", " ") });
            }
            return findings.ToArray();
        }

        // Shared fixtures exercise the exact engine used by both PowerShell wrappers, without packages.
        public static string[] SelfTest(string fixturePath)
        {
            var doc = new XmlDocument { XmlResolver = null }; doc.Load(fixturePath);
            var log = new List<string>(); int passed = 0, failed = 0;
            var cases = doc.SelectNodes("/cases/case");
            if (cases.Count == 0) throw new InvalidDataException("No guard self-test cases: " + fixturePath);
            foreach (XmlElement test in cases)
            {
                try
                {
                    Finding[] actual;
                    string input = test["input"].InnerText;
                    switch (test.GetAttribute("kind"))
                    {
                        case "source": actual = ScanSources(new[] { "tests/Fixture.cs" }, new[] { input }); break;
                        case "diff": actual = ScanDiff(input); break;
                        case "trx": actual = ScanTrx(input, "fixture.trx"); break;
                        case "baseline": ReadBaseline(input); actual = new Finding[0]; break;
                        case "allowlist":
                            var known = ReadBaseline(test["baseline"].InnerText);
                            actual = ScanSources(new[] { "tests/Fixture.cs" }, new[] { input }).Where(f => !known.ContainsKey(f.Key)).ToArray();
                            break;
                        case "justification": actual = HasJustification(input) ? new Finding[0] : new[] { new Finding { Key = "missing-test-change fixture", Detail = "no explanation" } }; break;
                        default: throw new InvalidDataException("Unknown case kind");
                    }
                    string expect = test.GetAttribute("expect");
                    if (expect == "error") throw new Exception("Expected malformed/empty input to fail");
                    if (expect == "clean" ? actual.Length != 0 : !actual.Any(f => f.Key.StartsWith(expect + " ", StringComparison.Ordinal)))
                        throw new Exception("expected " + expect + ", got " + String.Join("; ", actual.Select(f => f.ToString())));
                    passed++; log.Add("PASS " + test.GetAttribute("name") + " findings=" + actual.Length);
                }
                catch (Exception ex)
                {
                    if (test.GetAttribute("expect") == "error" && (ex is XmlException || ex is InvalidDataException)) { passed++; log.Add("PASS " + test.GetAttribute("name") + " rejected invalid input"); }
                    else { failed++; log.Add("FAIL " + test.GetAttribute("name") + " " + ex.Message); }
                }
            }
            log.Add("guard self-tests: passed=" + passed + " failed=" + failed);
            if (failed != 0) throw new InvalidOperationException(String.Join(Environment.NewLine, log));
            return log.ToArray();
        }
    }
}
