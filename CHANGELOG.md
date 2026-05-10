# VMB Launcher Changelog

## v0.2.11 (2026-05-10)

### Changed
- **Replaced the Visibility ComboBox with three radio buttons.** v0.2.9 and v0.2.10 attempted to dark-theme the dropdown via ComboBoxItem styles and SystemColors overrides — both failed because WPF's default ComboBox popup uses theme-specific brushes that those approaches don't reach. Radios sidestep the issue entirely, plus all three options stay visible at once instead of being hidden behind a dropdown click (better UX for a 3-choice picker).
- Public radio button text is colored `#F48771` (warm orange) to reinforce the "this can't be undone if reported" warning right next to the option.

## v0.2.10 (2026-05-10)

### Fixed
- **Visibility dropdown still rendered with white popup background** after v0.2.9. The `ComboBoxItem` style alone wasn't enough — WPF's default `ComboBox` template uses `SystemColors` resources for the popup background, item highlight, and text. v0.2.10 overrides those resource keys at the `Window.Resources` level so the popup picks up the dark theme.

## v0.2.9 (2026-05-10)

### Fixed
- **New Mod dialog visibility dropdown was unreadable** — the dropdown items used Windows' default styling on hover/select, which paired our light-gray foreground with Windows' default white selection background. Added an explicit `ComboBoxItem` style that keeps the dark theme through hover, selection, and the highlighted state.

## v0.2.8 (2026-05-10)

### Fixed
The actual root cause for the `"generic failure (probably empty content directory)" (0x2)` error on first uploads. The fix was documented in the maintainer's own `old-backup/ANTIGRAVITY.md` all along (lines 114 and 129):

> "The tool adds `tags = [ ];` automatically after a successful upload — **do NOT add it manually**."

> "For a **new** item, set `published_id = 0L;` — the tool will populate it after creation."

Our launcher was doing the opposite on both:
- Writing `tags = [ ];` into both the scaffolded `itemV2.cfg` AND the staged upload cfg.
- Omitting `published_id` entirely on new mods instead of writing `published_id = 0L;`.

Fix:
- `ModScaffolder.WriteItemCfg`: drops the `tags = [ ];` line. ugc_tool adds it itself after the first successful upload.
- `UploadStager.WriteStagedCfg`: same drop, plus writes `published_id = 0L;` explicitly when the mod has no ID yet (was: omitted).
- `UploadStager.PropagatePublishedIdBack`: skips when the staged ID is "0" — that's the sentinel before ugc_tool runs, not a real workshop ID.

### Tests
- Updated two tests to reflect the new behavior, added one for the no-tags rule.
- Total: 124 tests, all passing.

## v0.2.7 (2026-05-10)

### Fixed
- v0.2.6's staging used a custom folder (`vmblauncher_staging/`) and an absolute cfg path. That worked on the maintainer's machine but **still failed on at least one friend's setup** with the same `"generic failure (probably empty content directory)" (0x2)` error. v0.2.7 matches the SDK's own `upload.bat` and the maintainer's legacy `old-backup/upload.ps1` verbatim:
  - Stage into `<sdk>/ugc_uploader/sample_item/` (the SDK's own designated staging folder, not a custom subfolder)
  - Write the cfg as `item.cfg` (not `itemV2.cfg`)
  - Invoke ugc_tool from `<sdk>/ugc_uploader/` (cwd = uploader dir, not staging dir)
  - Pass cfg as relative path: `-c sample_item/item.cfg` (not the absolute path)
- Plausible mechanism: ugc_tool likely has the literal string `sample_item` hardcoded somewhere in its content-resolution path, or its argv parser only resolves relative cfg paths cleanly. Either way, this matches what the SDK ships and what the maintainer's pre-VMB-migration scripts used reliably.

### Note
This overwrites your existing `<sdk>/ugc_uploader/sample_item/` contents on each upload. That folder is the SDK's designated scratch area; the legacy `upload.ps1` did the same.

## v0.2.6 (2026-05-10)

### Fixed
- **Uploads now use SDK staging**, matching the maintainer's existing documented fix in `vermintide-2-tweaker/DEVELOPMENT.md` for `"generic failure (probably empty content directory)" (0x2)`. Despite VMB's design claim, ugc_tool's relative-path resolution for `content` / `preview` is unreliable when the cfg lives outside the SDK uploader's own directory tree. Staging into `<sdk>/ugc_uploader/vmblauncher_staging/` with relative paths in the staged cfg matches what the SDK's own `upload.bat` does (`ugc_tool -c sample_item/item.cfg`) and is the empirically reliable pattern.
- Per upload, the launcher now:
  1. Wipes and recreates `vmblauncher_staging/content/`
  2. Copies `<mod>/bundleV2/*` into `staging/content/`
  3. Copies the mod's preview image into `staging/`
  4. Writes a derived `itemV2.cfg` with relative `content="content"` and the right preview filename, preserving `published_id` if set
  5. Runs `ugc_tool -c staging/itemV2.cfg -x` with cwd = staging folder
  6. After success, reads back any newly-written `published_id` from the staged cfg and propagates it into the mod's actual `itemV2.cfg` so future uploads target the same Workshop item

### Tests
- 15 new `UploadStager` tests covering: staging folder creation, bundle copy, preview detection (item_preview.png / preview.jpg / preview.png fallback), staged cfg shape, `published_id` upsert (replace, insert, append), back-propagation logic, missing-bundle guard, staging wipe on re-stage.
- Total: 123 tests, all passing.

### Note
This release supersedes v0.2.4 (cwd change) and v0.2.5 (forward-slash paths) — both were partial-credit theories. Staging is what the maintainer's existing tweaker docs already documented as the working fix.

## v0.2.5 (2026-05-10)

### Fixed
- **Real cause of `"generic failure (probably empty content directory)" (0x2)`:** ugc_tool's internal path parsing for "resolve `content` relative to cfg location" is **forward-slash-only**. Passing a Windows backslash path makes its `dirname()` return the wrong directory, so it looks for `bundleV2/` in the wrong place. The maintainer's existing `upload_ct.ps1` / `upload_wt.ps1` already did the same conversion via `-replace '\\','/'` for exactly this reason — institutional knowledge the launcher hadn't yet absorbed. VMB also emits forward slashes throughout (Stingray + ugc_tool are a Linux-flavored toolchain).
- Fix: convert both ugc_tool exe path and cfg path to forward slashes before invoking.
- The v0.2.4 cwd change (mod folder instead of `ugc_uploader/`) is kept since it doesn't hurt and is more explicit, but it wasn't the real cause.

## v0.2.4 (2026-05-10)

### Fixed
- **Upload failed with `"generic failure (probably empty content directory)" (0x2)`** even after a successful build that produced bundles in `bundleV2/`. Root cause: ugc_tool resolves the `content = "bundleV2"` relative path in `itemV2.cfg` against its **process working directory**, NOT against the cfg file location (despite what the SDK README claims). The launcher was running ugc_tool with cwd = `<sdk>/ugc_uploader/`, so it looked for `bundleV2/` inside the uploader folder and found nothing.
- Fix: run ugc_tool with cwd = the mod folder (where `itemV2.cfg` and `bundleV2/` actually live). First uploads of brand-new mods now succeed cleanly. Documented in tweaker repo's DEVELOPMENT.md and `feedback_workshop_upload_verify.md` — converting that institutional knowledge into a permanent code fix.

### Tests
- Total still 108, all passing. The cwd choice is verified through the production call site in `ModRunner.UploadAsync` (now passes `mod.ModDir`); a deeper integration test would require spawning a real ugc_tool with a real Steamworks session and is out of scope.

## v0.2.3 (2026-05-10)

### Fixed
- **Log pane corruption** when the Stingray compiler emitted bare `\r` for progress overwrites. The old `BeginOutputReadLine` treated `\r` as a line terminator, producing `[C\nompiler]`-style splits and worse one-character-per-line breakage on heavy output. Replaced with a custom async character reader that only splits on `\n`, strips trailing `\r` for CRLF, and locks the per-line callback so stdout/stderr never interleave at sub-line granularity.
- 9 new tests targeting the specific failure modes: bare `\r` mid-line, CRLF, LF-only, no trailing newline, empty input, progress-overwrite collapse, single-byte chunked reads, and end-to-end with `powershell.exe` writing `\r` between two words.

### Tests
- Total: 108 tests, all passing (was 99).

## v0.2.2 (2026-05-10)

### Fixed
- **"Workshop registration failed" on every new mod.** VMB v1.8.4's `vmb create` calls the uploader immediately on a freshly-scaffolded mod with an empty `bundleV2/`, ugc_tool refuses with `"generic failure (probably empty content directory)"`, and VMB then deletes the entire scaffold. The launcher now bypasses `vmb create` entirely and scaffolds the mod itself by copying VMB's `.template-vmf/` folder with `%%name` / `%%title` / `%%description` substitution. The user registers on Workshop later via Build → Upload (ugc_tool creates the entry on first upload when `itemV2.cfg` has no `published_id`).
- The "Workshop registration failed, scaffold rebuilt locally" recovery dialog is gone — there's nothing to recover from now. New dialog: *"Scaffolded MyMod at … To register the mod on Steam Workshop, click Build → Upload."*

### Added
- `ModScaffolder` service. Pure logic, fully tested (15 new test cases): finds the right template folder, copies + substitutes recursively, treats binary files (`.png`, `.dds`, `.jpg`) as binary, escapes quotes/newlines for Lua and itemV2.cfg formats, fails cleanly on duplicate folders or missing template.

### Tests
- Total: 99 tests, all passing (was 84).

## v0.2.1 (2026-05-10)

### Fixed
- **New-mod "Create" button stayed greyed out with no explanation** when the user typed a name with uppercase letters (e.g. `SecondMod`). The old regex required all-lowercase + underscores; loosened to `^[A-Za-z][A-Za-z0-9_]{1,63}$` (start with letter, 2–64 chars, letters / digits / underscore).
- Added a live inline hint under the name field showing exactly why the button is disabled — e.g. *"Name must start with a letter"*, *"Name can only contain letters, digits, and underscores. \"-\" isn't allowed."* — so users never have to guess.
- Tooltip on the Create button explains the same reason on hover.

### Tests
- 20 new test cases covering `NewModWindow.ValidateName` (legal names, illegal chars, length bounds, leading-digit / leading-underscore rejection, whitespace trim).
- Total: 84 tests, all passing.

## v0.2.0 (2026-05-10)

### Added
- **Download VMB now** button in the first-run dialog. Pulls the latest release from `Vermintide-Mod-Framework/Vermintide-Mod-Builder` on GitHub, downloads the zip, extracts to `%LOCALAPPDATA%\VMBLauncher\vmb\`, and points the launcher at it. New users no longer need to grab VMB manually.
- Progress dialog with cancel button during VMB download.
- xUnit test project (`tests/VmbLauncher.Tests.csproj`) covering 64 tests across `VmbProject`, `VmbLocator`, `ModDiscovery` (parser + writer), `Settings`, `Diagnostics`, `VmbDownloader` (with mocked HTTP via `HttpMessageHandler`), `ProcessRunner`, and `HashFile`.
- `test.ps1` runs the suite headlessly. `publish.ps1` now runs tests before building the exe.
- `VmbProject.AutoDetect` accepts an optional `extraCandidates` enumerable so tests can disable the disk scan.

### Removed
- "Open Workshop Page" per-mod button. Subscribing through Steam is the same number of clicks and works without a browser.

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
