using System.Text.RegularExpressions;

namespace D365FO.Compare.Infrastructure.ODataData;

internal static class DataErrorDetails
{
    public static string Message(string text, IReadOnlySet<string>? identifiers = null, string? query = null)
    {
        text = text.Length > 4096 ? text[..4096] : text;
        text = Regex.Replace(text, @"(?i)Bearer\s+[^\s,;]+", "Bearer <redacted>");
        text = Regex.Replace(text, @"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b", "<redacted-token>");
        text = Regex.Replace(text, @"(?i)https?://[^\s]+", "<redacted-url>");
        text = Regex.Replace(text, @"[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}", "<redacted-email>");
        var secrets = query is null ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : Regex.Matches(Uri.UnescapeDataString(query), "'(?:''|[^'])*'")
                .Select(match => match.Value[1..^1].Replace("''", "'", StringComparison.Ordinal)).Where(value => value.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        text = Regex.Replace(text, "'(?:''|[^'])*'|\"(?:\\\\.|[^\"\\\\])*\"", match =>
        {
            var value = match.Value[1..^1].Replace("''", "'", StringComparison.Ordinal);
            return identifiers?.Contains(value) == true && !secrets.Contains(value) ? match.Value : "<redacted-value>";
        });
        foreach (var secret in secrets.OrderByDescending(value => value.Length))
            text = Regex.Replace(text, @"(?<![\w])" + Regex.Escape(secret) + @"(?![\w])", "<redacted-value>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        text = Regex.Replace(text, @"\b\d{4,}\b", "<redacted-number>");
        return text.Replace('\r', ' ').Replace('\n', ' ').Trim();
    }
    public static string? Identifier(string? value) => value is not null && Regex.IsMatch(value, @"^[A-Za-z0-9_.:-]{1,128}$", RegexOptions.CultureInvariant)
        ? value : null;
}