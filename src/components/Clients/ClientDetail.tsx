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
import { useContracts, type ContractsSortKey } from '../../hooks/useContracts';
import { apiService } from '../../services/api';
import { usePayments } from '../../hooks/usePayments';
import { useTranslation } from '../../hooks/useTranslation';
import { useAuth } from '../../contexts/AuthContext';
import { useRolePermissions } from '../../hooks/useRolePermissions';
import { useLegalEntities } from '../../hooks/useLegalEntities';
import { useDictionaries } from '../../hooks/useDictionaries';
import { PaymentModal } from '../Calendar/PaymentModal';
import type {
  Act,
  ActInput,
  ActResponsible,
  ActStatus,
  Client,
  ClientCase,
  ClientInput,
  ClientStatus,
  Contract,
  ContractClient,
  ContractInput,
  Invoice,
  InvoiceInput,
  Payment,
  PaymentPayload,
  PaymentStatus,
} from '../../types';
import type { MenuSectionKey } from '../../types/permissions';
import { toRuDate, formatLocalYMD } from '../../utils/dateUtils';
import { MonthRangePicker } from '../MonthRange/MonthRangePicker';
import { CaseModal } from './CaseModal';
import { TwoTypeStats } from '../Statistics/TwoTypeStats';
import { formatCurrencySmart } from '../../utils/formatters';
import { ActModal } from '../Acts/ActModal';
import { InvoiceModal } from '../Accounts/InvoiceModal';
import { ContractModal } from '../Contracts/ContractModal';
import { ClientModal } from './ClientModal';
import { buildStatusBadgeStyle } from '../../utils/styleUtils';

interface ClientDetailProps {
  clientId: number;
  onBack: () => void;
  initialCaseId?: number | 'all';
}

type PaymentUpsert = PaymentPayload | ({ id: number } & PaymentPayload);

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
    placeholderTitle: 'Документы клиента',
    placeholderDescription: 'Управление документами будет доступно после доработки раздела.',
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

const SECTION_PERMISSION_MAP: Record<ClientDetailSection, MenuSectionKey> = {
  payments: 'clients',
  accounts: 'accounts',
  acts: 'acts',
  contracts: 'contracts',
  settlement: 'clients',
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

function toRuPaymentStatus(status?: string, fallback: string = '—'): string {
  if (!status) return fallback;
  const normalized = status.toLowerCase();
  if (normalized.includes('overdue') || normalized.includes('проср')) return 'Просрочено';
  if (normalized.includes('complete') || normalized.includes('оплач')) return 'Оплачено';
  if (normalized.includes('process')) return 'В обработке';
  if (normalized.includes('cancel')) return 'Отменено';
  if (normalized.includes('pending') || normalized.includes('ожид')) return 'Ожидается';
  if (normalized.includes('draft')) return 'Черновик';
  if (normalized.includes('schedule')) return 'Запланировано';
  if (normalized.includes('fail') || normalized.includes('error')) return 'Ошибка';
  return fallback;
}

const MIN_DATE = '1900-01-01';
const MAX_DATE = '2100-12-31';

export function ClientDetail({ clientId, onBack, initialCaseId }: ClientDetailProps) {
  const { t } = useTranslation();
  const { user } = useAuth();
  const permissions = useRolePermissions(user?.role?.id);
  const clientPermissions = permissions.clients;
  const accountPermissions = permissions.accounts;
  const actPermissions = permissions.acts;
  const contractPermissions = permissions.contracts;
  const { legalEntities } = useLegalEntities();
  const { clientStatuses } = useDictionaries();
  const sectionPermissions = useCallback(
    (section: ClientDetailSection) => permissions[SECTION_PERMISSION_MAP[section]],
    [permissions],
  );
  const [clientName, setClientName] = useState<string>('...');
  const [clientData, setClientData] = useState<Client | null>(null);
  const [clientModalOpen, setClientModalOpen] = useState(false);
  const [selectedCaseId, setSelectedCaseId] = useState<number | 'all'>(initialCaseId ?? 'all');

  const { cases: serverCases } = useClientCases(clientId);
  const [casesLocal, setCasesLocal] = useState<ClientCase[] | null>(null);
  useEffect(() => setCasesLocal(serverCases), [serverCases]);
  const cases = useMemo(() => casesLocal ?? serverCases, [casesLocal, serverCases]);

  const clientStatusById = useMemo(() => {
    const map = new Map<number, ClientStatus>();
    for (const status of clientStatuses) {
      map.set(status.id, status);
    }
    return map;
  }, [clientStatuses]);

  const resolvedClientStatus = useMemo(() => {
    if (clientData?.clientStatus) return clientData.clientStatus;
    if (clientData?.clientStatusId) {
      return clientStatusById.get(clientData.clientStatusId) ?? null;
    }
    return null;
  }, [clientData, clientStatusById]);

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

  const [contractFrom, setContractFrom] = useState<string>(startOfYear);
  const [contractTo, setContractTo] = useState<string>(todayYMD);
  const [contractSearch, setContractSearch] = useState('');
  const debouncedContractSearch = useDebouncedValue(contractSearch.trim(), 400);
  const [contractSort, setContractSort] = useState<{ key: ContractsSortKey; direction: 'asc' | 'desc' }>({
    key: 'date',
    direction: 'desc',
  });
  const [contractPage, setContractPage] = useState(1);
  const contractPageSize = 10;

  const [invoiceModalOpen, setInvoiceModalOpen] = useState(false);
  const [invoiceModalMode, setInvoiceModalMode] = useState<'create' | 'edit'>('create');
  const [selectedInvoice, setSelectedInvoice] = useState<Invoice | null>(null);
  const [invoiceSubmitting, setInvoiceSubmitting] = useState(false);
  const [invoiceModalError, setInvoiceModalError] = useState<string | null>(null);
  const [invoiceClients, setInvoiceClients] = useState<Client[]>([]);
  const [invoiceLookupsLoading, setInvoiceLookupsLoading] = useState(false);
  const [invoiceLookupsError, setInvoiceLookupsError] = useState<string | null>(null);

  const [contractModalOpen, setContractModalOpen] = useState(false);
  const [contractModalMode, setContractModalMode] = useState<'create' | 'edit'>('create');
  const [selectedContract, setSelectedContract] = useState<Contract | null>(null);
  const [contractSubmitting, setContractSubmitting] = useState(false);
  const [contractModalError, setContractModalError] = useState<string | null>(null);
  const [contractActionError, setContractActionError] = useState<string | null>(null);

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

  const {
    contracts: clientContracts,
    loading: contractsLoading,
    refreshing: contractsRefreshing,
    error: contractsError,
    pagination: contractsPagination,
    refresh: refreshContracts,
    createContract,
    updateContract,
    deleteContract,
  } = useContracts({
    from: contractFrom || undefined,
    to: contractTo || undefined,
    search: debouncedContractSearch || undefined,
    clientId,
    sortBy: contractSort.key,
    sortDir: contractSort.direction,
    page: contractPage,
    pageSize: contractPageSize,
  });

  useEffect(() => {
    setContractPage(1);
  }, [contractFrom, contractTo, debouncedContractSearch, clientId]);

  const contractTotalPages = useMemo(() => {
    if (!contractsPagination.pageSize) return 1;
    return Math.max(1, Math.ceil((contractsPagination.total ?? 0) / contractsPagination.pageSize));
  }, [contractsPagination.pageSize, contractsPagination.total]);

  const invoiceStatusClasses: Record<PaymentStatus, string> = {
    Pending: 'bg-amber-100 text-amber-700 border border-amber-200',
    Completed: 'bg-emerald-100 text-emerald-700 border border-emerald-200',
    Overdue: 'bg-rose-100 text-rose-700 border border-rose-200',
    Processing: 'bg-blue-100 text-blue-700 border border-blue-200',
    Cancelled: 'bg-slate-100 text-slate-600 border border-slate-200',
  };

  const openInvoiceCreate = () => {
    if (!accountPermissions.canCreate) {
      showPermissionWarning('Недостаточно прав для добавления счетов.');
      return;
    }
    setInvoiceModalMode('create');
    setSelectedInvoice(null);
    setInvoiceModalError(null);
    setInvoiceModalOpen(true);
  };

  const openInvoiceEdit = (invoice: Invoice) => {
    if (!accountPermissions.canEdit) {
      showPermissionWarning('Недостаточно прав для редактирования счетов.');
      return;
    }
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
    const isEdit = invoiceModalMode === 'edit' && selectedInvoice;
    if (isEdit && !accountPermissions.canEdit) {
      showPermissionWarning('Недостаточно прав для редактирования счетов.');
      return;
    }
    if (!isEdit && !accountPermissions.canCreate) {
      showPermissionWarning('Недостаточно прав для добавления счетов.');
      return;
    }
    setInvoiceSubmitting(true);
    setInvoiceModalError(null);
    try {
      if (isEdit && selectedInvoice) {
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
    if (!accountPermissions.canDelete) {
      showPermissionWarning('Недостаточно прав для удаления счетов.');
      return;
    }
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

  const resetContractFilters = () => {
    setContractFrom(startOfYear);
    setContractTo(todayYMD);
    setContractSearch('');
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

  const handleContractSort = (key: ContractsSortKey) => {
    setContractSort((prev) => {
      if (prev.key === key) {
        return { key, direction: prev.direction === 'asc' ? 'desc' : 'asc' };
      }
      return { key, direction: key === 'date' || key === 'createdAt' ? 'desc' : 'asc' };
    });
    setContractPage(1);
  };

  const openClientEdit = () => {
    if (!clientPermissions.canEdit) {
      showPermissionWarning('Недостаточно прав для редактирования клиента.');
      return;
    }
    if (!clientData) {
      void apiService
        .getClient(clientId)
        .then((client) => {
          setClientData(client);
          setClientName(client.name ?? `Client #${clientId}`);
        })
        .catch(() => {
          /* ignore */
        });
    }
    setClientModalOpen(true);
  };

  const handleUpdateClient = async (payload: ClientInput) => {
    if (!clientPermissions.canEdit) {
      showPermissionWarning('Недостаточно прав для редактирования клиента.');
      throw new Error('Недостаточно прав для редактирования клиента.');
    }
    try {
      const updated = await apiService.updateClient(clientId, payload);
      setClientData(updated);
      setClientName(updated.name ?? `Client #${clientId}`);
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Не удалось обновить клиента';
      window.alert(message);
      throw err instanceof Error ? err : new Error(message);
    }
  };

  const openContractCreate = () => {
    if (!contractPermissions.canCreate) {
      showPermissionWarning('Недостаточно прав для добавления договоров.');
      return;
    }
    setSelectedContract(null);
    setContractModalMode('create');
    setContractModalError(null);
    setContractActionError(null);
    setContractModalOpen(true);
  };

  const openContractEdit = (contract: Contract) => {
    if (!contractPermissions.canEdit) {
      showPermissionWarning('Недостаточно прав для редактирования договоров.');
      return;
    }
    setSelectedContract(contract);
    setContractModalMode('edit');
    setContractModalError(null);
    setContractActionError(null);
    setContractModalOpen(true);
  };

  const closeContractModal = () => {
    setContractModalOpen(false);
    setSelectedContract(null);
    setContractModalError(null);
  };

  const submitContract = async (payload: ContractInput) => {
    const isEdit = contractModalMode === 'edit' && selectedContract;
    if (isEdit && !contractPermissions.canEdit) {
      showPermissionWarning('Недостаточно прав для редактирования договоров.');
      return;
    }
    if (!isEdit && !contractPermissions.canCreate) {
      showPermissionWarning('Недостаточно прав для добавления договоров.');
      return;
    }
    const normalizedPayload: ContractInput = {
      ...payload,
      clientIds: payload.clientIds.includes(clientId)
        ? payload.clientIds
        : [...payload.clientIds, clientId],
    };
    setContractSubmitting(true);
    setContractModalError(null);
    try {
      if (isEdit && selectedContract) {
        await updateContract(selectedContract.id, normalizedPayload);
      } else {
        await createContract(normalizedPayload);
      }
      closeContractModal();
    } catch (err) {
      setContractModalError(err instanceof Error ? err.message : 'Не удалось сохранить договор');
    } finally {
      setContractSubmitting(false);
    }
  };

  const removeContract = async (contract: Contract) => {
    if (!contractPermissions.canDelete) {
      showPermissionWarning('Недостаточно прав для удаления договоров.');
      return;
    }
    const template = t('contractDeleteConfirm') ?? 'Удалить договор «{{number}}»?';
    const confirmMessage = template.replace(
      '{{number}}',
      contract.number || contract.title || String(contract.id),
    );
    if (!window.confirm(confirmMessage)) return;
    setContractActionError(null);
    try {
      await deleteContract(contract.id);
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Не удалось удалить договор';
      setContractActionError(message);
    }
  };

  const [caseModalOpen, setCaseModalOpen] = useState(false);
  const [editingCase, setEditingCase] = useState<ClientCase | null>(null);
  const [caseMode, setCaseMode] = useState<'create' | 'edit'>('create');

  const visibleSections = useMemo(
    () =>
      SECTION_ORDER.filter(
        (section) => section !== 'settlement' && sectionPermissions(section).canView,
      ),
    [sectionPermissions],
  );

  const [activeSection, setActiveSection] = useState<ClientDetailSection>(() => {
    const firstAllowed = SECTION_ORDER.find((section) => {
      if (section === 'settlement') return false;
      try {
        return sectionPermissions(section).canView;
      } catch {
        return false;
      }
    });
    return firstAllowed ?? 'payments';
  });

  useEffect(() => {
    if (activeSection === 'settlement' || !sectionPermissions(activeSection).canView) {
      const fallback = SECTION_ORDER.find(
        (section) => section !== 'settlement' && sectionPermissions(section).canView,
      );
      if (fallback) setActiveSection(fallback);
    }
  }, [activeSection, sectionPermissions]);

  const [statsReloadToken, setStatsReloadToken] = useState(0);
  const bumpStats = () => setStatsReloadToken((x) => x + 1);

  const showPermissionWarning = useCallback((message: string) => {
    window.alert(message || 'Недостаточно прав для выполнения действия.');
  }, []);

  useEffect(() => {
    let alive = true;
    apiService
      .getClient(clientId)
      .then((c) => {
        if (!alive) return;
        setClientName(c.name ?? `Client #${clientId}`);
        setClientData(c);
      })
      .catch(() => {
        if (!alive) return;
        setClientName(`Client #${clientId}`);
        setClientData(null);
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

  const contractDefaultClients = useMemo<ContractClient[]>(() => {
    const normalizedName =
      clientName && clientName !== '...'
        ? clientName
        : clientData?.name ?? `Клиент #${clientId}`;

    return [
      {
        id: clientId,
        name: normalizedName,
        company: clientData?.company ?? undefined,
      },
    ];
  }, [clientId, clientName, clientData?.name, clientData?.company]);

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

  const editClientLabel = t('editClient') ?? 'Изменить клиента';
  const addCaseLabel = t('addCase') ?? 'Добавить дело';

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
    if (!clientPermissions.canCreate) {
      showPermissionWarning(t('permissionNoAddPayment') ?? 'Недостаточно прав для добавления платежей.');
      return;
    }
    setActiveSection('payments');
    setEditingPayment(null);
    setPayModalOpen(true);
  };
  const openEditPayment = (p: Payment) => {
    if (!clientPermissions.canEdit) {
      showPermissionWarning(t('permissionNoEditPayment') ?? 'Недостаточно прав для редактирования платежей.');
      return;
    }
    setActiveSection('payments');
    setEditingPayment(p);
    setPayModalOpen(true);
  };
  const openAddAct = () => {
    if (!actPermissions.canCreate) {
      showPermissionWarning('Недостаточно прав для добавления актов.');
      return;
    }
    setActiveSection('acts');
    setActModalMode('create');
    setSelectedAct(null);
    setActModalError(null);
    setActModalOpen(true);
  };
  const openEditAct = (act: Act) => {
    if (!actPermissions.canEdit) {
      showPermissionWarning('Недостаточно прав для редактирования актов.');
      return;
    }
    setActiveSection('acts');
    setActModalMode('edit');
    setSelectedAct(act);
    setActModalError(null);
    setActModalOpen(true);
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

  const toBody = (p: PaymentPayload): PaymentPayload => ({
    ...p,
    notes: p.notes ?? '',
    systemNotes: p.systemNotes ?? '',
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
    const isEdit = 'id' in payload;
    if (isEdit && !clientPermissions.canEdit) {
      showPermissionWarning(t('permissionNoEditPayment') ?? 'Недостаточно прав для редактирования платежей.');
      return;
    }
    if (!isEdit && !clientPermissions.canCreate) {
      showPermissionWarning(t('permissionNoAddPayment') ?? 'Недостаточно прав для добавления платежей.');
      return;
    }
    if (isEdit) {
      const { id, ...rest } = payload;
      await updatePayment({ id, ...toBody(rest) });
    } else {
      await createPayment(toBody(payload));
    }
    closePayModal();
    await refreshPayments();
    bumpStats();
  };

  const removePayment = async (id: number) => {
    if (!clientPermissions.canDelete) {
      showPermissionWarning(t('permissionNoDeletePayment') ?? 'Недостаточно прав для удаления платежей.');
      return;
    }
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
      const isEdit = actModalMode === 'edit' && selectedAct;
      if (isEdit && !actPermissions.canEdit) {
        showPermissionWarning('Недостаточно прав для редактирования актов.');
        setActSubmitting(false);
        return;
      }
      if (!isEdit && !actPermissions.canCreate) {
        showPermissionWarning('Недостаточно прав для добавления актов.');
        setActSubmitting(false);
        return;
      }
      if (isEdit && selectedAct) {
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
    if (!actPermissions.canDelete) {
      showPermissionWarning('Недостаточно прав для удаления актов.');
      return;
    }
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
    if (!clientPermissions.canCreate) {
      showPermissionWarning('Недостаточно прав для добавления дел.');
      return;
    }
    setEditingCase(null);
    setCaseMode('create');
    setCaseModalOpen(true);
  };
  const saveCase = async (patch: Partial<ClientCase>): Promise<void> => {
    if (caseMode === 'edit' && editingCase) {
      if (!clientPermissions.canEdit) {
        showPermissionWarning('Недостаточно прав для редактирования дел.');
        return;
      }
    } else if (!clientPermissions.canCreate) {
      showPermissionWarning('Недостаточно прав для добавления дел.');
      return;
    }

    const base: Omit<ClientCase, 'id' | 'createdAt' | 'payments'> = {
      clientId,
      title: (patch.title ?? editingCase?.title ?? '').trim(),
      description: (patch.description ?? editingCase?.description ?? '').trim(),
      status: (patch.status ?? editingCase?.status ?? 'Open') as ClientCase['status'],
    };

    if (caseMode === 'edit' && editingCase) {
      const updated = await apiService.updateCase(editingCase.id, base);
      setCasesLocal((prev) =>
        (prev ?? []).map((item) => (item.id === updated.id ? { ...item, ...updated } : item)),
      );
      setCaseModalOpen(false);
      setEditingCase(updated);
    } else {
      const created = await apiService.createCase(base);
      setCasesLocal((prev) => [created, ...(prev ?? [])]);
      setSelectedCaseId(created.id);
      setCaseModalOpen(false);
      setEditingCase(null);
    }
  };

  const deleteCase = async (): Promise<void> => {
    if (!editingCase) return;
    if (!clientPermissions.canDelete) {
      showPermissionWarning('Недостаточно прав для удаления дел.');
      return;
    }
    await apiService.deleteCase(editingCase.id);
    setCasesLocal((prev) => (prev ?? []).filter((item) => item.id !== editingCase.id));
    setCaseModalOpen(false);
    setEditingCase(null);
    setSelectedCaseId('all');
  };

  const notFound = !loadingPayments && clientName === '...' && payments.length === 0;

  if (visibleSections.length === 0) {
    return (
      <div className="min-h-screen bg-gray-50 flex items-center justify-center p-6">
        <div className="w-full max-w-lg rounded-2xl border border-red-200 bg-white px-6 py-10 text-center shadow-sm">
          <Ban className="mx-auto h-12 w-12 text-red-500" />
          <h1 className="mt-4 text-2xl font-semibold text-red-700">Доступ к данным клиента ограничен</h1>
          <p className="mt-2 text-sm text-red-600">
            Недостаточно прав для просмотра разделов этой карточки. Обратитесь к администратору.
          </p>
          <button
            type="button"
            onClick={onBack}
            className="mt-6 inline-flex items-center justify-center rounded-lg border border-red-300 px-4 py-2 text-sm font-medium text-red-600 hover:bg-red-50"
          >
            Назад
          </button>
        </div>
      </div>
    );
  }

  return (
    <div className="min-h-screen bg-gray-50">
      <div className="max-w-[calc(100vw-2rem)] mx-auto p-6">
        <div className="mb-6 flex flex-wrap items-center gap-3">
          <button
            onClick={onBack}
            className="p-1 hover:bg-gray-100 rounded-lg transition-colors shrink-0"
            title="Назад"
            aria-label="Назад"
          >
            <ArrowLeft size={24} />
          </button>
          <div className="flex min-w-0 flex-1 flex-col gap-1">
            <h1 className="truncate text-2xl font-bold text-gray-900" title={clientName}>
              {clientName}
            </h1>
            {resolvedClientStatus ? (
              <span
                className="inline-flex w-fit items-center rounded-full border px-2 py-0.5 text-xs font-medium"
                style={buildStatusBadgeStyle(resolvedClientStatus.colorHex)}
              >
                {resolvedClientStatus.name}
              </span>
            ) : null}
          </div>
          <div className="ml-auto flex flex-wrap items-center gap-2">
            {clientPermissions.canEdit && (
              <button
                type="button"
                onClick={openClientEdit}
                className="inline-flex h-10 w-10 items-center justify-center rounded-lg border border-emerald-200 bg-white text-emerald-600 shadow-sm transition-colors hover:bg-emerald-50"
                title={editClientLabel}
                aria-label={editClientLabel}
              >
                <Edit className="h-5 w-5" />
              </button>
            )}
            {clientPermissions.canCreate && (
              <button
                type="button"
                onClick={openAddCase}
                className="inline-flex h-10 w-10 items-center justify-center rounded-lg bg-emerald-600 text-white shadow-sm transition-colors hover:bg-emerald-700"
                title={addCaseLabel}
                aria-label={addCaseLabel}
              >
                <Plus className="h-5 w-5" />
              </button>
            )}
          </div>
        </div>

        <div className="mb-6 flex flex-wrap items-center justify-between gap-3">
          <div className="flex flex-wrap gap-2">
            {visibleSections.map((section) => {
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
                  }`}
                >
                  {meta.label}
                </button>
              );
            })}
          </div>
          <div className="flex flex-wrap items-center gap-2">
            {clientPermissions.canCreate && (
              <button
                type="button"
                onClick={openAddPayment}
                className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-3 py-2 text-sm font-medium text-white shadow-sm transition-colors hover:bg-blue-700"
              >
                <Plus size={16} /> {SECTION_META.payments.actionLabel}
              </button>
            )}
            {accountPermissions.canCreate && (
              <button
                type="button"
                onClick={openInvoiceCreate}
                className="inline-flex items-center gap-2 rounded-lg bg-emerald-600 px-3 py-2 text-sm font-medium text-white shadow-sm transition-colors hover:bg-emerald-700"
              >
                <Plus size={16} /> {SECTION_META.accounts.actionLabel}
              </button>
            )}
            {actPermissions.canCreate && (
              <button
                type="button"
                onClick={openAddAct}
                className="inline-flex items-center gap-2 rounded-lg bg-emerald-600 px-3 py-2 text-sm font-medium text-white shadow-sm transition-colors hover:bg-emerald-700"
              >
                <Plus size={16} /> {SECTION_META.acts.actionLabel}
              </button>
            )}
            {contractPermissions.canCreate && (
              <button
                type="button"
                onClick={openContractCreate}
                className="inline-flex items-center gap-2 rounded-lg bg-indigo-600 px-3 py-2 text-sm font-medium text-white shadow-sm transition-colors hover:bg-indigo-700"
              >
                <Plus size={16} /> {SECTION_META.contracts.actionLabel}
              </button>
            )}
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
              </div>
            </div>
          </>
        ) : null}

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
                <div className="overflow-x-auto">
                  <table className="min-w-full divide-y divide-gray-200 text-sm">
                    <colgroup>
                      <col className="w-48" />
                      <col className="w-56" />
                      <col className="w-60" />
                      <col className="w-56" />
                      <col className="w-[18rem]" />
                      <col className="w-56" />
                      {clientPermissions.canEdit || clientPermissions.canDelete ? <col className="w-36" /> : null}
                    </colgroup>
                    <thead className="bg-slate-50">
                      <tr className="text-left text-xs font-semibold uppercase tracking-wide text-slate-500">
                        <th scope="col" className="px-4 py-3">Период</th>
                        <th scope="col" className="px-4 py-3">Сумма и тип</th>
                        <th scope="col" className="px-4 py-3">Категория / источник</th>
                        <th scope="col" className="px-4 py-3">Дело и контакт</th>
                        <th scope="col" className="px-4 py-3">Документы и комментарии</th>
                        <th scope="col" className="px-4 py-3">Статус и прогресс</th>
                        {(clientPermissions.canEdit || clientPermissions.canDelete) && (
                          <th scope="col" className="px-4 py-3 text-right">Действия</th>
                        )}
                      </tr>
                    </thead>
                    <tbody className="divide-y divide-gray-100 text-slate-700">
                      {visiblePayments.map((payment) => {
                        const status = normalizeStatus(payment.status);
                        const statusLabel = toRuPaymentStatus(
                          payment.paymentStatusEntity?.name ?? payment.status,
                          payment.paymentStatusEntity?.name || payment.status || '—',
                        );
                        const statusBadgeClass =
                          status === 'completed'
                            ? 'bg-emerald-50 text-emerald-700 border border-emerald-100'
                            : status === 'overdue'
                              ? 'bg-purple-50 text-purple-700 border border-purple-100'
                              : 'bg-amber-50 text-amber-700 border border-amber-100';
                        const StatusIcon =
                          status === 'completed'
                            ? CheckCircle
                            : status === 'overdue'
                              ? AlertTriangle
                              : Clock;
                        const directionLabel =
                          payment.dealType?.name ||
                          payment.incomeType?.name ||
                          payment.paymentSource?.name ||
                          '—';
                        const secondaryDirections = [
                          payment.dealType?.name,
                          payment.incomeType?.name,
                          payment.paymentSource?.name,
                        ]
                          .filter((item): item is string => Boolean(item && item !== directionLabel))
                          .filter((value, index, self) => self.indexOf(value) === index);
                        const DirectionIcon = payment.type === 'Income' ? TrendingUp : TrendingDown;
                        const hasProgress = payment.hasPartialPayment && payment.paidAmount > 0;
                        const paidProgress = payment.amount > 0 ? Math.min(payment.paidAmount / payment.amount, 1) : 0;

                        return (
                          <tr key={payment.id} className="align-top transition-colors hover:bg-slate-50/70">
                            <td className="px-4 py-4 text-slate-600">
                              <div className="font-medium text-slate-900">{toRuDate(payment.date)}</div>
                              {payment.plannedDate && payment.plannedDate !== payment.date ? (
                                <div className="mt-1 text-xs text-slate-500">
                                  План: {toRuDate(payment.plannedDate)}
                                </div>
                              ) : null}
                              {payment.paidDate ? (
                                <div className="mt-1 text-xs text-emerald-600">
                                  Оплачен: {toRuDate(payment.paidDate)}
                                </div>
                              ) : null}
                              <div className="mt-2 text-xs text-slate-400">
                                Создан: {toRuDate(payment.createdAt)}
                              </div>
                              {payment.rescheduleCount > 0 ? (
                                <div className="mt-2 inline-flex items-center rounded-full border border-amber-200 bg-amber-50 px-2 py-0.5 text-[11px] font-medium text-amber-700">
                                  <RotateCcw size={12} className="mr-1" /> Переносов: {payment.rescheduleCount}
                                </div>
                              ) : null}
                            </td>
                            <td className="px-4 py-4">
                              <div
                                className={`flex items-center gap-2 text-sm font-semibold ${
                                  payment.type === 'Income' ? 'text-emerald-600' : 'text-rose-600'
                                }`}
                                title={formatCurrencySmart(payment.amount, { alwaysCents: true }).full}>
                                <DirectionIcon size={18} />
                                {formatCurrencySmart(payment.amount).full}
                              </div>
                              <div className="mt-2 inline-flex items-center rounded-full border border-slate-200 bg-slate-50 px-2 py-0.5 text-[11px] font-medium text-slate-600">
                                {payment.type === 'Income' ? 'Поступление' : 'Списание'}
                              </div>
                              {payment.outstandingAmount > 0 ? (
                                <div className="mt-2 text-xs text-rose-600">
                                  Осталось оплатить: {formatCurrencySmart(payment.outstandingAmount).full}
                                </div>
                              ) : null}
                              {payment.hasPartialPayment && payment.paidAmount > 0 ? (
                                <div className="mt-2">
                                  <div className="flex items-center justify-between text-xs text-slate-500">
                                    <span>Оплачено</span>
                                    <span className="font-medium text-slate-700">
                                      {formatCurrencySmart(payment.paidAmount).full}
                                    </span>
                                  </div>
                                  <div className="mt-1 h-1.5 w-full rounded-full bg-slate-200">
                                    <div
                                      className={`h-full rounded-full ${
                                        payment.type === 'Income' ? 'bg-emerald-500' : 'bg-rose-500'
                                      }`}
                                      style={{ width: `${Math.max(0, Math.min(100, paidProgress * 100))}%` }}
                                    />
                                  </div>
                                </div>
                              ) : null}
                            </td>
                            <td className="px-4 py-4">
                              <div className="font-medium text-slate-900">{directionLabel}</div>
                              {secondaryDirections.length ? (
                                <ul className="mt-1 space-y-0.5 text-xs text-slate-500">
                                  {secondaryDirections.map((label) => (
                                    <li key={label}>{label}</li>
                                  ))}
                                </ul>
                              ) : null}
                              {payment.account ? (
                                <div className="mt-2 text-xs text-slate-500">Счёт: {payment.account}</div>
                              ) : null}
                              {payment.accountDate ? (
                                <div className="text-xs text-slate-400">
                                  Дата счёта: {toRuDate(payment.accountDate)}
                                </div>
                              ) : null}
                            </td>
                            <td className="px-4 py-4">
                              {payment.clientCase?.title ? (
                                <div className="font-medium text-slate-900">{payment.clientCase.title}</div>
                              ) : (
                                <div className="text-xs text-slate-400">Дело не указано</div>
                              )}
                              {payment.client?.name ? (
                                <div className="mt-1 text-xs text-slate-500">{payment.client.name}</div>
                              ) : null}
                              {payment.clientCase?.status ? (
                                <div className="mt-1 inline-flex items-center rounded-full border border-slate-200 bg-slate-50 px-2 py-0.5 text-[11px] text-slate-600">
                                  {caseStatusLabel(payment.clientCase.status)}
                                </div>
                              ) : null}
                            </td>
                            <td className="px-4 py-4">
                              {payment.description ? (
                                <div className="text-slate-800">{payment.description}</div>
                              ) : (
                                <div className="text-xs text-slate-400">Без описания</div>
                              )}
                              {payment.notes ? (
                                <div className="mt-2 rounded-lg border border-slate-100 bg-slate-50 px-3 py-2 text-xs text-slate-600" title={payment.notes}>
                                  <span className="font-medium text-slate-700">Заметка:</span> {payment.notes}
                                </div>
                              ) : null}
                              {payment.systemNotes ? (
                                <div className="mt-2 rounded-lg border border-slate-100 bg-white px-3 py-2 text-xs text-slate-500" title={payment.systemNotes}>
                                  <span className="font-medium text-slate-600">Системно:</span> {payment.systemNotes}
                                </div>
                              ) : null}
                            </td>
                            <td className="px-4 py-4">
                              <div
                                className={`inline-flex items-center gap-2 rounded-full px-3 py-1 text-xs font-medium ${statusBadgeClass}`}
                              >
                                <StatusIcon size={14} />
                                <span className="leading-none">{statusLabel}</span>
                              </div>
                              {payment.delayDays ? (
                                <div className="mt-2 inline-flex items-center rounded-full border border-purple-200 bg-purple-50 px-2 py-0.5 text-[11px] font-medium text-purple-700">
                                  <AlertTriangle size={12} className="mr-1" /> Просрочка: {payment.delayDays} дн.
                                </div>
                              ) : null}
                              {payment.lastPaymentDate ? (
                                <div className="mt-2 text-xs text-slate-500">
                                  Последний платёж: {toRuDate(payment.lastPaymentDate)}
                                </div>
                              ) : null}
                              {payment.hasPartialPayment && !hasProgress ? (
                                <div className="mt-2 text-xs text-slate-500">Оплачено частично</div>
                              ) : null}
                            </td>
                            {(clientPermissions.canEdit || clientPermissions.canDelete) && (
                              <td className="px-4 py-4">
                                <div className="flex items-center justify-end gap-2">
                                  {clientPermissions.canEdit ? (
                                    <button
                                      type="button"
                                      className="inline-flex h-9 w-9 items-center justify-center rounded-md border border-slate-200 text-blue-600 transition-colors hover:border-blue-300 hover:bg-blue-50"
                                      onClick={() => openEditPayment(payment)}
                                      title="Редактировать"
                                      aria-label="Редактировать"
                                    >
                                      <Edit size={16} />
                                    </button>
                                  ) : null}
                                  {clientPermissions.canDelete ? (
                                    <button
                                      type="button"
                                      className="inline-flex h-9 w-9 items-center justify-center rounded-md border border-slate-200 text-rose-600 transition-colors hover:border-rose-300 hover:bg-rose-50"
                                      onClick={() => removePayment(payment.id)}
                                      title="Удалить"
                                      aria-label="Удалить"
                                    >
                                      <Trash2 size={16} />
                                    </button>
                                  ) : null}
                                </div>
                              </td>
                            )}
                          </tr>
                        );
                      })}
                    </tbody>
                  </table>
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
                                {accountPermissions.canEdit && (
                                  <button
                                    type="button"
                                    onClick={() => openInvoiceEdit(invoice)}
                                    className="inline-flex h-8 w-8 items-center justify-center rounded-full border border-gray-200 text-gray-600 hover:bg-gray-50"
                                    title={t('edit') ?? 'Редактировать'}
                                    aria-label={t('edit') ?? 'Редактировать'}
                                  >
                                    <Edit className="h-4 w-4" />
                                  </button>
                                )}
                                {accountPermissions.canDelete && (
                                  <button
                                    type="button"
                                    onClick={() => removeInvoice(invoice)}
                                    className="inline-flex h-8 w-8 items-center justify-center rounded-full border border-gray-200 text-red-600 hover:bg-red-50"
                                    title={t('delete') ?? 'Удалить'}
                                    aria-label={t('delete') ?? 'Удалить'}
                                  >
                                    <Trash2 className="h-4 w-4" />
                                  </button>
                                )}
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
                      {t('actSectionTitle') ?? 'Сводка и список'}
                    </h3>
                    <p className="text-sm text-gray-500">
                      {t('actSectionDescription') ?? 'Отслеживайте статусы и суммы по документам клиента.'}
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
                          {(t('actsSummaryCount') ?? 'Количество')}: {card.bucket.count}
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
                              {actsError ?? t('actsEmpty') ?? 'Документы не найдены'}
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
                                    {actPermissions.canEdit && (
                                      <button
                                        type="button"
                                        onClick={() => openEditAct(act)}
                                        className="inline-flex h-8 w-8 items-center justify-center rounded-full border border-gray-200 text-gray-600 hover:bg-gray-50"
                                        title={t('edit') ?? 'Редактировать'}
                                        aria-label={t('edit') ?? 'Редактировать'}
                                      >
                                        <Edit className="h-4 w-4" />
                                      </button>
                                    )}
                                    {actPermissions.canDelete && (
                                      <button
                                        type="button"
                                        onClick={() => removeAct(act)}
                                        className="inline-flex h-8 w-8 items-center justify-center rounded-full border border-gray-200 text-red-600 hover:bg-red-50"
                                        title={t('delete') ?? 'Удалить'}
                                        aria-label={t('delete') ?? 'Удалить'}
                                      >
                                        <Trash2 className="h-4 w-4" />
                                      </button>
                                    )}
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
            ) : activeSection === 'contracts' ? (
              <>
                <div className="mb-6 flex flex-wrap items-center gap-3 rounded-xl border border-gray-200 bg-white p-4 shadow-sm">
                  <div className="flex items-center gap-2">
                    <CalendarRange className="h-4 w-4 text-gray-500" />
                    <input
                      type="date"
                      value={contractFrom}
                      onChange={(event) => setContractFrom(event.target.value)}
                      className="w-36 rounded-lg border border-gray-300 px-3 py-2 text-sm focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-200"
                    />
                    <span className="text-gray-500">—</span>
                    <input
                      type="date"
                      value={contractTo}
                      onChange={(event) => setContractTo(event.target.value)}
                      className="w-36 rounded-lg border border-gray-300 px-3 py-2 text-sm focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-200"
                    />
                  </div>
                  <div className="relative flex-1 min-w-[220px]">
                    <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-gray-400" />
                    <input
                      type="search"
                      value={contractSearch}
                      onChange={(event) => setContractSearch(event.target.value)}
                      placeholder={
                        t('contractsSearchPlaceholder') ??
                        'Поиск по номеру, названию или описанию договора'
                      }
                      className="w-full rounded-lg border border-gray-300 pl-9 pr-3 py-2 text-sm focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-200"
                    />
                  </div>
                  <div className="flex items-center gap-2 ml-auto">
                    <button
                      type="button"
                      onClick={() => refreshContracts()}
                      className="inline-flex items-center gap-2 rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50"
                    >
                      <RotateCcw className={contractsRefreshing ? 'h-4 w-4 animate-spin' : 'h-4 w-4'} />
                      {t('invoiceRefresh') ?? 'Обновить'}
                    </button>
                    <button
                      type="button"
                      onClick={resetContractFilters}
                      className="inline-flex items-center gap-2 rounded-lg border border-gray-200 px-3 py-2 text-sm text-gray-600 hover:bg-gray-50"
                    >
                      {t('invoiceFiltersReset') ?? 'Сбросить'}
                    </button>
                  </div>
                </div>

                {contractActionError ? (
                  <div className="mb-4 rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">
                    {contractActionError}
                  </div>
                ) : null}

                {contractsError ? (
                  <div className="mb-4 rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">
                    {(t('contractsLoadError') ?? 'Не удалось загрузить договоры') + ': '}
                    {contractsError}
                  </div>
                ) : null}

                {contractsLoading ? (
                  <div className="py-10 text-center text-gray-500">
                    <Loader2 className="mx-auto mb-2 h-5 w-5 animate-spin" />
                    {t('loading') ?? 'Загрузка...'}
                  </div>
                ) : clientContracts.length === 0 ? (
                  <div className="py-10 text-center text-gray-500">
                    <div className="text-lg font-semibold text-gray-900">
                      {t('contractsEmptyTitle') ?? 'Договоры отсутствуют'}
                    </div>
                    <div className="mt-2 text-sm text-gray-500">
                      {t('contractsEmptyDescription') ??
                        'Создайте первый договор, чтобы отслеживать обязательства и сроки.'}
                    </div>
                  </div>
                ) : (
                  <>
                    <div className="overflow-x-auto">
                      <table className="min-w-full divide-y divide-gray-200 text-sm">
                        <thead className="bg-gray-50">
                          <tr className="text-left text-xs font-semibold uppercase tracking-wide text-gray-500">
                            <th scope="col" className="px-4 py-3">
                              <button
                                type="button"
                                onClick={() => handleContractSort('number')}
                                className="flex items-center gap-1"
                              >
                                {t('contractNumber') ?? 'Номер договора'}
                                <span className="text-gray-400">
                                  {contractSort.key === 'number'
                                    ? contractSort.direction === 'asc'
                                      ? '▲'
                                      : '▼'
                                    : ''}
                                </span>
                              </button>
                            </th>
                            <th scope="col" className="px-4 py-3">
                              <button
                                type="button"
                                onClick={() => handleContractSort('date')}
                                className="flex items-center gap-1"
                              >
                                {t('contractDate') ?? 'Дата договора'}
                                <span className="text-gray-400">
                                  {contractSort.key === 'date'
                                    ? contractSort.direction === 'asc'
                                      ? '▲'
                                      : '▼'
                                    : ''}
                                </span>
                              </button>
                            </th>
                            <th scope="col" className="px-4 py-3">
                              {t('contractClients') ?? 'Клиенты'}
                            </th>
                            <th scope="col" className="px-4 py-3">
                              <button
                                type="button"
                                onClick={() => handleContractSort('amount')}
                                className="flex items-center gap-1"
                              >
                                {t('contractAmount') ?? 'Сумма договора'}
                                <span className="text-gray-400">
                                  {contractSort.key === 'amount'
                                    ? contractSort.direction === 'asc'
                                      ? '▲'
                                      : '▼'
                                    : ''}
                                </span>
                              </button>
                            </th>
                            <th scope="col" className="px-4 py-3">
                              {t('contractValidUntil') ?? 'Действует до'}
                            </th>
                            <th scope="col" className="px-4 py-3">
                              <button
                                type="button"
                                onClick={() => handleContractSort('createdAt')}
                                className="flex items-center gap-1"
                              >
                                {t('contractCreatedAt') ?? 'Создано'}
                                <span className="text-gray-400">
                                  {contractSort.key === 'createdAt'
                                    ? contractSort.direction === 'asc'
                                      ? '▲'
                                      : '▼'
                                    : ''}
                                </span>
                              </button>
                            </th>
                            {(contractPermissions.canEdit || contractPermissions.canDelete) && (
                              <th scope="col" className="px-4 py-3">
                                {t('actions') ?? 'Действия'}
                              </th>
                            )}
                          </tr>
                        </thead>
                        <tbody className="divide-y divide-gray-200 text-gray-700">
                          {clientContracts.map((contract) => (
                            <tr key={contract.id} className="hover:bg-gray-50/80">
                              <td className="px-4 py-3 font-medium text-gray-900">№{contract.number}</td>
                              <td className="px-4 py-3 text-gray-900">{toRuDate(contract.date)}</td>
                              <td className="px-4 py-3">
                                {contract.clients.length === 0 ? (
                                  <span className="text-xs text-gray-500">
                                    {t('contractClientsRequired') ?? 'Клиенты не указаны'}
                                  </span>
                                ) : (
                                  <div className="flex flex-wrap gap-1">
                                    {contract.clients.map((client) => (
                                      <span
                                        key={client.id}
                                        className="inline-flex items-center rounded-full bg-gray-100 px-2 py-1 text-[11px] text-gray-700"
                                      >
                                        {client.name}
                                      </span>
                                    ))}
                                  </div>
                                )}
                              </td>
                              <td className="px-4 py-3 font-semibold text-gray-900">
                                {contract.amount !== null && contract.amount !== undefined
                                  ? formatCurrencySmart(contract.amount).full
                                  : '—'}
                              </td>
                              <td className="px-4 py-3 text-gray-900">
                                {contract.validUntil ? toRuDate(contract.validUntil) : '—'}
                              </td>
                              <td className="px-4 py-3 text-gray-900">{toRuDate(contract.createdAt)}</td>
                              {(contractPermissions.canEdit || contractPermissions.canDelete) && (
                                <td className="px-4 py-3">
                                  <div className="flex items-center gap-2">
                                    {contractPermissions.canEdit && (
                                      <button
                                        type="button"
                                        onClick={() => openContractEdit(contract)}
                                        className="inline-flex h-8 w-8 items-center justify-center rounded-full border border-gray-200 text-gray-600 hover:bg-gray-50"
                                        title={t('edit') ?? 'Редактировать'}
                                        aria-label={t('edit') ?? 'Редактировать'}
                                      >
                                        <Edit className="h-4 w-4" />
                                      </button>
                                    )}
                                    {contractPermissions.canDelete && (
                                      <button
                                        type="button"
                                        onClick={() => removeContract(contract)}
                                        className="inline-flex h-8 w-8 items-center justify-center rounded-full border border-gray-200 text-red-600 hover:bg-red-50"
                                        title={t('delete') ?? 'Удалить'}
                                        aria-label={t('delete') ?? 'Удалить'}
                                      >
                                        <Trash2 className="h-4 w-4" />
                                      </button>
                                    )}
                                  </div>
                                </td>
                              )}
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                    <div className="flex items-center justify-between border-t border-gray-200 bg-gray-50 px-4 py-3 text-sm text-gray-600">
                      <div>
                        {(t('recordsFound') ?? 'Найдено записей')}: {contractsPagination.total ?? clientContracts.length}
                      </div>
                      <div className="flex items-center gap-2">
                        <button
                          type="button"
                          onClick={() => setContractPage((value) => Math.max(1, value - 1))}
                          disabled={contractPage <= 1}
                          className="rounded-lg border border-gray-200 px-3 py-1 disabled:cursor-not-allowed disabled:opacity-50"
                        >
                          {t('previous') ?? 'Назад'}
                        </button>
                        <span>
                          {(contractsPagination.page ?? contractPage)} / {contractTotalPages}
                        </span>
                        <button
                          type="button"
                          onClick={() => setContractPage((value) => Math.min(contractTotalPages, value + 1))}
                          disabled={contractPage >= contractTotalPages}
                          className="rounded-lg border border-gray-200 px-3 py-1 disabled:cursor-not-allowed disabled:opacity-50"
                        >
                          {t('next') ?? 'Вперёд'}
                        </button>
                      </div>
                    </div>
                  </>
                )}
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

        <ContractModal
          open={contractModalOpen}
          mode={contractModalMode}
          contract={selectedContract}
          onClose={closeContractModal}
          onSubmit={submitContract}
          submitting={contractSubmitting}
          errorMessage={contractModalError}
          defaultClients={contractDefaultClients}
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
            onDelete={caseMode === 'edit' && clientPermissions.canDelete ? deleteCase : undefined}
          />
        )}

        <ClientModal
          isOpen={clientModalOpen}
          onClose={() => setClientModalOpen(false)}
          onSubmit={handleUpdateClient}
          client={clientData ?? undefined}
          legalEntities={legalEntities}
          clientStatuses={clientStatuses}
        />

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
