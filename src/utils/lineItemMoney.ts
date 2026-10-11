const decimal = (value: number): { integer: bigint; scale: number } => {
  const [mantissa, exponent = '0'] = value.toString().toLowerCase().split('e');
  const parts = mantissa.split('.');
  const scale = (parts[1]?.length ?? 0) - Number(exponent);
  const integer = BigInt(parts.join(''));
  return scale < 0 ? { integer: integer * 10n ** BigInt(-scale), scale: 0 } : { integer, scale };
};

export const lineItemPrecisionError = 'В цене допускается до двух знаков после запятой, в количестве — до четырёх.';

/** Sum rounded line amounts as integer cents so JSON retains the exact document total. */
export function sumLineAmounts(amounts: readonly number[], roundLines: boolean): number {
  return roundLines
    ? amounts.reduce((sum, amount) => sum + Math.round(amount * 100), 0) / 100
    : amounts.reduce((sum, amount) => sum + amount, 0);
}

export function hasStorableLineItemPrecision(quantity: number, price: number): boolean {
  return Number.isFinite(quantity) && Number.isFinite(price)
    && decimal(quantity).scale <= 4 && decimal(price).scale <= 2;
}

/** Multiply the same decimal values sent in JSON and round each line to cents, away from zero. */
export function roundedLineTotal(quantity: number, price: number): number {
  if (!Number.isFinite(quantity) || !Number.isFinite(price)) return Number.NaN;
  const left = decimal(quantity);
  const right = decimal(price);
  const product = left.integer * right.integer;
  const scale = left.scale + right.scale;
  if (scale <= 2) return Number(product * 10n ** BigInt(2 - scale)) / 100;
  const divisor = 10n ** BigInt(scale - 2);
  const absolute = product < 0n ? -product : product;
  const cents = absolute / divisor + (absolute % divisor * 2n >= divisor ? 1n : 0n);
  return Number(product < 0n ? -cents : cents) / 100;
}
