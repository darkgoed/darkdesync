using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DarkDesync;
using DarkDesync.Core;
using DarkDesync.ViewModels;

internal static partial class Program
{
    [StructLayout(LayoutKind.Sequential)] private struct ScreenRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out ScreenRect rect);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr source, int sx, int sy, uint operation);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);

    private static void CapturePanel(MainWindow window, string folder, string name)
    {
        window.Activate(); window.UpdateLayout(); Pump();
        string capturedState = ((TextBlock)window.FindName("CompactCaptureState")).Text;
        Require(GetWindowRect(new WindowInteropHelper(window).Handle, out var rect), "Native screenshot window bounds");
        int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
        var source = GetDC(IntPtr.Zero); var dc = CreateCompatibleDC(source); var bitmap = CreateCompatibleBitmap(source, w, h); var previous = SelectObject(dc, bitmap);
        try
        {
            Require(bitmap != IntPtr.Zero && BitBlt(dc, 0, 0, w, h, source, rect.Left, rect.Top, 0x00CC0020), "Actual Windows desktop screenshot via GDI, not a synthesized UI");
            var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0); byte[] pixels = new byte[w * h * 4]; converted.CopyPixels(pixels, w * 4, 0);
            Require(pixels.Distinct().Count() > 32, "Screenshot must not be blank");
            Directory.CreateDirectory(folder);
            using var stream = File.Create(Path.Combine(folder, name + ".png"));
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(stream);
            // Real loaded WPF visual at 120 DPI, not an OS monitor-scale claim.
            var root = (FrameworkElement)window.FindName("PanelRoot");
            var scaled = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * 1.25), (int)Math.Ceiling(root.ActualHeight * 1.25), 120, 120, PixelFormats.Pbgra32);
            scaled.Render(root);
            using var dpiStream = File.Create(Path.Combine(folder, name + "-render125.png"));
            var dpiEncoder = new PngBitmapEncoder(); dpiEncoder.Frames.Add(BitmapFrame.Create(scaled)); dpiEncoder.Save(dpiStream);
            File.WriteAllText(Path.Combine(folder, name + "-render125.json"), JsonSerializer.Serialize(new { name, width = scaled.PixelWidth, height = scaled.PixelHeight, dpi = 120, scale = "125%", source = "Actual WPF visual rasterized at 120dpi; OS desktop scaling not changed", state = capturedState }));
            File.WriteAllText(Path.Combine(folder, name + ".json"), JsonSerializer.Serialize(new { name, width = w, height = h, windowWidthDip = window.Width, windowHeightDip = window.Height, source = "Windows desktop BitBlt", state = capturedState }));
        }
        finally { SelectObject(dc, previous); DeleteObject(bitmap); DeleteDC(dc); ReleaseDC(IntPtr.Zero, source); }
    }
    private static void CheckStateColor(MainWindow window, string text, string rgb)
    {
        Pump(); var label = (TextBlock)window.FindName("CompactCaptureState");
        Require(label.Text == text && ((SolidColorBrush)label.Foreground).Color.ToString() == "#FF" + rgb[1..], "Actual rendered state text/brush: " + text);
    }
    private static void VerifyCompactPanelUi(MainWindow window, MainViewModel vm, PacketEngine engine, string folder)
    {
        var descendants = Descendants(window).ToArray();
        Require(window.FindName("CaptureState") == null && window.FindName("CopyDiagnosticButton") == null && !descendants.OfType<Expander>().Any() && !descendants.OfType<ListBox>().Any(), "Old capture/copy/log removed from visual tree, not hidden");
        Require(!descendants.OfType<TextBlock>().Any(t => new[] { "CAPTURA", "Copiar diagnóstico", "Diagnóstico de captura" }.Contains(t.Text)), "No capture card or diagnostic section");
        foreach (var t in descendants.OfType<TextBlock>())
        {
            var path = t.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path?.Path;
            Require(path is not ("NetworkDiagnosticsText" or "ScopedTuplesText" or "SupportDiagnosticText" or "Logs"), "Technical telemetry has no main-window binding");
        }
        Require(vm.SupportDiagnosticText.Contains("scope=Outbound") && vm.Logs != null, "Sanitized support/telemetry retained internally");
        CheckStateColor(window, "PARADO", "#8D93AA");
        foreach (var size in new[] { (900d, 560d), (1000d, 640d) })
        {
            window.Width = size.Item1; window.Height = size.Item2; window.UpdateLayout(); Pump();
            var root = (FrameworkElement)window.FindName("PanelRoot");
            Rect Bounds(string name) { var e = (FrameworkElement)window.FindName(name); return e.TransformToAncestor(root).TransformBounds(new Rect(e.RenderSize)); }
            foreach (string name in new[] { "EffectSelector", "LegitStyleButton", "AggressiveStyleButton", "ManualDurationToggle", "DurationEditor", "DurationSlider", "OutboundScope", "StartStopButton", "ManualPulseButton", "SaveSettingsButton", "LoadSettingsButton", "StartStatus" })
            {
                var b = Bounds(name); Require(b.Width > 0 && b.Height > 0 && b.Left >= 0 && b.Top >= 0 && b.Right <= root.ActualWidth + 1 && b.Bottom <= root.ActualHeight + 1, "No clipping at " + size + ": " + name);
            }
            Require(Bounds("EffectArea").Right < Bounds("DurationArea").Left && Bounds("DurationArea").Bottom < Bounds("OutboundScope").Top, "Both columns occupied, no overlap with actions");
            foreach (var mode in ModeCatalog.OutboundChoices.Values)
            {
                vm.SelectedDiagnosticModeText = ModeCatalog.Name(mode); vm.SelectPulseStyleCommand.Execute("Agressivo"); Pump();
                var expected = EffectExperienceCatalog.Rating(mode, 800);
                Require(((TextBlock)window.FindName("EffectRatingLabel")).Text == expected.Label, "All four selections render at both sizes");
                CapturePanel(window, folder, $"{(int)size.Item1}x{(int)size.Item2}-{mode}-800-stopped");
            }
            vm.SelectedDiagnosticModeText = ModeCatalog.Name(DiagnosticMode.OutboundFreeze); vm.SelectPulseStyleCommand.Execute("Legit");
            CapturePanel(window, folder, $"{(int)size.Item1}x{(int)size.Item2}-Freeze-Legit250");
        }
        // Waiting is not a successful native OPEN: explicit view-state seam only.
        var requested = typeof(PacketEngine).GetField("_captureRequested", BindingFlags.Instance | BindingFlags.NonPublic)!;
        requested.SetValue(engine, true);
        typeof(MainViewModel).GetMethod("RefreshNetworkDiagnostics", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null);
        CheckStateColor(window, "AGUARDANDO", "#FF9F1C"); CapturePanel(window, folder, "waiting-view-state-seam-no-native-open");
        requested.SetValue(engine, false);
        vm.ResetSettingsCommand.Execute(null); Pump(); CheckStateColor(window, "PARADO", "#8D93AA");
        var start = (Button)window.FindName("StartStopButton"); start.Focus(); Pump();
        Require(start.IsKeyboardFocused && start.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.Next)), "Tab traverses primary actions");
        RouteSpace(start); Require(!engine.IsCaptureRequested, "Focused Space cannot click START without an armed capture");
        Console.WriteLine("PASS compact visual panel: real desktop PNGs 900x560/1000x640 and WPF 125% rasterization (not OS scaling), four favorite pills, no diagnostics/copy/log elements or bindings, two primary styles/manual; stopped/waiting brush verified. Native ARMADO/PULSO colors verified separately.");
    }
}
