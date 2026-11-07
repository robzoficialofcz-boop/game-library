using System;
using System.Diagnostics;
using System.IO;
using GameLibrary.Models;

namespace GameLibrary.Services;

public sealed class GameLauncherService
{
    public Process Start(Game game)
    {
        if (string.IsNullOrWhiteSpace(game.ExecutablePath) || !File.Exists(game.ExecutablePath))
            throw new FileNotFoundException("O executável não foi encontrado. Verifique o caminho do jogo.", game.ExecutablePath);

        var directory = Path.GetDirectoryName(game.ExecutablePath);
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = game.ExecutablePath,
            WorkingDirectory = string.IsNullOrWhiteSpace(directory) ? Environment.CurrentDirectory : directory,
            UseShellExecute = true
        });

        return process ?? throw new InvalidOperationException("O Windows não iniciou o jogo.");
    }
}
