import { useEffect, useState } from 'react';
import { ChevronDown, TrendingDown, TrendingUp } from 'lucide-react';
import type { PeriodKey, Payment } from '../../types';
import { TypeStatsBlock } from './TypeStatsBlock';

type StatusFilter = 'All' | 'Pending' | 'Completed' | 'Overdue';
type GroupKind = 'Income' | 'Expense';
type CollapsedState = Record<GroupKind, boolean>;

type Props = {
  clientId?: number;
  caseId?: number;
  from?: string;
  to?: string;
  period?: PeriodKey;
  statusFilter?: StatusFilter;
  search?: string;
  reloadToken?: number;
  rawPayments?: Payment[];
  className?: string;
  storageKey?: string;
  onCardSelect?: (payload: {
    kind: 'Income' | 'Expense';
    metric: 'completed' | 'pending' | 'overdue' | 'overall' | 'debt';
    title: string;
  }) => void;
};

function getDefaultCollapsedState(): CollapsedState {
  return {
    Income: false,
    Expense: false,
  };
}

function loadCollapsedState(storageKey?: string): CollapsedState {
  if (!storageKey) {
    return getDefaultCollapsedState();
  }

  try {
    const raw = localStorage.getItem(storageKey);
    if (!raw) {
      return getDefaultCollapsedState();
    }

    const parsed = JSON.parse(raw) as Partial<CollapsedState> | null;
    return {
      Income: parsed?.Income ?? false,
      Expense: parsed?.Expense ?? false,
    };
  } catch {
    return getDefaultCollapsedState();
  }
}

function StatsGroup({
  title,
  Icon,
  tone,
  collapsed,
  onToggle,
  children,
}: {
  title: string;
  Icon: React.ComponentType<{ className?: string }>;
  tone: 'income' | 'expense';
  collapsed: boolean;
  onToggle: () => void;
  children: React.ReactNode;
}) {
  const toneClasses =
    tone === 'income' ? 'bg-emerald-50/60 text-emerald-700' : 'bg-rose-50/60 text-rose-700';

  return (
    <section className="rounded-2xl border border-gray-200 bg-white/70 p-3 shadow-sm backdrop-blur sm:p-4">
      <button
        type="button"
        onClick={onToggle}
        aria-expanded={!collapsed}
        className={`flex w-full items-center justify-between gap-3 text-left ${collapsed ? '' : 'mb-3 sm:mb-4'}`}>
        <span className="flex items-center gap-2">
          <span className={`inline-flex h-8 w-8 items-center justify-center rounded-xl ${toneClasses}`}>
            <Icon className="h-4 w-4" />
          </span>
          <h3 className="text-base font-semibold text-gray-900 sm:text-lg">{title}</h3>
        </span>

        <span className="inline-flex items-center gap-2 rounded-full border border-gray-200 bg-white px-3 py-1 text-xs font-medium text-gray-600 shadow-sm">
          <span className="hidden sm:inline">{collapsed ? 'Развернуть' : 'Свернуть'}</span>
          <ChevronDown className={`h-4 w-4 transition-transform ${collapsed ? '-rotate-90' : 'rotate-0'}`} />
        </span>
      </button>

      <div className={collapsed ? 'hidden' : ''}>{children}</div>
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
  rawPayments,
  className = '',
  storageKey,
  onCardSelect,
}: Props) {
  const [tab, setTab] = useState<GroupKind>('Income');
  const [collapsed, setCollapsed] = useState<CollapsedState>(() => loadCollapsedState(storageKey));

  useEffect(() => {
    setCollapsed(loadCollapsedState(storageKey));
  }, [storageKey]);

  useEffect(() => {
    if (!storageKey) {
      return;
    }

    try {
      localStorage.setItem(storageKey, JSON.stringify(collapsed));
    } catch {
      /** */
    }
  }, [collapsed, storageKey]);

  const toggleGroup = (kind: GroupKind) => {
    setCollapsed((prev) => ({
      ...prev,
      [kind]: !prev[kind],
    }));
  };

  return (
    <div className={`w-full ${className}`}>
      <div className="mb-3 sm:hidden">
        <div
          role="tablist"
          aria-label="Статистика по типам"
          className="grid grid-cols-2 rounded-xl border bg-white p-1 shadow-sm">
          <button
            type="button"
            role="tab"
            aria-selected={tab === 'Income'}
            onClick={() => setTab('Income')}
            className={[
              'rounded-lg px-3 py-2 text-sm font-medium transition-colors',
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
              'rounded-lg px-3 py-2 text-sm font-medium transition-colors',
              tab === 'Expense'
                ? 'bg-rose-50 text-rose-700 shadow-inner'
                : 'text-gray-600 hover:bg-gray-50',
            ].join(' ')}>
            Расходы
          </button>
        </div>
      </div>

      <div className="space-y-4 sm:hidden">
        {tab === 'Income' ? (
          <StatsGroup
            title="Доходы"
            Icon={TrendingUp}
            tone="income"
            collapsed={collapsed.Income}
            onToggle={() => toggleGroup('Income')}>
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
              rawPayments={rawPayments}
              onCardSelect={onCardSelect}
            />
          </StatsGroup>
        ) : (
          <StatsGroup
            title="Расходы"
            Icon={TrendingDown}
            tone="expense"
            collapsed={collapsed.Expense}
            onToggle={() => toggleGroup('Expense')}>
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
              rawPayments={rawPayments}
              onCardSelect={onCardSelect}
            />
          </StatsGroup>
        )}
      </div>

      <div className="hidden gap-4 sm:flex sm:flex-col">
        <StatsGroup
          title="Доходы"
          Icon={TrendingUp}
          tone="income"
          collapsed={collapsed.Income}
          onToggle={() => toggleGroup('Income')}>
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
            rawPayments={rawPayments}
            onCardSelect={onCardSelect}
          />
        </StatsGroup>

        <StatsGroup
          title="Расходы"
          Icon={TrendingDown}
          tone="expense"
          collapsed={collapsed.Expense}
          onToggle={() => toggleGroup('Expense')}>
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
            rawPayments={rawPayments}
            onCardSelect={onCardSelect}
          />
        </StatsGroup>
      </div>
    </div>
  );
}
