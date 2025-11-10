import { useMemo } from 'react';
import { TrendingUp, TrendingDown, Clock, AlertTriangle, Wallet, Banknote } from 'lucide-react';
import { StatsCards, type StatCardItem } from './StatCardItem';
import { useSummaryStats } from '../../hooks/useSummaryStats';
import type { PeriodKey, Payment, SummaryStatus } from '../../types';
import { formatCurrencySmart } from '../../utils/formatters';

type StatusFilter = 'All' | 'Pending' | 'Completed' | 'Overdue';

type Props = {
  kind: 'Income' | 'Expense';
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
  onCardSelect?: (payload: { kind: 'Income' | 'Expense'; metric: 'completed' | 'pending' | 'overdue' | 'overall' | 'debt'; title: string }) => void;
};

function mapServerStats(raw: unknown, kind: 'Income' | 'Expense') {
  const s = (raw ?? {}) as Record<string, unknown>;
  const sectKeys =
    kind === 'Income'
      ? ['income', 'incomes', 'in', 'incomeStats']
      : ['expense', 'expenses', 'outcome', 'outgo', 'expenseStats'];
  let bucket: Record<string, unknown> = {};
  for (const k of sectKeys) {
    const v = s[k];
    if (v && typeof v === 'object') {
      bucket = v as Record<string, unknown>;
      break;
    }
  }
  const num = (v: unknown) => (typeof v === 'number' && Number.isFinite(v) ? v : 0);

  const completed = num(bucket['completedAmount']) ?? 0;
  const pending = num(bucket['pendingAmount']) || num(bucket['expected']) || 0;
  const overdue = num(bucket['overdueAmount']) || num(bucket['overdue']) || 0;
  const overall = num(bucket['totalAmount']) || num(s['overall']) || 0;

  let debtLeft = num(bucket['debtLeft']) || num(bucket['remaining']) || 0;
  if (!debtLeft) debtLeft = pending + overdue;

  return { sumByType: completed, expected: pending, overdue, overall, debtLeft };
}

function matchesSearch(p: Payment, q: string): boolean {
  const s = q.trim().toLowerCase();
  if (!s) return true;
  const fields: Array<string | null | undefined> = [
    p.description,
    p.notes,
    p.type,
    p.status,
    p.client?.name,
    p.dealType?.name,
    p.incomeType?.name,
    p.paymentSource?.name,
  ];
  return fields.some((v) => (v ?? '').toLowerCase().includes(s));
}

function aggregateFromPayments(
  payments: Payment[],
  kind: 'Income' | 'Expense',
  opts: { clientId?: number; caseId?: number; statusFilter?: StatusFilter; search?: string },
) {
  const filtered = payments.filter((p) => {
    if (p.type !== kind) return false;
    if (opts.clientId && p.clientId !== opts.clientId) return false;
    if (opts.caseId && p.clientCaseId !== opts.caseId) return false;
    if (opts.statusFilter && opts.statusFilter !== 'All' && p.status !== opts.statusFilter)
      return false;
    if (opts.search && !matchesSearch(p, opts.search)) return false;
    return true;
  });

  let completed = 0;
  let pending = 0;
  let overdue = 0;
  for (const p of filtered) {
    const amt = typeof p.amount === 'number' ? p.amount : 0;
    switch (p.status) {
      case 'Completed':
        completed += amt;
        break;
      case 'Overdue':
        overdue += amt;
        break;
      case 'Pending':
      default:
        pending += amt;
        break;
    }
  }

  const overall = completed + pending + overdue;
  const debtLeft = pending + overdue;
  return { sumByType: completed, expected: pending, overdue, overall, debtLeft };
}

export function TypeStatsBlock({
  kind,
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
  onCardSelect,
}: Props) {
  const apiStatus: SummaryStatus | undefined =
    statusFilter && statusFilter !== 'All' ? (statusFilter as SummaryStatus) : undefined;

  const { data, loading } = useSummaryStats({
    clientId,
    caseId,
    from,
    to,
    period,
    type: kind,
    status: apiStatus,
    q: search && search.trim() ? search.trim() : undefined,
    reloadToken,
  });
  const mappedServer = useMemo(() => mapServerStats(data, kind), [data, kind]);

  const mappedFallback = useMemo(
    () =>
      aggregateFromPayments(rawPayments ?? [], kind, {
        clientId,
        caseId,
        statusFilter,
        search,
      }),
    [rawPayments, kind, clientId, caseId, statusFilter, search],
  );

  const useFallback =
    !data ||
    (mappedServer.sumByType === 0 &&
      mappedServer.expected === 0 &&
      mappedServer.overdue === 0 &&
      mappedServer.overall === 0 &&
      mappedServer.debtLeft === 0);

  const m = useFallback ? mappedFallback : mappedServer;

  const hasDataAlready = !!data || !!(rawPayments && rawPayments.length > 0);
  const showLoading = loading && !hasDataAlready;

  const fmt = (n: number) => formatCurrencySmart(n);
  const title = kind === 'Income' ? 'Доходы' : 'Расходы';
  const headIcon = kind === 'Income' ? TrendingUp : TrendingDown;
  const headColor = kind === 'Income' ? 'text-emerald-600' : 'text-rose-600';
  const headBg = kind === 'Income' ? 'bg-emerald-50' : 'bg-rose-50';

  const items: StatCardItem[] = [
    {
      title,
      value: fmt(m.sumByType).full,
      titleAttr: fmt(m.sumByType).full,
      icon: headIcon,
      color: headColor,
      bg: headBg,
      hint: (
        <div className="text-xs leading-5">
          Сумма платежей где статус равен 'Выполнено' в выбранном диапазоне дат
        </div>
      ),
      onClick: onCardSelect
        ? () =>
            onCardSelect({
              kind,
              metric: 'completed',
              title,
            })
        : undefined,
    },
    {
      title: 'Ожидается',
      value: fmt(m.expected).full,
      titleAttr: fmt(m.expected).full,
      icon: Clock,
      color: 'text-amber-600',
      bg: 'bg-amber-50',
      hint: (
        <div className="text-xs leading-5">
          Сумма платежей где статус равен 'Ожидается' в выбранном диапазоне дат
        </div>
      ),
      onClick: onCardSelect
        ? () =>
            onCardSelect({
              kind,
              metric: 'pending',
              title: 'Ожидается',
            })
        : undefined,
    },
    {
      title: 'Просрочено',
      value: fmt(m.overdue).full,
      titleAttr: fmt(m.overdue).full,
      icon: AlertTriangle,
      color: 'text-purple-600',
      bg: 'bg-purple-50',
      hint: (
        <div className="text-xs leading-5">
          Сумма платежей где статус равен 'Просрочено' в выбранном диапазоне дат
        </div>
      ),
      onClick: onCardSelect
        ? () =>
            onCardSelect({
              kind,
              metric: 'overdue',
              title: 'Просрочено',
            })
        : undefined,
    },
    {
      title: 'Общая сумма',
      value: fmt(m.overall).full,
      titleAttr: fmt(m.overall).full,
      icon: Wallet,
      color: 'text-cyan-700',
      bg: 'bg-cyan-50',
      hint: <div className="text-xs leading-5">Оплачено + Ожидается + Просрочено</div>,
      onClick: onCardSelect
        ? () =>
            onCardSelect({
              kind,
              metric: 'overall',
              title: 'Общая сумма',
            })
        : undefined,
    },
    {
      title: 'Остаток долга',
      value: fmt(m.debtLeft).full,
      titleAttr: fmt(m.debtLeft).full,
      icon: Banknote,
      color: 'text-sky-700',
      bg: 'bg-sky-50',
      hint: <div className="text-xs leading-5">Ожидается + Просрочено</div>,
      onClick: onCardSelect
        ? () =>
            onCardSelect({
              kind,
              metric: 'debt',
              title: 'Остаток долга',
            })
        : undefined,
    },
  ];

  return (
    <div className={`flex flex-col gap-3 ${className}`}>
      <div className="min-h-[120px]">
        <StatsCards items={items} loading={showLoading} lgCols={items.length} />
      </div>
    </div>
  );
}
