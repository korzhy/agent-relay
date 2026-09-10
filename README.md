# Agent Relay

Agent Relay is a local-first Windows companion for assisted implementation
hand-offs between an open Codex task and an Antigravity Gemini High executor. It launches
and monitors the executor, validates its report, then notifies the user and copies an
exact review prompt for the already-open Codex task. It never starts
`codex.exe` in the background.

Target: Windows 10/11 x64, .NET 8 WPF, self-contained per-user install.
There is no backend or telemetry. Managed account metadata is local; OAuth
credentials are protected by Windows Credential Manager. Stable releases can
update automatically from this repository's GitHub Releases.

## Two settings that must not be confused

The controller is whichever model is active in Codex with the required tools and permissions.
Legacy `Sol` class/phase names remain for compatibility; Relay does not require a Sol model
or impose model-specific reasoning settings. The external executor remains Antigravity Gemini High.

| Setting | Values | Meaning |
|---|---|---|
| External delegation threshold / Порог внешнего делегирования | `off`, `low`, `medium`, `high` (default `medium`) | How readily Codex routes suitable implementation to an external agent. |
| Gemini executor | `Antigravity / latest-observed gemini-*-high` | Resolved through `agy models` before each new handoff; the exact model is then pinned in the protocol. |

The `high` suffix in the selected model is not the delegation threshold.
Agent Relay records when each Gemini High model is first observed in `agy models` and selects the most recently observed model, regardless of family or numeric version. Models present when the discovery ledger is first created share one baseline timestamp, so numeric version is used only as a deterministic tie-breaker for that baseline. The last verified model is cached, and the built-in fallback is used only when discovery and cache are unavailable.
`agy models` does not expose vendor release timestamps, so the ledger must be initialized before the future model appears. If multiple new models first appear between two Relay observations, they share one observation timestamp and use the same numeric tie-breaker.

## Quickstart

1. Install the x64 setup package as the current user. Administrator rights are
   not required. The default location is
   `%LOCALAPPDATA%\Programs\AgentRelay`.
2. Launch Agent Relay once and choose the global delegation threshold:
   `OFF`, `LOW`, `MEDIUM`, or `HIGH`. It is saved immediately.
3. Open **Аккаунты**, give the account a local label, and complete the visible
   official `agy` OAuth. Agent Relay does not infer the subscription tier;
   monitor account type manually. It never scans or imports Antigravity Tools accounts.
4. The installer creates the idempotent managed block in
   `$HOME\.codex\AGENTS.md`, the global policy, and the
   `external-agent-delegation` skill. Foreign AGENTS content is preserved.
5. Continue working in Codex normally. Sol reads the threshold and invokes
   `AgentRelay.exe` itself when a bounded hand-off is worthwhile; the Relay GUI
   need not be running.
6. At the first real hand-off for a workspace, approve the single trust
   warning for that exact folder. Before approval the repository remains
   untouched and `agy.exe` is not started.
7. The dashboard shows one current mission and confirmed operational phases
   for Sol and the Gemini executor. A validated report copies the exact review prompt to the
   clipboard once; Sol still independently validates hashes, gates, semantics,
   and final integration.

Autostart is off by default. Closing the window minimizes to the tray; use the
tray menu to exit. Auto-update is enabled by default and runs while Relay is
open or in the tray.

## Install on a second PC

Copy `AgentRelaySetup-x64.exe` to the second Windows 10/11 x64 PC and run it as
the target user. Install Codex App and Antigravity separately, sign in to them
through their normal UIs, and confirm `agy models` lists
at least one `gemini-*-high` model. Then run Agent Relay Doctor and **Install / repair
Codex integration**.

Version `0.2.0` and older cannot update themselves. Install `0.3.0` once on
each PC; all later stable releases can then update automatically when Relay is
next running.

The internal workspace binding and trust are deliberately machine-local and
must be repeated. There is no project-registration workflow in the normal GUI;
Relay creates the local binding when Sol first attempts a hand-off. Copying a
repository preserves its `.agent-relay` history if
that directory was committed or otherwise transferred; Agent Relay does not
sync it. Global policy is stored at
`$HOME\.codex\external-agent-delegation.json`.

## CLI

The same `AgentRelay.exe` serves GUI and CLI:

```text
AgentRelay.exe doctor --json
AgentRelay.exe quota --json
AgentRelay.exe account list
AgentRelay.exe account add --label "Work Pro" --activate
AgentRelay.exe account activate --id <id>
AgentRelay.exe account refresh --id <id>
AgentRelay.exe account refresh --all
AgentRelay.exe account remove --id <id>
AgentRelay.exe account settings --rotation-threshold 10
AgentRelay.exe policy get
AgentRelay.exe policy set medium
AgentRelay.exe project add C:\work\project
AgentRelay.exe project trust C:\work\project
AgentRelay.exe project list
AgentRelay.exe project remove <id-or-path>
AgentRelay.exe activity get --project <id-or-path>
AgentRelay.exe activity set --project <id-or-path> --phase reviewing --summary "Sol reviews the report"
AgentRelay.exe activity clear --project <id-or-path>
AgentRelay.exe handoff publish --project <id-or-path> --task task.md --gate "dotnet test"
AgentRelay.exe handoff status --project <id-or-path>
AgentRelay.exe handoff cancel --project <id-or-path>
AgentRelay.exe handoff resume --project <id-or-path>
AgentRelay.exe codex install|repair|remove
AgentRelay.exe update status
AgentRelay.exe update check
AgentRelay.exe update set on|off
AgentRelay.exe update apply
```

The `project` commands are diagnostic/compatibility interfaces, not the normal
workflow. `project trust` is an explicit consent operation: it authorizes Agent Relay to
resolve and start the most recently observed available Gemini High executor in that workspace using
`--mode accept-edits --dangerously-skip-permissions`. Drive roots, the user
profile root, Windows, Program Files, ProgramData, and system directories are
rejected. `handoff cancel` cancels the current protocol handoff and arms a
durable per-project pause, so later publish attempts are rejected before a new
handoff is created. `handoff resume` (and Resume in the GUI) removes that pause
and re-enables future dispatch; it never silently replays an interrupted or
cancelled mission. After resume, publish the task again to create a new handoff.

## Bounded execution and local experience (0.5)

The bundled skill supplies a compact task contract: observable outcome, known facts versus
hypotheses, chosen approach, invariants, allowed write scope, non-goals and exact acceptance
checks. Uncertain diagnosis can be delegated as evidence-only investigation; architectural
decisions remain with Codex. Published tasks also carry stop rules against repeated failures,
scope expansion, weakening tests and repeated checks without new evidence.

CLI handoffs now have a **30-minute hard timeout per runner attempt**, configurable with
`--timeout-minutes 1..120` (previous CLI default: two hours). Quota rotation creates a new
attempt and restarts that timeout; it is not a total mission/token budget. A timeout leaves
partial work for inspection and is never success. Prompt-based scope/loop rules are guidance,
not a filesystem sandbox or a tool-call counter.

`--gate` commands are supplied to the executor. Relay validates report structure and hashes;
Codex independently executes required acceptance checks, including the behavioral/integration
case, before acceptance. A valid report is not semantic acceptance.

```text
AgentRelay.exe experience recall --project <id-or-path> --kind implementation
AgentRelay.exe experience recall --project <id-or-path> --model <exact-gemini-model>
AgentRelay.exe experience record --project <id-or-path> --file reviewed-outcome.json
```

After review, Codex records `accepted`, `corrected`, `rejected`, `blocked` or `abandoned`,
with task kind, correction count, failure category, brief evidence and a conditional lesson.
The store binds the exact executor and task SHA-256. Accepted/corrected entries require a
matching report envelope and successful controller review commands; this is a controller
attestation, not automatically verified semantic truth. Identical retries are idempotent;
different reviews cannot overwrite a handoff's recorded outcome.

The **Опыт** window shows project-specific outcomes. History lives only under
`%LOCALAPPDATA%\AgentRelay\experience\<project-id>`; no telemetry, embeddings, extra model
calls, background summaries or automatic policy changes. Recall samples at most 200 recent
records from 90 days, filters task kind/exact model, and returns at most five short lessons.
Counts describe the sample; different models/tasks and quota/environment failures should
not be treated as a model benchmark. Full evidence/commands stay in local records and are
not included in recall. Memory is advisory data, never instructions or authorization.

The [experience schema and workflow](src/AgentRelay.App/Assets/external-agent-delegation/references/experience.md)
describe recording limits. Do not store secrets or source dumps. History is retained on
uninstall and never placed in Git automatically. No old reports are retroactively called successes.

The app, tray and installer share a multi-size icon generated from `Assets/RelayMark.xaml`;
run `powershell -STA -File scripts/build-icon.ps1` to regenerate it. Dashboard labels use
Codex/Gemini roles and readable threshold names (Выкл./Точечно/Баланс/Активно).

## Automatic updates

Relay checks `https://api.github.com/repos/korzhy/agent-relay/releases/latest`
at startup and no more often than every six hours. Only a stable tag in exact
`vMAJOR.MINOR.PATCH` form is accepted. The release must contain exactly one
`AgentRelaySetup-x64.exe` and one `AgentRelaySetup-x64.exe.sha256`.

The updater:

- accepts download URLs only from this repository over HTTPS;
- requires GitHub's SHA-256 asset digest and the published checksum to agree;
- streams to an app-owned `.partial` file with a 200 MB limit;
- verifies length and SHA-256 before publication and again before execution;
- refuses downgrade or same-version reinstall;
- defers installation while any Gemini runner is live;
- runs the existing per-user Inno Setup installer silently, closes Relay
  cleanly, repairs the managed Codex integration idempotently, and relaunches
  Relay.

Settings and update state are stored under
`%LOCALAPPDATA%\AgentRelay\updates`. If Relay is not running, nothing runs in
the background; the update is applied the next time Relay starts. Diagnostics
and `update set off` can disable automatic updates.

To release from another trusted PC, push the tested commit to `main`, create
and push a `vMAJOR.MINOR.PATCH` tag, and wait for the tagged GitHub Actions
workflow. The workflow tests, publishes, compiles the installer, writes the
checksum, and creates the stable GitHub Release. Installed Relay instances
will discover it without any machine-specific coordination.

## Protocol and state

One executor and one active mission are allowed per project. Publication uses
same-directory temporary files, flush-to-disk, atomic replacement, SHA-256,
workspace-relative paths, globally unique handoff/run IDs, stable mission IDs,
strictly increasing revisions, and exact UTC timestamps. Published task,
report, and review payloads are immutable.

Lifecycle has two coordinated layers. Runtime `running`/`waiting` means a live
runner; `stalled` and `quotaExhausted` are non-completions and the protocol
handoff remains active, so a replacement publish is blocked until explicit
cancel. `reportReady` is terminal because a validated report pointer exists.
Cancel writes the matching terminal `cancel.json` and arms the runtime pause;
while paused, publish exits before creating transport. Resume removes the pause
and resets runtime to identity-free `ready`, but never dispatches an old
handoff. A new publish is always required after resume.

The dashboard maps `ready`, `running`, `waiting`, `stalled`,
`quota exhausted`, `report ready`, and `paused` into a single mission view. It
also shows explicit Sol phases (`evaluating`, `delegating`, legacy protocol name `waitingForFlash`,
`working`, `reviewing`, `integrating`, `completed`, `blocked`) from local
atomic activity state. A phase older than 15 minutes is labelled as last known,
not as live model activity. FileSystemWatcher events,
debounce/hash suppression, a per-project mutex, and process health supervise
the runner; no model calls poll unchanged files. A crash or missing/invalid
report is not completion. Quota exhaustion appears only when actual process
exit/output matches a known exhaustion pattern.

The header and `quota [--json]` command show the active managed account. Quota
is read through the official `agy --output-format json --print /usage` command;
the effective Gemini remainder is the lower of `gemini-weekly` and `gemini-5h`.
The configurable rotation threshold is 1–50% (default 10%) and is strict:
exactly 10% remains usable at the default threshold. Before publication Relay
refreshes the active account and, when rotation is needed, candidates; selection
uses highest remainder, least recently used, then stable account ID.

Agent Relay intentionally does not gate accounts on the undocumented
`loadCodeAssist` tier response: official `/usage` proves that the credential can
be used and supplies the rotation quota, while the subscription type is left to
the user to monitor. A missing or changing tier response therefore does not
block activation. Access and refresh credentials are separate Windows
Credential Manager entries. `%LOCALAPPDATA%\AgentRelay\accounts` contains only
IDs, labels, email, eligibility, quota, timestamps, and settings. OAuth client
credentials are not embedded or copied from Antigravity Tools.

Only one `agy` operation/runner may run globally across Relay projects. The
global lease is acquired before account/model preflight and before handoff
publication, so `runnerBusy` creates no protocol payload. A healthy runner is
not interrupted merely for crossing the threshold. Confirmed quota/rate-limit
output cancels the old revision, rotates to an unused eligible account, resolves
`agy models` again, and publishes a new revision/runAttempt for the same mission.

A report must claim `PASS`, `FAIL`, `BLOCKED`, or `UNVERIFIED` and list changed
files, commands and exit codes, first failure, unavailable dependencies, and
all prohibited-action confirmations. PASS is rejected when executable proof
is absent or a required dependency is unavailable.

## Threat and safety model

Agent Relay assumes the locally signed-in Codex and Antigravity installations
and the selected workspace owner are trusted. Task text and repository content
are untrusted inputs. Controls include:

- exact executor/model pinning with an explicit cached or built-in fallback when discovery fails;
- per-workspace one-time trust before noninteractive edits;
- canonical path containment and system/root-directory denial;
- immutable payloads and hash-bound envelopes;
- durable pause/cancel state, process identity checks, and one project mutex;
- logs of actions, tools, commands, exit codes, and errors only—never hidden
  model reasoning;
- stable updates pinned to `korzhy/agent-relay`, with exact asset names,
  HTTPS URL restrictions, GitHub asset digest, published SHA-256, size limits,
  no downgrade, and a second hash check immediately before execution;
- no secret storage, telemetry, backend, deploy, push, production access, or
  irreversible-action authority;
- Codex remains responsible for architecture, security, deterministic
  validation, final readiness, and integration.

Noninteractive edit access is inherently powerful. Use a clean Git worktree,
review diffs, keep secrets outside task/repository content, and run only
bounded tasks with deterministic gates. Agent Relay is not a sandbox and does
not make malicious repository scripts safe.

## Build and test

Prerequisites for development: .NET SDK `8.0.423` (pinned by `global.json`) and
Windows x64. Inno Setup `7.0.2` x64 is pinned in
`installer/inno-version.json`.

```powershell
dotnet test AgentRelay.sln -c Release
powershell -ExecutionPolicy Bypass -File scripts\bootstrap-inno.ps1
powershell -ExecutionPolicy Bypass -File scripts\build-release.ps1 `
  -Iscc .\work\inno\ISCC.exe
```

The release script runs tests, publishes a self-contained `win-x64`
single-file app, and produces `outputs\AgentRelaySetup-x64.exe`. GitHub Actions
runs build/test on pushes and pull requests and attaches the same named setup
artifact to `v*` tag releases.

## Uninstall and data ownership

Uninstall removes program files and invokes `codex remove` before deleting the
executable. That command removes only the Agent Relay managed AGENTS block and
owned skill. It restores a pre-existing skill/policy backup when safe, deletes
an app-created policy only if its hash is still unchanged, and leaves a
user-modified policy in place. Internal workspace bindings, logs, trust decisions,
and project `.agent-relay` history are preserved by default under
`%LOCALAPPDATA%\AgentRelay` and in the project. Downloaded update packages,
update state, and update settings are app-owned and are removed.

## MVP limitations

- Windows 10/11 x64 only.
- One local executor and one active mission per project.
- No cloud sync or remote management.
- Quota percentage is last-observed general prompt-credit capacity, not a
  model-specific reservation or dispatch guarantee.
- Review hand-off is assisted: the prompt is copied once but never pasted,
  submitted, or used to launch Codex secretly.
- Repository transport history cleanup/retention is manual.
- The installer is not Authenticode-signed. SHA-256 protects integrity in
  transit and detects release inconsistencies, but it does not protect against
  compromise of the GitHub repository or its release workflow. Protect
  maintainer accounts with MFA and keep branch/tag protections enabled; add a
  code-signing identity before treating updates as enterprise-grade.

## License

MIT
