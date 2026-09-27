import type { SummaryStats, MonthlyStats } from '../types';

export function toMonthlyStats(s: SummaryStats): MonthlyStats {
  const income = s.income.collectedAmount;
  const expense = s.expense.collectedAmount;

  const completedAmount = s.income.completedAmount + s.expense.completedAmount;
  const pendingAmount = s.income.pendingAmount + s.expense.pendingAmount;
  const overdueAmount = s.income.overdueAmount + s.expense.overdueAmount;

  const completedCount = s.income.completedCount + s.expense.completedCount;
  const pendingCount = s.income.pendingCount + s.expense.pendingCount;
  const overdueCount = s.income.overdueCount + s.expense.overdueCount;
  const totalCount = s.income.totalCount + s.expense.totalCount;

  const completionRate = totalCount ? Math.round((completedCount / totalCount) * 1000) / 10 : 0;
  const profit = s.netCollected;

  return {
    isPartial: s.isPartial,
    income,
    expense,
    profit,
    completionRate,
    counts: {
      completed: completedCount,
      pending: pendingCount,
      overdue: overdueCount,
      total: totalCount,
    },
    completedAmount,
    pendingAmount,
    overdueAmount,
  };
}
