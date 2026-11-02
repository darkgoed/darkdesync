using System.Net;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DarkDesync.Core;

namespace DarkDesync;

// Modal lab: no interaction with DiagnosticMode, settings, global START or Space pulse.
public sealed class NetworkExperimentsWindow : Window
{
    private readonly ComboBox _selector = new() { Name = "ExperimentSelector", MinWidth = 360, Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBlock _description = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
    private readonly TextBox _address = new() { Name = "ReflectorAddress", Text = "127.0.0.1", Width = 150 };
    private readonly TextBox _port = new() { Name = "ReflectorPort", Text = "39435", Width = 65 };
    private readonly PasswordBox _token = new() { Name = "ReflectorToken", Width = 150 };
    private readonly CheckBox _owned = new() { Name = "OwnedReflectorConsent", Content = "Confirmo gateway e reflector sob meu controle (somente NAT)", Margin = new Thickness(0, 8, 0, 8) };
    private readonly Button _run = new() { Name = "RunExperiment", Content = "Rodar ensaio próprio", Padding = new Thickness(12, 5, 12, 5) };
    private readonly Button _stop = new() { Name = "StopExperiment", Content = "STOP ensaio", IsEnabled = false, Margin = new Thickness(10, 0, 0, 0) };
    private readonly Button _save = new() { Name = "SaveExperimentEvidence", Content = "Salvar evidência JSON", IsEnabled = false, Margin = new Thickness(10, 0, 0, 0) };
    private readonly TextBox _output = new() { Name = "ExperimentOutput", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private CancellationTokenSource? _cancel;
    private bool _closeAfter;
    internal ExperimentReport? LastReport { get; private set; }

    public NetworkExperimentsWindow()
    {
        Title = "Experimentos de rede (6) — laboratório próprio"; Width = 800; Height = 640; MinWidth = 680; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)FindResource("BgDarkBrush");
        Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush");
        foreach (var button in new[] { _run, _stop, _save }) button.Style = (Style)FindResource("OutlineButton");
        var panel = new DockPanel { Margin = new Thickness(18) };
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); panel.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "Experimentos de rede (6) · separados dos Efeitos de pulso (4)", FontWeight = FontWeights.Bold, FontSize = 16 });
        top.Children.Add(new TextBlock { Text = "Não são exploits nem efeitos globais. TCP não afeta UDP. Nenhuma alteração em NIC, firewall, rotas ou NAT.", TextWrapping = TextWrapping.Wrap });
        _selector.ItemsSource = NetworkExperiments.Catalog; top.Children.Add(_selector); top.Children.Add(_description);
        top.Children.Add(new TextBlock { Text = "NAT opcional: IP numérico / porta / token do seu reflector (não salvo)" });
        var fields = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (Control field in new Control[] { _address, _port, _token }) { field.Margin = new Thickness(0, 4, 8, 4); fields.Children.Add(field); }
        top.Children.Add(fields); top.Children.Add(_owned);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        buttons.Children.Add(_run); buttons.Children.Add(_stop); buttons.Children.Add(_save); top.Children.Add(buttons);
        panel.Children.Add(_output); Content = panel;
        foreach (FrameworkElement element in new FrameworkElement[] { _selector, _address, _port, _token, _owned, _run, _stop, _save, _output })
            AutomationProperties.SetAutomationId(element, element.Name);
        AutomationProperties.SetName(_token, "Token do reflector próprio");
        _selector.SelectionChanged += (_, _) =>
        {
            var item = (ExperimentDefinition)_selector.SelectedItem;
            _description.Text = "Requer laboratório até executar. " + item.Dependency + "\n" + item.Limitation;
            bool nat = item.Id == NetworkExperiment.UdpNatLifetime;
            _address.IsEnabled = _port.IsEnabled = _token.IsEnabled = _owned.IsEnabled = nat;
        };
        _selector.SelectedIndex = 0;
        _run.Click += async (_, _) => await RunSelectedAsync();
        _stop.Click += (_, _) => _cancel?.Cancel();
        _save.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "JSON evidence|*.json", FileName = "darkdesync-network-experiment.json" };
            if (dialog.ShowDialog(this) == true)
            {
                try { System.IO.File.WriteAllText(dialog.FileName, _output.Text); }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { MessageBox.Show(this, ex.Message, "Evidência não salva"); }
            }
        };
        Closing += (_, e) => { if (_cancel != null) { e.Cancel = true; _closeAfter = true; _cancel.Cancel(); } };
    }

    internal async Task RunSelectedAsync()
    {
        if (_cancel != null) return;
        LastReport = null; _save.IsEnabled = false;
        var id = ((ExperimentDefinition)_selector.SelectedItem).Id;
        NatLabEndpoint? nat = null;
        if (id == NetworkExperiment.UdpNatLifetime && _owned.IsChecked == true)
        {
            if (!IPAddress.TryParse(_address.Text, out var ip) || !int.TryParse(_port.Text, out int port))
            { _output.Text = "Indisponível: IP numérico e porta válidos obrigatórios; nenhuma sonda enviada."; return; }
            nat = new(ip, port, true, _token.Password);
        }
        _cancel = new CancellationTokenSource();
        LastReport = null;
        _run.IsEnabled = _selector.IsEnabled = _save.IsEnabled = false; _stop.IsEnabled = true;
        _output.Text = "EXECUTANDO · STOP cancela; timeout máximo 25 s. Sem captura global.";
        try
        {
            LastReport = await NetworkExperiments.RunAsync(id, nat, _cancel.Token);
            _output.Text = JsonSerializer.Serialize(new { Status = NetworkExperiments.StatusLabel(LastReport.Status), Report = LastReport }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex) { _output.Text = "Indisponível: falha de ensaio; nenhuma prova concluída. " + ex.Message; }
        finally
        {
            _cancel.Dispose(); _cancel = null; _run.IsEnabled = _selector.IsEnabled = true; _stop.IsEnabled = false; _save.IsEnabled = LastReport != null;
            if (_closeAfter) Close();
        }
    }
}
