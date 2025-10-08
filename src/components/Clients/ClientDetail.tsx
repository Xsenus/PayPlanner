import {
  ArrowLeft,
  Users,
  TrendingUp,
  TrendingDown,
  Calendar as CalendarIcon,
  CheckCircle,
  Clock,
  AlertTriangle,
  Plus,
  Edit,
  Trash2,
  PlusCircle,
} from 'lucide-react';
import { useEffect, useMemo, useRef, useState } from 'react';
import { useClientCases } from '../../hooks/useClientCases';
import { apiService } from '../../services/api';
import { usePayments } from '../../hooks/usePayments';
import { PaymentModal } from '../Calendar/PaymentModal';
import type { Payment, ClientCase } from '../../types';
import { toRuDate, formatLocalYMD } from '../../utils/dateUtils';
import { MonthRangePicker } from '../MonthRange/MonthRangePicker';
import { CaseModal } from './CaseModal';
import { TwoTypeStats } from '../Statistics/TwoTypeStats';
import { formatCurrencySmart } from '../../utils/formatters';

interface ClientDetailProps {
  clientId: number;
  onBack: () => void;
  initialCaseId?: number | 'all';
}

type PaymentBody = Omit<Payment, 'id' | 'createdAt'>;
type PaymentUpsert =
  | Omit<Payment, 'id' | 'createdAt'>
  | ({ id: number } & Omit<Payment, 'id' | 'createdAt'>);

type NormalizedStatus = 'completed' | 'pending' | 'overdue';

function normalizeStatus(s?: string): NormalizedStatus {
  const v = (s ?? '').toLowerCase();
  if (v.includes('complete') || v.includes('выполн')) return 'completed';
  if (v.includes('overdue') || v.includes('проср')) return 'overdue';
  return 'pending';
}

function ymStart(ym: string): string {
  const [y, m] = ym.split('-').map(Number);
  return formatLocalYMD(new Date(y, (m ?? 1) - 1, 1));
}
function ymEnd(ym: string): string {
  const [y, m] = ym.split('-').map(Number);
  return formatLocalYMD(new Date(y, m ?? 1, 0));
}

function caseStatusLabel(s?: string) {
  const v = (s ?? '').toLowerCase();
  if (v.includes('open')) return 'Открыто';
  if (v.includes('hold')) return 'Приостановлено';
  if (v.includes('closed')) return 'Закрыто';
  return s ?? '';
}

function toStatsStatusFilter(
  s: 'all' | NormalizedStatus,
): 'All' | 'Pending' | 'Completed' | 'Overdue' {
  switch (s) {
    case 'completed':
      return 'Completed';
    case 'overdue':
      return 'Overdue';
    case 'pending':
      return 'Pending';
    default:
      return 'All';
  }
}

const MIN_DATE = '1900-01-01';
const MAX_DATE = '2100-12-31';

export function ClientDetail({ clientId, onBack, initialCaseId }: ClientDetailProps) {
  const [clientName, setClientName] = useState<string>('...');
  const [selectedCaseId, setSelectedCaseId] = useState<number | 'all'>(initialCaseId ?? 'all');

  const { cases: serverCases } = useClientCases(clientId);
  const [casesLocal, setCasesLocal] = useState<ClientCase[] | null>(null);
  useEffect(() => setCasesLocal(serverCases), [serverCases]);
  const cases = useMemo(() => casesLocal ?? serverCases, [casesLocal, serverCases]);

  const [monthFrom, setMonthFrom] = useState<string>('');
  const [monthTo, setMonthTo] = useState<string>('');
  const [statusFilter, setStatusFilter] = useState<'all' | NormalizedStatus>('all');

  const [payModalOpen, setPayModalOpen] = useState(false);
  const [editingPayment, setEditingPayment] = useState<Payment | null>(null);

  const [caseModalOpen, setCaseModalOpen] = useState(false);
  const [editingCase, setEditingCase] = useState<ClientCase | null>(null);
  const [caseMode, setCaseMode] = useState<'create' | 'edit'>('create');

  const [statsReloadToken, setStatsReloadToken] = useState(0);
  const bumpStats = () => setStatsReloadToken((x) => x + 1);

  useEffect(() => {
    let alive = true;
    apiService
      .getClient(clientId)
      .then((c) => {
        if (alive) setClientName(c.name ?? `Client #${clientId}`);
      })
      .catch(() => {
        if (alive) setClientName(`Client #${clientId}`);
      });
    return () => {
      alive = false;
    };
  }, [clientId]);

  const caseIdForQuery = selectedCaseId === 'all' ? undefined : Number(selectedCaseId);

  const { fromDateStr, toDateStr } = useMemo(() => {
    if (monthFrom && monthTo) {
      return { fromDateStr: ymStart(monthFrom), toDateStr: ymEnd(monthTo) };
    }
    if (monthFrom && !monthTo) {
      const f = ymStart(monthFrom);
      return { fromDateStr: f, toDateStr: ymEnd(monthFrom) };
    }
    if (!monthFrom && monthTo) {
      const t = ymEnd(monthTo);
      return { fromDateStr: ymStart(monthTo), toDateStr: t };
    }
    return { fromDateStr: MIN_DATE, toDateStr: MAX_DATE };
  }, [monthFrom, monthTo]);

  const pollInterval = payModalOpen ? 0 : 5000;
  const {
    payments,
    loading: loadingPayments,
    error,
    refresh: refreshPayments,
    createPayment,
    updatePayment,
    deletePayment,
  } = usePayments(fromDateStr, toDateStr, { pollInterval, clientId, caseId: caseIdForQuery });

  function makeStatsSignature(arr: Payment[]): string {
    if (!arr || arr.length === 0) return 'empty';
    return arr
      .map((p) => `${p.id}:${p.amount}:${p.status}:${p.type}`)
      .sort()
      .join('|');
  }
  const prevStatsSigRef = useRef<string>('init');
  useEffect(() => {
    const sig = makeStatsSignature(payments);
    if (prevStatsSigRef.current !== sig) {
      prevStatsSigRef.current = sig;
      bumpStats();
    }
  }, [payments]);

  const lastPaymentDate = useMemo(() => {
    if (!payments.length) return null;
    return payments.map((p) => p.date).sort((a, b) => +new Date(b) - +new Date(a))[0];
  }, [payments]);

  const visiblePayments = useMemo(() => {
    const list = payments.slice().sort((a, b) => +new Date(a.date) - +new Date(b.date));
    if (statusFilter === 'all') return list;
    return list.filter((p) => normalizeStatus(p.status) === statusFilter);
  }, [payments, statusFilter]);

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

  const toBody = (p: PaymentBody): PaymentBody => ({
    ...p,
    notes: p.notes ?? null,
    clientId: p.clientId ?? clientId ?? null,
    clientCaseId: p.clientCaseId ?? caseIdForQuery ?? null,
    dealTypeId: p.dealTypeId ?? null,
    incomeTypeId: p.incomeTypeId ?? null,
    paymentSourceId: p.paymentSourceId ?? null,
    paymentStatusId: p.paymentStatusId ?? null,
    account: p.account ?? null,
    accountDate: p.accountDate ?? null,
  });

  const submitPayment = async (payload: PaymentUpsert) => {
    if ('id' in payload) {
      await updatePayment({ ...toBody(payload as PaymentBody), id: payload.id });
    } else {
      await createPayment(toBody(payload as PaymentBody));
    }
    closePayModal();
    await refreshPayments();
    bumpStats();
  };

  const removePayment = async (id: number) => {
    if (!window.confirm('Удалить платёж?')) return;
    await deletePayment(id);
    await refreshPayments();
    bumpStats();
  };

  const openAddCase = () => {
    setEditingCase(null);
    setCaseMode('create');
    setCaseModalOpen(true);
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

  const notFound = !loadingPayments && clientName === '...' && payments.length === 0;

  return (
    <div className="min-h-screen bg-gray-50">
      <div className="max-w-[calc(100vw-2rem)] mx-auto p-6">
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
              title={clientName}>
              {clientName}
            </h1>
            <button
              type="button"
              onClick={openAddCase}
              title="Добавить дело"
              aria-label="Добавить дело"
              className="order-3 sm:order-2 ml-auto sm:ml-2 w-12 h-12 inline-flex items-center justify-center rounded-lg border border-dashed border-emerald-300 text-emerald-700 bg-emerald-50 hover:bg-emerald-100 hover:border-emerald-400 transition-colors shrink-0">
              <PlusCircle size={24} />
            </button>
          </div>
        </div>

        <TwoTypeStats
          clientId={clientId}
          caseId={caseIdForQuery}
          from={fromDateStr}
          to={toDateStr}
          statusFilter={toStatsStatusFilter(statusFilter)}
          reloadToken={statsReloadToken}
          rawPayments={payments}
          className="mb-6"
        />

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
                className="w-full bg-white border border-gray-300 rounded-lg px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-emerald-500 focus:border-emerald-500">
                <option value="all">Все статусы</option>
                <option value="pending">Ожидается</option>
                <option value="completed">Выполнено</option>
                <option value="overdue">Просрочено</option>
              </select>
            </div>

            <div className="w-full sm:w-auto sm:ml-auto order-last sm:order-none">
              <button
                onClick={openAddPayment}
                className="w-full sm:w-auto inline-flex items-center justify-center gap-2 bg-blue-600 text-white px-3 py-2 rounded-lg hover:bg-blue-700">
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
                const s = (k.status ?? '').toLowerCase();
                const statusClasses = s.includes('open')
                  ? 'bg-green-50 text-green-700 border-green-200'
                  : s.includes('hold')
                  ? 'bg-amber-50 text-amber-800 border-amber-200'
                  : s.includes('closed')
                  ? 'bg-red-50 text-red-700 border-red-200'
                  : 'bg-slate-50 text-slate-700 border-slate-200';
                return (
                  <button
                    key={k.id}
                    type="button"
                    onClick={() => setSelectedCaseId(k.id)}
                    title={caseStatusLabel(k.status)}
                    className={`px-3 py-1.5 rounded-full text-sm border transition-colors ${
                      active
                        ? 'bg-slate-800 text-white border-slate-800 shadow-sm'
                        : `${statusClasses} hover:opacity-90`
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
              {lastPaymentDate && (
                <div className="flex items-center gap-2 text-sm text-gray-500">
                  <CalendarIcon size={16} />
                  Последний платёж: {toRuDate(lastPaymentDate)}
                </div>
              )}
            </div>
          </div>

          <div className="p-6">
            {loadingPayments ? (
              <div className="text-center py-8 text-gray-500">Загрузка...</div>
            ) : visiblePayments.length === 0 ? (
              <div className="text-center py-8">
                <CalendarIcon size={48} className="mx-auto mb-4 text-gray-300" />
                <h3 className="text-lg font-medium text-gray-900 mb-2">Платежи не найдены</h3>
                <p className="text-gray-500">Нет платежей по выбранному фильтру</p>
              </div>
            ) : (
              <div className="space-y-4">
                {visiblePayments.map((payment) => (
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
                        <p
                          className="font-medium text-gray-900"
                          title={formatCurrencySmart(payment.amount, { alwaysCents: true }).full}>
                          {formatCurrencySmart(payment.amount).full}
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
                              <span className="text-xs text-amber-600 leading-none">Ожидается</span>
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
          key={payModalOpen ? (editingPayment ? `edit-${editingPayment.id}` : 'new') : 'closed'}
          isOpen={payModalOpen}
          onClose={closePayModal}
          payment={editingPayment ?? undefined}
          onSubmit={submitPayment}
          onDelete={async (id: number) => {
            await removePayment(id);
            closePayModal();
          }}
          type={editingPayment?.type ?? 'Income'}
          defaultClientId={clientId}
          defaultClientCaseId={selectedCaseId === 'all' ? undefined : Number(selectedCaseId)}
          casesPrefetch={cases}
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

        {notFound && (
          <div className="min-h-[120px] flex items-center justify-center text-gray-500">
            <Users size={24} className="mr-2" /> Клиент не найден
          </div>
        )}
      </div>
    </div>
  );
}
