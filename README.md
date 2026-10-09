# MultiAI Shell

A Windows desktop tool for running multiple AI coding-agent console sessions side by side in a configurable grid.

MultiAI Shell launches CLI coding agents (`opencode`, `claude`, `pi`, or any custom command) inside embedded console windows arranged in an adjustable grid — one project directory per cell — so you can supervise several agent sessions at once in a single window.

## Features

- **Session grid** — any layout from 1×1 up to 10×10 (presets for 2×2, 3×3, 4×4, …), rebuilt on demand.
- **Embedded consoles** — each session's console window is hosted directly inside the app (via Win32 `SetParent`), with clipboard paste (Ctrl+V or right-click), focus forwarding, and detach-to-window support.
- **Per-session configuration** — session name, working directory (with browsable saved-path history), and agent to run.
- **Launch All / Stop All** — start every stopped session with one agent selection from the toolbar, or stop everything at once.
- **Layout persistence** — the grid, session names, directories, and agent assignments are auto-saved on exit and restored on startup; saved paths and the agent list persist globally.
- **External terminal helpers** — open any session's directory in Explorer or a standalone terminal.

## Requirements

- Windows 10/11
- [.NET 10 SDK](https://dotnet.microsoft.com/download) (WPF workload)
- At least one CLI coding agent installed and on `PATH` (e.g. `opencode`, `claude`)

> **Note on embedding:** console embedding works with the legacy Windows Console Host. If Windows Terminal is your default terminal application, switch to *Windows Console Host* under
> *Settings → Privacy & security → For developers → Terminal* (or right-click a session and use *Detach to separate window* mode).

## Building

```sh
dotnet build -c Release
```

Then run `bin/Release/net10.0-windows/MultiAI.exe`, or publish a self-contained build:

```sh
dotnet publish -c Release -r win-x64 --self-contained
```

## Usage

1. Set the grid size in the toolbar (or click a preset) and press **Apply**.
2. In each cell, pick a working directory (type a path, browse, or pick a saved one) and the agent to run.
3. Press **▶** in the cell — or fill the toolbar agent box and hit **Launch All**.
4. Names, paths, and the layout are saved automatically when you close the app.

## Security notes

Agent names are validated against a strict whitelist (single command tokens only — no shell metacharacters) before they are ever passed to a command line, both when you add them and again at launch time, since they are interpolated into a `cmd.exe` invocation.

## License

Released under the [MIT License](LICENSE).

