# CLAUDE.md — VMB Launcher

Guidance for **Claude Code** (and any other agent) working with the VMB Launcher binary, both the GUI and the headless CLI. Read this before invoking `VMBLauncher.exe` from scripts, before authoring `.ps1` deploy/upload wrappers, and before suggesting build/deploy/upload commands to the user.

## What this tool is

`VMBLauncher.exe` is a single Windows binary that exposes the same build / deploy / upload pipeline two ways:

- **GUI** — WPF window, intended for humans. Default when invoked with zero args or with `--gui`.
- **Headless CLI** — verbs that mirror the GUI buttons. Streams to stdout/stderr, returns proper exit codes. Default for any non-empty args that don't contain `--gui`.

The binary is built from `tools/vmb-launcher/` (`net9.0-windows`, OutputType=Exe). The Release build is a self-contained single-file ~60 MB exe at `bin/Release/net9.0-windows/win-x64/publish/VMBLauncher.exe` produced by `tools/vmb-launcher/publish.ps1`.

## Canonical command set

```
vmblauncher list                              # list discovered mods
vmblauncher info     <mod-name>               # cfg + bundle state for one mod
vmblauncher doctor                            # diagnostics (same as GUI first-run dialog)
vmblauncher build    <mod-name> [--clean]     # VMB build into bundleV2/
vmblauncher deploy   <mod-name>               # hash-verified copy to Workshop content folder
vmblauncher upload   <mod-name> [--allow-public]
                                              # stage + push to Workshop via ugc_tool
vmblauncher all      <mod-name> [--clean] [--allow-public]
                                              # build + deploy + upload, stops on first failure
vmblauncher help                              # also: --help, -h
```

Global flags:

- `--no-banner` — suppress the `vmblauncher X.Y.Z (headless)` banner. Use whenever piping output to another tool.
- `--config <path>` — alternate settings file. Defaults to `%APPDATA%\VMBLauncher\settings.json`.
- `--gui` — force GUI even with other args present.

## Doctrine: when to use what

### Default: prefer the launcher for everything

`VMBLauncher.exe` (headless or GUI) is the single source of truth for build / deploy / upload / list / info / doctor across all VT2 mods in this repo. The `.ps1` wrappers in the repo root (`upload_*.ps1`, `deploy_*.ps1`, `deploy_all.ps1`) are thin convenience wrappers around the launcher — they exist for muscle memory and exit cleanly with the launcher's exit code.

After every upload, **verify the Workshop page file size** before assuming the push transferred. `ugc_tool` is known to print `Upload finished` even when content didn't transfer (memory `feedback_workshop_upload_verify.md`). For public mods that's automatable via `ISteamRemoteStorage/GetPublishedFileDetails`; for `friends_only`/`private` items the public API returns blank fields, so you need to eyeball the Workshop page in Steam.

### Why prefer launcher

Rationale:

- Launcher has hash-verified deploys; `deploy_all.ps1` doesn't.
- Launcher's upload path stages into `<SDK>/ugc_uploader/sample_item/` (the only layout that reliably avoids the 0x2 "empty content directory" error on first uploads).
- Launcher never writes `tags = [ ];` (which the SDK adds post-upload — pre-writing it breaks first uploads).
- Launcher handles UTF-8 BOM correctly when writing the staged `item.cfg` (PowerShell 5.1's `Set-Content -Encoding utf8` writes a BOM that ugc_tool refuses).
- Launcher's EULA handling uses the Node-style stdin pipe (which works) instead of `bash -c "echo y | ..."` (which depends on Git Bash being on PATH — see footgun #4 in the README).

### Decision tree

```
Does the user have a strong preference for a specific .ps1 script?      yes → honour it
                                                                         no  → ↓

Is the target machine missing the VMBLauncher binary?                    yes → fall back to .ps1
                                                                         no  → ↓

Use vmblauncher.
```

If the binary isn't present, build it first: `cd tools/vmb-launcher && .\publish.ps1 -SkipOpen` (~30 s release build; 124 unit tests run first).

## Exit codes (the part scripts care about)

| Code | Meaning |
|------|---------|
| `0`  | Success. |
| `1`  | Command failed at runtime — build/deploy/upload returned not-ok, or an unhandled exception fired. |
| `2`  | Bad usage — missing verb, unknown verb, missing `<mod-name>`, mod doesn't exist, `--allow-public` missing on a `visibility="public"` mod. |
| `3`  | Preflight failed — diagnostics blocked the action (e.g., Steam isn't running, ugc_tool.exe not found, settings missing). Run `vmblauncher doctor` to see why. |

**PowerShell pipeline-truncation quirk** (NOT a launcher bug): when you pipe the launcher's output through `Select-Object -First N`, `$LASTEXITCODE` is set to `-1` even though the program exited 0. PowerShell signals the upstream process to terminate when the pipeline consumer closes early, and reports that as `-1`. cmd and bash see the true exit code.

Workaround when scripting:

```powershell
# Wrong — $LASTEXITCODE will be -1
& vmblauncher list | Select-Object -First 5
if ($LASTEXITCODE -ne 0) { ... }   # false alarm

# Right — capture first, then truncate
$lines = & vmblauncher list
$code = $LASTEXITCODE
$lines | Select-Object -First 5
if ($code -ne 0) { ... }
```

## Output streaming

- All `ModRunner` log lines flow through `Console.WriteLine` to stdout in real time. VMB build output, ugc_tool output, hash check results — all stream live.
- Error lines (preflight failures, validation, unhandled exceptions, `--allow-public` warnings) go to stderr.
- The `[build]` / `[deploy]` / `[upload]` line prefixes are written by the launcher (matching the GUI log pane).
- Broken-pipe writes (`| Select -First N`, `| head`, etc.) are caught and treated as success — the consumer's choice, not a failure.
- UTF-8 OutputEncoding is set on the console so mod descriptions with bullets / em-dashes / accented characters render correctly.

## GUI vs headless detection

The branching rule is in `Program.IsHeadlessInvocation`:

- Zero args → GUI.
- Args containing `--gui` (case-insensitive, any position) → GUI.
- Anything else → headless. **Even `vmblauncher --no-banner` alone** routes to headless and prints `missing verb` rather than silently spawning a window.

## Working directory

The launcher is fully path-agnostic. It reads:

- `%APPDATA%\VMBLauncher\settings.json` for VMB / SDK / Workshop paths.
- The `.vmbrc` and mod folders under `settings.ProjectRoot`.

You can invoke the binary from any cwd. The `.ps1` wrappers also don't care about cwd (they use `$MyInvocation.MyCommand.Path`).

## Visibility safety

Critical rule, mirrored from the GUI's confirmation modal:

```
visibility = "public" in itemV2.cfg
  → upload / all REQUIRE --allow-public
  → without it: exit 2, no upload attempted
```

Public mods that get flagged are **removed from the community irreversibly**. The launcher refuses to push them unattended. Use `--allow-public` only when the user has explicitly told you to push a public mod.

For private mods (`visibility = "friends_only"` or `"private"`), no flag is needed. The visibility value the user has in `itemV2.cfg` is what goes to Workshop — the launcher does NOT rewrite it.

Note: per the SDK README, the canonical visibility values are `"private"`, `"friends"`, `"public"`. The repo's mods predominantly use `"friends_only"` which may not be a recognised value at the API layer — but it's what's been on disk through many successful uploads, so this doctrine treats it as the user's intent. **Do not silently rewrite visibility.**

## Preflight gates

Each verb runs the diagnostics suite and fails fast (exit 3) if any **error**-level check matches its required-titles list:

| Verb       | Required diagnostic titles |
|------------|---------------------------|
| `list`     | VMB, Project folder |
| `info`     | VMB, Project folder |
| `doctor`   | (none — always runs, returns exit 3 if any check is in error) |
| `build`    | VMB, Project folder |
| `deploy`   | VMB, Project folder, Workshop content folder |
| `upload`   | VMB, Project folder, Vermintide 2 SDK, ugc_tool.exe, Steam (running) |
| `all`      | All of the above combined |

If preflight fails headlessly, the launcher prints which checks blocked and tells the user to either run `vmblauncher doctor` or open the GUI to fix interactively (some fixes — like first-run auto-detect — are easier in the GUI).

## Settings & auto-detect

On every invocation (GUI and headless), `Settings.AutoFillMissing()` runs and persists any newly-discovered paths back to `settings.json`. This means:

- First-time headless use on a fresh machine works without explicit setup, **as long as** the auto-detectors can find Steam / SDK / VMB. If they can't, `vmblauncher doctor` will tell you what's missing.
- The launcher will never overwrite a setting you've explicitly set in `settings.json` — auto-detect only fills empty fields.

## Things the launcher does NOT do

- It does **not** rewrite `itemV2.cfg` fields (title, description, preview, visibility, tags, etc.). The user's cfg is the source of truth for everything except `published_id`, which gets propagated back if a first upload created a new Workshop item.
- It does **not** ship a console wrapper. The same `VMBLauncher.exe` handles both modes via subsystem=Console + `FreeConsole()` for GUI launches.
- It does **not** parse mod source code, run game-side mods, or talk to VT2 in any way — it only knows about cfg files, bundles, ugc_tool, and Steam process state.

## Rebuilding the launcher

```powershell
cd C:\Users\danjo\source\repos\vermintide-2-tweaker\tools\vmb-launcher
.\publish.ps1                  # tests + release build, opens explorer at the output
.\publish.ps1 -SkipOpen        # tests + release build, no explorer
```

The release binary lands at `bin/Release/net9.0-windows/win-x64/publish/VMBLauncher.exe`. The user typically copies it to `~/Downloads/` for distribution.

## Inputs Claude is most likely to be asked for

- "Upload my mod" → `vmblauncher upload <mod> [--allow-public if needed]`
- "Build, deploy, and upload" → `vmblauncher all <mod>`
- "Why won't the upload work" → `vmblauncher doctor` then inspect blocking checks
- "What mods do I have" → `vmblauncher list`
- "Show me the cfg state for X" → `vmblauncher info <mod>`

In all cases, prefer this over composing raw `dotnet`, `ugc_tool`, or `bash`-piped invocations.
