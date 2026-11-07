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
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Media.Animation;
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

    private void WindowTitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        if (e.ClickCount == 2)
        {
            ToggleMaximizeRestore();
            return;
        }

        if (WindowState == WindowState.Maximized)
        {
            var point = e.GetPosition(this);
            var screenPoint = PointToScreen(point);
            WindowState = WindowState.Normal;
            Left = screenPoint.X - (RestoreBounds.Width * (point.X / ActualWidth));
            Top = screenPoint.Y - 12;
        }

        DragMove();
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeWindow_Click(object sender, RoutedEventArgs e)
        => ToggleMaximizeRestore();

    private void CloseWindow_Click(object sender, RoutedEventArgs e)
        => Close();

    private void ToggleMaximizeRestore()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
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
        Opacity = 0;
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });

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
            if (_selectedGame == game) UpdateFeaturedGame(game);
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
            if (_selectedGame == game) UpdateFeaturedGame(game);
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
            .Where(item => item.Bounds.HasValue && item.Bounds.Value.Width > 0 && item.Bounds.Value.Height > 0)
            .ToList();

        if (candidates.Count == 0) { FocusFirstGameAction(); return; }
        var origin = GetBounds(current);
        if (!origin.HasValue) { FocusFirstGameAction(); return; }

        var ox = origin.Value.Left + origin.Value.Width / 2;
        var oy = origin.Value.Top + origin.Value.Height / 2;
        var ranked = candidates.Select(item =>
        {
            var bounds = item.Bounds!.Value;
            var x = bounds.Left + bounds.Width / 2;
            var y = bounds.Top + bounds.Height / 2;
            var dx = x - ox;
            var dy = y - oy;
            var primary = horizontal != 0 ? dx * horizontal : dy * vertical;
            var secondary = horizontal != 0 ? Math.Abs(dy) : Math.Abs(dx);
            var score = primary > 2
                ? primary + secondary * 1.35 + secondary * secondary / Math.Max(primary, 1) * 0.15
                : double.MaxValue;
            return new { item.Element, Score = score, X = x, Y = y };
        }).ToList();

        var next = ranked.Where(item => item.Score < double.MaxValue)
                         .OrderBy(item => item.Score)
                         .FirstOrDefault()?.Element;

        // If there is no control in that direction, wrap to the opposite edge
        // instead of silently losing controller navigation.
        if (next is null)
        {
            next = ranked.OrderBy(item =>
                horizontal != 0
                    ? Math.Abs(item.Y - oy) * 2 + (horizontal > 0 ? item.X : -item.X)
                    : Math.Abs(item.X - ox) * 2 + (vertical > 0 ? item.Y : -item.Y))
                .FirstOrDefault()?.Element;
        }

        if (next is null || !next.Focus())
        {
            FocusFirstGameAction();
            return;
        }

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
            if (child is Control control && control.Focusable && KeyboardNavigation.GetIsTabStop(control))
                yield return control;
            else if (child is FrameworkElement element && element.Focusable && KeyboardNavigation.GetIsTabStop(element))
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
        var entries = Games.OrderByDescending(g => g.HoursPlayed).ToList();
        var total = entries.Sum(g => g.HoursPlayed);

        var window = new Window
        {
            Title = "Estatísticas da biblioteca",
            Owner = this,
            Width = 880,
            Height = 610,
            MinWidth = 760,
            MinHeight = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(16, 19, 28)),
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Segoe UI")
        };

        var root = new Grid { Margin = new Thickness(28) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 22) };
        heading.Children.Add(new TextBlock
        {
            Text = "ESTATÍSTICAS",
            Foreground = new SolidColorBrush(Color.FromRgb(174, 145, 255)),
            FontSize = 10,
            FontWeight = FontWeights.Bold
        });
        heading.Children.Add(new TextBlock
        {
            Text = "Tempo por jogo",
            Foreground = Brushes.White,
            FontSize = 27,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 5, 0, 3)
        });
        heading.Children.Add(new TextBlock
        {
            Text = $"Total acumulado: {FormatPlaytime(total)}  •  {entries.Count} jogo(s)",
            Foreground = new SolidColorBrush(Color.FromRgb(145, 155, 174)),
            FontSize = 12
        });
        Grid.SetRow(heading, 0);
        root.Children.Add(heading);

        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(390) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(content, 1);
        root.Children.Add(content);

        var chartHost = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var canvas = new Canvas { Width = 340, Height = 340 };
        chartHost.Children.Add(canvas);
        Grid.SetColumn(chartHost, 0);
        content.Children.Add(chartHost);

        var legendScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(20, 0, 0, 0) };
        var legend = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        legendScroll.Content = legend;
        Grid.SetColumn(legendScroll, 1);
        content.Children.Add(legendScroll);

        if (entries.Count == 0 || total <= 0)
        {
            legend.Children.Add(new TextBlock
            {
                Text = entries.Count == 0 ? "Adicione jogos para visualizar suas estatísticas." : "Ainda não há tempo de jogo registrado.",
                Foreground = new SolidColorBrush(Color.FromRgb(160, 169, 187)),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 12, 0, 0)
            });
            canvas.Children.Add(new Ellipse
            {
                Width = 270,
                Height = 270,
                Fill = new SolidColorBrush(Color.FromRgb(37, 43, 57))
            });
            Canvas.SetLeft(canvas.Children[0], 35);
            Canvas.SetTop(canvas.Children[0], 35);
        }
        else
        {
            Color[] palette =
            {
                Color.FromRgb(143, 105, 235), Color.FromRgb(74, 151, 224),
                Color.FromRgb(68, 190, 157), Color.FromRgb(235, 169, 83),
                Color.FromRgb(224, 104, 137), Color.FromRgb(117, 129, 220),
                Color.FromRgb(176, 117, 210), Color.FromRgb(126, 167, 111)
            };

            var angle = -90d;
            var index = 0;
            foreach (var game in entries.Where(g => g.HoursPlayed > 0))
            {
                var sweep = game.HoursPlayed / total * 360d;
                var start = angle * Math.PI / 180d;
                var end = (angle + sweep) * Math.PI / 180d;
                var center = new Point(170, 170);
                var radius = 145d;
                var startPoint = new Point(center.X + radius * Math.Cos(start), center.Y + radius * Math.Sin(start));
                var endPoint = new Point(center.X + radius * Math.Cos(end), center.Y + radius * Math.Sin(end));
                var figure = new PathFigure { StartPoint = center, IsClosed = true };
                figure.Segments.Add(new LineSegment(startPoint, true));
                figure.Segments.Add(new ArcSegment(endPoint, new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise, true));
                var geometry = new PathGeometry();
                geometry.Figures.Add(figure);
                var color = palette[index % palette.Length];
                canvas.Children.Add(new System.Windows.Shapes.Path
                {
                    Data = geometry,
                    Fill = new SolidColorBrush(color),
                    Stroke = new SolidColorBrush(Color.FromRgb(16, 19, 28)),
                    StrokeThickness = 2,
                    ToolTip = $"{game.Name}: {FormatPlaytime(game.HoursPlayed)}"
                });

                var row = new Grid { Margin = new Thickness(0, 0, 0, 15) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(15) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var swatch = new Border
                {
                    Width = 10,
                    Height = 10,
                    CornerRadius = new CornerRadius(3),
                    Background = new SolidColorBrush(color),
                    VerticalAlignment = VerticalAlignment.Center
                };
                row.Children.Add(swatch);
                var name = new TextBlock
                {
                    Text = game.Name,
                    Foreground = new SolidColorBrush(Color.FromRgb(232, 235, 244)),
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = game.Name
                };
                Grid.SetColumn(name, 1);
                row.Children.Add(name);
                var value = new TextBlock
                {
                    Text = $"{FormatPlaytime(game.HoursPlayed)}  ({game.HoursPlayed / total:P0})",
                    Foreground = new SolidColorBrush(Color.FromRgb(163, 173, 192)),
                    FontSize = 11,
                    Margin = new Thickness(12, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(value, 2);
                row.Children.Add(value);
                legend.Children.Add(row);

                angle += sweep;
                index++;
            }
        }

        var footer = new TextBlock
        {
            Text = entries.Count == 0 ? "O tempo de jogo será contabilizado enquanto os jogos forem executados pela biblioteca." :
                $"Jogo mais utilizado: {(entries.FirstOrDefault()?.Name ?? "—")}  •  O gráfico considera o tempo registrado por jogo.",
            Foreground = new SolidColorBrush(Color.FromRgb(119, 131, 152)),
            FontSize = 10,
            Margin = new Thickness(0, 18, 0, 0)
        };
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        window.Content = root;
        window.ShowDialog();
    }

    private static string FormatPlaytime(double hours)
        => hours < 1 ? $"{hours * 60:F0} min" : $"{hours:F1} h";

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
