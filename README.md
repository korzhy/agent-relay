# Agent Relay

Agent Relay is a local-first Windows companion for assisted implementation
hand-offs between an open Codex task and Antigravity Gemini Flash. It launches
and monitors Flash, validates its report, then notifies the user and copies an
exact review prompt for the already-open Codex task. It never starts
`codex.exe` in the background.

MVP target: Windows 10/11 x64, .NET 8 WPF, self-contained per-user install.
There is no backend, account, telemetry, secret storage, or auto-update.

## Two settings that must not be confused

| Setting | Values | Meaning |
|---|---|---|
| External delegation threshold / Порог внешнего делегирования | `off`, `low`, `medium`, `high` (default `medium`) | How readily Codex routes suitable implementation to an external agent. |
| Flash executor | `Antigravity / gemini-3.6-flash-high` | Exact provider, model, and model-effort suffix used by the runner. |

The `high` suffix in `gemini-3.6-flash-high` is not the delegation threshold.
Agent Relay fails closed if `agy models` does not list that exact model.

## Quickstart

1. Install the x64 setup package as the current user. Administrator rights are
   not required. The default location is
   `%LOCALAPPDATA%\Programs\AgentRelay`.
2. Launch Agent Relay once and choose the global delegation threshold:
   `OFF`, `LOW`, `MEDIUM`, or `HIGH`. It is saved immediately.
3. The installer creates the idempotent managed block in
   `$HOME\.codex\AGENTS.md`, the global policy, and the
   `external-agent-delegation` skill. Foreign AGENTS content is preserved.
4. Continue working in Codex normally. Sol reads the threshold and invokes
   `AgentRelay.exe` itself when a bounded hand-off is worthwhile; the Relay GUI
   need not be running.
5. At the first real hand-off for a workspace, approve the single trust
   warning for that exact folder. Before approval the repository remains
   untouched and `agy.exe` is not started.
6. The dashboard shows one current mission and confirmed operational phases
   for Sol and Flash. A validated report copies the exact review prompt to the
   clipboard once; Sol still independently validates hashes, gates, semantics,
   and final integration.

Autostart is off by default. Closing the window minimizes to the tray; use the
tray menu to exit.

## Install on a second PC

Copy `AgentRelaySetup-x64.exe` to the second Windows 10/11 x64 PC and run it as
the target user. Install Codex App and Antigravity separately, sign in to them
through their normal UIs, and confirm `agy models` lists
`gemini-3.6-flash-high`. Then run Agent Relay Doctor and **Install / repair
Codex integration**.

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
```

The `project` commands are diagnostic/compatibility interfaces, not the normal
workflow. `project trust` is an explicit consent operation: it authorizes Agent Relay to
start the exact Flash executor in that workspace using
`--mode accept-edits --dangerously-skip-permissions`. Drive roots, the user
profile root, Windows, Program Files, ProgramData, and system directories are
rejected. `resume` in the GUI re-enables future dispatch; it never silently
replays an interrupted mission.

## Protocol and state

One executor and one active mission are allowed per project. Publication uses
same-directory temporary files, flush-to-disk, atomic replacement, SHA-256,
workspace-relative paths, globally unique handoff/run IDs, stable mission IDs,
strictly increasing revisions, and exact UTC timestamps. Published task,
report, and review payloads are immutable.

The dashboard maps `ready`, `running`, `waiting`, `stalled`,
`quota exhausted`, `report ready`, and `paused` into a single mission view. It
also shows explicit Sol phases (`evaluating`, `delegating`, `waitingForFlash`,
`working`, `reviewing`, `integrating`, `completed`, `blocked`) from local
atomic activity state. A phase older than 15 minutes is labelled as last known,
not as live model activity. FileSystemWatcher events,
debounce/hash suppression, a per-project mutex, and process health supervise
the runner; no model calls poll unchanged files. A crash or missing/invalid
report is not completion. Quota exhaustion appears only when actual process
exit/output matches a known exhaustion pattern.

The header and `quota [--json]` command use the last observed percentage of
general Antigravity prompt credits when a compatible local
`Quota update received` log is available. The value includes its source,
timestamp, and fresh/stale status. Agent Relay extracts only the numeric credit
fields and timestamp; it does not read process tokens or call Antigravity's
private localhost API. This percentage is not a model-specific guarantee for
`gemini-3.6-flash-high`. A stale snapshot never shows a percentage in the main
header; its old value and timestamp remain available in diagnostics. Without a
compatible source the value is explicitly `N/A`, never invented.

A report must claim `PASS`, `FAIL`, `BLOCKED`, or `UNVERIFIED` and list changed
files, commands and exit codes, first failure, unavailable dependencies, and
all prohibited-action confirmations. PASS is rejected when executable proof
is absent or a required dependency is unavailable.

## Threat and safety model

Agent Relay assumes the locally signed-in Codex and Antigravity installations
and the selected workspace owner are trusted. Task text and repository content
are untrusted inputs. Controls include:

- exact executor/model verification with no silent fallback;
- per-workspace one-time trust before noninteractive edits;
- canonical path containment and system/root-directory denial;
- immutable payloads and hash-bound envelopes;
- durable pause/cancel state, process identity checks, and one project mutex;
- logs of actions, tools, commands, exit codes, and errors only—never hidden
  model reasoning;
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
`%LOCALAPPDATA%\AgentRelay` and in the project.

## MVP limitations

- Windows 10/11 x64 only.
- One local executor and one active mission per project.
- No auto-update, cloud sync, or remote management.
- Quota percentage is last-observed general prompt-credit capacity, not a
  model-specific reservation or dispatch guarantee.
- Review hand-off is assisted: the prompt is copied once but never pasted,
  submitted, or used to launch Codex secretly.
- Repository transport history cleanup/retention is manual.
- The installer is unsigned until a future public release pipeline is supplied
  a code-signing identity.

## License

MIT
