using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GameLibrary.Services;

/// <summary>Busca opcional de capas pela SteamGridDB. Não exige conta no Game Library.</summary>
public sealed class CoverService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public async Task<string?> DownloadAsync(string gameName, string destinationFolder, CancellationToken cancellationToken = default)
    {
        var key = Environment.GetEnvironmentVariable("STEAMGRIDDB_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) return null;

        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://www.steamgriddb.com/api/v2/search/autocomplete/{Uri.EscapeDataString(gameName)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var searchResponse = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        searchResponse.EnsureSuccessStatusCode();

        using var searchStream = await searchResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var search = await JsonDocument.ParseAsync(searchStream, cancellationToken: cancellationToken);
        if (!search.RootElement.TryGetProperty("data", out var results) ||
            results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
            return null;

        var id = results[0].GetProperty("id").GetInt32();
        using var gridsRequest = new HttpRequestMessage(HttpMethod.Get, $"https://www.steamgriddb.com/api/v2/grids/game/{id}");
        gridsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var gridsResponse = await Http.SendAsync(gridsRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        gridsResponse.EnsureSuccessStatusCode();

        using var gridsStream = await gridsResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var grids = await JsonDocument.ParseAsync(gridsStream, cancellationToken: cancellationToken);
        if (!grids.RootElement.TryGetProperty("data", out var items) ||
            items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
            return null;

        var url = items[0].GetProperty("url").GetString();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var imageUri) ||
            (imageUri.Scheme != Uri.UriSchemeHttps && imageUri.Scheme != Uri.UriSchemeHttp))
            return null;

        using var imageResponse = await Http.GetAsync(imageUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        imageResponse.EnsureSuccessStatusCode();
        var mediaType = imageResponse.Content.Headers.ContentType?.MediaType;
        if (mediaType is null || !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return null;

        Directory.CreateDirectory(destinationFolder);
        var extension = mediaType.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            _ => ".jpg"
        };
        var path = Path.Combine(destinationFolder, Guid.NewGuid().ToString("N") + extension);
        await using var source = await imageResponse.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await source.CopyToAsync(target, cancellationToken);
        return path;
    }
}
