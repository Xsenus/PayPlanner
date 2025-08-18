import { TrendingUp, TrendingDown, Check, Loader, AlertTriangle } from 'lucide-react';
import type { MonthlyStats } from '../../types';

interface SummaryCardsProps {
  stats: MonthlyStats | null;
}

export function SummaryCards({ stats }: SummaryCardsProps) {
  if (!stats) {
    return (
      <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-3 sm:gap-6 mb-6 sm:mb-8">
        {[...Array(4)].map((_, i) => (
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
      title: 'Выполнено',
      value: `${stats.profit ?? 0}`,
      icon: Check,
      color: stats.profit >= 0 ? 'text-emerald-600' : 'text-green-600',
      bg: stats.profit >= 0 ? 'bg-emerald-50' : 'bg-green-50',
    },
    {
      title: 'Ожидается',
      value: `${stats.counts?.pending ?? 0}`,
      icon: Loader,
      color: 'text-orange-600',
      bg: 'bg-orange-50',
    },
    {
      title: 'Просрочено',
      value: `${stats.counts?.overdue ?? 0}`,
      icon: AlertTriangle,
      color: 'text-red-600',
      bg: 'bg-red-50',
    },
  ];

  return (
    <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-5 gap-3 sm:gap-6 mb-6 sm:mb-8">
      {cards.map((c, i) => {
        const Icon = c.icon;
        return (
          <div key={i} className="bg-white rounded-xl p-4 sm:p-5 shadow-sm border border-gray-100">
            <div className="flex items-center justify-between md:flex-col md:items-start">
              <div className="flex items-center gap-3 min-w-0">
                <span className={`p-2 rounded-lg ${c.bg}`}>
                  <Icon className={`h-5 w-5 ${c.color}`} />
                </span>
                <span className="text-sm font-medium text-gray-600 truncate">{c.title}</span>
              </div>

              <span
                className={['ml-3 text-base font-bold', 'md:ml-0 md:mt-2 md:text-xl', c.color].join(
                  ' ',
                )}>
                {c.value}
              </span>
            </div>
          </div>
        );
      })}
    </div>
  );
}
