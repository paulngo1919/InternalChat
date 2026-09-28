# Feature Specification: Public Self-Service Registration

**Feature Branch**: `004-public-self-registration`

**Created**: 2026-09-28

**Status**: Draft

**Input**: User description: "Public self-service registration for chat.benda.io.vn. An outside person opening `https://chat.benda.io.vn` can create their own account (username, email, password, display name) on the sign-in page, then sign in and chat immediately. On first sign-in, the platform provisions the employee record just-in-time from the identity claims instead of refusing the person as unknown, so no admin step is needed. This deliberately relaxes the base spec's 'corporate directory is the sole authority' assumption for the public deployment only; it must be switchable (off by default for the corporate/internal deployment, on for the public deployment). Abuse controls appropriate for an internet-facing sign-up: email verification or equivalent, brute-force protection, rate limiting of registrations, and the option to place an identity-aware proxy in front. Self-registered users must be distinguishable from directory-provisioned employees, can be deactivated by an admin (existing revocation within 5 minutes still applies), and must not receive admin privileges. Existing deactivated employees must not be resurrected by just-in-time provisioning."

## Clarifications

### Session 2026-09-28

- Q: Which verification step is required before a new account may chat — email confirmation,
  administrator approval, or none beyond rate limiting? → A: Administrator approval. No outgoing
  mail service is added; a newcomer can register and sign in at once but sees only an
  awaiting-approval page until an administrator approves them.

## Context

The base specification (`specs/001-enterprise-chat-platform/spec.md`) assumes a corporate directory
that already holds every person allowed to chat (Assumption "Corporate directory exists", FR-001,
FR-003). A person who is not in that directory is refused even with a valid sign-in, and today the
only way to add someone is an administrator creating the identity account *and* the employee
record by hand on the host machine.

The public deployment at `https://chat.benda.io.vn` has no corporate directory behind it. This
feature introduces a second, explicitly chosen **enrolment mode** for a deployment:

| Mode | Who decides who can chat | Default for |
| --- | --- | --- |
| **Directory** (existing behaviour) | The corporate directory; unknown identities are refused | Corporate/internal deployments |
| **Open registration** (this feature) | The person registers themself; an administrator approves each account before it can chat, and can remove it later | The public deployment |

Every other rule of the base specification — deny-by-default access to conversations, audit,
revocation within 5 minutes, no platform-stored passwords — continues to apply in both modes.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - A newcomer registers, is approved, and starts chatting (Priority: P1)

A person who has never used the platform opens the public address, chooses to create an account,
and enters a username, email address, display name, and password. They are signed in straight away
but see only a notice that their account is awaiting approval. An administrator sees the pending
account, approves it, and the person can then find other people and start a conversation — without
signing in again and without anyone creating records by hand.

**Why this priority**: This is the whole feature. Without it, nobody outside the seeded roster can
use the public deployment at all.

**Independent Test**: On a deployment in open-registration mode, register a brand-new account from
a browser outside the host network, approve it as an administrator, and send a message to an
existing user; confirm the existing user receives it.

**Acceptance Scenarios**:

1. **Given** a deployment in open-registration mode, **When** a visitor opens the sign-in page,
   **Then** an option to create an account is visible alongside sign-in.
2. **Given** a visitor who has just completed the registration form, **When** they finish, **Then**
   they are signed in and see a page stating their account is awaiting approval, and nothing else —
   no conversations, no people directory.
3. **Given** a pending account, **When** an administrator opens the list of pending accounts,
   **Then** it appears with its username, display name, email, registration time, and source.
4. **Given** a pending person still on the awaiting-approval page, **When** an administrator
   approves them, **Then** within 60 seconds they reach the conversation list without signing in
   again.
5. **Given** an approved person, **When** they search the people directory, **Then** they can find
   existing users and start a one-to-one conversation, and existing users can find them.
6. **Given** an approved person, **When** they sign in a second time, **Then** they reach the same
   account and profile, not a duplicate.
7. **Given** a pending account, **When** an administrator rejects it, **Then** the person sees that
   registration was not accepted and cannot sign in or register again with the same email.

---

### User Story 2 - The internal deployment stays closed (Priority: P1)

An operator running the platform for the corporation, with the directory as the authority, keeps
exactly today's behaviour: there is no way to create an account from the sign-in page, and a
valid identity that is not in the directory is refused.

**Why this priority**: Shipping open registration must not silently open the corporate deployment.
This is a security regression guard and is as critical as the feature itself.

**Independent Test**: Start a deployment with no enrolment setting at all; confirm the sign-in page
offers no registration, and that an identity created outside the directory is refused on first
sign-in and the refusal is audited.

**Acceptance Scenarios**:

1. **Given** a deployment where enrolment mode has not been configured, **When** it starts,
   **Then** it runs in directory mode.
2. **Given** directory mode, **When** a visitor opens the sign-in page, **Then** no
   account-creation option is offered, and a direct request to the registration address is refused.
3. **Given** directory mode and an identity that has no employee record, **When** that identity
   signs in, **Then** access is refused exactly as today and the refusal is recorded.

---

### User Story 3 - An administrator removes an abusive self-registered user (Priority: P2)

An administrator notices a self-registered account posting spam. They can see that the account was
self-registered (not from the directory), when it registered, and deactivate it. The person loses
access within the platform's existing revocation window, and signing up again with the same email
address does not bring the account back.

**Why this priority**: Open registration on the internet will attract misuse; a way to remove it
is required before the feature can be left running, but the first sign-ups work without it.

**Independent Test**: Register an account, deactivate it as an administrator, confirm its open
sessions end within 5 minutes, and confirm neither signing in again nor re-registering with the
same email restores access.

**Acceptance Scenarios**:

1. **Given** a list of people, **When** an administrator views it, **Then** each person shows
   whether they joined by self-registration or from the directory, and the registration date.
2. **Given** a signed-in self-registered person, **When** an administrator deactivates them,
   **Then** their sessions and live connections end within 5 minutes and the action is audited.
3. **Given** a deactivated person, **When** they sign in again, **Then** access is refused and they
   are not re-created or reactivated.
4. **Given** a deactivated person's email address, **When** someone attempts to register a new
   account with it, **Then** registration is refused.

---

### User Story 4 - The sign-up form resists automated abuse (Priority: P2)

An operator of the public deployment can leave registration open without the platform being
flooded by scripted sign-ups or password-guessing.

**Why this priority**: Required for responsible internet exposure, but independent of the core
journey and testable on its own.

**Independent Test**: Script repeated registrations and repeated wrong-password sign-ins from one
source and confirm both are throttled and recorded, while a normal person from another source is
unaffected.

**Acceptance Scenarios**:

1. **Given** open-registration mode, **When** one source submits more registrations than the
   configured limit within the limit window, **Then** further registrations from that source are
   refused until the window passes, and each refusal is recorded.
2. **Given** any account, **When** wrong passwords are entered repeatedly, **Then** further sign-in
   attempts for that account are temporarily blocked and the block is recorded.
3. **Given** a registration form, **When** a password below the minimum strength is submitted,
   **Then** registration is refused with a message stating the rule that was not met.
4. **Given** the operator has placed an identity-aware access gate in front of the public address,
   **When** a visitor who has not passed that gate opens the address, **Then** they cannot reach the
   registration page; the application itself needs no change for this.

---

### Edge Cases

- **Username or email already taken**: registration is refused with a message that does not reveal
  whether the existing account is active, deactivated, or directory-provisioned.
- **Email or display name changed later**: the employee record follows the person's current
  profile at their next sign-in; it never creates a second record.
- **Collision with a directory employee**: a self-registration using the email address of a
  directory-provisioned employee is refused; self-registration can never take over or merge into a
  directory identity.
- **Two first sign-ins at the same moment** (e.g., two tabs): exactly one employee record results.
- **Mode switched from open registration back to directory**: existing self-registered people keep
  working until an administrator deactivates them, new registrations stop immediately, and a
  self-registered identity with no employee record yet (registered but never signed in) is refused, and accounts still awaiting approval stay pending.
- **Display name containing markup, control characters, or impersonation** (e.g., "Administrator"):
  markup is rendered as text; the name is length-limited; reserved names are refused.
- **No administrator acts**: a pending account stays pending (and useless) until approved, rejected,
  or automatically rejected after the configured period (FR-009d).
- **Two administrators act on one pending account at once**: the first decision wins; the second
  sees that the account has already been decided.
- **Pending person already signed in on several devices when approved**: every device gains access
  without signing in again.
- **First sign-in while the platform's background systems are degraded**: provisioning either
  completes or the person sees a retryable error; a partial record never grants access.

## Requirements *(mandatory)*

### Functional Requirements

#### Enrolment mode

- **FR-001**: Each deployment MUST run in exactly one enrolment mode — *directory* or *open
  registration* — chosen by the operator at deployment time.
- **FR-002**: When no enrolment mode is configured, the deployment MUST run in *directory* mode.
- **FR-003**: In *directory* mode the system MUST behave exactly as the base specification requires:
  no self-registration is offered or accepted, and an identity without an employee record is
  refused and audited.
- **FR-004**: The enrolment mode in effect MUST be recorded when the deployment starts, and every
  change of mode MUST be recorded in the audit log with the operator and time.

#### Registration

- **FR-005**: In *open registration* mode, the sign-in page MUST offer account creation collecting
  username, email address, display name, and password.
- **FR-006**: The platform MUST NOT store the person's password itself; credentials remain the
  responsibility of the identity provider, as in base FR-001.
- **FR-007**: Registration MUST enforce a minimum password strength of at least 10 characters and
  refuse passwords found in a list of commonly used passwords.
- **FR-008**: Registration MUST refuse a username or email address that already belongs to any
  account, active or deactivated, directory-provisioned or self-registered, without revealing which.
- **FR-009**: A newly registered account MUST NOT grant access to any conversation, the people
  directory, or any other person's data until an administrator has approved it. Until then the
  person, when signed in, MUST see only a page stating that their account is awaiting approval.
- **FR-009a**: Administrators MUST be able to list accounts awaiting approval (oldest first, showing
  username, display name, email, registration time, and source address) and approve or reject each.
- **FR-009b**: On approval, the person MUST gain access at their next request without signing in
  again, within 60 seconds. On rejection, the account MUST be treated as deactivated (FR-013,
  FR-019). Every approval and rejection MUST be audited with the approving administrator.
- **FR-009c**: A person awaiting approval MUST NOT appear in the people directory or search, and
  MUST NOT be addable to any conversation.
- **FR-009d**: Accounts left awaiting approval for longer than a configurable period (default 30
  days) MUST be automatically rejected, and the rejection audited with the system as actor.
- **FR-010**: Display names MUST be 1–100 characters after trimming, MUST have control characters
  removed, and MUST NOT match a configurable list of reserved names (at least "admin",
  "administrator", "system", "support", and the product name).

#### Just-in-time provisioning

- **FR-011**: In *open registration* mode, when a self-registered identity signs in and has no
  employee record, the system MUST create one from the identity's subject, display name, and email
  address, in the *awaiting approval* state (FR-009), and MUST show the awaiting-approval page on that
  same sign-in rather than refusing the person as unknown.
- **FR-012**: Provisioning MUST be idempotent: concurrent or repeated first sign-ins for the same
  identity MUST result in exactly one employee record.
- **FR-013**: Provisioning MUST NOT reactivate, overwrite, or create a duplicate of an existing
  employee record that is deactivated; such a sign-in MUST be refused and audited.
- **FR-014**: Provisioning MUST NOT create a record for an identity that did not come from
  self-registration on this deployment (for example, an identity from another realm, client, or
  directory source), even in *open registration* mode.
- **FR-015**: At each later sign-in, the system MUST update the person's display name and email
  address from their current identity profile, subject to FR-010.

#### Distinction, privileges, and removal

- **FR-016**: Every employee record MUST carry its origin — *directory* or *self-registered* — and
  the time it was created; administrators MUST be able to see and filter by origin.
- **FR-017**: Self-registration and just-in-time provisioning MUST only ever grant the ordinary
  member role; administrator rights MUST NOT be obtainable by any self-service path.
- **FR-018**: Administrators MUST be able to deactivate a self-registered person, and base FR-003
  (revocation of sessions within 5 minutes) MUST apply to them unchanged.
- **FR-019**: A deactivated self-registered person's email address and username MUST remain
  reserved so the same address cannot be used to register again.
- **FR-020**: Self-registered people MUST be subject to every access rule of the base specification;
  in particular base FR-002 (deny by default, member-only access) applies to them unchanged.

#### Abuse controls

- **FR-021**: The system MUST limit registrations per source address to a configurable number per
  hour (default 5), and MUST limit total registrations to a configurable number per day (default
  200); refusals MUST be recorded.
- **FR-022**: The system MUST temporarily block sign-in for an account after a configurable number
  of consecutive failed attempts (default 5), with the block lengthening on repeat, and record it.
- **FR-023**: The public deployment MUST remain compatible with an operator-provided identity-aware
  access gate in front of it, with no application change required to enable it.

#### Audit

- **FR-024**: The system MUST record every registration attempt (accepted or refused, with reason),
  approval, rejection (manual or automatic), just-in-time provisioning, refused provisioning, rate-limit refusal,
  and sign-in block with actor, subject, timestamp, source address, and outcome, in the same
  tamper-resistant log required by base FR-006.

### Key Entities

- **Enrolment mode**: A deployment-wide setting, *directory* or *open registration*, with the time it
  took effect and who set it.
- **Employee** (extended): The existing person record, now also carrying its **origin**
  (*directory* or *self-registered*) and creation time, and a new state *awaiting approval* before
  *active*. Transitions: awaiting approval → active (approved) or → deactivated (rejected,
  manually or automatically); active → deactivated. Nothing leaves *deactivated* by self-service.
  Once active, a self-registered employee is otherwise identical to a directory employee.
- **Approval decision**: Who approved or rejected a pending employee, when, and (for rejection)
  whether it was manual or automatic.
- **Registration attempt**: An audit record of one try to create an account — source address, time,
  requested username/email (as recorded identifiers, not credentials), outcome, and refusal reason.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A newcomer can complete registration in under 2 minutes, and can send their first
  message within 1 minute of an administrator approving them.
- **SC-001a**: An administrator can review and approve or reject a pending account in under
  30 seconds from opening the pending list.
- **SC-002**: 95% of first sign-ins after registration reach the awaiting-approval page, and 95% of
  approved people reach the conversation list, within 2 seconds of the identity provider returning
  them to the application (**responsiveness**).
- **SC-003**: The platform sustains 50 registrations per minute and 100 concurrent first sign-ins
  with zero duplicate employee records and zero failed provisionings not followed by a successful
  retry (**scale**).
- **SC-004**: On a deployment with no enrolment mode configured, 100% of attempts to register or
  to sign in with an identity lacking an employee record are refused (**access control**).
- **SC-005**: Zero self-registered people hold administrator rights at any time, verified by an
  automated check over all self-registered records (**access control**).
- **SC-006**: A deactivated self-registered person loses all access within 5 minutes, and 100% of
  their attempts to sign in again or re-register with the same email are refused
  (**access control**).
- **SC-007**: 100% of registration attempts, provisionings, rate-limit refusals, and sign-in blocks
  appear in the audit log with actor, subject, timestamp, source address, and outcome
  (**auditability**).
- **SC-008**: An account whose registration was accepted is never lost: after any restart of the
  platform, the person can still sign in and reach the same conversations (**data durability**).
- **SC-009**: A scripted burst of 100 registrations from one source within one hour results in no
  more than the configured per-source limit being accepted.

## Assumptions

- **The public deployment is one community**: everyone on `chat.benda.io.vn` — seeded users and
  self-registered people alike — can find and message each other. Separating self-registered
  people from directory employees within one deployment is out of scope.
- **The identity provider does the registration**: account creation, credential storage, password
  policy enforcement, and brute-force detection are performed by the existing identity provider;
  the platform's own work is the enrolment-mode switch, provisioning, origin tracking, and audit.
- **Registration rate limits are enforced at the public edge** in front of the identity provider,
  since the identity provider's own controls cover failed sign-ins but not sign-up volume.
- **The identity-aware access gate is optional and operator-owned**: the platform must work with or
  without it; configuring it is not part of this feature.
- **No new paid service**: any service added for this feature (for example outgoing mail) must be
  free, open source, and self-hostable, per constitution Principle VIII.
- **Out of scope**: social sign-in (Google, Facebook), password reset by administrators on behalf of
  users, invitation links, per-organization tenancy, and deletion of a person's data on request.
- **Base spec amendment**: this feature amends base Assumption "Corporate directory exists" and base
  FR-001/FR-003 to say they hold in *directory* mode; the amendment will be recorded in the base
  spec when this feature is planned.
