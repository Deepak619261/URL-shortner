namespace UrlShortener.Api.Models;

public class ShortCode
{
    public long Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string LongUrl { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedByIp { get; set; }

    // null for anonymous shortens; set when an authenticated user creates it.
    // Used by DELETE for ownership check.
    public long? UserId { get; set; }
}
