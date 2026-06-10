import { formatCurrencySmart } from './formatters';
import { formatReportDate, formatReportDateTime, formatReportDeadlineDate } from './reportDateFormatting';

const ruMonthFormatter = new Intl.DateTimeFormat('ru-RU', {
  month: 'long',
  year: 'numeric',
});

export type ReportYearMonth = `${number}-${string}`;

export function formatCurrency(amount: number) {
  return formatCurrencySmart(amount).full;
}

export function formatYmLabel(ym: string) {
  const [year, month] = ym.split('-').map(Number);
  return ruMonthFormatter.format(new Date(year, (month ?? 1) - 1, 1));
}

export function normalizeYmRange(fromYM: ReportYearMonth, toYM: ReportYearMonth) {
  return fromYM <= toYM ? { fromYM, toYM } : { fromYM: toYM, toYM: fromYM };
}

export function buildTitleSuffix(fromYM: ReportYearMonth, toYM: ReportYearMonth) {
  if (fromYM === toYM) {
    return formatYmLabel(fromYM);
  }

  return `${formatYmLabel(fromYM)} - ${formatYmLabel(toYM)}`;
}

export function monthRangeToDates(fromYM: ReportYearMonth, toYM: ReportYearMonth) {
  const normalized = normalizeYmRange(fromYM, toYM);
  const [fromYear, fromMonth] = normalized.fromYM.split('-').map(Number);
  const [toYear, toMonth] = normalized.toYM.split('-').map(Number);
  const monthDays = new Date(toYear, toMonth, 0).getDate();

  return {
    from: `${fromYear}-${String(fromMonth).padStart(2, '0')}-01`,
    to: `${toYear}-${String(toMonth).padStart(2, '0')}-${String(monthDays).padStart(2, '0')}`,
  };
}

export function formatDate(value: string, timeZoneId?: string | null) {
  return formatReportDate(value, timeZoneId);
}

export function formatDateTime(value: string, timeZoneId?: string | null) {
  return formatReportDateTime(value, timeZoneId);
}

export function formatDeadlineDate(value: string, allDay?: boolean, timeZoneId?: string | null) {
  return formatReportDeadlineDate(value, allDay, timeZoneId);
}

export function formatDeadlineOffset(days: number) {
  if (days < 0) {
    return `${Math.abs(days)} дн. просрочено`;
  }

  if (days === 0) {
    return 'сегодня';
  }

  return `через ${days} дн.`;
}
