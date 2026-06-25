import { describe, expect, it } from 'vitest';
import { buildBankReportFilters } from './Reports';
import { buildVisibleReportGroups } from './reportAccess';

describe('Reports bank filters', () => {
  it('keeps selected money filters and forces bank source', () => {
    const filters = buildBankReportFilters({
      from: '2026-05-01',
      to: '2026-05-31',
      source: 'Cash',
      accountName: 'Основной счет',
      counterpartyName: 'ООО Ромашка',
      clientId: 10,
      caseId: 20,
      contractId: 30,
      incomeTypeId: 40,
    });

    expect(filters).toEqual({
      from: '2026-05-01',
      to: '2026-05-31',
      source: 'Bank',
      accountName: 'Основной счет',
      counterpartyName: 'ООО Ромашка',
      clientId: 10,
      caseId: 20,
      contractId: 30,
      incomeTypeId: 40,
    });
  });
});

describe('Reports access matrix', () => {
  it('shows report groups only for sections available to the role', () => {
    expect(buildVisibleReportGroups({
      payments: false,
      tasks: true,
      cases: false,
      accounts: false,
      acts: false,
      contracts: false,
    })).toEqual(['tasks']);

    expect(buildVisibleReportGroups({
      payments: false,
      tasks: false,
      cases: true,
      accounts: false,
      acts: false,
      contracts: false,
    })).toEqual(['legal']);

    expect(buildVisibleReportGroups({
      payments: false,
      tasks: false,
      cases: false,
      accounts: true,
      acts: false,
      contracts: false,
    })).toEqual(['analytics']);
  });

  it('hides every inner report group when related sections are unavailable', () => {
    expect(buildVisibleReportGroups({
      payments: false,
      tasks: false,
      cases: false,
      accounts: false,
      acts: false,
      contracts: false,
    })).toEqual([]);
  });
});
