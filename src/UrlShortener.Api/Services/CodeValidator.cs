namespace UrlShortener.Api.Services;

public static class CodeValidator
{
    // Reserved paths that conflict with our routes / static files
    private static readonly HashSet<string> ReservedWords =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "health", "shorten", "analytics", "openapi",
            "index", "favicon", "robots", "sitemap",
            "admin", "api", "auth", "login", "logout"
        };

    public const int MinLength = 3;
    public const int MaxLength = 10;

    public static (bool ok, string? error) Validate(string code)
    {
        if (string.IsNullOrEmpty(code))
            return (false, "Custom code is empty");
        if (code.Length < MinLength || code.Length > MaxLength)
            return (false, $"Custom code must be {MinLength}-{MaxLength} characters");
        if (ReservedWords.Contains(code))
            return (false, $"'{code}' is a reserved word");
        foreach (var c in code)
        {
            if (!IsBase62(c))
                return (false, "Custom code can only contain letters and digits (base62)");
        }
        return (true, null);
    }

    private static bool IsBase62(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
}
