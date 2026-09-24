export function courtAmountToInput(value?: number | null): string {
  return typeof value === 'number' && Number.isFinite(value) ? String(value) : '';
}

export function parseCourtAmountInput(value: string): number | null {
  const normalized = value.trim().replace(',', '.');
  if (!normalized) return null;
  if (!/^\d+(?:\.\d{1,2})?$/.test(normalized)) return Number.NaN;
  const amount = Number(normalized);
  return Number.isFinite(amount) && amount <= 999999999999.99 ? amount : Number.NaN;
}

export function hasInvalidCourtAmounts(...values: string[]): boolean {
  return values.some((value) => Number.isNaN(parseCourtAmountInput(value)));
}
