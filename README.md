# VMB Launcher

A friendly Windows GUI for the Vermintide 2 mod build / deploy / upload pipeline. Wraps Vermintide Mod Builder + ugc_tool so you don't have to memorise PowerShell incantations or hand-edit configs.

![Workflow: Build, Deploy, Upload — one click each](https://img.shields.io/badge/platform-windows-blue) ![C#](https://img.shields.io/badge/.NET-9.0-512BD4) ![License](https://img.shields.io/badge/license-MIT-green)

## Download

Grab the latest `VMBLauncher.exe` from the [Releases page](https://github.com/Ensrick/vmb-launcher/releases). It's self-contained — no .NET install required.

> **Heads up:** the exe is unsigned, so on first launch Windows SmartScreen will say "Windows protected your PC". Click **More info** → **Run anyway**. Standard for any unsigned hobbyist tool.

## What it solves

The official VT2 modding workflow is several command-line tools chained together. There are footguns:

- Steam doesn't auto-subscribe you to your own Workshop uploads, so deploys silently fail until you manually subscribe.
- `vmb create` deletes your whole scaffold if the Workshop registration fails (Steam offline, missing SDK, etc).
- `ugc_tool` says "Upload finished" even when 0 bytes transferred.
- `bash -c "echo y | ..."` workarounds for the EULA prompt can trigger the Windows WSL installer if Git Bash isn't on PATH.

This tool handles all of that.

## What it does

- **Auto-detects** VMB, Steam, the VT2 SDK, `ugc_tool.exe`, and the Workshop content folder. Most users never touch the Settings dialog.
- **First-run setup** highlights anything missing with one-click fixes (auto-detect, browse, install SDK via Steam, subscribe in Steam).
- **Build / Deploy / Upload** buttons per mod. Plus a "Build + Deploy + Upload" combo for the common case.
- **Headless CLI** — same binary, run from PowerShell or cmd with verbs (`list`, `info`, `doctor`, `build`, `deploy`, `upload`, `all`). Real exit codes, streamed output, no second binary to install. See [Headless mode](#headless-mode) below.
- **Hash-verified deploys** so a half-flushed bundle never lands in the Workshop folder.
- **`--cwd` workflow supported** — projects with `.vmbrc` outside the VMB folder work without manual reconfiguration.
- **New-mod wizard** runs `vmb create` with pre-flight checks. If VMB deletes the scaffold after a failed Workshop registration, the wizard rebuilds the folder locally so nothing is lost.
- **Visibility-public guard** with explicit confirmation. Public mods that get reported are removed irreversibly; the tool makes you confirm every time.
- **Settings + Diagnostics** dialog for power users who want to override paths or just see what was detected.

## Headless mode

> **Known issue (2026-05-14):** the staging pipeline used by `upload` and `all` silently fails for established Workshop items — `ugc_tool` prints `Upload finished` and exits 0, but `file_size` on the Workshop page doesn't change. Build / Deploy / List / Info / Doctor are unaffected. Use direct-call `.ps1` wrappers for upload until the staging path is fixed. See [`CLAUDE.md`](./CLAUDE.md) and [`CHANGELOG.md`](./CHANGELOG.md) for the full write-up.

Any non-empty args (other than `--gui`) put the same binary into CLI mode:

```powershell
& .\VMBLauncher.exe list                              # list discovered mods
& .\VMBLauncher.exe info     my_mod                   # cfg + bundle state
& .\VMBLauncher.exe doctor                            # diagnostics
& .\VMBLauncher.exe build    my_mod [--clean]
& .\VMBLauncher.exe deploy   my_mod
& .\VMBLauncher.exe upload   my_mod [--allow-public]
& .\VMBLauncher.exe all      my_mod [--clean] [--allow-public]
& .\VMBLauncher.exe help                              # full reference
```

Exit codes:

| Code | Meaning |
|------|---------|
| 0    | Success |
| 1    | Command failed at runtime |
| 2    | Bad usage — missing verb / mod / `--allow-public` on a public mod |
| 3    | Preflight failed — diagnostics blocked the action (run `doctor` to see why) |

Useful flags:

- `--no-banner` — suppress the version banner (good for piping)
- `--config <path>` — alternate settings file location
- `--gui` — force GUI even with other args present

Watch out for the **PowerShell pipeline-truncation quirk**: when you pipe through `Select-Object -First N`, PowerShell sets `$LASTEXITCODE = -1` regardless of the program's actual exit code. Capture into a variable before truncating:

```powershell
$lines = & .\VMBLauncher.exe list
$code  = $LASTEXITCODE     # this is the real exit code
$lines | Select-Object -First 5
```

For the full doctrine — when to use the headless CLI vs the legacy `.ps1` wrappers, preflight gates per verb, output streaming, broken-pipe behaviour — see [`CLAUDE.md`](./CLAUDE.md).

## Requirements

- Windows 10 or 11
- Steam, plus a Vermintide 2 license
- Vermintide Mod Builder ([github](https://github.com/Vermintide-Mod-Framework/Vermintide-Mod-Builder) — binary release recommended; Node-based clones also supported)
- Vermintide 2 SDK (install via Steam: Library → Tools → "Vermintide 2 SDK")

The launcher will tell you exactly which of these is missing on first launch and give you a button to fix it.

## Building from source

```powershell
git clone https://github.com/Ensrick/vmb-launcher.git
cd vmb-launcher
.\publish.ps1
```

`publish.ps1` runs the test suite first, then builds. Output: `bin/Release/net9.0-windows/win-x64/publish/VMBLauncher.exe` (~60 MB).

For development:
```powershell
dotnet run -c Debug   # launch the GUI in debug mode
.\test.ps1            # run xUnit tests headlessly
```

### Tests

124 unit tests across all service classes (run via `test.ps1`). The downloader is fully tested with a mocked `HttpMessageHandler`, so it never hits the live GitHub API in CI.

Plus an end-to-end **headless smoke suite** at `tests/headless_smoke.ps1` that exercises the real binary against the real filesystem: every verb, every error path, exit codes through cmd's pipe, truncated-pipe handling, settings auto-detect, and GUI fallback. Runs automatically as part of `publish.ps1`.

```powershell
.\test.ps1                        # unit tests
.\tests\headless_smoke.ps1        # end-to-end against Debug build
```

## Settings file location

`%APPDATA%\VMBLauncher\settings.json`. Delete it to re-trigger the first-run setup flow.

## License

MIT — see [LICENSE](LICENSE).
