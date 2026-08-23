namespace Vacuon.Core.Preview;

/// <summary>What a run of characters is, as far as colouring is concerned.</summary>
public enum TokenKind
{
    Plain,
    Comment,
    String,
    Number,
    Keyword,
}

/// <summary>A run of characters of one kind, by position in the text.</summary>
public readonly record struct SyntaxSpan(int Start, int Length, TokenKind Kind)
{
    public int End => Start + Length;
}

/// <summary>
/// Splits source text into coloured runs.
/// <para>
/// <b>Deliberately one shallow tokenizer rather than a grammar per language.</b> What is being
/// coloured is the first 64 KiB of a file somebody is deciding whether to delete — the job is
/// to make structure visible at a glance, not to parse. A real per-language front end would be
/// a great deal of code, a dependency, and a new way to be wrong about a file this app only
/// ever reads.
/// </para>
/// <para>
/// It returns <b>positions</b>, never anything drawable. Nothing in this assembly may know
/// what a brush is, so the view maps a <see cref="TokenKind"/> to a colour from the theme it
/// is already using.
/// </para>
/// <para>
/// The consequence, stated rather than hidden: this will occasionally colour something that is
/// not really a keyword, because it does not know which language it is reading. In a read-only
/// preview that costs a wrong colour, which is the cheapest kind of wrong this application
/// can be.
/// </para>
/// </summary>
public static class SyntaxSpans
{
    /// <summary>
    /// Words coloured as keywords, drawn from the languages this app's own users are most
    /// likely to be looking at. Shared across languages on purpose — see the class remarks.
    /// </summary>
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        // C-family
        "abstract", "async", "await", "base", "bool", "break", "byte", "case", "catch", "char",
        "class", "const", "continue", "default", "delegate", "do", "double", "else", "enum",
        "event", "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach",
        "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock", "long",
        "namespace", "new", "null", "object", "operator", "out", "override", "params", "private",
        "protected", "public", "readonly", "record", "ref", "return", "sealed", "short", "sizeof",
        "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint",
        "ulong", "unsafe", "ushort", "using", "var", "virtual", "void", "while", "yield",

        // JavaScript and TypeScript
        "const", "let", "function", "export", "import", "from", "of", "typeof", "instanceof",
        "undefined", "type", "declare", "extends", "implements",

        // Python
        "def", "elif", "except", "global", "lambda", "None", "nonlocal", "not", "pass", "raise",
        "self", "True", "False", "and", "or", "with", "as", "assert",

        // Shell and PowerShell
        "echo", "then", "fi", "esac", "done", "local", "param", "begin", "process", "end",

        // SQL
        "select", "insert", "update", "delete", "where", "join", "group", "order", "having",
        "values", "table", "index", "create", "drop", "alter",
    };

    /// <summary>
    /// Whether an extension is worth colouring at all.
    /// <para>
    /// A log or a CSV gets no colouring: there is no syntax in it to reveal, and colouring
    /// arbitrary words in a log invents structure that is not there.
    /// </para>
    /// </summary>
    public static bool IsSource(ReadOnlySpan<char> fileName)
    {
        int dot = fileName.LastIndexOf('.');
        if (dot < 0) return false;

        ReadOnlySpan<char> extension = fileName[(dot + 1)..];

        foreach (string known in SourceExtensions)
            if (extension.Equals(known, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    private static readonly string[] SourceExtensions =
    [
        "cs", "js", "mjs", "cjs", "ts", "tsx", "jsx", "py", "java", "kt", "go", "rs", "rb",
        "php", "c", "h", "cpp", "hpp", "cc", "cxx", "swift", "sh", "bash", "zsh", "ps1", "psm1",
        "sql", "json", "xml", "yaml", "yml", "toml", "css", "scss", "less", "html", "htm",
        "vue", "svelte", "lua", "pl", "r", "scala", "dart", "gradle", "cmake",
    ];

    /// <summary>
    /// The coloured runs of a piece of text, in order and never overlapping.
    /// <para>
    /// Only non-plain runs are returned. Emitting a span for every ordinary character would
    /// multiply the work and the allocation for something the view already draws by default.
    /// </para>
    /// </summary>
    public static IReadOnlyList<SyntaxSpan> Of(string text)
    {
        var spans = new List<SyntaxSpan>();

        int i = 0;

        while (i < text.Length)
        {
            char c = text[i];

            // Line comments: //, #, and -- all mean the same thing here, which is why one
            // tokenizer can serve several languages without knowing which it is reading.
            if (c == '#' || (c == '/' && Next(text, i) == '/') || (c == '-' && Next(text, i) == '-'))
            {
                int start = i;
                while (i < text.Length && text[i] != '\n') i++;

                spans.Add(new SyntaxSpan(start, i - start, TokenKind.Comment));
                continue;
            }

            // Block comments.
            if (c == '/' && Next(text, i) == '*')
            {
                int start = i;
                i += 2;

                while (i < text.Length && !(text[i] == '*' && Next(text, i) == '/')) i++;
                if (i < text.Length) i += 2;

                spans.Add(new SyntaxSpan(start, i - start, TokenKind.Comment));
                continue;
            }

            if (c == '"' || c == '\'' || c == '`')
            {
                int start = i;
                char quote = c;
                i++;

                while (i < text.Length && text[i] != quote)
                {
                    // A backslash escapes the next character, so an escaped quote does not
                    // end the string. Without this, one \" swallows the rest of the file.
                    if (text[i] == '\\' && i + 1 < text.Length) i++;

                    // An unterminated string must not run to the end of a 64 KiB preview and
                    // paint everything after it. A line break ends it.
                    if (text[i] == '\n') break;

                    i++;
                }

                if (i < text.Length && text[i] == quote) i++;

                spans.Add(new SyntaxSpan(start, i - start, TokenKind.String));
                continue;
            }

            if (char.IsAsciiDigit(c) && !IsWordChar(Previous(text, i)))
            {
                int start = i;

                while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] == '.' || text[i] == '_'))
                    i++;

                spans.Add(new SyntaxSpan(start, i - start, TokenKind.Number));
                continue;
            }

            if (IsWordStart(c))
            {
                int start = i;
                while (i < text.Length && IsWordChar(text[i])) i++;

                if (Keywords.Contains(text[start..i]))
                    spans.Add(new SyntaxSpan(start, i - start, TokenKind.Keyword));

                continue;
            }

            i++;
        }

        return spans;
    }

    private static char Next(string text, int i) => i + 1 < text.Length ? text[i + 1] : '\0';
    private static char Previous(string text, int i) => i > 0 ? text[i - 1] : '\0';

    private static bool IsWordStart(char c) => char.IsAsciiLetter(c) || c == '_';
    private static bool IsWordChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
}
