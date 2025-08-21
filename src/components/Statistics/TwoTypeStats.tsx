import { useState } from 'react';
import type { PeriodKey } from '../../types';
import { TypeStatsBlock } from './TypeStatsBlock';
import { TrendingUp, TrendingDown } from 'lucide-react';

type StatusFilter = 'All' | 'Pending' | 'Completed' | 'Overdue';

type Props = {
  clientId?: number;
  caseId?: number;
  from?: string;
  to?: string;
  period?: PeriodKey;
  statusFilter?: StatusFilter;
  search?: string;
  reloadToken?: number;
  className?: string;
};

function StatsGroup({
  title,
  Icon,
  tone,
  children,
}: {
  title: string;
  Icon: React.ComponentType<{ className?: string }>;
  tone: 'income' | 'expense';
  children: React.ReactNode;
}) {
  const toneClasses =
    tone === 'income' ? 'text-emerald-700 bg-emerald-50/60' : 'text-rose-700 bg-rose-50/60';
  return (
    <section className="rounded-2xl border border-gray-200 bg-white/70 backdrop-blur p-3 sm:p-4 shadow-sm">
      <div className="flex items-center gap-2 mb-3 sm:mb-4">
        <div
          className={`inline-flex items-center justify-center w-8 h-8 rounded-xl ${toneClasses}`}>
          <Icon className="w-4 h-4" />
        </div>
        <h3 className="text-base sm:text-lg font-semibold text-gray-900">{title}</h3>
      </div>
      {children}
    </section>
  );
}

export function TwoTypeStats({
  clientId,
  caseId,
  from,
  to,
  period,
  statusFilter,
  search,
  reloadToken,
  className = '',
}: Props) {
  const [tab, setTab] = useState<'Income' | 'Expense'>('Income');

  return (
    <div className={`w-full ${className}`}>
      <div className="sm:hidden mb-3">
        <div
          role="tablist"
          aria-label="Статистика по типам"
          className="grid grid-cols-2 p-1 rounded-xl border bg-white shadow-sm">
          <button
            type="button"
            role="tab"
            aria-selected={tab === 'Income'}
            onClick={() => setTab('Income')}
            className={[
              'px-3 py-2 rounded-lg text-sm font-medium transition-colors',
              tab === 'Income'
                ? 'bg-emerald-50 text-emerald-700 shadow-inner'
                : 'text-gray-600 hover:bg-gray-50',
            ].join(' ')}>
            Доходы
          </button>
          <button
            type="button"
            role="tab"
            aria-selected={tab === 'Expense'}
            onClick={() => setTab('Expense')}
            className={[
              'px-3 py-2 rounded-lg text-sm font-medium transition-colors',
              tab === 'Expense'
                ? 'bg-rose-50 text-rose-700 shadow-inner'
                : 'text-gray-600 hover:bg-gray-50',
            ].join(' ')}>
            Расходы
          </button>
        </div>
      </div>

      <div className="sm:hidden space-y-4">
        {tab === 'Income' ? (
          <StatsGroup title="Доходы" Icon={TrendingUp} tone="income">
            <TypeStatsBlock
              kind="Income"
              clientId={clientId}
              caseId={caseId}
              from={from}
              to={to}
              period={period}
              statusFilter={statusFilter}
              search={search}
              reloadToken={reloadToken}
            />
          </StatsGroup>
        ) : (
          <StatsGroup title="Расходы" Icon={TrendingDown} tone="expense">
            <TypeStatsBlock
              kind="Expense"
              clientId={clientId}
              caseId={caseId}
              from={from}
              to={to}
              period={period}
              statusFilter={statusFilter}
              search={search}
              reloadToken={reloadToken}
            />
          </StatsGroup>
        )}
      </div>

      <div className="hidden sm:flex sm:flex-col gap-4">
        <StatsGroup title="Доходы" Icon={TrendingUp} tone="income">
          <TypeStatsBlock
            kind="Income"
            clientId={clientId}
            caseId={caseId}
            from={from}
            to={to}
            period={period}
            statusFilter={statusFilter}
            search={search}
            reloadToken={reloadToken}
          />
        </StatsGroup>
        <StatsGroup title="Расходы" Icon={TrendingDown} tone="expense">
          <TypeStatsBlock
            kind="Expense"
            clientId={clientId}
            caseId={caseId}
            from={from}
            to={to}
            period={period}
            statusFilter={statusFilter}
            search={search}
            reloadToken={reloadToken}
          />
        </StatsGroup>
      </div>
    </div>
  );
}
