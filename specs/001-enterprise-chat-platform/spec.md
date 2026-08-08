# Feature Specification: Enterprise Internal Chat Platform

**Feature Branch**: `001-enterprise-chat-platform`

**Created**: 2026-07-31

**Status**: Draft

**Input**: User description: "Build an internal enterprise chat application. Features: User
authentication, One-to-one chat, Group chat, Image upload, Video upload, Video meeting, Screen
sharing, Notification, Message search. Target: 10000 employees."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Secure Sign-In and Access Control (Priority: P1)

An employee opens the chat application and signs in with their existing corporate credentials.
They are shown only the conversations they belong to. When an employee leaves the company and is
deactivated in the corporate directory, their access is revoked and they can no longer read or
send anything.

**Why this priority**: Nothing else in the platform can be trusted without it. An internal chat
system holds the organization's most sensitive unstructured conversation, so identity and
revocation are the first thing that must work, not the last.

**Independent Test**: Sign in as an employee with valid corporate credentials and confirm access
to a seeded conversation. Attempt to open a conversation the employee does not belong to and
confirm refusal. Deactivate the employee in the corporate directory and confirm that access ends
within the stated revocation window, including for an already-open session.

**Acceptance Scenarios**:

1. **Given** an active employee with corporate credentials, **When** they sign in, **Then** they
   reach their conversation list without creating a separate chat password.
2. **Given** a signed-in employee, **When** they request a conversation they are not a member of,
   **Then** access is refused and the attempt is recorded in the audit log.
3. **Given** an employee with an active session, **When** their account is deactivated in the
   corporate directory, **Then** their access ends within 5 minutes without an administrator
   touching the chat system.
4. **Given** an employee signed in on a laptop and a second device, **When** they sign out of one
   device, **Then** the other session remains valid and both sessions are visible to the employee.
5. **Given** an administrator, **When** they change a member's role or remove them from a group,
   **Then** the change takes effect for that member's next action and is recorded with actor,
   subject, timestamp, and outcome.

---

### User Story 2 - One-to-One Direct Messaging (Priority: P1)

An employee finds a colleague in the directory, opens a direct conversation, and exchanges
messages with them in real time. Messages they sent while the colleague was offline are waiting
when the colleague returns, in the order they were sent.

**Why this priority**: This is the smallest slice that makes the product useful. Delivered alone,
10,000 employees can already replace ad-hoc email threads for quick exchanges.

**Independent Test**: Sign in as two employees in two browsers, start a direct conversation from
the directory, and confirm each message appears on the other side without a manual refresh. Close
one browser, send three messages, reopen, and confirm all three appear in order exactly once.

**Acceptance Scenarios**:

1. **Given** two signed-in colleagues, **When** one sends a message, **Then** it appears for the
   other within the stated delivery time with no page refresh.
2. **Given** a colleague who is offline, **When** a message is sent to them, **Then** it is stored
   and delivered when they next connect, in the original order.
3. **Given** an employee scrolling a long conversation, **When** they scroll upward, **Then**
   older messages load progressively without freezing the interface.
4. **Given** an employee who loses network connectivity mid-typing, **When** they press send,
   **Then** the message is queued, the interface shows it as pending, and it is sent exactly once
   on reconnect.
5. **Given** an employee viewing a conversation, **When** the other person is typing, **Then** a
   typing indicator is shown and it disappears when typing stops or the connection drops.
6. **Given** a sent message, **When** the sender edits or deletes it within the allowed window,
   **Then** all participants see the edit or removal, and the fact that an edit or deletion
   occurred remains visible.

---

### User Story 3 - Group Conversations (Priority: P2)

An employee creates a group conversation for their team, adds members, and the team discusses
there. Members can be added and removed over time, and a new member's view of history follows a
clear, stated rule.

**Why this priority**: Team conversation is where most internal chat volume lives, but it is only
meaningful once one-to-one messaging is proven. It builds directly on US2's delivery mechanics.

**Independent Test**: Create a group with three members, post messages, add a fourth member and
confirm their history visibility matches the stated rule, remove a member and confirm they lose
access to new messages immediately.

**Acceptance Scenarios**:

1. **Given** an employee, **When** they create a group and add members, **Then** all members see
   the group in their conversation list without refreshing.
2. **Given** a group with members, **When** anyone posts, **Then** every online member receives it
   within the stated delivery time and offline members receive it on reconnect.
3. **Given** a group, **When** a member is removed, **Then** they immediately lose access to new
   messages and to the group's files, and the removal is recorded in the audit log.
4. **Given** a newly added member, **When** they open the group, **Then** they see history
   according to the stated visibility rule and the rule is displayed to them.
5. **Given** a group conversation, **When** a member mentions another member by name, **Then** the
   mentioned member is notified distinctly from ordinary group traffic.
6. **Given** a group with 500 members, **When** a message is posted, **Then** delivery time to
   online members stays within the stated budget.

---

### User Story 4 - Notifications and Unread State (Priority: P2)

An employee is working in another application. When someone messages them directly or mentions
them in a group, they are notified. When they return, unread conversations are clearly marked and
the unread count is correct across all their devices.

**Why this priority**: Without notifications, a chat platform is only useful to people already
staring at it. This is what makes the platform a reliable channel rather than a place to check.
It requires US2 and is most valuable alongside US3.

**Independent Test**: Sign in on two devices, have a colleague send a direct message while the
app is in the background, confirm a notification arrives on both, then read the conversation on
one device and confirm the unread badge clears on the other.

**Acceptance Scenarios**:

1. **Given** an employee not currently viewing a conversation, **When** they receive a direct
   message, **Then** they are notified within the stated notification time.
2. **Given** an employee in a busy group, **When** ordinary messages arrive, **Then** they are not
   notified for each one, but they ARE notified when directly mentioned.
3. **Given** an employee with unread messages, **When** they read them on one device, **Then** the
   unread state clears on all their other devices.
4. **Given** an employee who has set a do-not-disturb window or muted a conversation, **When**
   messages arrive in that window or conversation, **Then** no interrupting notification is
   delivered, but unread state is still updated.
5. **Given** an employee who has been offline for a day, **When** they return, **Then** they
   receive a single summary rather than a backlog of individual interruptions.
6. **Given** a notification, **When** the employee opens it, **Then** they land directly on the
   message that triggered it.

---

### User Story 5 - Image Sharing (Priority: P2)

An employee attaches a screenshot or photo to a message. Recipients see an inline preview
immediately and can open the full image or download it. Images obey the same access rules as the
conversation they were posted in.

**Why this priority**: Screenshots are the single most common attachment in internal engineering
and support conversation, and they are cheap to support compared with video.

**Independent Test**: Post an image to a group, confirm every member sees an inline preview,
confirm a non-member cannot retrieve the image even with its direct address, and confirm an
oversized or unsupported file is rejected with a clear message.

**Acceptance Scenarios**:

1. **Given** an employee composing a message, **When** they attach or paste an image within the
   size limit, **Then** it uploads with visible progress and appears inline for all members.
2. **Given** a posted image, **When** a non-member obtains its direct address, **Then** retrieval
   is refused.
3. **Given** an employee attaching a file that exceeds the size limit or is an unsupported type,
   **When** they try to send, **Then** it is rejected before upload with a clear reason.
4. **Given** an uploaded file, **When** it is found to be malicious, **Then** it never becomes
   retrievable and the uploader is informed.
5. **Given** an upload interrupted by a network drop, **When** the employee retries, **Then** no
   partial or duplicate attachment is left in the conversation.
6. **Given** a message with an image, **When** the message is deleted, **Then** the image stops
   being retrievable.

---

### User Story 6 - Search Across Messages and Files (Priority: P3)

An employee remembers a decision made weeks ago. They search for a phrase and get matching
messages ranked by relevance, restricted to conversations they belong to, and can jump straight
to a result in its original context.

**Why this priority**: Search is what turns chat history from a liability into an asset, but the
platform is usable without it. It requires meaningful message volume to be worth building.

**Independent Test**: Seed history across several conversations, search a distinctive phrase, and
confirm results come only from conversations the searching employee belongs to. Confirm a phrase
that exists only in a conversation they are not a member of returns nothing.

**Acceptance Scenarios**:

1. **Given** an employee with history, **When** they search a phrase, **Then** matching messages
   are returned within the stated search time, ranked by relevance.
2. **Given** search results, **When** the employee selects one, **Then** they land on that message
   in its original conversation with surrounding context.
3. **Given** a phrase that appears only in conversations the employee does not belong to, **When**
   they search it, **Then** no results are returned and nothing reveals that the content exists.
4. **Given** an employee, **When** they filter results by person, conversation, date range, or
   attachment type, **Then** only matching results are returned.
5. **Given** a message that is edited or deleted, **When** the employee searches its former
   content, **Then** results reflect the current state, not the old content.
6. **Given** an employee removed from a group, **When** they search phrases from that group,
   **Then** those results no longer appear.

---

### User Story 7 - Video File Sharing (Priority: P3)

An employee shares a screen recording or short video clip in a conversation. Recipients can play
it in place without downloading the whole file first.

**Why this priority**: Recorded walkthroughs reduce meeting load, but video carries far heavier
storage and bandwidth cost than images, so it should land only after the core is stable and
storage policy is settled.

**Independent Test**: Upload a video within the size limit, confirm playback starts without a full
download, confirm a file over the limit is rejected with a clear reason, and confirm the same
access rules as images apply.

**Acceptance Scenarios**:

1. **Given** an employee attaching a video within the size limit, **When** they send it, **Then**
   it uploads with visible progress and members can play it inline.
2. **Given** a shared video, **When** a member plays it, **Then** playback starts without
   downloading the entire file first, and they can seek within it.
3. **Given** a video that exceeds the size or duration limit, **When** the employee attempts to
   send it, **Then** it is rejected before upload with the limit clearly stated.
4. **Given** storage approaching its configured capacity, **When** the threshold is crossed,
   **Then** administrators are alerted before uploads begin failing for employees.
5. **Given** a shared video, **When** a non-member obtains its direct address, **Then** retrieval
   is refused.

---

### User Story 8 - Video Meetings (Priority: P4)

Employees start a video meeting from a conversation. Up to 25 participants join with camera and
microphone, see and hear each other, and can mute, disable video, and leave. The meeting is
confined to members of the originating conversation. The platform supports up to 50 such meetings
running at the same time across the organization.

**Why this priority**: The highest-cost, highest-risk capability in the platform and the one least
like the rest of it. Every other story delivers value without it, and it should not be allowed to
delay them. It is also the only story that requires hardware beyond the single application host.

**Independent Test**: Start a meeting from a group with three participants joining from separate
machines, confirm two-way audio and video for all, confirm mute and camera-off propagate to other
participants, and confirm a non-member cannot join with the meeting address. Separately, load-test
to 25 participants in one meeting and to the platform-wide ceiling of 1,250 concurrent
participants, confirming meetings already in progress are unaffected when the ceiling is reached.

**Acceptance Scenarios**:

1. **Given** a conversation, **When** a member starts a meeting, **Then** other members see a join
   prompt in that conversation.
2. **Given** participants in a meeting, **When** they speak, **Then** other participants hear them
   with audio delay within the stated budget.
3. **Given** a participant, **When** they mute, disable video, or leave, **Then** all other
   participants see the change immediately.
4. **Given** a non-member with the meeting address, **When** they attempt to join, **Then** they
   are refused.
5. **Given** a participant whose network degrades, **When** bandwidth drops, **Then** their video
   quality is reduced to preserve audio rather than dropping the call.
6. **Given** the participant who started a meeting, **When** they leave, **Then** the meeting
   continues for the remaining participants.
7. **Given** a meeting, **When** it starts and ends, **Then** the fact of the meeting, its
   participants, and its duration are recorded in the audit log.
8. **Given** a meeting already holding 25 participants, **When** a 26th member tries to join,
   **Then** they are refused with the limit stated, and the existing 25 are unaffected.
9. **Given** the platform at its concurrent-participant ceiling, **When** an employee tries to
   start a new meeting, **Then** they are told why and when to retry, and every meeting already
   in progress continues at full quality.

---

### User Story 9 - Screen Sharing in Meetings (Priority: P4)

During a meeting, a participant shares their screen or a single application window so others can
follow along. Others can see clearly enough to read text on the shared screen.

**Why this priority**: A meaningful increment on top of US8 that can ship separately, and the most
common reason internal meetings happen at all. It is worthless without US8.

**Independent Test**: In an active meeting, share a screen and confirm other participants can read
normal-size text on it. Start a second share from another participant and confirm the stated
handling. Stop sharing and confirm the view returns to camera video.

**Acceptance Scenarios**:

1. **Given** a participant in a meeting, **When** they share a screen or window, **Then** other
   participants see it within the stated time and can read standard document text on it.
2. **Given** a participant sharing, **When** a second participant starts sharing, **Then** the
   platform applies a stated rule and all participants are told which share they are viewing.
3. **Given** a participant sharing a single window, **When** they switch to another application,
   **Then** the other application is NOT revealed to participants.
4. **Given** a participant sharing, **When** they stop, **Then** all participants return to camera
   video without rejoining.
5. **Given** a viewer of a shared screen, **When** they want detail, **Then** they can enlarge the
   shared view to their full window.

---

### Edge Cases

- **Duplicate send on retry**: an employee's client retries a send it never got confirmation for.
  The message MUST appear exactly once, not twice.
- **Ordering under reconnect**: an employee sends three messages, loses connection after the
  first, and reconnects. All three MUST appear once, in the order sent.
- **Same person, many devices**: an employee is signed in on a laptop, desktop, and phone. Read
  state, unread counts, and delivery MUST stay consistent across all of them.
- **Deactivated employee's history**: when an employee leaves, their past messages remain visible
  to conversation members, but their identity is shown as inactive and they cannot be added to
  new conversations.
- **Very large group**: a company-wide conversation containing all 10,000 employees. Posting MUST
  not degrade delivery for other conversations.
- **Member removed mid-conversation**: a member is removed while they have the conversation open.
  They MUST stop receiving new messages immediately, without needing to refresh.
- **Storage exhaustion**: attachment storage fills. Employees MUST get a clear error and text
  messaging MUST continue to work.
- **Malicious upload**: a file passes the type check but is malicious. It MUST never become
  retrievable by any member.
- **Search over the full corpus**: an employee searches a common word across years of history.
  Results MUST return within the stated time or be explicitly truncated with that fact shown.
- **Meeting starter disconnects**: the person who started a meeting drops off. The meeting MUST
  continue for everyone else.
- **Simultaneous screen shares**: two participants share at once. The platform MUST apply one
  stated rule, not leave the state ambiguous.
- **Clock skew**: an employee's device clock is wrong. Message ordering MUST NOT depend on it.
- **Notification for a deleted message**: a message is deleted after it triggered a notification.
  Opening the notification MUST NOT reveal the deleted content.
- **Empty and hostile input**: zero-length messages, messages at the maximum length, and messages
  containing markup or scripts MUST be handled without breaking rendering for other members.

## Requirements *(mandatory)*

### Functional Requirements

#### Identity and Access

- **FR-001**: System MUST authenticate employees against the organization's existing corporate
  directory. The platform MUST NOT store its own passwords.
- **FR-002**: System MUST deny access to every conversation, message, attachment, meeting, and
  search result by default, granting it only to confirmed members of the containing conversation.
- **FR-003**: System MUST revoke a deactivated employee's access, including active sessions,
  within 5 minutes of deactivation in the corporate directory.
- **FR-004**: System MUST support at least two roles — member and administrator — where
  administrators can manage conversations, membership, and retention but MUST NOT gain the ability
  to read conversations they are not a member of without that access being recorded.
- **FR-005**: System MUST allow an employee to be signed in on multiple devices simultaneously and
  to view and revoke their own sessions.
- **FR-006**: System MUST record every authentication event, access denial, membership change,
  permission change, export, and administrative action with actor, subject, timestamp, source
  address, and outcome, in a log that application code cannot alter or delete.

#### Messaging

- **FR-007**: Users MUST be able to start a one-to-one conversation with any active employee found
  in the directory.
- **FR-008**: Users MUST be able to create group conversations, name them, and add or remove
  members.
- **FR-009**: System MUST deliver a message to all online members of its conversation without the
  recipient taking any action.
- **FR-010**: System MUST store every accepted message durably and deliver it to members who were
  offline when it was sent, preserving send order within a conversation.
- **FR-011**: System MUST guarantee that an accepted message is delivered exactly once per
  recipient device, even when the sender's client retries.
- **FR-012**: System MUST establish message order within a conversation independently of any
  client device's clock.
- **FR-013**: Users MUST be able to load older history in a conversation progressively rather than
  all at once.
- **FR-014**: Users MUST be able to edit and delete their own messages within a defined window,
  with the fact of the edit or deletion remaining visible to other members.
- **FR-015**: Users MUST be able to mention specific members, and mentioned members MUST be
  notified distinctly from ordinary traffic.
- **FR-016**: System MUST show when a conversation partner is typing and MUST clear that state on
  disconnect.
- **FR-017**: System MUST show each employee's availability (online, away, offline, do not
  disturb) to colleagues.
- **FR-018**: System MUST reconnect automatically after a network interruption, show the employee
  that it is reconnecting, and send any messages queued during the interruption exactly once.
- **FR-019**: System MUST reject messages exceeding a defined maximum length with a clear message
  before sending.
- **FR-020**: System MUST render message text safely such that content submitted by one employee
  cannot alter or script another employee's interface.

#### Attachments

- **FR-021**: Users MUST be able to attach images to messages and see inline previews.
- **FR-022**: Users MUST be able to attach videos to messages and play them without downloading
  the entire file first.
- **FR-023**: System MUST enforce a maximum file size and an allowed file-type list, and MUST
  reject violations before upload with the limit stated.
- **FR-024**: System MUST scan every uploaded file for malicious content before it becomes
  retrievable by anyone.
- **FR-025**: System MUST restrict attachment retrieval to members of the conversation the
  attachment was posted in, regardless of how the retrieval address was obtained.
- **FR-026**: System MUST show upload progress and MUST leave no partial or duplicate attachment
  when an upload is interrupted.
- **FR-027**: System MUST stop serving an attachment when its message is deleted or its
  conversation is removed.
- **FR-028**: System MUST alert administrators before attachment storage capacity is exhausted,
  and MUST keep text messaging working if it is exhausted.

#### Search

- **FR-029**: Users MUST be able to search message text and attachment names across every
  conversation they belong to, and MUST NOT receive results from any conversation they do not
  belong to.
- **FR-030**: System MUST rank search results by relevance and allow filtering by person,
  conversation, date range, and attachment type.
- **FR-031**: Users MUST be able to jump from a search result to that message in its original
  conversation with surrounding context.
- **FR-032**: System MUST reflect edits, deletions, and membership removals in search results, so
  that content a user can no longer access stops appearing for them.
- **FR-033**: System MUST either return search results within the stated time or state explicitly
  that results were truncated.

#### Notifications

- **FR-034**: System MUST notify an employee of direct messages and of mentions when they are not
  actively viewing the relevant conversation, including when the application is not the
  foreground window.
- **FR-035**: System MUST NOT notify an employee for every message in a busy group conversation
  they have not been mentioned in.
- **FR-036**: System MUST keep unread state and unread counts consistent across all of an
  employee's devices.
- **FR-037**: Users MUST be able to mute individual conversations and set do-not-disturb windows,
  during which unread state still updates but no interrupting notification is delivered.
- **FR-038**: System MUST batch a returning employee's backlog into a summary rather than
  delivering a burst of individual interruptions.
- **FR-039**: Opening a notification MUST take the employee to the triggering message, and MUST
  NOT reveal content that has since been deleted or that they have lost access to.
- **FR-040**: System MUST detect when an employee's current device or browser cannot receive
  notifications, tell them so plainly, and give them the steps to enable it. Employees MUST NOT
  silently believe they are reachable when they are not.

#### Meetings and Screen Sharing

- **FR-041**: Users MUST be able to start a video meeting from a conversation, and only members of
  that conversation MUST be able to join.
- **FR-042**: System MUST support up to 25 simultaneous participants in a single meeting, all with
  camera and microphone active.
- **FR-043**: System MUST support up to 50 meetings running simultaneously across the
  organization, up to a platform-wide ceiling of 1,250 concurrent meeting participants.
- **FR-044**: System MUST refuse to start a new meeting once the platform-wide capacity ceiling is
  reached, telling the employee why and when to retry, rather than degrading quality for meetings
  already in progress.
- **FR-045**: Participants MUST be able to enable and disable their camera and microphone, and
  those state changes MUST be visible to other participants immediately.
- **FR-046**: System MUST degrade video quality rather than dropping the call when a participant's
  available bandwidth falls.
- **FR-047**: A meeting MUST continue when the participant who started it leaves.
- **FR-048**: Participants MUST be able to share their entire screen or a single application
  window, and sharing a single window MUST NOT reveal any other application.
- **FR-049**: Shared screen content MUST be legible enough for participants to read standard
  document text.
- **FR-050**: System MUST apply one defined, visible rule when more than one participant attempts
  to share simultaneously.
- **FR-051**: System MUST record the occurrence, participants, and duration of every meeting in
  the audit log.

#### Operations and Compliance

- **FR-052**: System MUST automatically delete messages and attachments 12 months after they were
  sent, and MUST record every retention deletion in the audit log.
- **FR-053**: System MUST make the retention period visible to employees, so that no one relies on
  chat as a permanent record without knowing it expires.
- **FR-054**: The retention period MUST be a configuration value an administrator can change
  without a code change, and changing it MUST be recorded in the audit log.
- **FR-055**: System MUST support exporting a specific employee's or conversation's content on
  request, with every export recorded in the audit log.
- **FR-056**: System MUST NOT record message bodies, attachment contents, or credentials in
  operational logs or telemetry.
- **FR-057**: System MUST remain fully operable by 10,000 employees without any per-seat,
  per-message, per-minute, or per-device charge to a third party.

### Key Entities *(include if feature involves data)*

- **Employee**: a person from the corporate directory. Identity, display name, availability
  state, active/deactivated status. Never holds a chat-specific password.
- **Conversation**: a direct (exactly two participants) or group (named, many participants)
  container for messages. Holds its retention setting and history-visibility rule.
- **Membership**: the link between an Employee and a Conversation, carrying role, join time, and
  the point in history from which the member may read.
- **Message**: text authored by an Employee in a Conversation at a server-assigned position in
  order. Carries edit and deletion state and an idempotency key supplied by the sender's client.
- **Attachment**: an image or video belonging to a Message. Carries type, size, scan verdict, and
  the access rules inherited from its Conversation.
- **Read State**: per Employee per Conversation, the position up to which they have read. Shared
  across all of that employee's devices.
- **Notification**: a pending or delivered alert to an Employee about a Message, carrying its
  reason (direct message or mention) and its delivery and read status.
- **Meeting**: a time-bounded session attached to a Conversation, with its participants, their
  join and leave times, and any screen-share periods.
- **Audit Event**: an immutable record of a security-relevant action, with actor, subject,
  timestamp, source address, and outcome.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: An employee can sign in and send their first message in under 60 seconds from
  opening the application, without creating a chat-specific account.
- **SC-002**: An employee can find a colleague and open a direct conversation in under 15 seconds.
- **SC-003**: 90% of employees successfully create a group and add members on their first attempt
  without assistance or documentation.
- **SC-004**: An employee can locate a message they remember from a month ago, using search, in
  under 30 seconds.
- **SC-005**: The platform costs the organization nothing beyond the hardware it runs on — no
  per-seat, per-message, per-minute, or per-gigabyte charge to any third party.

#### Responsiveness (required by constitution)

- **SC-006**: 95% of sent messages appear for all online recipients within 0.5 seconds of the
  sender pressing send; 99% within 1 second.
- **SC-007**: 95% of searches return results within 1 second across the full retained history.
- **SC-008**: 95% of notifications reach an employee's browser within 5 seconds of the triggering
  message, on every device where notifications have been enabled.
- **SC-009**: In a meeting, participants hear each other with under 300 milliseconds of audio
  delay, and a started screen share becomes visible to other participants within 3 seconds.
- **SC-010**: A conversation with 12 months of history opens and becomes interactive within 2
  seconds.

#### Scale (required by constitution)

- **SC-011**: The platform serves 10,000 registered employees with 7,000 simultaneously connected
  during peak hours, without exceeding the responsiveness criteria above.
- **SC-012**: The platform sustains 100 messages per second during peak hours and absorbs bursts
  of 1,000 messages per second without loss or reordering.
- **SC-013**: A group conversation containing all 10,000 employees can be posted to without
  degrading delivery time in any other conversation.
- **SC-014**: A single meeting holds 25 participants with all cameras on while meeting SC-009's
  delay budget.
- **SC-015**: 50 meetings run simultaneously, totalling up to 1,250 participants, without
  degrading messaging responsiveness (SC-006) for anyone else on the platform.
- **SC-016**: Twelve months of retained history for 10,000 employees fits within the provisioned
  storage, with the projection verified against measured usage before the retention period first
  elapses.

#### Access Control (required by constitution)

- **SC-017**: No employee can retrieve any message, attachment, meeting, or search result from a
  conversation they are not a member of — through any path, including direct addresses, search,
  notifications, and export.
- **SC-018**: A deactivated employee loses all access within 5 minutes, verified against an
  already-open session.
- **SC-019**: Zero unmitigated Critical or High severity findings in a security review before the
  first production deployment.

#### Auditability (required by constitution)

- **SC-020**: Every authentication event, access denial, membership change, permission change,
  export, retention deletion, retention-policy change, administrative action, and meeting is
  recorded with actor, subject, timestamp, source address, and outcome, and cannot be altered by
  the application.
- **SC-021**: An administrator can answer "who had access to this conversation on this date, and
  who changed it" from the audit log alone, in under 10 minutes.

#### Data Durability (required by constitution)

- **SC-022**: An accepted message is never lost and never duplicated — verified across client
  reconnects, server restarts, and a full cache outage.
- **SC-023**: A complete restore from backup returns the platform to a consistent state with no
  more than 24 hours of data loss, verified by a rehearsed restore before first production use.
- **SC-024**: Losing the entire cache tier causes slower responses only — never data loss, never
  incorrect access decisions.
- **SC-025**: No message or attachment is deleted before its 12-month retention point, and none
  survives more than 7 days past it.

## Assumptions

**Made from context and industry standard** — each of these is a reasonable default that was not
specified, and each can be overridden before planning:

- **Corporate directory exists**: the organization already has an identity system holding all
  10,000 employees, capable of standard single sign-on, and it is the authority for who works
  here. Chat does not manage employee lifecycle.
- **Internal only**: only employees can access the platform. Guests, customers, contractors
  outside the directory, and federation with other organizations' chat systems are out of scope
  for v1.
- **No end-to-end encryption**: messages are encrypted in transit and at rest, but the server can
  read them. This is required for server-side search, retention enforcement, malware scanning, and
  lawful export — all of which are stated requirements and all of which end-to-end encryption
  would make impossible.
- **Responsive web application, browser notifications only**: v1 targets a browser on desktop and
  mobile. Native iOS and Android applications are out of scope, and so is native mobile push.
  Employees on iOS receive notifications only if they add the platform to their home screen — a
  known limitation of that platform, surfaced to the user by FR-040 rather than hidden.
- **Peak concurrency**: 7,000 of 10,000 employees connected simultaneously at peak, with typical
  message volume of roughly 50 messages per employee per day.
- **History visibility for new group members**: a member added to a group sees history from their
  join date forward by default, with the group creator able to grant full history at creation
  time. The rule in effect is shown to members.
- **Edit and delete window**: employees may edit or delete their own messages for 24 hours, after
  which content is fixed. Deletion removes content from view but the audit record persists.
- **Attachment limits**: images up to 25 MB; videos up to 500 MB and 10 minutes. With a 12-month
  retention period these limits keep total storage bounded and projectable (SC-016).
- **Meetings are not recorded** in v1. Meeting recording introduces storage, consent, and
  retention obligations disproportionate to v1 value.
- **No legal hold**: retention deletion at 12 months is unconditional. If the organization later
  acquires a litigation-hold obligation, that is a new feature, not a configuration change — the
  hold must be able to override automatic deletion, which v1 deliberately does not support.
- **Message threading, reactions, read receipts, custom emoji, bots, and integrations** are out of
  scope for v1. They are natural v2 candidates and none of them block the stories above.
- **Availability window**: the platform serves a single organization in a small number of adjacent
  time zones, so a nightly maintenance window is acceptable.

**Constrained by the project constitution** (`.specify/memory/constitution.md` v1.2.0):

- Every capability above MUST be delivered with free, self-hosted, open-source components running
  under Docker Compose. This is a hard scope boundary and is why FR-057 and SC-005 exist.
- User Stories 8 and 9 are the sole exception to single-host deployment: meeting capacity of 1,250
  concurrent participants requires a dedicated media host. Constitution v1.2.0 was amended to
  permit exactly this one additional host and no more.

## Clarifications

### Session 2026-07-31

- **Q**: Maximum participants per video meeting, and maximum concurrent meetings?
  **A**: Up to 25 participants per meeting, up to 50 concurrent meetings, ceiling of 1,250
  concurrent participants platform-wide. Encoded as FR-042, FR-043, FR-044, SC-014, SC-015.
  Consequence: meetings need a dedicated media host separate from the application host, which
  required constitution amendment v1.2.0.
- **Q**: Retention period for messages and attachments, and is legal hold required?
  **A**: 12 months, then automatic deletion. No legal hold. Encoded as FR-052 through FR-055,
  SC-016, SC-025. Consequence: storage is bounded and projectable; a future litigation-hold
  obligation would be a new feature, not a setting.
- **Q**: How far must notifications reach when an employee is away from their desk?
  **A**: Browser and desktop notifications only. No native mobile applications, no paid mobile
  push. Encoded as FR-034, FR-040, SC-008. Consequence: Constitution Principle VIII holds intact;
  iOS employees must add the platform to their home screen, and FR-040 requires the platform to
  tell them so rather than letting them believe they are reachable.

## Dependencies

- An existing corporate identity system supporting standard single sign-on, with a group or
  attribute the platform can read to determine administrators.
- A Linux host with sufficient CPU, memory, disk, and network capacity for 7,000 concurrent
  connections plus 12 months of attachment storage. Capacity sizing is a planning output.
- A second Linux host dedicated to meeting media, sized for 1,250 concurrent participants, with
  sufficient upstream bandwidth. Required only for User Stories 8 and 9; Stories 1 through 7 are
  deliverable without it.
- Employees' browsers must permit camera, microphone, screen-capture, and notification access for
  User Stories 8, 9, and 4 respectively.
- Organizational sign-off that a hard 12-month deletion with no legal hold is acceptable, before
  FR-052 ships. Once history begins expiring, it cannot be recovered.
