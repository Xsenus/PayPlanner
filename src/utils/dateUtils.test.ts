// @vitest-environment node

import { describe, expect, it } from 'vitest';

import { todayYMDInTimeZone } from './dateUtils';

describe('dateUtils', () => {
  it('returns today key in the selected profile time zone', () => {
    const instant = new Date('2026-06-04T22:30:00Z');

    expect(todayYMDInTimeZone('Europe/Samara', instant)).toBe('2026-06-05');
    expect(todayYMDInTimeZone('Europe/Moscow', instant)).toBe('2026-06-05');
    expect(todayYMDInTimeZone('America/New_York', instant)).toBe('2026-06-04');
  });
});
