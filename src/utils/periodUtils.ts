import { formatLocalYMD, toRuDate } from './dateUtils';

export type PeriodPreset =
  | 'today'
  | 'yesterday'
  | 'current_week'
  | 'previous_week'
  | 'current_month'
  | 'previous_month'
  | 'current_quarter'
  | 'previous_quarter'
  | 'current_year'
  | 'previous_year'
  | 'year_to_date';

export type PeriodFilter =
  | { mode: 'preset'; preset: PeriodPreset }
  | { mode: 'custom'; from?: string; to?: string };

export const PERIOD_PRESET_LABELS: Record<PeriodPreset, string> = {
  today: 'Сегодня',
  yesterday: 'Вчера',
  current_week: 'Текущая неделя',
  previous_week: 'Предыдущая неделя',
  current_month: 'Текущий месяц',
  previous_month: 'Предыдущий месяц',
  current_quarter: 'Текущий квартал',
  previous_quarter: 'Предыдущий квартал',
  current_year: 'Текущий год',
  previous_year: 'Предыдущий год',
  year_to_date: 'С начала года',
};

export const PERIOD_PRESETS: PeriodPreset[] = [
  'today',
  'yesterday',
  'current_week',
  'previous_week',
  'current_month',
  'previous_month',
  'current_quarter',
  'previous_quarter',
  'current_year',
  'previous_year',
  'year_to_date',
];

function startOfWeek(date: Date): Date {
  const day = date.getDay();
  const diff = (day + 6) % 7;
  return new Date(date.getFullYear(), date.getMonth(), date.getDate() - diff);
}

function endOfWeek(date: Date): Date {
  const start = startOfWeek(date);
  return new Date(start.getFullYear(), start.getMonth(), start.getDate() + 6);
}

function startOfMonth(date: Date): Date {
  return new Date(date.getFullYear(), date.getMonth(), 1);
}

function endOfMonth(date: Date): Date {
  return new Date(date.getFullYear(), date.getMonth() + 1, 0);
}

function startOfQuarter(date: Date): Date {
  const quarterStartMonth = Math.floor(date.getMonth() / 3) * 3;
  return new Date(date.getFullYear(), quarterStartMonth, 1);
}

function endOfQuarter(date: Date): Date {
  const start = startOfQuarter(date);
  return new Date(start.getFullYear(), start.getMonth() + 3, 0);
}

function startOfYear(date: Date): Date {
  return new Date(date.getFullYear(), 0, 1);
}

function endOfYear(date: Date): Date {
  return new Date(date.getFullYear(), 11, 31);
}

function addDays(date: Date, days: number): Date {
  return new Date(date.getFullYear(), date.getMonth(), date.getDate() + days);
}

function addMonths(date: Date, months: number): Date {
  return new Date(date.getFullYear(), date.getMonth() + months, 1);
}

function addYears(date: Date, years: number): Date {
  return new Date(date.getFullYear() + years, date.getMonth(), date.getDate());
}

export function resolvePreset(preset: PeriodPreset, now: Date = new Date()): { from: string; to: string } {
  switch (preset) {
    case 'today': {
      const day = formatLocalYMD(now);
      return { from: day, to: day };
    }
    case 'yesterday': {
      const day = formatLocalYMD(addDays(now, -1));
      return { from: day, to: day };
    }
    case 'current_week':
      return { from: formatLocalYMD(startOfWeek(now)), to: formatLocalYMD(endOfWeek(now)) };
    case 'previous_week': {
      const previous = addDays(startOfWeek(now), -7);
      return { from: formatLocalYMD(previous), to: formatLocalYMD(endOfWeek(previous)) };
    }
    case 'current_month':
      return { from: formatLocalYMD(startOfMonth(now)), to: formatLocalYMD(endOfMonth(now)) };
    case 'previous_month': {
      const previous = addMonths(now, -1);
      return { from: formatLocalYMD(startOfMonth(previous)), to: formatLocalYMD(endOfMonth(previous)) };
    }
    case 'current_quarter':
      return { from: formatLocalYMD(startOfQuarter(now)), to: formatLocalYMD(endOfQuarter(now)) };
    case 'previous_quarter': {
      const previous = addMonths(startOfQuarter(now), -3);
      return { from: formatLocalYMD(startOfQuarter(previous)), to: formatLocalYMD(endOfQuarter(previous)) };
    }
    case 'current_year':
      return { from: formatLocalYMD(startOfYear(now)), to: formatLocalYMD(endOfYear(now)) };
    case 'previous_year': {
      const previous = addYears(now, -1);
      return { from: formatLocalYMD(startOfYear(previous)), to: formatLocalYMD(endOfYear(previous)) };
    }
    case 'year_to_date':
      return { from: formatLocalYMD(startOfYear(now)), to: formatLocalYMD(now) };
    default: {
      const day = formatLocalYMD(now);
      return { from: day, to: day };
    }
  }
}

export function resolvePeriod(period: PeriodFilter, now: Date = new Date()): { from?: string; to?: string } {
  if (period.mode === 'preset') {
    return resolvePreset(period.preset, now);
  }
  return { from: period.from || undefined, to: period.to || undefined };
}

export function formatPeriodLabel(
  period: PeriodFilter,
  now: Date = new Date(),
  emptyLabel = 'Период не задан',
): string {
  const range = resolvePeriod(period, now);
  if (!range.from && !range.to) return emptyLabel;
  if (range.from && !range.to) return `с ${toRuDate(range.from)}`;
  if (!range.from && range.to) return `по ${toRuDate(range.to)}`;
  return `${toRuDate(range.from)} — ${toRuDate(range.to)}`;
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

export function normalizeStoredPeriod(raw: unknown, fallback: PeriodFilter): PeriodFilter {
  if (isObject(raw)) {
    if (raw.mode === 'preset' && typeof raw.preset === 'string') {
      const preset = raw.preset as PeriodPreset;
      if (PERIOD_PRESETS.includes(preset)) {
        return { mode: 'preset', preset };
      }
    }

    const from = typeof raw.from === 'string' ? raw.from : undefined;
    const to = typeof raw.to === 'string' ? raw.to : undefined;
    if (from || to) {
      return { mode: 'custom', from, to };
    }
  }

  return fallback;
}
