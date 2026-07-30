using static WriteFix.Interop.NativeMethods;

namespace WriteFix.Interop;

/// <summary>Sends the few keystrokes WriteFix needs: Ctrl+C, Ctrl+A, Ctrl+V.</summary>
internal static class InputSender
{
    /// <summary>
    /// Presses Ctrl + <paramref name="key"/> in the foreground window.
    ///
    /// The hotkey that got us here (e.g. Ctrl+Alt+F) leaves Ctrl and Alt physically
    /// down. If we synthesised Ctrl+C on top of that, the target would see
    /// Ctrl+Alt+C. So every held modifier is released first.
    /// </summary>
    public static void SendCtrl(ushort key)
    {
        ReleaseHeldModifiers();

        var inputs = new[]
        {
            KeyDown(VK_CONTROL),
            KeyDown(key),
            KeyUp(key),
            KeyUp(VK_CONTROL),
        };

        Send(inputs);
    }

    /// <summary>
    /// Lets go of any modifier the user is still physically holding, so synthetic
    /// keystrokes are not polluted by it.
    /// </summary>
    public static void ReleaseHeldModifiers()
    {
        ushort[] modifiers = [VK_CONTROL, VK_MENU, VK_SHIFT, VK_LWIN, VK_RWIN];

        var up = modifiers
            .Where(IsDown)
            .Select(KeyUp)
            .ToArray();

        if (up.Length > 0)
        {
            Send(up);
            Thread.Sleep(30);
        }
    }

    private static bool IsDown(ushort vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private static void Send(INPUT[] inputs)
    {
        var sent = SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            // Usually means a more-privileged window (UAC, elevated app) owns the
            // foreground and is refusing our input. Caller treats this as a failure.
            throw new InvalidOperationException($"SendInput delivered {sent} of {inputs.Length} events.");
        }
    }

    private static INPUT KeyDown(ushort vk) => Build(vk, 0);

    private static INPUT KeyUp(ushort vk) => Build(vk, KEYEVENTF_KEYUP);

    private static INPUT Build(ushort vk, uint flags)
    {
        var scan = (ushort)MapVirtualKey(vk, MAPVK_VK_TO_VSC);
        if (vk is VK_LWIN or VK_RWIN) flags |= KEYEVENTF_EXTENDEDKEY;

        return new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = scan,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero,
                },
            },
        };
    }
}
