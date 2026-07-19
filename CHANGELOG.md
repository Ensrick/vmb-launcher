# VMB Launcher Changelog

## v0.5.6 (2026-07-18)

### Added: machine-global ship/version claim gate on `upload` / `all` (monorepo issue #724)

The monorepo's `tools/ship/claim.ps1` broker allocates a mod's next MOD_VERSION into a per-checkout `.ship_claims/<mod>.claim` that `ship.ps1` gates on — but only in checkouts whose `ship.ps1` postdates PR 757. A parallel session shipping from an older worktree bypassed the gate entirely (2026-07-18: ct_dev collided twice, `0.7.295-dev` and `0.7.296-dev`, the 20:00 upload clobbering the 19:17 fix build), and per-checkout `.ship_claims/` dirs can never see each other. The launcher is the one chokepoint every upload passes through regardless of checkout vintage, so the gate now lives here too:

- `claim.ps1` mirrors every claim into the machine-global `%APPDATA%\VMBLauncher\ship_claims\<mod>.claim` (same base dir as settings.json).
- `upload` and `all` evaluate that mirror BEFORE staging anything for ugc_tool (`all` checks up front, before the build — fail fast, same ordering as ship.ps1):
  - **live claim (< 2 h) for a DIFFERENT version → REFUSED, exit 3** with the claim's version + session and the fix (`.\tools\ship\claim.ps1 -Mod <name> -Release` then re-claim, or bump MOD_VERSION to the claimed version). This is the only refusing verdict — it means two sessions are racing different versions at the same Workshop item.
  - live claim that matches the source MOD_VERSION → one `[claim-gate] OK` line, proceed.
  - no claim / stale (>= 2 h) / unreadable claim → WARNING (unclaimed upload, collisions possible) and proceed — a missing claim must not brick old-workflow ships.
- New `--no-claim` flag skips the check with a loud warning (parity with `ship.ps1 -NoClaim`).
- New `Services/ShipClaimGate.cs` (parser + evaluator, pure/no-mkdir) with xUnit pins in `tests/ShipClaimGateTests.cs` (verdict matrix incl. the exact issue-724 incident shape, ordinal version compare so `-dev` suffixes matter, stale boundary at exactly 2 h, and a no-directory-creation pin).
- Scope note: the gate runs in the headless CLI verbs (the path every scripted ship takes). The GUI upload button does not run it.

## v0.5.5 (2026-07-05, committed 2026-07-18 as a working-tree sync)

### Added: cross-process upload serialization + staged-content guard (#344 concurrent-ship defense)

Two concurrent launcher processes share the SINGLE staging dir `<SDK>/ugc_uploader/sample_item/`. In the 2026-07-05 incident, session B's `Stage()` overwrote session A's staged content BETWEEN A's stage and A's ugc_tool push, so A pushed B's bundle onto A's Workshop item; A's post-upload cfg write-back then read B's `published_id` out of the shared staged cfg and stamped it into A's `itemV2.cfg` (the id-stomp). The v0.5.4 crossed-id guard validates the staged CFG id but not the staged CONTENT, so a same-target content swap slipped it. Two defenses in `ModRunner.UploadAsync`:

- **Named-semaphore upload lock** `Global\VMBLauncher_ugc_upload` (max count 1, 5-min acquire timeout) held around the entire stage → ugc_tool → cfg-write-back window, so two launcher PROCESSES serialize. A Semaphore, not a Mutex: ugc_tool runs behind `await`, and `Mutex.ReleaseMutex()` throws when the continuation lands on a different thread.
- **Staged-content ownership check** (`InspectStagedContent`, inside the lock, right after `Stage()`): the staged `content/` dir must carry exactly this mod's `<name>.mod` — a foreign `.mod` (or none) refuses with `[stage-guard] REFUSING upload: … concurrent-ship staging collision (issue #344)`.

New xUnit pins in `tests/StagedContentGuardTests.cs`. Also syncs `CLAUDE.md` with the monorepo's 2026-07-13 test-refresh ruling (author tests the hash-verified local deploy without a Steam restart; volunteer testers refresh via the dev collection).

*This entry was committed 2026-07-18 as a sync of a 2026-07-05 working tree that was never committed (same class of gap as the v0.3.1 → v0.5.4 sync, 3f12987). The v0.5.5 binary was never published; v0.5.6 is the first published build carrying these guards.*

## v0.5.4 (2026-07-05)

### Added: crossed-`published_id` guard on `deploy` / `upload` (#344 — hijack + double-install prevention)

A crossed `published_id` in `itemV2.cfg` (correct at ship-start, **stomped mid-ship** in the #344 incident: `gui_tweaker_dev`'s cfg transiently carried `chaos_wastes_tweaker_dev`'s id `3733366926`) drove two irreversible failures through the launcher — the `upload` pushed gut content onto ct_dev's Workshop item (hijack), and a ct_dev `deploy` wrote ct files into gut's Workshop folder `3751024698`, making the game load ct_dev twice (VMF duplicate-mod fatal) and gut not at all. Because the id was crossed *during* the ship, a repo-level preflight can miss it — the guard has to run at the moment the launcher acts.

New self-consistency check (no new canonical id map): a Workshop content folder `…\content\552500\<id>\` that already exists carries the owning mod's `<name>.mod` entry file. Before acting on an id, the launcher verifies that folder's `.mod` basename matches the mod being processed.

- **`deploy`** (`ModRunner.DeployAsync`): checked BEFORE any file delete/copy. If folder `<id>` is owned by another mod, aborts with `[id-guard] REFUSING deploy: target folder <id> owned by '<foreignMod>', not '<mod>' …`. This is the check that would have prevented the user-facing breakage outright.
- **`upload`** (`ModRunner.UploadAsync`): the `published_id` is read from the STAGED cfg (exactly what `ugc_tool` pushes) after staging, before the `ugc_tool` invocation. Foreign owner aborts with `[id-guard] REFUSING upload: cfg published_id <id> maps to Workshop folder owned by '<foreignMod>', not '<mod>' …`.
- Shared primitive `ModRunner.FindForeignModOwner(contentDir, modName)` returns the foreign `.mod` basename or null. The extension match is EXACT `.mod` (not the `*.mod` glob — Windows' three-character search-pattern rule makes `EnumerateFiles(dir, "*.mod")` also return `*.mod_bundle` files, whose hash basenames would false-positive as foreign owners).
- Graceful degradation: sentinel `0`/empty id (never-uploaded item), a missing local folder, an empty folder, or an unset `WorkshopContentRoot` all log `[id-guard] … proceeding` rather than block a legitimate first upload. A PASS logs `[id-guard] <id> owned by '<mod>'.mod - ok`.
- **Remote deploy (PC-B scp) is left unguarded** — the remote-push path has no cheap way to list `<remote>/<id>/` for a foreign `.mod` before transferring, and inventing a remote listing/parse round-trip was out of scope. The local deploy runs first and its guard aborts the whole `DeployAsync` before the remote push is reached, so a crossed id local-and-remote is still caught; only a target whose crossed id exists ONLY remotely would slip through.

New xUnit pins in `tests/WorkshopIdGuardTests.cs`: foreign `.mod` returns the owner name; own `.mod` returns null; empty dir / missing dir / `.mod_bundle`-only folder all return null (the last pinning the exact-extension rule).

## v0.5.3 (2026-05-29)

### Added: stale-bundle guard on `upload` / `all` (item 1 — #1 silent ship-wrong-thing path)

`upload` previously only asserted bundles *exist*, never that they're newer than source — so `upload <mod>` could ship a stale `bundleV2/` if source changed since the last build (the documented "uploaded v0.2, game ran v0.1" burn). New `Services/PreflightGates.cs` → `BundleFreshness.Check(mod)` computes the newest mtime under `<mod>/scripts/**` and `<mod>/resource_packages/**` versus the newest `<mod>/bundleV2/*.mod_bundle`.

- **`upload`**: if source is newer, emits a loud `[upload] WARNING: source newer than bundle — run 'build' first (shipping stale bundle)` to stderr but **proceeds** (a deliberate re-upload of a known-current bundle isn't hard-blocked).
- **`all`**: `all` builds first, so a stale bundle there means the build didn't write bundles — new `ModRunner.AssertBundleFresh` **hard-fails** the pipeline after the build step (wired in `AllCommand`).

### Added: cheap static-lint gate on `upload` (item 2 — closes the bypass every shipped %-format bug used)

The pre-commit hook + CI run `qa/check_localization.ps1` and `qa/check_vmf_widget_types.ps1`, but the `build`→`deploy` iteration loop bypassed them (commit deferred) and CI is `continue-on-error`. `UploadAsync` now shells out to both, scoped to the single target mod (`-RepoRoot <mod-dir>`, ripgrep-fast), BEFORE staging:

- `check_localization.ps1` exit 2 (errors — e.g. unescaped `%`) **blocks** the upload with the offending output; exit 1 (warnings) warns and proceeds.
- `check_vmf_widget_types.ps1` any non-canonical widget type (exit 2) **blocks** (it has no warning tier).

The gate is best-effort: if the qa script or a PowerShell host (`pwsh`/`powershell`) is genuinely absent it logs a skip rather than blocking (`QaScriptGate.Verdict.NotRun`).

New xUnit pins in `tests/PreflightGatesTests.cs`: freshness stale/fresh/no-bundle/no-source/resource_packages cases, plus a planted-unescaped-`%` test asserting the localization gate returns `Error` (→ blocked upload) and a clean-loc test asserting it does not.

### Related (outside the launcher binary)

- **`tools/mod-lint/lint-mod.ps1`**: network-bound-mutation rule refined for precision — removed the `stat_buff = "max_*"` Pattern B (false-positived on crt's two `stat_buff = "max_health"` data entries), added guarded-downward-clamp recognition (`if X > CONST then X = CONST`, the load-bearing ct:7831 `_max_ammo` clamp), and still flags bare widening assignments. Self-test extended; verified false-positives gone on ct + crt and real detection survives. Doc + `qa/CHECKS.md` row 7f updated.
- **`tools/mod-inventory.psd1`** (new): single source of truth for the active-mod inventory. `lint-mod.ps1` ($KnownMods — now scans all 4 dev clones + gui_tweaker; previously listed retired lobby_tweaker/material_hijack_patched and omitted dev clones), `tools/publish-release/publish-release.ps1` ($mods), and `qa/check_cfg.ps1` ($expectedVisibility) all read it.

## v0.5.2 (2026-05-26)

### Fixed: `UploadStager` ignored the cfg's `preview` field

`Services/UploadStager.cs` iterated a hardcoded `{ item_preview.png, preview.jpg, preview.png }` list to choose which preview file to stage, ignoring the `preview = "<filename>";` line in `itemV2.cfg` entirely. Editing the cfg to point at a custom preview filename (e.g. `preview = "test.jpg";`) had no effect — the launcher silently kept staging the first hardcoded match found in the mod dir.

**Fix.** `ModDiscovery.ParseItemCfg` now exposes a `Preview` property on `ModInfo` (mirroring the existing title/visibility/published_id parse). `UploadStager.Stage` honours `mod.Preview` as the primary path when set AND the named file exists in `<mod>/`, staging it under the cfg's literal name (no force-rename to `preview.jpg`). The hardcoded iteration becomes the fallback for mods whose cfg has no `preview` field.

New xUnit pin `Stage_copies_cfg_named_preview_when_set` asserts the staged file appears under the cfg's name and the staged item.cfg matches.

Burned 2026-05-26 while applying a unified thumbnail across friends-only Workshop mods — the cfg edits looked correct, the uploads silently kept the old preview.

## v0.4.1 (2026-05-21)

### Fixed: scp "unexpected filename" regression on remote deploy

Every `deploy` and `all` invocation that had a `RemoteDeployTargets` entry enabled was failing the remote push with `scp: error: unexpected filename: C:\Users\danjo\source\repos\...`. OpenSSH 9 added a new "unexpected filename" guard that rejects positional args whose embedded `:` makes them look like a `host:path` remote spec — every Windows absolute path trips it (the `C:` prefix).

**Fix.** Insert the `--` end-of-options sentinel between `-O` and the source path. After `--`, scp treats every remaining arg as positional and stops trying to parse the `:` in `C:\...` as a remote-spec separator. The destination spec (`pc-b:"..."`) stays after `--` deliberately — that arg still contains a real `host:path` separator and scp's positional parser handles it correctly.

```csharp
// Before:  new[] { "-O", src, destSpec }
// After:   new[] { "-O", "--", src, destSpec }
```

Extracted into `RemoteDeploy.BuildScpArgs(src, destSpec)` so the arg ordering is unit-testable. Four new xUnit tests in `tests/RemoteDeployTests.cs` pin the sentinel position so this can't regress silently.

Burned all four mods (`wt`, `ct`, `gt`, `cosmetics_tweaker`) on every `vmblauncher deploy` / `all` invocation between v0.4.0 (2026-05-20) and v0.4.1 (2026-05-21). Local deploy succeeded throughout; only the remote PC-B push failed, and the failure surfaced as the `local deploy OK, but remote push failed: ...` aggregate message added in v0.4.0.

## v0.4.0 (2026-05-20)

### Added: Multi-machine deploy — every `deploy` and `all` also pushes to remote targets

`deploy` and `all` now push the built bundles to every enabled remote machine in `settings.json` immediately after the local Workshop-folder copy succeeds. Default behaviour, not opt-in — the standing rule (`feedback_deploy_both_machines.md`) is that iterative VT2 mod debugging must keep the test client in lockstep with the host, and local-only deploys silently masked four days of host/client sync bugs (cosmetics_tweaker v0.8.67-dev → v0.8.71, 2026-05-15 → 2026-05-19). The launcher now enforces the rule so workflows can't forget.

**Configuration.** `settings.json` gains a `RemoteDeployTargets` array. Each target has:

```json
{
  "Name": "pc-b",
  "SshHost": "pc-b",
  "WorkshopContentRoot": "C:/(025) Steam/steamapps/workshop/content/552500",
  "Enabled": true
}
```

`SshHost` must resolve via `~/.ssh/config` (key-only auth — headless mode can't prompt for passwords). `WorkshopContentRoot` is the remote machine's Steam content root for App ID 552500.

**Auto-detect.** On first run, `AutoFillMissing` scans `~/.ssh/config` for `Host pc-b` and, if found, pre-fills the standard PC-B target with the canonical `(025) Steam` Steam path (per `reference_pc_b_dispatch.md`). The detector is a fixed allowlist so users with unrelated `pc-b` aliases don't get surprise deploys.

**Transport.** Each bundle file is sent via `scp -O`. The `-O` flag forces legacy SCP protocol, which is required for Windows OpenSSH destinations whose paths contain spaces — the modern SFTP protocol mangles quote tokenisation in the remote PowerShell shell layer and produces `dest open ""C:/Program Files...""` errors. The `ssh` probe and post-transfer size verification both rely on the same alias and key.

**Verification.** After transfer, the launcher runs a single `ssh` probe to list `<remote>/<workshopId>/` and asserts each local `name=size` pair appears in the listing. Catches truncated writes and silent zero-byte failures.

**Opt-out.** `vmblauncher deploy <mod> --no-remote` skips the remote push for one invocation. `--no-remote` also works on `all`. The local deploy still runs and is still hash-verified.

**Failure mode.** If any remote target fails (ssh probe, scp transfer, size mismatch), the whole `deploy` returns exit 1 with the local copy already in place. The user sees `local deploy OK, but remote push failed: <reason>` and knows immediately that PC-B is stale rather than discovering it three sessions later via a host/client desync crash. The previous silent-stale failure mode caused the v0.7.4-alpha → v0.7.10-alpha ct iteration burn.

**No GUI surface for it yet.** Manage targets by hand in `%APPDATA%\VMBLauncher\settings.json` until there's enough usage signal to warrant the dialog. The auto-detect covers the only known case.

## v0.3.1 (2026-05-14)

### Changed: retracted v0.3.0's "silent upload failure" caveat

v0.3.0 shipped with a "Known issue" claim that the launcher's `upload`/`all` verbs silently failed to transfer for established Workshop items. That claim was an inference from a separate `_upload_helper.ps1` script's failure and was never actually verified against the launcher itself. The launcher's own staging path is presumed working — the doctrine is now back to "use the launcher as the canonical path for build / deploy / upload / list / info / doctor".

`feedback_workshop_upload_verify.md` is still the operative rule for any upload: `ugc_tool` prints `Upload finished` even when content doesn't transfer, so eyeball the Workshop page (or hit `ISteamRemoteStorage/GetPublishedFileDetails` for public mods) before assuming success.

Documentation correction only — no binary behaviour change in the launcher between v0.3.0 and v0.3.1.

## v0.3.0 (2026-05-14)

### Added: Headless CLI mode

`VMBLauncher.exe` now exposes the same build / deploy / upload pipeline as a verb-based CLI that streams to stdout/stderr and returns proper exit codes. Same binary as before — zero args or `--gui` launches the WPF window; any other args route to the CLI handler.

Verbs: `list`, `info`, `doctor`, `build`, `deploy`, `upload`, `all`, `help`. Global flags: `--no-banner`, `--config <path>`, `--gui`. Exit codes: 0/1/2/3 (success / runtime fail / bad usage / preflight failed). Full reference in `CLAUDE.md`.

Implementation:

- `OutputType` changed from `WinExe` to `Exe` (console subsystem) so stdout/stderr are real pipes. The GUI path calls `FreeConsole()` immediately to dismiss the inherited console window before WPF spins up — no visible flash from explorer.exe launches.
- New `Program.cs` is the entry point (`<StartupObject>VmbLauncher.Program</StartupObject>`); `App.xaml` demoted from `ApplicationDefinition` to `Page` so the WPF SDK stops auto-generating a competing `Main`.
- `Settings.Load(string?)` overload accepts an alternate config path for `--config`.
- Broken-pipe writes from truncating consumers (`| head`, etc.) are caught and treated as success.
- `UTF-8` `OutputEncoding` set so mod descriptions with bullets / em-dashes survive non-UTF-8 terminals.

Note for PowerShell users: piping the launcher's output through `Select-Object -First N` sets `$LASTEXITCODE = -1` even on success. This is a PowerShell pipeline-termination signal, not a launcher behaviour — cmd and bash see the real exit code. Workaround documented in `CLAUDE.md`.

Testing:

- Added `tests/headless_smoke.ps1` — end-to-end suite (35 assertions) exercising every verb, every error path, exit codes through cmd/bash, broken-pipe handling, settings auto-detect, `--config` flag, and GUI fallback. Runs automatically as part of `publish.ps1`.
- 124 unit tests continue to pass unchanged.

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
