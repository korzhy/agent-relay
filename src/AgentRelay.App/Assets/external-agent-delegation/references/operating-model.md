# Operating model

Compare external specification + wait + review + likely correction cost with
direct Codex implementation. Delegate only when the active threshold is met.

An external Gemini executor is useful for bounded mechanical edits, repetitive contract alignment,
fixture/test generation, and exact build repairs. It is not an authority for
architecture, security, quota semantics, concurrency, lifecycle, visual truth,
or release readiness.

Review ladder:

1. Filesystem events, hashes, debounce, mutex, and process health.
2. Deterministic build/test/type/lint gates.
3. Smallest sufficient Codex semantic review.
4. High-effort Codex review for architecture, security, lifecycle, isolation,
   and release milestones.
