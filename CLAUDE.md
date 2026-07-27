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
vmblauncher deploy   <mod-name> [--no-remote] # hash-verified copy to Workshop content folder
                                              #   THEN push to every enabled remote target
                                              #   (default: pc-b via Tailscale, auto-detected)
vmblauncher help                              # also: --help, -h
```

`upload` and `all` are internal publication verbs. They cannot reach
`ugc_tool` without `--publication-receipt` containing the exact short-lived
receipt bytes hosted on the canonical GitHub release by the monorepo's
`tools\ship\ship.ps1` transaction. Caller-authored JSON is not authority. GUI
Upload and Build+Deploy+Upload are intentionally non-publishing.

Global flags:

- `--no-banner` — suppress the `vmblauncher X.Y.Z (headless)` banner. Use whenever piping output to another tool.
- `--config <path>` — alternate settings file. Defaults to `%APPDATA%\VMBLauncher\settings.json`.
- `--gui` — force GUI even with other args present.

## Doctrine: when to use what

### Canonical release path: `tools\ship\ship.ps1 -Mod <name>`

Publication order is exact and non-bypassable:

1. Acquire the machine-global claim for the mod/version owner.
2. Edit source, `itemV2.cfg`, CHANGELOG, and version.
3. Run `tools\ship\ship.ps1 -Mod <name> -BuildOnly` to generate and validate
   the tracked bundle without deployment or publication.
4. Commit source and bundle together, push, open the pull request, pass hosted
   `qa-gate`, and merge.
5. From a clean checkout at the exact live default-branch HEAD, run
   `tools\ship\ship.ps1 -Mod <name>` (plus `-AllowPublic` only when required).

The final ship independently proves the clean commit, merged PR, hosted
`qa-gate`, claim owner/version, and exact cfg/bundle hashes. It records GitHub
release provenance and hosts a receipt valid for at most five minutes.
VMBLauncher independently downloads that exact receipt, rechecks every fact,
and verifies the SDK staging file set and hashes immediately before `ugc_tool`.
`-SkipGitHub`, direct publisher calls, direct launcher publication, and GUI
publication are not supported paths.

**Test refresh (user ruling 2026-07-13):** the author on PC-A tests the
hash-verified local deploy and does not need to restart Steam. Volunteer testers
refresh via the dev collection by unsubscribing/resubscribing the affected mods.
For every tester, confirm the running build via the newest
`%APPDATA%\Fatshark\Vermintide 2\console_logs\` log's `[<id>:LOAD] vX.Y.Z` line.

The launcher `build` and `deploy` verbs remain the primitives for local,
non-publishing iteration.

### Default: prefer the launcher for everything

`VMBLauncher.exe` remains the engineered build/deploy/upload boundary across
all VT2 mods. Operators use its `build`, `deploy`, `list`, `info`, and `doctor`
verbs directly; `tools\ship\ship.ps1` alone drives its internal publication
verb with the GitHub-hosted receipt.

**Do not** invent `scp`/`ssh`/Robocopy pipelines for PC-B deploy. The launcher
already pushes to every enabled `RemoteDeployTargets` entry after the local
copy completes.

After every upload, **verify the Workshop page file size** before assuming the push transferred. `ugc_tool` is known to print `Upload finished` even when content didn't transfer. For public mods that's automatable via `ISteamRemoteStorage/GetPublishedFileDetails`; for `friends_only`/`private` items the public API returns blank fields, so you need to eyeball the Workshop page in Steam.

The canonical ship records the GitHub release before Workshop mutation and
deploys the exact reviewed bundle before publication. Do not reverse that
order or add a post-upload commit/push step.

### Why prefer launcher

Rationale:

- Launcher has hash-verified deploys; `deploy_all.ps1` doesn't.
- Launcher deploys to remote machines (PC-B over Tailscale) in the same step — `deploy_all.ps1` doesn't.
- Launcher's upload path stages into `<SDK>/ugc_uploader/sample_item/` (the only layout that reliably avoids the 0x2 "empty content directory" error on first uploads).
- Launcher never writes `tags = [ ];` (which the SDK adds post-upload — pre-writing it breaks first uploads).
- Launcher handles UTF-8 BOM correctly when writing the staged `item.cfg` (PowerShell 5.1's `Set-Content -Encoding utf8` writes a BOM that ugc_tool refuses).
- Launcher's EULA handling uses the Node-style stdin pipe (which works) instead of `bash -c "echo y | ..."` (which depends on Git Bash being on PATH — see footgun #4 in the README).

### Decision tree

```
Does the user have a strong preference for a specific .ps1 script?      yes → honour it
                                                                         no  → ↓

Is the target machine missing the approved VMBLauncher binary?           yes → stop; install the approved binary
                                                                         no  → ↓

Use vmblauncher.
```

If the approved binary is absent, the canonical ship fails closed. Do not
fall back to raw PowerShell uploaders or create an ad hoc release binary during
the ship. Launcher development may use an isolated test build, but installing a
publication-capable binary is a separate reviewed maintainer action.

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

**Dev-stream clones never use `--allow-public`.** The five `<mod>_dev/` directories (`chaos_wastes_tweaker_dev`, `crafting_in_modded_dev`, `general_tweaker_dev`, `gui_tweaker_dev`, `verminious_dreams_lighting_dev`) are friends-only by design (see repo-root `CLAUDE.md` § "Dev/stable split workflow"). Their `itemV2.cfg` must keep `visibility = "friends_only"` and the launcher's `upload`/`all` invocations for them must never pass `--allow-public`. Only the matching stable directories (`chaos_wastes_tweaker`, `crafting_in_modded`, `general_tweaker`, `gui_tweaker`, `verminious_dreams_lighting`) take that flag.

## Remote deploy targets

`deploy` (and the `deploy` step inside `all`) push the bundle to every enabled remote machine in `settings.json` immediately after the local Workshop-folder copy completes. This is the default — opt out per-invocation with `--no-remote`. The standing rule (`feedback_deploy_both_machines.md`) is that iterative VT2 debugging must keep the test client in lockstep with the host; the launcher enforces it so individual workflows can't forget.

Config schema (`%APPDATA%\VMBLauncher\settings.json`):

```json
"RemoteDeployTargets": [
  {
    "Name": "pc-b",
    "SshHost": "pc-b",
    "WorkshopContentRoot": "C:/(025) Steam/steamapps/workshop/content/552500",
    "Enabled": true
  }
]
```

- `SshHost` resolves via `~/.ssh/config`. Authentication is key-only — headless mode can't prompt for passwords. If `~/.ssh/config` already has `Host pc-b`, the first-run auto-detect adds this entry for you with PC-B's standard `(025) Steam` path; otherwise edit by hand.
- Transport uses `scp -O -- <src> <destSpec>` (legacy SCP protocol). `-O` is non-negotiable for Windows OpenSSH targets whose paths contain spaces — the modern SFTP protocol mangles quote tokenisation in the remote PowerShell shell and produces `dest open ""C:/Program Files...""` errors. The `--` end-of-options sentinel is also non-negotiable: OpenSSH 9 added an "unexpected filename" guard that rejects positional args whose embedded `:` looks like a `host:path` remote spec, and every Windows absolute source path (`C:\...`) trips it. The sentinel forces scp to treat the following args as positional (src, dest) rather than as more options/remote-specs. v0.4.1 fixes this regression; see `Services/RemoteDeploy.cs` → `BuildScpArgs` and its xUnit pins in `tests/RemoteDeployTests.cs`.
- Post-transfer verification: a single `ssh` probe lists `<remote>/<workshopId>/` and asserts each local `name=size` pair appears in the listing. Catches truncated writes and silent zero-byte transfers.
- Failure of any remote target returns exit 1 with the message `local deploy OK, but remote push failed: <reason>` — the user finds out about a stale PC-B immediately, not three sessions later via a host/client desync crash.
- The remote folder must already exist (i.e. the user must be subscribed to the mod on the remote machine). The launcher refuses to create stray directories from a typo.

When to bypass:

- `--no-remote` — one-off, local-only deploy. Useful when iterating on something the remote PC doesn't need (e.g. a UI-only fix while testing in PC-A's keep).
- Disable a target without removing it — flip `Enabled` to `false` in `settings.json`.

## Ship-claim gate (machine-global, monorepo issue #724)

The only live claim authority is machine-global:
`%APPDATA%\VMBLauncher\ship_claims\<mod_folder_name>.claim`. Repo-local
`.ship_claims` content is documentation only. The monorepo's
`tools/ship/claim.ps1` owns atomic acquire/release and uses a 24-hour stale
window.

| Claim state | Behavior |
|---|---|
| Live (< 24 h), exact mod/version/owner match | Necessary coordination gate; continue to receipt verification. |
| Missing, stale, unreadable, wrong mod/version, or foreign owner | **REFUSE** before publication. |

A matching claim is never sufficient publication authority. The final
`PublicationReceiptGate` requires a receipt no older than five minutes, obtains
the release asset independently, requires byte-for-byte identity with the local
handoff, and verifies clean local HEAD, live default HEAD, the exact merged PR,
successful hosted `qa-gate`, cfg/source hashes, and the exact staged cfg/content
file set. `--no-claim` is rejected as an unknown flag. GUI publication is
disabled.

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

### Run `qa/check_localization.ps1` before declaring any localization edit complete

**RULE:** any time you touch a `<mod>_localization.lua` file — adding a key, editing a tooltip, or copy-pasting a description from another mod — run `qa/check_localization.ps1` from the repo root before calling the edit done. The launcher does NOT currently run this check at build time; it's an out-of-band static lint.

```powershell
pwsh -NoProfile -File qa/check_localization.ps1
# Exit 0 = pass, 1 = warnings only, 2 = ERRORS (unescaped %, etc.)
```

The check catches the **unescaped `%` format bug** — a literal `%APPDATA%`, `%USERNAME%`, `5%`, or `10% chance` in a loc value will crash VMF's tooltip render path with `invalid option '%A' to 'format'` and surface as a red tooltip error in the VMF settings UI. The fix is always the same: double the literal `%` to `%%` (so `%APPDATA%` becomes `%%APPDATA%%`).

**Why this rule exists** (burned 2026-05-25): the per-mod `qa/run_all.ps1` pre-commit hook catches the bug at COMMIT time, but agent workflows that build/deploy without committing slip past it. Every shipped instance of the bug landed via a session that ran `VMBLauncher.exe build <mod>` and called it done — the static lint was never invoked. Until the launcher itself wires this gate in (see GitHub Issues for the launcher-side proposal), the rule is: **build verification is not localization verification; run `qa/check_localization.ps1` explicitly.**

Runtime safety net: each mod also runs a `_rt_register("localization_format_safe", ...)` check at `/<mod>_regression_test` time that walks the loc table and `pcall(string.format, value)` on each entry. It can't prevent the bug from shipping, but it surfaces the regression on the next regression sweep instead of waiting for a user mouseover report.

## Settings & auto-detect

On every invocation (GUI and headless), `Settings.AutoFillMissing()` runs and persists any newly-discovered paths back to `settings.json`. This means:

- First-time headless use on a fresh machine works without explicit setup, **as long as** the auto-detectors can find Steam / SDK / VMB. If they can't, `vmblauncher doctor` will tell you what's missing.
- The launcher will never overwrite a setting you've explicitly set in `settings.json` — auto-detect only fills empty fields.

## Gotchas & preflight checks

The doctrine items below describe failure modes the launcher already
guards against. If you're authoring tooling outside the launcher (raw
PowerShell wrappers, manual `ugc_tool` invocations, alternative
upload paths), respect every item — most are silent failures.

### PowerShell 5.1 `Get-Content -Raw` is NOT UTF-8

PowerShell 5.1's `Get-Content -Raw $path` reads files using the
system's **default code page**, NOT UTF-8. On en-US Windows that's
Windows-1252. Reading a UTF-8 file this way silently mangles
multi-byte sequences:

- bullet `•` (UTF-8: `E2 80 A2`) → Win-1252 reads as 3 chars `â€¢`
- em-dash `—` (UTF-8: `E2 80 94`) → `â€"`
- `ö` (UTF-8: `C3 B6`) → `Ã¶`

If you then re-write the string as UTF-8 (e.g.
`[System.IO.File]::WriteAllText` with `System.Text.UTF8Encoding`),
each garbled char gets re-encoded as 2-3 UTF-8 bytes. Net: 1 bullet
becomes 7 bytes of garbage. Steam Workshop shows the description with
literal `â€¢` instead of bullets.

**Right way for reading config / Workshop cfg files** — symmetric with
what you'd use for writing:

```powershell
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$cfgRaw = [System.IO.File]::ReadAllText($cfgPath, [System.Text.Encoding]::UTF8)
# ... transform ...
[System.IO.File]::WriteAllText($cfgPath, $cfgRaw, $utf8NoBom)
```

**Set-Content / Out-File caveat (separate but related):** in PS 5.1,
`Set-Content -Encoding utf8` writes a **BOM-prefixed** UTF-8 file.
ugc_tool's cfg parser rejects BOM-prefixed UTF-8 ("item config file
not found" — verified). Use `WriteAllText` with `UTF8Encoding($false)`
(no-BOM) for cfg writes.

PowerShell 7+ defaults to UTF-8 for both `Get-Content` and
`Set-Content`, but this repo targets PS 5.1 (the version that ships
with Windows). Don't rely on the newer default.

**Burned 2026-05-14 in `_upload_helper.ps1`:** the helper read
`itemV2.cfg` via `Get-Content -Raw`, parsed fields, and wrote the
staged `item.cfg` via `WriteAllText` with no-BOM UTF-8. The bullets /
em-dashes in `chaos_wastes_tweaker`'s description were mangled in
flight. Workshop subscribers saw `â€¢ Toggle any campaign mission...`
instead of `• Toggle...`. Fixed by switching the helper's
`Get-Content -Raw` calls to
`[System.IO.File]::ReadAllText(..., [System.Text.Encoding]::UTF8)`.

**Verification trick:** count UTF-8 bytes for `•` in both source and
staged cfg:

```bash
xxd -p source.cfg | tr -d '\n' | grep -o 'e280a2' | wc -l
xxd -p staged.cfg | tr -d '\n' | grep -o 'e280a2' | wc -l
```

Counts should match. If staged is `0` and source is non-zero, the
helper is mangling encoding.

The launcher's upload path already uses `WriteAllText` with no-BOM
UTF-8 throughout — but any auxiliary tooling that reads/writes cfgs
must match.

### Drop `tags = [ ];` from cfg on first upload

**For a brand-new mod's first upload, the `itemV2.cfg` must NOT
contain `tags = [ ];`** — the SDK's `ugc_tool` adds that line itself
post-upload. Pre-writing it causes "generic failure (probably empty
content directory)" 0x2 even when bundles + cfg + staging are
otherwise correct.

Sir Aiedail (VMB author) confirmed this in a 2020 Discord chat; the
maintainer captured the rule in `old-backup/ANTIGRAVITY.md:129` and
`:213`:

> "tags = [ ]; is added automatically by the tool — do NOT add it
> manually before first upload"

Verified across vmb-launcher v0.2.0 → v0.2.8: every version before
v0.2.8 wrote `tags = [ ];` in the staged cfg, every version failed on
first uploads. v0.2.8 dropped the line and the failure disappeared
for users on healthy networks.

**What does NOT actually need to be different.** A lot of red
herrings turned up while debugging this. None of these were the root
cause:

- Whether the staging folder is named `sample_item` vs custom
  (`vmblauncher_staging`).
- Whether the cfg path passed to ugc_tool is relative or absolute.
- Whether the cwd is the SDK uploader dir or the staging dir.
- Whether paths use forward slashes or backslashes.
- Whether `published_id = 0L;` is set explicitly or omitted for new
  items (SDK README says either works).

The SDK README at `<sdk>/ugc_uploader/README.txt` says paths are
relative to cfg location, and that's true in practice — none of cwd /
path-separator-style / absolute-vs-relative matter as long as the cfg
points at an existing folder. The reason previous launcher iterations
failed wasn't path resolution; it was the `tags = [ ];` line.

### Zapret blocks Workshop content uploads

**Zapret** (Russian DPI-bypass tool, `bol-van/zapret`) interferes with
Steam Workshop's content-upload protocol specifically. Users running
Zapret can browse the Workshop, download mods, and play VT2 normally,
but `ugc_tool` uploads fail with `Timeout uploading manifest` in
Steam's own log. The launcher-side error surfaces as the same
misleading "empty content directory" 0x2.

**Fix:** disable Zapret for uploads. Other Russian-Steam workarounds
(VPN, region change, off-peak retry) may also be needed depending on
baseline ISP behavior, but Zapret itself is an active blocker for
Workshop content upload — not a workaround.

This note exists for advising third parties. The repo maintainer
themselves does NOT run Zapret — that note came from helping a
Russian friend.

### ugc_tool pushes ALL cfg fields — verify before upload

`ugc_tool.exe` reads the entire `itemV2.cfg` and pushes EVERY field
(title, description, preview image, visibility, tags) to the Steam
Workshop page on every upload — not just the bundle content. This
means:

1. If the user has edited the live Workshop page directly (changed
   title, swapped preview image, changed visibility), the LOCAL cfg
   may be out of sync with the live page.
2. Uploading without checking REVERTS those direct-edit changes to
   whatever the cfg currently says.
3. Untracked preview files (e.g., `preview.png` added locally but cfg
   still says `preview = "preview.jpg"`) won't be pushed unless the
   cfg's `preview =` line is updated to reference them.

**Why:** Uploaded ct + cim with stale cfg metadata. User then pointed
out a new `preview.png` that should have been live. The PNG was
untracked locally and the cfg still referenced the old JPG, so the
upload pushed the JPG and silently kept the wrong thumbnail on
Workshop.

**Mandatory pre-upload checklist:**

- Read the current `itemV2.cfg` immediately before running
  `ugc_tool`.
- Glob the mod folder for any `preview.png` / `preview.jpg` / other
  image files. If multiple exist, ask user which should be pushed.
- Cross-check: ask the user (or read the live Workshop page if
  possible via gh-style API) what title, description, preview
  filename, and visibility should currently be on Workshop.
- Verify the file referenced by `preview = "..."` exists AND is the
  one the user expects (not a stale JPG when a PNG was added).
- Confirm visibility matches user's intent.
- If any field's drifted, edit the cfg to match live state BEFORE
  uploading OR confirm the cfg's values are what should overwrite
  live.
- Only THEN run `ugc_tool`.

The launcher itself does NOT rewrite `itemV2.cfg` (other than the
auto-managed `title` version suffix and `published_id` propagation
after a first upload). The user's cfg is the source of truth — and
that's exactly why the pre-upload audit matters.

Related rules:

- **Never auto-change cfg title/desc/preview/visibility** — user
  dictates Workshop metadata. The trailing ` v<MOD_VERSION>` title
  suffix is the ONLY auto-managed field; everything else
  (description, base title text, preview filename, visibility) is
  user-dictated. When asked to check/read live values, fetch from
  `https://steamcommunity.com/sharedfiles/filedetails/?id=<workshop_id>`
  and report verbatim — local cfg has drifted from live in the past.
- **Always verify Workshop file size after upload** — `ugc_tool`
  prints "Upload finished" even when content didn't transfer.

#### Visibility — never flip without explicit instruction

**NEVER set or change `visibility` in `itemV2.cfg` without the user's
explicit instruction.** A prior agent flipped two mods to `"public"`,
both got flagged and "removed from community" — Steam's removal
status is one-way and irreversible. When migrating an SDK mod to VMB,
default the new `itemV2.cfg`'s `visibility` to `"private"` UNLESS the
user explicitly says otherwise. Confirm before any upload that
visibility is what the user wants.

The launcher enforces this with `--allow-public` gating on
`visibility = "public"` uploads (see § Visibility safety above), but
the cfg field itself is still user-dictated.

**Per-mod intended visibility** lives in the Mod Directory table at
`vermintide-2-tweaker/CLAUDE.md` § "Mod Directory". Consult that
table (it's the live source of truth) rather than duplicating it here
— visibilities change over time and a duplicate table drifts. Quick
read: most tweaker mods are intentionally `private` or
`friends_only`; only `chaos_wastes_tweaker`, `general_tweaker`,
`material_hijack_patched`, and `verminious_dreams_lighting` are
intentionally `public` (others may have moved between sessions —
check the table).

`VMBLauncher.exe upload` (called by `ship.ps1`) aborts with an error if a
mod's `itemV2.cfg` has `visibility = "public"` and `--allow-public` was not
passed, as a guardrail.

### Workshop upload verification: `workshop_log.txt` is source of truth

`ugc_tool` prints `[Info]: Upload finished` and exits 0 **even when no
content actually transferred**. If the staged bundle is byte-identical to
what's already on Workshop, Steam logs `No content change detected for
item <id>` and skips the content upload. The Workshop's `time_updated`
does NOT bump on no-op content uploads (only on actual manifest changes),
so the public API still shows the old timestamp. **"Upload finished" tells
you nothing.** The author can re-run `upload` ten times and nothing ships.

The real outcome lives at
`C:\Program Files (x86)\Steam\logs\workshop_log.txt`:

| Log line | Meaning |
|---|---|
| `Uploaded new content ( ManifestID <id> )` | Actual new manifest pushed |
| `No content change detected` | Staged bundle == Workshop bundle; content skipped |
| `Reverting to previous content` | Steam rejected the new manifest and rolled back |
| `Size limit exceeded for preview file` | Preview > 1 MB (hard cap; shrink via `magick <src> -strip -resize 80% <src>`) |

Grep this file after every upload run.

**Caveat for `friends_only` / `private` items:** for items uploaded with
non-public visibility, `ugc_tool` does NOT write to `workshop_log.txt` at
all in some configurations. When the log is missing, fall back to:

- Eyeball the Workshop page file size (the launcher can't fetch this via
  the public API for non-public items — `ISteamRemoteStorage/GetPublishedFileDetails`
  returns blank fields when `result: 9` is the access error).
- Byte-cmp between `<mod>/bundleV2/` and
  `C:\Program Files (x86)\Steam\steamapps\workshop\content\552500\<id>\`
  to spot un-deployed local changes (these usually match unless un-deployed
  changes exist).

**Stale-bundle prevention.** Publication never chooses between direct `all`
and `upload`. Generate the artifact with `ship.ps1 -BuildOnly`, commit the
exact source and bundle together, pass PR review and hosted QA, merge, then run
the canonical ship from clean live default HEAD. The hosted receipt records
every source bundle hash, while the launcher also derives the canonical staged
cfg and verifies every staged content byte immediately before `ugc_tool`.

For non-publishing iteration, `vmblauncher build <mod>` and
`vmblauncher deploy <mod>` remain available. A deploy updates only local and
enabled remote test folders; it does not update subscribers.

### Hand-scaffolded first uploads (without `vmb create`)

When hand-writing a new VT2 VMB mod's `.mod` / `.package` / cfg / lua files
manually (skipping `node vmb.js create ...`), the first canonical ship still
has several non-obvious gotchas:

**Preview file requirement.** VMB-built mods use **`item_preview.png`**
in the cfg (`preview = "item_preview.png";`), NOT `preview.jpg` (which is
the legacy SDK-only convention). The launcher does **NOT** synthesize a
placeholder when the preview file is missing — the user's belief that
"vmblauncher uses a generic mod thumbnail" is wrong. That auto-synthesis
only happens via `vmb create`, which scaffolds `item_preview.png` from
the template at scaffold time.

`ugc_tool` errors with
`Upload Failed: "file not found; invalid workshop item, invalid preview file or invalid content path", (0x9)`
when the preview file is missing — **and still creates an orphan Workshop
item** (see below).

Fix: before first upload, copy the VMB template's generic 238 KB
thumbnail into the mod root:
```powershell
Copy-Item C:\Users\danjo\source\repos\vmb\.template-vmf\item_preview.png <mod>\item_preview.png
```

**Orphan Workshop item on first-upload failure.** `ugc_tool` creates the
Workshop item BEFORE validating preview/content. A failed first upload
still leaves an orphan item on Steam, identified only by the
`New workshop item created with publisher_id: -<signed_int>` line in
the launcher's stdout. `ugc_tool` only writes `published_id = NL;` back
to `itemV2.cfg` on a **successful** upload. On failure the orphan ID is
gone from automation — capture it from the printed log.

Convert signed → unsigned: `unsigned = signed + 2^32`
(e.g. `-565121781` → `3729845515`).

Before retry, manually add `published_id = <unsigned>L;` to `itemV2.cfg`,
or the retry creates a SECOND orphan item.

**Subscribe-to-own-upload required before `deploy` works.** After a
successful upload, Steam does NOT auto-subscribe you to your own Workshop
item. `vmblauncher deploy <mod>` fails with
`Workshop folder missing: ...workshop\content\552500\<id>` until you
manually subscribe via
`https://steamcommunity.com/sharedfiles/filedetails/?id=<published_id>`
(Steam → Subscribe). Once subscribed and Steam has downloaded the bundle
(creates the folder), `deploy` works for hot-iteration thereafter.

**Canonical first-upload sequence for hand-scaffolded new mod:**

1. Hand-write `<mod>.mod`, `<mod>/resource_packages/<mod>/<mod>.package`,
   three lua files, `itemV2.cfg` (with `visibility = "friends_only"` and
   `preview = "item_preview.png"`, and `published_id = 0L`; omit `tags`).
2. `Copy-Item C:\Users\danjo\source\repos\vmb\.template-vmf\item_preview.png <mod>\item_preview.png`
3. Acquire the machine-global claim and run
   `tools\ship\ship.ps1 -Mod <mod> -BuildOnly`.
4. Commit source and generated bundles together, push, open the PR, pass
   hosted `qa-gate`, merge, then run the canonical ship from clean live
   default HEAD. Canonical ship issues a distinct hosted bootstrap receipt;
   never use direct launcher or GUI publication.
5. On successful item creation, the launcher validates the complete ugc_tool
   cfg change and compare-and-swaps only the returned `published_id` into the
   still-authorized source cfg. The ship stops as **not test-ready** and retains
   the machine-global claim. Commit the ID-only change, pass protected PR QA,
   merge it, and run the ordinary canonical ship before releasing the claim or
   applying any in-game lifecycle label.
6. Open `https://steamcommunity.com/sharedfiles/filedetails/?id=<published_id>`
   → Subscribe.
7. Steam downloads to
   `C:\Program Files (x86)\Steam\steamapps\workshop\content\552500\<published_id>\`.
8. `vmblauncher deploy <mod>` now works for subsequent non-publishing iterations.

See also the `vmb create`-based path, which scaffolds the preview
automatically but has its own delete-on-failure quirk.

### itemV2.cfg format reference (merged from DEVELOPMENT.md, issue #432)

```ini
title = "Tweaker: Weapons";
description = "Weapon unlock and runtime experimentation for Vermintide 2. Requires VMF.";
preview = "preview.jpg";
content = "bundleV2";
language = "english";
visibility = "private";
published_id = 3712896117L;
apply_for_sanctioned_status = false;
tags = [ ];
```

- `preview` and `content` resolve RELATIVE to the cfg file's parent directory;
  `content = "bundleV2"` points at VMB's build output folder.
- `published_id` requires the `L` suffix (64-bit integer literal).
- Semicolons are required on every line (parsed by `libconfig.dll`).
- `apply_for_sanctioned_status = false` is valid and should be kept.
- For a NEW item, omit `published_id` (or set `0L;`) and omit `tags = [ ];`
  entirely (see "Drop `tags`" above) - the tool populates both on first upload.
- `visibility` is user-dictated; never set or change it without explicit
  direction (see "NEVER set or change visibility" above - a wrong public flip
  has caused an irreversible "removed from community" before).

---

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

**Headless-first rebuild guarantee.** `publish.ps1` auto-stops any held `VMBLauncher.exe` processes (GUI or in-flight CLI verb) before invoking `dotnet publish`. Without this, the single-file publish fails with `MSB4018: The process cannot access the file ... because it is being used by another process` and the doctrine "no GUI interface should be necessary" breaks down. Burned 2026-05-24 in the AI Takeover fix loop — the v0.4.x binary couldn't be rebuilt because the user had the GUI open from a separate session. Now the script terminates held instances itself and waits for the file lock to release (max 10 s) before publishing.

## Inputs Claude is most likely to be asked for

- "Upload my mod" → follow claim → BuildOnly → commit/push/PR/qa-gate/merge →
  canonical `tools\ship\ship.ps1`
- "Build, deploy, and upload" → use the same canonical reviewed ship sequence
- "Why won't the upload work" → `vmblauncher doctor` then inspect blocking checks
- "What mods do I have" → `vmblauncher list`
- "Show me the cfg state for X" → `vmblauncher info <mod>`

In all cases, prefer this over composing raw `dotnet`, `ugc_tool`, or `bash`-piped invocations.
