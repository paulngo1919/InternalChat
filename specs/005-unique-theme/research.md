# Research: Unique Theme and Dark/Light Mode

## CSS Variables for Theme Toggling

- **Decision**: Use Vanilla CSS custom properties (`--color-primary`, `--bg-surface`) attached to `[data-theme="dark"]` and `[data-theme="light"]` on the `:root` element.
- **Rationale**: Meets the zero-cost and vanilla CSS requirements while providing instant, glitch-free theme toggling with zero API overhead. 
- **Alternatives considered**: Tailwind CSS or CSS-in-JS (rejected due to adding third-party UI dependencies).

## Color Palette Generation

- **Decision**: Use an HSL-based color palette with glassmorphism effects (e.g., translucent backgrounds with backdrop-filter) to distinguish from flat, standard AI generated templates.
- **Rationale**: HSL allows easy scaling of lightness for dark/light modes. Glassmorphism provides a modern, premium feel.

## System Preference Detection

- **Decision**: Use `window.matchMedia('(prefers-color-scheme: dark)')` to detect the OS-level theme preference.
- **Rationale**: Standard browser API for respecting user's global preferences.
