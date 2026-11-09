using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LanternLauncher.Models;

namespace LanternLauncher.Services;

public sealed record DownloadItem(
    string Label,
    string Url,
    string LocalPath,
    string? Sha1,
    long Size);

public sealed record DownloadProgress(
    int Completed,
    int Total,
    long BytesDone,
    string CurrentLabel)
{
    public double Fraction => Total <= 0 ? 0 : (double)Completed / Total;
}

public sealed record InstallSummary(
    int Downloaded,
    int Skipped,
    int Failed,
    long Bytes,
    List<string> Errors);

/// <summary>
/// Version install and repair: places the client jar, libraries, asset index and asset objects under .minecraft.
/// Policy: official source first, BMCLAPI mirror on failure; each file lands as .part, is SHA1-checked, then renamed.
/// </summary>
public sealed class DownloadService
{
    private const int Parallelism = 8;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            AllowAutoRedirect = true,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent + " (+wpf)");
        return client;
    }

    private readonly VersionService _versions;

    public DownloadService(VersionService versions) => _versions = versions;

    // Mirrors

    /// <summary>Official URL to BMCLAPI mirror URL, or null when there is no mapping.</summary>
    public static string? MirrorUrl(string url)
    {
        const string libs = "https://libraries.minecraft.net/";
        const string res = "https://resources.download.minecraft.net/";
        const string bmcl = "https://bmclapi2.bangbang93.com";

        if (url.StartsWith(libs, StringComparison.OrdinalIgnoreCase))
            return bmcl + "/maven/" + url[libs.Length..];
        if (url.StartsWith(res, StringComparison.OrdinalIgnoreCase))
            return bmcl + "/assets/" + url[res.Length..];
        return null;
    }

    // Plan

    /// <summary>Works out every file the version needs. No network calls except the asset index.</summary>
    public async Task<List<DownloadItem>> PlanAsync(
        ResolvedVersion resolved,
        bool includeAssets,
        CancellationToken ct = default)
    {
        var chain = _versions.ResolveChain(resolved.Id);
        var items = new List<DownloadItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string label, string? url, string? relPath, string? sha1, long size, string absPath)
        {
            if (string.IsNullOrWhiteSpace(url) || !seen.Add(absPath)) return;
            items.Add(new DownloadItem(label, url!, absPath, sha1, size));
        }

        foreach (var json in chain)
        {
            // Client main jar: only the root version has one.
            var client = json.Downloads?.Client;
            if (client is not null && !string.IsNullOrWhiteSpace(client.Url))
            {
                var abs = Path.Combine(_versions.VersionsDir, json.Id, json.Id + ".jar");
                Add(json.Id + ".jar", client.Url, client.Path, client.Sha1, client.Size, abs);
            }

            foreach (var lib in json.Libraries)
            {
                if (!VersionService.IsAllowed(lib.Rules)) continue;

                var art = lib.Downloads?.Artifact;
                if (art is not null && !string.IsNullOrWhiteSpace(art.Url) && !string.IsNullOrWhiteSpace(art.Path))
                {
                    var abs = Path.Combine(_versions.LibrariesDir, art.Path!.Replace('/', Path.DirectorySeparatorChar));
                    Add(lib.Name, art.Url, art.Path, art.Sha1, art.Size, abs);
                }

                // Natives classifiers, pre-1.19 only.
                if (lib.Natives is not null &&
                    lib.Natives.TryGetValue("windows", out var classifier) &&
                    lib.Downloads?.Classifiers is not null &&
                    lib.Downloads.Classifiers.TryGetValue(classifier, out var nativeArt) &&
                    !string.IsNullOrWhiteSpace(nativeArt.Url) && !string.IsNullOrWhiteSpace(nativeArt.Path))
                {
                    var abs = Path.Combine(_versions.LibrariesDir, nativeArt.Path!.Replace('/', Path.DirectorySeparatorChar));
                    Add(lib.Name + " (natives)", nativeArt.Url, nativeArt.Path, nativeArt.Sha1, nativeArt.Size, abs);
                }
            }
        }

        // asset index
        var indexId = resolved.AssetIndexId;
        if (!string.IsNullOrWhiteSpace(indexId))
        {
            var indexPath = _versions.AssetIndexPath(indexId);
            if (!File.Exists(indexPath))
            {
                var id = chain.Select(c => c.AssetIndex).LastOrDefault(a => a is not null && a.Id == indexId);
                if (id is not null && !string.IsNullOrWhiteSpace(id.Url))
                    Add("assets/indexes/" + indexId + ".json", id.Url, null, id.Sha1, id.Size, indexPath);
            }

            if (includeAssets)
                await AddAssetObjectsAsync(indexId!, indexPath, items, seen, ct).ConfigureAwait(false);
        }

        return items;
    }

    private async Task AddAssetObjectsAsync(
        string indexId,
        string indexPath,
        List<DownloadItem> items,
        HashSet<string> seen,
        CancellationToken ct)
    {
        if (!File.Exists(indexPath)) return;

        AssetIndexFile? index;
        try
        {
            await using var fs = File.OpenRead(indexPath);
            index = await JsonSerializer
                .DeserializeAsync<AssetIndexFile>(fs, cancellationToken: ct)
                .ConfigureAwait(false);
        }
        catch (JsonException) { return; }
        catch (IOException) { return; }

        if (index?.Objects is null) return;

        foreach (var (name, obj) in index.Objects)
        {
            if (obj?.Hash is null || obj.Hash.Length < 3) continue;
            var bucket = obj.Hash[..2];
            var abs = Path.Combine(_versions.AssetsDir, "objects", bucket, obj.Hash);
            if (!seen.Add(abs)) continue;
            items.Add(new DownloadItem(
                "assets/" + name,
                $"https://resources.download.minecraft.net/{bucket}/{obj.Hash}",
                abs, obj.Hash, obj.Size));
        }
    }

    // Execute

    public async Task<InstallSummary> RunAsync(
        IReadOnlyList<DownloadItem> items,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var total = items.Count;
        var done = 0;
        var downloaded = 0;
        var skipped = 0;
        var failed = 0;
        long bytes = 0;
        var errors = new List<string>();

        using var gate = new SemaphoreSlim(Parallelism);

        var tasks = items.Select(async item =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (await IsUpToDateAsync(item, ct).ConfigureAwait(false))
                {
                    Interlocked.Increment(ref skipped);
                    Interlocked.Increment(ref done);
                    progress?.Report(new DownloadProgress(done, total, Interlocked.Read(ref bytes), item.Label));
                    return;
                }

                var written = await FetchAsync(item, ct).ConfigureAwait(false);
                Interlocked.Increment(ref downloaded);
                Interlocked.Add(ref bytes, written);
                Interlocked.Increment(ref done);
                progress?.Report(new DownloadProgress(done, total, Interlocked.Read(ref bytes), item.Label));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failed);
                Interlocked.Increment(ref done);
                lock (errors) errors.Add($"{item.Label}: {ex.Message}");
                progress?.Report(new DownloadProgress(done, total, Interlocked.Read(ref bytes), item.Label));
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        return new InstallSummary(downloaded, skipped, failed, bytes, errors);
    }

    private static async Task<bool> IsUpToDateAsync(DownloadItem item, CancellationToken ct)
    {
        if (!File.Exists(item.LocalPath)) return false;
        var info = new FileInfo(item.LocalPath);
        if (item.Size > 0 && info.Length != item.Size) return false;
        if (string.IsNullOrWhiteSpace(item.Sha1)) return item.Size <= 0 || info.Length == item.Size;

        var hash = await Sha1Async(item.LocalPath, ct).ConfigureAwait(false);
        return string.Equals(hash, item.Sha1, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<long> FetchAsync(DownloadItem item, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(item.LocalPath)!);

        var part = item.LocalPath + ".part";
        var candidates = new List<string> { item.Url };
        var mirror = MirrorUrl(item.Url);
        if (mirror is not null) candidates.Add(mirror);

        Exception? last = null;
        foreach (var url in candidates)
        {
            try
            {
                if (File.Exists(part)) File.Delete(part);

                long written = 0;
                await using (var src = await Http.GetStreamAsync(url, ct).ConfigureAwait(false))
                await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        written += read;
                    }
                }

                if (!string.IsNullOrWhiteSpace(item.Sha1))
                {
                    var hash = await Sha1Async(part, ct).ConfigureAwait(false);
                    if (!string.Equals(hash, item.Sha1, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException($"SHA1 mismatch (expected {item.Sha1[..8]}..., got {hash[..8]}...)");
                }

                File.Move(part, item.LocalPath, overwrite: true);
                return written;
            }
            catch (HttpRequestException ex) { last = ex; }
            catch (IOException ex) { last = ex; }
            catch (InvalidDataException ex) { last = ex; }
        }

        if (File.Exists(part)) { try { File.Delete(part); } catch (IOException) { } }
        throw new IOException(last?.Message ?? "Download failed");
    }

    private static async Task<string> Sha1Async(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        var hash = await SHA1.HashDataAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // Asset index layout

    private sealed class AssetIndexFile
    {
        public Dictionary<string, AssetObject?>? Objects { get; set; }
    }

    private sealed class AssetObject
    {
        public string? Hash { get; set; }
        public long Size { get; set; }
    }
}
