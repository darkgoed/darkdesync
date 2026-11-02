using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DarkDesync;
using DarkDesync.Core;
using DarkDesync.Services;
using DarkDesync.ViewModels;

internal static partial class Program
{
    private static void VerifyExperienceUi(MainWindow window, MainViewModel vm, SettingsStore store, PacketEngine engine)
    {
        var effects = (ComboBox)window.FindName("EffectSelector");
        var styles = (ComboBox)window.FindName("PulseStyleSelector");
        var chip = (Border)window.FindName("EffectRatingChip");
        var label = (TextBlock)window.FindName("EffectRatingLabel");
        var note = (TextBlock)window.FindName("EffectRatingNote");
        var slider = Descendants(window).OfType<Slider>().Single();
        var editor = Descendants(window).OfType<TextBox>().Single();
        void Check(string text, string color, int ms)
        {
            Pump(); Require(label.Text == text && ((SolidColorBrush)chip.Background).Color.ToString() == "#FF" + color[1..], "Rendered rating label and accessible chip color");
            Require(vm.DiagnosticCycleDurationMs == ms && slider.Value == ms && editor.Text == ms.ToString(), "Preset updates real field/slider, not just appearance");
            Require(note.Text.Contains("Avaliação pessoal") && note.Text.Contains(ms + " ms"), "Subjective rating explicitly tied to exact duration");
            Require(!engine.IsCaptureRequested && !engine.IsEffectActive, "Style/rating never starts capture or pulse");
        }
        effects.SelectedItem = ModeCatalog.Name(DiagnosticMode.OutboundFreeze); styles.SelectedItem = "Legítimo"; Check("Legítimo", "#14532D", 250);
        styles.SelectedItem = "Agressivo"; Check("Agressivo", "#7F1D1D", 800); Require(note.Text.Contains("injogável"), "Freeze 800ms warning remains selectable");
        foreach (var mode in ModeCatalog.OutboundChoices.Values)
        {
            effects.SelectedItem = ModeCatalog.Name(mode); Pump();
            var expected = EffectExperienceCatalog.Rating(mode, 800); Check(expected.Label, expected.Background, 800);
            Require(Equals(effects.SelectedItem, ModeCatalog.Name(mode)), "Changing style/rating must never substitute the effect");
        }
        effects.SelectedItem = ModeCatalog.Name(DiagnosticMode.FlowAwareShaping); Check("Equilibrado", "#78350F", 800);
        styles.SelectedItem = "Equilibrado"; Pump(); Require(vm.DiagnosticCycleDurationMs == 800, "Balanced doesn't invent a fixed duration");
        slider.Value = 317; Check("Não testado", "#374151", 317); Require(vm.SelectedPulseStyle == "Equilibrado", "Balanced remains adjustable");
        styles.SelectedItem = "Legítimo"; Pump(); slider.Value = 275; Check("Não testado", "#374151", 275); Require(vm.SelectedPulseStyle == "Personalizado", "Manual edit overrides fixed preset explicitly");
        effects.SelectedItem = ModeCatalog.Name(DiagnosticMode.OutboundFreeze); styles.SelectedItem = "Agressivo"; Pump();
        vm.SaveSettingsCommand.Execute(null); byte[] before = File.ReadAllBytes(store.FilePath);
        styles.SelectedItem = "Legítimo"; Pump(); Require(before.SequenceEqual(File.ReadAllBytes(store.FilePath)), "Style changes never write settings");
        vm.LoadSettings(); Check("Agressivo", "#7F1D1D", 800); Require(Equals(styles.SelectedItem, "Agressivo"), "Saved style recovers through binding");
        Require(before.SequenceEqual(File.ReadAllBytes(store.FilePath)), "Load remains read-only");
        // Open/realize the real popup: covers inherited DataContext across its
        // separate visual tree and pills for every effect, not just selected one.
        effects.IsDropDownOpen = true; Pump(); effects.UpdateLayout();
        for (int i = 0; i < effects.Items.Count; i++)
        {
            var container = effects.ItemContainerGenerator.ContainerFromIndex(i) as ComboBoxItem;
            Require(container != null, "Effect dropdown item realized");
            var expected = EffectExperienceCatalog.Rating(ModeCatalog.Parse((string)effects.Items[i]), 800);
            Require(Descendants(container!).OfType<TextBlock>().Any(t => t.Text == expected.Label), "Every dropdown row has correct personal pill");
        }
        effects.IsDropDownOpen = false; Pump(); vm.ResetSettingsCommand.Execute(null); Pump();
        Check("Não testado", "#374151", 200); Require(Equals(styles.SelectedItem, "Personalizado"), "Defaults retain original 200ms and custom style");
        Require(before.SequenceEqual(File.ReadAllBytes(store.FilePath)), "Defaults don't overwrite settings without Save");
        Console.WriteLine("PASS personal ratings/styles WPF: all twelve dropdown pills, exact owner observations at 800ms/Freeze250, new effects untested, rendered colors+text, 250/800 presets and adjustable Balanced, slider/editor synchronization, manual Custom override, save/load/defaults read-only and no automatic capture. No objective/gameplay claim.");
    }
}
