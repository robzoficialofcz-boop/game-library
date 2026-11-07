using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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

    public async Task<List<Game>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(DatabasePath)) return new List<Game>();
        try
        {
            await using var stream = new FileStream(DatabasePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<List<Game>>(stream, cancellationToken: cancellationToken)
                ?? new List<Game>();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("O arquivo da biblioteca contém dados inválidos. O original foi preservado.", ex);
        }
    }

    public async Task SaveAsync(IEnumerable<Game> games, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_folder);
        var temp = DatabasePath + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, games,
                    new JsonSerializerOptions { WriteIndented = true }, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temp, DatabasePath, true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
        }
    }
}
