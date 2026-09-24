import { describe, expect, it } from 'vitest';
import { courtAmountToInput, hasInvalidCourtAmounts, parseCourtAmountInput } from './courtAmounts';

describe('court amounts', () => {
  it('keeps an unspecified amount separate from zero', () => {
    expect(courtAmountToInput(null)).toBe('');
    expect(courtAmountToInput(0)).toBe('0');
    expect(parseCourtAmountInput('  ')).toBeNull();
    expect(parseCourtAmountInput('0')).toBe(0);
  });

  it('accepts rubles and kopecks with a dot or comma', () => {
    expect(parseCourtAmountInput(' 12345,67 ')).toBe(12345.67);
    expect(parseCourtAmountInput('999999999999.99')).toBe(999999999999.99);
  });

  it.each(['-1', '1.234', '1,2,3', '1e3', '1000000000000', 'Infinity'])('rejects invalid value %s', (value) => {
    expect(hasInvalidCourtAmounts(value)).toBe(true);
  });
});
