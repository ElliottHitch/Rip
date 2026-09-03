# ADR-0003: Approved privacy and security boundaries

- Status: Accepted and non-negotiable
- Date: 2026-09-03 UTC
- Decision owners: Product owner, engineering, and security
- Evidence: `[ARCH]`, `[TEST]`, `[DOC]`, and sections 4 and 7 of `../refactor-proposal.md`

## Decision

Browser-session access is default-off and requires explicit consent for each download plus a selected supported browser. The adapter may construct only the supported in-memory `cookiesfrombrowser` argument. Browser, profile, keyring, cookie, and session values are opaque run-scoped data: they are never displayed, logged, persisted, exported, copied, uploaded, or returned by Core ports. Consent, browser, profile, and option state clears after success, failure, or cancellation. Default runs do not inspect browser data.

The product must not ask for passwords, automate login or CAPTCHA, export or persist cookie files, upload browser data, use a remote browser bridge, rotate proxies, spoof fingerprints or headers, bypass service restrictions, or automatically hammer HTTP 429 responses. A bounded stream-403 fresh resolution is a compatibility rule, not a bypass.

Local opening is a separate capability and port. It accepts only a freshly reverified, readable, non-empty local MP4 and opens an encoded local `file://` URI through the OS handler. It cannot invoke yt-dlp, network, browser-session, or retry code, and it receives no session object, URL, or process handle.

All events, logs, dialogs, retained errors, exception causes/contexts, traceback locals, and future diagnostics carry only safe codes and bounded, display-safe values. Scrub source/query URLs, home/destination/profile paths, cookies, signed URLs, tokens, authorization fields, raw upstream/browser diagnostics, and child stderr. Synthetic sentinel values are allowed only in tests.

## Consequences

Privacy is a port and information-flow invariant, not a UI promise. The application can report capability and safe remediation without exposing account-bearing local data. Redaction regressions, traceback-local retention, stale event leakage, and local-opener coupling are release blockers.

## Validation and release gates

- Assert default-off behavior and explicit consent in deterministic Core/application/adapter tests.
- Inject synthetic cookie/profile/path/signature/token sentinels and assert absence from every safe output, event, error, log, cause, context, and traceback local.
- Verify stale runs cannot re-enable controls or retain session state.
- Verify local opening with path, permission, non-empty, and default-handler failures while proving no provider/network/session call occurs.
- Any sensitive retention, implicit browser access, bypass behavior, false completion, or opener coupling is a no-go regardless of other test results.
