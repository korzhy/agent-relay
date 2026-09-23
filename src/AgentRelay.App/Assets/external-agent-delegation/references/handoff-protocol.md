# Agent Relay atomic hand-off protocol

Protocol v1 keeps transport under `.agent-relay/` only after a real delegation:

- `control.json`: current atomic control pointer;
- `tasks/`: immutable task payloads;
- `reports/`: immutable report payloads and envelopes;
- `report.json`: current validated report pointer;
- `failure.json`: terminal failed-attempt pointer with stage, exit code and local log paths;
- `reviews/`: exact immutable Codex review prompts;
- `cancel.json`: durable cancellation pointer.

Every envelope includes a globally unique handoff ID, stable mission ID,
strictly increasing revision, run/review attempt identity, exact provider and
model, UTC timestamp, workspace-relative payload paths, and SHA-256.

Write payload to a same-directory temporary file, flush, atomically publish it,
then publish its pointer. Never edit a published payload. Reject path escape,
hash mismatch, stale identity, executor substitution, and concurrent active
missions.

Reports must say PASS, FAIL, BLOCKED, or UNVERIFIED and include changed files,
commands with exit codes, first failure, unavailable dependencies, and explicit
confirmation that prohibited actions did not occur. PASS requires at least one
successful command, no first failure and no unavailable required dependencies.
Relay checks report consistency; Codex independently checks the required gates.
