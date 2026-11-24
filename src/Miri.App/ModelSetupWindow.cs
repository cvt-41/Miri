using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Miri.Brain;
using Miri.Core;

namespace Miri.App;

/// <summary>
/// First-start (and tray "Choose models…") picker: suggests a chat + vision model pair for this PC,
/// downloads the chosen pair, switches Miri to it and deletes the models it replaces.
/// </summary>
public sealed class ModelSetupWindow : Window
{
    private readonly MiriSettings _settings;
    private readonly Func<IMiriBrain?> _brain;
    private readonly List<RadioButton> _choices = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _go, _keep;
    private CancellationTokenSource? _cts;

    public ModelSetupWindow(MiriSettings settings, Func<IMiriBrain?> brain)
    {
        _settings = settings;
        _brain = brain;
        Title = "Miri: choose my brain";
        Width = 470; SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = System.Windows.Media.Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize; Topmost = true; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas, Segoe UI"); FontSize = 13;
        SetResourceReference(ForegroundProperty, "TextBrush");

        var pc = PcSpecs.Detect();
        int rec = ModelAdvisor.Recommend(pc);
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = "Meow! Let's pick my brain 🐾", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6) });
        stack.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = $"Your PC: {pc.RamGb:0.#} GB RAM" + (pc.VramGb >= 1 ? $", {pc.Gpu} ({pc.VramGb:0.#} GB)" : ", integrated graphics") + ".\n" +
                   "I need TWO models: one to answer your questions and use your apps, and one vision model to see your screen.",
        });
        for (int i = 0; i < ModelAdvisor.Pairs.Length; i++)
        {
            var p = ModelAdvisor.Pairs[i];
            var rb = new RadioButton
            {
                GroupName = "pair", IsChecked = i == rec, Margin = new Thickness(0, 6, 0, 0), Tag = p,
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = $"{p.Label}{(i == rec ? " (recommended)" : "")}: {p.Chat} + {p.Vision}, ~{p.DownloadGb:0.#} GB\n{p.Note}" +
                           (p.MinRamGb > Math.Max(pc.RamGb, pc.VramGb * 2) + 1 ? "  ⚠ may be too big for this PC" : ""),
                },
            };
            rb.SetResourceReference(ForegroundProperty, "TextBrush");
            _choices.Add(rb);
            stack.Children.Add(rb);
        }
        stack.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), FontSize = 11,
            Text = $"Switching downloads the new pair first, then deletes the ones I use now ({string.Join(", ", new[] { settings.ChatModel, settings.VisionModel }.Distinct())}). Change any time: tray → Choose models…",
        });
        stack.Children.Add(_status);

        _go = new Button { Content = "Download & switch", Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        _keep = new Button { Content = "Keep current", IsCancel = true };
        _go.SetResourceReference(StyleProperty, "PixelButton");
        _keep.SetResourceReference(StyleProperty, "PixelButton");
        _go.Click += async (_, _) => await SwitchAsync();
        _keep.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(_go); buttons.Children.Add(_keep);
        stack.Children.Add(buttons);

        Content = new PixelPanel { Pixel = 3, Padding = new Thickness(12, 10, 12, 10), Child = stack };
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch (InvalidOperationException) { } };
        Closed += (_, _) => _cts?.Cancel();
    }

    private async Task SwitchAsync()
    {
        var pair = (ModelPair)_choices.First(c => c.IsChecked == true).Tag;
        _go.IsEnabled = false;
        foreach (var c in _choices) c.IsEnabled = false;
        _keep.Content = "Cancel";
        _cts = new CancellationTokenSource();
        try
        {
            await ModelAdvisor.SwitchAsync(_settings, pair, new Progress<string>(s => _status.Text = s), _cts.Token);
            if (_brain() is { } b) b.ActiveModel = pair.Chat;
            _status.Text = $"All set! I now think with {pair.Chat} and see with {pair.Vision}. Remember: I need both, one to answer and one to see.";
            _keep.Content = "Close";
        }
        catch (OperationCanceledException) { _status.Text = "Stopped. I'm still using my old models."; }
        catch (Exception ex)
        {
            AppShell.Log("Model switch failed: " + ex);
            _status.Text = $"That didn't work ({ex.Message}). I kept my old models. Is Ollama running?";
            _go.IsEnabled = true;
            foreach (var c in _choices) c.IsEnabled = true;
            _keep.Content = "Keep current";
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (!_settings.ModelSetupDone) { _settings.ModelSetupDone = true; try { _settings.Save(); } catch { } }   // answered: don't ask again at start
    }
}
