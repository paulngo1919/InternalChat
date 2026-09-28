# Tasks: unique-theme

**Input**: Design documents from `/specs/005-unique-theme/`

**Prerequisites**: plan.md (required), spec.md (required for user stories), research.md, data-model.md, contracts/

**Tests**: Tests are MANDATORY per Constitution Principle III. Every user story MUST include test tasks.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Project initialization and basic structure

*(No setup required - using existing React SPA)*

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core infrastructure that MUST be complete before ANY user story can be implemented

*(No foundational tasks required)*

---

## Phase 3: User Story 1 - Apply Unique Brand Identity (Priority: P1) 🎯 MVP

**Goal**: Implement the core dark and light mode color palette using CSS variables.

**Independent Test**: Visually compare the application's appearance against typical generic AI tools.

### Implementation for User Story 1

- [x] T001 [P] [US1] Define dark mode CSS variables (`[data-theme="dark"]`) using a non-generic HSL palette in `src/internalchat-web/src/index.css`
- [x] T002 [P] [US1] Define light mode CSS variables (`[data-theme="light"]`) using a non-generic HSL palette in `src/internalchat-web/src/index.css`
- [x] T003 [US1] Refactor existing hardcoded colors in `src/internalchat-web/src/index.css` to use the new CSS variables.

**Checkpoint**: At this point, User Story 1 should be fully functional (by manually setting `data-theme` on HTML).

---

## Phase 4: User Story 2 - Toggle Dark and Light Mode (Priority: P2)

**Goal**: Seamlessly support toggling between Dark, Light, and System modes via the UI.

**Independent Test**: Switch OS theme and click UI toggle to verify instant update.

### Tests for User Story 2 (REQUIRED) ⚠️

- [x] T004 [P] [US2] Unit tests for `useTheme` hook in `src/internalchat-web/src/features/layout/__tests__/useTheme.test.ts`

### Implementation for User Story 2

- [x] T005 [P] [US2] Create the `useTheme` hook to manage `theme_preference` in LocalStorage and apply it in `src/internalchat-web/src/features/layout/useTheme.ts`
- [x] T006 [P] [US2] Create the `ThemeToggle` UI component in `src/internalchat-web/src/features/layout/ThemeToggle.tsx`
- [x] T007 [US2] Integrate the `ThemeToggle` component into the `Sidebar` or `App` layout in `src/internalchat-web/src/App.tsx`
- [x] T008 [US2] Integrate `useTheme` into `App.tsx` so the theme applies automatically on load.

**Checkpoint**: At this point, User Stories 1 AND 2 should both work independently.

---

## Phase N: Polish & Cross-Cutting Concerns

**Purpose**: Improvements that affect multiple user stories

- [x] T009 [P] Run quickstart.md validation to ensure no FOUC occurs and toggle is < 50ms.

---

## Dependencies & Execution Order

### Phase Dependencies

- **User Stories (Phase 3+)**: US1 must complete before US2.

### Within Each User Story

- Tests MUST be written and observed FAILING before implementation.
- Core implementation before integration.

### Parallel Opportunities

- T001 and T002 can be done in parallel.
- T004, T005, and T006 can be done in parallel.
