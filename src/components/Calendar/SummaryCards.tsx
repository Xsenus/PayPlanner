import {
  TrendingUp,
  TrendingDown,
  Check,
  Loader,
  AlertTriangle,
  Wallet,
  Banknote,
} from 'lucide-react';
import type { MonthlyStats } from '../../types';
import { StatCardItem, StatsCards } from '../Statistics/StatCardItem';
import { formatCurrencySmart } from '../../utils/formatters';

interface SummaryCardsProps {
  stats: MonthlyStats | null;
}

export function SummaryCards({ stats }: SummaryCardsProps) {
  const income = stats?.income ?? 0;
  const expense = stats?.expense ?? 0;
  const completed = stats?.completedAmount ?? 0;
  const pending = stats?.pendingAmount ?? 0;
  const overdue = stats?.overdueAmount ?? 0;
  const overall = income + pending + overdue;
  const remaining = pending + overdue;

  const f = (n: number) => formatCurrencySmart(n);
  const items: StatCardItem[] = [
    {
      title: 'Доходы',
      ...f(income),
      icon: TrendingUp,
      color: 'text-emerald-600',
      bg: 'bg-emerald-50',
    },
    { title: 'Расходы', ...f(expense), icon: TrendingDown, color: 'text-red-600', bg: 'bg-red-50' },
    {
      title: 'Итог',
      ...f(completed),
      icon: Check,
      color: completed >= 0 ? 'text-emerald-600' : 'text-red-600',
      bg: completed >= 0 ? 'bg-emerald-50' : 'bg-red-50',
    },
    {
      title: 'Ожидается',
      ...f(pending),
      icon: Loader,
      color: 'text-orange-600',
      bg: 'bg-orange-50',
    },
    {
      title: 'Просрочено',
      ...f(overdue),
      icon: AlertTriangle,
      color: 'text-purple-700',
      bg: 'bg-purple-50',
    },
    { title: 'Общая сумма', ...f(overall), icon: Wallet, color: 'text-sky-700', bg: 'bg-sky-50' },
    {
      title: 'Остаток долга',
      ...f(remaining),
      icon: Banknote,
      color: 'text-indigo-700',
      bg: 'bg-indigo-50',
    },
  ].map((x) => ({ ...x, value: x.short, titleAttr: x.full }));

  return <StatsCards items={items} loading={!stats} lgCols={7} skeletonCount={7} />;
}
