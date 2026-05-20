import { describe, expect, it } from 'vitest';

import { parseFeatureFlag } from './features';

describe('parseFeatureFlag', () => {
  it('accepts explicit enabled values', () => {
    expect(parseFeatureFlag('true')).toBe(true);
    expect(parseFeatureFlag('1')).toBe(true);
    expect(parseFeatureFlag('enabled')).toBe(true);
  });

  it('accepts explicit disabled values', () => {
    expect(parseFeatureFlag('false', true)).toBe(false);
    expect(parseFeatureFlag('0', true)).toBe(false);
    expect(parseFeatureFlag('disabled', true)).toBe(false);
  });

  it('falls back for empty or unknown values', () => {
    expect(parseFeatureFlag(undefined, true)).toBe(true);
    expect(parseFeatureFlag('later', false)).toBe(false);
  });
});
