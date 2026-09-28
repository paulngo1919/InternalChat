# Tasks: Responsive App Redesign

**Input**: Design documents from `specs/003-responsive-app-redesign/`

**Prerequisites**: plan.md (required), spec.md (required for user stories), research.md, data-model.md, quickstart.md

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Project initialization and basic structure

- [x] T001 Setup base CSS media queries in `src/internalchat-web/src/assets/styles/index.css` for breakpoints (<768px, 768px-1024px, >1024px)
- [x] T002 Add LayoutState context or hooks to track window dimensions in `src/internalchat-web/src/features/layout/useLayoutState.ts`

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core infrastructure that MUST be complete before ANY user story can be implemented

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

- [x] T003 Update main `App.tsx` layout structure to use the new CSS Grid / Flexbox foundation in `src/internalchat-web/src/App.tsx`
- [x] T004 [P] Apply base `min-height: 44px` and `min-width: 44px` touch targets in `src/internalchat-web/src/assets/styles/index.css`

---

## Phase 3: User Story 1 - Desktop User Experience (Priority: P1) 🎯 MVP

**Goal**: Seamless view on a desktop monitor with a wide layout, side-by-side components.

**Independent Test**: Maximize window > 1024px, verify sidebar and chat view do not overlap.

### Tests for User Story 1 (REQUIRED) ⚠️

- [ ] T005 [P] [US1] Unit test for desktop layout rendering in `src/internalchat-web/src/features/layout/__tests__/DesktopLayout.test.tsx`

### Implementation for User Story 1

- [x] T006 [P] [US1] Implement desktop CSS Grid layout classes in `src/internalchat-web/src/assets/styles/index.css`
- [ ] T007 [US1] Update `Sidebar` component for desktop width constraints in `src/internalchat-web/src/features/layout/Sidebar.tsx`
- [ ] T008 [US1] Update `MessageList` layout for desktop in `src/internalchat-web/src/features/messages/MessageList.tsx`

---

## Phase 4: User Story 2 - Tablet/iPad User Experience (Priority: P1)

**Goal**: Layout tailored to touch interactions, collapsible sidebars.

**Independent Test**: Simulate iPad Mini (768px), verify UI adapts, sidebar collapses.

### Tests for User Story 2 (REQUIRED) ⚠️

- [ ] T009 [P] [US2] Unit test for tablet layout and collapsible sidebar in `src/internalchat-web/src/features/layout/__tests__/TabletLayout.test.tsx`

### Implementation for User Story 2

- [x] T010 [P] [US2] Add tablet media query styles in `src/internalchat-web/src/assets/styles/index.css`
- [ ] T011 [US2] Implement collapsible sidebar state in `src/internalchat-web/src/features/layout/Sidebar.tsx`
- [x] T012 [US2] Update touch targets and fonts for tablet in `src/internalchat-web/src/assets/styles/index.css`

---

## Phase 5: User Story 3 - Mobile User Experience (Priority: P1)

**Goal**: Fully mobile-optimized, single-column layout, ensuring core chat functionalities are accessible without horizontal scrolling.

**Independent Test**: Simulate iPhone 13 (390px), verify single-column layout and view transitions.

### Tests for User Story 3 (REQUIRED) ⚠️

- [ ] T013 [P] [US3] Unit test for mobile single-column transition logic in `src/internalchat-web/src/features/layout/__tests__/MobileLayout.test.tsx`

### Implementation for User Story 3

- [x] T014 [P] [US3] Add mobile media query flexbox styles in `src/internalchat-web/src/assets/styles/index.css`
- [x] T015 [US3] Implement mobile navigation state (Channel list <-> Chat view) in `src/internalchat-web/src/App.tsx` (formerly MobileNav.tsx)
- [ ] T016 [US3] Ensure chat input remains fixed above the keyboard on mobile in `src/internalchat-web/src/features/messages/ChatInput.tsx`

---

## Phase N: Polish & Cross-Cutting Concerns

**Purpose**: Improvements that affect multiple user stories

- [ ] T017 [P] Verify layout shift < 50ms and CLS < 0.1 via Lighthouse audit
- [ ] T018 Code cleanup and removal of unused CSS classes
- [ ] T019 Run quickstart.md validation manually across Desktop, Tablet, and Mobile devices

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies - can start immediately
- **Foundational (Phase 2)**: Depends on Setup completion - BLOCKS all user stories
- **User Stories (Phase 3-5)**: All depend on Foundational phase completion
  - Can proceed in parallel as they touch different aspects or CSS queries.
- **Polish (Final Phase)**: Depends on all user stories being complete

### User Story Dependencies

- **User Story 1 (P1)**: Can start after Foundational
- **User Story 2 (P1)**: Can start after Foundational
- **User Story 3 (P1)**: Can start after Foundational

### Parallel Opportunities

- All test creation tasks marked `[P]` can run in parallel.
- CSS updates per media query can run in parallel.
