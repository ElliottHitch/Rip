# Phase 2 Avalonia App shell UX and accessibility contract

Status: implementation-ready design contract; documentation only

Scope: `UnifiDownloader.App` presentation shell over the typed `UnifiDownloader.Core`

This document defines the single-window UX, accessibility contract, safe state projection, and validation gates for Phase 2. It does not add Avalonia code, dependencies, adapters, network behavior, browser access, credentials, packaging, launcher behavior, or release support. The legacy `app.py` remains the runnable rollback reference until the separately approved cutover gates pass.

The contract is subordinate to [ADR-0001](decisions/0001-target-stack.md), [ADR-0002](decisions/0002-runtime-and-process-ownership.md), [ADR-0003](decisions/0003-privacy-and-security-boundaries.md), [ADR-0004](decisions/0004-support-packaging-and-rollback.md), and [ADR-0005](decisions/0005-phase-1-implementation-contract.md). Where those records leave a behavior unverified, this document names a validation gate rather than claiming support.

## 1. Product and design intent

The shell is an Operate-mode desktop utility for a technically capable, trust-sensitive user. It should feel calm, dense, keyboard-first, and unambiguous under attention during a long-running operation. It is not a marketing surface, a login surface, a browser, or a diagnostic console.

The visual direction is restrained dark-tool language with an editorial hierarchy and modest industrial character:

- Use a quiet neutral surface, one restrained amber action accent, and semantic green, amber, and red status treatments.
- Never use color as the only status signal. Every status has text, an icon or shape with a text alternative, and an accessible announcement where appropriate.
- Keep spacing and grouping predictable. Prefer standard Avalonia controls and platform file dialogs over custom chrome.
- Use a platform-resolved system font and respect the user's text scaling. Do not assert that an Avalonia control is a native Win32 or GTK control.
- Keep motion low. Progress must communicate work without decorative animation or attention capture.
- Maintain a useful minimum layout: no control or activity region may collapse to an unusable sliver at the minimum supported window size.

The window owns one active request at a time. The user enters exactly one video reference per run. A new run is available only after the current run reaches a terminal state and the shell has applied its reset rules.

## 2. Information architecture and single-window layout

### 2.1 Window regions

The shell is one resizable window with a stable vertical reading order. The regions are:

1. Header: product name and a concise subtitle such as “One video per run”. It contains no source value, profile value, token, or process detail.
2. Request: the video URL field and operation selector.
3. Output: destination folder, file stem, container, and optional frame-rate target.
4. Browser session: a clearly separated, default-off consent group.
5. Environment: a compact capability summary and a `Test Environment` action.
6. Run controls: the primary `Start` action and secondary `Cancel` action.
7. Activity: current stage, progress, safe activity text, and a bounded in-memory activity log.
8. Completion: published-output summary and local-only actions, shown only when their capability conditions are true.
9. Footer/status line: concise safe status and, when present, a safe diagnostic reference.

The layout may use a two-column arrangement at a sufficiently wide window, but the logical and keyboard order remains the numbered order above. At narrower widths it becomes one column without hiding required controls. The activity region must remain visible below or beside the form; it must not be reduced to a one-pixel or effectively unusable log area.

### 2.2 Request region

The Request region contains:

- `Video URL`: one editable text field. It accepts an absolute HTTP or HTTPS address through the Core `VideoReference` boundary. The shell does not parse provider-specific query or signature semantics beyond the validation contract supplied by Core.
- `Operation`: a mutually exclusive choice with exactly three values:
  - `Metadata` — resolve and present safe metadata only.
  - `Video` — download the video stream and produce the selected output.
  - `Audio` — download the audio stream and produce the selected output.

The operation control is a radio group or an equivalent mutually exclusive Avalonia choice control. The default is `Video` unless the application owner records a different default. The selected operation is shown in text as well as by selection styling.

The shell must not offer a playlist, batch, multi-video, provider-login, or arbitrary extractor mode. A request containing more than one video is rejected by the Core one-video policy.

### 2.3 Output region

The Output region contains:

- `Output folder`: a read-only or editable path field according to the selected file-dialog implementation, plus `Choose Output Folder`. The displayed value is a user-selected destination, not a diagnostic payload. The field and any accessible description must not expose it through activity, errors, logs, or window chrome beyond the field itself.
- `File stem`: an editable proposed filename stem. Core safe-filename policy owns normalization. The UI shows the normalized or accepted stem only after Core returns it; it does not reproduce an unsanitized title in a diagnostic or announcement.
- `Container`: a mutually exclusive choice of `MP4` and `Unifi MP4`, mapped exactly to `OutputContainer.Mp4` and `OutputContainer.UnifiMp4`. The shell does not claim semantic Unifi compliance merely because this value is selected; compliance remains an application/media validation result.
- `Frame rate`: optional finite target. The default choice is `Preserve source`. Explicit values are `24 FPS`, `25 FPS`, or `30 FPS`, matching `FrameRatePolicy`. The control rejects empty malformed text, non-finite values, and values outside the Core contract before `Start` is enabled. A visible description says that a target can require conversion.

For `Metadata`, the output controls may remain visible for orientation, but they are disabled and marked `Not used for metadata`; no output request is submitted from those disabled controls. For `Video` and `Audio`, all applicable output controls are enabled. Disabled controls retain an explanatory description and are not silently skipped in the focus path without a corresponding accessible state.

The destination default, if supplied, is an application-owned compatibility default. This document does not authorize a configuration file or persistence schema; absent such an approved schema, form values are run-scoped and reset according to the privacy/reset rules below.

### 2.4 Browser-session region

The Browser session region is visibly and semantically separate from both the URL field and the local `Open in Browser` action.

Controls:

- `Use browser session`: an unchecked consent checkbox, default off for every new run.
- `Browser`: a supported-browser choice containing only the browser kinds exposed by Core, currently Chromium, Chrome, Edge, and Firefox. It is disabled while consent is off.
- `Browser session help`: static text explaining that enabling the checkbox gives the provider adapter explicit, run-scoped permission to use the selected supported browser session. It does not ask for a password, profile path, cookie file, login, or CAPTCHA.

The `Start` action is disabled or the run is rejected until a supported browser is selected when consent is on. Turning consent off clears the browser selection before the next submission. The shell never displays or accepts a profile path, cookie value, keyring value, opaque lease, generated argument, or provider-session identifier. The Core `BrowserSessionSelection` contains only the selected browser kind at this boundary.

Consent is per run, not a standing application permission. The consent checkbox, browser choice, and any associated opaque in-memory state are cleared after success, failure, or cancellation. A stale event may not re-enable consent or repopulate a prior browser choice.

The help text must explicitly distinguish this provider capability from opening a local file:

> Browser session access is optional and off by default. If enabled, the selected browser session is used only for this run. The app never asks for a password, exports cookies, automates login or CAPTCHA, uploads browser data, uses a remote browser bridge, rotates proxies, spoofs fingerprints or headers, or bypasses service restrictions.

### 2.5 Environment region

`Test Environment` invokes the application capability diagnostic through the composition boundary. It does not start a download, inspect browser data, open a browser, or include raw process output in the UI.

The region may summarize capability categories such as `yt-dlp available`, `FFmpeg available`, and `runtime available` using safe statuses. A missing capability is actionable but not silently repaired. Exact executable paths, command lines, profile paths, child stderr, exception chains, and upstream diagnostics are never rendered.

Environment results are capability hints, not proof that live provider access, semantic MP4/Unifi validation, screen-reader support, packaging, or release readiness has passed.

### 2.6 Activity and completion regions

Before a run, Activity shows `Ready` and no fabricated progress. During a run it shows:

- a stage label,
- a determinate progress bar only when the event supplies a valid fraction,
- an indeterminate progress indicator only when work is active but no fraction is available,
- the latest safe activity string from `DownloadProgress.Activity`, and
- a bounded in-memory activity log containing safe stage/activity entries only.

After a terminal event, active progress stops. The terminal state is displayed as text and remains authoritative even if cleanup produces a warning.

Completion exposes:

- a safe output summary, such as a sanitized file name and size category or size value supplied by the application contract,
- `Open Folder` only when the application has a valid published local result and the action's separate file-system conditions are satisfied,
- `Open in Browser` only under the local-media gate in section 7,
- `Start New Run` after terminal reset handling.

The completion region never treats a staged artifact, an unverified artifact, a cancelled run, or a failed run as a published output.

## 3. Form rules, commands, and focus contract

### 3.1 Initial focus and logical order

On first display, focus lands on `Video URL` unless an environment or startup error requires a safe status announcement and the first actionable remediation. The complete logical tab order is:

1. `Video URL` text field.
2. `Metadata`, `Video`, `Audio` operation choices.
3. `Output folder` field or summary.
4. `Choose Output Folder`.
5. `File stem`.
6. `Container` choices.
7. `Frame rate` choice/input.
8. `Use browser session` consent checkbox.
9. `Browser` choice, when enabled.
10. `Test Environment`.
11. `Start`.
12. `Cancel`, when enabled during an active run.
13. `Current stage and progress` status region.
14. `Activity log` or its disclosure control.
15. `Open Folder`, when enabled.
16. `Open in Browser`, when enabled.
17. `Start New Run`, when enabled.

The implementation may use a compact visual arrangement, but keyboard traversal must not jump from a field to a visually unrelated region. Disabled controls are not keyboard destinations unless the platform accessibility inspection exposes them in a way that still communicates why they are unavailable; the chosen behavior must be consistent on Windows UIA and Linux AT-SPI2.

When a modal consent confirmation is used instead of an inline checkbox, its focus order is `Keep Browser Session Off`, `Allow for This Run`, `Cancel`. `Keep Browser Session Off` is the default action. `Escape` and window-close dismiss the prompt without consent and return focus to the consent control. There is no keyboard trap outside the bounded dialog, and there is no prompt for credentials.

### 3.2 Keyboard behavior

- `Tab` and `Shift+Tab` follow the logical order above.
- `Space` toggles the consent checkbox and selects a focused radio/choice item.
- `Enter` activates the focused default button. It must not submit a request while focus is inside a multiline activity view or while validation is unresolved.
- `Alt+S` may be assigned to `Start` and `Escape` to `Cancel` only if the platform's access-key behavior is verified and the shortcut is announced in the accessible name or description. A single shortcut must not conflict with text editing or the host desktop.
- `Escape` cancels a consent prompt and requests cancellation for an active run; it never claims immediate termination when the application is waiting for bounded adapter cancellation.
- Window close during a run follows the same cancellation confirmation policy as `Escape`; closing is not a hidden force-kill promise.
- No action is available only through pointer hover, drag, color, or a context menu.

### 3.3 Focus-visible behavior

Every keyboard-focused interactive control has a persistent, high-contrast focus indicator that is distinct from selection, validation, and hover. It must remain visible in dark, light, high-contrast, 100% scaling, and increased text-size presentations. Do not remove the platform focus visual merely to achieve a denser layout.

When a terminal event changes the enabled action set, focus is moved deliberately:

- completion: move to the terminal status or the first newly enabled completion action, according to the platform focus test result;
- cancellation or failure: move to the safe error/status heading, then expose the first safe recovery action if one exists;
- stale or rejected event: leave focus on the current run and do not focus hidden or obsolete controls.

Focus movement is announced only when it changes the user's task context. It must not repeatedly steal focus on every progress event.

## 4. Accessible names, roles, descriptions, and announcements

### 4.1 Naming rules

Each control has a stable, localized accessible name that describes purpose, not implementation or a dynamic secret-bearing value. The following names are the contract baseline:

| Visual control | Role | Accessible name | Description/state requirements |
| --- | --- | --- | --- |
| URL field | editable text | `Video URL` | Describe that exactly one HTTP(S) video is accepted. Never append the entered URL to the name or description. |
| Operation group | radio group | `Operation` | State that exactly one of `Metadata`, `Video`, and `Audio` is selectable. |
| Metadata choice | radio button | `Metadata` | State whether output controls are not used. |
| Video choice | radio button | `Video` | State that a video stream is downloaded. |
| Audio choice | radio button | `Audio` | State that an audio stream is downloaded. |
| Output folder field | editable/read-only text | `Output folder` | State that it is the destination for staged publication. Do not repeat the full path in status text. |
| Folder button | button | `Choose Output Folder` | State that a folder picker opens. |
| File stem | editable text | `File stem` | State that Core safe-filename policy normalizes it. |
| UniFi compatibility | checkbox | `Make output UniFi-compatible` | Off by default and available for video runs only. When enabled, target MP4/H.264/AAC and derive an allowed 24/25/30 FPS rate. |
| Consent checkbox | checkbox | `Use browser session` | State `Off by default; applies only to this run`. |
| Browser choice | combo box | `Browser` | State that it is required only when consent is on; never expose a profile path. |
| Environment button | button | `Test Environment` | State that it reports safe capability statuses without starting a download. |
| Start button | button | `Start` | State why it is disabled: invalid request, consent selection incomplete, missing required capability, or active run. |
| Cancel button | button | `Cancel` | State that cancellation is requested and may take a bounded time. |
| Stage display | status/text | `Current stage` | Contains only safe stage text such as `Resolving` or `Publishing`. |
| Progress bar | progress bar | `Download progress` | Expose determinate value only for a valid fraction; otherwise expose busy/indeterminate state. |
| Activity log | log/status region | `Activity log` | Contains bounded safe activity text; never raw child stderr or provider diagnostics. |
| Open Folder | button | `Open Folder` | State the published-result condition; no path interpolation in the name. |
| Open in Browser | button | `Open in Browser` | State that it opens the verified local media file through the OS handler, not a provider page or session. |
| New run | button | `Start New Run` | State that terminal form/session state will be reset. |

The URL and output-folder controls may expose their own current editable value as required for normal assistive editing, but no surrounding label, tooltip, status, log, exception, or announcement may echo that value. In particular, the shell must never synthesize an accessible name from the URL, query, signature, output path, or browser data.

### 4.2 Status and screen-reader announcements

Use one live status region for meaningful transitions, not every byte or progress tick. Announce:

- request accepted and the selected operation;
- consent required or consent accepted for this run, without naming opaque session data;
- stage changes: `Validating`, `Resolving`, `Downloading`, `Processing`, and `Publishing`;
- a meaningful progress milestone only when the event stream supplies a safe determinate fraction and throttling prevents announcement noise;
- cancellation requested and then the terminal cancellation result;
- completion only after publication truth is established;
- failure with safe user message and available recovery action;
- completion-with-warning or cleanup warning without converting it to failure;
- local-opener success or safe opener failure.

Do not announce raw URLs, query strings, signatures, signed stream addresses, full paths, browser/profile/session values, authorization values, cookies, tokens, exception chains, or child stderr. Do not announce a percentage when the source denominator is unknown. Do not leave an indeterminate progress announcement active after a terminal event.

Announcement text is a projection of safe Core values. It is not a second event stream and does not alter lifecycle truth.

### 4.3 UI Automation and AT-SPI2 expectations

The implementation must select Avalonia control peers and properties that expose, at minimum:

- role/control type,
- stable accessible name,
- description or help text where needed,
- enabled/disabled state,
- checked/selected state for choices,
- editable value for fields through the platform's normal control semantics,
- focus state,
- progress range/value or indeterminate state,
- live status/log semantics appropriate to the platform.

Validation must inspect the actual tree through Windows UI Automation and Linux AT-SPI2. Framework documentation is not evidence that this product's shell exposes the intended tree. Do not infer native-control behavior from the choice of Avalonia.

## 5. View-state projection over Core contracts

### 5.1 Authority and shape

The shell consumes a presentation read model projected from Core requests and typed events. It does not create a competing lifecycle state machine, infer terminal truth from control state, or apply provider/process policy.

The authoritative lifecycle facts are the `RunIdentity`, `DownloadStage`, sequence, `LifecycleStatus`, cancellation flag, and acceptance result produced by the Core lifecycle reducer in `Application/Lifecycle.cs`. A presentation controller applies the reducer result on the UI thread and publishes a view projection. A view model may hold display fields such as safe text, enabled states, and the last verified output reference, but it must not own alternate terminal semantics.

Recommended projection fields are:

- `ScreenState`: `Idle`, `Validating`, `Resolving`, `Downloading`, `Processing`, `Publishing`, `Completed`, `Cancelled`, or `Failed`;
- `RunIdentity` and last accepted sequence, held only for stale-event filtering;
- `SelectedOperation` and safe form validity;
- `StageText`, `ProgressMode`, `ProgressFraction`, and safe `ActivityText`;
- `IsTerminal`, `CancellationRequested`, and `CanCancel`;
- safe error code/message/retry action, when present;
- a bounded list of safe activity entries;
- a published typed local-media result only after the application has returned one and local re-verification has passed;
- `CanOpenFolder`, `CanOpenInBrowser`, and their safe reasons when disabled.

No field in this projection contains a provider handle, browser-session lease, profile path, cookie, URL object with query/signature, process object, raw argv, child output, exception object, or unrestricted filesystem handle.

### 5.2 Mapping table

The current Core event types are `DownloadProgress`, `DownloadCompleted`, `DownloadCancelled`, and `DownloadFailed`, each carrying a `RunIdentity`, `DownloadStage`, and monotonic sequence. There is no need for the shell to invent additional lifecycle events. Initial idle and stage labels are derived from the current accepted lifecycle snapshot and the event's typed stage.

| Core input / condition | Observable screen state | Progress/activity behavior | Terminal and action behavior |
| --- | --- | --- | --- |
| No active run; no pending validation | `Idle` | No progress; show `Ready`; clear prior activity and session-sensitive state after reset. | `Start` available only when the form is valid; `Cancel` unavailable. |
| Accepted `DownloadProgress` with `Stage == Validating` | `Validating` | Determinate only for a valid `Fraction`; otherwise indeterminate while active. Show safe validation activity. | Form and browser controls lock; `Cancel` requests cancellation. |
| Accepted `DownloadProgress` with `Stage == Metadata` or `Resolving` | `Resolving` | Show safe resolving activity; no fabricated percentage. | Form locks; no automatic user retry is inferred from progress. |
| Accepted `DownloadProgress` with `Stage == Downloading` | `Downloading` | Show the supplied bounded fraction/activity. Video and audio sub-operation wording may be supplied only as a safe application activity value. | Form locks; `Cancel` remains available. A stream refresh is not exposed as an unbounded retry loop. |
| Accepted `DownloadProgress` with `Stage == Processing` | `Processing` | Show determinate or indeterminate mode from the event; show `Remuxing or converting media` only as a safe stage/activity label. | Form locks; `Cancel` requests bounded cancellation. |
| Accepted `DownloadProgress` with `Stage == Publishing` | `Publishing` | Stop presenting download percentage as if it were publication progress; use indeterminate or a separately defined safe publication progress value. | Form locks; publication remains the commit boundary. |
| Accepted `DownloadProgress` with `Stage == Opening` | `Publishing` or a local-open status projection only if a future application contract explicitly uses this stage; it is not a provider-download state. | Do not expose `Opening` as a claim that a remote browser was opened. | Local opening is handled only by the separate opener action and does not alter download terminal truth. |
| Accepted `DownloadCompleted` | `Completed` | Stop progress and announce completion only after the application contract says publication is complete. | The reducer is terminal. Enable local actions only for a returned, verified, freshly rechecked local media file. Do not infer a published path from `StagedArtifact` alone. |
| Accepted `DownloadCancelled` | `Cancelled` | Stop progress; show cancellation truth. If publication already committed, show that the verified output was preserved as a safe warning/result. | The reducer is terminal. No `Open in Browser` unless the preserved output independently satisfies the local-media gate. Enable a new run after reset. |
| Accepted `DownloadFailed` | `Failed` | Stop progress; show only `SafeDownloadError.UserMessage`, safe code/category, and safe retry action. | The reducer is terminal. Enable retry only when the typed error policy says so; never retry 429 automatically. |
| Event run differs from current `RunIdentity` | Current state unchanged; no visible stale transition | Do not append activity, change progress, or announce the event. | Core rejection is respected. It cannot re-enable controls, restore consent, replace output, or overwrite a newer run. |
| Event sequence is not greater than the accepted sequence | Current state unchanged | Ignore the duplicate/out-of-order event for presentation. | Do not announce it and do not change terminal truth. |
| Any event after a terminal reducer state | Current terminal state unchanged | Ignore it. | No action or focus state may regress to active. |

The `Metadata` value in `DownloadStage` is an implementation detail of the typed pipeline. The user-facing resolving label should remain understandable and safe. The shell may distinguish `Metadata` from `Resolving` in an accessible detail string only if the application supplies that safe distinction; it must not imply that a local media file exists for a metadata-only request.

### 5.3 Progress and activity rules

`DownloadProgress.Fraction` is already constrained by Core to 0 through 1, and `Activity` is redacted by Core. The presentation layer still treats all incoming text as safe-display input only; it does not concatenate URLs, paths, process output, or exception text onto it.

- Preserve determinate versus indeterminate mode.
- Clamp nothing silently in the view. An invalid event is rejected by Core and does not update the screen.
- Throttle visual updates and announcements without changing the last accepted sequence.
- Keep the last safe activity visible through a terminal transition when it helps explain the result, then clear it on `Start New Run`.
- The activity log is bounded by count and text length. It is not an export, support bundle, or raw process console.

### 5.4 Cancellation, terminal truth, and reset

`LifecycleReducer.RequestCancellation` changes only a non-terminal snapshot and sets `CancellationRequested`. The view immediately communicates `Cancellation requested` and disables `Start`; it does not claim that work has stopped until `DownloadCancelled` or another terminal event is accepted.

While cancellation is requested:

- ignore later progress rejected by Core;
- keep the cancel affordance available only if the application can still report a bounded wait, otherwise present `Stopping` as a safe status;
- do not show completion before a terminal completion event;
- do not delete, move, or overwrite a published result;
- preserve and report a result if publication already committed, as required by the application contract.

After any terminal event, clear browser consent, browser selection, and all opaque run-scoped session data. A reset clears the form and activity according to the approved reset policy, but never deletes media. `Start New Run` creates a new `RunIdentity`; it does not reuse the prior identity or generation.

## 6. Failure and recovery UX

Safe error display uses `SafeDownloadError.Code`, `Stage`, `UserMessage`, `Retry`, and the opaque diagnostic token only. A diagnostic reference may be displayed in the form `Diagnostic reference available` plus a safe reference value if the owner later approves showing that value; it is never a route to raw logs from the UI. The shell does not display exception causes or contexts.

The following copy is the baseline. Implementations may localize it or make it shorter without weakening its truth.

| Condition | Safe primary copy | Controls and recovery |
| --- | --- | --- |
| Missing yt-dlp (`MissingTool`, provider/resolve stage) | `The downloader component is unavailable. Install or select the approved yt-dlp component, then run Test Environment again.` | No automatic download, network fetch, or silent substitute. `Test Environment` and `Start New Run` remain available. A retry is user initiated after the capability is corrected. |
| Missing FFmpeg (`MissingTool`, processing stage) | `FFmpeg is required to finish this output and is not available. Install the approved external prerequisite, then run Test Environment again.` | Do not promise bundling or an automatic installer. No retry until the environment is corrected or the user deliberately starts a new run. |
| Provider unavailable (`ProviderUnavailable`) | `The provider could not be reached or did not return a usable result. Check your connection and try again later.` | User-initiated `Try Again` may start a new run if the typed retry action permits it. Do not expose upstream detail or request a login. |
| Rate limited (`RateLimited`) | `The provider is rate limiting this request. Wait and try again later.` | No automatic retry, countdown, repeated request, proxy rotation, fingerprint spoofing, or bypass. A visible `Try Again Later` action starts nothing automatically; it returns the user to a new-run path. |
| Stream access denied after the one bounded refresh (`AccessDenied`, downloading stage) | `The media stream remained unavailable after one refresh. Try again later.` | No further automatic refresh. A user-initiated new run may be offered only when the typed error policy permits it. Do not call this a bypass or expose a signed stream address. |
| Publication conflict (`PublicationConflict`, publishing stage) | `A file with that name already exists. Nothing was overwritten.` | Offer `Choose Another Folder`, `Change File Stem`, and `Start New Run` as applicable. Never offer an overwrite checkbox or silently replace the destination. |
| Publication verification failure | `The staged output could not be verified, so it was not reported as complete.` | Keep the final destination unclaimed; permit a user-initiated new run if the typed error allows it. If cleanup itself warns, say so separately without changing the false-completion rule. |
| Cancellation before publication (`Cancelled`) | `Cancelled. No published output was reported.` | Keep `Open Folder` and `Open in Browser` disabled unless an independently verified prior output is selected through a separate future action. Offer `Start New Run`. |
| Cancellation after publication (`Cancelled` with preserved output) | `Cancelled after publication. The verified output was preserved.` | `Open Folder` may be enabled for that verified result. `Open in Browser` still requires the complete local-media recheck. Offer `Start New Run`. |
| Local opener failure | `The verified local media file could not be opened by the operating system.` | Do not retry provider work. Offer `Open Folder` if valid and safe. Do not expose a raw URI, path, or OS exception. |
| Unknown safe failure (`Unknown`) | `The operation could not be completed. Try again later or check the environment.` | User action follows the typed `RetryAction`; otherwise only `Start New Run` and `Test Environment` are offered. No raw diagnostic is rendered. |
| Legacy rollback | `The target shell is not ready for this operation. Continue with the preserved legacy app path while validation is completed.` | Rollback is an operator/release action to select a prior verified artifact or launch the existing `app.py` path. The shell must not silently launch Python, embed a sidecar, or claim that rollback was executed by a UI button unless a separate approved integration exists. |

### 6.1 Retry semantics

The `RetryAction` is authoritative:

- `None`: do not show a retry button; offer correction or a new run.
- `RefreshStream`: this is an application-controlled, bounded fresh stream resolution and may occur at most once for a downloading stream access failure. It is not shown as a general retry loop and is not a service restriction bypass.
- `RetryAfterDelay`: show a user-initiated action only if the application supplies the delay contract. Do not implement a timer or automatic request in the view.
- `UserActionRequired`: show the safe remediation text and no automatic retry.

The shell never translates an error category into a retry on its own. In particular, `RateLimited` never causes an automatic 429 recovery.

## 7. Verified-local-media `Open in Browser` contract

`Open in Browser` is a local-file convenience action. It is not browser-session selection, provider access, a provider-page opener, a login flow, or a way to resume a download.

### 7.1 Enabled conditions

Enable `Open in Browser` only when all conditions are true:

1. The current or preserved terminal result identifies a published, verified local media output.
2. The output is Matroska in generic mode or MP4 in UniFi compatibility mode according to the application result, not merely a requested option.
3. The result is freshly reverified immediately before the action: it exists, is readable, and is non-empty.
4. The current action is not operating on a stale run, staged artifact, unverified artifact, failed result, or in-progress result.
5. The local opener capability is available through the explicit composition boundary.

The button is disabled for an absent result, metadata-only result without a published local media file, pre-publication cancellation, failed or unverified output, a path that no longer exists or is unreadable, and any result whose re-verification fails. The disabled description is safe, for example `Available after a published, verified local media file is rechecked`.

### 7.2 Invocation and separation

The presentation controller passes only the verified local result needed by `ILocalFileOpener.OpenAsync`. The local opener rechecks the file, creates an encoded local `file://` URI through the approved OS boundary, and invokes the OS handler. It receives no source URL, browser-session selection, browser lease, provider handle, network client, retry policy, yt-dlp handle, or process handle.

The shell must not infer that the OS handler is Chrome, Edge, Firefox, or any particular browser. It reports only safe success/failure. It does not display the local URI or full path in accessible names, status, logs, or errors.

A separate `Use browser session` choice is never consulted by `Open in Browser`. Turning provider consent on or off cannot enable, disable, or redirect the local opener except through the independent run reset rules.

## 8. Privacy and redaction invariants

These are information-flow rules, not copy guidelines. A design or implementation that violates one is a release blocker under ADR-0003.

### 8.1 Never cross the safe presentation boundary

The UI state, accessible tree metadata, announcements, activity log, error dialogs, diagnostic reference text, and any future support surface must never contain:

- source URL query components, signatures, signed media URLs, authorization values, or token-like values;
- browser profile paths, cookies, keyring values, browser database data, opaque session data, or generated provider arguments;
- raw yt-dlp, FFmpeg, or other child-process stderr/stdout, raw argv, executable paths, or unrestricted process diagnostics;
- full home, temporary, staging, or destination paths when a safe basename, category, or capability status is sufficient;
- exception chains, traceback locals, provider response bodies, or unbounded diagnostic context.

The view never receives these values. Redaction must happen before values cross the Core/application observer boundary, and the presentation layer must not reconstruct them from another object.

### 8.2 Safe display patterns

Use:

- stable enum names or localized safe stage labels;
- bounded user messages from `SafeDownloadError.UserMessage`;
- safe activity strings from `DownloadProgress.Activity`;
- a safe output basename only where the application explicitly returns it for completion;
- size or size category supplied by a safe result;
- capability categories such as `Available`, `Missing`, or `Unavailable`;
- an opaque diagnostic token only if the approved UI contract explicitly permits it.

Do not use:

- URL-derived titles or paths before Core safe-filename normalization;
- string interpolation of request objects, `VideoReference`, media-plan details, `ProcessSpec`, browser leases, exceptions, or output locations into labels;
- a “copy details” function that copies raw diagnostics;
- tooltips that contain hidden values not present in the visible text;
- automation IDs, test IDs, or accessibility descriptions derived from secrets or paths.

### 8.3 Lifetime and stale-run rules

Consent, browser selection, output staging references, safe errors, and activity entries are run-scoped. On success, failure, or cancellation, clear browser-session selection and opaque session state. On a new `RunIdentity`, discard old event subscriptions and ignore old events by exact run identity and sequence. A stale event cannot restore text, output buttons, focus, consent, or diagnostics from a prior run.

No UI export, clipboard diagnostic, crash report, telemetry payload, or persistence schema is authorized by this document. Any future support surface requires a separate retention/redaction decision.

## 9. Component and view-model boundary

### 9.1 Presentation components

The proposed presentation structure is intentionally thin:

- `DownloadView`: Avalonia layout and controls only; no provider, process, filesystem, or browser calls.
- `DownloadViewModel`: bindable safe form fields and the presentation read model; no external handles and no alternate lifecycle reducer.
- `PresentationController`: validates form intent through Core/application contracts, subscribes to typed events, applies the Core reducer result, dispatches commands, and marshals updates to the UI thread.
- `AccessibilityAnnouncer`: maps safe stage/terminal transitions to platform-accessible status updates; it never accepts raw diagnostics.
- `EnvironmentSummaryView`: renders safe capability categories and remediation copy.
- `CompletionActionsView`: renders `Open Folder`, `Open in Browser`, and `Start New Run` only from independent capability predicates.
- `App` composition root: manually composes the application service, typed Core ports, infrastructure adapters, observer, clock, opener, and view/controller. No DI container is assumed.

Names are proposed presentation responsibilities, not authorization to create files in this documentation phase.

### 9.2 Forbidden dependencies

Views and view models must not import or hold:

- Avalonia-independent provider/session/process implementation details;
- yt-dlp or FFmpeg executable paths, argument vectors, process objects, stdout/stderr readers, or cancellation process handles;
- browser profile, cookie, keyring, authorization, or opaque lease data;
- network clients, source URL objects with sensitive components, filesystem handles, or raw exceptions;
- a second retry, cancellation, publication, or terminal-state policy.

Commands cross the boundary as typed intent, such as a `DownloadRequest` composed from validated fields, `CancellationToken`/run cancellation request, environment probe request, or verified local-open request. The controller cannot bypass Core policy by changing button state locally.

### 9.3 Manual composition at the App root

The root selects concrete adapters for the environment and passes only their typed interfaces into the application service. It is the only place that knows which executable, filesystem, browser-session, process, local-opener, diagnostics, observer, and clock implementations are selected. Views receive the resulting application/controller interfaces, not the adapter graph.

This preserves ADR-0001's one-way dependency direction: App/presentation depends on Core; Infrastructure implements Core ports; Core does not reference Avalonia, process APIs, network APIs, browser APIs, or concrete filesystem APIs.

## 10. Theme, contrast, scaling, dialogs, and reduced motion

### 10.1 Themes and high contrast

Provide dark and light themes through shared resources, with the restrained amber accent adapted for contrast. Honor Windows high-contrast settings where exposed and provide a Linux presentation that remains legible when desktop contrast or theme settings change.

Validation must confirm:

- body, muted, focus, link/action, success, warning, and error text meet the adopted contrast target;
- focus indicators remain distinct from borders and selection;
- disabled text remains distinguishable without becoming unreadable;
- status icons have accessible names and text equivalents;
- error and warning states are conveyed by text and structure, never color alone;
- selected browser and operation choices remain apparent without relying on a subtle fill change.

Do not use a background image, animated glow, or decorative texture as a status cue.

### 10.2 Scaling and constrained layouts

The supported layout must be tested at 100%, 125%, 150%, and 200% Windows scaling and equivalent Linux desktop scaling configurations available on the declared support rows. Also test increased text size and a narrow but usable window.

The shell must:

- preserve labels and controls without clipping or overlapping;
- let long output folders and file stems elide safely without corrupting the underlying value;
- keep primary actions reachable without requiring horizontal scrolling;
- allow the activity log to expand while retaining the form and terminal status;
- handle long safe titles/stems through Core normalization and visual truncation, not raw path display;
- provide a documented minimum window size based on actual layout inspection, not an arbitrary default.

Exact dimensions and typography tokens remain implementation details to be measured in the Avalonia spike. This document does not claim a measured artifact, startup time, or support result.

### 10.3 File and folder dialogs

`Choose Output Folder` invokes the platform file/folder dialog through an application boundary, never by exposing a filesystem handle to the view. The dialog must:

- be keyboard operable;
- support cancellation without changing the prior valid destination unexpectedly;
- handle paths with spaces and the declared path/permission cases;
- return a typed selected destination to the controller;
- avoid placing full paths into status announcements or error text;
- preserve safe focus when it closes.

The exact Avalonia dialog implementation and native integration remain a Phase 2 implementation and platform validation gate. Do not call a dialog “native” until the actual behavior is verified; Avalonia controls are not thereby Win32 or GTK controls.

### 10.4 Reduced motion

Respect OS reduced-motion preferences where available and provide a no-motion presentation when requested. In reduced-motion mode:

- do not animate progress indeterminacy with decorative transitions;
- use a static busy indicator or an accessible busy state with periodic text updates;
- do not animate focus, completion, error, or layout changes;
- keep progress updates truthful and rate-limited;
- never replace a textual stage announcement with motion.

Motion intensity remains deliberately low even when reduced-motion is not requested.

## 11. Platform validation and release gates

The initial support declaration is Windows 10/11 x64 and Linux x64 on the Ubuntu/Debian family using X11 or XWayland, subject to evidence. Native Wayland, ARM64, other distributions, and macOS are not silently included.

### 11.1 Windows 10/11 x64 checklist

Record evidence on clean test machines or equivalent isolated environments for both declared Windows versions:

- startup and shutdown at the approved .NET SDK and exact Avalonia patch;
- keyboard-only traversal in the order in section 3, including consent Escape/close behavior;
- UI Automation tree roles, names, descriptions, states, focus, progress, and live status behavior;
- Narrator and/or NVDA task completion for entering a request, selecting operation, choosing a folder, consenting, starting, cancelling, reading an error, and opening a verified local media file;
- 100%, 125%, 150%, and 200% scaling plus increased text-size behavior;
- dark, light, and Windows high-contrast presentation;
- folder dialog cancellation, paths with spaces, permissions, long safe names, and destination collisions;
- process cancellation and descendant cleanup evidence from the application/adapters, without surfacing raw child output;
- OS-mediated local `file://` opening and safe default-handler failure handling;
- no stale-event control re-enablement after a new run;
- no sensitive values in UI automation properties, accessible names, logs, retained errors, or crash/support surfaces;
- evidence that no native Win32-control claim is being made for Avalonia-rendered controls.

### 11.2 Linux x64 Ubuntu/Debian-family X11 and XWayland checklist

Record the exact distribution, desktop environment, display server, compositor, .NET SDK, and Avalonia patch. Test at least the declared X11 path and an XWayland session where available:

- startup, shutdown, focus, keyboard traversal, and consent dismissal;
- AT-SPI2 tree roles, names, descriptions, states, focus, progress, and status behavior;
- Orca and/or Accerciser inspection and a screen-reader task pass for the core request flow;
- scaling, text size, theme, contrast, and reduced-motion behavior supported by the desktop session;
- folder dialog keyboard behavior, cancellation, paths with spaces, permissions, and collisions;
- process cancellation/descendant cleanup and bounded output handling;
- OS-mediated local opening and safe default-handler failures;
- stale-event rejection and terminal control locking;
- sensitive-value absence from accessible properties, activity, errors, and diagnostics;
- evidence that the tested display path is X11/XWayland and that no universal Linux or native-control claim is inferred.

### 11.3 Explicitly unverified and deferred gates

The following are limitations until separately tested and recorded:

- ARM64 Windows or Linux: no support claim without target-native build, startup, path/process, scaling, accessibility, and package evidence.
- Native Wayland: no support claim from an X11 or XWayland result. It requires its own Avalonia backend, desktop/compositor, dialog, accessibility, scaling, and opener evidence.
- Linux distributions outside the Ubuntu/Debian-family row: no universal support claim.
- macOS: outside the initial support tier.
- Native handler, semantic MP4/Unifi compliance, FFmpeg licensing/bundling, EJS/Deno ownership, package formats, signing, SBOM/notices, and rollback are separate gates under ADR-0002 and ADR-0004.
- Screen-reader behavior cannot be inferred from Avalonia documentation or a headless test.

Release evidence must include the tested OS/distribution and architecture, display server/compositor, exact SDK and dependency versions, artifact revision, test tool versions, screen-reader/accessibility inspection results, scaling/theme results, dialog/opener results, process/cancellation results, and a redaction scan. Sensitive source values, real browser profiles, credentials, cookies, signed URLs, and raw child diagnostics must not be included in the evidence bundle.

A failed UIA/AT-SPI2, keyboard, scaling, dialog, opener, process, privacy, packaging, or rollback gate is a no-go for the corresponding release row. It does not authorize a silent Python GUI, Tkinter/PySide6 target, webview shell, Python sidecar, Tauri substitution, or service bypass.

## 12. Implementation acceptance checklist

Before the Avalonia shell is considered ready for the next integration gate, verify:

- one request and one video are enforced through Core policy;
- metadata, video, and audio are visibly distinct operations;
- output folder and file stem map to typed Core values; video runs expose one off-by-default UniFi compatibility toggle and otherwise use generic Matroska output;
- browser-session consent is unchecked by default, selected per run, supported-browser-only, and cleared on every terminal path;
- `Open in Browser` is a separate verified-local-media action and never touches provider/session/network code;
- all active, terminal, retry, cancellation, and stale-run behavior is projected from Core lifecycle results;
- no view or view model holds provider, browser, process, network, filesystem, raw exception, or raw diagnostic state;
- safe accessible names/descriptions, focus-visible behavior, keyboard order, live announcements, and disabled reasons are present;
- determinate/indeterminate progress is truthful and stops at terminal state;
- missing tools, rate limiting, bounded stream-refresh exhaustion, publication conflicts, cancellation, local opener failures, and legacy rollback have truthful copy and no invented automation;
- high contrast, themes, scaling, reduced motion, dialogs, long values, and constrained layout are covered by tests;
- UIA and AT-SPI2 evidence exists for every release row before support is claimed;
- no URL query/signature, profile path, cookie, authorization value, token, signed URL, child stderr, exception chain, or opaque browser-session data crosses the safe presentation boundary;
- no live service, credentials, release, packaging, cutover, or legacy-source change is required by this document.

## 13. Relationship to frozen decisions

This contract preserves, rather than reopens, the approved direction:

- Avalonia 12.x over .NET 10 LTS is the selected presentation stack; exact pins remain a bootstrap/release gate.
- Core remains UI-independent, with Infrastructure adapters behind typed ports and manual composition at the App root.
- yt-dlp remains an official pinned standalone executable adapter; metadata, video, and audio operations remain distinct.
- FFmpeg remains an explicit prerequisite/adapter concern, not a UI implementation or silently bundled dependency.
- Browser-session access remains default-off, explicit, in-memory, no-export, no-persistence, and no-bypass.
- Publication remains staged, verified, non-overwriting, and the commit point for completion truth.
- One bounded stream-403 refresh remains distinguishable from no automatic 429 recovery.
- The local opener remains a separate verified-local-media capability.
- Windows 10/11 x64 and Linux x64 Ubuntu/Debian-family X11/XWayland are the initial declared rows only after evidence; ARM64 and native Wayland remain unverified.
- `app.py` remains an operational rollback reference, not a target shell, sidecar, or hidden compatibility dependency.

No section of this document claims that the Avalonia shell, adapters, screen-reader behavior, process handling, package artifacts, or release gates have been implemented or exercised.
