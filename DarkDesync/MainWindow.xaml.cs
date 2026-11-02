using System.Windows;
using DarkDesync.Core;
using DarkDesync.Services;
using DarkDesync.ViewModels;
using Microsoft.Extensions.Logging;

namespace DarkDesync;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly HotkeyService _hotkeyService;
    private readonly ILoggerFactory _loggerFactory;

    public MainWindow() : this(null) { }

    internal MainWindow(SettingsStore? settingsStore)
    {
        InitializeComponent();

        // Setup logger factory
        var loggerFactory = _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        var processScannerLogger = loggerFactory.CreateLogger<ProcessNetworkScanner>();
        var filterBuilderLogger = loggerFactory.CreateLogger<FilterBuilder>();
        var flowTrackerLogger = loggerFactory.CreateLogger<FlowTracker>();
        var packetEngineLogger = loggerFactory.CreateLogger<PacketEngine>();
        var processServiceLogger = loggerFactory.CreateLogger<ProcessService>();
        var hotkeyServiceLogger = loggerFactory.CreateLogger<HotkeyService>();
        var desyncControllerLogger = loggerFactory.CreateLogger<DesyncController>();

        // Initialize core dependencies
        var stateMachine = new DesyncStateMachine();
        var scanner = new ProcessNetworkScanner(processScannerLogger);
        var filterBuilder = new FilterBuilder(filterBuilderLogger);
        var flowTracker = new FlowTracker(flowTrackerLogger);
        var packetEngine = new PacketEngine(packetEngineLogger, scanner, filterBuilder, flowTracker, stateMachine);

        _hotkeyService = new HotkeyService(hotkeyServiceLogger);
        var processService = new ProcessService(processServiceLogger);
        var controller = new DesyncController(desyncControllerLogger, stateMachine, packetEngine);

        _viewModel = new MainViewModel(processService, _hotkeyService, controller, packetEngine, settingsStore);
        if (System.IO.File.Exists(_viewModel.SettingsPath)) _viewModel.LoadSettings();
        DataContext = _viewModel;

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void StartStop_PreviewKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // Space is the global pulse key, never local button/radio activation.
        // Applies across the panel, not only STOP: focus cannot change a preset,
        // toggle manual mode or fire a duplicate manual pulse on key-up.
        // Enter/Tab/arrow navigation remain standard; the OS hook is pass-through.
        if (e.Key == System.Windows.Input.Key.Space && _viewModel.SelectedHotkeyName == "Espaço")
            e.Handled = true;
    }

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Install global keyboard hook
        try
        {
            _hotkeyService.Install();
        }
        catch (Exception ex)
        {
            _viewModel.AddLog($"[AVISO] Não foi possível registrar hook de teclado: {ex.Message}");
        }
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Ensure graceful traffic restore and driver handle release
        try
        {
            _hotkeyService.Uninstall();
            _viewModel.Dispose();
        }
        catch { }
        finally { _loggerFactory.Dispose(); }
    }
}
