// @vitest-environment node

import { describe, expect, it } from 'vitest';

import { toManagementReportApiFilters } from './managementReportExportFilters';

describe('toManagementReportApiFilters', () => {
  it('omits UI-only exportDisabled flag from management report API filters', () => {
    expect(toManagementReportApiFilters({
      from: '2026-05-01',
      to: '2026-05-31',
      source: 'Bank',
      clientId: 10,
      exportDisabled: true,
    })).toEqual({
      from: '2026-05-01',
      to: '2026-05-31',
      source: 'Bank',
      clientId: 10,
    });
  });
});
