# RepoBar to WinUI 3

## Goal and agreed first milestone

Build an original C# / WinUI 3 Windows notification-area app inspired by
[RepoBar](https://github.com/steipete/RepoBar). The first milestone focuses on the
signed-in user's GitHub activity, authored pull requests, review requests, and
recently pushed repositories. Authentication reuses GitHub CLI. The follow-up
contribution-graph milestone adds the real last-year GitHub contribution calendar
above those activity modes.

Reference checkout: `%USERPROFILE%\Projects\references\RepoBar`.
Reference revision: `8ec7033a8404e815956a7009c97bdc7f5f3f6bc2`.
RepoBar is MIT licensed. Its source is a behavioral reference, not a build
dependency. No Swift implementation or RepoBar artwork is copied into this port.
Preserve its copyright and MIT notice if code or assets are reused in a future
milestone. The app currently uses the official WinUI template's placeholder icon.

## Implementation sequence

1. Establish a typed, independently testable GitHub data boundary. Invoke `gh api`
   without a shell, with read-only methods, a fixed GitHub.com host, cancellation,
   and a request timeout. Never retrieve or persist tokens.
2. Build a native WinUI tray shell with account identity, four activity modes,
   browser links, refresh, and settings. Closing the panel must not quit the app.
3. Add bounded periodic refresh, per-section error states, last-success retention
   during transient failures, and validated, atomic local settings persistence.
4. Verify response parsing, authentication failures, account switching, partial
   failures, cancellation, persistence, native launch, and tray interactions.
5. Extend toward RepoBar parity only after the activity workflow is usable.

## Architecture and ownership

| Component | Responsibility |
| --- | --- |
| `GitHubTray.Core` | Domain snapshot, explicit refresh reasons, section-freshness selection, safe URL/JSON boundary, GitHub CLI process lifecycle, read-only REST and GraphQL queries, settings storage, and versioned account-cache persistence/clearing. No WinUI dependency. |
| `GitHubTray.AppState` | App-owned refresh session: single-flight refresh, retained recovery data, clear-generation fencing, coalesced forced manual follow-up, immutable published state, refresh cancellation and draining. References Core, with no WinUI dependency. |
| `GitHubTray.App` | WinUI views and MVVM projection, native refresh scheduling, settings orchestration, Windows notification-area lifecycle, window placement, browser launching. |
| `GitHubTray.Core.Tests` | Deterministic response fixtures; no live authentication or network dependency. |
| `GitHubTray.AppState.Tests` | Production refresh-session and Core behavior with fixture transport responses; no WinUI runtime, live authentication, or network dependency. |
| `GitHubTray.App.Tests` | Source-linked production heatmap view-model tests without a WinUI runtime or live data. |

The App-owned refresh session separates saved data, verification, and publication:

- **Saved dashboard:** durable account-scoped per-section successes eligible for
  cache-first display with their original timestamps and provenance.
- **Verification state:** independently records whether the displayed account is
  still being checked, verified for this process, or failed verification.
- **Published dashboard:** data currently eligible for display. It may be saved
  and explicitly unverified, or verified with individually stale sections.

The session exposes one immutable current state and shares one in-flight refresh
across startup, timer, toolbar, keyboard, and tray requests. Entry points carry
`Startup`, `Periodic`, or `Manual` through the session into Core. WinUI projects
state into bindings on the UI thread; it does not own a second recovery snapshot.
Displayability and account trust are separate: an identity or network failure
keeps saved rows, contributions, repositories, PR checks, and Copilot usage
readable while marking the account unverified and exposing the verification error.

Core evaluates same-account successful-section timestamps after the initial
identity operation. Startup can reuse Activity and both PR lists while they are
younger than the configured refresh interval. Periodic cycles always fetch those
three sections. Repositories and contributions use a 15-minute automatic window;
Copilot uses five minutes. Boundaries are exclusive: age equal to the window
requires a fetch. Manual refresh bypasses every window.

The durable JSON store and an optional in-memory recovery snapshot feed the same
Core hydration policy, with no second TTL implementation or identity probe.
On process startup, Core resolves the store's durable last-used account and
publishes its eligible sections before awaiting `/user`. Verification failure
leaves that snapshot published but unverified. A confirmed different REST account
or account/viewer mismatch clears the prior snapshot before pending section work
can publish. If startup confirms a different account, only that account's own
eligible cache can replace the previous account's saved content. Reuse returns the original section object, timestamp,
successful-empty state, and failure provenance.

Core verifies `/user` before requesting new account-specific data and verifies the account
again before publishing the completed live snapshot. A detected account change,
including a mismatched GraphQL viewer, rejects the entire refresh rather than
mixing data under the original account label. Individual section
failures keep that section's last successful items and timestamp, clearly marked
as stale. A different account invalidates the previous snapshot's cached sections.
An identity lookup failure is an error, not a successful refresh; saved data may
remain visible only with failed verification state.

Native timers remain in the App, outside the refresh session. On shutdown the
App stops those timers and cancels settings work; the session stops accepting
refreshes, cancels and drains its active refresh, and prevents late publication.
The App also waits for initialization and settings work before closing.
While hidden, the panel stops only its local timestamp/countdown clock and catches
up on reveal. Scheduled API refresh continues at the configured interval.
Dashboard projection also waits while hidden: the latest session state is applied
before reveal, including verification failures that retain saved data and proven
account changes that remove old-account data.
The hidden panel releases its page and presentation rows while retaining the
native window shell, tray, settings, and refresh session. Reopening rebuilds the
page from that session rather than refetching data. This intentionally exchanges
reopen work for a smaller retained presentation graph; it does not promise that
the allocator immediately returns all released memory to Windows. While visible,
unchanged section metadata and item identities retain existing presentation rows.
Already deeply immutable session snapshots are shared instead of being recopied;
mutable inputs are still frozen at the publication boundary.
The repeating timer is not restarted when a refresh completes, so Activity and
PR cadence stays tied to scheduled ticks rather than completion time. If a manual
request overlaps an automatic cycle that reused sections, the shared flight runs
at most one forced full follow-up. More manual triggers share that same task.
Cancelling a caller's `WaitAsync` does not cancel session-owned work; only session
shutdown controls the lifetime token.

The primary instance starts its initial refresh before constructing the window.
Window initialization adopts that exact session and refresh task, including an
already-completed task, rather than issuing a second startup refresh. Settings
loading runs independently of the initial data projection. The panel opens
immediately with loading feedback; early fetching overlaps setup without delaying
the first show. Local cache hydration completes independently of the initial
identity request, so saved data can reach WinUI while `/user` is still blocked.
Account-state changes are projected during every refresh, so confirmed account
changes remove old content before delayed section requests finish. The persisted refresh interval
loads concurrently and is applied before startup Activity/PR freshness selection.
Cache misses and identity failures do not add another identity probe.
One settings-load task supplies both startup freshness and the window's settings
projection. Startup owns its cancellation and observes it during failed window
construction or shutdown.

Successful dashboard sections are persisted under the app's user-local
`dashboard-cache` directory, separate from `settings.json`. Records use
source-generated JSON, a document schema revision, per-section/query revisions,
and host/stable-user-ID partitions. Each section expires independently at an age
of seven days; equality with the seven-day boundary is expired. Reads and failed
refreshes do not renew timestamps. Atomic replacement and a single store gate
protect the last valid record from failed, cancelled, or concurrent writes.
Malformed, incompatible, expired, and inaccessible records become explicit cache
miss diagnostics without preventing live data from loading.
The store also owns an atomic `last-account.json` selector. Confirmed identity,
not section freshness, updates it. Existing installations without the selector
migrate once by examining the newest owned account-record file by filesystem write
time. A reusable newest record is persisted as the selection; a malformed,
incompatible, or expired newest record returns its explicit diagnostic without
falling back to an older record. Later section timestamps cannot change a
successful selection.
Malformed, incompatible, or inaccessible selectors produce explicit diagnostics;
they do not trigger migration to a different saved account.

The settings surface exposes **Clear cached data** for every cached account.
Clearing advances both store and refresh-session invalidation generations, removes
only cache-owned account records, the selector, and interrupted temporary writes, clears private
recovery/freshness input, and forces the next accepted refresh to fetch all six
sections. The current published snapshot may remain rendered but is not passed
back to Core as reusable state. A pre-clear read, write, hydrated publication, or
refresh completion cannot cross the generation fence; only verified post-clear
successes can repopulate storage. Manual refresh from the tray, accelerator, or
other entry point during deletion shares one task that covers clearing and one
forced full refresh afterward.

Deletion runs away from the UI thread and is serialized with reads and atomic
writes. Concurrent clear requests share one task. Shutdown cancels and drains
clearing with other accepted lifecycle work. Partial deletion reports exact
deleted/failed file counts and keeps the session invalidated; it never claims
success or deletes `settings.json`, credentials, or unrelated files.

The refresh interval and contribution cell-size preset remain in the settings
file. A malformed settings file produces a visible warning; it is replaced only
when the user explicitly saves a valid setting. Cache records can contain private
dashboard metadata and Copilot quota details, but never credentials,
authorization headers, or raw authentication errors. The product intentionally
allows those saved private surfaces to appear before network verification and to
remain readable when verification fails; trust labels and errors must never imply
that verification or refresh succeeded.

## GitHub contract

| View | API |
| --- | --- |
| Account | `GET /user` |
| Contributions | `POST /graphql` with a read-only `viewer.contributionsCollection.contributionCalendar` query |
| Copilot usage | `GET /copilot_internal/user` through the same GitHub CLI account |
| Activity | `GET /users/{login}/events?per_page=30`, then one bounded GraphQL batch for referenced PRs |
| My PRs | Read-only GraphQL `search(type: ISSUE, first: 30)` for `is:pr author:{login} sort:updated-desc`, including all PR states and latest-commit checks |
| Reviews | The same GraphQL selection for `is:pr is:open review-requested:{login} sort:updated-desc` |
| Repositories | `GET /user/repos?sort=pushed&direction=desc&per_page=30&affiliation=owner,collaborator,organization_member` |

Each activity list is bounded, not a complete history or a total count.
Activity means the user's generated events, not the followed-user feed,
notifications inbox, or contribution calendar. GitHub's Events API can be delayed
and returns a limited historical window. Private data depends on the active
credential's repository access and organization SSO. GraphQL errors, including
responses containing partial data, are reported rather than silently replacing
the last complete section.

### Copilot usage contract

Reference checkout: `%USERPROFILE%\Projects\references\CodexBar`, revision
`b202fc0c8ce7f6b39d6acc832f5cb15ede1e4dd2` (MIT licensed).
Its Copilot provider confirms the same internal usage endpoint. The live GitHub
response also includes newer quota/reset fields not modeled by that reference
revision; this implementation follows the observed response rather than copying
its older quota assumptions. No CodexBar code or artwork is copied.

The original native Copilot card uses `gh api copilot_internal/user`, not a
Copilot CLI subprocess or a separate OAuth flow. The response login must match
the initially verified GitHub account, and the existing final identity check
still gates publication. Missing access, malformed responses and unknown quota
schemas produce a section error; account mismatches reject the full refresh.
Last-success usage is immutable, account-scoped, eligible for the same
seven-day durable retention, and visibly stale after a failed request. It shares
the dashboard's single-flight refresh and
shutdown cancellation rather than adding a timer or retry loop.

`quota_snapshots` supplies premium interactions, chat and completions.
`token_based_billing` selects the AI-credit label rather than legacy premium
requests. Finite meters show `100 - percent_remaining`; the other counters are
not assumed to be requests, credits or money. Unlimited quotas have no meter,
absent quotas are omitted, and missing all supported quotas is an error.
Quota-specific nonzero Unix reset times take precedence over
`quota_reset_date_utc`, then `quota_reset_date`; missing dates are disclosed, not
guessed. Reset times display as a relative countdown (days/hours, hours/minutes,
or minutes), updated by the existing one-minute UI clock without another API
request. An elapsed reset remains "Reset pending" until fresh data arrives.
The card has a top divider, no redundant GitHub Copilot heading, and no unlimited
chat/completion summary; the quota label, plan, meter and countdown remain.

This is an undocumented GitHub endpoint, not the organization-admin usage
metrics API. It reports account-wide quota rather than CLI-session consumption
or a complete bill. CodexBar is a behavioral reference for the endpoint and
quota presentation; its authentication, credential storage and macOS code are
not ported. GitHub Tray still never retrieves, displays or saves tokens.

### Pull requests and contributions

The two PR queries each fetch metadata, the first 10 labels with their total,
and `commits(last: 1).commit.statusCheckRollup`, including up to 100 check runs
and legacy status contexts with their actual total. GitHub's rollup remains
authoritative when context details are truncated; counts from a partial list
must not be presented as complete progress. No rollup is "No checks", not success.
Unknown, queued, running, cancelled, neutral, skipped, and action-required checks
retain distinct states. PR metadata and checks are one immutable snapshot, so a
refresh never attaches old checks to a new head commit. Each PR query verifies
the GraphQL viewer in addition to the surrounding REST identity checks.
Cards prepare summary text eagerly, but defer individual check-detail viewmodels
and the flyout content tree until the checks flyout opens. Details are reused only
for that card's snapshot; recycled or unloaded cards release their popup content
and avatar references. The 24-DIP avatar uses a bounded 96-pixel decode rather than
retaining a full-resolution image. Check bars reuse their distribution for an
unchanged width and segment set.
The CLI boundary permits only the exact generated PR operations (validated login
or repository/number references, fixed selections and limits); arbitrary GraphQL
arguments and mutations remain rejected. There are no per-PR fan-out requests or
new polling timers.
The activity batch shares one fixed `PullRequest` fragment across its aliases,
so metadata and check selections appear only once in the request. The complete
document, including that fragment, remains subject to exact allowlist validation.
Already-cancelled requests are rejected before preparing or starting a CLI process;
input validation still runs first.

My PRs includes open, closed, and merged authored PRs, ordered by update time.
Reviews continues to show open requests awaiting the account's review.
Activity first sorts and deduplicates the latest 30 events, then groups PR events,
reviews, review comments, and issue comments on PRs by repository and PR number.
Each group keeps its latest event time/action and the number of events in that
fetched window. Non-PR activity is preserved as separate rows. One additional
GraphQL batch fetches current details for at most 30 referenced PRs, reusing the
same card data selection as the PR modes. Closed and merged PRs are included.
Batch failures retain the previous activity section with an explicit stale
warning; unavailable PRs are never replaced by invented titles, authors, or CI.

The contribution graph is a separate data source: GraphQL returns the period
total, week boundaries, daily dates/counts, and contribution intensity levels.
Omitting `from` and `to` uses GitHub's default last-year interval. The graph labels
the actual returned date range, retains partial first/last weeks, and leaves
out-of-range cells blank. It never derives contribution counts from events.
The authenticated GraphQL viewer must match the REST account before a newly
fetched calendar is published as verified. GraphQL errors and malformed calendars are failures even when
HTTP succeeds; a failed refresh may retain only that same account's last
successful calendar, visibly marked stale.

GitHub CLI chooses credentials, including `GH_TOKEN` / `GITHUB_TOKEN` environment
precedence and its current saved account. The app does not change that selection.
All requests explicitly target GitHub.com. Each CLI call has a 30-second timeout.
Refresh defaults to five minutes and is configurable from one to sixty minutes.
Errors do not trigger an immediate retry loop. A manual refresh always retries
all sections; automatic retries follow the section freshness policy and preserve
an eligible prior success's original timestamp and error. Eligible per-section
successes are durable across restarts and may be shown immediately as saved,
unverified data. Browser links are restricted to HTTPS GitHub.com URLs.

A normal authenticated full refresh has eight operations: initial identity, six
section operations, and final identity. With all non-cadence sections fresh, a
periodic cycle has five operations: both identities plus Activity and both PR
lists. An all-fresh eligible hydrated startup has only the two identity
operations. Tests assert these shapes and the absence of skipped transport calls.
No live cold/warm GitHub timing was captured, so these counts are evidence of
avoided requests and subprocesses, not a measured speedup.

The heatmap view model owns selection transitions and returns immutable previous
and current selections for keyboard input, pointer input, and calendar replacement.
Replacement retains the selected date when available and otherwise selects the
latest returned day; an unavailable calendar clears selection. The native control
synchronizes the outline after any cell rebuild, even when the accessible value
is unchanged. Changed values raise UI Automation value-property notifications;
only changed user selections request a polite live-region announcement.
Day cells reuse one localized description for their automation name and hover
tooltip.
Returning to the present reuses cell geometry when viewport width, DPI, week
count, and preset are unchanged, while still restoring selection and scroll
position. Replacing cells or reactivating the graph invalidates that geometry.
Skeleton updates reuse the running animation when its targets and stagger
origin are unchanged; hiding, unloading, or disabling motion stops it.

## Native UX contract

- Visual reference: the user's selected
  [RepoBar macOS screenshot](https://github.com/steipete/RepoBar/blob/main/docs/assets/repobar.png).
  Match its compact translucent menu, dense divided rows, restrained accent
  selection, secondary metadata, relative timestamps, and footer actions using
  native Windows controls and acrylic rather than copying macOS chrome.
- Compact, 420-DIP-wide taskbar-adjacent panel rather than a browser or full-size dashboard.
- Header: bold primary-foreground GitHub handle above the secondary display name,
  with the contribution total and `12 months` at the right. No app title, close
  button, or Contributions heading. The chart shares the header's text edges,
  with cell sizes increased proportionally to the wider drawing area.
- Footer: Preferences, Refresh, and Quit only; no refresh-schedule or Escape hint.
- Show on launch; closing or Escape returns to the tray. Quit explicitly exits.
- Four native selectable modes with a virtualized list and clear loading, empty,
  unavailable, and stale states.
- Activity, My PRs, and Reviews share an original native `PullRequestCard`, informed by
  RepoBar's `PullRequestMenuItemView`: avatar, two-line title, compact metadata,
  monospace branch direction, and label chips. The CI summary opens native
  check details without activating the PR row. The component emits navigation
  requests to its host rather than owning account verification or browser
  launching; repository identity can be hidden when reused in repository details.
  Open, Draft, Closed, and Merged have distinct badges/icons. PR state and review
  decisions are separate from CI. Activity cards show the latest action and
  grouped event count instead of repeating each transition. Refresh failures
  visibly mark retained check status as stale. Non-PR activity and repository
  rows stay unchanged.
- Card status reads as chips rather than prose. The PR state badge sits beside the
  title, and the CI and review-decision chips share the footer row; all three are
  tinted with the Windows success, caution, attention, critical, and neutral status
  tokens so severity is visible before the text is read. Stale check results always
  read as caution. Label chips wrap onto as many lines as they need, tinted with
  their own GitHub color while names keep platform text brushes; high contrast
  drops the tint. A trailing `+N` chip covers labels beyond the shown ones.
- The CI chip names the loaded outcomes as counts, worst first
  (`1 failed · 1 running · 2 passed`), instead of a vague verdict such as "mixed
  outcomes". Counts never claim success: neutral, skipped, and cancelled stay
  named, and an aggregate outcome that no loaded check shows is stated in front of
  the counts (`Checks failed · 1 passed`). A truncated list still reports only the
  aggregate.
- A native contribution heatmap above the modes, with the real total
  and an accessible date range. Its borderless container shrinks with the preset
  and keeps all seven rows visible (up to 164 DIPs for Large), using
  pixel-aligned square-cell layout rather than bitmap scaling. Gesture and
  keyboard zoom are deferred. No zoom toolbar or month/day axis labels. Preferences offers S/M/L:
  Small fits the loaded year; Medium and Large preserve larger squares and scroll
  horizontally instead of shrinking with the window. The preset persists; reopening starts at the latest week on the
  right. Horizontal scrolling explores the loaded year. End selects the latest day.
- Cell details appear only in hover/keyboard tooltips; no legend or visible
  selected-day detail strip. Preserve empty space and the selected-day UIA value
  and live-region peer. Zero-activity cells use a neutral background token at 50%
  opacity plus a faint stroke, not faded green; high contrast uses opaque system
  brushes. Future/out-of-range days remain absent.
- Hide the normal date-range, update-time, and help footer without reserving an
  empty detail row. Show status text only for loading or errors. Compact calendars
  are left-aligned, and scrollbar space is reserved only for overflowing history.
- Reserve the same per-preset plot geometry for loading, empty and error states.
  First-load skeleton cells shimmer via staggered cell opacity, never a
  viewport overlay. Motion runs only while the panel and graph are visible and
  Windows animations are enabled; reduced motion/high contrast use static cells.
  Refresh retains the last successful eligible calendar instead of replacing it
  with a skeleton.
- Manual refresh and a local refresh-interval setting.
- Follow Windows light/dark/high-contrast colors, system typography, keyboard
  focus, UI Automation labels, and platform motion preferences.
- Never start authentication, change OS startup settings, or request broader
  GitHub permissions in the background.

The screenshot's account contribution heatmap is included. Repository metrics,
local Git status, repository-specific graphs, and cascading issue/repository
details belong to later milestones. No unimplemented statistics are fabricated.

## Later milestones

| Milestone | Scope and prerequisite decisions |
| --- | --- |
| Repository dashboard | Pinned repositories, CI/check status, separate issue/PR counts, releases, repository-specific graphs. Agree selection and permissions first. |
| Advanced cache policy | ETags, rate-limit reset/backoff scheduling, and sign-out cleanup. |
| Native authentication | Register our own OAuth/GitHub App; device/browser flow, Windows credential storage, revocation, and minimal documented permissions. Never reuse upstream credentials. |
| Multi-account / Enterprise | Explicit account/host selection, host validation, per-account caches, organization SSO diagnostics. |
| Windows distribution | Final product identity/icon, signed MSIX, installation/update strategy, opt-in launch at sign-in, x64 and ARM64 release checks. |
| Advanced RepoBar features | Local checkout discovery/status, Git actions, clipboard reference lookup, traffic, discussions, archive import, notifications. Separate product and privacy decisions. |

RepoBar's AppKit menus, Keychain storage, Sparkle updates, Finder/Terminal
integration, and macOS launch-at-login APIs require Windows equivalents rather
than line-by-line translation.
