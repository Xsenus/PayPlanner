import { describe, expect, it } from 'vitest';

import { formatDate } from './Clients';

describe('Clients date formatting', () => {
  it('formats exact client timestamps in the selected profile time zone', () => {
    expect(formatDate('2026-06-04T22:30:00Z', 'Europe/Samara')).toBe('05.06.2026');
    expect(formatDate('2026-06-04T22:30:00Z', 'America/New_York')).toBe('04.06.2026');
  });
});
