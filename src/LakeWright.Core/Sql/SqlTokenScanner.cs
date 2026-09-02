namespace LakeWright.Core.Sql;

/// <summary>Finds SQL tokens while ignoring strings, comments, and quoted identifiers.</summary>
public static class SqlTokenScanner
{
    /// <summary>
    /// Returns whether <paramref name="character"/> occurs in executable SQL rather than a
    /// quoted literal, comment, or backtick identifier.
    /// </summary>
    public static bool ContainsExecutableCharacter(string sql, char character)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        for (var index = 0; index < sql.Length; index++)
        {
            var current = sql[index];
            var next = index + 1 < sql.Length ? sql[index + 1] : '\0';
            if (current == '\'')
            {
                index = SkipQuoted(sql, index, '\'');
                continue;
            }
            if (current == '`')
            {
                index = SkipQuoted(sql, index, '`');
                continue;
            }
            if (current == '-' && next == '-')
            {
                index = sql.IndexOf('\n', index + 2);
                if (index < 0) { break; }
                continue;
            }
            if (current == '/' && next == '*')
            {
                var end = sql.IndexOf("*/", index + 2, StringComparison.Ordinal);
                if (end < 0) { break; }
                index = end + 1;
                continue;
            }

            if (current == character)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns offsets of <paramref name="token"/> that occur in executable SQL.</summary>
    public static IReadOnlyList<int> Find(string sql, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var hits = new List<int>();
        for (var index = 0; index < sql.Length; index++)
        {
            var current = sql[index];
            var next = index + 1 < sql.Length ? sql[index + 1] : '\0';
            if (current == '\'')
            {
                index = SkipQuoted(sql, index, '\'');
                continue;
            }
            if (current == '`')
            {
                index = SkipQuoted(sql, index, '`');
                continue;
            }
            if (current == '-' && next == '-')
            {
                index = sql.IndexOf('\n', index + 2);
                if (index < 0) { break; }
                continue;
            }
            if (current == '/' && next == '*')
            {
                var end = sql.IndexOf("*/", index + 2, StringComparison.Ordinal);
                if (end < 0) { break; }
                index = end + 1;
                continue;
            }

            if (Matches(sql, token, index))
            {
                hits.Add(index);
                index += token.Length - 1;
            }
        }
        return hits;
    }

    private static int SkipQuoted(string sql, int index, char quote)
    {
        for (index++; index < sql.Length; index++)
        {
            if (sql[index] != quote) { continue; }
            if (quote == '\'' && index + 1 < sql.Length && sql[index + 1] == quote)
            {
                index++;
                continue;
            }
            return index;
        }
        return sql.Length;
    }

    private static bool Matches(string sql, string token, int index)
    {
        if (index + token.Length > sql.Length
            || (index > 0 && IsIdentifierPart(sql[index - 1]))
            || (index + token.Length < sql.Length && IsIdentifierPart(sql[index + token.Length])))
        {
            return false;
        }

        return string.Compare(sql, index, token, 0, token.Length, StringComparison.OrdinalIgnoreCase) == 0;
    }

    private static bool IsIdentifierPart(char character) => character == '_' || char.IsLetterOrDigit(character);
}
