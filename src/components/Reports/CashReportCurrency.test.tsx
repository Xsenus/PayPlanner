import { render, screen, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { CashReportSection } from './Reports';
import type { CashBookSummary } from '../../types';

const report: CashBookSummary = {
  from: '2026-05-01', to: '2026-05-03', accountName: 'Касса', currencyStatus: 'single', currency: 'USD',
  openingBalance: 100, income: 0, expense: 20, closingBalance: 80, incomingCount: 0, outgoingCount: 1,
  days: [{ date: '2026-05-02', openingBalance: 100, income: 0, expense: 20, closingBalance: 80, incomingCount: 0, outgoingCount: 1 }],
};

describe('cash report currency', () => {
  it('shows the confirmed currency in metrics and daily balances', () => {
    const { container } = render(<CashReportSection report={report} loading={false} error={null} onReload={vi.fn()} canExportPaymentReports />);
    expect(within(container).getAllByText('100,00 USD').length).toBeGreaterThan(0);
    expect(within(container).getAllByText('80,00 USD').length).toBeGreaterThan(0);
    expect(screen.queryByText('80,00 ₽')).toBeNull();
  });

  it('does not infer currency from an old response', () => {
    render(<CashReportSection report={{ ...report, currencyStatus: undefined, currency: undefined }} loading={false} error={null} onReload={vi.fn()} canExportPaymentReports />);
    expect(screen.getAllByText('—').length).toBeGreaterThan(0);
    expect(screen.queryByText('80,00 ₽')).toBeNull();
  });
});
