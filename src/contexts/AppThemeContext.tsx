import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { useAuth } from './AuthContext';
import { apiService } from '../services/api';
import {
  APP_THEME_OPTIONS,
  applyAppTheme,
  normalizeAppThemeKey,
  readCachedAppTheme,
  writeCachedAppTheme,
  type AppThemeKey,
  type AppThemeOption,
} from '../utils/appThemes';

interface AppThemeContextValue {
  themeKey: AppThemeKey;
  themes: AppThemeOption[];
  loading: boolean;
  saving: boolean;
  setGlobalTheme: (themeKey: AppThemeKey) => Promise<void>;
}

const AppThemeContext = createContext<AppThemeContextValue | null>(null);

export function AppThemeProvider({ children }: { children: ReactNode }) {
  const { user } = useAuth();
  const [themeKey, setThemeKey] = useState<AppThemeKey>(() => applyAppTheme(readCachedAppTheme()));
  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);

  const activateTheme = useCallback((value?: string | null) => {
    const normalized = applyAppTheme(value);
    writeCachedAppTheme(normalized);
    setThemeKey(normalized);
    return normalized;
  }, []);

  useEffect(() => {
    if (!user) return;

    let cancelled = false;
    setLoading(true);
    apiService.getAppearanceSettings()
      .then((settings) => {
        if (!cancelled) activateTheme(settings.themeKey);
      })
      .catch(() => {
        // Keep the last server-provided theme when the connection is temporarily unavailable.
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [activateTheme, user]);

  const setGlobalTheme = useCallback(async (nextThemeKey: AppThemeKey) => {
    const normalized = normalizeAppThemeKey(nextThemeKey);
    const previous = themeKey;
    activateTheme(normalized);
    setSaving(true);
    try {
      const settings = await apiService.updateAppearanceSettings(normalized);
      activateTheme(settings.themeKey);
    } catch (error) {
      activateTheme(previous);
      throw error;
    } finally {
      setSaving(false);
    }
  }, [activateTheme, themeKey]);

  const value = useMemo<AppThemeContextValue>(() => ({
    themeKey,
    themes: APP_THEME_OPTIONS,
    loading,
    saving,
    setGlobalTheme,
  }), [loading, saving, setGlobalTheme, themeKey]);

  return <AppThemeContext.Provider value={value}>{children}</AppThemeContext.Provider>;
}

// eslint-disable-next-line react-refresh/only-export-components
export function useAppTheme(): AppThemeContextValue {
  const context = useContext(AppThemeContext);
  if (!context) throw new Error('useAppTheme must be used within AppThemeProvider');
  return context;
}
