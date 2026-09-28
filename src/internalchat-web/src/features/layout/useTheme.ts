import { useState, useEffect } from 'react';

export type Theme = 'light' | 'dark' | 'system';

export function useTheme() {
  const [theme, setThemeState] = useState<Theme>(() => {
    const saved = localStorage.getItem('theme_preference') as Theme | null;
    return saved ?? 'system';
  });

  useEffect(() => {
    const applyTheme = (currentTheme: Theme) => {
      const root = document.documentElement;
      if (currentTheme === 'system') {
        let isDark = false;
        // eslint-disable-next-line @typescript-eslint/no-unnecessary-condition
        if (typeof window.matchMedia === 'function') {
          isDark = window.matchMedia('(prefers-color-scheme: dark)').matches;
        }
        if (isDark) {
          root.setAttribute('data-theme', 'dark');
        } else {
          root.removeAttribute('data-theme');
        }
      } else if (currentTheme === 'dark') {
        root.setAttribute('data-theme', 'dark');
      } else {
        root.setAttribute('data-theme', 'light');
      }
    };

    applyTheme(theme);
    localStorage.setItem('theme_preference', theme);

    // Listen for system theme changes if set to system
    // eslint-disable-next-line @typescript-eslint/no-unnecessary-condition
    if (theme === 'system' && window.matchMedia) {
      const mediaQuery = window.matchMedia('(prefers-color-scheme: dark)');
      const handleChange = () => { applyTheme('system'); };
      mediaQuery.addEventListener('change', handleChange);
      return () => { mediaQuery.removeEventListener('change', handleChange); };
    }
    return undefined;
  }, [theme]);

  const setTheme = (newTheme: Theme) => {
    setThemeState(newTheme);
  };

  return { theme, setTheme };
}
