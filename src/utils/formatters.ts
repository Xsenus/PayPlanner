export function formatCurrencySmart(
  value: number | null | undefined,
  opts?: { locale?: string; currency?: string },
) {
  const v = Number(value ?? 0);
  const locale = opts?.locale ?? 'ru-RU';
  const currency = opts?.currency ?? 'RUB';

  const full = new Intl.NumberFormat(locale, {
    style: 'currency',
    currency,
    minimumFractionDigits: 0,
    maximumFractionDigits: 0,
  }).format(v);

  const abs = Math.abs(v);
  const useCompact = abs >= 100_000_000;
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
