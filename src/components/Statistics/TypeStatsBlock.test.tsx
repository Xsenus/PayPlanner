import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

import { TypeStatsBlock } from './TypeStatsBlock';

vi.mock('../../hooks/useSummaryStats', () => ({
  useSummaryStats: () => ({
    loading: false,
    data: {
      isPartial: true,
      income: {
        completedAmount: 100,
        pendingAmount: 0,
        overdueAmount: 0,
        totalAmount: 100,
      },
      expense: {
        completedAmount: 0,
        pendingAmount: 0,
        overdueAmount: 0,
        totalAmount: 0,
      },
    },
  }),
}));

describe('TypeStatsBlock', () => {
  it('warns when payment totals are based on a limited visibility scope', () => {
    render(<TypeStatsBlock kind="Income" />);

    expect(screen.getByRole('status').textContent).toContain('только доступные вам платежи');
  });
});
