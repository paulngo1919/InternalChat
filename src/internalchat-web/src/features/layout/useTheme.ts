import { useState, useEffect } from 'react';

export type Theme = 'light' | 'dark' | 'system';

export function useTheme() {
  const [theme, setThemeState] = useState<Theme>(() => {
    const saved = localStorage.getItem('theme_preference') as Theme | null;
    return saved ?? 'system';
  });

  useEffect(() => {
    const updateMetaThemeColor = (isDark: boolean) => {
      let meta = document.querySelector<HTMLMetaElement>('meta[name="theme-color"]');
      if (!meta) {
        meta = document.createElement('meta');
        meta.name = 'theme-color';
        document.head.appendChild(meta);
      }
      meta.setAttribute('content', isDark ? '#061218' : '#f4f7f6');
    };

    const applyTheme = (currentTheme: Theme) => {
      const root = document.documentElement;
      let isDark = false;
      if (currentTheme === 'system') {
        if (typeof window.matchMedia === 'function') {
          isDark = window.matchMedia('(prefers-color-scheme: dark)').matches;
        }
        if (isDark) {
          root.setAttribute('data-theme', 'dark');
        } else {
          root.removeAttribute('data-theme');
        }
      } else if (currentTheme === 'dark') {
        isDark = true;
        root.setAttribute('data-theme', 'dark');
      } else {
        isDark = false;
        root.setAttribute('data-theme', 'light');
      }
      updateMetaThemeColor(isDark);
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
