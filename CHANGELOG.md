# VMB Launcher Changelog

## v0.1.0 (2026-05-10)

First release. A friendly Windows GUI for the Vermintide 2 mod build / deploy / upload pipeline.

### What it does
- **Auto-detects** VMB, Steam, Vermintide 2 SDK, ugc_tool.exe, and the Workshop content folder via Windows registry + filesystem scan.
- **First-run setup** highlights anything missing with one-click fixes (auto-detect, browse, install SDK via Steam, subscribe via `steam://`).
- **Build / Deploy / Upload** buttons per mod, plus a "Build + Deploy + Upload" combo.
- **Hash-verified deploys** so half-flushed bundles never reach the Workshop folder.
- **EULA prompt handled via stdin redirection** (no `bash`, no risk of triggering the WSL installer).
- **Visibility-public guard** with explicit confirmation. Public mods that get reported are removed irreversibly.
- **New-mod wizard** runs `vmb create` with pre-flight checks. If VMB deletes the scaffold after a failed Workshop registration, the wizard rebuilds the folder locally so nothing's lost.
- **Settings + Diagnostics** dialog with manual overrides if auto-detect picks the wrong path.
- **Subscribe-in-Steam / Open mod folder** quick links.
- **`--cwd` workflow supported** — projects that store mods alongside `.vmbrc` outside the VMB folder work without manual configuration.

### Distribution
Self-contained single-file Windows binary (`VMBLauncher.exe`, ~60 MB). No .NET install required on the user's machine. Unsigned, so SmartScreen will warn on first launch — click "More info" → "Run anyway".

### Known caveats
- **`ugc_tool` upload size verification is manual.** It prints "Upload finished" even on 0-byte transfers; the launcher logs a warning telling you to confirm the file size on the Workshop page.
- **First launch shows SmartScreen warning.** Unsigned exe; standard Windows behavior. Code signing would remove this but isn't free.
- **Hot-reload not addressed.** This launcher only handles build / deploy / upload. In-game hot-reload (Ctrl+Shift+R) is unrelated and may still crash for mods that hook unit creation.
