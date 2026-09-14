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
| `GitHubTray.Core` | Domain snapshot, safe URL/JSON boundary, GitHub CLI process lifecycle, read-only REST and GraphQL queries, settings storage. No WinUI dependency. |
| `GitHubTray.App` | WinUI views and MVVM state, refresh scheduling, Windows notification-area lifecycle, window placement, browser launching. |
| `GitHubTray.Core.Tests` | Deterministic response fixtures; no live authentication or network dependency. |
| `GitHubTray.App.Tests` | Source-linked production heatmap view-model tests without a WinUI runtime or live data. |

The UI owns one current snapshot and prevents overlapping refreshes. A refresh
resolves `/user` before requesting account-specific data and verifies the account
again before publishing the completed snapshot. A detected account change,
including a mismatched GraphQL viewer, rejects the entire refresh rather than
mixing data under the original account label. Individual section
failures keep that section's last successful items and timestamp, clearly marked
as stale. A different account invalidates the previous snapshot's cached sections.
An identity lookup failure is an error, not a successful empty dashboard.

Activity data stays in memory and is not persisted to disk. Only the refresh
interval is stored locally. A malformed settings file produces a visible warning;
it is replaced only when the user explicitly saves a valid setting.

## GitHub contract

| View | API |
| --- | --- |
| Account | `GET /user` |
| Contributions | `POST /graphql` with a read-only `viewer.contributionsCollection.contributionCalendar` query |
| Activity | `GET /users/{login}/events?per_page=30` |
| My PRs | `GET /search/issues?q=is:pr is:open author:{login}&sort=updated&order=desc&per_page=30` |
| Reviews | `GET /search/issues?q=is:pr is:open review-requested:{login}&sort=updated&order=desc&per_page=30` |
| Repositories | `GET /user/repos?sort=pushed&direction=desc&per_page=30&affiliation=owner,collaborator,organization_member` |

Each activity list is bounded, not a complete history or a total count.
Activity means the user's generated events, not the followed-user feed,
notifications inbox, or contribution calendar. GitHub's Events API can be delayed
and returns a limited historical window. Private data depends on the active
credential's repository access and organization SSO. Incomplete search responses
are reported rather than silently replacing complete cached results.

The contribution graph is a separate data source: GraphQL returns the period
total, week boundaries, daily dates/counts, and contribution intensity levels.
Omitting `from` and `to` uses GitHub's default last-year interval. The graph labels
the actual returned date range, retains partial first/last weeks, and leaves
out-of-range cells blank. It never derives contribution counts from events.
The authenticated GraphQL viewer must match the REST account before its calendar
is displayed. GraphQL errors and malformed calendars are failures even when
HTTP succeeds; a failed refresh may retain only that same account's last
successful calendar, visibly marked stale.

GitHub CLI chooses credentials, including `GH_TOKEN` / `GITHUB_TOKEN` environment
precedence and its current saved account. The app does not change that selection.
All requests explicitly target GitHub.com. Each CLI call has a 30-second timeout.
Refresh defaults to five minutes and is configurable from one to sixty minutes.
Errors do not trigger an immediate retry loop; the next scheduled or manual
refresh is the next attempt. Last-success retention is per process, not durable
offline caching. Browser links are restricted to HTTPS GitHub.com URLs.

The heatmap view model owns selection transitions and returns immutable previous
and current selections for keyboard input, pointer input, and calendar replacement.
Replacement retains the selected date when available and otherwise selects the
latest returned day; an unavailable calendar clears selection. The native control
synchronizes the outline after any cell rebuild, even when the accessible value
is unchanged. Changed values raise UI Automation value-property notifications;
only changed user selections request a polite live-region announcement.

## Native UX contract

- Visual reference: the user's selected
  [RepoBar macOS screenshot](https://github.com/steipete/RepoBar/blob/main/docs/assets/repobar.png).
  Match its compact translucent menu, dense divided rows, restrained accent
  selection, secondary metadata, relative timestamps, and footer actions using
  native Windows controls and acrylic rather than copying macOS chrome.
- Compact, taskbar-adjacent panel rather than a browser or full-size dashboard.
- Show on launch; closing or Escape returns to the tray. Quit explicitly exits.
- Four native selectable modes with a virtualized list and clear loading, empty,
  unavailable, and stale states.
- A compact native contribution heatmap above the modes, with the real total and
  date range, hover details, and one keyboard focus stop for day navigation.
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
| Durable offline experience | Account-partitioned response cache, ETags, TTLs, rate-limit reset/backoff scheduling, data retention and sign-out cleanup. |
| Native authentication | Register our own OAuth/GitHub App; device/browser flow, Windows credential storage, revocation, and minimal documented permissions. Never reuse upstream credentials. |
| Multi-account / Enterprise | Explicit account/host selection, host validation, per-account caches, organization SSO diagnostics. |
| Windows distribution | Final product identity/icon, signed MSIX, installation/update strategy, opt-in launch at sign-in, x64 and ARM64 release checks. |
| Advanced RepoBar features | Local checkout discovery/status, Git actions, clipboard reference lookup, traffic, discussions, archive import, notifications. Separate product and privacy decisions. |

RepoBar's AppKit menus, Keychain storage, Sparkle updates, Finder/Terminal
integration, and macOS launch-at-login APIs require Windows equivalents rather
than line-by-line translation.
