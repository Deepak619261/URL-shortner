namespace UrlShortener.Api.Models;

public class Click
{
    public long Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public DateTime ClickedAt { get; set; } = DateTime.UtcNow;
    public string? Ip { get; set; }
    public string? UserAgent { get; set; }
}
