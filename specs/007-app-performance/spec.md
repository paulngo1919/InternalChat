# Feature Specification: App Performance — Fast Open, Fast Switch, Short Network Path

**Feature Branch**: `007-app-performance`

**Created**: 2026-09-28

**Status**: Draft

**Input**: User description: "cải tiến perfomance của app" (improve the app's performance)

## Context — what was measured

Measured on 2026-09-28 against the live public deployment (`https://chat.benda.io.vn`) with a
desktop browser, signed in as a seeded employee. These numbers are the baseline this feature must
beat; they are recorded so the improvement can be verified, not assumed.

| What a person does | Measured today | Where the time goes |
| --- | --- | --- |
| Open the app after signing in | 3.4 s until the conversation list | A chain of steps that each wait for the one before |
| Reload the page (already signed in, files cached) | **4.9–7.7 s** until the conversation list | A full round trip to the sign-in service on every reload, then the profile, then the list — one after another |
| Open a conversation | 1.3 s until the first message | Nothing is kept from last time; every open fetches again |
| Switch back to a conversation opened a moment ago | 1.3 s | Same as above |
| Any single request to the server | **~1.0 s** over the internet vs **2–3 ms** on the host | The server is fast; the network path is not |

The last row dominates the others. A request that the server answers in 3 ms takes a full second
to reach a person, because traffic to the public address currently leaves the region, is carried
to a relay on another continent, and comes back. Every step in the chains above pays that price, so
the chains are slow mainly because they are chains over a slow path.

The downloaded application itself is not the problem: after the first visit it loads from the
browser's cache in effectively no time, and the first-visit download is modest.

## Clarifications

### Session 2026-09-28

- Q: Is improving the network path to the public deployment (where traffic is relayed, and how the
  host reaches the internet) part of this feature? → A: No. This feature optimises the application
  only. The ~1 s per-request network cost is taken as a given, and every budget below is set to be
  achievable with it (see Assumptions).

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Coming back to the app is instant (Priority: P1)

An employee who used the app earlier today opens it again, or reloads the tab. They see their
conversation list almost at once — the one they saw last time — while it quietly brings itself up
to date. They are not sent through the sign-in screen again while their session is still valid.

**Why this priority**: Reopening is the most frequent action in a chat app, and it is the slowest
today (up to 7.7 s). Everyone pays it many times a day.

**Independent Test**: Sign in, use the app, reload the page; time from reload to a usable
conversation list, and confirm no sign-in screen appears.

**Acceptance Scenarios**:

1. **Given** an employee signed in within their session lifetime, **When** they reload the app,
   **Then** the conversation list they last saw is on screen within 1 second, and no sign-in page is
   shown.
2. **Given** the list is shown from the previous visit, **When** the up-to-date list arrives,
   **Then** it replaces the old one in place without the screen jumping or the list flashing empty.
3. **Given** an employee whose session has expired or been revoked, **When** they reload, **Then**
   they are asked to sign in and see none of the previously shown content, not even briefly.
4. **Given** an employee who signed out, **When** anyone opens the app in that browser, **Then** no
   conversation names or messages from the previous session are shown.

---

### User Story 2 - Opening and switching conversations is instant (Priority: P1)

An employee moves between conversations. A conversation they opened earlier in this visit appears
immediately, as they left it, and then catches up. A conversation they have not opened yet appears
quickly, with a placeholder instead of a blank pane while it loads.

**Why this priority**: Switching between conversations is the second most frequent action, and
today every switch costs a full second or more even when nothing has changed.

**Independent Test**: Open conversation A, then B, then A again; time each until messages are
readable.

**Acceptance Scenarios**:

1. **Given** a conversation opened earlier in this visit, **When** the employee opens it again,
   **Then** its messages are readable within 200 milliseconds.
2. **Given** a conversation not yet opened in this visit, **When** the employee opens it, **Then**
   a placeholder is shown within 300 milliseconds and its most recent messages are readable within
   1.5 seconds on a typical connection.
3. **Given** new messages arrived in a conversation while the employee was elsewhere, **When** they
   return to it, **Then** the new messages are included, in order, with none missing or duplicated.
4. **Given** the employee lost access to a conversation while away, **When** they try to return to
   it, **Then** its previously shown messages are not displayed.

---

### User Story 3 - Performance does not quietly regress (Priority: P2)

Whoever changes the app later learns, before release, if a change makes opening, switching, or the
download size worse than the budgets in this specification.

**Why this priority**: Without a guard, the gains decay one change at a time and nobody notices
until people complain.

**Independent Test**: Introduce a deliberate slowdown (e.g., an artificial delay on the conversation
list, or a large added download) and confirm the release check fails and names the budget exceeded.

**Acceptance Scenarios**:

1. **Given** a change that pushes any budget in Success Criteria past its limit, **When** it is
   checked before release, **Then** the check fails and reports which budget, the measured value,
   and the limit.
2. **Given** the live deployment, **When** an operator looks at its health, **Then** they can see
   the current time-to-list and time-to-messages as experienced by real people, in aggregate, without
   any message content or personal data.

---

### Edge Cases

- **Offline reload**: with no connection, a reload still shows the last conversation list and the
  messages of recently opened conversations, clearly marked as not up to date; sending is queued as
  today.
- **Shared or public computer**: after sign-out, nothing from the session remains readable in the
  browser (User Story 1, scenario 4).
- **Another person signs in on the same browser**: they never see the previous person's list or
  messages, even for an instant.
- **Very long conversation**: returning to it shows the part last viewed quickly; it does not keep
  the entire history in memory.
- **Slow or lossy mobile connection**: placeholders appear instead of blank panes, and the app stays
  responsive to taps while data loads.
- **Many conversations**: an employee with 500+ conversations sees the most recent ones first within
  the same budget; the rest load as they scroll.
- **Stale data shown, then corrected**: a message deleted or edited while away is corrected when the
  fresh data arrives, and a deleted message's content is never shown again after that.

## Requirements *(mandatory)*

### Functional Requirements

#### Opening the app

- **FR-001**: A returning employee whose session is still valid MUST reach a usable conversation list
  without being shown the sign-in page.
- **FR-002**: The app MUST show the conversation list from the employee's previous visit immediately
  on open, then update it in place from the server.
- **FR-003**: The steps needed to show the list MUST NOT wait on each other where they do not depend
  on each other; independent loads MUST happen at the same time.
- **FR-004**: Content shown from a previous visit MUST belong to the employee now signed in; it MUST
  be discarded on sign-out, on session expiry or revocation, and when a different employee signs in.

#### Conversations

- **FR-005**: The app MUST keep recently opened conversations available for the rest of the visit, so
  returning to one shows its messages without waiting for the server, then brings them up to date.
- **FR-006**: Bringing a conversation up to date MUST fetch only what changed since it was last shown,
  not its full recent history again, where the server can tell.
- **FR-007**: Kept content MUST be bounded (at most the 20 most recently opened conversations and
  their most recent 200 messages each) so memory use does not grow with use.
- **FR-008**: When access to a conversation is lost, its kept content MUST be discarded immediately,
  consistent with base FR-002 and 002's revocation behaviour.
- **FR-009**: Any wait longer than 300 milliseconds MUST show a placeholder in the shape of the
  content (as introduced by the loading-state work), never an empty pane.

#### Requests

- **FR-010**: Every request the app makes on opening or switching MUST be needed for what is on
  screen; a request whose answer the app already holds and knows to be current MUST NOT be repeated.
  With each request costing ~1 s on the public path, a removed request is the largest saving
  available to the application.
- **FR-011**: Connections to the server MUST be reused across requests so that a sequence of requests
  does not pay connection setup each time.

#### Size and measurement

- **FR-012**: The download needed before the app is first usable MUST stay within the budget in
  SC-006; features not needed at open (meetings, attachments viewer, search) MUST load only when first
  used.
- **FR-013**: The release process MUST measure the Success Criteria budgets automatically and fail a
  release that exceeds any of them, naming the budget, the value, and the limit.
- **FR-014**: The live deployment MUST report, in aggregate, time-to-list and time-to-messages as
  experienced in people's browsers, extending the existing delivery telemetry; the report MUST NOT
  contain message content, conversation names, or anything identifying a person.

### Key Entities

- **Kept view state**: for one signed-in employee in one browser — the last conversation list and the
  recent messages of up to 20 recently opened conversations, each with the point it was last brought
  up to date. Owned by that employee's session; destroyed with it.
- **Performance budget**: a named user-facing measure (e.g., time-to-list on reload), its limit, and
  how it is measured; checked before every release.
- **Experience sample**: one anonymous measurement from a real browser (which budget, how long,
  connection type), aggregated for the operator view.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: On reload within a valid session, 95% of employees see a usable conversation list
  within **1 second** (baseline: 4.9–7.7 s) (**responsiveness**).
- **SC-002**: On a first open after signing in, 95% see the conversation list within **2.5 seconds**
  of returning from the sign-in page (baseline: 3.4 s). With ~1 s per request this is only reachable
  by removing waits between requests (**responsiveness**).
- **SC-003**: Returning to a conversation opened earlier in the visit shows its messages within
  **200 milliseconds** for 95% of switches; a first open shows a placeholder within 300 ms and its
  messages within **1.5 seconds** for 95% (baseline: 1.3 s both, no placeholder)
  (**responsiveness**).
- **SC-004**: Once sign-in completes, the conversation list is usable after at most **one round of
  requests** — nothing waits for another request unless it needs that request's answer (baseline:
  three rounds, one after another) (**responsiveness**).
- **SC-005**: The budgets above hold with **500 employees** using the public deployment at the same
  time, each with up to 500 conversations (**scale**).
- **SC-006**: The download before the app is first usable stays at or below **150 KB compressed**
  on a first visit (today ~136 KB), and a repeat visit downloads no application code unless it
  changed (**scale**).
- **SC-007**: In 100% of tested cases, content kept from a previous session is never shown to a
  different signed-in employee, after sign-out, or after revocation — including for an instant before
  a redirect (**access control**).
- **SC-008**: Every change to a performance budget is recorded with
  who, when, and the before/after values; aggregate experience samples are retained for 30 days
  (**auditability**).
- **SC-009**: Keeping content for fast display never causes a sent or received message to be lost or
  shown out of order: across 1,000 scripted switch-and-return cycles with messages arriving in
  between, the transcript always matches the server exactly (**data durability**).
- **SC-010**: A deliberate regression past any budget is caught by the release check in 100% of
  trials (User Story 3).

## Assumptions

- **Where people are**: the main audience is in the same country/region as the host machine
  (Vietnam). "Typical connection" means a domestic broadband or 4G connection in that region.
- **Session lifetime is unchanged**: this feature does not lengthen how long a session lasts; it only
  avoids re-showing the sign-in page while a session is still valid (base FR-003 revocation within
  5 minutes still applies).
- **What is kept in the browser** is limited to what the employee could already see, is tied to
  their session, and is cleared as FR-004 describes. Attachment file contents are not kept.
- **The network path is out of scope**: traffic to the public address is relayed via another
  continent (the tunnel connects to an edge in the United States because the host reaches the
  internet through a US address), costing ~1 s per request. This feature does not change that; every
  budget is set to be achievable with it. Shortening the path is a separate infrastructure task and
  would improve every number here further without changing this specification.
- **Server processing is not the bottleneck** (2–3 ms per request measured on the host); server-side
  optimisation is out of scope unless a budget cannot be met without it.
- **Real-time delivery** targets are owned by feature 002 and must not regress; this feature does not
  redefine them.
- **Meetings media** performance (audio/video quality) is out of scope.
- **Baseline method**: the numbers in Context were taken with a scripted browser from the host's
  network; the SC measurements will be repeated the same way plus from a connection outside the
  host network.
