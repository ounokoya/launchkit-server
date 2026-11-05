using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace LaunchKit.Api.Common;

/// <summary>Dates are ISO 8601 UTC strings with milliseconds, sortable as text (server-donnees.md).</summary>
public static class Iso
{
    public const string Format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public static string Now(TimeProvider clock) => Of(clock.GetUtcNow());

    public static string Of(DateTimeOffset t) => t.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture);
}

public static class Emails
{
    public static string Normalize(string email) => email.Trim().ToLowerInvariant();

    /// <summary>Pragmatic check: one @, a dot in the domain, no spaces. The real proof is the verification link.</summary>
    public static bool LooksValid(string email)
    {
        var at = email.IndexOf('@', StringComparison.Ordinal);
        return email.Length <= 254
               && at > 0
               && at == email.LastIndexOf('@')
               && email.IndexOf('.', at) > at + 1
               && !email.EndsWith('.')
               && !email.Any(char.IsWhiteSpace);
    }

    /// <summary>First word of a full name, used to greet people ("Bonjour Marie").</summary>
    public static string FirstName(string name)
    {
        var trimmed = name.Trim();
        var space = trimmed.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? trimmed : trimmed[..space];
    }
}

/// <summary>Opaque tokens (ADR-0004): 32 random bytes in base64url; only their SHA-256 is stored.</summary>
public static class Tokens
{
    public static string New() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

public enum OrgRole
{
    Member = 0,
    Admin = 1,
    Owner = 2,
}

public static class Roles
{
    public static string Code(OrgRole role) => role switch
    {
        OrgRole.Owner => "owner",
        OrgRole.Admin => "admin",
        _ => "member",
    };

    public static OrgRole Parse(string code) => code switch
    {
        "owner" => OrgRole.Owner,
        "admin" => OrgRole.Admin,
        "member" => OrgRole.Member,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown role"),
    };

    /// <summary>Roles that can be given by invitation or role change: never owner.</summary>
    public static readonly string[] Assignable = ["admin", "member"];
}
