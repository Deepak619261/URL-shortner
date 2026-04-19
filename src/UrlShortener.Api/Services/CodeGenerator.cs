using System.Security.Cryptography;

namespace UrlShortener.Api.Services;

public static class CodeGenerator
{
    private const string Alphabet =
        "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";

    public static string Generate(int length = 7)
    {
        return RandomNumberGenerator.GetString(Alphabet, length);
    }
}
