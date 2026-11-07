using System.IO;
using System.Diagnostics;
using GameLibrary.Models;

namespace GameLibrary.Services;

public sealed class GameLauncherService
{
    public Process Start(Game game)
    {
        if (!File.Exists(game.ExecutablePath))
            throw new FileNotFoundException("O executável não foi encontrado.", game.ExecutablePath);
        return Process.Start(new ProcessStartInfo
        {
            FileName = game.ExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(game.ExecutablePath) ?? Environment.CurrentDirectory,
            UseShellExecute = true
        }) ?? throw new InvalidOperationException("O Windows não iniciou o jogo.");
    }
}
