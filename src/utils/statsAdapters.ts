import type { SummaryStats, MonthlyStats } from '../types';

export function toMonthlyStats(s: SummaryStats): MonthlyStats {
  const income = s.income.totalAmount;
  const expense = s.expense.totalAmount;

  const completedAmount = s.netCompleted;
  const pendingAmount = s.income.pendingAmount + s.expense.pendingAmount;
  const overdueAmount = s.income.overdueAmount + s.expense.overdueAmount;

  const completedCount = s.income.completedCount + s.expense.completedCount;
  const pendingCount = s.income.pendingCount + s.expense.pendingCount;
  const overdueCount = s.income.overdueCount + s.expense.overdueCount;
  const totalCount = s.income.totalCount + s.expense.totalCount;

  const completionRate = totalCount ? Math.round((completedCount / totalCount) * 100) : 0;
  const profit = income - expense;

  return {
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
