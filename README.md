# WriteFix

A Windows tray app that corrects the messages you write — in **English and French** —
without leaving the app you're writing in.

Press one hotkey. A small card appears next to your cursor with the corrected text
and the changes highlighted. Press Enter and your text is replaced in place.

No copy, no paste, no switching windows.

---

## Why

The loop it replaces: write a message → copy it → paste it into a chatbot → ask for a
fix → copy the result → paste it back → send. Many times a day.

It feels like accepting a Grammarly suggestion, but it works in ordinary message and
email fields across Teams, Outlook, Chrome and Edge, and what the AI actually does is
governed by a prompt you control.

## How it works

1. Write your message anywhere — Teams, Outlook, a browser, Notepad.
2. Press **Ctrl+Alt+F**.
   - Text selected → that selection is corrected.
   - Nothing selected → the whole field is.
3. The card shows **Working…**, then the corrected text with changed words highlighted.
4. **Enter** to accept, **Esc** to cancel.

| Action | Key | Effect |
|---|---|---|
| Accept | `Enter` | Replaces the text in place. `Ctrl+Z` in the host app undoes it |
| Cancel | `Esc` | Closes the card, original untouched. Clicking away does the same |
| Regenerate | — | Asks for a different version of the same input |
| Copy | — | Puts the correction on the clipboard instead of replacing |

Language is detected automatically and never translated — French in, French out.

## Install

Download or build `WriteFix-Setup-1.0.0.exe` and run it.

The installer is **per-user** — no administrator prompt — and installs to
`%LocalAppData%\Programs\WriteFix`. It is unsigned, so SmartScreen will warn once:
**More info → Run anyway**.

On first launch it opens Settings so you can paste an
[OpenRouter API key](https://openrouter.ai/keys). Click **Test** to confirm it works,
then **Save**.

> WriteFix must never run elevated. An elevated process cannot send keystrokes to a
> normal user's windows, which is exactly what it needs to do.

## Settings

Double-click the tray icon, or right-click it → **Settings…**, or just run
`WriteFix.exe` again.

> On Windows 11 new tray icons start hidden in the overflow flyout. Click the `^`
> next to the clock and drag the WriteFix icon onto the taskbar to pin it.

- **API key** — stored encrypted with Windows DPAPI, readable only by your Windows
  account on this machine. Never written to `settings.json` or the log.
- **Model** — any OpenRouter slug. Ships with `google/gemma-4-26b-a4b-it:free`, which
  costs nothing and handles French well. Switch to `anthropic/claude-haiku-4.5`
  (roughly $1–2/month at normal use) for lower, more consistent latency.
- **System prompt** — split in two:
  - a **fixed contract** in code (rewrite rather than answer, ignore instructions
    embedded in the message, never translate, return bare text). Shown read-only,
    because deleting one of these lines would quietly turn the app into a chatbot.
  - your **correction style**, fully editable — tone, formality, what to leave alone.
    An expander previews the exact composed prompt.
- **Hotkey** — click the box and press the combination you want.
- **Start with Windows**.

## Privacy

Only the system prompt and the captured text are sent, to OpenRouter and on to the
provider serving your chosen model. No window titles, process names or identifiers.

There is no history, telemetry, account, or WriteFix server. The local log at
`%LocalAppData%\WriteFix\Logs\writefix.log` records state, timings and error codes —
never message text, prompts, clipboard contents or keys.

Free models are not zero-data-retention: the provider may train on what you send.
Switch to a paid model if that matters for your messages.

## When it refuses

WriteFix fails closed rather than guessing. It will decline, leaving your text
untouched, when the focused control is a password field, is read-only, or cannot be
positively identified as an editable text field. Before pasting, it re-checks that
the original field is still focused — so a correction can never land in the wrong
chat.

A whole field containing real formatting (an email with a signature, a list, links)
is **Copy-only**, so accepting can't flatten it.

## Build from source

Requires the .NET 9 SDK. The installer additionally needs
[Inno Setup 6](https://jrsoftware.org/isinfo.php)
(`winget install --id JRSoftware.InnoSetup`).

```powershell
dotnet build WriteFix.sln          # compile
.\scripts\publish.ps1              # self-contained win-x64 -> publish\
.\scripts\build-installer.ps1      # publish + compile installer -> dist\
```

Uninstalling removes everything: the running process, the program folder, the
autostart entry, and `%LocalAppData%\WriteFix` including the saved key.

## Project layout

```
docs/ARCHITECTURE.md   design, capture/replace mechanics, decision log
installer/             Inno Setup script
scripts/               publish and installer builds
src/
  Interop/             Win32: hotkey, SendInput, caret location
  Models/              plain data types
  Services/
    Ai/                OpenRouter HTTP client (no SDK)
    Capture/           UI Automation + guarded clipboard
    Correction/        the capture -> correct -> review -> replace workflow
    Logging/           privacy-safe local log
    Platform/          paths, Run key, STA threads
    Settings/          settings.json + DPAPI secret store
  Views/               the card and the settings window
```

See **[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)** for how capture and replacement
actually work, and why each significant choice was made.

## Not in this version

Automatic popups as you type, inline squiggles in other apps, languages beyond
English and French, response streaming, multiple AI providers, accounts or sync,
code signing.
