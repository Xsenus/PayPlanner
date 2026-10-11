import { describe, expect, it } from 'vitest';
import { hasStorableLineItemPrecision, roundedLineTotal, sumLineAmounts } from './lineItemMoney';

describe('roundedLineTotal', () => {
  it('sums rounded lines without binary fractions in the JSON payload while preserving legacy arithmetic', () => {
    expect(sumLineAmounts([0.1, 0.2], true)).toBe(0.3);
    expect(JSON.stringify({ amount: sumLineAmounts([0.1, 0.2], true) })).toBe('{"amount":0.3}');
    expect(sumLineAmounts([10.08, 15.11, -0.02], true)).toBe(25.17);
    expect(sumLineAmounts([0.1, 0.2], false)).toBe(0.1 + 0.2);
  });
  it.each([
    [1.0075, 10, true], [1.5, 0.01, true], [0.0001, 0.29, true], [1, 10.075, false],
    [1.50001, 10, false], [1e-5, 1, false], [1, 1e-3, false], [Number.NaN, 1, false],
  ])('accepts only quantities and prices that retain their decimal values in storage: %s × %s', (quantity, price, expected) => {
    expect(hasStorableLineItemPrecision(Number(quantity), Number(price))).toBe(expected);
  });
  it.each([
    [1.5, 0.01, 0.02], [2.5, 0.01, 0.03], [4.015, 1, 4.02], [10.075, 1, 10.08],
    [0.1, 0.2, 0.02], [1e-7, 1e7, 1], [-1.5, 0.01, -0.02], [999999999.99, 1, 999999999.99],
  ])('rounds %s × %s to %s using decimal arithmetic', (quantity, price, expected) => {
    expect(roundedLineTotal(quantity, price)).toBe(expected);
  });
  it('keeps invalid inputs invalid', () => {
    expect(roundedLineTotal(Number.NaN, 1)).toBeNaN();
    expect(roundedLineTotal(1, Number.POSITIVE_INFINITY)).toBeNaN();
  });
});
