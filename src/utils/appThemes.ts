export const APP_THEME_STORAGE_KEY = 'payplanner.global-theme.v1';
export const DEFAULT_APP_THEME_KEY = 'standard' as const;

export const APP_THEME_KEYS = [
  'standard',
  'business',
  'ocean',
  'emerald',
  'warm',
  'violet',
  'graphite',
  'midnight',
] as const;

export type AppThemeKey = (typeof APP_THEME_KEYS)[number];

export interface AppThemeOption {
  key: AppThemeKey;
  name: string;
  description: string;
  primaryColor: string;
  surfaceColor: string;
  backgroundColor: string;
  isDark: boolean;
}

export const APP_THEME_OPTIONS: AppThemeOption[] = [
  {
    key: 'standard',
    name: 'Стандартная',
    description: 'Привычная светлая тема PayPlanner с более чёткими границами и рабочими акцентами.',
    primaryColor: '#2563eb',
    surfaceColor: '#ffffff',
    backgroundColor: '#f1f5f9',
    isDark: false,
  },
  {
    key: 'business',
    name: 'Деловая',
    description: 'Строгая светлая палитра с глубоким синим акцентом и плотной иерархией таблиц.',
    primaryColor: '#1769c2',
    surfaceColor: '#ffffff',
    backgroundColor: '#eaf0f6',
    isDark: false,
  },
  {
    key: 'ocean',
    name: 'Океан',
    description: 'Холодная светлая тема с бирюзово-синими акцентами для длительной работы.',
    primaryColor: '#087f8c',
    surfaceColor: '#fbfeff',
    backgroundColor: '#eaf5f7',
    isDark: false,
  },
  {
    key: 'emerald',
    name: 'Изумрудная',
    description: 'Спокойная светлая тема с зелёными акцентами и хорошо различимыми рабочими зонами.',
    primaryColor: '#0f766e',
    surfaceColor: '#ffffff',
    backgroundColor: '#edf5f1',
    isDark: false,
  },
  {
    key: 'warm',
    name: 'Тёплая',
    description: 'Мягкая кремовая палитра с янтарным акцентом и комфортным контрастом.',
    primaryColor: '#a45116',
    surfaceColor: '#fffdf9',
    backgroundColor: '#f5eee4',
    isDark: false,
  },
  {
    key: 'violet',
    name: 'Фиолетовая',
    description: 'Современная светлая тема с фиолетовым акцентом для аналитики и автоматизаций.',
    primaryColor: '#6d3bc2',
    surfaceColor: '#ffffff',
    backgroundColor: '#f2eff9',
    isDark: false,
  },
  {
    key: 'graphite',
    name: 'Графит',
    description: 'Строгая тёмная тема с мятным акцентом и контрастными таблицами.',
    primaryColor: '#45d7b4',
    surfaceColor: '#1a202a',
    backgroundColor: '#10151d',
    isDark: true,
  },
  {
    key: 'midnight',
    name: 'Полночь',
    description: 'Глубокая тёмно-синяя тема с ярким голубым акцентом для вечерней работы.',
    primaryColor: '#60a5fa',
    surfaceColor: '#111c2f',
    backgroundColor: '#08111f',
    isDark: true,
  },
];

export function normalizeAppThemeKey(value?: string | null): AppThemeKey {
  const normalized = value?.trim().toLowerCase();
  return APP_THEME_KEYS.includes(normalized as AppThemeKey)
    ? normalized as AppThemeKey
    : DEFAULT_APP_THEME_KEY;
}

export function getAppThemeOption(value?: string | null): AppThemeOption {
  const key = normalizeAppThemeKey(value);
  return APP_THEME_OPTIONS.find((theme) => theme.key === key) ?? APP_THEME_OPTIONS[0];
}

export function readCachedAppTheme(): AppThemeKey {
  if (typeof window === 'undefined') return DEFAULT_APP_THEME_KEY;
  try {
    return normalizeAppThemeKey(window.localStorage.getItem(APP_THEME_STORAGE_KEY));
  } catch {
    return DEFAULT_APP_THEME_KEY;
  }
}

export function writeCachedAppTheme(themeKey: AppThemeKey): void {
  if (typeof window === 'undefined') return;
  try {
    window.localStorage.setItem(APP_THEME_STORAGE_KEY, themeKey);
  } catch {
    // The theme remains active for the current session when storage is unavailable.
  }
}

export function applyAppTheme(value?: string | null): AppThemeKey {
  const themeKey = normalizeAppThemeKey(value);
  if (typeof document === 'undefined') return themeKey;

  const theme = getAppThemeOption(themeKey);
  document.documentElement.dataset.payplannerTheme = themeKey;
  document.documentElement.style.colorScheme = theme.isDark ? 'dark' : 'light';
  return themeKey;
}
