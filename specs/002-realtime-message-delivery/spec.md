# Feature Specification: Instant Message Delivery

**Feature Branch**: `002-realtime-message-delivery`

**Created**: 2026-09-25

**Status**: Draft

**Input**: User description: "chỉnh lại để có thể chát thì nhận ngay lập tức, hiện tại đang thấy bị delay"
(Adjust chat so that messages are received immediately; there is currently a noticeable delay.)

## Context

Feature 001 already commits to delivering a message to online recipients without any action on
their part (FR-009) and sets a responsiveness target (SC-006: 95% within 0.5 seconds, 99% within
1 second). In practice, employees report a visible lag between a colleague pressing send and the
message appearing — enough that a conversation feels like email rather than chat. This feature
closes that gap: it makes "instant" the experienced behaviour, makes the lag measurable, and
prevents it from coming back. It changes no product scope; it tightens and enforces how fast the
existing messaging behaves.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Recipient sees a message the moment it is sent (Priority: P1)

Two colleagues are chatting. When one presses send, the other sees the message appear in the open
conversation straight away — fast enough that it feels like talking, not like waiting for a
refresh. This holds whether the conversation has been busy for an hour or silent all morning.

**Why this priority**: This is the complaint. Every other improvement is secondary to the
recipient no longer waiting. Delivered alone, it resolves the reported problem.

**Independent Test**: Sign in as two employees in two browsers on the same network. Leave the
system idle for several minutes, then send 50 messages spaced a few seconds apart and record, for
each, the time between pressing send and the message appearing on the other screen. Confirm the
distribution meets SC-001 and that the first message after the idle period is no slower than the
rest.

**Acceptance Scenarios**:

1. **Given** two signed-in colleagues with a direct conversation open, **When** one sends a
   message, **Then** it appears for the other within the stated delivery time (SC-001) with no
   refresh or other action.
2. **Given** the platform has carried no messages for 10 minutes, **When** an employee sends a
   message, **Then** it is delivered within the same time as a message sent during steady traffic.
3. **Given** a recipient who is online but viewing a different conversation, **When** a message
   arrives, **Then** that conversation's unread indicator updates within the same delivery time.
4. **Given** a recipient signed in on two devices, **When** a message arrives, **Then** it appears
   on both within the delivery time.

---

### User Story 2 - Sender sees their own message immediately (Priority: P2)

When an employee presses send, their own message shows in the conversation at once, clearly marked
as sending, and switches to sent as soon as the platform has accepted it. They never wonder
whether the button worked, and their other open devices show the message just as quickly.

**Why this priority**: Perceived lag on the sender's side is as damaging as real lag on the
recipient's side — a sender who sees nothing happen presses send again. It is independent of
Story 1 and valuable on its own.

**Independent Test**: In one browser, send 50 messages and record the time from pressing send to
the message appearing in the sender's own view, and to the "sent" state. Confirm both meet SC-002.

**Acceptance Scenarios**:

1. **Given** a signed-in employee, **When** they press send, **Then** the message appears in their
   own conversation view immediately, marked as sending.
2. **Given** a message marked as sending, **When** the platform accepts it, **Then** the mark
   changes to sent within the stated time and the message does not move, flicker, or duplicate.
3. **Given** the platform rejects the message (for example, the sender is no longer a member),
   **When** the rejection arrives, **Then** the message is marked as failed with the reason, and
   is never shown to anyone else.
4. **Given** the sender has the same conversation open on a second device, **When** they send,
   **Then** the message appears on the second device within the recipient delivery time.

---

### User Story 3 - Delivery stays instant under load and after interruptions (Priority: P3)

Delivery stays fast at the busiest hour of the day, in large groups, and for an employee whose
connection has just dropped and come back. A reconnecting employee catches up on what they missed
within seconds, and from then on new messages arrive instantly again.

**Why this priority**: Stories 1 and 2 fix the everyday case; this story keeps the fix from
collapsing exactly when the most people are watching. It depends on Stories 1 and 2 being in
place to be meaningful.

**Independent Test**: Under simulated peak load (feature 001 SC-011, SC-012), measure delivery
time in a direct conversation and in a 500-member group, then disconnect one client for two
minutes while messages continue, reconnect it, and time how long until it is fully caught up and
receiving new messages live.

**Acceptance Scenarios**:

1. **Given** the platform at peak sustained traffic, **When** messages are sent in a direct
   conversation, **Then** delivery times still meet SC-001.
2. **Given** a 500-member group, **When** a message is posted, **Then** every online member
   receives it within SC-003's time, and delivery in unrelated conversations is not slowed.
3. **Given** an employee whose connection dropped for up to 5 minutes, **When** it is restored,
   **Then** every missed message appears, in order, exactly once, within SC-004's time, and
   subsequent messages arrive live.

---

### Edge Cases

- The first message after a long quiet period (overnight, lunchtime) must not be slower than
  messages during steady traffic.
- A burst of messages from one sender (e.g. pasting several lines quickly) arrives in send order
  and without being held back to be delivered together.
- A recipient whose connection can only use a fallback, less efficient channel (for example,
  restrictive office network) still receives messages within the relaxed target in SC-005, and
  the interface tells them their connection is degraded.
- The recipient's browser tab is in the background; the message is still received immediately and
  shown when the tab is focused, and the unread indicator and notification are unaffected.
- A message that the sender edits or deletes seconds after sending reflects its final state for
  recipients; an intermediate state is never shown after the final one.
- A component involved in delivery is briefly unavailable: messages already accepted are delayed
  but never lost or duplicated, and delivery returns to normal speed without manual intervention.
- The same message delivered twice to a device (for example, during a reconnect) is shown once.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST deliver an accepted message to every online member's open sessions
  without the recipient taking any action, within the targets in SC-001.
- **FR-002**: Delivery time MUST NOT depend on how long the platform has been idle; the first
  message after any quiet period MUST meet the same target as messages during steady traffic.
- **FR-003**: System MUST NOT hold back an accepted message to group it with later messages; each
  message is delivered as soon as it is accepted.
- **FR-004**: The sender's own view MUST show a message immediately on send, marked as sending, and
  MUST mark it sent or failed as soon as the platform decides, without reordering or duplicating it.
- **FR-005**: The sender's other signed-in devices MUST receive the sender's message within the
  same target as recipients.
- **FR-006**: Unread indicators for conversations the recipient is not currently viewing MUST
  update within the same target as message delivery.
- **FR-007**: A reconnecting client MUST receive every message it missed, in conversation order and
  exactly once, and then resume live delivery, within SC-004.
- **FR-008**: Speeding up delivery MUST NOT weaken any existing guarantee: an accepted message is
  never lost, never duplicated per device, never reordered, and never shown to anyone who is not a
  member of the conversation at the time it is sent (feature 001 FR-009 to FR-012, SC-017, SC-022).
- **FR-009**: A message the platform rejects MUST never reach any recipient, even briefly.
- **FR-010**: When a client is connected by a degraded channel, the interface MUST indicate this to
  the employee, and delivery MUST still meet SC-005.
- **FR-011**: The platform MUST continuously measure send-to-display delivery time and make its
  distribution (at the percentiles in SC-001) visible to operators, so a regression is detected
  without relying on employee reports.
- **FR-012**: Operators MUST be alerted when delivery time breaches SC-001 for a sustained period
  of 5 minutes.
- **FR-013**: The delivery targets in this feature MUST be verified by an automated check that
  fails the release if they are not met, including the idle-then-send case of FR-002.

### Key Entities

- **Delivery measurement**: one observation of a message's journey — when the sender pressed send,
  when the platform accepted it, and when it was displayed on each recipient device — used only in
  aggregate to report delivery-time percentiles. Carries no message content.
- **Message send state**: what the sender sees for their own message — sending, sent, or failed
  (with reason).

## Success Criteria *(mandatory)*

### Measurable Outcomes

**Responsiveness**

- **SC-001**: 95% of messages appear for all online recipients within 0.3 seconds of the sender
  pressing send, and 99% within 0.5 seconds, measured on the organization's network.
- **SC-002**: A sent message appears in the sender's own view within 0.1 seconds of pressing send
  in 99% of cases, and shows as sent within 0.3 seconds in 95% of cases.
- **SC-003**: In a 500-member group, 95% of online members receive a message within 1 second.
- **SC-004**: A client reconnecting after up to 5 minutes offline is fully caught up and receiving
  live messages within 3 seconds of the connection returning.
- **SC-005**: A client on a degraded connection channel receives 95% of messages within 1 second.
- **SC-006**: The first message sent after 10 or more idle minutes meets SC-001, in 100 out of 100
  trials.

**Scale**

- **SC-007**: SC-001 holds with 7,000 simultaneously connected employees and 100 messages per
  second sustained, and SC-003 holds during bursts of 1,000 messages per second.

**Access control**

- **SC-008**: Zero messages are delivered, even transiently, to a device of anyone who is not a
  member of the conversation, and zero rejected messages reach any recipient — verified by
  automated tests covering the faster delivery path.

**Auditability**

- **SC-009**: Delivery-time percentiles are recorded for every minute of operation and retained for
  at least 30 days, so any reported delay can be checked against what was measured at that time.
  No measurement contains message content.

**Data durability**

- **SC-010**: Across 10,000 messages sent under peak load with deliberate client disconnects and a
  brief outage of one delivery component, zero messages are lost, duplicated on any device, or
  shown out of order.

**User outcome**

- **SC-011**: In a follow-up check with pilot users, at least 90% agree that messages arrive
  "instantly", and reports of message delay drop to near zero.

## Assumptions

- The targets are measured on the organization's internal network with employees using supported
  browsers on typical office hardware; delays caused by an employee's own poor home connection are
  outside what the platform can guarantee.
- SC-001 and SC-002 tighten feature 001's SC-006 (0.5 s / 1 s). When this feature ships, feature
  001's SC-006 is superseded by SC-001 here.
- The observed delay originates between the platform accepting a message and pushing it to
  recipients, not in the employee's device; the exact cause is established during planning.
- No change in product scope: conversations, membership, notifications, and meetings behave as in
  feature 001. Browser notifications (feature 001 SC-008, 5 seconds) keep their own target.
- The existing durability, ordering, and access guarantees are non-negotiable and constrain how
  delivery may be sped up.
- All measurement and alerting use the platform's existing self-hosted monitoring; nothing new is
  bought or hosted externally (Constitution Principle VIII).
