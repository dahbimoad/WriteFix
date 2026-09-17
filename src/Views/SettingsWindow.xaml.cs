using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WriteFix.Interop;
using WriteFix.Models;
using WriteFix.Services.Ai;
using WriteFix.Services.Platform;
using WriteFix.Services.Settings;
using WriteFix.Services.Updates;
using Brush = System.Windows.Media.Brush;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;
using TextBox = System.Windows.Controls.TextBox;

namespace WriteFix.Views;

public partial class SettingsWindow : Window
{
    /// <summary>
    /// A provider WriteFix has actually been tested against, and the models worth
    /// starting from on it. Both boxes stay editable — this is a shortlist, not a
    /// whitelist, and any OpenAI-compatible endpoint works.
    /// </summary>
    private sealed record Provider(string BaseUrl, string KeyPage, string[] Models);

    private static readonly Provider[] Providers =
    [
        new(AppSettings.GroqBaseUrl, "console.groq.com/keys",
        [
            // Measured 2026-08-31 against the production prompt. gpt-oss-120b was
            // fastest (~570ms) and the only one that got the French elision and
            // accents right; both gpt-oss models keep their reasoning in a separate
            // field. qwen3.8 is quick but answers the text instead of correcting it
            // when the text looks like an instruction.
            "openai/gpt-oss-120b",
            "openai/gpt-oss-20b",
            "qwen/qwen3.8-27b",
        ]),
        new(AppSettings.OpenRouterBaseUrl, "openrouter.ai/keys",
        [
            // Free here means a pool shared with every other OpenRouter user, so
            // these 429 for reasons that have nothing to do with your key.
            "google/gemma-4-26b-a4b-it:free",
            "openrouter/free",
            // Paid — need credit on the account.
            "anthropic/claude-haiku-4.5",
        ]),
    ];

    private const string WaitingForKeysPrompt = "Press a shortcut…";
    private const string WaitingForKeysStatus = "Waiting for keys. Esc keeps the current shortcut.";

    private readonly SettingsStore _settings;
    private readonly SecretStore _secrets;
    private readonly AiRouter _ai;
    private readonly UpdateCoordinator _updates;

    /// <summary>Owns the global hotkeys: suspended while a shortcut is typed, probed for conflicts, re-registered on save.</summary>
    private readonly AppMessageWindow _hotkeyHost;

    /// <summary>The shortcut shown for each mode, which may not be saved yet.</summary>
    private readonly Dictionary<CorrectionMode, HotkeySpec> _hotkeys;

    private bool _hotkeysSuspended;
    private bool _keyEdited;
    private bool _loading;

    public SettingsWindow(
        SettingsStore settings,
        SecretStore secrets,
        AiRouter ai,
        UpdateCoordinator updates,
        AppMessageWindow hotkeyHost)
    {
        InitializeComponent();

        _settings = settings;
        _secrets = secrets;
        _ai = ai;
        _updates = updates;
        _hotkeyHost = hotkeyHost;

        var current = settings.Current;
        _hotkeys = Enum.GetValues<CorrectionMode>().ToDictionary(mode => mode, mode => HotkeySpec.SavedFor(current, mode));

        // Setting Text below can select a matching item, which would fire
        // OnProviderChanged and overwrite the saved model with a preset.
        _loading = true;
        foreach (var provider in Providers) ProviderBox.Items.Add(provider.BaseUrl);
        ProviderBox.Text = current.ApiBaseUrl;

        foreach (var model in ModelsFor(current.ApiBaseUrl)) ModelBox.Items.Add(model);
        ModelBox.Text = current.Model;
        _loading = false;

        OpenCodeModelBox.Text = current.OpenCodeModel;
        ConnectionOpenCodeOption.IsChecked = current.Provider == AiProvider.OpenCode;
        ConnectionApiOption.IsChecked = current.Provider == AiProvider.ChatCompletions;

        PromptBox.Text = current.StyleInstructions;
        RephrasePromptBox.Text = current.RephraseInstructions;
        FixHotkeyBox.Text = _hotkeys[CorrectionMode.Fix].ToString();
        RephraseHotkeyBox.Text = _hotkeys[CorrectionMode.Rephrase].ToString();
        SelectLockedMode(current.LockedMode);
        StartupBox.IsChecked = StartupRegistry.IsEnabled();
        AutoUpdateBox.IsChecked = current.AutoCheckUpdates;
        VersionText.Text = $"WriteFix {UpdateService.CurrentVersion}";

        ApiKeyBox.PasswordChanged += (_, _) => _keyEdited = true;

        // Focus normally leaves the shortcut box before the window closes, but a
        // suspended hotkey must never outlive the window if it does not.
        Closed += (_, _) => ResumeHotkeys();

        UpdateKeyStatus();
    }

    private void UpdateKeyStatus()
    {
        KeyStatusText.Text = _secrets.HasKey
            ? "A key is saved and encrypted for your Windows account. Leave the box empty to keep it."
            : $"Create a key at {KeyPageFor(ProviderBox.Text)}, then paste it here.";
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

    // ---- Connection --------------------------------------------------------

    private AiProvider SelectedConnection() =>
        ConnectionOpenCodeOption.IsChecked == true ? AiProvider.OpenCode : AiProvider.ChatCompletions;

    private void OnConnectionChanged(object sender, RoutedEventArgs e)
    {
        var openCode = SelectedConnection() == AiProvider.OpenCode;
        OpenCodePanel.Visibility = openCode ? Visibility.Visible : Visibility.Collapsed;
        ApiPanel.Visibility = openCode ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Switching provider makes the current model id meaningless — a Groq id is not a
    /// slug OpenRouter knows — so the model list and the pick are both replaced. Only
    /// a real user choice does this; loading the window does not.
    /// </summary>
    private void OnProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProviderBox.SelectedItem is not string baseUrl) return;

        var models = ModelsFor(baseUrl);

        ModelBox.Items.Clear();
        foreach (var model in models) ModelBox.Items.Add(model);
        ModelBox.Text = models.FirstOrDefault() ?? "";

        UpdateKeyStatus();
        SetFooter(_secrets.HasKey
            ? "Provider changed. The saved key belongs to the old one — paste a new key before saving."
            : "Provider changed.");
    }

    private static string[] ModelsFor(string baseUrl) =>
        Match(baseUrl)?.Models ?? [];

    private static string KeyPageFor(string baseUrl) =>
        Match(baseUrl)?.KeyPage ?? "your provider's console";

    private static Provider? Match(string baseUrl) =>
        Providers.FirstOrDefault(p =>
            string.Equals(p.BaseUrl, baseUrl?.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase));

    /// <summary>Fills the model list from OpenCode, starting it if needed; the box keeps whatever was typed.</summary>
    private async void OnLoadOpenCodeModels(object sender, RoutedEventArgs e)
    {
        LoadOpenCodeModelsButton.IsEnabled = false;
        SetFooter("Starting the SDK and loading its models…");

        try
        {
            var (models, error) = await _ai.OpenCode.LoadModelsAsync(CancellationToken.None);
            if (models.Count == 0)
            {
                SetFooter(error, isError: true);
                return;
            }

            var typed = OpenCodeModelBox.Text;
            OpenCodeModelBox.Items.Clear();
            foreach (var model in models) OpenCodeModelBox.Items.Add(model);
            OpenCodeModelBox.Text = typed;

            SetFooter($"{models.Count} SDK models loaded. Open the list to pick one.");
        }
        finally
        {
            LoadOpenCodeModelsButton.IsEnabled = true;
        }
    }

    private async void OnTestOpenCode(object sender, RoutedEventArgs e)
    {
        TestOpenCodeButton.IsEnabled = false;
        SetFooter("Starting the SDK and checking the model…");

        try
        {
            var (ok, message) = await _ai.OpenCode.TestConnectionAsync(OpenCodeModelBox.Text, CancellationToken.None);
            SetFooter(message, isError: !ok);
        }
        finally
        {
            TestOpenCodeButton.IsEnabled = true;
        }
    }

    // ---- Mode --------------------------------------------------------------

    private void SelectLockedMode(CorrectionMode? lockedMode)
    {
        ModeChooseOption.IsChecked = lockedMode is null;
        ModeFixOption.IsChecked = lockedMode == CorrectionMode.Fix;
        ModeRephraseOption.IsChecked = lockedMode == CorrectionMode.Rephrase;
    }

    private CorrectionMode? SelectedLockedMode()
    {
        if (ModeFixOption.IsChecked == true) return CorrectionMode.Fix;
        if (ModeRephraseOption.IsChecked == true) return CorrectionMode.Rephrase;
        return null;
    }

    private void OnLockedModeChanged(object sender, RoutedEventArgs e)
    {
        ModeHelpText.Text = SelectedLockedMode() switch
        {
            CorrectionMode.Fix => "Both shortcuts only fix errors. The card hides its Fix / Rephrase switch.",
            CorrectionMode.Rephrase => "Both shortcuts rephrase. The card hides its Fix / Rephrase switch.",
            _ => "The Fix shortcut starts in Fix, the Rephrase shortcut in Rephrase, and the card lets you switch.",
        };
    }

    // ---- Hotkey capture ----------------------------------------------------

    private CorrectionMode ModeOf(object hotkeyBox) =>
        hotkeyBox == RephraseHotkeyBox ? CorrectionMode.Rephrase : CorrectionMode.Fix;

    private TextBlock StatusFor(CorrectionMode mode) =>
        mode == CorrectionMode.Rephrase ? RephraseHotkeyStatus : FixHotkeyStatus;

    /// <summary>
    /// While a shortcut box has focus every WriteFix hotkey is released, so pressing
    /// the current combination records it instead of starting a correction.
    /// </summary>
    private void OnHotkeyGotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        SuspendHotkeys();

        ((TextBox)sender).Text = WaitingForKeysPrompt;
        SetHotkeyStatus(ModeOf(sender), WaitingForKeysStatus, "InkSoft");
    }

    private void OnHotkeyLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        var mode = ModeOf(sender);
        ((TextBox)sender).Text = _hotkeys[mode].ToString();

        if (StatusFor(mode).Text == WaitingForKeysStatus) SetHotkeyStatus(mode, "", "InkSoft");

        ResumeHotkeys();
    }

    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var shiftOrNothing = (Keyboard.Modifiers & ~ModifierKeys.Shift) == ModifierKeys.None;

        // Tab and Shift+Tab still move between fields.
        if (key == Key.Tab && shiftOrNothing) return;

        e.Handled = true;

        if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            Keyboard.ClearFocus();
            return;
        }

        // Ignore the modifier keys themselves; wait for the real key.
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return;
        }

        var mode = ModeOf(sender);

        // Shift alone would steal a capital letter from every app.
        if (shiftOrNothing)
        {
            SetHotkeyStatus(mode, "Add Ctrl, Alt or Win to the combination.", "Danger");
            return;
        }

        _hotkeys[mode] = new HotkeySpec(ReadModifiers(), key);
        ((TextBox)sender).Text = _hotkeys[mode].ToString();
        CheckHotkey(mode);
    }

    private static HotkeyModifiers ReadModifiers()
    {
        var modifiers = HotkeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= HotkeyModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= HotkeyModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= HotkeyModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= HotkeyModifiers.Win;
        return modifiers;
    }

    /// <summary>Tests the shortcut shown for <paramref name="mode"/>, shows the verdict under its box, and returns whether it can be saved.</summary>
    private bool CheckHotkey(CorrectionMode mode)
    {
        var spec = _hotkeys[mode];
        var otherMode = mode == CorrectionMode.Fix ? CorrectionMode.Rephrase : CorrectionMode.Fix;

        if (spec == _hotkeys[otherMode])
        {
            SetHotkeyStatus(mode, $"{spec} is already your {otherMode} shortcut.", "Danger");
            return false;
        }

        if (!_hotkeyHost.IsHotkeyAvailable(spec))
        {
            SetHotkeyStatus(mode, $"{spec} is taken by another app or by Windows. Try another one.", "Danger");
            return false;
        }

        var message = spec == HotkeySpec.SavedFor(_settings.Current, mode)
            ? $"{spec} is your current shortcut and works."
            : $"{spec} is free. Save changes to use it.";

        SetHotkeyStatus(mode, message, "Positive");
        return true;
    }

    private void SuspendHotkeys()
    {
        _hotkeyHost.UnregisterHotkeys();
        _hotkeysSuspended = true;
    }

    private void ResumeHotkeys()
    {
        if (!_hotkeysSuspended) return;
        _hotkeysSuspended = false;

        var taken = _hotkeyHost.RegisterSavedHotkeys(_settings.Current);
        if (taken.Count > 0)
            SetFooter($"{string.Join(" and ", taken)} could not be registered again. Pick a different shortcut.", isError: true);
    }

    private void SetHotkeyStatus(CorrectionMode mode, string message, string brushKey)
    {
        var status = StatusFor(mode);
        status.Text = message;
        status.Foreground = (Brush)FindResource(brushKey);
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
            var (ok, message) = await _ai.ChatCompletions.TestConnectionAsync(
                ProviderBox.Text.Trim(), key, ModelBox.Text.Trim(), CancellationToken.None);
            SetFooter(message, isError: !ok);
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// A check the user asked for, so it reports either way. It also ignores a
    /// previously skipped version: pressing this button is asking about it again.
    /// </summary>
    private async void OnCheckForUpdates(object sender, RoutedEventArgs e)
    {
        UpdateCheckButton.IsEnabled = false;
        SetFooter("Checking for updates…");

        try
        {
            var result = await _updates.CheckAsync(manual: true);
            SetFooter(result.Message, isError: !result.Ok);
        }
        finally
        {
            UpdateCheckButton.IsEnabled = true;
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
        SetFooter("Fix instructions reset to the default.");
    }

    private void OnResetRephrasePrompt(object sender, RoutedEventArgs e)
    {
        RephrasePromptBox.Text = AppSettings.DefaultRephraseInstructions;
        SetFooter("Rephrase instructions reset to the default.");
    }

    /// <summary>Shows exactly what will be sent, so the fixed half is inspectable even though it is not editable.</summary>
    private void OnPreviewExpanded(object sender, RoutedEventArgs e)
    {
        var preview = _settings.Current.Clone();
        preview.StyleInstructions = PromptBox.Text;
        preview.RephraseInstructions = RephrasePromptBox.Text;

        PromptPreview.Text =
            $"FIX\n\n{preview.BuildSystemPrompt(CorrectionMode.Fix)}\n\n\n" +
            $"REPHRASE\n\n{preview.BuildSystemPrompt(CorrectionMode.Rephrase)}";
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        // Check both before refusing, so each box shows its own verdict.
        var hotkeysValid = true;
        foreach (var mode in _hotkeys.Keys) hotkeysValid &= CheckHotkey(mode);

        if (!hotkeysValid)
        {
            SetFooter("Nothing saved yet: choose a different shortcut first.", isError: true);
            return;
        }

        if (_keyEdited && ApiKeyBox.Password.Length > 0)
        {
            _secrets.Write(ApiKeyBox.Password);
            ApiKeyBox.Clear();
            _keyEdited = false;
        }

        var updated = _settings.Current.Clone();
        updated.Provider = SelectedConnection();
        updated.ApiBaseUrl = ProviderBox.Text.Trim();
        updated.Model = string.IsNullOrWhiteSpace(ModelBox.Text) ? AppSettings.DefaultModel : ModelBox.Text.Trim();
        updated.OpenCodeModel = OpenCodeModelBox.Text.Trim();
        updated.StyleInstructions = PromptBox.Text;
        updated.RephraseInstructions = RephrasePromptBox.Text;
        updated.LockedMode = SelectedLockedMode();
        updated.Hotkey = _hotkeys[CorrectionMode.Fix].ToString();
        updated.RephraseHotkey = _hotkeys[CorrectionMode.Rephrase].ToString();
        updated.StartWithWindows = StartupBox.IsChecked == true;
        updated.AutoCheckUpdates = AutoUpdateBox.IsChecked == true;
        updated.HasApiKey = _secrets.HasKey;

        StartupRegistry.Set(updated.StartWithWindows);
        _settings.Save(updated);

        var taken = _hotkeyHost.RegisterSavedHotkeys(updated);

        UpdateKeyStatus();
        SetFooter(
            taken.Count == 0 ? "Saved." : $"Saved, but {string.Join(" and ", taken)} could not be registered. Pick a different one.",
            isError: taken.Count > 0);
    }

    private void OnRunInBackground(object sender, RoutedEventArgs e) => Close();

    private void SetFooter(string message, bool isError = false)
    {
        FooterStatus.Text = message;
        FooterStatus.Foreground = (Brush)FindResource(isError ? "Danger" : "InkSoft");
    }
}
