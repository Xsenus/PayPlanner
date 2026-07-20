import type { CashFlowReportFilters } from '../../hooks/useCashFlowReport';

export function buildBankReportFilters(filters: CashFlowReportFilters): CashFlowReportFilters {
  return {
    ...filters,
    source: 'Bank',
  };
}
