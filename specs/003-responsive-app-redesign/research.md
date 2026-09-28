# Research & Decisions: Responsive App Redesign

## Decisions

### 1. Responsive Approach
**Decision**: Use Mobile-First CSS Media Queries in `index.css`.
**Rationale**: Mobile-first approach ensures the lightest styles are loaded by default on mobile devices. Vanilla CSS allows us to maintain the strict performance budget (<300KB bundle size) without introducing heavy UI frameworks or styling libraries.
**Alternatives considered**: TailwindCSS (rejected due to constitution constraint of not adding new major dependencies unless necessary, and user requirement to use Vanilla CSS).

### 2. Layout Structure
**Decision**: CSS Grid for Desktop/Tablet main layout, Flexbox for single-column Mobile view.
**Rationale**: CSS Grid is optimal for a fixed 2-column sidebar/main layout on larger screens. Flexbox provides smooth vertical stacking and navigation transitions for mobile.
**Alternatives considered**: Using only Flexbox (rejected because CSS Grid provides cleaner column sizing for the sidebar layout).

### 3. Touch Targets
**Decision**: Apply `min-height: 44px` and `min-width: 44px` padding to all interactive elements on mobile/tablet breakpoints.
**Rationale**: Meets standard mobile accessibility guidelines for tap targets.
**Alternatives considered**: None, this is a standard requirement.
