using System.Globalization;
using System.Text;

namespace WaybackViewer.Models;

public static class SearchTextMatcher
{
    public static string CacheKey(string query) => Normalize(query);

    public static bool Matches(string query, string? value)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var queryTokens = Tokenize(query);
        if (queryTokens.Count == 0) return true;

        var haystacks = BuildVariants(value);
        foreach (var hay in haystacks)
        {
            var allMatched = true;
            foreach (var token in queryTokens)
            {
                var tokenVariants = BuildVariants(token);
                if (!tokenVariants.Any(v => v.Length > 0 && hay.Contains(v, StringComparison.Ordinal)))
                {
                    allMatched = false;
                    break;
                }
            }

            if (allMatched) return true;
        }

        return false;
    }

    public static bool MatchesAny(string query, params string?[] values)
    {
        foreach (var value in values)
            if (Matches(query, value))
                return true;
        return false;
    }

    private static List<string> Tokenize(string value) =>
        Normalize(value)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static IReadOnlyList<string> BuildVariants(string value)
    {
        var normalized = Normalize(value);
        var cyrToLat = Normalize(TransliterateCyrillicToLatin(value));
        var latToCyr = Normalize(TransliterateLatinToCyrillic(value));
        var simplified = SimplifyLatinDigraphs(normalized);
        var simplifiedCyrToLat = SimplifyLatinDigraphs(cyrToLat);

        return new[] { normalized, cyrToLat, latToCyr, simplified, simplifiedCyrToLat }
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string SimplifyLatinDigraphs(string value) =>
        value
            .Replace("sh", "s", StringComparison.Ordinal)
            .Replace("ch", "c", StringComparison.Ordinal)
            .Replace("zh", "z", StringComparison.Ordinal)
            .Replace("dj", "d", StringComparison.Ordinal)
            .Replace("dz", "z", StringComparison.Ordinal);

    private static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var formD = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(formD.Length);
        var prevSpace = false;
        foreach (var ch in formD)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;

            var lower = char.ToLowerInvariant(ch);
            if (char.IsLetterOrDigit(lower) || lower is '/' or ':' or '.')
            {
                sb.Append(lower);
                prevSpace = false;
            }
            else if (!prevSpace)
            {
                sb.Append(' ');
                prevSpace = true;
            }
        }

        return sb.ToString().Trim();
    }

    private static string TransliterateCyrillicToLatin(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var sb = new StringBuilder(value.Length * 2);
        foreach (var ch in value)
        {
            sb.Append(ch switch
            {
                'а' => "a", 'б' => "b", 'в' => "v", 'г' => "g", 'д' => "d", 'ђ' => "dj", 'е' => "e", 'ж' => "zh",
                'з' => "z", 'и' => "i", 'ј' => "j", 'к' => "k", 'л' => "l", 'љ' => "lj", 'м' => "m", 'н' => "n",
                'њ' => "nj", 'о' => "o", 'п' => "p", 'р' => "r", 'с' => "s", 'т' => "t", 'ћ' => "c", 'у' => "u",
                'ф' => "f", 'х' => "h", 'ц' => "c", 'ч' => "ch", 'џ' => "dz", 'ш' => "sh",
                'А' => "a", 'Б' => "b", 'В' => "v", 'Г' => "g", 'Д' => "d", 'Ђ' => "dj", 'Е' => "e", 'Ж' => "zh",
                'З' => "z", 'И' => "i", 'Ј' => "j", 'К' => "k", 'Л' => "l", 'Љ' => "lj", 'М' => "m", 'Н' => "n",
                'Њ' => "nj", 'О' => "o", 'П' => "p", 'Р' => "r", 'С' => "s", 'Т' => "t", 'Ћ' => "c", 'У' => "u",
                'Ф' => "f", 'Х' => "h", 'Ц' => "c", 'Ч' => "ch", 'Џ' => "dz", 'Ш' => "sh",
                _ => ch.ToString()
            });
        }

        return sb.ToString();
    }

    private static string TransliterateLatinToCyrillic(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var s = value.ToLowerInvariant();
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var next2 = i + 1 < s.Length ? s.Substring(i, 2) : string.Empty;
            if (next2 == "lj") { sb.Append('љ'); i++; continue; }
            if (next2 == "nj") { sb.Append('њ'); i++; continue; }
            if (next2 == "dj") { sb.Append('ђ'); i++; continue; }
            if (next2 == "dz") { sb.Append('џ'); i++; continue; }
            if (next2 == "zh") { sb.Append('ж'); i++; continue; }
            if (next2 == "ch") { sb.Append('ч'); i++; continue; }
            if (next2 == "sh") { sb.Append('ш'); i++; continue; }

            sb.Append(s[i] switch
            {
                'a' => 'а', 'b' => 'б', 'v' => 'в', 'g' => 'г', 'd' => 'д', 'e' => 'е', 'z' => 'з', 'i' => 'и',
                'j' => 'ј', 'k' => 'к', 'l' => 'л', 'm' => 'м', 'n' => 'н', 'o' => 'о', 'p' => 'п', 'r' => 'р',
                's' => 'с', 't' => 'т', 'u' => 'у', 'f' => 'ф', 'h' => 'х', 'c' => 'ц',
                _ => s[i]
            });
        }

        return sb.ToString();
    }
}