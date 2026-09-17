using System.Windows.Input;
using WriteFix.Models;

namespace WriteFix.Interop;

/// <summary>A parsed hotkey such as "Ctrl+Alt+F", round-trippable to and from settings.json.</summary>
public sealed record HotkeySpec(HotkeyModifiers Modifiers, Key Key)
{
    public uint VirtualKey => (uint)KeyInterop.VirtualKeyFromKey(Key);

    public static HotkeySpec Default { get; } =
        new(HotkeyModifiers.Control | HotkeyModifiers.Alt, Key.F);

    public static HotkeySpec DefaultRephrase { get; } =
        new(HotkeyModifiers.Control | HotkeyModifiers.Alt, Key.R);

    public static HotkeySpec DefaultFor(CorrectionMode mode) =>
        mode == CorrectionMode.Rephrase ? DefaultRephrase : Default;

    /// <summary>The shortcut saved for <paramref name="mode"/>, or that mode's default if the saved text is unusable.</summary>
    public static HotkeySpec SavedFor(AppSettings settings, CorrectionMode mode) =>
        TryParse(settings.HotkeyFor(mode), out var spec) ? spec : DefaultFor(mode);

    public static bool TryParse(string? text, out HotkeySpec spec)
    {
        spec = Default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var modifiers = HotkeyModifiers.None;
        Key? key = null;

        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers |= HotkeyModifiers.Control;
                    break;
                case "alt":
                    modifiers |= HotkeyModifiers.Alt;
                    break;
                case "shift":
                    modifiers |= HotkeyModifiers.Shift;
                    break;
                case "win" or "windows":
                    modifiers |= HotkeyModifiers.Win;
                    break;
                default:
                    if (!Enum.TryParse<Key>(raw, ignoreCase: true, out var parsed)) return false;
                    key = parsed;
                    break;
            }
        }

        // A bare key with no modifier would swallow that key system-wide.
        if (key is null || modifiers == HotkeyModifiers.None) return false;

        spec = new HotkeySpec(modifiers, key.Value);
        return true;
    }

    public override string ToString()
    {
        var parts = new List<string>(4);
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(Key.ToString());
        return string.Join("+", parts);
    }
}
