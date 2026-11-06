using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using UltimatePoKeSync.App.Services;
using UltimatePoKeSync.App.ViewModels;

namespace UltimatePoKeSync.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    private double? _normalWidth;
    private double? _normalHeight;
    private PixelPoint? _normalPosition;

    public MainWindow()
    {
        // The script has to be on disk before the setup screen points at it.
        SetupGuide.EnsureScript();
        _viewModel = new MainWindowViewModel();

        AvaloniaXamlLoader.Load(this);
        DataContext = _viewModel;

        RestoreLayout(AppSettings.Load());
        PositionChanged += (_, _) => RememberNormalBounds();

        // Both copy buttons are optional: the header lost its own when the shell was
        // rebuilt, and a screen that has not been rebuilt yet may not carry one either.
        foreach (string name in new[] { "CopyPathButton", "CopyHeaderPathButton" })
        {
            if (this.FindControl<Button>(name) is { } button)
            {
                button.Click += CopyScriptPath;
            }
        }
        _viewModel.Start();
    }

    /// <summary>
    /// The red bar is the window's own title bar, so dragging it has to move the window:
    /// the system one that would normally do that is gone.
    /// </summary>
    private void DragWindow(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void CloseWindow(object? sender, RoutedEventArgs e) => Close();

    private void MinimiseWindow(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void ZoomWindow(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    /// <summary>
    /// The path is the one thing a user has to move by hand into mGBA's file dialog, so
    /// it is a button rather than something to retype.
    /// </summary>
    private async void CopyScriptPath(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(_viewModel.ScriptPath);
            if (sender is Button button)
            {
                button.Content = "Copied";
            }
        }
    }

    /// <summary>
    /// Where the window was last time. A stored size that is too small to use is ignored,
    /// because a window saved at 40×20 by a crash or a hand-edited file would reopen unusable, but
    /// a stored maximised state is honoured either way. See D-038.
    /// </summary>
    private void RestoreLayout(AppSettings settings)
    {
        if (settings.HasUsableSize)
        {
            _normalWidth = Width = settings.WindowWidth!.Value;
            _normalHeight = Height = settings.WindowHeight!.Value;

            if (settings.HasPosition)
            {
                _normalPosition = new PixelPoint(settings.WindowX!.Value, settings.WindowY!.Value);
                WindowStartupLocation = WindowStartupLocation.Manual;
                Position = _normalPosition.Value;
            }
        }

        if (settings.WindowMaximised)
        {
            WindowState = WindowState.Maximized;
        }
    }

    /// <summary>
    /// The size and position from before it was maximised, kept as the window moves. A
    /// maximised window reports the screen's bounds, and saving those makes it reopen
    /// filling the screen with no way back to the size the player chose.
    /// </summary>
    private void RememberNormalBounds()
    {
        if (WindowState != WindowState.Normal)
        {
            return;
        }

        _normalWidth = Width;
        _normalHeight = Height;
        _normalPosition = Position;
    }

    protected override void OnResized(WindowResizedEventArgs e)
    {
        base.OnResized(e);
        RememberNormalBounds();
    }

    private void SaveLayout() =>
        new AppSettings
        {
            WindowWidth = _normalWidth,
            WindowHeight = _normalHeight,
            WindowX = _normalPosition?.X,
            WindowY = _normalPosition?.Y,
            WindowMaximised = WindowState == WindowState.Maximized,
            CompetitiveProfile = _viewModel.IsCompetitive,
        }.Save();

    /// <remarks>
    /// The emulators have to be told to let go before the process can exit. An async void
    /// here returned at once, and the application could finish before the detach reached the
    /// emulator, leaving its debugger port jammed for the next session. So the disposal is
    /// waited for, but off the UI thread, so anything it needs from the dispatcher can still
    /// run, and bounded, so a read stuck on a dead socket cannot keep the process alive. See
    /// D-074.
    /// </remarks>
    protected override void OnClosed(EventArgs e)
    {
        SaveLayout();
        base.OnClosed(e);

        try
        {
            Task.Run(() => _viewModel.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
            // Shutting down regardless; whatever failed has nowhere left to be reported.
        }
    }
}
