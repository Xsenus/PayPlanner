import { describe, expect, it } from 'vitest';
import { buildBankReportFilters } from './Reports';

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
