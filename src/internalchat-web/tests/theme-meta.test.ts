import { act, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useTheme } from '../src/features/layout/useTheme';

describe('useTheme dynamic meta theme-color sync', () => {
  let metaTag: HTMLMetaElement;

  beforeEach(() => {
    localStorage.clear();
    document.head.innerHTML = '';
    metaTag = document.createElement('meta');
    metaTag.name = 'theme-color';
    metaTag.content = '#000000';
    document.head.appendChild(metaTag);

    document.documentElement.setAttribute = vi.fn();
    document.documentElement.removeAttribute = vi.fn();

    window.matchMedia = vi.fn().mockImplementation((query: string) => ({
      matches: false,
      media: query,
      onchange: null,
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
      dispatchEvent: vi.fn(),
    }));
  });

  afterEach(() => {
    vi.clearAllMocks();
  });

  it('updates meta theme-color tag to dark color when dark theme is active', () => {
    const { result } = renderHook(() => useTheme());

    act(() => {
      result.current.setTheme('dark');
    });

    const meta = document.querySelector<HTMLMetaElement>('meta[name="theme-color"]');
    expect(meta?.getAttribute('content')).toBe('#061218');
  });

  it('updates meta theme-color tag to light color when light theme is active', () => {
    const { result } = renderHook(() => useTheme());

    act(() => {
      result.current.setTheme('light');
    });

    const meta = document.querySelector<HTMLMetaElement>('meta[name="theme-color"]');
    expect(meta?.getAttribute('content')).toBe('#f4f7f6');
  });
});
