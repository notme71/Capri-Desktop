<p align="center">
  <img src="assets/chip-source.png" alt="Chip" width="40%" align="middle">
  <img src="assets/capri.png" alt="Capri" width="40%" align="middle">
</p>

# Capri

A small desktop companion for Windows: Capri, from *Cyberpunk: Edgerunners*, lives on your screen. She sits there, stretches, yawns, falls asleep, peeks in from the edge, sits on your windows, gets head pats, and plays tricks on your screen. If you connect an AI model she also talks to you, and can look at your screen when you ask.

Beware that Capri may try to take over your human CPU. No responsibility is taken for her actions.

By default everything runs locally. There is no account and no telemetry, and the only thing she talks to is a model server on your own machine. You can optionally connect a cloud model with your own API key (see below); that is off until you switch it on.

<p align="center">
  <img src="docs/sit.png" alt="Capri sitting" height="220">
  <img src="docs/pat.png" alt="Capri getting a head pat" height="220">
</p>
<p align="center">
  <img src="docs/hack-note.png" alt="A hacked note" width="70%">
</p>

## Download

The latest release is **V2.4**. Get `Capri-Setup.exe` from the [Releases](../../releases) page and run it. Windows may show a SmartScreen warning because the file is not code-signed; the source is all here, and you can build it yourself (below).

## Quick answers

**Is it safe? Is it a virus?** No. The whole source is in this repository, so you can read it, and you can build it yourself with `build.bat` (below), which uses the compiler already built into Windows. If you would rather not trust the download, that is the way. What she does and does not do:
- No keyboard or mouse hooks, and no logging of what you type.
- No account and no telemetry. A screenshot is only taken when you ask her to look, or if you switch on dares that look (off by default, and it asks first). It stays in memory and is never saved.
- She only goes on the internet for things you turn on: a cloud model with your own key, "Play X on YouTube" (one request to youtube.com), or Chat, **Download better ears…**, which asks first and then downloads two fixed files from github.com and huggingface.co. Chat, memory and everything else stay on your computer.
- Windows may warn about the download because the file is not code-signed. That is the only reason it is flagged, not a sign of anything wrong. On the warning, click **More info**, then **Run anyway**. If you downloaded the zip, right-click it, choose **Properties** and tick **Unblock** before you unzip it.
- She installs for your Windows user only, no admin needed, and **Remove Capri** in the installer takes her back out.

**How do I install her?** Download `Capri-Setup.exe` (or unzip the release zip first) and run it. That is all.

**She doesn't hear me, or hears the wrong thing.** She starts with Windows' own speech recognizer, which is basic and only understands the speech packs installed in Windows (usually just your Windows language). For better hearing, add Whisper (next question). Also pick your microphone in her menu under Chat, Microphone instead of "Windows default", and check that Windows allows desktop apps to use it (Settings, Privacy & security, Microphone). She now says what is wrong when she can: "I can't hear any sound at all" means the microphone is silent or blocked, and "Whisper isn't working" means your Whisper files are set up wrong. Every attempt is also written to `%LocalAppData%\Capri\voice-log.txt` with the reason (no recording and no words, only numbers and errors). If it is still a mystery, open an issue and paste the last few lines of that file.

**Which Whisper do I need?** The easy way: in her menu choose Chat, **Download better ears…**. She asks first, then downloads Whisper (about 9 MB) and a model that understands many languages (about 190 MB), checks both against known fingerprints and sets them up. Or do it by hand. Whisper is optional and runs locally; nothing is sent anywhere. She needs two things in `%LocalAppData%\Capri\speech`:
- `bin\Release\whisper-cli.exe`, from a [whisper.cpp](https://github.com/ggml-org/whisper.cpp) Windows build (`bin\whisper-cli.exe` also works).
- A model file in `models`. Use **`ggml-small.en.bin`** (best choice here, about 470 MB) or **`ggml-base.en.bin`** (faster and smaller, less accurate). Both are English-only models. You can get them from the [whisper.cpp models on Hugging Face](https://huggingface.co/ggerganov/whisper.cpp). If neither is there she uses any other `ggml-*.bin` she finds in that folder.

The Chat menu shows which ears are active, "Whisper (small.en)" or "Windows' own". With Capri set to English she prefers the English models above, which are the most accurate for English. Set to any other language, she picks a multilingual model (the one from **Download better ears…**, or any `ggml-small.bin` / `ggml-base.bin` style model without `.en` in its name) and tells Whisper to listen in that language. Without a multilingual model she falls back to Windows' own recognizer, which has to have that language installed.

**How do I give her a voice?** The voice feature is very much a work in progress. If you still want to try it, see "Voice lines (optional)" below. Nothing is bundled: you add your own `.wav` clips, and her menu has Voice, **Open voice folder…**. No ElevenLabs or any other service is needed.

**Which model do I need? Is it CUDA only?** No. She does not run the AI herself: you connect a model to her through Ollama or LM Studio on your own computer, or through a cloud model with your own API key (Chat, Cloud model settings). Whatever those can run on your setup works, and the cloud option needs no GPU at all. See "Setting up her brain" below.

**Is there a Linux or Mac version?** No. She is a Windows app.

**Which languages?** English, Polski, Français, Deutsch, Español, Italiano, Português, Русский, Čeština and Tiếng Việt. See "Languages" below. Voice input follows the language she is set to when she has a multilingual Whisper model (Chat, Download better ears…). Typed and spoken commands such as "set a timer for 10 minutes" are still English only.

## What she does

Most of Capri works with **no AI at all**: no model, no internet, nothing extra to install. The AI is only for open-ended conversation and for looking at your screen, and all of it is optional. Anything that needs a model falls back to her built-in lines, or tells you a model is needed.

### Works without any AI

**Alive on your desktop**
- Idle reactions: stretching, looking around, yawning, falling asleep, peeking from the screen edge. Each can be switched off.
- Head pats: hover over her, or click.
- She sits on the top edge of your windows and follows them when they move. Drop her on one to seat her.
- Steps aside while a full-screen game or video is in front, and comes back after.
- A tray menu for everything, in a black, crimson and green terminal style. **Start with Windows** is in it (off until you tick it).

**Mischief** (off by default; Ctrl+Alt+C stops it at once)
- Screen glitches in six styles: bars, tear, corruption, hex rain, a fake "ENGRAM SYNC" progress box, and a red cursor that chases yours. All of them are painted on a click-through overlay and stay gentle: low contrast, nothing flashes, and reduced motion in Windows turns them off.
- "You've been hacked" notes.

**She notices and dares** (no screen capture)
- She remarks, rarely, when you come back after a long break, stay up late, run low on battery, max out the CPU, or sit in one program for hours. She reads only the clock, idle time, CPU, battery and the name of the program in front.
- Dares: after about ten minutes in one program she may dare you with something quick and harmless for it ("Dare you to close every tab except one."). Say "dare me" any time, and "done" when you have. Without a model they come from her own list.
- **Sync:** a 0 to 100% meter of how far she has got into your life. It grows with time, chatting, pats, agreeing with her and finished dares (with daily limits), opens up new glitch styles, and is shown in her sync glitch box. Ask "how's our sync?".

**Errands** (typed or spoken, matched from plain words, no model needed)
- Timers, alarms and reminders: "set a timer for 10 minutes", "wake me at 7am", "remind me in 20 minutes to stretch", and a study timer. A finished timer interrupts whatever she was saying.
- "Open downloads", "open Discord", "open Edge": a short fixed list of folders and Windows tools, plus any program with a Start menu shortcut, found by name. "Open youtube", "open github" and a few sites open in your browser.
- "Pause the music", "skip", "previous song", "mute", "turn it up".
- "Search for cute cats on YouTube", "google X": opens your browser's results.
- **"Play X on Spotify":** she opens Spotify's search, finds the Play button for that song by name (through the same accessibility interface screen readers use, so it works in any language) and presses it, then checks that something new is really playing. If she can't find it, she writes `%LocalAppData%\Capri\spotify-buttons.txt` with what she saw.
- **"Play X on YouTube":** makes one request to youtube.com to find the first video, then opens it in your browser.
- Speaking these needs speech recognition (below), but not a chat model.

**What she remembers and says herself**
- "Remember that my dog is called Bo." Notes live in a plain text file (`%LocalAppData%\Capri\memory.txt`) that you can read or delete. She refuses passwords. "Forget about my dog", "forget everything" and the Chat, Memory menu manage it. The notes are kept without a model; she only brings them up in conversation when a model is on.
- Her real lines from the show for questions that have one ("who are you", "what are you", "are you an AI", and a few more).
- She shows what music or video is playing, with a short canned cheer.

### Needs an AI model

Local with [Ollama](https://ollama.com) or LM Studio, or optionally a cloud model with your own key (see below).

- **Chat:** a window in her style (Ctrl+Alt+T), or just talk: click the mic beside her, or press Ctrl+Alt+V, and she answers in a speech bubble.
- **Look:** press Ctrl+Alt+L or ask "what am I looking at?" or "what should I buy here?". She takes one screenshot, sends it to your vision model, and throws it away. It is only taken when you ask, held in memory only, and never written to disk. You can switch it off in Chat, "Let her look when I ask".
- **Dares about your screen** (off by default, and it asks first): one screenshot when she dares you, so the dare fits what is really on screen (a game, a page). It goes only to your local model, and to a cloud model only if you allowed screenshots there. She stays hidden in fullscreen games unless you switch off Hide in fullscreen.
- Dares and music comments written fresh by the model instead of from her list, using her notes about you when memory is on.
- Her tone changing with how far sync has got.

## Hotkeys

| Keys | What it does |
| --- | --- |
| Ctrl+Alt+C | Hop off the cursor, and stop any glitch |
| Ctrl+Alt+T | Open the chat window |
| Ctrl+Alt+V | Talk to her (click again to stop, or press DONE to send early) |
| Ctrl+Alt+L | Look at my screen |

## Setting up her brain

Chat, voice and Look use a model server on your own computer. [Ollama](https://ollama.com) is the easiest.

1. Install Ollama and pull a model. A vision-capable one (for example `qwen3.5:9b`) also lets her look at your screen. A text-only model works for chat.
2. In her tray menu choose Chat, then Use a local model. She finds Ollama or LM Studio on the usual local ports. Only `localhost` and loopback addresses are accepted.
3. Optional: put a `persona.txt` in `%LocalAppData%\Capri` to change how she talks.

### Cloud models (optional)

No GPU? Chat, Cloud model settings lets you use OpenRouter, OpenAI, Anthropic, or any OpenAI-style https address with **your own API key**. A Claude or ChatGPT subscription is not an API key; keys come from the provider's developer console and are billed separately (her replies are short, so a small model costs very little).

- Providers show a key only once, when you create it. Copy it then; if you lose it, make a new one. Free models on OpenRouter work with a $0 key. Some models think before answering and may feel slow or sometimes answer with nothing; she retries once and tells you.
- If a model says "the request was refused", she tries once more with plain settings and shows the provider's own message. Copy the model's exact id (on OpenRouter, the copy button next to its name; free ones end in `:free`) and pick one that is not labeled "thinking" or "reasoning", since those often answer with nothing.
- It is off until you switch it on. Your chat messages and her persona are sent to that company.
- Screenshots (Look) and her notes about you are **not** sent unless you tick their own boxes in the dialog. Without that box, Look still works if you also run a local vision model.
- The key is stored encrypted for your Windows account only (`%LocalAppData%\Capri\cloud.key`), never in the settings file.
- Only https addresses are accepted for the cloud.

### Better ears (optional)

The easy way is Chat, **Download better ears…** in her menu (it asks before it downloads anything, and shows the sizes). It fetches the Windows build of [whisper.cpp](https://github.com/ggml-org/whisper.cpp) release b5454 and the `ggml-small-q5_1.bin` model (about 190 MB) from Hugging Face, and only uses them if their size and SHA-256 match exactly. To do it by hand instead:

Install [whisper.cpp](https://github.com/ggml-org/whisper.cpp) and put it here:

```
%LocalAppData%\Capri\speech\bin\Release\whisper-cli.exe
%LocalAppData%\Capri\speech\models\ggml-small.en.bin
```

She uses it automatically when it is there (Chat menu shows which ears are active). `base.en` is faster and less accurate.

### Voice lines (optional)

Nothing is bundled. Put `.wav` clips in `%LocalAppData%\Capri\voices` named for an occasion (`pat.wav`, `hello-2.wav`, ...). A `README.txt` is written into that folder with the full list of names.

## Languages

Her menus, windows, installer and canned lines come in English, Polski, Français, Deutsch, Español, Italiano, Português, Русский, Čeština and Tiếng Việt. Pick one in the tray menu under Language (the installer also asks); it takes effect the next time she starts. The translations are machine-made and have not been checked by native speakers, so some wording may be off. Corrections are welcome.

With a language chosen, a model is also told to answer in it. How well that works depends on the model: small local models are weaker outside English. Her exact lines from the show, and typed or spoken commands ("set a timer for 10 minutes"), stay in English.

### Fix or add a language

Each language is one plain text file in `assets\lang`, named by its two-letter code (`fr.txt`, `pt.txt`). A line starting with `> ` is the English wording and the line under it is the translation:

```
> Hide Capri
Cacher Capri
```

Keep `{0}` and `{1}` as they are, write `\n` for a new line, and delete a pair to fall back to English. Wording with a number can have a singular and a plural form separated by `|`, such as `{0} minute|{0} minutes`.

- **To fix a translation:** edit the line and open a pull request or an issue. To try it without building, put a file with the same name in `%LocalAppData%\Capri\lang` (for example `fr.txt`); it overrides the built-in one, and only the lines you include.
- **To add a language:** copy `assets\lang\es.txt` to your code (for example `nl.txt`), translate the lines, then add its code and name to `Lang.Choices` in `app\Lang.cs`, plus its English name to `Names` there so the model is told which language to use. `build.bat` embeds every file in `assets\lang` automatically. Languages with unusual plural rules (Polish has three forms) need a rule in `Lang.Count`.

Or just open an issue with the language you want.

## Build it yourself

Windows with the .NET Framework 4 (already part of Windows 10 and 11). No other tools or packages.

```
build.bat
```

This builds `build\Capri.exe`, then `build\Capri-Setup.exe`, which contains `Capri.exe`. Quit Capri first if she is running from the build folder.

The art is embedded from `assets\art`. `tools\make-art.ps1` rebuilds those trimmed sprites from full-size frames, which are not kept in this repository (put them in `assets\originals` if you have them).

Her smoke test is built in: `Capri.exe --smoke-test <folder>` runs the checks and saves screenshots to the folder.

## Disclaimer

This is an unofficial fan project, not affiliated with or endorsed by CD PROJEKT RED or Studio Trigger, and it is not for sale. Capri, Chip, and all artwork and sprites used here belong to CD PROJEKT RED and Studio Trigger (*Cyberpunk: Edgerunners*). If you are a rights holder and want something changed or removed, open an issue and it will be dealt with.

## Credits

- Started from [lachydotmcg/capri-desktop](https://github.com/lachydotmcg/capri-desktop), which brought Capri to the desktop first. Inspired by jpartnma_'s TikTok video. Based on lachydotmcg's 0.2.0 release.
- Code added since then is under the [MIT license](LICENSE); the original code remains under its author's terms.
