import {
  ArrowLeft,
  Users,
  TrendingUp,
  TrendingDown,
  Calendar as CalendarIcon,
  CheckCircle,
  Clock,
  AlertTriangle,
  Loader2,
  Plus,
  Edit,
  Trash2,
  PlusCircle,
  WalletCards,
  FileCheck2,
  FileSignature,
  Calculator,
} from 'lucide-react';
import type { LucideIcon } from 'lucide-react';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useClientCases } from '../../hooks/useClientCases';
import { apiService } from '../../services/api';
import { usePayments } from '../../hooks/usePayments';
import { PaymentModal } from '../Calendar/PaymentModal';
import type { Act, ActInput, ActResponsible, ActStatus, Client, ClientCase, Payment } from '../../types';
import { toRuDate, formatLocalYMD } from '../../utils/dateUtils';
import { MonthRangePicker } from '../MonthRange/MonthRangePicker';
import { CaseModal } from './CaseModal';
import { TwoTypeStats } from '../Statistics/TwoTypeStats';
import { formatCurrencySmart } from '../../utils/formatters';
import { ActModal } from '../Acts/ActModal';

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

type ClientDetailSection =
  | 'payments'
  | 'accounts'
  | 'acts'
  | 'contracts'
  | 'settlement';

const SECTION_ORDER: ClientDetailSection[] = [
  'payments',
  'accounts',
  'acts',
  'contracts',
  'settlement',
];

const SECTION_META: Record<
  ClientDetailSection,
  {
    label: string;
    actionLabel: string;
    placeholderTitle?: string;
    placeholderDescription?: string;
    icon?: LucideIcon | null;
  }
> = {
  payments: {
    label: 'Платежи',
    actionLabel: 'Добавить платёж',
  },
  accounts: {
    label: 'Счета',
    actionLabel: 'Добавить счёт',
    placeholderTitle: 'Счета клиента',
    placeholderDescription: 'Здесь появится список счетов клиента после подключения модуля.',
    icon: WalletCards,
  },
  acts: {
    label: 'Акты',
    actionLabel: 'Добавить акт',
    placeholderTitle: 'Акты клиента',
    placeholderDescription: 'Управление актами будет доступно после доработки раздела.',
    icon: FileCheck2,
  },
  contracts: {
    label: 'Договоры',
    actionLabel: 'Добавить договор',
    placeholderTitle: 'Договоры клиента',
    placeholderDescription: 'Список договоров появится в этом разделе.',
    icon: FileSignature,
  },
  settlement: {
    label: 'Расчёт дела',
    actionLabel: 'Расчёт дела',
    placeholderTitle: 'Расчёт дела',
    placeholderDescription: 'Инструменты расчёта будут добавлены на следующем этапе.',
    icon: Calculator,
  },
};

const ACT_STATUS_LABELS: Record<ActStatus, string> = {
  Created: 'Создан',
  Transferred: 'Передан',
  Signed: 'Подписан',
  Terminated: 'Расторгнут',
};

const ACT_STATUS_CLASSES: Record<ActStatus, string> = {
  Created: 'bg-slate-100 text-slate-700 border border-slate-200',
  Transferred: 'bg-amber-100 text-amber-800 border border-amber-200',
  Signed: 'bg-emerald-100 text-emerald-700 border border-emerald-200',
  Terminated: 'bg-rose-100 text-rose-700 border border-rose-200',
};

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

  const [acts, setActs] = useState<Act[]>([]);
  const [actsLoading, setActsLoading] = useState(false);
  const [actsError, setActsError] = useState<string | null>(null);
  const [actsLoaded, setActsLoaded] = useState(false);

  const [actModalOpen, setActModalOpen] = useState(false);
  const [actModalMode, setActModalMode] = useState<'create' | 'edit'>('create');
  const [selectedAct, setSelectedAct] = useState<Act | null>(null);
  const [actSubmitting, setActSubmitting] = useState(false);
  const [actModalError, setActModalError] = useState<string | null>(null);
  const [actClients, setActClients] = useState<Client[]>([]);
  const [actResponsibles, setActResponsibles] = useState<ActResponsible[]>([]);
  const [actLookupsLoading, setActLookupsLoading] = useState(false);
  const [actLookupsError, setActLookupsError] = useState<string | null>(null);

  const [caseModalOpen, setCaseModalOpen] = useState(false);
  const [editingCase, setEditingCase] = useState<ClientCase | null>(null);
  const [caseMode, setCaseMode] = useState<'create' | 'edit'>('create');
  const [activeSection, setActiveSection] = useState<ClientDetailSection>('payments');

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

  const actModalClients = useMemo(() => {
    const normalizedName = clientName && clientName !== '...' ? clientName : `Клиент #${clientId}`;
    if (actClients.length === 0) {
      return [
        {
          id: clientId,
          name: normalizedName,
          email: '',
          phone: '',
          company: '',
          address: '',
          notes: '',
          createdAt: '1970-01-01T00:00:00.000Z',
          isActive: true,
        } satisfies Client,
      ];
    }

    const hasClient = actClients.some((c) => c.id === clientId);
    if (!hasClient) {
      return [
        ...actClients,
        {
          id: clientId,
          name: normalizedName,
          email: '',
          phone: '',
          company: '',
          address: '',
          notes: '',
          createdAt: '1970-01-01T00:00:00.000Z',
          isActive: true,
        } satisfies Client,
      ];
    }

    return actClients;
  }, [actClients, clientId, clientName]);

  const fetchActs = useCallback(async () => {
    setActsLoading(true);
    try {
      const response = await apiService.getActs({
        clientId,
        page: 1,
        pageSize: 100,
        sortBy: 'date',
        sortDir: 'desc',
      });
      setActs(response?.items ?? []);
      setActsError(null);
    } catch (error) {
      setActsError(error instanceof Error ? error.message : 'Не удалось загрузить акты');
    } finally {
      setActsLoading(false);
      setActsLoaded(true);
    }
  }, [clientId]);

  useEffect(() => {
    void fetchActs();
  }, [fetchActs]);

  const ensureActLookups = useCallback(async () => {
    if (actLookupsLoading) return;
    if (actClients.length > 0 && actResponsibles.length > 0 && !actLookupsError) return;
    setActLookupsLoading(true);
    try {
      const [clientsResponse, responsiblesResponse] = await Promise.all([
        apiService.getClients(),
        apiService.getActResponsibles(),
      ]);
      setActClients(clientsResponse ?? []);
      setActResponsibles(responsiblesResponse ?? []);
      setActLookupsError(null);
    } catch (error) {
      setActLookupsError(error instanceof Error ? error.message : 'Не удалось загрузить справочники');
    } finally {
      setActLookupsLoading(false);
    }
  }, [actClients.length, actResponsibles.length, actLookupsError, actLookupsLoading]);

  useEffect(() => {
    if (actModalOpen) {
      void ensureActLookups();
    }
  }, [actModalOpen, ensureActLookups]);

  const openAddPayment = () => {
    setActiveSection('payments');
    setEditingPayment(null);
    setPayModalOpen(true);
  };
  const openEditPayment = (p: Payment) => {
    setActiveSection('payments');
    setEditingPayment(p);
    setPayModalOpen(true);
  };
  const openAddAct = () => {
    setActiveSection('acts');
    setActModalMode('create');
    setSelectedAct(null);
    setActModalError(null);
    setActModalOpen(true);
  };
  const openEditAct = (act: Act) => {
    setActiveSection('acts');
    setActModalMode('edit');
    setSelectedAct(act);
    setActModalError(null);
    setActModalOpen(true);
  };
  const notifyFeatureInProgress = (section: ClientDetailSection) => {
    const meta = SECTION_META[section];
    alert(`${meta.label} пока в разработке.`);
  };
  const closePayModal = () => {
    setPayModalOpen(false);
    setEditingPayment(null);
  };
  const closeActModal = () => {
    setActModalOpen(false);
    setSelectedAct(null);
    setActModalError(null);
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

  const submitAct = async (payload: ActInput) => {
    const resolvedClientId = payload.clientId ?? clientId;
    if (!resolvedClientId) {
      setActModalError('Клиент не выбран');
      return;
    }

    setActSubmitting(true);
    setActModalError(null);
    const body: ActInput = {
      ...payload,
      clientId: resolvedClientId,
    };

    try {
      if (actModalMode === 'edit' && selectedAct) {
        await apiService.updateAct(selectedAct.id, body);
      } else {
        await apiService.createAct(body);
      }
      await fetchActs();
      closeActModal();
    } catch (error) {
      setActModalError(error instanceof Error ? error.message : 'Не удалось сохранить акт');
    } finally {
      setActSubmitting(false);
    }
  };

  const removeAct = async (act: Act) => {
    if (!window.confirm(`Удалить акт №${act.number}?`)) return;
    try {
      await apiService.deleteAct(act.id);
      await fetchActs();
    } catch (error) {
      alert(error instanceof Error ? error.message : 'Не удалось удалить акт');
    }
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

        {activeSection === 'payments' ? (
          <>
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
                    <Plus size={16} /> {SECTION_META.payments.actionLabel}
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
          </>
        ) : null}

        <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between mb-4">
          <div className="flex flex-wrap gap-2">
            {SECTION_ORDER.map((section) => {
              const meta = SECTION_META[section];
              const isActive = activeSection === section;
              return (
                <button
                  key={section}
                  type="button"
                  onClick={() => setActiveSection(section)}
                  className={`px-3 py-2 text-sm font-medium rounded-lg border transition-colors ${
                    isActive
                      ? 'bg-emerald-600 text-white border-emerald-600 shadow-sm'
                      : 'bg-white text-gray-600 border-gray-200 hover:bg-gray-50'
                  }`}>
                  {meta.label}
                </button>
              );
            })}
          </div>

          <div className="flex flex-wrap gap-2 justify-start sm:justify-end">
            <button
              type="button"
              onClick={() => {
                setActiveSection('payments');
                openAddPayment();
              }}
              className="inline-flex items-center justify-center gap-2 rounded-lg bg-blue-600 px-3 py-2 text-sm font-medium text-white hover:bg-blue-700">
              <Plus size={16} /> {SECTION_META.payments.actionLabel}
            </button>
            <button
              type="button"
              onClick={() => {
                setActiveSection('accounts');
                notifyFeatureInProgress('accounts');
              }}
              className="inline-flex items-center justify-center gap-2 rounded-lg border border-gray-300 px-3 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50">
              <Plus size={16} /> {SECTION_META.accounts.actionLabel}
            </button>
            <button
              type="button"
              onClick={openAddAct}
              className="inline-flex items-center justify-center gap-2 rounded-lg bg-emerald-600 px-3 py-2 text-sm font-medium text-white hover:bg-emerald-700">
              <Plus size={16} /> {SECTION_META.acts.actionLabel}
            </button>
            <button
              type="button"
              onClick={() => {
                setActiveSection('contracts');
                notifyFeatureInProgress('contracts');
              }}
              className="inline-flex items-center justify-center gap-2 rounded-lg border border-gray-300 px-3 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50">
              <Plus size={16} /> {SECTION_META.contracts.actionLabel}
            </button>
            <button
              type="button"
              onClick={() => {
                setActiveSection('settlement');
                notifyFeatureInProgress('settlement');
              }}
              className="inline-flex items-center justify-center gap-2 rounded-lg border border-gray-300 px-3 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50">
              <Calculator size={16} /> {SECTION_META.settlement.actionLabel}
            </button>
          </div>
        </div>

        <div className="bg-white rounded-xl shadow-sm border border-gray-100">
          <div className="p-6 border-b border-gray-100">
            <div className="flex items-center justify-between">
              <h2 className="text-xl font-semibold text-gray-900">
                {SECTION_META[activeSection].label}
              </h2>
              {activeSection === 'payments' && lastPaymentDate && (
                <div className="flex items-center gap-2 text-sm text-gray-500">
                  <CalendarIcon size={16} />
                  Последний платёж: {toRuDate(lastPaymentDate)}
                </div>
              )}
            </div>
          </div>

          <div className="p-6">
            {activeSection === 'payments' ? (
              loadingPayments ? (
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
              )
            ) : activeSection === 'acts' ? (
              actsLoading && !actsLoaded ? (
                <div className="flex items-center justify-center gap-2 py-8 text-gray-500">
                  <Loader2 className="h-5 w-5 animate-spin" />
                  Загрузка актов...
                </div>
              ) : actsError ? (
                <div className="rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-700">
                  {actsError}
                </div>
              ) : acts.length === 0 ? (
                <div className="text-center py-12">
                  <FileCheck2 size={48} className="mx-auto mb-4 text-gray-300" />
                  <h3 className="text-lg font-medium text-gray-900 mb-2">
                    {SECTION_META.acts.placeholderTitle}
                  </h3>
                  <p className="text-gray-500">{SECTION_META.acts.placeholderDescription}</p>
                  <button
                    type="button"
                    onClick={openAddAct}
                    className="mt-6 inline-flex items-center justify-center gap-2 rounded-lg bg-emerald-600 px-4 py-2 text-sm font-medium text-white hover:bg-emerald-700">
                    <Plus size={16} /> {SECTION_META.acts.actionLabel}
                  </button>
                </div>
              ) : (
                <>
                  {actsLoading && (
                    <div className="mb-4 flex items-center gap-2 text-sm text-gray-500">
                      <Loader2 className="h-4 w-4 animate-spin" />
                      Обновление списка актов...
                    </div>
                  )}
                  <div className="space-y-4">
                    {acts.map((act) => {
                      const title = act.title?.trim();
                      return (
                        <div
                          key={act.id}
                          className="rounded-xl border border-gray-200 bg-white p-4 shadow-sm">
                          <div className="flex flex-col gap-4 lg:flex-row lg:items-start lg:justify-between">
                            <div className="min-w-0">
                              <p className="text-xs font-semibold uppercase tracking-wide text-gray-500">
                                Акт №{act.number}
                              </p>
                              <p className="text-lg font-semibold text-gray-900">
                                {title && title !== `№${act.number}` ? title : `Акт №${act.number}`}
                              </p>
                              {act.comment && (
                                <p className="mt-2 text-sm text-gray-500">{act.comment}</p>
                              )}
                              {act.invoiceNumber && (
                                <p className="mt-1 text-xs text-gray-500">
                                  Счёт {act.invoiceNumber}
                                </p>
                              )}
                            </div>
                            <div className="flex flex-wrap items-center gap-3 lg:justify-end">
                              <span
                                className={`inline-flex items-center rounded-full px-3 py-1 text-sm font-medium ${ACT_STATUS_CLASSES[act.status]}`}>
                                {ACT_STATUS_LABELS[act.status]}
                              </span>
                              <div className="text-right text-sm text-gray-600">
                                <span className="block font-medium text-gray-900">
                                  {toRuDate(act.date)}
                                </span>
                                <span className="block text-xs text-gray-500">Дата</span>
                              </div>
                              <div className="text-right text-sm text-gray-600">
                                <span className="block font-medium text-gray-900">
                                  {formatCurrencySmart(act.amount).full}
                                </span>
                                <span className="block text-xs text-gray-500">Сумма</span>
                              </div>
                              <div className="text-right text-sm text-gray-600">
                                <span className="block font-medium text-gray-900">
                                  {act.responsibleName ?? 'Не назначен'}
                                </span>
                                <span className="block text-xs text-gray-500">Ответственный</span>
                              </div>
                              <div className="flex items-center gap-2">
                                <button
                                  type="button"
                                  onClick={() => openEditAct(act)}
                                  className="inline-flex h-8 w-8 items-center justify-center rounded-full border border-gray-200 text-gray-600 transition-colors hover:bg-gray-50 hover:text-gray-900"
                                  title="Редактировать акт"
                                  aria-label="Редактировать акт">
                                  <Edit size={16} />
                                </button>
                                <button
                                  type="button"
                                  onClick={() => removeAct(act)}
                                  className="inline-flex h-8 w-8 items-center justify-center rounded-full border border-gray-200 text-red-600 transition-colors hover:bg-red-50"
                                  title="Удалить акт"
                                  aria-label="Удалить акт">
                                  <Trash2 size={16} />
                                </button>
                              </div>
                            </div>
                          </div>
                        </div>
                      );
                    })}
                  </div>
                </>
              )
            ) : (
              (() => {
                const meta = SECTION_META[activeSection];
                if (!meta.placeholderTitle) {
                  return null;
                }
                const PlaceholderIcon = meta.icon;
                return (
                  <div className="text-center py-12">
                    {PlaceholderIcon ? (
                      <PlaceholderIcon size={48} className="mx-auto mb-4 text-gray-300" />
                    ) : null}
                    <h3 className="text-lg font-medium text-gray-900 mb-2">{meta.placeholderTitle}</h3>
                    <p className="text-gray-500">{meta.placeholderDescription}</p>
                  </div>
                );
              })()
            )}
          </div>
        </div>

        <ActModal
          open={actModalOpen}
          mode={actModalMode}
          act={selectedAct}
          onClose={closeActModal}
          onSubmit={submitAct}
          submitting={actSubmitting}
          errorMessage={actModalError}
          clients={actModalClients}
          responsibles={actResponsibles}
          lookupsLoading={actLookupsLoading}
          lookupsError={actLookupsError}
          defaultClientId={clientId}
        />

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
