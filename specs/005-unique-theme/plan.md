# Implementation Plan: [FEATURE]

**Branch**: `[###-feature-name]` | **Date**: [DATE] | **Spec**: [link]

**Input**: Feature specification from `/specs/[###-feature-name]/spec.md`

**Note**: This template is filled in by the `/speckit-plan` command. See `.specify/templates/plan-template.md` for the execution workflow.

## Summary

Implement a unique, non-generic brand color theme using vanilla CSS variables, fully supporting a seamless toggle between Dark, Light, and System modes.

## Technical Context

**Language/Version**: TypeScript / CSS3

**Primary Dependencies**: React 19, Vanilla CSS (no third-party styling frameworks)

**Storage**: LocalStorage (`theme_preference`)

**Testing**: Jest / React Testing Library for the `useTheme` hook

**Target Platform**: Web (InternalChat SPA)

**Project Type**: Frontend feature

**Performance Goals**: Theme toggle must reflect in < 50ms; zero API overhead.

**Constraints**: No Flash of Unstyled Content (FOUC), WCAG 2.1 AA contrast ratios

**Scale/Scope**: Frontend only; updates to `index.css` and a new layout hook/component.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

*Source: `.specify/memory/constitution.md` v1.0.0. Mark each gate PASS / FAIL / N/A with a
one-line justification. Any FAIL blocks the plan until resolved or recorded in Complexity
Tracking below.*

| # | Gate | Status | Notes |
| --- | --- | --- | --- |
| I | **Clean Architecture** | PASS | CSS and React frontend only, no architecture violations. |
| II | **SOLID** | PASS | React hooks for theme preference adhere to single responsibility. |
| III | **Test-First** | PASS | Unit tests will be added for the `useTheme` hook. |
| IV | **Security by Default** | PASS | No security implications for a local UI theme toggle. |
| V | **Performance Budgets** | PASS | < 50ms toggle, measured by UI responsiveness. |
| VI | **Messaging Contracts** | N/A | No cross-boundary messaging involved. |
| VII | **Data Authority & Cache** | PASS | Theme data stored in LocalStorage, no PostgreSQL mutation. |
| VIII | **Zero-Cost & Self-Hosted** | PASS | Vanilla CSS, zero cost, no third party UI frameworks. |
| — | **Stack** | PASS | Uses existing mandated React/CSS stack. |
| — | **Docker-First** | PASS | Does not affect docker deployment or service architecture. |

**Declared performance budget for this feature**: Theme toggle UI update < 50ms (measured via React Profiler).

**Declared security surface for this feature**: None (strictly local UI state).

## Project Structure

### Documentation (this feature)

```text
specs/005-unique-theme/
├── plan.md              # This file
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output
├── quickstart.md        # Phase 1 output
└── tasks.md             # Phase 2 output
```

### Source Code (repository root)

```text
src/internalchat-web/src/
├── index.css                    # CSS Variables defining the dark/light tokens
├── features/layout/ThemeToggle.tsx # UI component to switch theme
└── features/layout/useTheme.ts  # Hook to manage localstorage state and apply data-theme
```

**Structure Decision**: The CSS variables will be managed globally in `index.css`. The UI toggle logic and local storage persistence will be cleanly encapsulated within the `layout` feature folder.

## Complexity Tracking

> **Fill ONLY if Constitution Check has violations that must be justified**

*(No violations)*
