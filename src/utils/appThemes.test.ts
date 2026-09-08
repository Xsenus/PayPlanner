import { afterEach, describe, expect, it } from 'vitest';
import {
  APP_THEME_OPTIONS,
  APP_THEME_STORAGE_KEY,
  applyAppTheme,
  normalizeAppThemeKey,
  readCachedAppTheme,
  writeCachedAppTheme,
} from './appThemes';

describe('appThemes', () => {
  afterEach(() => {
    document.documentElement.removeAttribute('data-payplanner-theme');
    document.documentElement.style.removeProperty('color-scheme');
    localStorage.clear();
  });

  it('provides eight curated themes including light and dark variants', () => {
    expect(APP_THEME_OPTIONS).toHaveLength(8);
    expect(APP_THEME_OPTIONS.some((theme) => !theme.isDark)).toBe(true);
    expect(APP_THEME_OPTIONS.some((theme) => theme.isDark)).toBe(true);
  });

  it('falls back to the standard theme for unknown values', () => {
    expect(normalizeAppThemeKey('not-a-theme')).toBe('standard');
    expect(normalizeAppThemeKey(null)).toBe('standard');
  });

  it('applies the selected theme and matching browser color scheme', () => {
    applyAppTheme('graphite');

    expect(document.documentElement.dataset.payplannerTheme).toBe('graphite');
    expect(document.documentElement.style.colorScheme).toBe('dark');
  });

  it('caches only a normalized global theme key', () => {
    writeCachedAppTheme('ocean');
    expect(localStorage.getItem(APP_THEME_STORAGE_KEY)).toBe('ocean');
    expect(readCachedAppTheme()).toBe('ocean');
  });
});
