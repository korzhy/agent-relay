# Security policy

Please report security issues privately to the repository maintainers before
opening a public issue. Do not include secrets, access tokens, personal paths,
or proprietary repository content in reports.

Agent Relay does not accept security authority from an external executor.
Codex and the user must independently review all changes and validation
evidence. See the threat and safety model in `README.md`.

Managed `agy` access/refresh credentials are stored as separate Generic
Credentials in the current user's Windows Credential Manager. Account JSON must
never contain OAuth tokens. The official `agy` OAuth is the only enrollment
path; Agent Relay does not read or import Antigravity Tools account storage.
Subscription tier classification is deliberately manual; the Relay uses
credential validity and `/usage` quota, not an undocumented tier endpoint, to
avoid rejecting accounts that can demonstrably execute tasks.
Report credential restoration failures, secret-bearing CLI JSON, dispatch with
an unrecognized active credential, or concurrent cross-project runners as
security issues.

Automatic updates trust the stable GitHub Release produced by
`korzhy/agent-relay`. Report unexpected release assets, checksum/digest
mismatches, downgrade behavior, redirects outside the allowed GitHub hosts,
or installer execution while a runner is active as security issues. The
installer is not yet Authenticode-signed, so repository and workflow account
security remain part of the update trust boundary.
