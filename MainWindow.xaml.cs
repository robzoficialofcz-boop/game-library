using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GameLibrary.Models;
using GameLibrary.Services;
using Microsoft.Win32;

namespace GameLibrary;

public partial class MainWindow : Window
{
    public ObservableCollection<Game> Games { get; } = new();
    private readonly GameStorageService _storage = new();
    private readonly GameLauncherService _launcher = new();
    private readonly CoverService _covers = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private Process? _activeProcess;
    private Game? _activeGame;
    private Stopwatch? _session;
    private TimeSpan _savedElapsed;
    private bool _isClosing;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        Loaded += OnLoaded;
        Closing += OnClosing;
        _timer.Tick += OnTimerTick;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            foreach (var game in await _storage.LoadAsync())
            {
                if (game.Id == Guid.Empty) game.Id = Guid.NewGuid();
                if (!string.IsNullOrWhiteSpace(game.Name) && !string.IsNullOrWhiteSpace(game.ExecutablePath))
                    Games.Add(game);
            }
            UpdateStatistics();
            ApplySearch();
            StatusText.Text = $"Biblioteca local carregada: {Games.Count} jogo(s).";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Não foi possível carregar a biblioteca.";
            MessageBox.Show(this, ex.Message, "Erro ao carregar biblioteca", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void AddGame_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Selecione o executável do jogo",
            Filter = "Executáveis (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        if (Games.Any(g => string.Equals(g.ExecutablePath, dialog.FileName, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "Este jogo já está na biblioteca.", "Game Library", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var game = new Game
        {
            Name = Path.GetFileNameWithoutExtension(dialog.FileName),
            ExecutablePath = dialog.FileName
        };
        Games.Add(game);
        await PersistSafelyAsync();
        UpdateStatistics();
        ApplySearch();
        StatusText.Text = $"Jogo adicionado: {game.Name}";
        _ = FindCoverAsync(game);
    }

    private async Task FindCoverAsync(Game game)
    {
        try
        {
            var path = await _covers.DownloadAsync(game.Name, _storage.CoversFolder);
            if (path is null) return;
            if (!Games.Contains(game))
            {
                try { File.Delete(path); } catch (IOException) { }
                return;
            }
            game.CoverPath = path;
            await PersistSafelyAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Não foi possível obter a capa de {game.Name}: {ex.Message}";
        }
    }

    private async void RemoveGame_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Game game }) return;
        var answer = MessageBox.Show(this,
            $"Remover {game.Name} da biblioteca? Os arquivos do jogo não serão apagados.",
            "Confirmar remoção", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        if (_activeGame == game)
        {
            MessageBox.Show(this, "Feche o jogo antes de removê-lo da biblioteca.");
            return;
        }
        Games.Remove(game);
        await PersistSafelyAsync();
        UpdateStatistics();
        ApplySearch();
        StatusText.Text = $"Jogo removido: {game.Name}";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplySearch();

    private void ApplySearch()
    {
        if (GamesItems is null) return;
        var query = SearchBox?.Text?.Trim() ?? "";
        GamesItems.ItemsSource = string.IsNullOrEmpty(query)
            ? Games
            : Games.Where(g => g.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void PlayGame_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Game game }) return;

        if (_activeProcess is not null && IsProcessRunning(_activeProcess))
        {
            MessageBox.Show(this, "Já existe um jogo sendo monitorado. Feche-o antes de iniciar outro.");
            return;
        }

        if (_session is not null) _ = FinishSessionAsync();

        try
        {
            _activeProcess?.Dispose();
            _activeProcess = _launcher.Start(game);
            _activeGame = game;
            _session = Stopwatch.StartNew();
            _savedElapsed = TimeSpan.Zero;
            game.LastPlayedUtc = DateTime.UtcNow;
            _timer.Start();
            NowPlayingText.Text = game.Name;
            StatusText.Text = $"Monitorando: {game.Name}";
            _ = PersistSafelyAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Erro ao iniciar jogo", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static bool IsProcessRunning(Process process)
    {
        try { return !process.HasExited; }
        catch (System.ComponentModel.Win32Exception) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private async void OnTimerTick(object? sender, EventArgs e)
    {
        if (_activeProcess is null || _activeGame is null || _session is null) return;

        if (!IsProcessRunning(_activeProcess))
        {
            await FinishSessionAsync();
            return;
        }

        var elapsed = _session.Elapsed;
        var delta = elapsed - _savedElapsed;
        if (delta > TimeSpan.Zero)
        {
            _activeGame.HoursPlayed += delta.TotalHours;
            _savedElapsed = elapsed;
            UpdateStatistics();
        }

        if ((int)elapsed.TotalSeconds % 30 == 0)
            await PersistSafelyAsync();
    }

    private async Task FinishSessionAsync()
    {
        if (_session is null) return;
        _timer.Stop();

        var delta = _session.Elapsed - _savedElapsed;
        if (_activeGame is not null && delta > TimeSpan.Zero)
            _activeGame.HoursPlayed += delta.TotalHours;

        _session.Stop();
        _session = null;
        _activeGame = null;
        _activeProcess?.Dispose();
        _activeProcess = null;
        NowPlayingText.Text = "Nenhum";
        UpdateStatistics();
        await PersistSafelyAsync();
        StatusText.Text = "Sessão monitorada finalizada.";
    }

    private async Task PersistSafelyAsync()
    {
        await _saveLock.WaitAsync();
        try { await _storage.SaveAsync(Games); }
        catch (Exception ex) { StatusText.Text = $"Erro ao salvar biblioteca: {ex.Message}"; }
        finally { _saveLock.Release(); }
    }

    private void UpdateStatistics()
    {
        GameCountText.Text = Games.Count.ToString();
        var total = Games.Sum(g => g.HoursPlayed);
        TotalTimeText.Text = total < 1 ? $"{total * 60:F0} min" : $"{total:F1} h";
    }

    private void Statistics_Click(object sender, RoutedEventArgs e)
    {
        var total = Games.Sum(g => g.HoursPlayed);
        var most = Games.OrderByDescending(g => g.HoursPlayed).FirstOrDefault();
        MessageBox.Show(this,
            $"Jogos cadastrados: {Games.Count}\nTempo total: {total:F2} horas\n" +
            (most is null ? "Nenhum jogo cadastrado." : $"Mais jogado: {most.Name} ({most.HoursPlayed:F2} h)"),
            "Estatísticas", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isClosing) return;
        _isClosing = true;
        _timer.Stop();

        if (_session is not null)
        {
            var delta = _session.Elapsed - _savedElapsed;
            if (_activeGame is not null && delta > TimeSpan.Zero)
                _activeGame.HoursPlayed += delta.TotalHours;
            _session.Stop();
            _session = null;
        }

        await PersistSafelyAsync();
        _activeProcess?.Dispose();
        _activeProcess = null;
    }
}
