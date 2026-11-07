using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace GameLibrary.Services;

/// <summary>Busca opcional de capas. O Game Library não exige conta ou login.</summary>
public sealed class CoverService
{
    private readonly HttpClient _http = new();

    public async Task<string?> DownloadAsync(string gameName, string destinationFolder)
    {
        string? key = Environment.GetEnvironmentVariable("STEAMGRIDDB_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) return null;

        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://www.steamgriddb.com/api/v2/search/autocomplete/{Uri.EscapeDataString(gameName)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var searchResponse = await _http.SendAsync(request);
        searchResponse.EnsureSuccessStatusCode();
        using var search = JsonDocument.Parse(await searchResponse.Content.ReadAsStringAsync());
        if (!search.RootElement.TryGetProperty("data", out var results) || results.GetArrayLength() == 0) return null;

        int id = results[0].GetProperty("id").GetInt32();
        using var gridsResponse = await _http.GetAsync($"https://www.steamgriddb.com/api/v2/grids/game/{id}");
        gridsResponse.EnsureSuccessStatusCode();
        using var grids = JsonDocument.Parse(await gridsResponse.Content.ReadAsStringAsync());
        if (!grids.RootElement.TryGetProperty("data", out var items) || items.GetArrayLength() == 0) return null;

        string? url = items[0].GetProperty("url").GetString();
        if (string.IsNullOrWhiteSpace(url)) return null;
        using var imageResponse = await _http.GetAsync(url);
        imageResponse.EnsureSuccessStatusCode();
        byte[] bytes = await imageResponse.Content.ReadAsByteArrayAsync();

        string safe = string.Concat(gameName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        Directory.CreateDirectory(destinationFolder);
        string path = Path.Combine(destinationFolder, safe + ".jpg");
        await File.WriteAllBytesAsync(path, bytes);
        return path;
    }
}
