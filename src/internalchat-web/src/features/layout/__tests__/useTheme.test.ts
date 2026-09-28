/* eslint-disable @typescript-eslint/unbound-method */
import { renderHook, act } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useTheme } from '../useTheme';

describe('useTheme', () => {
  let originalMatchMedia: typeof window.matchMedia;
  let mediaQueryListeners: Record<string, ((e: Event) => void) | undefined> = {};
  let mediaQueryMatches = false;

  beforeEach(() => {
    // Clear localStorage
    localStorage.clear();

    // Mock document.documentElement.setAttribute / removeAttribute
    document.documentElement.setAttribute = vi.fn();
    document.documentElement.removeAttribute = vi.fn();

    // Mock matchMedia
    originalMatchMedia = window.matchMedia;
    window.matchMedia = vi.fn().mockImplementation((query: string) => {
      return {
        matches: mediaQueryMatches,
        media: query,
        onchange: null,
        addEventListener: vi.fn((event: string, callback: (e: Event) => void) => {
          mediaQueryListeners[event] = callback;
        }),
        removeEventListener: vi.fn((event: string) => {
          mediaQueryListeners[event] = undefined;
        }),
        dispatchEvent: vi.fn(),
      };
    });
  });

  afterEach(() => {
    window.matchMedia = originalMatchMedia;
    mediaQueryListeners = {};
    vi.clearAllMocks();
  });

  it('should default to system theme if no preference is saved', () => {
    mediaQueryMatches = true; // System is dark
    const { result } = renderHook(() => useTheme());

    expect(result.current.theme).toBe('system');
    expect(localStorage.getItem('theme_preference')).toBe('system');
    expect(document.documentElement.setAttribute).toHaveBeenCalledWith('data-theme', 'dark');
  });

  it('should apply light theme when system is light', () => {
    mediaQueryMatches = false; // System is light
    const { result } = renderHook(() => useTheme());

    expect(result.current.theme).toBe('system');
    expect(document.documentElement.removeAttribute).toHaveBeenCalledWith('data-theme');
  });

  it('should use saved preference from localStorage', () => {
    localStorage.setItem('theme_preference', 'light');
    const { result } = renderHook(() => useTheme());

    expect(result.current.theme).toBe('light');
    expect(document.documentElement.setAttribute).toHaveBeenCalledWith('data-theme', 'light');
  });

  it('should update theme when setTheme is called', () => {
    const { result } = renderHook(() => useTheme());

    act(() => {
      result.current.setTheme('dark');
    });

    expect(result.current.theme).toBe('dark');
    expect(localStorage.getItem('theme_preference')).toBe('dark');
    expect(document.documentElement.setAttribute).toHaveBeenCalledWith('data-theme', 'dark');
  });
});
