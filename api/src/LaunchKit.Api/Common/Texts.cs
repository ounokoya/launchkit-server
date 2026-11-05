using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Net.Http.Headers;

namespace LaunchKit.Api.Common;

/// <summary>
/// Texts exported from brand/textes (ADR-0007): flat keys per locale, simple ICU placeholders such as <c>{email}</c>.
/// The API only uses texts without plural forms.
/// </summary>
public sealed partial class Texts
{
    public static readonly string[] Locales = ["fr", "en"];
    public const string DefaultLocale = "fr";

    private readonly Dictionary<string, Dictionary<string, string>> _byLocale;

    public Texts(Dictionary<string, Dictionary<string, string>> byLocale) => _byLocale = byLocale;

    public static Texts Load(string directory)
    {
        var byLocale = Locales.ToDictionary(
            l => l,
            l => JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(directory, $"{l}.json")))!);
        return new Texts(byLocale);
    }

    public bool Has(string locale, string key) => _byLocale.TryGetValue(locale, out var t) && t.ContainsKey(key);

    /// <summary>Returns the text with its placeholders filled; an unknown key returns the key itself.</summary>
    public string Get(string locale, string key, IReadOnlyDictionary<string, object?>? args = null)
    {
        if (!_byLocale.TryGetValue(locale, out var texts))
            texts = _byLocale[DefaultLocale];
        if (!texts.TryGetValue(key, out var text))
            return key;
        if (args is null || args.Count == 0)
            return text;
        return Placeholder().Replace(text, m => args.TryGetValue(m.Groups[1].Value, out var v)
            ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""
            : m.Value);
    }

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex Placeholder();

    public static string Normalize(string? locale)
        => locale is not null && Locales.Contains(locale) ? locale : DefaultLocale;

    /// <summary>Language of the messages: the signed-in user's, then Accept-Language, then French.</summary>
    public static string Resolve(HttpContext http)
    {
        var claim = http.User.FindFirst(Auth.Claims.Locale)?.Value;
        if (claim is not null && Locales.Contains(claim))
            return claim;
        var header = http.Request.Headers.AcceptLanguage.ToString();
        if (StringWithQualityHeaderValue.TryParseList(header.Split(','), out var langs))
        {
            foreach (var lang in langs.OrderByDescending(l => l.Quality ?? 1))
            {
                var code = lang.Value.Value?.Split('-')[0].ToLowerInvariant();
                if (code is not null && Locales.Contains(code))
                    return code;
            }
        }
        return DefaultLocale;
    }
}
