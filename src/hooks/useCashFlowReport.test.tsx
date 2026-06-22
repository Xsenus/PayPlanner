import { render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import type { CashFlowReport } from '../types';
import { apiService } from '../services/api';
import { useCashFlowReport } from './useCashFlowReport';

const { getCashFlowReportMock } = vi.hoisted(() => ({
  getCashFlowReportMock: vi.fn(),
}));

vi.mock('../services/api', () => ({
  apiService: {
    getCashFlowReport: getCashFlowReportMock,
  },
}));

const emptyReport: CashFlowReport = {
  from: '2026-05-01',
  to: '2026-05-31',
  source: null,
  accountName: null,
  openingBalance: 0,
  income: 0,
  expense: 0,
  netCashFlow: 0,
  closingBalance: 0,
  incomeCount: 0,
  expenseCount: 0,
  bySource: [],
  byAccount: [],
  days: [],
};

function CashFlowReportProbe({ enabled }: { enabled: boolean }) {
  const { report, loading, error } = useCashFlowReport(
    { from: '2026-05-01', to: '2026-05-31' },
    { enabled },
  );

  return (
    <div>
      <div data-testid="loading">{loading ? 'loading' : 'ready'}</div>
      <div data-testid="error">{error ?? ''}</div>
      <div data-testid="income">{report?.income ?? 'none'}</div>
    </div>
  );
}

describe('useCashFlowReport', () => {
  beforeEach(() => {
    getCashFlowReportMock.mockReset();
  });

  it('does not request the report when disabled', async () => {
    render(<CashFlowReportProbe enabled={false} />);

    await waitFor(() => {
      expect(screen.getByTestId('loading').textContent).toBe('ready');
    });
    expect(screen.getByTestId('income').textContent).toBe('none');
    expect(apiService.getCashFlowReport).not.toHaveBeenCalled();
  });

  it('loads the report when enabled', async () => {
    getCashFlowReportMock.mockResolvedValue(emptyReport);

    render(<CashFlowReportProbe enabled />);

    await waitFor(() => {
      expect(screen.getByTestId('income').textContent).toBe('0');
    });
    expect(apiService.getCashFlowReport).toHaveBeenCalledWith({
      from: '2026-05-01',
      to: '2026-05-31',
    });
  });
});
