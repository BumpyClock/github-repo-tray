# Architecture and porting notes

GitHub Tray is a C# / WinUI 3 notification-area app inspired by
[RepoBar](https://github.com/steipete/RepoBar). It implements account activity,
authored PRs, review requests, recently pushed repositories, contributions, and
Copilot quota. It reuses GitHub CLI authentication rather than porting macOS
authentication or platform services.

RepoBar is MIT licensed and is a behavioral reference, not a build dependency.
The app does not copy its Swift implementation or artwork. Preserve upstream
copyright and license notices if code or assets are reused.
[CodexBar](https://github.com/steipete/CodexBar), also MIT licensed, is a reference
for the Copilot usage endpoint, not a dependency or credential source.

## Module ownership

| Component | Responsibility |
| --- | --- |
| `GitHubTray.Core` | Domain snapshots, section freshness, URL/JSON validation, GitHub CLI process lifecycle, read-only API queries, settings, and versioned account-cache persistence. No WinUI dependency. |
| `GitHubTray.AppState` | The refresh session: single-flight requests, recovery data, cache-clear coordination, immutable publication, cancellation, and shutdown. References Core, not WinUI. |
| `GitHubTray.App` | WinUI views and MVVM projection, native timers, settings orchestration, tray lifecycle, placement, and browser launching. |
| `GitHubTray.Core.Tests` | Fixture-based API, transport, settings, and cache tests. |
| `GitHubTray.AppState.Tests` | Production session and Core behavior with fixture transport responses. |
| `GitHubTray.App.Tests` | Source-linked production view-model and presentation-helper tests without a WinUI runtime. |

The automated test projects do not require live authentication or network access.
Commands and native UI checks are in the [README](../README.md#tests).

## Refresh and account trust

`DashboardRefreshSession` separates three concerns:

- Saved dashboard data consists of account-scoped section successes, with
  original timestamps and cache provenance.
- Verification state records whether the displayed account is pending
  verification, verified for this process, or failed verification.
- Published state is the immutable snapshot eligible for display. It may contain
  saved, explicitly unverified data or verified data with stale sections.

WinUI projects the session state on the UI thread. It does not own another
recovery snapshot. Immutable snapshots can be shared; mutable inputs are frozen
at the publication boundary.

Startup, periodic, and manual requests carry their refresh reason into
`DashboardService`. Overlapping requests share one in-flight task. If a manual
request overlaps an automatic refresh that reused sections, the shared task
includes at most one forced full follow-up. Additional manual triggers share it.
Cancelling a caller's wait does not cancel the session's work.

Core applies the [automatic freshness windows](../README.md#refresh-and-saved-data)
after the initial identity request. Reuse preserves the original section,
successful-fetch timestamp, successful-empty state, and known error. Manual
refresh bypasses freshness. Timers stay in the App, are not restarted on refresh
completion, and do not add immediate retry loops.

### Startup and identity checks

The primary instance starts the refresh before constructing the window. The
window adopts that session and task, even if the task has already completed.
One settings-load task supplies both startup freshness and Preferences; its
refresh interval is resolved before startup Activity/PR reuse is decided.
The panel opens with loading feedback rather than waiting for GitHub.

Local cache hydration publishes eligible last-used-account data before Core
awaits the first `GET /user`. The header marks it saved and unverified. If the
identity request fails, the saved snapshot remains readable with a verification
error. Displayability is not proof of account trust.

Before fetching new account-specific data, Core verifies `/user`. It verifies the
account again before publishing the completed live snapshot. GraphQL viewers and
the Copilot response login must match the expected account. A confirmed change
removes the previous account's published content immediately; a mismatch during
the refresh rejects the new snapshot rather than combining accounts. If startup
confirms a different account, only that account's own eligible cache can replace
the saved content.

Individual section failures retain only that account's eligible last-success
data and timestamp, with an explicit stale indicator. An identity lookup failure
is an error, not a successful refresh.

### Visibility and shutdown

Hiding releases the page, presentation rows, popups, and acrylic backdrop while
retaining the tray, native shell, settings, and session. The local
timestamp/countdown clock and dashboard projection pause; scheduled API refresh
continues. Reopening applies the latest state and reconstructs the page before
showing it, without another fetch. Selected mode and preference values survive,
but list scrolling and transient popups do not.

This reduces retained presentation objects at the cost of rebuilding them on
reveal. It does not guarantee an immediate return of memory to Windows.
While visible, unchanged sections retain their presentation rows. Check-detail
view models and flyout content are created on demand and reused only for the
card's snapshot; recycled or unloaded cards release popup content and avatars.
Avatar decoding is bounded to 96 pixels for the 24-DIP display.

Shutdown stops timers and settings work, rejects new session refreshes, cancels
and drains active refresh/clear work, and prevents late publication. The App also
waits for initialization and settings work before closing.

## Persistence

`SettingsStore` and `JsonDashboardCacheStore` use source-generated JSON metadata.
Settings and dashboard records are separate under the package's user-local data
folder. Malformed settings produce a visible warning and are replaced only when
the user saves a valid setting.

### Account cache

Records are partitioned by host and stable user ID, retaining the login for
display. A document schema and per-section/query revisions prevent incompatible
data from being reused. Each successful section is reusable for less than seven
days; equality with that boundary is expired. Reads and failed refreshes do not
renew timestamps. Successful empty responses replace previous nonempty results.
The durable store and in-memory recovery use the same retention policy.

Atomic replacement and a store gate protect the previous valid record from
failed, cancelled, or concurrent writes. Malformed, incompatible, expired, and
inaccessible records produce diagnostics without blocking live data.

An atomic `last-account.json` selector records the last confirmed identity, not
the section with the newest success time. If the selector is absent, migration
examines the newest owned account-record file by filesystem write time and saves
the selection only if that record is reusable. An invalid or expired newest
record produces a diagnostic without falling back to an older account. An
unreadable or incompatible existing selector likewise does not select another
account silently.

Cache records may contain private dashboard metadata and Copilot quota. They
must not contain credentials, authorization headers, or raw authentication
errors. Offline display deliberately permits saved private data before account
verification and after verification failure. Trust labels must never imply that
verification or refresh succeeded when it did not.

### Clearing

**Clear cached data** advances store and session invalidation generations. It
removes cache-owned account records, the selector, and interrupted temporary
writes, and clears recovery/freshness input. The current snapshot may remain
displayed but cannot satisfy automatic reuse. The next accepted refresh must
fetch all six sections.

Pre-clear reads, writes, hydration, and refresh completion cannot cross the
generation boundary. Only successful verified post-clear results can repopulate
storage. Repeated clears share one operation. Manual refresh during deletion
shares a task covering deletion and one forced full refresh afterward.

Deletion runs off the UI thread and is serialized with reads and writes. Partial
deletion reports deleted/failed counts and leaves reuse invalidated. It never
deletes settings, credentials, or unrelated files.

## GitHub API contract

| Data | API |
| --- | --- |
| Account | `GET /user`, before and after section loading |
| Contributions | Read-only `POST /graphql`, using `viewer.contributionsCollection.contributionCalendar` |
| Copilot usage | `GET /copilot_internal/user` |
| Activity | `GET /users/{login}/events?per_page=30`, plus one GraphQL batch if the events reference PRs |
| My PRs | GraphQL `search(type: ISSUE, first: 30)` for `is:pr author:{login} sort:updated-desc` |
| Reviews | The same selection for `is:pr is:open review-requested:{login} sort:updated-desc` |
| Repositories | `GET /user/repos?sort=pushed&direction=desc&per_page=30&affiliation=owner,collaborator,organization_member` |

`GitHubCliApi` launches `gh api` without a shell, fixes the host to GitHub.com,
disables prompts and inherited HTTP debugging, and uses a 30-second timeout per
call. Cancellation terminates and drains the child process. Validation runs
before process creation, including for already-cancelled requests. Raw stderr is
classified into fixed messages rather than exposed or stored.

The app does not change GitHub CLI's saved-account selection or environment-token
precedence. Private data depends on credential permissions and organization SSO.
Browser navigation accepts only HTTPS GitHub.com URLs.

GraphQL accepts restricted read-only field selections and exact generated PR
operations. The latter validate logins or repository/number references, fixed
selections, and limits; arbitrary arguments and mutations are rejected.
GraphQL errors, including errors alongside partial data, fail the section instead
of silently replacing its last complete result.

A successful full refresh uses eight API calls when Activity has no PR
references, or nine when it requires the detail batch. A periodic refresh with
fresh repositories, contributions, and Copilot uses five or six respectively.
An all-fresh eligible startup uses only the two identity calls. Each call launches
one `gh` process; request counts are not a latency guarantee.

### PRs and activity

My PRs includes all PR states, ordered by update time. Reviews contains open
requests awaiting the account's review. Each query returns up to 30 PRs, the first
10 labels with their total, and the latest commit's `statusCheckRollup` with up to
100 check runs/legacy status contexts and their total.

The rollup remains authoritative when context details are truncated. Partial
counts must not appear as complete progress. No rollup means "No checks", not
success. Unknown, queued, running, cancelled, neutral, skipped, and
action-required outcomes retain distinct states. PR metadata and checks belong
to one immutable snapshot; old checks must not attach to a new head commit.

Activity sorts and deduplicates the latest 30 events, then groups PR events,
reviews, review comments, and issue comments on PRs by repository and PR number.
Groups retain the latest event time/action and count within that window.
Non-PR events remain separate. The detail query batches at most 30 PRs, including
closed/merged ones, using one shared fragment and no per-PR fan-out.
An inaccessible PR or failed batch fails the Activity section; it does not
produce invented metadata or checks.

### Contributions

The calendar supplies the total, week boundaries, daily dates/counts, and
intensity levels. Omitting `from` and `to` uses GitHub's default last-year
interval. Partial first/last weeks are retained; future and out-of-range cells
remain blank. Contribution counts are never derived from events. Malformed
calendars and GraphQL errors are failures even when HTTP succeeds.

### Copilot quota

`copilot_internal/user` is undocumented and may change or deny access. It is not
the organization-admin metrics API, a session-usage endpoint, or a complete bill.
No Copilot CLI subprocess, separate OAuth flow, or credential extraction is used.

`quota_snapshots` supplies premium interactions, chat, and completions.
`token_based_billing` selects the AI-credit label for premium interactions;
finite meters use `100 - percent_remaining`. Other counters are not assumed to
be request counts, credits, or money. Unlimited quotas have no meter, absent
quotas are omitted, and missing all supported quotas is an error. The UI omits
unlimited chat/completion rows.

Quota-specific nonzero Unix reset times take precedence over
`quota_reset_date_utc`, then `quota_reset_date`. Missing reset times are disclosed,
not guessed. The existing one-minute presentation clock updates the countdown
without another API call. An elapsed reset reads "Reset pending" until fresh
data arrives. Usage shares dashboard retention, freshness, account verification,
and cancellation rather than adding its own timer or retry loop.

## Native UI constraints

- Use native WinUI controls, not a WebView. The taskbar-adjacent panel is normally
  420 DIPs wide, constrained by the available work area. Acrylic falls back to an
  opaque theme surface where unsupported.
- Keep the footer limited to Preferences, Refresh, and Quit. Closing, Escape,
  and focus loss hide the panel; an unavailable tray prevents hiding so the user
  does not lose access.
- Activity, My PRs, and Reviews share `PullRequestCard`. It emits navigation
  requests to its host; it does not own account verification or browser launch.
  CI details must open without activating the PR row.
- Keep PR state, review decisions, and CI distinct. Status chips name outcomes,
  including stale results, rather than relying on color. Label tints use GitHub
  colors with platform text brushes; high contrast removes the tint.
- The contribution graph has seven weekday rows and pixel-aligned square cells.
  Small fits the loaded year at the standard width; larger presets scroll without
  shrinking their cells. The container fits the preset, aligns left, and reserves
  scrollbar space only when history overflows.
- Day inspection uses hover/keyboard tooltips and a single keyboard focus stop.
  Arrow keys move selection; Home/End reach the period boundaries. Calendar
  replacement retains an available selected date or selects the latest day.
  Changed values notify UI Automation; only changed user selections request a
  polite live-region announcement.
- Keep the actual date range accessible without a visible normal-state footer,
  legend, or selected-day strip. Loading and error status remain visible.
  Zero-activity cells use a neutral fill and outline; high contrast uses opaque
  system brushes.
- Loading, empty, and error states reserve the same per-preset geometry.
  First-load animation affects cells, not an overlay or gaps. Hide, unload,
  Preferences, reduced motion, and high contrast stop animation. Refresh retains
  an eligible calendar instead of replacing it with a skeleton.
- Follow system typography, theme, focus, accessibility, and motion preferences.
  Do not initiate authentication, broaden permissions, or change Windows startup
  settings in the background.

NativeAOT interop and packaging constraints are in the
[release guide](RELEASING.md).

## Scope limits

There is no pinned-repository dashboard, repository-specific graph, local checkout
or Git-action integration, standalone workflow-run monitor, or notifications
inbox. The app does not provide native OAuth, an account/Enterprise-host selector,
ETag/backoff scheduling, or automatic cache clearing on GitHub CLI sign-out.
It does not fabricate unimplemented metrics.

The build produces unsigned MSIX bundles. Production identity, signing,
installation/update policy, and opt-in launch at sign-in require separate
distribution decisions. RepoBar's AppKit menus, Keychain, Sparkle, Finder/Terminal,
and launch-at-login integrations are platform-specific; they cannot be ported by
line-by-line translation.
