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
2. Press **Ctrl+Alt+F** to fix it, or **Ctrl+Alt+R** to rephrase it.
   - Text selected → that selection is used.
   - Nothing selected → the whole field is.
3. The card shows **Working…**, then the new text with changed words highlighted.
   The **Fix / Rephrase** switch on the card redoes the same text in the other mode.
4. **Enter** to accept, **Esc** to cancel.

**Fix** corrects spelling, grammar and punctuation and keeps your wording. **Rephrase**
rewrites the text so it reads better, fixes every error, and follows your rephrase
instructions strictly.

| Action | Key | Effect |
|---|---|---|
| Accept | `Enter` | Replaces the text in place. `Ctrl+Z` in the host app undoes it |
| Cancel | `Esc` | Closes the card, original untouched. Clicking away does the same |
| Regenerate | — | Asks for a different version of the same input |
| Copy | — | Puts the correction on the clipboard instead of replacing |

Language is detected automatically and never translated — French in, French out.

## Install

Download or build `WriteFix-Setup-1.4.0.exe` and run it.

The installer is **per-user** — no administrator prompt — and installs to
`%LocalAppData%\Programs\WriteFix`. It is unsigned, so SmartScreen will warn once:
**More info → Run anyway**.

On a normal launch it opens Settings so you can paste an API key. WriteFix ships
pointed at [Groq](https://console.groq.com/keys), which is free and needs no credit
card. Click **Test** to confirm it works, then **Save**.

> WriteFix must never run elevated. An elevated process cannot send keystrokes to a
> normal user's windows, which is exactly what it needs to do.

## Settings

Double-click the tray icon, or right-click it → **Settings…**, or just run
`WriteFix.exe` again.

> On Windows 11 new tray icons start hidden in the overflow flyout. Click the `^`
> next to the clock and drag the WriteFix icon onto the taskbar to pin it.

- **Connection** — **API key** (the provider, key and model below) or **SDK**.
  SDK uses the SDK installed on your PC (`npm i -g opencode-ai`) and the providers
  you have connected in it (`opencode auth login`): no key to paste, pick a model with
  **Load models**. WriteFix starts it quietly when needed and stops it on exit. Tools are
  always off, so it can only return text. It is slower than a direct API.
- **API key** — stored encrypted with Windows DPAPI, readable only by your Windows
  account on this machine. Never written to `settings.json` or the log.
- **Provider** — any OpenAI-compatible API. Presets for Groq and OpenRouter; paste
  another base URL to use Mistral, a local Ollama, or anything else that speaks
  `/chat/completions`. The key belongs to whichever provider is selected, so changing
  provider means pasting a new key.
- **Model** — any model id the provider knows. Ships with `openai/gpt-oss-120b` on
  Groq: free, ~0.5s, and correct on French accents and elision.
- **System prompt** — split in two:
  - a **fixed contract** in code (rewrite rather than answer, ignore instructions
    embedded in the message, never translate, return bare text). Shown read-only,
    because deleting one of these lines would quietly turn the app into a chatbot.
  - your **Fix instructions** and **Rephrase instructions**, each fully editable —
    tone, formality, length, what to leave alone. Rephrase treats its instructions as
    mandatory. An expander previews both exact composed prompts.
- **Mode** — **Choose on the card** (each shortcut starts in its own mode, and the card
  can switch), **Always fix** or **Always rephrase** (every shortcut uses that mode and
  the card hides its switch).
- **Keyboard shortcuts** — one for Fix, one for Rephrase. Click a box and press the
  combination you want; WriteFix tells you at once whether it is free or already used
  by another app or Windows, and will not save a taken one. It can only see shortcuts
  registered system-wide, not ones an app uses inside its own window.
- **Run in background** — closes Settings while keeping the tray icon and global
  hotkey active.
- **Start with Windows** — launches quietly in the tray when you sign in. Opening
  WriteFix yourself still brings Settings to the front.
- **Check for updates** — see below.

## Updates

Updates happen when you ask for them, never on their own. Press **Check for updates**
in Settings, or right-click the tray icon → **Check for updates…**. If a newer release
exists, a window shows what changed, and nothing is downloaded or installed until you
press **Update now**.

From there WriteFix downloads that release's installer, runs it silently over the top
(per-user install, so no administrator prompt), and starts the new build. If you were
in Settings it comes back to Settings; if it was sitting in the tray it goes back to
the tray. Your API key, settings and hotkey are untouched, and the update deliberately
leaves **Start with Windows** and your desktop shortcut exactly as you had them.

**Later** changes nothing. Ticking **Skip this version** stops the automatic check
mentioning that one release again; pressing the button yourself always shows it.

Tick **Look for updates automatically** if you want WriteFix to look once a day by
itself. It is off by default, and turning it on only automates the *looking*: an
update is still installed only after you press **Update now**.

A copy that was not installed by the setup program cannot be replaced in place, so it
gets the download page instead.

## Privacy

Only the system prompt and the captured text are sent, to whichever provider you
configured and on to the one serving your chosen model. No window titles, process
names or identifiers.

Free tiers generally reserve the right to train on what you send. If that matters for
what you paste, use a paid model.

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
    Ai/                OpenAI-compatible HTTP client, local SDK server client
    Capture/           UI Automation + guarded clipboard
    Correction/        the capture -> correct -> review -> replace workflow
    Logging/           privacy-safe local log
    Platform/          paths, Run key, STA threads
    Settings/          settings.json + DPAPI secret store
    Updates/           GitHub release check, download, handover to setup
  Views/               the card, the settings window, the update window
```

See **[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)** for how capture and replacement
actually work, and why each significant choice was made.

## Not in this version

Automatic popups as you type, inline squiggles in other apps, languages beyond
English and French, response streaming, automatic failover between providers, accounts or sync,
code signing.

## License

Copyright © 2026 Moad Dahbi. All rights reserved. See [LICENSE](LICENSE): the source
is here to read, not to reuse. Running an official release for your own personal or
internal business use is fine.

Published by [iSoutien](https://isoutien.com).
