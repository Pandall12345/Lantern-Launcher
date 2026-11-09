using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanternLauncher.Services;

public enum BrowseType { Mod, ModPack, Shader, ResourcePack }

public enum BrowseSort { Relevance, Downloads, Follows, Newest, Updated }

/// <summary>
/// Client for the public Modrinth v2 API (no auth required).
/// Reference for endpoints/parameters: PCL-CE Modules/Minecraft/ModComp.cs
/// (re-implemented from scratch against https://docs.modrinth.com).
/// </summary>
public static class ModrinthService
{
    private const string ApiBase = "https://api.modrinth.com/v2";
    public const int PageSize = 40;

    /// <summary>Browse page size (seven rows fit on screen without scrolling).</summary>
    public const int BrowsePageSize = 7;

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var c = new HttpClient();
        // Modrinth asks third-party clients to send a descriptive UA.
        c.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent + " (Modrinth client)");
        c.Timeout = TimeSpan.FromSeconds(20);
        return c;
    }

    // Wire DTOs

    private sealed class SearchEnvelope
    {
        [JsonPropertyName("hits")] public List<SearchHit> Hits { get; set; } = new();
        [JsonPropertyName("total_hits")] public int TotalHits { get; set; }
    }

    private sealed class SearchHit
    {
        [JsonPropertyName("project_id")] public string ProjectId { get; set; } = "";
        [JsonPropertyName("project_type")] public string ProjectType { get; set; } = "";
        [JsonPropertyName("slug")] public string Slug { get; set; } = "";
        [JsonPropertyName("author")] public string Author { get; set; } = "";
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("description")] public string Description { get; set; } = "";
        [JsonPropertyName("categories")] public List<string> Categories { get; set; } = new();
        [JsonPropertyName("display_categories")] public List<string> DisplayCategories { get; set; } = new();
        [JsonPropertyName("icon_url")] public string? IconUrl { get; set; }
        [JsonPropertyName("downloads")] public long Downloads { get; set; }
        [JsonPropertyName("follows")] public long Follows { get; set; }
        [JsonPropertyName("date_modified")] public DateTime DateModified { get; set; }
    }

    private sealed class VersionDto
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("version_number")] public string VersionNumber { get; set; } = "";
        [JsonPropertyName("loaders")] public List<string> Loaders { get; set; } = new();
        [JsonPropertyName("game_versions")] public List<string> GameVersions { get; set; } = new();
        [JsonPropertyName("date_published")] public DateTime DatePublished { get; set; }
        [JsonPropertyName("files")] public List<VersionFileDto> Files { get; set; } = new();
    }

    private sealed class VersionFileDto
    {
        [JsonPropertyName("url")] public string Url { get; set; } = "";
        [JsonPropertyName("filename")] public string FileName { get; set; } = "";
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("primary")] public bool Primary { get; set; }
    }

    private sealed class ProjectDto
    {
        [JsonPropertyName("body")] public string Body { get; set; } = "";
    }

    // Search

    public static async Task<(List<ModSearchResult> Items, int TotalHits)> SearchAsync(
        BrowseType type, string query, string gameVersion, string loader, BrowseSort sort,
        int offset, CancellationToken ct = default, int limit = PageSize)
    {
        var index = sort switch
        {
            BrowseSort.Downloads => "downloads",
            BrowseSort.Follows => "follows",
            BrowseSort.Newest => "newest",
            BrowseSort.Updated => "updated",
            _ => "relevance"
        };

        var url = $"{ApiBase}/search?limit={limit}&offset={offset}&index={index}" +
                  $"&query={Uri.EscapeDataString(query ?? "")}";

        // Facets are AND-ed groups: [["project_type:mod"],["versions:1.21.1"]]
        var facets = new List<string> { $"project_type:{TypeToString(type)}" };
        if (type == BrowseType.Mod && !string.IsNullOrEmpty(loader) && loader != "any")
            facets.Add($"categories:{loader}");
        if (!string.IsNullOrEmpty(gameVersion))
            facets.Add($"versions:{gameVersion}");
        var facetsJson = JsonSerializer.Serialize(facets.Select(f => new[] { f }));
        url += "&facets=" + Uri.EscapeDataString(facetsJson);

        using var resp = await Http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var data = await JsonSerializer.DeserializeAsync<SearchEnvelope>(stream, cancellationToken: ct)
                   ?? new SearchEnvelope();

        var items = data.Hits.Select(h => new ModSearchResult
        {
            ProjectId = h.ProjectId,
            Slug = h.Slug,
            Title = h.Title,
            Description = h.Description,
            Author = h.Author,
            IconUrl = h.IconUrl ?? "",
            Downloads = h.Downloads,
            Follows = h.Follows,
            DisplayCategories = h.DisplayCategories.Count > 0 ? h.DisplayCategories : h.Categories,
            ProjectType = h.ProjectType,
            DateModified = h.DateModified
        }).ToList();

        return (items, data.TotalHits);
    }

    public static string TypeToString(BrowseType type) => type switch
    {
        BrowseType.Mod => "mod",
        BrowseType.ModPack => "modpack",
        BrowseType.Shader => "shader",
        _ => "resourcepack"
    };

    // Project versions

    /// <summary>All published versions of a project, newest first (API order).</summary>
    public static async Task<List<ModVersion>> GetVersionsAsync(string projectId, CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync($"{ApiBase}/project/{Uri.EscapeDataString(projectId)}/version", ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var dtos = await JsonSerializer.DeserializeAsync<List<VersionDto>>(stream, cancellationToken: ct)
                   ?? new List<VersionDto>();

        var list = new List<ModVersion>();
        foreach (var v in dtos)
        {
            // Prefer the primary downloadable file; fall back to the first one.
            var file = v.Files.FirstOrDefault(f => f.Primary) ?? v.Files.FirstOrDefault();
            if (file is null) continue; // version without files is not downloadable
            list.Add(new ModVersion
            {
                Name = v.Name,
                VersionNumber = v.VersionNumber,
                Loaders = v.Loaders,
                GameVersions = v.GameVersions,
                DatePublished = v.DatePublished,
                DownloadUrl = file.Url,
                FileName = file.FileName,
                SizeBytes = file.Size
            });
        }
        return list;
    }

    /// <summary>Raw project description body (markdown).</summary>
    public static async Task<string> GetProjectBodyAsync(string projectId, CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync($"{ApiBase}/project/{Uri.EscapeDataString(projectId)}", ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var dto = await JsonSerializer.DeserializeAsync<ProjectDto>(stream, cancellationToken: ct);
        return dto?.Body ?? "";
    }

    // Shared download client (files live on cdn.modrinth.com)

    public static HttpClient DownloadHttp => Http;
}
