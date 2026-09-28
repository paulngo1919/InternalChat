import React from 'react';
import { useTheme, type Theme } from './useTheme';

export const ThemeToggle: React.FC = () => {
  const { theme, setTheme } = useTheme();

  return (
    <div style={{
      display: 'flex',
      alignItems: 'center',
      gap: '8px',
      padding: '12px 16px',
      background: 'var(--bg-elevated)',
      borderRadius: '8px',
      border: '1px solid var(--border-light)',
      margin: '8px',
      backdropFilter: 'blur(12px)',
      WebkitBackdropFilter: 'blur(12px)',
    }}>
      <label htmlFor="theme-select" style={{ fontSize: '0.875rem', fontWeight: 500 }}>
        Theme:
      </label>
      <select
        id="theme-select"
        value={theme}
        onChange={(e) => { setTheme(e.target.value as Theme); }}
        style={{
          flex: 1,
          padding: '6px 8px',
          background: 'var(--bg-surface)',
          border: '1px solid var(--border-strong)',
          borderRadius: '4px',
          color: 'var(--text-primary)',
          cursor: 'pointer',
          fontSize: '0.875rem',
        }}
      >
        <option value="system">System (Auto)</option>
        <option value="light">Light</option>
        <option value="dark">Dark</option>
      </select>
    </div>
  );
};
