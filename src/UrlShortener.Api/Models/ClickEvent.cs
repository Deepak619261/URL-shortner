namespace UrlShortener.Api.Models;

public record ClickEvent(string Code, DateTime ClickedAt, string? Ip, string? UserAgent);
