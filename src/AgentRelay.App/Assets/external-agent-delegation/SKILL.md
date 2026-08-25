---
name: external-agent-delegation
description: Route bounded, locally verifiable implementation through Agent Relay to the most recently observed available Antigravity Gemini High model when the active delegation threshold makes that cheaper than direct Codex work; retain Sol architecture, security, review, and integration authority.
---

# External Agent Delegation

## Resolve policy

Read `$HOME\.codex\external-agent-delegation.json`. Apply an explicit user
instruction first, then this global policy, then safe default `low`. The Relay
GUI setting is global for every workspace; do not let a project file override
global `off`.

- `off`: do not consider or invoke the external Gemini executor.
- `low`: delegate only mechanical, unambiguous work with about 2x expected
  Codex-effort savings.
- `medium`: allow a coherent 30–90 minute bounded implementation with exact
  contracts and local gates; allow one correction.
- `high`: prefer the external Gemini executor for locally provable implementation; transfer to Codex
  after the same root cause repeats twice.

Threshold is separate from model effort. Agent Relay resolves the most recently
observed available `gemini-*-high` via `agy models` before each new handoff, then
records that exact executor in the immutable task and control payloads. First-observed
date is authoritative across model families; numeric version only breaks a tie between
models first observed together. Do not bypass Relay's resolver or substitute a non-Gemini
model or a lower effort.

## Dispatch through Agent Relay

Use `%LOCALAPPDATA%\Programs\AgentRelay\AgentRelay.exe`. The GUI need not be
running.

1. If delegation is genuinely competitive, record the decision:
   `activity set --project <workspace> --phase evaluating --summary <safe summary>`.
2. Write a bounded task file outside the workspace. Include exact scope,
   prohibited actions, acceptance criteria, and deterministic gates.
3. Run:
   `handoff publish --project <workspace> --task <file> --title <title> --gate <command>`.
4. If Relay returns exit `5` / `trustRequired`, let the user answer Relay's
   one-time workspace prompt. Never invoke `project trust` for the user.
5. If Relay returns exit `6` / `delegationOff`, continue directly without
   external execution.
6. Treat crash, invalid/missing report, stalled, paused, and quota exhausted as
   non-completion. Do not use a model call to poll unchanged state.

Relay automatically records `delegating`, the legacy protocol phase `waitingForFlash`, and the validated
report transition. Use `activity set` for Sol's real subsequent phases:
`reviewing`, `integrating`, `completed`, or `blocked`. Do not claim continuous
Sol activity when no explicit operational phase exists.

## Review and integrate

Read `references/handoff-protocol.md` when validating an actual report. Verify
immutable IDs and SHA-256 bindings, run the first deterministic gate, then
inspect semantics independently. The implementer report is a claim, not
evidence.

Sol always owns architecture, security, concurrency, lifecycle acceptance,
final readiness, production, deploy, secrets, irreversible actions, and final
integration. Never delegate or authorize those responsibilities.
