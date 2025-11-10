import {
  ArrowLeft,
  Ban,
  Users,
  TrendingUp,
  TrendingDown,
  Calendar as CalendarIcon,
  CheckCircle,
  Clock,
  AlertTriangle,
  CalendarRange,
  Loader2,
  Plus,
  Edit,
  Trash2,
  PlusCircle,
  RotateCcw,
  Search,
  WalletCards,
  CheckCircle2,
  Send,
  FileCheck2,
  FileSignature,
  Calculator,
} from 'lucide-react';
import type { LucideIcon } from 'lucide-react';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useClientCases } from '../../hooks/useClientCases';
import { useDebouncedValue } from '../../hooks/useDebouncedValue';
import { useActs, type ActsSortKey } from '../../hooks/useActs';
import { useInvoices, type InvoicesSortKey } from '../../hooks/useInvoices';
import { apiService } from '../../services/api';
import { usePayments } from '../../hooks/usePayments';
import { useTranslation } from '../../hooks/useTranslation';
import { PaymentModal } from '../Calendar/PaymentModal';
import type {
  Act,
  ActInput,
  ActResponsible,
  ActStatus,
  Client,
  ClientCase,
  Invoice,
  InvoiceInput,
  Payment,
  PaymentStatus,
} from '../../types';
import { toRuDate, formatLocalYMD } from '../../utils/dateUtils';
import { MonthRangePicker } from '../MonthRange/MonthRangePicker';
import { CaseModal } from './CaseModal';
import { TwoTypeStats } from '../Statistics/TwoTypeStats';
import { formatCurrencySmart } from '../../utils/formatters';
import { ActModal } from '../Acts/ActModal';
import { InvoiceModal } from '../Accounts/InvoiceModal';

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

const ACT_STATUS_ORDER: ActStatus[] = ['Created', 'Transferred', 'Signed', 'Terminated'];

const ACT_STATUS_CLASSES: Record<ActStatus, string> = {
  Created: 'bg-slate-100 text-slate-700 border border-slate-200',
  Transferred: 'bg-amber-100 text-amber-800 border border-amber-200',
  Signed: 'bg-emerald-100 text-emerald-700 border border-emerald-200',
  Terminated: 'bg-rose-100 text-rose-700 border border-rose-200',
};

const INVOICE_STATUS_ORDER: PaymentStatus[] = [
  'Pending',
  'Overdue',
  'Completed',
  'Processing',
  'Cancelled',
];

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
  const { t } = useTranslation();
  const [clientName, setClientName] = useState<string>('...');
  const [selectedCaseId, setSelectedCaseId] = useState<number | 'all'>(initialCaseId ?? 'all');

  const { cases: serverCases } = useClientCases(clientId);
  const [casesLocal, setCasesLocal] = useState<ClientCase[] | null>(null);
  useEffect(() => setCasesLocal(serverCases), [serverCases]);
  const cases = useMemo(() => casesLocal ?? serverCases, [casesLocal, serverCases]);

  const [monthFrom, setMonthFrom] = useState<string>('');
  const [monthTo, setMonthTo] = useState<string>('');
  const [statusFilter, setStatusFilter] = useState<'all' | NormalizedStatus>('all');

  const [actFrom, setActFrom] = useState<string>(() =>
    formatLocalYMD(new Date(new Date().getFullYear(), 0, 1)),
  );
  const [actTo, setActTo] = useState<string>(() => formatLocalYMD(new Date()));
  const [actStatusFilter, setActStatusFilter] = useState<'all' | ActStatus>('all');
  const [actSearch, setActSearch] = useState('');
  const debouncedActSearch = useDebouncedValue(actSearch.trim(), 400);
  const [actSort, setActSort] = useState<{ key: ActsSortKey; direction: 'asc' | 'desc' }>(
    { key: 'date', direction: 'desc' },
  );
  const [actPage, setActPage] = useState(1);
  const actPageSize = 10;

  const [payModalOpen, setPayModalOpen] = useState(false);
  const [editingPayment, setEditingPayment] = useState<Payment | null>(null);

  const [actModalOpen, setActModalOpen] = useState(false);
  const [actModalMode, setActModalMode] = useState<'create' | 'edit'>('create');
  const [selectedAct, setSelectedAct] = useState<Act | null>(null);
  const [actSubmitting, setActSubmitting] = useState(false);
  const [actModalError, setActModalError] = useState<string | null>(null);
  const [actClients, setActClients] = useState<Client[]>([]);
  const [actResponsibles, setActResponsibles] = useState<ActResponsible[]>([]);
  const [actLookupsLoading, setActLookupsLoading] = useState(false);
  const [actLookupsError, setActLookupsError] = useState<string | null>(null);

  const today = useMemo(() => new Date(), []);
  const startOfYear = useMemo(
    () => formatLocalYMD(new Date(today.getFullYear(), 0, 1)),
    [today],
  );
  const todayYMD = useMemo(() => formatLocalYMD(today), [today]);

  const [invoiceFrom, setInvoiceFrom] = useState<string>(startOfYear);
  const [invoiceTo, setInvoiceTo] = useState<string>(todayYMD);
  const [invoiceStatus, setInvoiceStatus] = useState<'all' | PaymentStatus>('all');
  const [invoiceSearch, setInvoiceSearch] = useState('');
  const debouncedInvoiceSearch = useDebouncedValue(invoiceSearch.trim(), 400);
  const [invoiceSort, setInvoiceSort] = useState<{ key: InvoicesSortKey; direction: 'asc' | 'desc' }>({
    key: 'date',
    direction: 'desc',
  });
  const [invoicePage, setInvoicePage] = useState(1);
  const invoicePageSize = 15;

  const [invoiceModalOpen, setInvoiceModalOpen] = useState(false);
  const [invoiceModalMode, setInvoiceModalMode] = useState<'create' | 'edit'>('create');
  const [selectedInvoice, setSelectedInvoice] = useState<Invoice | null>(null);
  const [invoiceSubmitting, setInvoiceSubmitting] = useState(false);
  const [invoiceModalError, setInvoiceModalError] = useState<string | null>(null);
  const [invoiceClients, setInvoiceClients] = useState<Client[]>([]);
  const [invoiceLookupsLoading, setInvoiceLookupsLoading] = useState(false);
  const [invoiceLookupsError, setInvoiceLookupsError] = useState<string | null>(null);

  const {
    acts: clientActs,
    loading: actsLoading,
    refreshing: actsRefreshing,
    error: actsError,
    summary: actsSummary,
    pagination: actsPagination,
    refresh: refreshActs,
    createAct,
    updateAct,
    deleteAct,
  } = useActs({
    from: actFrom || undefined,
    to: actTo || undefined,
    status: actStatusFilter === 'all' ? undefined : actStatusFilter,
    clientId,
    search: debouncedActSearch || undefined,
    sortBy: actSort.key,
    sortDir: actSort.direction,
    page: actPage,
    pageSize: actPageSize,
  });

  useEffect(() => {
    setActPage(1);
  }, [actFrom, actTo, actStatusFilter, debouncedActSearch, clientId]);

  const {
    invoices: clientInvoices,
    loading: invoicesLoading,
    refreshing: invoicesRefreshing,
    error: invoicesError,
    summary: invoicesSummary,
    pagination: invoicesPagination,
    refresh: refreshInvoices,
    createInvoice: createClientInvoice,
    updateInvoice: updateClientInvoice,
    deleteInvoice: deleteClientInvoice,
  } = useInvoices({
    from: invoiceFrom || undefined,
    to: invoiceTo || undefined,
    status: invoiceStatus === 'all' ? undefined : invoiceStatus,
    clientId,
    search: debouncedInvoiceSearch || undefined,
    sortBy: invoiceSort.key,
    sortDir: invoiceSort.direction,
    page: invoicePage,
    pageSize: invoicePageSize,
  });

  useEffect(() => {
    setInvoicePage(1);
  }, [invoiceFrom, invoiceTo, invoiceStatus, debouncedInvoiceSearch, clientId]);

  const invoiceTotalPages = useMemo(() => {
    if (!invoicesPagination.pageSize) return 1;
    return Math.max(1, Math.ceil((invoicesPagination.total ?? 0) / invoicesPagination.pageSize));
  }, [invoicesPagination.pageSize, invoicesPagination.total]);

  const invoiceSummaryBuckets = invoicesSummary ?? {
    total: { amount: 0, count: 0 },
    pending: { amount: 0, count: 0 },
    paid: { amount: 0, count: 0 },
    overdue: { amount: 0, count: 0 },
  };

  const invoiceStatusLabels: Record<PaymentStatus, string> = {
    Pending: t('invoicePendingBadge') ?? t('pending') ?? 'Ожидается',
    Completed: t('invoicePaidBadge') ?? t('completedStatus') ?? 'Оплачено',
    Overdue: t('invoiceOverdueBadge') ?? t('overdue') ?? 'Просрочено',
    Processing: t('processingStatus') ?? 'В обработке',
    Cancelled: t('cancelledStatus') ?? 'Отменено',
  };

  const invoiceStatusClasses: Record<PaymentStatus, string> = {
    Pending: 'bg-amber-100 text-amber-700 border border-amber-200',
    Completed: 'bg-emerald-100 text-emerald-700 border border-emerald-200',
    Overdue: 'bg-rose-100 text-rose-700 border border-rose-200',
    Processing: 'bg-blue-100 text-blue-700 border border-blue-200',
    Cancelled: 'bg-slate-100 text-slate-600 border border-slate-200',
  };

  const openInvoiceCreate = () => {
    setInvoiceModalMode('create');
    setSelectedInvoice(null);
    setInvoiceModalError(null);
    setInvoiceModalOpen(true);
  };

  const openInvoiceEdit = (invoice: Invoice) => {
    setInvoiceModalMode('edit');
    setSelectedInvoice(invoice);
    setInvoiceModalError(null);
    setInvoiceModalOpen(true);
  };

  const closeInvoiceModal = () => {
    setInvoiceModalOpen(false);
    setSelectedInvoice(null);
    setInvoiceModalError(null);
  };

  const ensureInvoiceClients = useCallback(async () => {
    if (!invoiceModalOpen) return;
    if (invoiceClients.length > 0 && !invoiceLookupsError) return;
    setInvoiceLookupsLoading(true);
    try {
      const list = await apiService.getClients();
      setInvoiceClients(list ?? []);
      setInvoiceLookupsError(null);
    } catch (err) {
      setInvoiceLookupsError(
        err instanceof Error ? err.message : t('clientsLoadError') ?? 'Не удалось загрузить клиентов',
      );
    } finally {
      setInvoiceLookupsLoading(false);
    }
  }, [invoiceModalOpen, invoiceClients.length, invoiceLookupsError, t]);

  useEffect(() => {
    if (invoiceModalOpen) {
      void ensureInvoiceClients();
    }
  }, [invoiceModalOpen, ensureInvoiceClients]);

  const submitInvoice = async (payload: InvoiceInput) => {
    setInvoiceSubmitting(true);
    setInvoiceModalError(null);
    try {
      if (invoiceModalMode === 'edit' && selectedInvoice) {
        await updateClientInvoice(selectedInvoice.id, payload);
      } else {
        await createClientInvoice(payload);
      }
      closeInvoiceModal();
    } catch (err) {
      setInvoiceModalError(err instanceof Error ? err.message : 'Не удалось сохранить счёт');
    } finally {
      setInvoiceSubmitting(false);
    }
  };

  const removeInvoice = async (invoice: Invoice) => {
    if (!window.confirm(t('invoiceDeleteConfirm') ?? 'Удалить счёт?')) return;
    try {
      await deleteClientInvoice(invoice.id);
    } catch (err) {
      window.alert(err instanceof Error ? err.message : 'Не удалось удалить счёт');
    }
  };

  const resetInvoiceFilters = () => {
    setInvoiceFrom(startOfYear);
    setInvoiceTo(todayYMD);
    setInvoiceStatus('all');
    setInvoiceSearch('');
  };

  const handleInvoiceSort = (key: InvoicesSortKey) => {
    setInvoiceSort((prev) => {
      if (prev.key === key) {
        return { key, direction: prev.direction === 'asc' ? 'desc' : 'asc' };
      }
      return { key, direction: key === 'date' || key === 'createdAt' ? 'desc' : 'asc' };
    });
    setInvoicePage(1);
  };

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

  const invoiceModalClients = useMemo(() => {
    const normalizedName = clientName && clientName !== '...' ? clientName : `Клиент #${clientId}`;
    if (invoiceClients.length === 0) {
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

    const hasClient = invoiceClients.some((c) => c.id === clientId);
    if (!hasClient) {
      return [
        ...invoiceClients,
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

    return invoiceClients;
  }, [invoiceClients, clientId, clientName]);

  const actSummaryBuckets = useMemo(
    () =>
      actsSummary ?? {
        created: { amount: 0, count: 0 },
        transferred: { amount: 0, count: 0 },
        signed: { amount: 0, count: 0 },
        terminated: { amount: 0, count: 0 },
        totalAmount: 0,
        totalCount: 0,
      },
    [actsSummary],
  );

  const actSummaryCards = useMemo(
    () => [
      {
        key: 'signed' as const,
        title: t('actSummarySigned') ?? 'Подписано',
        bucket: actSummaryBuckets.signed,
        icon: CheckCircle2,
        accent: 'text-emerald-700 bg-emerald-50 border border-emerald-100',
      },
      {
        key: 'transferred' as const,
        title: t('actSummaryTransferred') ?? 'Передано',
        bucket: actSummaryBuckets.transferred,
        icon: Send,
        accent: 'text-amber-700 bg-amber-50 border border-amber-100',
      },
      {
        key: 'terminated' as const,
        title: t('actSummaryTerminated') ?? 'Расторгнуто',
        bucket: actSummaryBuckets.terminated,
        icon: Ban,
        accent: 'text-rose-700 bg-rose-50 border border-rose-100',
      },
    ],
    [actSummaryBuckets, t],
  );

  const actStatusLabels = useMemo<Record<ActStatus, string>>(
    () => ({
      Created: t('actStatusCreated') ?? 'Создан',
      Transferred: t('actStatusTransferred') ?? 'Передано',
      Signed: t('actStatusSigned') ?? 'Подписано',
      Terminated: t('actStatusTerminated') ?? 'Расторгнуто',
    }),
    [t],
  );

  const actTotalPages = useMemo(() => {
    const size = actsPagination.pageSize || actPageSize;
    if (!size) return 1;
    return Math.max(1, Math.ceil((actsPagination.total ?? 0) / size));
  }, [actsPagination.pageSize, actsPagination.total, actPageSize]);

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
        await updateAct(selectedAct.id, body);
      } else {
        await createAct(body);
      }
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
      await deleteAct(act.id);
    } catch (error) {
      alert(error instanceof Error ? error.message : 'Не удалось удалить акт');
    }
  };

  const handleActSort = (key: ActsSortKey) => {
    setActSort((prev) => {
      if (prev.key === key) {
        return { key, direction: prev.direction === 'asc' ? 'desc' : 'asc' };
      }
      return { key, direction: key === 'date' || key === 'createdAt' ? 'desc' : 'asc' };
    });
    setActPage(1);
  };

  const resetActFilters = () => {
    setActFrom(formatLocalYMD(new Date(new Date().getFullYear(), 0, 1)));
    setActTo(formatLocalYMD(new Date()));
    setActStatusFilter('all');
    setActSearch('');
    setActPage(1);
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
        ) : activeSection === 'accounts' ? (
          <>
            <div className="grid gap-4 mb-6 md:grid-cols-3">
              {[
                {
                  key: 'total',
                  title: t('invoiceSummaryTotal') ?? 'Сумма счетов всего',
                  value: invoiceSummaryBuckets.total.amount,
                  count: invoiceSummaryBuckets.total.count,
                  accent: 'border border-blue-100 bg-blue-50 text-blue-700',
                  icon: WalletCards,
                },
                {
                  key: 'overdue',
                  title: t('invoiceSummaryOverdue') ?? 'Просрочено',
                  value: invoiceSummaryBuckets.overdue.amount,
                  count: invoiceSummaryBuckets.overdue.count,
                  accent: 'border border-rose-100 bg-rose-50 text-rose-700',
                  icon: AlertTriangle,
                },
                {
                  key: 'paid',
                  title: t('invoiceSummaryPaid') ?? 'Оплачено',
                  value: invoiceSummaryBuckets.paid.amount,
                  count: invoiceSummaryBuckets.paid.count,
                  accent: 'border border-emerald-100 bg-emerald-50 text-emerald-700',
                  icon: CheckCircle2,
                },
              ].map((card) => {
                const Icon = card.icon;
                return (
                  <div key={card.key} className={`rounded-2xl p-5 shadow-sm ${card.accent}`}>
                    <div className="flex items-center justify-between">
                      <div>
                        <p className="text-sm font-medium">{card.title}</p>
                        <p className="mt-2 text-2xl font-semibold">
                          {formatCurrencySmart(card.value).full}
                        </p>
                      </div>
                      <Icon className="h-10 w-10 opacity-80" />
                    </div>
                    <p className="mt-4 text-xs">
                      {(t('invoiceSummaryCount') ?? 'Количество счетов') + ': '} {card.count}
                    </p>
                  </div>
                );
              })}
            </div>

            <div className="mb-6 flex flex-wrap items-center gap-3 rounded-xl border border-gray-200 bg-white p-4 shadow-sm">
              <div className="flex items-center gap-2">
                <CalendarRange className="h-4 w-4 text-gray-500" />
                <input
                  type="date"
                  value={invoiceFrom}
                  onChange={(e) => setInvoiceFrom(e.target.value)}
                  className="w-36 rounded-lg border border-gray-300 px-3 py-2 text-sm focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-200"
                />
                <span className="text-gray-500">—</span>
                <input
                  type="date"
                  value={invoiceTo}
                  onChange={(e) => setInvoiceTo(e.target.value)}
                  className="w-36 rounded-lg border border-gray-300 px-3 py-2 text-sm focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-200"
                />
              </div>

              <div>
                <select
                  value={invoiceStatus}
                  onChange={(e) => setInvoiceStatus(e.target.value as 'all' | PaymentStatus)}
                  className="rounded-lg border border-gray-300 px-3 py-2 text-sm focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-200"
                >
                  <option value="all">{t('allStatuses') ?? 'Все статусы'}</option>
                  {INVOICE_STATUS_ORDER.map((status) => (
                    <option key={status} value={status}>
                      {invoiceStatusLabels[status]}
                    </option>
                  ))}
                </select>
              </div>

              <div className="relative flex-1 min-w-[220px]">
                <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-gray-400" />
                <input
                  type="search"
                  value={invoiceSearch}
                  onChange={(e) => setInvoiceSearch(e.target.value)}
                  placeholder={t('invoiceSearchPlaceholder') ?? 'Номер счёта или акт'}
                  className="w-full rounded-lg border border-gray-300 pl-9 pr-3 py-2 text-sm focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-200"
                />
              </div>

              <div className="flex items-center gap-2 ml-auto">
                <button
                  type="button"
                  onClick={() => refreshInvoices()}
                  className="inline-flex items-center gap-2 rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-700 hover:bg-gray-50"
                >
                  <RotateCcw className={invoicesRefreshing ? 'h-4 w-4 animate-spin' : 'h-4 w-4'} />
                  {t('invoiceRefresh') ?? 'Обновить'}
                </button>
                <button
                  type="button"
                  onClick={resetInvoiceFilters}
                  className="inline-flex items-center gap-2 rounded-lg border border-gray-200 px-3 py-2 text-sm text-gray-600 hover:bg-gray-50"
                >
                  {t('invoiceFiltersReset') ?? 'Сбросить'}
                </button>
                <button
                  type="button"
                  onClick={openInvoiceCreate}
                  className="inline-flex items-center gap-2 rounded-lg bg-emerald-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-emerald-700"
                >
                  <Plus className="h-4 w-4" /> {t('invoiceAdd') ?? 'Добавить счёт'}
                </button>
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
            ) : activeSection === 'accounts' ? (
              <>
                {invoicesLoading ? (
                  <div className="py-10 text-center text-gray-500">
                    <div className="inline-flex items-center gap-2">
                      <Loader2 className="h-4 w-4 animate-spin" />
                      {t('loading') ?? 'Загрузка...'}
                    </div>
                  </div>
                ) : clientInvoices.length === 0 ? (
                  <div className="py-10 text-center text-gray-500">
                    {invoicesError ?? t('invoicesEmpty') ?? 'Счета не найдены'}
                  </div>
                ) : (
                  <div className="overflow-x-auto">
                    <table className="min-w-full divide-y divide-gray-200">
                      <thead className="bg-gray-50">
                        <tr className="text-left text-xs font-semibold uppercase tracking-wide text-gray-500">
                          {[
                            { key: 'date' as InvoicesSortKey, label: t('invoiceDate') ?? 'Дата' },
                            { key: 'number' as InvoicesSortKey, label: t('invoiceNumber') ?? 'Номер счёта' },
                            { key: 'amount' as InvoicesSortKey, label: t('invoiceAmount') ?? 'Сумма' },
                            { key: 'status' as InvoicesSortKey, label: t('invoiceStatus') ?? 'Статус' },
                            { key: 'dueDate' as InvoicesSortKey, label: t('invoiceDueDate') ?? 'Срок оплаты' },
                            { key: 'responsible' as InvoicesSortKey, label: t('invoiceResponsible') ?? 'Ответственный' },
                            { key: 'createdAt' as InvoicesSortKey, label: t('createdAt') ?? 'Создан' },
                            { key: 'number' as InvoicesSortKey, label: t('actions') ?? 'Действия', sortable: false },
                          ].map((column) => {
                            const isSortable = column.sortable !== false;
                            const isActive = invoiceSort.key === column.key;
                            const direction = isActive ? invoiceSort.direction : undefined;
                            return (
                              <th key={column.label} scope="col" className="px-4 py-3">
                                {isSortable ? (
                                  <button
                                    type="button"
                                    onClick={() => handleInvoiceSort(column.key)}
                                    className="flex items-center gap-1 text-xs font-semibold uppercase tracking-wide text-gray-500"
                                  >
                                    {column.label}
                                    <span className="text-gray-400">
                                      {isActive ? (direction === 'asc' ? '▲' : '▼') : ''}
                                    </span>
                                  </button>
                                ) : (
                                  <span>{column.label}</span>
                                )}
                              </th>
                            );
                          })}
                        </tr>
                      </thead>
                      <tbody className="divide-y divide-gray-200 text-sm text-gray-700">
                        {clientInvoices.map((invoice) => (
                          <tr key={invoice.id} className="hover:bg-gray-50/80">
                            <td className="px-4 py-3 font-medium text-gray-900">{toRuDate(invoice.date)}</td>
                            <td className="px-4 py-3">
                              <div className="font-medium text-gray-900">№{invoice.number}</div>
                              {invoice.clientCaseTitle ? (
                                <div className="text-xs text-gray-500">{invoice.clientCaseTitle}</div>
                              ) : null}
                              {invoice.actNumber ? (
                                <div className="text-xs text-gray-500">
                                  {(t('invoiceAct') ?? 'Акт')} №{invoice.actNumber}
                                </div>
                              ) : null}
                            </td>
                            <td className="px-4 py-3 font-semibold text-gray-900">
                              {formatCurrencySmart(invoice.amount).full}
                            </td>
                            <td className="px-4 py-3">
                              <span
                                className={`inline-flex items-center rounded-full px-3 py-1 text-xs font-medium ${invoiceStatusClasses[invoice.status]}`}
                              >
                                {invoiceStatusLabels[invoice.status]}
                              </span>
                            </td>
                            <td className="px-4 py-3 text-gray-900">
                              {invoice.dueDate ? toRuDate(invoice.dueDate) : '—'}
                              {invoice.paidDate ? (
                                <div className="text-xs text-emerald-600">
                                  {(t('invoicePaidOn') ?? 'Оплачен') + ': '} {toRuDate(invoice.paidDate)}
                                </div>
                              ) : null}
                            </td>
                            <td className="px-4 py-3 text-gray-900">{invoice.responsibleName ?? '—'}</td>
                            <td className="px-4 py-3 text-gray-900">{toRuDate(invoice.createdAt)}</td>
                            <td className="px-4 py-3">
                              <div className="flex items-center gap-2">
                                <button
                                  type="button"
                                  onClick={() => openInvoiceEdit(invoice)}
                                  className="inline-flex h-8 w-8 items-center justify-center rounded-full border border-gray-200 text-gray-600 hover:bg-gray-50"
                                  title={t('edit') ?? 'Редактировать'}
                                  aria-label={t('edit') ?? 'Редактировать'}
                                >
                                  <Pencil className="h-4 w-4" />
                                </button>
                                <button
                                  type="button"
                                  onClick={() => removeInvoice(invoice)}
                                  className="inline-flex h-8 w-8 items-center justify-center rounded-full border border-gray-200 text-red-600 hover:bg-red-50"
                                  title={t('delete') ?? 'Удалить'}
                                  aria-label={t('delete') ?? 'Удалить'}
                                >
                                  <Trash2 className="h-4 w-4" />
                                </button>
                              </div>
                            </td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                )}
                {clientInvoices.length > 0 && !invoicesLoading ? (
                  <div className="flex items-center justify-between border-t border-gray-200 bg-gray-50 px-4 py-3 text-sm text-gray-600">
                    <div>
                      {(t('recordsFound') ?? 'Найдено записей')}: {invoicesPagination.total}
                    </div>
                    <div className="flex items-center gap-2">
                      <button
                        type="button"
                        onClick={() => setInvoicePage((value) => Math.max(1, value - 1))}
                        disabled={invoicePage <= 1}
                        className="rounded-lg border border-gray-200 px-3 py-1 disabled:cursor-not-allowed disabled:opacity-50"
                      >
                        {t('previous') ?? 'Назад'}
                      </button>
                      <span>
                        {invoicesPagination.page} / {invoiceTotalPages}
                      </span>
                      <button
                        type="button"
                        onClick={() => setInvoicePage((value) => Math.min(invoiceTotalPages, value + 1))}
                        disabled={invoicePage >= invoiceTotalPages}
                        className="rounded-lg border border-gray-200 px-3 py-1 disabled:cursor-not-allowed disabled:opacity-50"
                      >
                        {t('next') ?? 'Вперёд'}
                      </button>
                    </div>
                  </div>
                ) : null}
              </>
            ) : activeSection === 'acts' ? (
              <>
                <div className="mb-6 flex flex-wrap items-center justify-between gap-4">
                  <div>
                    <h3 className="text-lg font-semibold text-gray-900">
                      {t('acts') ?? SECTION_META.acts.label}
                    </h3>
                    <p className="text-sm text-gray-500">
                      {t('actSectionDescription') ?? SECTION_META.acts.placeholderDescription}
                    </p>
                  </div>
                  <div className="flex flex-wrap items-center gap-2">
                    <button
                      type="button"
                      onClick={() => {
                        void refreshActs();
                      }}
                      className="inline-flex items-center gap-2 rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50">
                      <RotateCcw className={actsRefreshing ? 'h-4 w-4 animate-spin' : 'h-4 w-4'} />
                      {t('invoiceRefresh') ?? t('actFiltersApply') ?? 'Обновить'}
                    </button>
                    <button
                      type="button"
                      onClick={openAddAct}
                      className="inline-flex items-center gap-2 rounded-lg bg-emerald-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-emerald-700">
                      <Plus size={16} /> {t('addAct') ?? SECTION_META.acts.actionLabel}
                    </button>
                  </div>
                </div>

                <div className="mb-6 grid gap-4 md:grid-cols-3">
                  {actSummaryCards.map((card) => {
                    const Icon = card.icon;
                    return (
                      <div
                        key={card.key}
                        className={`rounded-2xl border bg-white p-5 shadow-sm ${card.accent}`}>
                        <div className="flex items-center justify-between">
                          <div>
                            <p className="text-sm font-medium text-gray-700">{card.title}</p>
                            <p className="mt-2 text-2xl font-semibold text-gray-900">
                              {formatCurrencySmart(card.bucket.amount).full}
                            </p>
                          </div>
                          <Icon className="h-10 w-10 opacity-80" />
                        </div>
                        <p className="mt-4 text-xs text-gray-500">
                          {(t('actsSummaryCount') ?? 'Количество актов')}: {card.bucket.count}
                        </p>
                      </div>
                    );
                  })}
                </div>

                <div className="mb-4 flex flex-wrap items-center gap-3 rounded-xl border border-gray-200 bg-gray-50 p-4">
                  <div className="flex items-center gap-2">
                    <CalendarRange className="h-4 w-4 text-gray-500" />
                    <input
                      type="date"
                      value={actFrom}
                      onChange={(event) => setActFrom(event.target.value)}
                      className="w-36 rounded-lg border border-gray-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                    />
                    <span className="text-gray-500">—</span>
                    <input
                      type="date"
                      value={actTo}
                      onChange={(event) => setActTo(event.target.value)}
                      className="w-36 rounded-lg border border-gray-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                    />
                  </div>
                  <div>
                    <select
                      value={actStatusFilter}
                      onChange={(event) => setActStatusFilter(event.target.value as 'all' | ActStatus)}
                      className="rounded-lg border border-gray-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                    >
                      <option value="all">{t('allStatuses') ?? 'Все статусы'}</option>
                      {ACT_STATUS_ORDER.map((status) => (
                        <option key={status} value={status}>
                          {actStatusLabels[status]}
                        </option>
                      ))}
                    </select>
                  </div>
                  <div className="relative flex-1 min-w-[220px]">
                    <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-gray-400" />
                    <input
                      type="search"
                      value={actSearch}
                      onChange={(event) => setActSearch(event.target.value)}
                      placeholder={t('actSearchPlaceholder') ?? 'Номер акта, название или ИНН контрагента'}
                      className="w-full rounded-lg border border-gray-300 pl-9 pr-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                    />
                  </div>
                  <button
                    type="button"
                    onClick={resetActFilters}
                    className="ml-auto inline-flex items-center gap-2 rounded-lg border border-gray-200 px-3 py-2 text-sm text-gray-600 hover:bg-gray-50"
                  >
                    {t('actFiltersReset') ?? 'Сбросить'}
                  </button>
                </div>

                <div className="rounded-xl border border-gray-200 bg-white shadow-sm">
                  <div className="overflow-x-auto">
                    <table className="min-w-full divide-y divide-gray-200">
                      <thead className="bg-gray-50">
                        <tr className="text-left text-xs font-semibold uppercase tracking-wide text-gray-500">
                          {(
                            [
                              { key: 'date', label: t('actDate') ?? 'Дата', sortable: true },
                              { key: 'number', label: t('actNumber') ?? '№', sortable: true },
                              { key: 'title' as const, label: t('actTitle') ?? 'Название', sortable: false },
                              { key: 'amount', label: t('actAmount') ?? 'Сумма', sortable: true },
                              { key: 'status', label: t('actStatus') ?? 'Статус', sortable: true },
                              { key: 'responsible', label: t('actResponsible') ?? 'Ответственный', sortable: true },
                              { key: 'counterpartyInn', label: t('actInn') ?? 'ИНН', sortable: true },
                              { key: 'actions' as const, label: t('actions') ?? 'Действия', sortable: false },
                            ] as Array<{ key: ActsSortKey | 'title' | 'actions'; label: string; sortable?: boolean }>
                          ).map((column) => {
                            const isSortable =
                              column.sortable !== false && column.key !== 'title' && column.key !== 'actions';
                            const isActive = actSort.key === column.key;
                            const direction = isActive ? actSort.direction : undefined;
                            return (
                              <th key={column.key} scope="col" className="px-4 py-3">
                                {isSortable ? (
                                  <button
                                    type="button"
                                    onClick={() => handleActSort(column.key as ActsSortKey)}
                                    className="flex items-center gap-1 text-xs font-semibold uppercase tracking-wide text-gray-500"
                                  >
                                    <span>{column.label}</span>
                                    <span className="text-gray-400">
                                      {isActive ? (direction === 'asc' ? '▲' : '▼') : ''}
                                    </span>
                                  </button>
                                ) : (
                                  <span>{column.label}</span>
                                )}
                              </th>
                            );
                          })}
                        </tr>
                      </thead>
                      <tbody className="divide-y divide-gray-200 text-sm text-gray-700">
                        {actsLoading ? (
                          <tr>
                            <td colSpan={8} className="py-10 text-center text-gray-500">
                              <div className="inline-flex items-center gap-2">
                                <Loader2 className="h-4 w-4 animate-spin" />
                                {t('loading') ?? 'Загрузка...'}
                              </div>
                            </td>
                          </tr>
                        ) : clientActs.length === 0 ? (
                          <tr>
                            <td colSpan={8} className="py-10 text-center text-gray-500">
                              {actsError ?? t('actsEmpty') ?? 'Акты не найдены'}
                            </td>
                          </tr>
                        ) : (
                          <>
                            {clientActs.map((act) => {
                              const title = act.title?.trim();
                              const displayTitle = title && title !== `№${act.number}` ? title : `Акт №${act.number}`;
                              return (
                                <tr key={act.id} className="hover:bg-gray-50/80">
                                  <td className="px-4 py-3 font-medium text-gray-900">{toRuDate(act.date)}</td>
                                <td className="px-4 py-3">
                                  <div className="font-medium text-gray-900">№{act.number}</div>
                                  {act.invoiceNumber && (
                                    <div className="text-xs text-gray-500">
                                      {(t('actInvoice') ?? 'Счёт')} {act.invoiceNumber}
                                    </div>
                                  )}
                                </td>
                                <td className="px-4 py-3">
                                  <div className="font-medium text-gray-900">{displayTitle}</div>
                                  {act.comment && (
                                    <div className="text-xs text-gray-500">{act.comment}</div>
                                  )}
                                </td>
                                <td className="px-4 py-3 font-semibold text-gray-900">
                                  {formatCurrencySmart(act.amount).full}
                                </td>
                                <td className="px-4 py-3">
                                  <span
                                    className={`inline-flex items-center rounded-full px-3 py-1 text-xs font-medium ${ACT_STATUS_CLASSES[act.status]}`}
                                  >
                                    {actStatusLabels[act.status]}
                                  </span>
                                </td>
                                <td className="px-4 py-3 text-gray-900">
                                  {act.responsibleName ?? t('actResponsibleNotSelected') ?? '—'}
                                </td>
                                <td className="px-4 py-3 text-gray-900">{act.counterpartyInn ?? '—'}</td>
                                <td className="px-4 py-3">
                                  <div className="flex items-center gap-2">
                                    <button
                                      type="button"
                                      onClick={() => openEditAct(act)}
                                      className="inline-flex h-8 w-8 items-center justify-center rounded-full border border-gray-200 text-gray-600 hover:bg-gray-50"
                                      title={t('edit') ?? 'Редактировать'}
                                      aria-label={t('edit') ?? 'Редактировать'}
                                    >
                                      <Edit className="h-4 w-4" />
                                    </button>
                                    <button
                                      type="button"
                                      onClick={() => removeAct(act)}
                                      className="inline-flex h-8 w-8 items-center justify-center rounded-full border border-gray-200 text-red-600 hover:bg-red-50"
                                      title={t('delete') ?? 'Удалить'}
                                      aria-label={t('delete') ?? 'Удалить'}
                                    >
                                      <Trash2 className="h-4 w-4" />
                                    </button>
                                  </div>
                                </td>
                              </tr>
                            );
                          })}
                            {actsError && (
                              <tr>
                                <td colSpan={8} className="bg-amber-50 px-4 py-3 text-sm text-amber-700">
                                  {actsError}
                                </td>
                              </tr>
                            )}
                          </>
                        )}
                      </tbody>
                    </table>
                  </div>
                  {clientActs.length > 0 && !actsLoading && (
                    <div className="flex items-center justify-between border-t border-gray-200 bg-gray-50 px-4 py-3 text-sm text-gray-600">
                      <div>
                        {(t('recordsFound') ?? 'Найдено записей')}: {actsPagination.total}
                      </div>
                      <div className="flex items-center gap-2">
                        <button
                          type="button"
                          onClick={() => setActPage((pageValue) => Math.max(1, pageValue - 1))}
                          disabled={actPage <= 1}
                          className="rounded-lg border border-gray-200 px-3 py-1 disabled:cursor-not-allowed disabled:opacity-50"
                        >
                          {t('previous') ?? 'Назад'}
                        </button>
                        <span>
                          {actsPagination.page} / {actTotalPages}
                        </span>
                        <button
                          type="button"
                          onClick={() => setActPage((pageValue) => Math.min(actTotalPages, pageValue + 1))}
                          disabled={actPage >= actTotalPages}
                          className="rounded-lg border border-gray-200 px-3 py-1 disabled:cursor-not-allowed disabled:opacity-50"
                        >
                          {t('next') ?? 'Вперёд'}
                        </button>
                      </div>
                    </div>
                  )}
                </div>
              </>
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

        <InvoiceModal
          open={invoiceModalOpen}
          mode={invoiceModalMode}
          invoice={selectedInvoice}
          onClose={closeInvoiceModal}
          onSubmit={submitInvoice}
          submitting={invoiceSubmitting}
          errorMessage={invoiceModalError}
          clients={invoiceModalClients}
          lookupsLoading={invoiceLookupsLoading}
          lookupsError={invoiceLookupsError}
          defaultClientId={clientId}
        />

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
