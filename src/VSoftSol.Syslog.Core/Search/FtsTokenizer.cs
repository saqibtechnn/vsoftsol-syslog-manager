using System.Text;

namespace VSoftSol.Syslog.Core.Search;

/// <summary>
/// Mirrors the SQLite FTS5 tokenizer the event index is built with
/// (<c>unicode61 remove_diacritics 2 tokenchars '.:-_/@'</c>). The golden-oracle matcher
/// tokenises text with this so its free-text results are identical to the FTS MATCH path.
/// </summary>
public static class FtsTokenizer
{
    /// <summary>The extra characters FTS5 keeps inside a token (IPs, MACs, paths, emails).</summary>
    public const string TokenChars = ".:-_/@";

    /// <summary>
    /// Splits <paramref name="text"/> into lower-cased, diacritic-folded tokens using the
    /// same rules as the index: a token is a maximal run of Unicode letters/digits plus
    /// the configured <see cref="TokenChars"/>. Attached token characters are kept exactly
    /// as FTS5 keeps them, so <c>denied.</c> is one token and a bare <c>denied</c> query
    /// does not match it — the oracle and the index agree because both use this method.
    /// </summary>
    public static IReadOnlyList<string> Tokenize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var tokens = new List<string>();
        var current = new StringBuilder();

        foreach (Rune rune in text.EnumerateRunes())
        {
            if (IsTokenChar(rune))
            {
                current.Append(rune.ToString());
            }
            else if (current.Length > 0)
            {
                tokens.Add(Fold(current.ToString()));
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(Fold(current.ToString()));
        }

        return tokens;
    }

    private static bool IsTokenChar(Rune rune) =>
        Rune.IsLetterOrDigit(rune) ||
        (rune.IsAscii && TokenChars.Contains((char)rune.Value, StringComparison.Ordinal));

    /// <summary>
    /// Lower-case and strip diacritics the way SQLite's <c>unicode61 remove_diacritics 2</c>
    /// tokenizer does. SQLite folds with its own built-in table, not ICU, so this replicates
    /// that behaviour directly (and stays correct under the runtime's invariant-globalization
    /// mode, where <see cref="string.Normalize()"/> is a no-op): drop combining marks
    /// (U+0300–U+036F) and map pre-composed Latin letters to their base letter.
    /// </summary>
    private static string Fold(string token)
    {
        var sb = new StringBuilder(token.Length);
        foreach (Rune rune in token.EnumerateRunes())
        {
            int cp = Rune.ToLowerInvariant(rune).Value;
            if (cp is >= 0x0300 and <= 0x036F)
            {
                continue; // combining diacritical mark
            }

            if (LatinFold.TryGetValue(cp, out char baseLetter))
            {
                sb.Append(baseLetter);
            }
            else
            {
                sb.Append(Rune.ToLowerInvariant(rune).ToString());
            }
        }

        return sb.ToString();
    }

    // Pre-composed lower-case Latin letters (Latin-1 Supplement + common Latin Extended-A)
    // → base letter. Keys are already lower-cased code points.
    private static readonly Dictionary<int, char> LatinFold = BuildLatinFold();

    private static Dictionary<int, char> BuildLatinFold()
    {
        var map = new Dictionary<int, char>();

        void Add(string accented, char baseLetter)
        {
            foreach (char c in accented)
            {
                map[char.ToLowerInvariant(c)] = baseLetter;
            }
        }

        Add("ÀÁÂÃÄÅĀĂĄ", 'a');
        Add("Çćĉċč", 'c');
        Add("ÐĎĐ", 'd');
        Add("ÈÉÊËĒĔĖĘĚ", 'e');
        Add("ĜĞĠĢ", 'g');
        Add("Ĥħ", 'h');
        Add("ÌÍÎÏĨĪĬĮİ", 'i');
        Add("Ĵ", 'j');
        Add("Ķ", 'k');
        Add("ĹĻĽĿŁ", 'l');
        Add("ÑŃŅŇ", 'n');
        Add("ÒÓÔÕÖØŌŎŐ", 'o');
        Add("ŔŖŘ", 'r');
        Add("ŚŜŞŠ", 's');
        Add("ŢŤŦ", 't');
        Add("ÙÚÛÜŨŪŬŮŰŲ", 'u');
        Add("Ŵ", 'w');
        Add("ÝŶŸ", 'y');
        Add("ŹŻŽ", 'z');

        return map;
    }
}
