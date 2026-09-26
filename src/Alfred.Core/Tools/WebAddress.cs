namespace Alfred.Core;

public static class WebAddress
{
    /// <summary>Sites people open by name. Keys are compact keys (<see cref="SpokenText.Key"/>).</summary>
    public static readonly IReadOnlyDictionary<string, string> KnownSites = new Dictionary<string, string>
    {
        ["youtube"] = "https://www.youtube.com",
        ["gmail"] = "https://mail.google.com",
        ["google"] = "https://www.google.com",
        ["googlemaps"] = "https://maps.google.com",
        ["github"] = "https://github.com",
        ["wikipedia"] = "https://www.wikipedia.org",
        ["reddit"] = "https://www.reddit.com",
        ["netflix"] = "https://www.netflix.com",
        ["amazon"] = "https://www.amazon.com",
        ["twitter"] = "https://x.com",
        ["x"] = "https://x.com",
        ["linkedin"] = "https://www.linkedin.com",
        ["instagram"] = "https://www.instagram.com",
        ["facebook"] = "https://www.facebook.com",
        ["whatsapp"] = "https://web.whatsapp.com",
        ["chatgpt"] = "https://chatgpt.com",
        ["claude"] = "https://claude.ai",
        ["twitch"] = "https://www.twitch.tv",
    };

    private static readonly HashSet<string> SpokenDomainEndings = ["com", "org", "net", "io", "ai", "dev", "app", "nl", "in", "co", "uk", "tv"];

    /// <summary>
    /// A bare domain as it arrives from normalised speech: "github com" or
    /// "github dot com" → https://github.com. Paths are left to the model.
    /// </summary>
    public static string? Spoken(string words)
    {
        var parts = words.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(p => p != "dot").ToList();
        if (parts.Count is < 2 or > 3 || !SpokenDomainEndings.Contains(parts[^1])) return null;
        var ending = parts[^1];
        parts.RemoveAt(parts.Count - 1);
        return Normalize(string.Concat(parts) + "." + ending);
    }

    /// <summary>
    /// Accepts "youtube.com", "https://youtube.com/x" and similar, returned as
    /// written with a scheme added. Only http and https: the model must not be
    /// able to open file:, shell: or app URL schemes.
    /// </summary>
    public static string? Normalize(string raw)
    {
        var text = raw.Trim();
        if (text.Length == 0 || text.Contains(' ')) return null;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var url)) return null;
        if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps) return null;
        var host = url.Host;
        if (!host.Contains('.') || host.StartsWith('.') || host.EndsWith('.')) return null;
        return text;
    }

    public static string HomePage(Uri url) => $"{url.Scheme}://{url.Host}";
}

public enum SearchEngine { Google, DuckDuckGo, Bing }

public static class SearchEngines
{
    public static string Title(this SearchEngine engine) => engine switch
    {
        SearchEngine.DuckDuckGo => "DuckDuckGo",
        SearchEngine.Bing => "Bing",
        _ => "Google",
    };

    public static string? Url(this SearchEngine engine, string query)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0) return null;
        var root = engine switch
        {
            SearchEngine.DuckDuckGo => "https://duckduckgo.com/",
            SearchEngine.Bing => "https://www.bing.com/search",
            _ => "https://www.google.com/search",
        };
        return $"{root}?q={Uri.EscapeDataString(trimmed)}";
    }
}
