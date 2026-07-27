---
name: external-agent-delegation
description: Route bounded implementation to an external agent while Codex retains architecture, security, final review, and integration authority.
---

# External Agent Delegation

Use external implementation only when specification, supervision, review, and
expected correction cost are lower than direct Codex implementation.

## Resolve policy

Read `$HOME\.codex\external-agent-delegation.json`. Apply an explicit user
instruction first, then an optional project override at
`.codex/external-agent-delegation.json`, then global policy, then safe default
`low`. `off` is a hard stop.

The delegation threshold (`off|low|medium|high`) controls routing willingness.
It is separate from model effort. The only supported executor is exactly
`Antigravity / gemini-3.6-flash-high`; never substitute another model.

## Routing

- Low: only mechanical, unambiguous, locally provable work with roughly 2x
  expected Codex-effort savings.
- Medium: coherent 30–90 minute implementation with exact contracts and local
  gates; allow one correction.
- High: prefer locally provable external implementation; transfer to Codex
  after the same root cause repeats twice.

Codex always owns architecture, security, concurrency, lifecycle acceptance,
final readiness, production, deploy, secrets, irreversible actions, and final
integration.

## Hand-off

Use the Agent Relay protocol described in
`references/handoff-protocol.md`. Require immutable payloads, SHA-256 binding,
globally unique IDs, stable mission ID, strictly increasing revision, exact
executor/model, UTC timestamps, and PASS/FAIL/BLOCKED/UNVERIFIED truth reports.

Supervise through filesystem events, hashes, debounce, mutex, and process
health. Never invoke a model to poll unchanged state. A crash or invalid report
is not completion. Quota exhaustion may be reported only from actual exit or
output evidence.

## Review

Validate hashes, run the first deterministic gate, and independently inspect
semantics. An implementer report is a claim, not evidence. Never let an
external agent deploy, push, touch production, handle secrets, authorize
security/final readiness, or perform irreversible actions.
