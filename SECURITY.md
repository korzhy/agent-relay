# Security policy

Please report security issues privately to the repository maintainers before
opening a public issue. Do not include secrets, access tokens, personal paths,
or proprietary repository content in reports.

Agent Relay does not accept security authority from an external executor.
Codex and the user must independently review all changes and validation
evidence. See the threat and safety model in `README.md`.

Automatic updates trust the stable GitHub Release produced by
`korzhy/agent-relay`. Report unexpected release assets, checksum/digest
mismatches, downgrade behavior, redirects outside the allowed GitHub hosts,
or installer execution while a runner is active as security issues. The
installer is not yet Authenticode-signed, so repository and workflow account
security remain part of the update trust boundary.
