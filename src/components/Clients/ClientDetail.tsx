import {
  ArrowLeft,
  Users,
  TrendingUp,
  TrendingDown,
  Calendar,
  Check,
  CheckCircle,
  Clock,
  AlertTriangle,
  Plus,
  Edit,
  Trash2,
  Wallet,
  Banknote,
  PlusCircle,
} from 'lucide-react';
import { useEffect, useMemo, useState } from 'react';
import { useClientStats } from '../../hooks/useClients';
import { useClientCases } from '../../hooks/useClientCases';
import { apiService } from '../../services/api';
import { PaymentModal } from '../Calendar/PaymentModal';
import type { Payment, ClientCase } from '../../types';
import { toRuDate } from '../../utils/dateUtils';
import { MonthRangePicker } from '../MonthRange/MonthRangePicker';
import { StatCardItem, StatsCards } from '../Statistics/StatCardItem';
import { formatCurrencySmart } from '../../utils/formatters';
import { CaseModal } from './CaseModal';

interface ClientDetailProps {
  clientId: number;
  onBack: () => void;
  initialCaseId?: number | 'all';
}

type PaymentUpsert =
  | Omit<Payment, 'id' | 'createdAt'>
  | ({ id: number } & Omit<Payment, 'id' | 'createdAt'>);

function caseStatusLabel(statusRaw?: string) {
  const s = (statusRaw ?? '').toLowerCase();
  if (s.includes('open')) return 'Открыто';
  if (s.includes('onhold') || s.includes('hold')) return 'Приостановлено';
  if (s.includes('closed')) return 'Закрыто';
  return statusRaw ?? '';
}

function caseStatusClasses(statusRaw?: string) {
  const s = (statusRaw ?? '').toLowerCase();
  if (s.includes('open')) return 'bg-green-50 text-green-700 border-green-200';
  if (s.includes('onhold') || s.includes('hold'))
    return 'bg-amber-50 text-amber-800 border-amber-200';
  if (s.includes('closed')) return 'bg-red-50 text-red-700 border-red-200';
  return 'bg-slate-50 text-slate-700 border-slate-200';
}

type ClientStatsShape = {
  clientName: string;
  totalIncome: number;
  totalExpenses: number;
  netAmount: number;
  pendingPaymentsCount?: number;
  overduePaymentsCount?: number;
  pendingAmount?: number;
  overdueAmount?: number;
  overallAmount?: number;
  remainingDebt?: number;
  paidPayments?: number;
  totalPayments?: number;
  lastPaymentDate?: string | null;
  recentPayments: Payment[];
};

type NormalizedStatus = 'completed' | 'pending' | 'overdue';
function normalizeStatus(s?: string): NormalizedStatus {
  const v = (s ?? '').toLowerCase();
  if (v.includes('complete') || v.includes('выполн')) return 'completed';
  if (v.includes('overdue') || v.includes('проср')) return 'overdue';
  return 'pending';
}

export function ClientDetail({ clientId, onBack, initialCaseId }: ClientDetailProps) {
  const [selectedCaseId, setSelectedCaseId] = useState<number | 'all'>(initialCaseId ?? 'all');

  const { stats, loading, error } = useClientStats(
    clientId,
    selectedCaseId === 'all' ? undefined : Number(selectedCaseId),
  );

  const { cases: serverCases } = useClientCases(clientId);
  const [casesLocal, setCasesLocal] = useState<ClientCase[] | null>(null);
  useEffect(() => {
    setCasesLocal(serverCases);
  }, [serverCases]);
  const cases = useMemo(() => casesLocal ?? serverCases, [casesLocal, serverCases]);

  const [monthFrom, setMonthFrom] = useState<string>('');
  const [monthTo, setMonthTo] = useState<string>('');

  const [statusFilter, setStatusFilter] = useState<'all' | NormalizedStatus>('all');

  const [payModalOpen, setPayModalOpen] = useState(false);
  const [editingPayment, setEditingPayment] = useState<Payment | null>(null);
  const [localStats, setLocalStats] = useState<ClientStatsShape | null>(null);

  const [caseModalOpen, setCaseModalOpen] = useState(false);
  const [editingCase, setEditingCase] = useState<ClientCase | null>(null);
  const [caseMode, setCaseMode] = useState<'create' | 'edit'>('create');

  useEffect(() => {
    if (!stats) return;
    const base = stats as unknown as ClientStatsShape;

    const all = base.recentPayments ?? [];
    const visible = all.filter(matchesFilter).sort((a, b) => +new Date(a.date) - +new Date(b.date));

    const withList = { ...base, recentPayments: visible, lastPaymentDate: maxDate(visible) };
    setLocalStats(recomputeAggregates(withList, visible));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [stats]);

  useEffect(() => {
    if (initialCaseId !== undefined) setSelectedCaseId(initialCaseId);
  }, [initialCaseId]);

  const view = localStats ?? (stats as unknown as ClientStatsShape);

  const openAddPayment = () => {
    setEditingPayment(null);
    setPayModalOpen(true);
  };
  const openEditPayment = (p: Payment) => {
    setEditingPayment(p);
    setPayModalOpen(true);
  };
  const closePayModal = () => {
    setPayModalOpen(false);
    setEditingPayment(null);
  };

  const openAddCase = () => {
    setEditingCase(null);
    setCaseMode('create');
    setCaseModalOpen(true);
  };

  const overallAmount =
    (view?.totalIncome ?? 0) + (view?.pendingAmount ?? 0) + (view?.overdueAmount ?? 0);

  const remainingDebt = (view?.pendingAmount ?? 0) + (view?.overdueAmount ?? 0);

  const formatCurrency = (amount: number) =>
    new Intl.NumberFormat('ru-RU', {
      style: 'currency',
      currency: 'RUB',
      minimumFractionDigits: 0,
      maximumFractionDigits: 0,
    }).format(amount);

  const maxDate = (ps: Payment[]) =>
    ps.length ? ps.map((p) => p.date).sort((a, b) => +new Date(b) - +new Date(a))[0] : null;

  const matchesFilter = (p: Payment) => {
    const ym = p.date.slice(0, 7);
    const okCase = selectedCaseId === 'all' || p.clientCaseId === Number(selectedCaseId);
    const okFrom = !monthFrom || ym >= monthFrom;
    const okTo = !monthTo || ym <= monthTo;
    const okStatus = statusFilter === 'all' || normalizeStatus(p.status) === statusFilter;
    return okCase && okFrom && okTo && okStatus;
  };

  type Totals = {
    income: number;
    expense: number;
    pending: number;
    overdue: number;
    total: number;
    pendingAmount: number;
    overdueAmount: number;
  };

  function recomputeAggregates(base: ClientStatsShape, items: Payment[]): ClientStatsShape {
    const totals = items.reduce<Totals>(
      (acc, p) => {
        const s = normalizeStatus(p.status);
        if (s === 'completed') {
          if (p.type === 'Income') acc.income += p.amount;
          if (p.type === 'Expense') acc.expense += p.amount;
        } else if (s === 'pending') {
          acc.pending += 1;
          acc.pendingAmount += p.amount;
        } else if (s === 'overdue') {
          acc.overdue += 1;
          acc.overdueAmount += p.amount;
        }

        acc.total += 1;
        return acc;
      },
      {
        income: 0,
        expense: 0,
        pending: 0,
        overdue: 0,
        total: 0,
        pendingAmount: 0,
        overdueAmount: 0,
      },
    );

    return {
      ...base,
      totalIncome: totals.income,
      totalExpenses: totals.expense,
      netAmount: totals.income - totals.expense,
      pendingPaymentsCount: totals.pending,
      overduePaymentsCount: totals.overdue,
      pendingAmount: totals.pendingAmount,
      overdueAmount: totals.overdueAmount,
      overallAmount: totals.income + totals.pendingAmount + totals.overdueAmount,
      remainingDebt: totals.pendingAmount + totals.overdueAmount,
      paidPayments: undefined,
      totalPayments: totals.total,
    };
  }

  function applyDelta(base: ClientStatsShape, p: Payment, sign: 1 | -1): ClientStatsShape {
    let list = base.recentPayments ?? [];

    if (sign === 1) {
      const exists = list.some((x) => x.id === p.id);
      list = exists ? list.map((x) => (x.id === p.id ? p : x)) : [p, ...list];
    } else {
      list = list.filter((x) => x.id !== p.id);
    }

    const visible = list
      .filter(matchesFilter)
      .sort((a, b) => +new Date(a.date) - +new Date(b.date));

    const withList = { ...base, recentPayments: visible, lastPaymentDate: maxDate(visible) };
    return recomputeAggregates(withList, visible);
  }

  function optimisticUpsert(prev: ClientStatsShape, newP: Payment, oldP?: Payment) {
    let snap = prev;
    if (oldP) snap = applyDelta(snap, oldP, -1);
    snap = applyDelta(snap, newP, +1);
    return snap;
  }
  function optimisticDelete(prev: ClientStatsShape, oldP: Payment) {
    return applyDelta(prev, oldP, -1);
  }

  useEffect(() => {
    setLocalStats((prev) => {
      if (!prev) return prev;
      const all =
        (stats as unknown as ClientStatsShape)?.recentPayments ?? prev.recentPayments ?? [];

      const visible = all
        .filter(matchesFilter)
        .sort((a, b) => +new Date(a.date) - +new Date(b.date));

      const withList = { ...prev, recentPayments: visible, lastPaymentDate: maxDate(visible) };
      return recomputeAggregates(withList, visible);
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [selectedCaseId, monthFrom, monthTo, statusFilter]);

  type PaymentBody = Omit<Payment, 'id' | 'createdAt'>;
  const toBodyFromPayment = (p: Payment): PaymentBody => ({
    date: p.date,
    amount: p.amount,
    type: p.type,
    status: p.status,
    description: p.description,
    isPaid: p.isPaid,
    paidDate: p.paidDate,
    notes: p.notes ?? null,
    clientId: p.clientId ?? null,
    clientCaseId: p.clientCaseId ?? null,
    dealTypeId: p.dealTypeId ?? null,
    incomeTypeId: p.incomeTypeId ?? null,
    paymentSourceId: p.paymentSourceId ?? null,
    paymentStatusId: p.paymentStatusId ?? null,
    account: p.account ?? null,
    accountDate: p.accountDate ?? null,
  });

  const normFk = (v: unknown): number | null | undefined => {
    if (v === undefined) return undefined;
    if (v === null || v === '') return null;
    const n = typeof v === 'number' ? v : Number(v);
    return Number.isFinite(n) ? n : null;
  };

  const submitPayment = async (payload: PaymentUpsert) => {
    const isEdit = 'id' in payload && typeof payload.id === 'number';
    const incoming = payload as Partial<PaymentBody>;

    const oldPayment = isEdit
      ? (view.recentPayments ?? []).find((p) => p.id === (payload as { id: number }).id)
      : undefined;

    const base: PaymentBody = oldPayment ? toBodyFromPayment(oldPayment) : ({} as PaymentBody);

    const resolvedClientId =
      incoming.clientId !== undefined
        ? normFk(incoming.clientId)
        : !isEdit
        ? clientId
        : base.clientId;
    const resolvedClientCaseId =
      incoming.clientCaseId !== undefined
        ? normFk(incoming.clientCaseId)
        : !isEdit && selectedCaseId !== 'all'
        ? Number(selectedCaseId)
        : base.clientCaseId;

    const body: PaymentBody = {
      ...base,
      ...incoming,
      clientId: (resolvedClientId ?? null) as number | null,
      clientCaseId: (resolvedClientCaseId ?? null) as number | null,
    };

    const saved = isEdit
      ? await apiService.updatePayment((payload as { id: number }).id, body)
      : await apiService.createPayment(body);

    if (isEdit && saved.clientId !== clientId) {
      if (oldPayment) {
        setLocalStats((prev) => {
          const current = prev ?? (stats as unknown as ClientStatsShape);
          return optimisticDelete(current, oldPayment);
        });
      }
      closePayModal();
      return;
    }

    setLocalStats((prev) => {
      const current = prev ?? (stats as unknown as ClientStatsShape);
      return optimisticUpsert(current, saved, oldPayment);
    });

    closePayModal();
  };

  const removePayment = async (id: number) => {
    const toRemove =
      (view.recentPayments ?? []).find((p) => p.id === id) ??
      (localStats?.recentPayments ?? []).find((p) => p.id === id);

    await apiService.deletePayment(id);

    if (toRemove) {
      setLocalStats((prev) => {
        const current = prev ?? (stats as unknown as ClientStatsShape);
        return optimisticDelete(current, toRemove);
      });
    }
  };

  const saveCase = async (patch: Partial<ClientCase>): Promise<void> => {
    const base: Omit<ClientCase, 'id' | 'createdAt' | 'payments'> = {
      clientId,
      title: (patch.title ?? '').trim(),
      description: (patch.description ?? '').trim(),
      status: (patch.status ?? 'Open') as ClientCase['status'],
    };

    const created = await apiService.createCase(base);
    setCasesLocal((prev) => [created, ...(prev ?? [])]);
    setSelectedCaseId(created.id);
    setCaseModalOpen(false);
    setEditingCase(null);
  };

  if (loading) {
    return (
      <div className="min-h-screen bg-gray-50 flex items-center justify-center">
        <div className="text-gray-500">Loading client details...</div>
      </div>
    );
  }

  if (!stats) {
    return (
      <div className="min-h-screen bg-gray-50 flex items-center justify-center">
        <div className="text-center">
          <Users size={48} className="mx-auto mb-4 text-gray-300" />
          <h3 className="text-lg font-medium text-gray-900 mb-2">Client not found</h3>
          <button onClick={onBack} className="text-blue-600 hover:text-blue-700 font-medium">
            Go back
          </button>
        </div>
      </div>
    );
  }

  const f = (n: number) => formatCurrencySmart(n);

  const items: StatCardItem[] = [
    {
      title: 'Доходы',
      ...f(view.totalIncome),
      icon: TrendingUp,
      color: 'text-emerald-600',
      bg: 'bg-emerald-50',
    },
    {
      title: 'Расходы',
      ...f(view.totalExpenses),
      icon: TrendingDown,
      color: 'text-red-600',
      bg: 'bg-red-50',
    },
    {
      title: 'Итог',
      ...f(view.netAmount),
      icon: Check,
      color: view.netAmount >= 0 ? 'text-emerald-600' : 'text-red-600',
      bg: view.netAmount >= 0 ? 'bg-emerald-50' : 'bg-red-50',
    },
    {
      title: 'Ожидается',
      ...f(view.pendingAmount ?? 0),
      icon: Clock,
      color: 'text-amber-600',
      bg: 'bg-amber-50',
    },
    {
      title: 'Просрочено',
      ...f(view.overdueAmount ?? 0),
      icon: AlertTriangle,
      color: 'text-purple-700',
      bg: 'bg-purple-50',
    },
    {
      title: 'Общая сумма',
      ...f(view.overallAmount ?? overallAmount),
      icon: Wallet,
      color: 'text-sky-700',
      bg: 'bg-sky-50',
    },
    {
      title: 'Остаток долга',
      ...f(view.remainingDebt ?? remainingDebt),
      icon: Banknote,
      color: 'text-indigo-700',
      bg: 'bg-indigo-50',
    },
  ].map(
    ({ short, full, ...rest }): StatCardItem => ({
      ...rest,
      value: short,
      titleAttr: full,
    }),
  );

  return (
    <div className="min-h-screen bg-gray-50">
      <div className="max-w-7xl mx-auto p-6">
        <div className="flex items-start justify-between gap-4 mb-8 flex-wrap">
          <div className="flex items-center gap-2 w-full">
            <button
              onClick={onBack}
              className="order-1 p-1 hover:bg-gray-100 rounded-lg transition-colors shrink-0"
              title="Назад"
              aria-label="Назад">
              <ArrowLeft size={24} />
            </button>
            <h1
              className="order-2 sm:order-3 flex-1 min-w-0 text-2xl font-bold text-gray-900"
              title={view.clientName}>
              {view.clientName}
            </h1>
            <button
              type="button"
              onClick={openAddCase}
              title="Добавить дело"
              aria-label="Добавить дело"
              className="order-3 sm:order-2 ml-auto sm:ml-2 w-12 h-12 inline-flex items-center justify-center
               rounded-lg border border-dashed border-emerald-300 text-emerald-700 bg-emerald-50
               hover:bg-emerald-100 hover:border-emerald-400 transition-colors shrink-0">
              <PlusCircle size={24} />
            </button>
          </div>
        </div>

        <StatsCards items={items} lgCols={7} />

        <div className="bg-white rounded-xl shadow-sm border border-gray-100 mb-6">
          <div className="p-6 flex flex-wrap items-center gap-3">
            <MonthRangePicker
              value={{ from: monthFrom || undefined, to: monthTo || undefined }}
              onChange={(r) => {
                setMonthFrom(r.from ?? '');
                setMonthTo(r.to ?? '');
              }}
              yearsBack={8}
              yearsForward={1}
            />

            <div className="relative w-full sm:w-56">
              <select
                value={statusFilter}
                onChange={(e) => setStatusFilter(e.target.value as 'all' | NormalizedStatus)}
                className="w-full bg-white border border-gray-300 rounded-lg px-3 py-2 text-sm
                           focus:outline-none focus:ring-2 focus:ring-emerald-500 focus:border-emerald-500">
                <option value="all">Все статусы</option>
                <option value="pending">Ожидается</option>
                <option value="completed">Выполнено</option>
                <option value="overdue">Просрочено</option>
              </select>
            </div>

            <div className="w-full sm:w-auto sm:ml-auto order-last sm:order-none">
              <button
                onClick={openAddPayment}
                className="w-full sm:w-auto inline-flex items-center justify-center gap-2
               bg-blue-600 text-white px-3 py-2 rounded-lg hover:bg-blue-700">
                <Plus size={16} /> Добавить платёж
              </button>
            </div>
          </div>

          <div className="px-6 pb-6 pt-0">
            <div className="flex flex-wrap gap-2">
              <button
                type="button"
                onClick={() => setSelectedCaseId('all')}
                className={`px-3 py-1.5 rounded-full text-sm border transition-colors ${
                  selectedCaseId === 'all'
                    ? 'bg-slate-800 text-white border-slate-800 shadow-sm'
                    : 'bg-slate-50 text-slate-700 border-slate-200 hover:bg-slate-100'
                }`}>
                Все дела
              </button>

              {cases.map((k) => {
                const active = selectedCaseId === k.id;
                const inactiveClasses = caseStatusClasses(k.status);
                return (
                  <button
                    key={k.id}
                    type="button"
                    onClick={() => setSelectedCaseId(k.id)}
                    title={caseStatusLabel(k.status)}
                    className={`px-3 py-1.5 rounded-full text-sm border transition-colors ${
                      active
                        ? 'bg-slate-800 text-white border-slate-800 shadow-sm'
                        : `${inactiveClasses} hover:opacity-90`
                    }`}>
                    {k.title}
                  </button>
                );
              })}
            </div>
          </div>
        </div>

        <div className="bg-white rounded-xl shadow-sm border border-gray-100">
          <div className="p-6 border-b border-gray-100">
            <div className="flex items-center justify-between">
              <h2 className="text-xl font-semibold text-gray-900">Платежи</h2>
              {view.lastPaymentDate && (
                <div className="flex items-center gap-2 text-sm text-gray-500">
                  <Calendar size={16} />
                  Последний платёж: {toRuDate(view.lastPaymentDate)}
                </div>
              )}
            </div>
          </div>

          <div className="p-6">
            {(view.recentPayments ?? []).length === 0 ? (
              <div className="text-center py-8">
                <Calendar size={48} className="mx-auto mb-4 text-gray-300" />
                <h3 className="text-lg font-medium text-gray-900 mb-2">Платежи не найдены</h3>
                <p className="text-gray-500">Нет платежей по выбранному фильтру</p>
              </div>
            ) : (
              <div className="space-y-4">
                {[...(view.recentPayments ?? [])]
                  .sort((a, b) => +new Date(a.date) - +new Date(b.date))
                  .map((payment: Payment) => (
                    <div
                      key={payment.id}
                      className="flex items-center justify-between p-4 border border-gray-200 rounded-lg">
                      <div className="flex items-start gap-3 min-w-0">
                        <div className="mt-0.5">
                          {payment.type === 'Income' ? (
                            <TrendingUp size={20} className="text-emerald-600" />
                          ) : (
                            <TrendingDown size={20} className="text-red-600" />
                          )}
                        </div>
                        <div className="min-w-0">
                          <p className="font-medium text-gray-900">
                            {formatCurrency(payment.amount)}
                          </p>
                          {payment.description && (
                            <p className="text-sm text-gray-500 truncate max-w-[45vw] sm:max-w-none">
                              {payment.description}
                            </p>
                          )}
                        </div>
                      </div>
                      <div className="grid grid-rows-2 content-center gap-1 text-right min-w-[160px]">
                        <div className="flex items-center justify-end gap-2">
                          <p className="text-sm font-medium text-gray-900 leading-none">
                            {toRuDate(payment.date)}
                          </p>
                          <button
                            className="inline-flex h-6 w-6 items-center justify-center rounded-md hover:bg-blue-50 text-blue-600"
                            onClick={() => openEditPayment(payment)}
                            title="Редактировать"
                            aria-label="Редактировать">
                            <Edit size={16} />
                          </button>
                        </div>
                        <div className="flex items-center justify-end gap-2">
                          {(() => {
                            const s = normalizeStatus(payment.status);
                            if (s === 'overdue') {
                              return (
                                <>
                                  <AlertTriangle size={14} className="text-purple-700" />
                                  <span className="text-xs text-purple-700 leading-none">
                                    Просрочено
                                  </span>
                                </>
                              );
                            }
                            if (s === 'completed') {
                              return (
                                <>
                                  <CheckCircle size={14} className="text-emerald-600" />
                                  <span className="text-xs text-emerald-600 leading-none">
                                    Выполнено
                                  </span>
                                </>
                              );
                            }
                            return (
                              <>
                                <Clock size={14} className="text-amber-600" />
                                <span className="text-xs text-amber-600 leading-none">
                                  Ожидается
                                </span>
                              </>
                            );
                          })()}
                          <button
                            className="inline-flex h-6 w-6 items-center justify-center rounded-md hover:bg-red-50 text-red-600"
                            onClick={() => removePayment(payment.id)}
                            title="Удалить"
                            aria-label="Удалить">
                            <Trash2 size={16} />
                          </button>
                        </div>
                      </div>
                    </div>
                  ))}
              </div>
            )}
          </div>
        </div>

        <PaymentModal
          isOpen={payModalOpen}
          onClose={closePayModal}
          payment={editingPayment ?? undefined}
          onSubmit={submitPayment}
          onDelete={async (id: number) => {
            await removePayment(id);
          }}
          type={editingPayment?.type ?? 'Income'}
          defaultClientId={clientId}
          defaultClientCaseId={selectedCaseId === 'all' ? undefined : Number(selectedCaseId)}
        />

        {caseModalOpen && (
          <CaseModal
            isOpen={caseModalOpen}
            onClose={() => setCaseModalOpen(false)}
            caseData={
              caseMode === 'edit' && editingCase
                ? editingCase
                : ({
                    id: 0,
                    clientId,
                    title: '',
                    description: '',
                    status: 'Open',
                    createdAt: new Date().toISOString(),
                  } as ClientCase)
            }
            onSave={saveCase}
            onDelete={undefined}
          />
        )}

        {error && (
          <div className="mt-4 text-sm text-red-600 bg-red-50 border border-red-200 rounded-lg px-3 py-2">
            {String(error)}
          </div>
        )}
      </div>
    </div>
  );
}
