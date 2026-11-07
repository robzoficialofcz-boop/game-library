using System.IO;
using System.Text.Json;
using GameLibrary.Models;

namespace GameLibrary.Services;

public sealed class GameStorageService
{
    private readonly string _folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameLibrary");
    private string DatabasePath => Path.Combine(_folder, "games.json");
    public string CoversFolder => Path.Combine(_folder, "Covers");

    public GameStorageService()
    {
        Directory.CreateDirectory(_folder);
        Directory.CreateDirectory(CoversFolder);
    }

    public async Task<List<Game>> LoadAsync()
    {
        if (!File.Exists(DatabasePath)) return new();
        try
        {
            await using var stream = File.OpenRead(DatabasePath);
            return await JsonSerializer.DeserializeAsync<List<Game>>(stream) ?? new();
        }
        catch (JsonException) { return new(); }
        catch (IOException) { return new(); }
    }

    public async Task SaveAsync(IEnumerable<Game> games)
    {
        var temp = DatabasePath + ".tmp";
        await using (var stream = File.Create(temp))
            await JsonSerializer.SerializeAsync(stream, games, new JsonSerializerOptions { WriteIndented = true });
        File.Move(temp, DatabasePath, true);
    }
}
