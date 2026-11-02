using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using DarkDesync;
using DarkDesync.Core;
using DarkDesync.Services;
using DarkDesync.ViewModels;

internal static partial class Program
{
    private static void VerifyExperienceUi(MainWindow window, MainViewModel vm, SettingsStore store, PacketEngine engine)
    {
        var effects = (ComboBox)window.FindName("EffectSelector");
        var legit = (RadioButton)window.FindName("LegitStyleButton");
        var aggressive = (RadioButton)window.FindName("AggressiveStyleButton");
        var manual = (CheckBox)window.FindName("ManualDurationToggle");
        void Select(RadioButton button) { ((ISelectionItemProvider)new RadioButtonAutomationPeer(button).GetPattern(PatternInterface.SelectionItem)).Select(); Pump(); }
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
        effects.SelectedItem = ModeCatalog.Name(DiagnosticMode.OutboundFreeze); Select(legit); Check("Legit", "#14532D", 250);
        Select(aggressive); Check("Agressivo", "#7F1D1D", 800); Require(note.Text.Contains("injogável"), "Freeze 800ms warning remains selectable");
        foreach (var mode in ModeCatalog.OutboundChoices.Values)
        {
            effects.SelectedItem = ModeCatalog.Name(mode); Pump();
            var expected = EffectExperienceCatalog.Rating(mode, 800); Check(expected.Label, expected.Background, 800);
            Require(Equals(effects.SelectedItem, ModeCatalog.Name(mode)), "Changing style/rating must never substitute the effect");
        }
        effects.SelectedItem = ModeCatalog.Name(DiagnosticMode.FlowAwareShaping); Check("Equilibrado", "#78350F", 800);
        Require(vm.PulseStyleOptions.SequenceEqual(new[] { "Legit", "Agressivo" }), "Exactly two primary presets, Balanced is classification only");
        Require(!slider.IsEnabled && !editor.IsEnabled, "Fixed preset fields are read-only");
        manual.IsChecked = true; Pump(); slider.Value = 317; Check("Não avaliado", "#374151", 317);
        Require(vm.SelectedPulseStyle == "Teste manual", "Manual adjustment requires explicit choice");
        Select(legit); Check("Não avaliado", "#374151", 250);
        manual.IsChecked = true; Pump(); slider.Value = 275; Check("Não avaliado", "#374151", 275);
        effects.SelectedItem = ModeCatalog.Name(DiagnosticMode.OutboundFreeze); Select(aggressive); Pump();
        vm.SaveSettingsCommand.Execute(null); byte[] before = File.ReadAllBytes(store.FilePath);
        Select(legit); Pump(); Require(before.SequenceEqual(File.ReadAllBytes(store.FilePath)), "Style changes never write settings");
        vm.LoadSettings(); Check("Agressivo", "#7F1D1D", 800); Require(aggressive.IsChecked == true, "Saved style recovers through binding");
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
        Check("Não avaliado", "#374151", 200); Require(manual.IsChecked == true, "Defaults retain original 200ms and custom style");
        Require(before.SequenceEqual(File.ReadAllBytes(store.FilePath)), "Defaults don't overwrite settings without Save");
        Console.WriteLine("PASS personal ratings/styles WPF: four favorite dropdown pills, exact owner observations at 800ms/Freeze250, rendered colors+text, two 250/800 primary presets, Balanced classification only, explicit manual, slider/editor synchronization, explicit Save/read-only load/defaults and no automatic capture. No objective/gameplay claim.");
    }
}
