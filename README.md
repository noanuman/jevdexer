<p align="center"><img src="brand/mascot-render-256.png" width="128" alt="Jevdexer mascot"></p>

<h1 align="center">Jevdexer</h1>

<p align="center">A grandma friendly Windows search engine.<br>
Find apps, folders and files by name, or by what they are.</p>

<p align="center"><a href="https://jevdexer.vercel.app">Website</a> · <a href="https://jevdexer-releases.vercel.app/Jevdexer.App-win-Setup.exe">Download for Windows</a></p>

---

Press **Win + Alt + Space** anywhere and type. "steam" finds Steam. With Smart search on,
"that green messaging app" finds WhatsApp and "edit a video" finds your video editors, even if you
forgot what they are called.

Jevdexer is free, has no account and no server. It builds its own index on your PC and answers
every search locally first.

## Features

- **Apps and games** from the Start menu, Store apps, Steam, Epic, and other launchers, with icons
  and descriptions.
- **Folders and files** by name, kept current as files change.
- **Smart search** (optional): understands descriptions instead of names. It uses your own
  [Vercel AI Gateway](https://vercel.com/ai-gateway) key.
- **Ultra Search** (optional): search inside the folders you choose: text in documents, scans and
  screenshots (OCR), and what photos show. Everything is read and understood on your PC, on the CPU.

## Privacy

Without a key, nothing about your searches or files leaves your PC. What does go over the network,
and when:

| What | When | Where |
|---|---|---|
| App names and your search text | Smart search is on and a local match is not confident | `ai-gateway.vercel.sh`, with your key |
| Folder names and short scrubbed summaries (never file contents) | Smart search for folders or Ultra Search refinement is on | `ai-gateway.vercel.sh`, with your key |
| Public app descriptions | Indexing | `cdn.winget.microsoft.com` (download only) |
| Local AI models | You turn on photo and meaning search in Ultra Search | `huggingface.co` (download only, hash checked) |
| Update check | App start | `jevdexer-releases.vercel.app` |

- Your key is stored encrypted with Windows DPAPI and only sent to the AI Gateway.
- Summaries are scrubbed on your PC first (emails, links, phone numbers, long numbers, your user
  name). Folders you mark private never go online at all.
- Files are parsed by a sandboxed worker process (a Windows AppContainer) with no network and no
  access to anything but the file it is given.

## Build

Requires Windows 10 or 11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet build
dotnet test
dotnet run --project src/Jevdexer.App
```

Build an installer with [Velopack](https://velopack.io) (`dotnet tool install -g vpk`):

```powershell
./scripts/package.ps1 -Version 1.0.0
```

## Project layout

| Folder | What |
|---|---|
| `src/Jevdexer.App` | The WPF app: search window, settings, tour, tray |
| `src/Jevdexer.Core` | Index, search and ranking, file index, Smart search client, Ultra Search |
| `src/Jevdexer.Discovery` | Finding installed apps and games |
| `src/Jevdexer.Content` | Text extraction, OCR and local models (used by the worker) |
| `src/Jevdexer.Worker` | The sandboxed file reader |
| `src/Jevdexer.Cli` | Command line tools for indexing and testing |
| `website/` | The website |

## License

[MIT](LICENSE). The Jevdexer name and mascot are not covered by the license.

Made by [@NoaNuman](https://x.com/NoaNuman).
