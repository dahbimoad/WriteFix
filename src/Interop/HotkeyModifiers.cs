namespace WriteFix.Interop;

/// <summary>
/// Modifier flags as <c>RegisterHotKey</c> expects them. Public (unlike the rest of
/// the Win32 surface) because <see cref="HotkeySpec"/> exposes it to the UI.
/// </summary>
[Flags]
public enum HotkeyModifiers : uint
{
    None = 0x0000,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,

    /// <summary>Stops a held-down hotkey from queuing a burst of corrections.</summary>
    NoRepeat = 0x4000,
}
