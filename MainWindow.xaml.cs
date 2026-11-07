using System.IO;
using System.Collections.ObjectModel;
using System.Diagnostics;
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
    private Process? _activeProcess;
    private Game? _activeGame;
    private Stopwatch? _session;
    private TimeSpan _savedElapsed;

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
        foreach (var game in await _storage.LoadAsync()) Games.Add(game);
        UpdateStatistics();
        StatusText.Text = "Biblioteca local carregada. Nenhuma conta é necessária.";
    }

    private async void AddGame_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Selecione o executável do jogo", Filter = "Executáveis (*.exe)|*.exe", CheckFileExists = true };
        if (dialog.ShowDialog() != true) return;
        if (Games.Any(g => string.Equals(g.ExecutablePath, dialog.FileName, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("Este jogo já está na biblioteca."); return;
        }
        var game = new Game { Name = Path.GetFileNameWithoutExtension(dialog.FileName), ExecutablePath = dialog.FileName };
        Games.Add(game);
        await SaveAsync();
        UpdateStatistics();
        StatusText.Text = $"Jogo adicionado: {game.Name}";
        _ = FindCoverAsync(game);
    }

    private async Task FindCoverAsync(Game game)
    {
        try
        {
            var path = await _covers.DownloadAsync(game.Name, _storage.CoversFolder);
            if (!string.IsNullOrWhiteSpace(path))
            {
                game.CoverPath = path;
                await SaveAsync();
                StatusText.Text = $"Capa encontrada: {game.Name}";
            }
            else StatusText.Text = "Busca de capas opcional: configure STEAMGRIDDB_API_KEY para habilitar.";
        }
        catch (Exception ex) { StatusText.Text = $"Falha ao buscar capa: {ex.Message}"; }
    }

    private void PlayGame_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Game game }) return;
        if (_activeProcess is { HasExited: false })
        {
            MessageBox.Show("Já existe um jogo sendo monitorado. Feche-o antes de iniciar outro."); return;
        }
        try
        {
            _activeProcess?.Dispose();
            _activeProcess = _launcher.Start(game);
            _activeGame = game;
            _session = Stopwatch.StartNew();
            _savedElapsed = TimeSpan.Zero;
            _timer.Start();
            NowPlayingText.Text = game.Name;
            StatusText.Text = $"Monitorando: {game.Name}";
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Erro ao iniciar jogo"); }
    }

    private async void OnTimerTick(object? sender, EventArgs e)
    {
        if (_activeProcess == null || _activeGame == null || _session == null) return;
        try
        {
            if (_activeProcess.HasExited) { await FinishSessionAsync(); return; }
            var elapsed = _session.Elapsed;
            var delta = elapsed - _savedElapsed;
            if (delta > TimeSpan.Zero)
            {
                _activeGame.HoursPlayed += delta.TotalHours;
                _savedElapsed = elapsed;
                UpdateStatistics();
            }
            if ((int)elapsed.TotalSeconds % 30 == 0) await SaveAsync();
        }
        catch (InvalidOperationException) { await FinishSessionAsync(); }
    }

    private async Task FinishSessionAsync()
    {
        _timer.Stop();
        if (_activeGame != null && _session != null)
        {
            var delta = _session.Elapsed - _savedElapsed;
            if (delta > TimeSpan.Zero) _activeGame.HoursPlayed += delta.TotalHours;
        }
        _session?.Stop();
        _session = null;
        _activeGame = null;
        _activeProcess?.Dispose();
        _activeProcess = null;
        NowPlayingText.Text = "Nenhum";
        UpdateStatistics();
        await SaveAsync();
        StatusText.Text = "Sessão finalizada.";
    }

    private async Task SaveAsync() => await _storage.SaveAsync(Games);

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
        MessageBox.Show($"Jogos cadastrados: {Games.Count}\nTempo total: {total:F2} horas\n" +
            (most == null ? "Nenhum jogo cadastrado." : $"Mais jogado: {most.Name} ({most.HoursPlayed:F2} h)"),
            "Estatísticas", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _timer.Stop();
        if (_activeGame != null && _session != null)
        {
            var delta = _session.Elapsed - _savedElapsed;
            if (delta > TimeSpan.Zero) _activeGame.HoursPlayed += delta.TotalHours;
        }
        _session?.Stop();
        try { await SaveAsync(); }
        catch (Exception ex) { MessageBox.Show($"Falha ao salvar: {ex.Message}"); }
    }
}
