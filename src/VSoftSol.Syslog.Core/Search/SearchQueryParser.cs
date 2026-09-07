using System.Globalization;

namespace VSoftSol.Syslog.Core.Search;

/// <summary>
/// Parses the Phase 5 search-query language into a <see cref="QueryNode"/> tree.
///
/// Grammar (lowest precedence first):
/// <code>
///   query   := orExpr?                       -- empty query is valid (MatchAll)
///   orExpr  := andExpr ( OR andExpr )*
///   andExpr := unary ( AND? unary )*          -- adjacency is an implicit AND
///   unary   := ( NOT | '-' ) unary | primary
///   primary := '(' orExpr ')' | term
///   term    := field ( ':' | op ) value | value
/// </code>
/// Supports: free text, quoted phrases, <c>field:value</c>, <c>field:&gt;value</c> and the
/// other ordering operators, <c>field:!=value</c>, trailing <c>*</c> wildcards, and
/// parentheses. It never throws on user input — malformed queries come back as a
/// <see cref="SearchParseResult"/> failure with a position.
/// </summary>
public static class SearchQueryParser
{
    /// <summary>The maximum query length accepted. Longer input fails fast, before lexing.</summary>
    public const int MaxQueryLength = 4096;

    public static SearchParseResult Parse(string? query)
    {
        query ??= string.Empty;

        if (query.Length > MaxQueryLength)
        {
            return SearchParseResult.Fail(
                string.Create(CultureInfo.InvariantCulture,
                    $"Query is too long ({query.Length} characters); the limit is {MaxQueryLength}."),
                0);
        }

        try
        {
            List<Token> tokens = Lexer.Tokenize(query);
            if (tokens.Count == 0)
            {
                return SearchParseResult.Ok(MatchAllNode.Instance);
            }

            var parser = new TreeParser(tokens);
            QueryNode tree = parser.ParseQuery();
            return SearchParseResult.Ok(tree);
        }
        catch (QueryFormatException ex)
        {
            return SearchParseResult.Fail(ex.Message, ex.Position);
        }
    }

    // ------------------------------------------------------------------ tokens

    private enum TokenKind
    {
        Term,
        And,
        Or,
        Not,
        LParen,
        RParen,
    }

    private readonly record struct Token(TokenKind Kind, int Position, QueryNode? Node, string Display);

    private sealed class QueryFormatException(string message, int position) : Exception(message)
    {
        public int Position { get; } = position;
    }

    // ------------------------------------------------------------------ lexer

    private static class Lexer
    {
        public static List<Token> Tokenize(string src)
        {
            var tokens = new List<Token>();
            int i = 0;

            while (i < src.Length)
            {
                char c = src[i];
                if (char.IsWhiteSpace(c))
                {
                    i++;
                    continue;
                }

                if (c == '(')
                {
                    tokens.Add(new Token(TokenKind.LParen, i, null, "("));
                    i++;
                    continue;
                }

                if (c == ')')
                {
                    tokens.Add(new Token(TokenKind.RParen, i, null, ")"));
                    i++;
                    continue;
                }

                if (c == '-' && i + 1 < src.Length && IsUnaryFollow(src[i + 1]))
                {
                    tokens.Add(new Token(TokenKind.Not, i, null, "-"));
                    i++;
                    continue;
                }

                int start = i;
                RawTerm raw = ReadTerm(src, ref i);

                if (raw is { Field: null, Quoted: false })
                {
                    switch (raw.Value.ToUpperInvariant())
                    {
                        case "AND":
                            tokens.Add(new Token(TokenKind.And, start, null, "AND"));
                            continue;
                        case "OR":
                            tokens.Add(new Token(TokenKind.Or, start, null, "OR"));
                            continue;
                        case "NOT":
                            tokens.Add(new Token(TokenKind.Not, start, null, "NOT"));
                            continue;
                    }
                }

                QueryNode node = TermBuilder.Build(raw);
                tokens.Add(new Token(TokenKind.Term, start, node, raw.Field is null ? raw.Value : raw.Field));
            }

            return tokens;
        }

        private static bool IsUnaryFollow(char c) =>
            char.IsLetterOrDigit(c) || c is '"' or '(' or '.';

        private static RawTerm ReadTerm(string src, ref int i)
        {
            int start = i;
            string? field = TryReadFieldPrefix(src, ref i);
            ComparisonOperator op = ComparisonOperator.Equals;

            if (field is not null)
            {
                op = ReadOperator(src, ref i);
            }

            bool quoted = i < src.Length && src[i] == '"';
            string value;
            bool prefix = false;

            if (quoted)
            {
                value = ReadQuoted(src, ref i, start);
                if (i < src.Length && src[i] == '*')
                {
                    prefix = true;
                    i++;
                }

                if (i < src.Length && !char.IsWhiteSpace(src[i]) && src[i] is not ('(' or ')'))
                {
                    throw new QueryFormatException("Unexpected text after a quoted phrase.", i);
                }
            }
            else
            {
                int vStart = i;
                while (i < src.Length && !char.IsWhiteSpace(src[i]) && src[i] is not ('(' or ')'))
                {
                    i++;
                }

                value = src[vStart..i];

                if (value.EndsWith('*'))
                {
                    prefix = true;
                    value = value[..^1];
                }

                int star = value.IndexOf('*', StringComparison.Ordinal);
                if (star >= 0)
                {
                    throw new QueryFormatException(
                        "A wildcard (*) may only appear at the end of a term.", vStart + star);
                }
            }

            return new RawTerm(field, op, value, quoted, prefix, start,
                field is null ? start : start + field.Length + 1);
        }

        private static string? TryReadFieldPrefix(string src, ref int i)
        {
            int j = i;
            if (j >= src.Length || !char.IsAsciiLetter(src[j]))
            {
                return null;
            }

            j++;
            while (j < src.Length && (char.IsAsciiLetterOrDigit(src[j]) || src[j] is '_' or '.'))
            {
                j++;
            }

            if (j >= src.Length || src[j] != ':')
            {
                return null;
            }

            string field = src[i..j];
            i = j + 1; // consume the ':'
            return field;
        }

        private static ComparisonOperator ReadOperator(string src, ref int i)
        {
            if (i + 1 < src.Length)
            {
                switch (src[i])
                {
                    case '>' when src[i + 1] == '=':
                        i += 2;
                        return ComparisonOperator.GreaterThanOrEqual;
                    case '<' when src[i + 1] == '=':
                        i += 2;
                        return ComparisonOperator.LessThanOrEqual;
                    case '!' when src[i + 1] == '=':
                        i += 2;
                        return ComparisonOperator.NotEquals;
                }
            }

            if (i < src.Length)
            {
                switch (src[i])
                {
                    case '>':
                        i++;
                        return ComparisonOperator.GreaterThan;
                    case '<':
                        i++;
                        return ComparisonOperator.LessThan;
                    case '=':
                        i++;
                        return ComparisonOperator.Equals;
                }
            }

            return ComparisonOperator.Equals;
        }

        private static string ReadQuoted(string src, ref int i, int termStart)
        {
            i++; // opening quote
            var sb = new System.Text.StringBuilder();
            while (i < src.Length)
            {
                char c = src[i];
                if (c == '\\' && i + 1 < src.Length && src[i + 1] is '"' or '\\')
                {
                    sb.Append(src[i + 1]);
                    i += 2;
                    continue;
                }

                if (c == '"')
                {
                    i++;
                    return sb.ToString();
                }

                sb.Append(c);
                i++;
            }

            throw new QueryFormatException(
                "Unterminated quoted phrase (missing a closing \").", termStart);
        }
    }

    private readonly record struct RawTerm(
        string? Field,
        ComparisonOperator Operator,
        string Value,
        bool Quoted,
        bool Prefix,
        int Position,
        int ValuePosition);

    // ------------------------------------------------------------------ term → node

    private static class TermBuilder
    {
        public static QueryNode Build(RawTerm t)
        {
            if (t.Field is null)
            {
                if (t.Value.Length == 0)
                {
                    throw new QueryFormatException(
                        t.Prefix
                            ? "A wildcard (*) needs at least one character before it."
                            : "Empty search term.",
                        t.Position);
                }

                return new TextTermNode(t.Value, t.Prefix);
            }

            if (!SearchFields.TryResolve(t.Field, out SearchField? field))
            {
                string hint = t.Field.StartsWith(SearchFields.CustomFieldPrefix, StringComparison.OrdinalIgnoreCase)
                    ? " Custom fields are addressed as field.<name> with a valid name."
                    : string.Empty;
                throw new QueryFormatException($"Unknown field '{t.Field}'.{hint}", t.Position);
            }

            bool isRange = t.Operator
                is ComparisonOperator.GreaterThan or ComparisonOperator.GreaterThanOrEqual
                or ComparisonOperator.LessThan or ComparisonOperator.LessThanOrEqual;

            if (isRange && !field.SupportsRangeOperators)
            {
                throw new QueryFormatException(
                    $"Field '{field.CanonicalName}' does not support '{Describe(t.Operator)}' — use ':' or '!='.",
                    t.Position);
            }

            if (t.Prefix && t.Operator != ComparisonOperator.Equals)
            {
                throw new QueryFormatException(
                    "A wildcard (*) can only be used with ':' (equals).", t.ValuePosition);
            }

            if (t.Value.Length == 0)
            {
                throw new QueryFormatException(
                    $"Expected a value after '{t.Field}:'.", t.ValuePosition);
            }

            string normalised = Normalise(field, t.Value, t.ValuePosition);
            return new FieldTermNode(field, t.Operator, normalised, t.Prefix && t.Operator == ComparisonOperator.Equals);
        }

        private static string Normalise(SearchField field, string value, int pos) => field.ValueType switch
        {
            SearchValueType.Severity => NormaliseSeverity(value)
                ?? throw new QueryFormatException(
                    $"Invalid severity '{value}' — use a name (emergency, alert, critical, error, warning, notice, info, debug) or 0-7.", pos),
            SearchValueType.Facility => NormaliseFacility(value)
                ?? throw new QueryFormatException(
                    $"Invalid facility '{value}' — use a name (kernel, user, mail, daemon, auth, … local0-local7) or 0-23.", pos),
            SearchValueType.Number => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
                ? value
                : throw new QueryFormatException(
                    $"Field '{field.CanonicalName}' expects a number, got '{value}'.", pos),
            SearchValueType.Timestamp => NormaliseTimestamp(value)
                ?? throw new QueryFormatException(
                    $"Field '{field.CanonicalName}' expects a timestamp such as 2026-09-01 or 2026-09-01T00:00:00Z, got '{value}'.", pos),
            SearchValueType.Protocol => Contains(Protocols, value)
                ? value.ToLowerInvariant()
                : throw new QueryFormatException(
                    $"Field 'protocol' expects one of udp, tcp, tls, snmp, wineventlog — got '{value}'.", pos),
            SearchValueType.ParseStatus => Contains(ParseStatuses, value)
                ? value.ToLowerInvariant()
                : throw new QueryFormatException(
                    $"Field 'parse_status' expects one of raw, rfc3164, rfc5424 — got '{value}'.", pos),
            _ => value,
        };

        private static bool Contains(string[] set, string value) =>
            Array.Exists(set, s => string.Equals(s, value, StringComparison.OrdinalIgnoreCase));

        private static readonly string[] Protocols = ["udp", "tcp", "tls", "snmp", "wineventlog"];
        private static readonly string[] ParseStatuses = ["raw", "rfc3164", "rfc5424"];

        private static string? NormaliseSeverity(string value)
        {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int code))
            {
                return code is >= 0 and <= 7 ? code.ToString(CultureInfo.InvariantCulture) : null;
            }

            return value.ToLowerInvariant() switch
            {
                "emergency" or "emerg" or "panic" => "0",
                "alert" => "1",
                "critical" or "crit" => "2",
                "error" or "err" => "3",
                "warning" or "warn" => "4",
                "notice" => "5",
                "informational" or "info" or "information" => "6",
                "debug" => "7",
                _ => null,
            };
        }

        private static string? NormaliseFacility(string value)
        {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int code))
            {
                return code is >= 0 and <= 23 ? code.ToString(CultureInfo.InvariantCulture) : null;
            }

            return value.ToLowerInvariant() switch
            {
                "kern" or "kernel" => "0",
                "user" => "1",
                "mail" => "2",
                "daemon" or "system" => "3",
                "auth" or "security" => "4",
                "syslog" or "syslogd" => "5",
                "lpr" or "lineprinter" => "6",
                "news" => "7",
                "uucp" => "8",
                "cron" or "clock" => "9",
                "authpriv" => "10",
                "ftp" => "11",
                "ntp" => "12",
                "audit" or "logaudit" => "13",
                "logalert" => "14",
                "clockdaemon" or "cron2" => "15",
                "local0" => "16",
                "local1" => "17",
                "local2" => "18",
                "local3" => "19",
                "local4" => "20",
                "local5" => "21",
                "local6" => "22",
                "local7" => "23",
                _ => null,
            };
        }

        private static string? NormaliseTimestamp(string value) =>
            DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset dto)
                ? dto.UtcDateTime.ToString("o", CultureInfo.InvariantCulture)
                : null;
    }

    private static string Describe(ComparisonOperator op) => op switch
    {
        ComparisonOperator.GreaterThan => ">",
        ComparisonOperator.GreaterThanOrEqual => ">=",
        ComparisonOperator.LessThan => "<",
        ComparisonOperator.LessThanOrEqual => "<=",
        ComparisonOperator.NotEquals => "!=",
        _ => ":",
    };

    // ------------------------------------------------------------------ recursive-descent

    private sealed class TreeParser(List<Token> tokens)
    {
        private int _i;

        public QueryNode ParseQuery()
        {
            QueryNode node = ParseOr();
            if (_i < tokens.Count)
            {
                Token extra = tokens[_i];
                throw new QueryFormatException($"Unexpected '{extra.Display}'.", extra.Position);
            }

            return node;
        }

        private QueryNode ParseOr()
        {
            QueryNode left = ParseAnd();
            while (Peek()?.Kind == TokenKind.Or)
            {
                Token op = Next();
                QueryNode right = ParseAndRequired(op, "OR");
                left = new OrNode(left, right);
            }

            return left;
        }

        private QueryNode ParseAnd()
        {
            QueryNode left = ParseUnary();
            while (true)
            {
                Token? p = Peek();
                if (p is null)
                {
                    break;
                }

                if (p.Value.Kind == TokenKind.And)
                {
                    Token op = Next();
                    left = new AndNode(left, ParseUnaryRequired(op, "AND"));
                    continue;
                }

                if (p.Value.Kind is TokenKind.Term or TokenKind.LParen or TokenKind.Not)
                {
                    left = new AndNode(left, ParseUnary());
                    continue;
                }

                break;
            }

            return left;
        }

        private QueryNode ParseAndRequired(Token op, string name)
        {
            if (Peek() is null || Peek()!.Value.Kind is TokenKind.Or or TokenKind.And or TokenKind.RParen)
            {
                throw new QueryFormatException($"Expected a term after '{name}'.", op.Position + name.Length);
            }

            return ParseAnd();
        }

        private QueryNode ParseUnaryRequired(Token op, string name)
        {
            if (Peek() is null || Peek()!.Value.Kind is TokenKind.Or or TokenKind.And or TokenKind.RParen)
            {
                throw new QueryFormatException($"Expected a term after '{name}'.", op.Position + name.Length);
            }

            return ParseUnary();
        }

        private QueryNode ParseUnary()
        {
            if (Peek()?.Kind == TokenKind.Not)
            {
                Token op = Next();
                if (Peek() is null || Peek()!.Value.Kind is TokenKind.Or or TokenKind.And or TokenKind.RParen)
                {
                    throw new QueryFormatException("Expected a term after 'NOT'.", op.Position + 1);
                }

                return new NotNode(ParseUnary());
            }

            return ParsePrimary();
        }

        private QueryNode ParsePrimary()
        {
            Token? p = Peek();
            if (p is null)
            {
                throw new QueryFormatException("Expected a term.", tokens[^1].Position + tokens[^1].Display.Length);
            }

            switch (p.Value.Kind)
            {
                case TokenKind.Term:
                    return Next().Node!;

                case TokenKind.LParen:
                    Token open = Next();
                    if (Peek()?.Kind == TokenKind.RParen)
                    {
                        throw new QueryFormatException(
                            "Empty group '()' — put a term between the parentheses.", open.Position);
                    }

                    QueryNode inner = ParseOr();
                    if (Peek()?.Kind != TokenKind.RParen)
                    {
                        throw new QueryFormatException("Unclosed '(' — add a matching ')'.", open.Position);
                    }

                    Next(); // ')'
                    return inner;

                case TokenKind.RParen:
                    throw new QueryFormatException("Unexpected ')' — no matching '('.", p.Value.Position);

                case TokenKind.And:
                case TokenKind.Or:
                    throw new QueryFormatException(
                        $"Expected a term before '{p.Value.Display}'.", p.Value.Position);

                default:
                    throw new QueryFormatException("Expected a term.", p.Value.Position);
            }
        }

        private Token? Peek() => _i < tokens.Count ? tokens[_i] : null;

        private Token Next() => tokens[_i++];
    }
}
