# Gemini Launcher

A lightweight replacement for the Windows Start menu and search (Bing). Tap the **Windows key** or click **Start** on the taskbar, start typing, and you get:

- **Google autocomplete** as you type, with inline ghost completion (press **Tab** or **→** to accept)
- **Gemini answers** grounded in **Google Search**, with the **URL context** tool turned on. Paste a link and Gemini reads and summarizes the page.
- **Apps**: every app Windows knows about (desktop and Microsoft Store), with real icons
- **Files and documents**: instant results from the Windows Search index, matching file names and the words inside documents
- **Settings pages** (Wi-Fi, Bluetooth, Display…), a **calculator**, and **website** shortcuts

When you open it without typing, you see:

- **Pinned** apps, files, folders and websites. Right-click anything to pin it, or use **Add**.
- **Recommended**: the apps you use most (from Windows' own usage data) and your recent files
- **Searches**: your recent searches plus trending Google searches for your region

It looks like Windows 11: it slides up from behind the taskbar like Start, with a system acrylic backdrop, rounded corners, Segoe UI Variable, your accent color, and it switches live between light and dark mode. On Windows 10 it uses acrylic blur. If transparency effects are off in Windows, it uses solid colors.

## Install

1. Download `GeminiLauncher.exe`. It's a single ~260 KB file and needs nothing else (it uses .NET Framework 4.8, which is built into Windows 10/11).
2. Run it and click **Install**. It installs for your account only, so no admin rights are needed. It goes to `%LOCALAPPDATA%\Programs\GeminiLauncher`, adds a Start menu entry, registers in **Settings → Apps** for uninstalling, and starts with Windows.
   - Windows SmartScreen may warn you because the file isn't code-signed. Click **More info → Run anyway**.
3. Paste your Gemini API key in Settings. You can get a free one at [Google AI Studio](https://aistudio.google.com/apikey). The key is encrypted with Windows DPAPI, so only your Windows account can read it.

To run without installing, use `GeminiLauncher.exe --portable`.

## Replacing the Start menu (optional, on by default)

In **Settings → Replace the Start menu** (also in the tray icon menu):

| Switch | What it does |
|---|---|
| **Windows key opens this launcher** | A lone tap of the Windows key opens Gemini Launcher instead of Start. Win+E, Win+D, Win+Shift+S and other shortcuts keep working. |
| **Taskbar Start button opens this launcher** | Clicking the Windows logo on the taskbar opens Gemini Launcher instead of Start (all monitors). |

You can still open the **native Start menu** at any time: press **Ctrl+Esc**, **Shift+click** the Start button, or use the tray menu → *Open native Start menu*. Turn both switches off to get the native behavior back completely. Nothing in Windows is modified or patched, and closing the app restores everything.

**Admin apps:** Windows doesn't let normal apps see keys you press while an app running as administrator is focused, such as Task Manager. If you want the Windows key to work there too, turn on **Also work over admin apps**. It creates a sign-in task with admin rights, after one UAC prompt. Apps you launch from the launcher still run with normal rights.

## Keyboard

| Key | Action |
|---|---|
| Type | Search everything |
| ↑ / ↓, PgUp / PgDn | Move selection |
| Enter | Open the selected item. On a Google suggestion it asks Gemini, and pressing Enter again on a finished answer opens Google. |
| Tab / → | Accept the autocomplete |
| Ctrl+Enter | Open the query in Google in your browser |
| Ctrl+Shift+Enter | Run as administrator |
| Alt+Enter | Open file location |
| Ctrl+P | Pin or unpin the selected item |
| Menu key / right-click | More actions |
| Ctrl+, | Settings |
| Esc | Clear, then close |

## Gemini settings

- **Model**: `gemini-3.5-flash-lite` by default, because it's the fastest. `gemini-3.8-flash` gives richer answers. You can type any model name.
- **Thinking level**: Medium by default (Minimal / Low / Medium / High / Model default). On Gemini 2.5 models it's converted to a thinking budget.
- **Grounding tools**: Google Search, Google Maps and URL context are all on and sent together. If a model rejects a combination, the launcher retries without the conflicting tool, and the **Test** button tells you exactly what your model accepted.
  - Google Maps places appear as chips labelled with the place name and "Google Maps", linking to the place.
  - **Use my location for Google Maps** is off by default. Turn it on to send your approximate location (about 100 m) with requests, so "near me" questions work.
- **Safety filters**: off. Every adjustable category (harassment, hate speech, sexually explicit, dangerous content, jailbreak) is set to `OFF`. Google's built-in protections still apply.
- **Instant answers while typing**: when you pause typing and no app or file is an obvious match, Gemini answers automatically. Turn this off if you only want to ask when you press Enter. Google Search and Google Maps grounding each include 5,000 free queries per month on Gemini 3+ models; after that, Google bills them.

## Lightweight

- Single small exe, with no runtime to install, no background indexing (it uses the Windows Search index) and no browser engine
- Keyboard and mouse hooks run on their own thread, so the app can never slow down your typing
- Memory is trimmed after the launcher has been hidden for 20 seconds

## Uninstall

Go to **Settings → Apps → Installed apps → Gemini Launcher → Uninstall**, or run `GeminiLauncher.exe --uninstall`.

## Build from source

```
dotnet build src/GeminiLauncher.csproj -c Release
```

This works on Windows, macOS or Linux (it targets `net48` with `EnableWindowsTargeting`).
