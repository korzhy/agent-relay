# Agent Relay 0.5.3

This patch makes an attempted Flash handoff easier to diagnose when the executor exits silently, the report is invalid, or Relay is interrupted.

- Persist `Waiting` before starting `agy`, then record a terminal outcome with a failure stage for process, capture, timeout, report, and transport faults. Missing or invalid reports never become `reportReady`.
- Keep the terminal runtime outcome when `failure.json` cannot be written, and repair the failure pointer when storage becomes available.
- On the next status check after a Relay interruption, verify the child process identity before deciding it has stopped. Recover a previously accepted report or recorded failure when its immutable evidence validates; otherwise mark the attempt `stalled`.
- Classify observed network/transport errors separately from quota exhaustion. This is diagnostic only; it does not infer whether the remote service completed work.
- Keep the existing `ReportPayload` and `FailureEnvelope` wire shapes unchanged.

Verification: 90 core tests and 101 integration tests in Release configuration; self-contained Windows x64 publish and Inno Setup 7.0.2 installer build. The 30-run real Flash pilot and the experimental consult/chat mode are **not** included in this release. Codex must still independently review valid Flash reports before accepting implementation results.
