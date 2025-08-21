export function formatCurrencySmart(
  value: number | null | undefined,
  opts?: {
    locale?: string;
    currency?: string;
    alwaysCents?: boolean;
    compactFrom?: number;
  },
) {
  const v = Number(value ?? 0);
  const locale = opts?.locale ?? 'ru-RU';
  const currency = opts?.currency ?? 'RUB';

  const cents = Math.abs(Math.round(v * 100) % 100);
  const hasCents = cents !== 0;
  const frac = opts?.alwaysCents ? 2 : hasCents ? 2 : 0;

  const full = new Intl.NumberFormat(locale, {
    style: 'currency',
    currency,
    minimumFractionDigits: frac,
    maximumFractionDigits: frac,
  }).format(v);

  const abs = Math.abs(v);
  const useCompact = abs >= (opts?.compactFrom ?? 100_000_000);
  const short = useCompact
    ? new Intl.NumberFormat(locale, {
        style: 'currency',
        currency,
        notation: 'compact',
        compactDisplay: 'short',
        minimumFractionDigits: 0,
        maximumFractionDigits: abs >= 1_000_000_000 ? 1 : 1,
      }).format(v)
    : full;

  return { short, full };
}
