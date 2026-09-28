# Quickstart & Validation: Responsive App Redesign

## Prerequisites
- Frontend development environment running (`npm run dev` in `src/internalchat-web`).

## Validation Scenarios

### 1. Desktop Layout Validation
- **Setup**: Open the application in Chrome.
- **Action**: Maximize the window (width > 1024px).
- **Expected Outcome**: Sidebar and Chat View are visible side-by-side. No overlapping elements.

### 2. Tablet Layout Validation
- **Setup**: Open Chrome DevTools, toggle Device Toolbar.
- **Action**: Select "iPad Mini" (768px width).
- **Expected Outcome**: UI adapts. Sidebar can be collapsed/toggled. Text and touch targets remain legible and usable.

### 3. Mobile Layout Validation
- **Setup**: In Chrome DevTools Device Toolbar, select "iPhone 13" (390px width).
- **Action**: Navigate the application.
- **Expected Outcome**: Single-column layout. When clicking a channel, the view completely transitions to the chat view. A "back" button allows returning to the channel list. No horizontal scrolling is possible.

### 4. Performance Validation
- **Setup**: Open Chrome DevTools -> Lighthouse.
- **Action**: Run a Lighthouse Audit for Mobile device.
- **Expected Outcome**: Accessibility and Best Practices score > 95. CLS score is < 0.1.
