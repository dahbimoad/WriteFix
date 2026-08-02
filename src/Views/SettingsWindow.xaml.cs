using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using WriteFix.Interop;
using WriteFix.Models;
using WriteFix.Services.Ai;
using WriteFix.Services.Platform;
using WriteFix.Services.Settings;
using Brush = System.Windows.Media.Brush;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;

namespace WriteFix.Views;

public partial class SettingsWindow : Window
{
    /// <summary>
    /// Starting points in the model box. Free slugs come and go on OpenRouter, so the
    /// field stays editable — this is a shortlist, not a whitelist.
    /// </summary>
    private static readonly string[] SuggestedModels =
    [
        // Free, and measured fastest and cleanest on EN + FR of the free slugs.
        "google/gemma-4-26b-a4b-it:free",
        "google/gemma-4-31b-it:free",
        "openai/gpt-oss-20b:free",
        // Free auto-router: survives individual free slugs disappearing, but slower.
        "openrouter/free",
        // Paid — need credit on the OpenRouter account.
        "qwen/qwen3-32b",
        "anthropic/claude-haiku-4.5",
    ];

    private readonly SettingsStore _settings;
    private readonly SecretStore _secrets;
    private readonly OpenRouterClient _client;

    /// <summary>Re-registers the global hotkey; false means another app already owns it.</summary>
    private readonly Func<HotkeySpec, bool> _applyHotkey;

    private HotkeySpec _hotkey;
    private bool _keyEdited;

    public SettingsWindow(
        SettingsStore settings,
        SecretStore secrets,
        OpenRouterClient client,
        Func<HotkeySpec, bool> applyHotkey)
    {
        InitializeComponent();

        _settings = settings;
        _secrets = secrets;
        _client = client;
        _applyHotkey = applyHotkey;

        var current = settings.Current;
        _hotkey = HotkeySpec.ParseOrDefault(current.Hotkey);

        foreach (var model in SuggestedModels) ModelBox.Items.Add(model);
        ModelBox.Text = current.Model;

        PromptBox.Text = current.StyleInstructions;
        HotkeyBox.Text = _hotkey.ToString();
        StartupBox.IsChecked = StartupRegistry.IsEnabled();

        ApiKeyBox.PasswordChanged += (_, _) => _keyEdited = true;

        UpdateKeyStatus();
    }

    private void UpdateKeyStatus()
    {
        KeyStatusText.Text = _secrets.HasKey
            ? "A key is saved and encrypted for your Windows account. Leave the box empty to keep it."
            : "Create a key at openrouter.ai/keys, then paste it here.";
    }

    // ---- Scrolling ---------------------------------------------------------

    /// <summary>
    /// WPF lets a nested control swallow the mouse wheel, which breaks the page in two
    /// ways: over the prompt box the page stops scrolling entirely, and over a closed
    /// ComboBox the wheel silently changes the selected model. Both controls route here
    /// instead, and the wheel is handed back to the page whenever the inner control has
    /// no business acting on it.
    /// </summary>
    private void OnNestedScroll(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;

        // A text box keeps the wheel only while it still has somewhere to go.
        if (sender is TextBox box && HasRoomToScroll(box, e.Delta)) return;

        e.Handled = true;

        Scroller.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = MouseWheelEvent,
            Source = sender,
        });
    }

    private static bool HasRoomToScroll(TextBox box, int delta)
    {
        // Offsets are doubles and land a hair off the extremes, so compare with slack.
        const double Slack = 0.5;

        return delta < 0
            ? box.VerticalOffset < box.ExtentHeight - box.ViewportHeight - Slack
            : box.VerticalOffset > Slack;
    }

    // ---- Hotkey capture ----------------------------------------------------

    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Ignore the modifier keys themselves; wait for the real key.
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return;
        }

        var modifiers = HotkeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= HotkeyModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= HotkeyModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= HotkeyModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= HotkeyModifiers.Win;

        if (modifiers == HotkeyModifiers.None)
        {
            SetFooter("A hotkey needs at least one of Ctrl, Alt, Shift or Win.", isError: true);
            return;
        }

        _hotkey = new HotkeySpec(modifiers, key);
        HotkeyBox.Text = _hotkey.ToString();
        SetFooter("");
    }

    // ---- Commands ----------------------------------------------------------

    private async void OnTestConnection(object sender, RoutedEventArgs e)
    {
        // Test what is typed if anything was typed, otherwise what is already stored.
        var key = _keyEdited && ApiKeyBox.Password.Length > 0 ? ApiKeyBox.Password : _secrets.Read();

        if (string.IsNullOrWhiteSpace(key))
        {
            SetFooter("Paste an API key first.", isError: true);
            return;
        }

        TestButton.IsEnabled = false;
        SetFooter("Checking…");

        try
        {
            var (ok, message) = await _client.TestConnectionAsync(key, CancellationToken.None);
            SetFooter(message, isError: !ok);
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void OnClearKey(object sender, RoutedEventArgs e)
    {
        _secrets.Delete();
        ApiKeyBox.Clear();
        _keyEdited = false;

        var updated = _settings.Current.Clone();
        updated.HasApiKey = false;
        _settings.Save(updated);

        UpdateKeyStatus();
        SetFooter("Key removed.");
    }

    private void OnResetPrompt(object sender, RoutedEventArgs e)
    {
        PromptBox.Text = AppSettings.DefaultStyleInstructions;
        SetFooter("Correction style reset to the default.");
    }

    /// <summary>Shows exactly what will be sent, so the fixed half is inspectable even though it is not editable.</summary>
    private void OnPreviewExpanded(object sender, RoutedEventArgs e)
    {
        var preview = _settings.Current.Clone();
        preview.StyleInstructions = PromptBox.Text;
        PromptPreview.Text = preview.BuildSystemPrompt();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_keyEdited && ApiKeyBox.Password.Length > 0)
        {
            _secrets.Write(ApiKeyBox.Password);
            ApiKeyBox.Clear();
            _keyEdited = false;
        }

        var updated = _settings.Current.Clone();
        updated.Model = string.IsNullOrWhiteSpace(ModelBox.Text) ? AppSettings.DefaultModel : ModelBox.Text.Trim();
        updated.StyleInstructions = PromptBox.Text;
        updated.Hotkey = _hotkey.ToString();
        updated.StartWithWindows = StartupBox.IsChecked == true;
        updated.HasApiKey = _secrets.HasKey;

        StartupRegistry.Set(updated.StartWithWindows);
        _settings.Save(updated);

        var registered = _applyHotkey(_hotkey);

        UpdateKeyStatus();
        SetFooter(
            registered ? "Saved." : $"Saved, but {_hotkey} is already taken by another app. Pick a different one.",
            isError: !registered);
    }

    private void OnRunInBackground(object sender, RoutedEventArgs e) => Close();

    private void SetFooter(string message, bool isError = false)
    {
        FooterStatus.Text = message;
        FooterStatus.Foreground = (Brush)FindResource(isError ? "Danger" : "InkSoft");
    }
}
