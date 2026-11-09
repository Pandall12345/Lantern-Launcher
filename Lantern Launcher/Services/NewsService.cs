using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanternLauncher.Services;

/// <summary>
/// Client for Mojang's official Minecraft news feed
/// (net-secondary.web.minecraft-services.net — the same endpoint PCL-CE uses).
/// No auth required; responses are already localized (zh-cn path segment).
/// </summary>
public static class NewsService
{
    private const string ApiUrl = "https://net-secondary.web.minecraft-services.net/api/v1.0/zh-cn/search";

    /// <summary>Only links pointing at these hosts (or their subdomains) may be opened.</summary>
    private static readonly string[] AllowedHosts =
    [
        "minecraft.net",
        "minecraft-services.net",
        "microsoft.com"
    ];

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var c = new HttpClient();
        c.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent);
        c.Timeout = TimeSpan.FromSeconds(15);
        return c;
    }

    // Wire format

    private sealed class ApiEnvelope
    {
        [JsonPropertyName("result")]
        public ApiResult? Result { get; set; }
    }

    private sealed class ApiResult
    {
        [JsonPropertyName("results")]
        public List<ApiNews>? Results { get; set; }
    }

    private sealed class ApiNews
    {
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("url")] public string Url { get; set; } = "";
        [JsonPropertyName("description")] public string Description { get; set; } = "";
        [JsonPropertyName("author")] public string? Author { get; set; }
        [JsonPropertyName("image")] public string Image { get; set; } = "";
        [JsonPropertyName("imageAltText")] public string? ImageAltText { get; set; }
        [JsonPropertyName("time")] public long Time { get; set; }
    }

    // Api

    public static async Task<List<NewsItem>> FetchAsync(int page = 1, int pageSize = 12)
    {
        var url = $"{ApiUrl}?pageSize={pageSize}&sortType=Recent&category=News&newsOnly=true&page={page}";
        using var resp = await Http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync();
        var envelope = await JsonSerializer.DeserializeAsync<ApiEnvelope>(stream);

        var list = new List<NewsItem>();
        foreach (var n in envelope?.Result?.Results ?? [])
        {
            list.Add(new NewsItem
            {
                Title = n.Title,
                Url = n.Url,
                // The feed returns HTML-encoded text ("&#39;" etc.) — decode for display.
                Description = WebUtility.HtmlDecode(n.Description),
                Author = n.Author,
                Image = n.Image,
                ImageAltText = n.ImageAltText,
                Time = n.Time
            });
        }
        return list;
    }

    // Safety

    /// <summary>True when the URL is http(s) and points at an allow-listed host
    /// (or a subdomain of one — exact suffix match, so "evilminecraft.net" fails).</summary>
    public static bool IsSafeNewsUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) &&
            !uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
            return false;

        var host = uri.IdnHost;
        foreach (var allowed in AllowedHosts)
        {
            if (host.Equals(allowed, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("." + allowed, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public static void OpenInBrowser(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
