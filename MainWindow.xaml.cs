using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
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
    private readonly DispatcherTimer _controllerTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly GamepadService _gamepad = new();
    private ushort _previousGamepadButtons;
    private int _previousStickDirection;
    private int _previousHorizontalDirection;
    private DateTime _lastNavigationAt = DateTime.MinValue;
    private bool _controllerConnected;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private Process? _activeProcess;
    private Game? _activeGame;
    private Stopwatch? _session;
    private TimeSpan _savedElapsed;
    private bool _isClosing;
    private bool _isFullscreen;
    private Game? _selectedGame;
    private WindowStyle _previousWindowStyle;
    private ResizeMode _previousResizeMode;
    private WindowState _previousWindowState;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        Loaded += OnLoaded;
        Closing += OnClosing;
        _timer.Tick += OnTimerTick;
        _controllerTimer.Tick += OnControllerTimerTick;
        _controllerTimer.Start();
    }

    private void ToggleFullscreen_Click(object sender, RoutedEventArgs e) => SetFullscreen(!_isFullscreen);

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            SetFullscreen(!_isFullscreen);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _isFullscreen)
        {
            SetFullscreen(false);
            e.Handled = true;
        }
    }

    private void SetFullscreen(bool enabled)
    {
        if (enabled == _isFullscreen) return;

        if (enabled)
        {
            _previousWindowStyle = WindowStyle;
            _previousResizeMode = ResizeMode;
            _previousWindowState = WindowState;
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            FullscreenButton.Content = "SAIR DA TELA CHEIA";
        }
        else
        {
            WindowState = WindowState.Normal;
            WindowStyle = _previousWindowStyle;
            ResizeMode = _previousResizeMode;
            WindowState = _previousWindowState;
            FullscreenButton.Content = "TELA CHEIA";
        }

        _isFullscreen = enabled;
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
            UpdateFeaturedGame(Games.OrderByDescending(g => g.LastPlayedUtc).FirstOrDefault());
            StatusText.Text = $"Biblioteca local carregada: {Games.Count} jogo(s).";
            Dispatcher.BeginInvoke(new Action(() => MoveFocus(new TraversalRequest(FocusNavigationDirection.First))));
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
        UpdateFeaturedGame(game);
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
            if (!Games.Contains(game) || !string.IsNullOrWhiteSpace(game.CoverPath))
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

    private async void ChangeCover_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Game game }) return;

        var dialog = new OpenFileDialog
        {
            Title = $"Escolha a capa de {game.Name}",
            Filter = "Imagens (*.png;*.jpg;*.jpeg;*.webp)|*.png;*.jpg;*.jpeg;*.webp|Todos os arquivos (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var extension = Path.GetExtension(dialog.FileName).ToLowerInvariant();
            if (extension is not (".png" or ".jpg" or ".jpeg" or ".webp"))
            {
                MessageBox.Show(this, "Formato não suportado. Escolha PNG, JPG, JPEG ou WEBP.",
                    "Capa inválida", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Directory.CreateDirectory(_storage.CoversFolder);
            var destination = Path.Combine(_storage.CoversFolder, $"{game.Id:N}_{Guid.NewGuid():N}{extension}");
            await using (var source = new FileStream(dialog.FileName, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
            await using (var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                await source.CopyToAsync(target);
            }

            game.CoverPath = destination;
            await PersistSafelyAsync();
            StatusText.Text = $"Capa atualizada: {game.Name}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Não foi possível copiar a imagem.\n{ex.Message}",
                "Erro ao trocar capa", MessageBoxButton.OK, MessageBoxImage.Error);
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
        if (_selectedGame == game) UpdateFeaturedGame(Games.FirstOrDefault());
        await PersistSafelyAsync();
        UpdateStatistics();
        ApplySearch();
        StatusText.Text = $"Jogo removido: {game.Name}";
    }

    private void OnControllerTimerTick(object? sender, EventArgs e)
    {
        if (!_gamepad.TryGetState(out var buttons, out var stickX, out var stickY))
        {
            if (_controllerConnected) ControllerStatusText.Text = "CONTROLE: desconectado";
            _controllerConnected = false;
            _previousGamepadButtons = 0;
            _previousStickDirection = 0;
            _previousHorizontalDirection = 0;
            return;
        }

        if (!_controllerConnected)
        {
            ControllerStatusText.Text = "CONTROLE XINPUT: conectado | DIRECIONAL: mover • A: confirmar • B: voltar • START: adicionar • Y: estatísticas • X: tela cheia";
            FocusFirstGameAction();
        }
        _controllerConnected = true;

        const ushort dpadUp = 0x0001, dpadDown = 0x0002, dpadLeft = 0x0004, dpadRight = 0x0008;
        const ushort buttonA = 0x1000, buttonB = 0x2000, buttonX = 0x4000, buttonY = 0x8000;
        const ushort buttonStart = 0x0010, buttonBack = 0x0020;
        const ushort leftShoulder = 0x0100, rightShoulder = 0x0200;

        var horizontal = (buttons & dpadLeft) != 0 ? -1 : (buttons & dpadRight) != 0 ? 1 :
            stickX < -16000 ? -1 : stickX > 16000 ? 1 : 0;
        var vertical = (buttons & dpadUp) != 0 ? -1 : (buttons & dpadDown) != 0 ? 1 :
            stickY > 16000 ? -1 : stickY < -16000 ? 1 : 0;

        var now = DateTime.UtcNow;
        var navigationReady = (now - _lastNavigationAt).TotalMilliseconds >= 180;
        if (navigationReady && (horizontal != 0 || vertical != 0))
        {
            NavigateSpatially(horizontal, vertical);
            _lastNavigationAt = now;
        }

        if (Pressed(buttons, buttonA)) ActivateFocusedControl();
        if (Pressed(buttons, buttonB) || Pressed(buttons, buttonBack)) GoBack();
        if (Pressed(buttons, buttonStart)) AddGame_Click(this, new RoutedEventArgs());
        if (Pressed(buttons, buttonY)) Statistics_Click(this, new RoutedEventArgs());
        if (Pressed(buttons, buttonX)) SetFullscreen(!_isFullscreen);
        if (Pressed(buttons, leftShoulder)) FocusFirstGameAction();
        if (Pressed(buttons, rightShoulder)) FocusSearchOrAddButton();

        _previousGamepadButtons = buttons;
        _previousStickDirection = vertical;
        _previousHorizontalDirection = horizontal;
    }

    private bool Pressed(ushort current, ushort mask) =>
        (current & mask) != 0 && (_previousGamepadButtons & mask) == 0;

    private void NavigateSpatially(int horizontal, int vertical)
    {
        var current = Keyboard.FocusedElement as FrameworkElement;
        if (current is null || !IsDescendantOf(current, this))
        {
            FocusFirstGameAction();
            return;
        }

        var candidates = GetFocusableElements(this)
            .Where(element => element != current && element.IsVisible && element.IsEnabled &&
                              element.Focusable && KeyboardNavigation.GetIsTabStop(element))
            .Select(element => new { Element = element, Bounds = GetBounds(element) })
            .Where(item => item.Bounds.HasValue)
            .ToList();

        var origin = GetBounds(current);
        if (!origin.HasValue) return;
        var ox = origin.Value.Left + origin.Value.Width / 2;
        var oy = origin.Value.Top + origin.Value.Height / 2;

        var next = candidates
            .Select(item =>
            {
                var x = item.Bounds!.Value.Left + item.Bounds.Value.Width / 2;
                var y = item.Bounds.Value.Top + item.Bounds.Value.Height / 2;
                var dx = x - ox;
                var dy = y - oy;
                var primary = horizontal != 0 ? dx * horizontal : dy * vertical;
                var secondary = horizontal != 0 ? Math.Abs(dy) : Math.Abs(dx);
                var cross = horizontal != 0 ? Math.Abs(dx) : Math.Abs(dy);
                var score = primary > 1 ? primary + secondary * 1.8 + cross * 0.15 : double.MaxValue;
                return new { item.Element, Score = score };
            })
            .Where(item => item.Score < double.MaxValue)
            .OrderBy(item => item.Score)
            .FirstOrDefault()?.Element;

        if (next is null) return;
        next.Focus();
        next.BringIntoView();
    }

    private Rect? GetBounds(FrameworkElement element)
    {
        try
        {
            if (!element.IsLoaded) return null;
            return element.TransformToAncestor(this).TransformBounds(new Rect(new Point(0, 0), element.RenderSize));
        }
        catch (InvalidOperationException) { return null; }
    }

    private static System.Collections.Generic.IEnumerable<FrameworkElement> GetFocusableElements(DependencyObject parent)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement element && element.Focusable && KeyboardNavigation.GetIsTabStop(element))
                yield return element;
            foreach (var descendant in GetFocusableElements(child))
                yield return descendant;
        }
    }

    private static bool IsDescendantOf(DependencyObject child, DependencyObject parent)
    {
        var current = child;
        while (current is not null)
        {
            if (current == parent) return true;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private void FocusFirstGameAction()
    {
        var first = GetFocusableElements(GamesItems).FirstOrDefault(element => element is Button);
        if (first is not null) { first.Focus(); first.BringIntoView(); }
        else FocusSearchOrAddButton();
    }

    private void FocusSearchOrAddButton()
    {
        if (SearchBox.IsVisible) SearchBox.Focus();
        else FullscreenButton.Focus();
    }

    private void GoBack()
    {
        if (Keyboard.FocusedElement is TextBox textBox)
        {
            Keyboard.ClearFocus();
            FocusFirstGameAction();
            return;
        }
        if (_isFullscreen)
        {
            SetFullscreen(false);
            return;
        }
        FocusFirstGameAction();
    }

    private static void ActivateFocusedControl()
    {
        if (Keyboard.FocusedElement is Button button && button.IsEnabled)
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        else if (Keyboard.FocusedElement is TextBox textBox)
            textBox.Focus();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplySearch();

    private void GameCard_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is Game game)
            UpdateFeaturedGame(game);
    }

    private void UpdateFeaturedGame(Game? game)
    {
        _selectedGame = game;
        HeroTitle.Text = game?.Name ?? "Sua biblioteca está vazia";
        HeroPlaytime.Text = game is null ? "Adicione um jogo para começar" : game.PlaytimeText;
        HeroPath.Text = game?.ExecutablePath ?? "Seus jogos aparecerão aqui.";
        HeroPlayButton.IsEnabled = game is not null;
        HeroCover.Source = null;

        if (!string.IsNullOrWhiteSpace(game?.CoverPath) && File.Exists(game.CoverPath))
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(game.CoverPath, UriKind.Absolute);
                image.EndInit();
                image.Freeze();
                HeroCover.Source = image;
            }
            catch (Exception) { HeroCover.Source = null; }
        }
    }

    private void PlayFeatured_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedGame is null) return;
        PlayGame_Click(new Button { Tag = _selectedGame }, e);
    }

    private void ChangeFeaturedCover_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedGame is null) return;
        ChangeCover_Click(new Button { Tag = _selectedGame }, e);
    }

    private void FocusFirstGameAction_Click(object sender, RoutedEventArgs e) => FocusFirstGameAction();

    private void ApplySearch()
    {
        if (GamesItems is null) return;
        var query = SearchBox?.Text?.Trim() ?? "";
        var visibleGames = string.IsNullOrEmpty(query)
            ? Games.ToList()
            : Games.Where(g => g.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        GamesItems.ItemsSource = visibleGames;
        if (_selectedGame is null || !visibleGames.Contains(_selectedGame))
            UpdateFeaturedGame(visibleGames.FirstOrDefault());
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
        _controllerTimer.Stop();

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
