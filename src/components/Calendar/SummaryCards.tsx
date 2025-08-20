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

interface SummaryCardsProps {
  stats: MonthlyStats | null;
}

export function SummaryCards({ stats }: SummaryCardsProps) {
  if (!stats) {
    return (
      <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-7 gap-3 sm:gap-6 mb-6 sm:mb-8">
        {[...Array(7)].map((_, i) => (
          <div key={i} className="bg-white rounded-xl p-4 sm:p-6 shadow-sm animate-pulse">
            <div className="h-4 bg-gray-200 rounded w-1/2 mb-3"></div>
            <div className="h-7 bg-gray-200 rounded w-3/4"></div>
          </div>
        ))}
      </div>
    );
  }

  const formatCurrency = (amount: number) =>
    new Intl.NumberFormat('ru-RU', {
      style: 'currency',
      currency: 'RUB',
      minimumFractionDigits: 0,
      maximumFractionDigits: 0,
    }).format(amount);

  const overallAmount =
    (stats.income ?? 0) + (stats.pendingAmount ?? 0) + (stats.overdueAmount ?? 0);
  const remainingDebt = (stats.pendingAmount ?? 0) + (stats.overdueAmount ?? 0);

  const cards = [
    {
      title: 'Доходы',
      value: formatCurrency(stats.income),
      icon: TrendingUp,
      color: 'text-emerald-600',
      bg: 'bg-emerald-50',
    },
    {
      title: 'Расходы',
      value: formatCurrency(stats.expense),
      icon: TrendingDown,
      color: 'text-red-600',
      bg: 'bg-red-50',
    },
    {
      title: 'Итог',
      value: formatCurrency(stats.completedAmount ?? 0),
      icon: Check,
      color: (stats.completedAmount ?? 0) >= 0 ? 'text-emerald-600' : 'text-red-600',
      bg: (stats.completedAmount ?? 0) >= 0 ? 'bg-emerald-50' : 'bg-red-50',
    },
    {
      title: 'Ожидается',
      value: formatCurrency(stats.pendingAmount ?? 0),
      icon: Loader,
      color: 'text-orange-600',
      bg: 'bg-orange-50',
    },
    {
      title: 'Просрочено',
      value: formatCurrency(stats.overdueAmount ?? 0),
      icon: AlertTriangle,
      color: 'text-purple-700',
      bg: 'bg-purple-50',
    },
    {
      title: 'Общая сумма',
      value: formatCurrency(overallAmount),
      icon: Wallet,
      color: 'text-sky-700',
      bg: 'bg-sky-50',
    },
    {
      title: 'Остаток долга',
      value: formatCurrency(remainingDebt),
      icon: Banknote,
      color: 'text-indigo-700',
      bg: 'bg-indigo-50',
    },
  ];

  return (
    <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-7 gap-3 sm:gap-6 mb-6 sm:mb-8">
      {cards.map((c, i) => {
        const Icon = c.icon;
        return (
          <div key={i} className="bg-white rounded-xl p-4 sm:p-5 shadow-sm border border-gray-100">
            <div className="flex items-center justify-between lg:flex-col lg:items-center lg:text-center h-full">
              <div className="flex items-center gap-3 min-w-0 lg:flex-col lg:gap-2 lg:w-full lg:items-center">
                <span className={`p-2 rounded-lg ${c.bg} lg:mb-1`}>
                  <Icon className={`h-5 w-5 ${c.color} lg:h-6 lg:w-6`} />
                </span>
                <span className="text-sm font-medium text-gray-600 truncate lg:overflow-visible lg:whitespace-normal lg:text-clip lg:w-full">
                  {c.title}
                </span>
              </div>

              <span
                className={[
                  'ml-3 text-base font-bold',
                  'lg:ml-0 lg:mt-3 lg:text-2xl',
                  c.color,
                ].join(' ')}>
                {c.value}
              </span>
            </div>
          </div>
        );
      })}
    </div>
  );
}
