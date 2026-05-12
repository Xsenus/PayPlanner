import { Ban, CalendarRange, CheckCircle2, FileCheck2, GripVertical, Loader2, Pencil, Plus, RotateCcw, Search, Send, SlidersHorizontal, Trash2 } from 'lucide-react';
import { useCallback, useEffect, useMemo, useState } from 'react';
import { useAuth } from '../../contexts/AuthContext';
import { useDialog } from '../../hooks/useDialog';
import { usePersistedTableSettings } from '../../hooks/usePersistedTableSettings';
import { useToast } from '../../hooks/useToast';
import { useTranslation } from '../../hooks/useTranslation';
import { useRolePermissions } from '../../hooks/useRolePermissions';
import { useActs, type ActsSortKey } from '../../hooks/useActs';
import { useDebouncedValue } from '../../hooks/useDebouncedValue';
import { apiService } from '../../services/api';
import type { Act, ActInput, ActResponsible, ActStatus, ActWorkflowStatus, Client, PaymentKind } from '../../types';
import { toDateInputValue } from '../../utils/dateUtils';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { normalizeStoredPeriod, resolvePeriod, type PeriodFilter } from '../../utils/periodUtils';
import { TableColumnSettings } from '../ui/TableColumnSettings';
import { TablePagination } from '../ui/TablePagination';
import { ActModal } from './ActModal';
import { DatePeriodPicker } from '../DateRange/DatePeriodPicker';

const STATUS_ORDER: ActStatus[] = ['Terminated', 'Transferred', 'Signed'];

function formatMoney(value: number): string {
  return new Intl.NumberFormat('ru-RU', {
    style: 'currency',
    currency: 'RUB',
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  }).format(value);
}

function formatDate(value?: string | null): string {
  if (!value) return '';
  const normalized = toDateInputValue(value);
  if (!normalized) return value;
  const [y, m, d] = normalized.split('-');
  return `${d}.${m}.${y}`;
}

type ActColumnKey = ActsSortKey | 'workflowStatus' | 'actions';

const PAGE_SIZE_OPTIONS = [10, 20, 50, 100];
const ACT_COLUMN_CONFIGS = [
  { key: 'date', defaultVisible: true, defaultWidth: 140 },
  { key: 'number', defaultVisible: true, defaultWidth: 140 },
  { key: 'amount', defaultVisible: true, defaultWidth: 150 },
  { key: 'invoiceNumber', defaultVisible: true, defaultWidth: 150 },
  { key: 'client', defaultVisible: true, defaultWidth: 250 },
  { key: 'status', defaultVisible: true, defaultWidth: 150 },
  { key: 'workflowStatus', defaultVisible: true, defaultWidth: 170 },
  { key: 'responsible', defaultVisible: true, defaultWidth: 180 },
  { key: 'actions', defaultVisible: true, defaultWidth: 130 },
] as const satisfies ReadonlyArray<{
  key: ActColumnKey;
  defaultVisible?: boolean;
  defaultWidth?: number;
}>;

function safeRead<T>(key: string, fallback: T): T {
  try {
    const raw = localStorage.getItem(key);
    if (!raw) return fallback;
    const parsed = JSON.parse(raw) as T;
    return parsed ?? fallback;
  } catch {
    return fallback;
  }
}

function safeWrite<T>(key: string, value: T) {
  try {
    localStorage.setItem(key, JSON.stringify(value));
  } catch {
    /** */
  }
}

type SortState = {
  key: ActsSortKey;
  direction: 'asc' | 'desc';
};

interface ActsProps {
  defaultType?: PaymentKind;
  lockType?: boolean;
  onOpenAct?: (actId: number) => void;
  onCreateActPage?: (initialValues?: Partial<ActInput> & { amount?: number | null }) => void;
  onOpenClient?: (clientId: number) => void;
  onOpenInvoiceEditor?: (invoiceId: number) => void;
}

export function Acts({
  defaultType,
  lockType = false,
  onOpenAct,
  onCreateActPage,
  onOpenClient,
  onOpenInvoiceEditor,
}: ActsProps) {
  const { user } = useAuth();
  const { confirm } = useDialog();
  const toast = useToast();
  const permissions = useRolePermissions(user?.role?.id);
  const actsPermissions = permissions.acts;
  const canCreate = actsPermissions.canCreate;
  const canEdit = actsPermissions.canEdit;
  const canDelete = actsPermissions.canDelete;

  const { t } = useTranslation();

  const defaultPeriod = useMemo<PeriodFilter>(() => ({ mode: 'custom', from: '', to: '' }), []);
  const storagePrefix = useMemo(() => `pp.acts.${user?.id ?? 'guest'}`, [user?.id]);
  const savedPeriod = useMemo(() => safeRead<unknown>(`${storagePrefix}.period`, null), [storagePrefix]);
  const [period, setPeriod] = useState<PeriodFilter>(() => normalizeStoredPeriod(savedPeriod, defaultPeriod));
  const periodRange = useMemo(() => resolvePeriod(period), [period]);
  const [typeFilter, setTypeFilter] = useState<'All' | PaymentKind>(defaultType ?? 'All');
  const [statusFilter, setStatusFilter] = useState<'all' | ActStatus>('all');
  const [workflowStatusFilter, setWorkflowStatusFilter] = useState<'all' | ActWorkflowStatus>('all');
  const [search, setSearch] = useState('');
  const debouncedSearch = useDebouncedValue(search.trim(), 400);
  const [sortState, setSortState] = useState<SortState>({ key: 'date', direction: 'desc' });
  const [page, setPage] = useState(1);
  const [columnsOpen, setColumnsOpen] = useState(false);
  const effectiveTypeFilter = lockType && defaultType ? defaultType : typeFilter;
  const canOpenActPage = typeof onOpenAct === 'function';

  const tableColumnConfigs = useMemo(
    () => ACT_COLUMN_CONFIGS.filter((column) => (column.key === 'actions' ? canEdit || canDelete : true)),
    [canDelete, canEdit],
  );

  const {
    columnOrder,
    orderedVisibleColumns: orderedVisibleColumnKeys,
    visibleColumns,
    draggingColumn,
    pageSize,
    setPageSize,
    toggleColumn,
    moveColumn,
    handleColumnDragStart,
    handleColumnDragOver,
    handleColumnDrop,
    handleColumnDragEnd,
    handleResizeStart,
    getColumnWidth,
    getTableWidth,
    resetTableSettings,
  } = usePersistedTableSettings<ActColumnKey>({
    storageKey: `${storagePrefix}.tableSettings.v1`,
    settingsKey: 'acts_table_settings',
    columns: tableColumnConfigs,
    defaultPageSize: 20,
    minPageSize: 10,
    maxPageSize: 100,
    minColumnWidth: 110,
  });
  const {
    acts,
    loading,
    refreshing,
    error,
    summary,
    summaryLoading,
    pagination,
    refresh,
    createAct,
  } = useActs({
    from: periodRange.from || undefined,
    to: periodRange.to || undefined,
    status: statusFilter === 'all' ? undefined : statusFilter,
    workflowStatus: workflowStatusFilter === 'all' ? undefined : workflowStatusFilter,
    type: effectiveTypeFilter === 'All' ? undefined : effectiveTypeFilter,
    search: debouncedSearch || undefined,
    sortBy: sortState.key,
    sortDir: sortState.direction,
    page,
    pageSize,
  });

  const [modalOpen, setModalOpen] = useState(false);
  const [selectedAct, setSelectedAct] = useState<Act | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [modalError, setModalError] = useState<string | null>(null);
  const [clients, setClients] = useState<Client[]>([]);
  const [responsibles, setResponsibles] = useState<ActResponsible[]>([]);
  const [lookupsLoading, setLookupsLoading] = useState(false);
  const [lookupsError, setLookupsError] = useState<string | null>(null);

  useEffect(() => {
    if (lockType && defaultType) {
      setTypeFilter(defaultType);
    }
  }, [defaultType, lockType]);

  const summaryBuckets = useMemo(
    () =>
      summary ?? {
        created: { amount: 0, count: 0 },
        transferred: { amount: 0, count: 0 },
        signed: { amount: 0, count: 0 },
        terminated: { amount: 0, count: 0 },
        totalAmount: 0,
        totalCount: 0,
      },
    [summary],
  );

  const statusLabels: Record<ActStatus, string> = {
    Created: t('actStatusCreated') ?? 'Создан',
    Transferred: t('actStatusTransferred') ?? 'Передано',
    Signed: t('actStatusSigned') ?? 'Подписано',
    Terminated: t('actStatusTerminated') ?? 'Расторгнуто',
  };

  const statusClasses: Record<ActStatus, string> = {
    Created: 'bg-slate-100 text-slate-700 border border-slate-200',
    Transferred: 'bg-amber-100 text-amber-800 border border-amber-200',
    Signed: 'bg-emerald-100 text-emerald-700 border border-emerald-200',
    Terminated: 'bg-rose-100 text-rose-700 border border-rose-200',
  };

  const workflowStatusLabels: Record<ActWorkflowStatus, string> = {
    Draft: 'Черновик',
    NotSigned: 'Не подписан',
    OnSigning: 'На подписании',
    Signed: 'Подписан',
    Cancelled: 'Отменен',
  };

  const workflowStatusClasses: Record<ActWorkflowStatus, string> = {
    Draft: 'bg-slate-100 text-slate-700 border border-slate-200',
    NotSigned: 'bg-slate-100 text-slate-700 border border-slate-200',
    OnSigning: 'bg-violet-100 text-violet-700 border border-violet-200',
    Signed: 'bg-emerald-100 text-emerald-700 border border-emerald-200',
    Cancelled: 'bg-rose-100 text-rose-700 border border-rose-200',
  };

  const resolveWorkflowStatus = (act: Act): ActWorkflowStatus => {
    if (act.workflowStatus) return act.workflowStatus;
    if (act.status === 'Transferred') return 'OnSigning';
    if (act.status === 'Signed') return 'Signed';
    if (act.status === 'Terminated') return 'Cancelled';
    return 'Draft';
  };

  const typeOptions: Array<'All' | PaymentKind> = ['All', 'Income', 'Expense'];
  const typeFilterLabels: Record<'All' | PaymentKind, string> = {
    All: t('invoiceTypeAllShort') ?? 'Все',
    Income: t('invoiceTypeIncomeShort') ?? 'Доходные',
    Expense: t('invoiceTypeExpenseShort') ?? 'Расходные',
  };
  const typeBadgeClasses: Record<PaymentKind, string> = {
    Income: 'border border-emerald-200 bg-emerald-50 text-emerald-700',
    Expense: 'border border-rose-200 bg-rose-50 text-rose-700',
  };
  const headingTitle =
    effectiveTypeFilter === 'Income'
      ? t('actsIncomeTitle') ?? 'Доходные акты'
      : effectiveTypeFilter === 'Expense'
        ? t('actsExpenseTitle') ?? 'Расходные акты'
        : t('acts') ?? 'Акты';
  const headingDescription =
    effectiveTypeFilter === 'Income'
      ? t('actsSectionDescriptionIncome') ?? 'Просматривайте акты по доходным счетам и контролируйте их статусы.'
      : effectiveTypeFilter === 'Expense'
        ? t('actsSectionDescriptionExpense') ?? 'Просматривайте акты по расходным счетам и контролируйте их статусы.'
        : t('actSectionDescription') ?? 'Управляйте актами и отслеживайте их статусы.';

  const resolvedHeadingTitle =
    headingTitle === 'actsIncomeTitle'
      ? 'Доходные акты'
      : headingTitle === 'actsExpenseTitle'
        ? 'Расходные акты'
        : headingTitle;
  const resolvedHeadingDescription =
    headingDescription === 'actsSectionDescriptionIncome'
      ? 'Просматривайте акты по доходным счетам и контролируйте их статусы.'
      : headingDescription === 'actsSectionDescriptionExpense'
        ? 'Просматривайте акты по расходным счетам и контролируйте их статусы.'
        : headingDescription;

  const summaryCards = useMemo(
    () => [
      {
        key: 'terminated' as const,
        title: t('actSummaryTerminated') ?? 'Расторгнуто',
        bucket: summaryBuckets.terminated,
        icon: Ban,
        accent: 'text-rose-700 bg-rose-50 border border-rose-100',
      },
      {
        key: 'transferred' as const,
        title: t('actSummaryTransferred') ?? 'Передано',
        bucket: summaryBuckets.transferred,
        icon: Send,
        accent: 'text-amber-700 bg-amber-50 border border-amber-100',
      },
      {
        key: 'signed' as const,
        title: t('actSummarySigned') ?? 'Подписано',
        bucket: summaryBuckets.signed,
        icon: CheckCircle2,
        accent: 'text-emerald-700 bg-emerald-50 border border-emerald-100',
      },
    ],
    [summaryBuckets, t],
  );

  const columns = useMemo(
    () => [
      { key: 'date' as ActColumnKey, label: t('actDate') ?? 'Дата', sortable: true },
      { key: 'number' as ActColumnKey, label: t('actNumber') ?? '№', sortable: true },
      { key: 'amount' as ActColumnKey, label: t('actAmount') ?? 'Сумма', sortable: true },
      { key: 'invoiceNumber' as ActColumnKey, label: t('actInvoice') ?? 'Счёт', sortable: true },
      { key: 'client' as ActColumnKey, label: t('actClient') ?? 'Контрагент', sortable: true },
      { key: 'status' as ActColumnKey, label: t('actStatus') ?? 'Статус', sortable: true },
      { key: 'workflowStatus' as ActColumnKey, label: 'Статус документа', sortable: false },
      { key: 'responsible' as ActColumnKey, label: t('actResponsible') ?? 'Ответственный', sortable: true },
      { key: 'actions' as ActColumnKey, label: t('actions') ?? 'Действия', sortable: false },
    ],
    [t],
  );

  const columnsForSettings = useMemo(
    () => columns.filter((column) => (column.key === 'actions' ? canEdit || canDelete : true)),
    [canDelete, canEdit, columns],
  );
  const columnsByKey = useMemo(
    () => Object.fromEntries(columnsForSettings.map((column) => [column.key, column])) as Record<ActColumnKey, (typeof columnsForSettings)[number]>,
    [columnsForSettings],
  );
  const visibleOrderedColumns = useMemo(
    () => orderedVisibleColumnKeys.map((key) => columnsByKey[key]).filter(Boolean),
    [columnsByKey, orderedVisibleColumnKeys],
  );

  const effectivePageSize = pagination.pageSize || pageSize;
  const totalPages = useMemo(() => {
    if (!effectivePageSize) return 1;
    return Math.max(1, Math.ceil((pagination.total ?? 0) / effectivePageSize));
  }, [effectivePageSize, pagination.total]);

  useEffect(() => {
    setPage(1);
  }, [debouncedSearch, effectiveTypeFilter, statusFilter, workflowStatusFilter, periodRange.from, periodRange.to, pageSize]);

  useEffect(() => {
    setPage((prev) => Math.min(prev, totalPages));
  }, [totalPages]);

  useEffect(() => {
    if (!error) return;
    toast.error(error);
  }, [error, toast]);

  useEffect(() => {
    setPeriod(normalizeStoredPeriod(savedPeriod, defaultPeriod));
  }, [defaultPeriod, savedPeriod]);

  useEffect(() => {
    safeWrite(`${storagePrefix}.period`, period);
  }, [period, storagePrefix]);

  const ensureLookups = useCallback(async () => {
    if (!modalOpen) return;
    if (clients.length > 0 && responsibles.length > 0 && !lookupsError) return;
    setLookupsLoading(true);
    try {
      const [clientsResponse, responsibleResponse] = await Promise.all([
        apiService.getClients(),
        apiService.getActResponsibles(),
      ]);
      setClients(clientsResponse ?? []);
      setResponsibles(responsibleResponse ?? []);
      setLookupsError(null);
    } catch (err) {
      setLookupsError(extractDisplayErrorMessage(err, 'Не удалось загрузить справочники'));
    } finally {
      setLookupsLoading(false);
    }
  }, [clients.length, responsibles.length, modalOpen, lookupsError]);

  useEffect(() => {
    if (modalOpen) {
      void ensureLookups();
    }
  }, [modalOpen, ensureLookups]);

  const openCreateModal = () => {
    if (onCreateActPage) {
      onCreateActPage({ status: 'Created' });
      return;
    }

    setSelectedAct(null);
    setModalOpen(true);
    setModalError(null);
  };

  const closeModal = () => {
    if (submitting) return;
    setModalOpen(false);
    setSelectedAct(null);
    setModalError(null);
  };

  const handleSubmit = async (payload: ActInput) => {
    setSubmitting(true);
    setModalError(null);
    try {
      if (lockType && effectiveTypeFilter !== 'All' && !payload.invoiceId) {
        throw new Error('Для доходного или расходного акта укажите связанный счет.');
      }
      await createAct(payload);
      toast.success(selectedAct ? 'Акт обновлен.' : 'Акт создан.');
      setModalOpen(false);
      setSelectedAct(null);
    } catch (err) {
      const message = extractDisplayErrorMessage(err, 'Не удалось сохранить акт');
      setModalError(message);
      toast.error(message);
      throw err;
    } finally {
      setSubmitting(false);
    }
  };


  const openInvoiceFromAct = useCallback(async (act: Act) => {
    if (!onOpenInvoiceEditor) return;
    if (act.invoiceId) {
      onOpenInvoiceEditor(act.invoiceId);
      return;
    }
    if (!act.invoiceNumber) return;
    try {
      const response = await apiService.getInvoices({ search: act.invoiceNumber, page: 1, pageSize: 100 });
      const normalized = act.invoiceNumber.trim().toLowerCase();
      const invoice = (response.items ?? []).find((item) => String(item.number ?? '').trim().toLowerCase() === normalized);
      if (invoice) {
        onOpenInvoiceEditor(invoice.id);
      } else {
        toast.error('Счет не найден.');
      }
    } catch (err) {
      toast.error(extractDisplayErrorMessage(err, 'Не удалось открыть счет.'));
    }
  }, [onOpenInvoiceEditor, toast]);

  const handleDelete = async (act: Act) => {
    const confirmText = t('actDeleteConfirm') ?? 'Удалить акт?';
    if (!(await confirm({ title: 'Подтверждение удаления', description: confirmText, confirmLabel: 'Удалить', cancelLabel: 'Отмена', variant: 'danger' }))) return;
    try {
      await apiService.deleteAct(act.id);
      toast.success('Акт удален.');
      if (acts.length === 1 && page > 1) {
        setPage((current) => Math.max(1, current - 1));
      } else {
        await refresh();
      }
    } catch (err) {
      toast.error(extractDisplayErrorMessage(err, 'Не удалось удалить акт.'));
    }
  };

  const changeSort = (key: ActsSortKey) => {
    setSortState((prev) => {
      if (prev.key === key) {
        return { key, direction: prev.direction === 'asc' ? 'desc' : 'asc' };
      }
      return { key, direction: key === 'date' ? 'desc' : 'asc' };
    });
  };

  const handleResetFilters = () => {
    setPeriod(defaultPeriod);
    if (!lockType) {
      setTypeFilter('All');
    }
    setStatusFilter('all');
    setWorkflowStatusFilter('all');
    setSearch('');
    setSortState({ key: 'date', direction: 'desc' });
    setPage(1);
  };

  const renderActCell = (act: Act, columnKey: ActColumnKey, key: string) => {
    switch (columnKey) {
      case 'date':
        return <td key={key} className="whitespace-nowrap px-4 py-3 text-sm text-slate-700">{formatDate(act.date)}</td>;
      case 'number':
        return <td key={key} className="whitespace-nowrap px-4 py-3 text-sm font-medium text-slate-900">{act.number}</td>;
      case 'amount':
        return <td key={key} className="whitespace-nowrap px-4 py-3 text-sm text-slate-700">{formatMoney(act.amount || 0)}</td>;
      case 'invoiceNumber':
        return (
          <td key={key} className="whitespace-nowrap px-4 py-3 text-sm text-slate-600">
            {act.invoiceNumber ? (
              <button
                type="button"
                className="text-indigo-600 hover:underline"
                onClick={(event) => {
                  event.stopPropagation();
                  void openInvoiceFromAct(act);
                }}
              >
                {act.invoiceNumber}
              </button>
            ) : '—'}
          </td>
        );
      case 'client':
        return (
          <td key={key} className="px-4 py-3 text-sm text-slate-700">
            <div className="flex flex-col">
              <span className="font-medium text-slate-900">
                {act.clientId && act.clientName ? (
                  <button
                    type="button"
                    className="text-indigo-600 hover:underline"
                    onClick={(event) => {
                      event.stopPropagation();
                      onOpenClient?.(act.clientId as number);
                    }}
                  >
                    {act.clientName}
                  </button>
                ) : (act.clientName || t('actClientUnknown') || 'Без контрагента')}
              </span>
              {act.title && <span className="text-xs text-slate-500">{act.title}</span>}
            </div>
          </td>
        );
      case 'status':
        return (
          <td key={key} className="px-4 py-3">
            <span className={`inline-flex items-center rounded-full px-3 py-1 text-xs font-semibold ${statusClasses[act.status]}`}>
              {statusLabels[act.status]}
            </span>
          </td>
        );
      case 'workflowStatus': {
        const workflowStatus = resolveWorkflowStatus(act);
        return (
          <td key={key} className="px-4 py-3">
            <span className={`inline-flex items-center rounded-full px-3 py-1 text-xs font-semibold ${workflowStatusClasses[workflowStatus]}`}>
              {workflowStatusLabels[workflowStatus]}
            </span>
          </td>
        );
      }
      case 'responsible':
        return <td key={key} className="whitespace-nowrap px-4 py-3 text-sm text-slate-700">{act.responsibleName || t('actResponsibleNotSelected') || '—'}</td>;
      case 'actions':
        return (
          <td key={key} className="whitespace-nowrap px-4 py-3 text-sm text-slate-500">
            <div className="flex items-center gap-2">
              {canEdit && (
                <button
                  type="button"
                  onClick={(event) => { event.stopPropagation(); onOpenAct?.(act.id); }}
                  className="rounded-lg border border-slate-200 p-2 text-slate-500 transition hover:border-indigo-200 hover:text-indigo-600"
                  title={t('edit') ?? 'Редактировать'}>
                  <Pencil className="h-4 w-4" />
                </button>
              )}
              {canDelete && (
                <button
                  type="button"
                  onClick={(event) => { event.stopPropagation(); void handleDelete(act); }}
                  className="rounded-lg border border-slate-200 p-2 text-slate-500 transition hover:border-rose-200 hover:text-rose-600"
                  title={t('delete') ?? 'Удалить'}>
                  <Trash2 className="h-4 w-4" />
                </button>
              )}
            </div>
          </td>
        );
      default:
        return null;
    }
  };

  return (
    <div className="min-h-screen bg-gray-50 pb-12">
      <div className="mx-auto w-full max-w-[calc(100vw-2rem)] px-4 pt-8 sm:px-6 lg:px-8">
        <div className="mb-6 flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
          <div>
            <div className="flex items-center gap-3">
              <div className="inline-flex h-12 w-12 items-center justify-center rounded-2xl bg-indigo-100 text-indigo-600">
                <FileCheck2 className="h-6 w-6" />
              </div>
              <div>
                <h1 className="text-2xl font-bold text-slate-900">{resolvedHeadingTitle}</h1>
                <p className="text-sm text-slate-500">{resolvedHeadingDescription}</p>
              </div>
            </div>
          </div>
          {canCreate && (
            <button
              type="button"
              onClick={openCreateModal}
              className="inline-flex items-center gap-2 rounded-xl bg-indigo-600 px-4 py-2 text-sm font-semibold text-white shadow-lg shadow-indigo-200 transition-colors hover:bg-indigo-500">
              <Plus className="h-4 w-4" />
              {t('addAct') ?? 'Добавить акт'}
            </button>
          )}
        </div>

        <div className="mb-6 grid gap-4 lg:grid-cols-3">
          {summaryLoading ? (
            STATUS_ORDER.map((_, index) => (
              <div
                key={index}
                className="rounded-2xl border border-slate-200 bg-white p-4 shadow-sm">
                <div className="h-4 w-28 animate-pulse rounded bg-slate-200" />
                <div className="mt-4 h-6 w-40 animate-pulse rounded bg-slate-200" />
                <div className="mt-3 h-4 w-24 animate-pulse rounded bg-slate-200" />
              </div>
            ))
          ) : (
            summaryCards.map((card) => {
              const Icon = card.icon;

              return (
                <div key={card.key} className={`rounded-2xl border bg-white p-5 shadow-sm transition hover:shadow-md ${card.accent}`}>
                  <div className="flex items-center justify-between">
                    <span className="text-sm font-medium uppercase tracking-wide text-slate-500">
                      {card.title}
                    </span>
                    <Icon className="h-5 w-5" />
                  </div>
                  <div className="mt-3 text-2xl font-semibold">{formatMoney(card.bucket.amount)}</div>
                  <div className="mt-2 text-sm text-slate-600">
                    {t('actsSummaryCount') ?? 'Количество актов'}: {card.bucket.count}
                  </div>
                </div>
              );
            })
          )}
        </div>

        <div className="mb-6 flex flex-wrap items-end gap-3 rounded-2xl border border-slate-200 bg-white p-4 shadow-sm">
          <div className="min-w-[260px]">
            {lockType && effectiveTypeFilter !== 'All' ? (
              <span
                className={`inline-flex items-center gap-2 rounded-full px-4 py-2 text-sm font-semibold ${
                  typeBadgeClasses[effectiveTypeFilter]
                }`}
              >
                {typeFilterLabels[effectiveTypeFilter]}
              </span>
            ) : (
              <div className="inline-flex flex-wrap items-center gap-2 rounded-full border border-slate-200 bg-slate-50 px-1 py-1 shadow-sm">
                {typeOptions.map((option) => {
                  const isActive = option === effectiveTypeFilter;
                  return (
                    <button
                      key={option}
                      type="button"
                      onClick={() => setTypeFilter(option)}
                      className={`rounded-full px-4 py-1 text-sm font-medium transition-colors ${
                        isActive ? 'bg-indigo-600 text-white shadow' : 'bg-transparent text-slate-600 hover:bg-slate-100'
                      }`}
                    >
                      {typeFilterLabels[option]}
                    </button>
                  );
                })}
              </div>
            )}
          </div>
          <div className="min-w-[260px] flex-1">
            <DatePeriodPicker
              value={period}
              onChange={setPeriod}
              label={t('actFiltersPeriod') ?? 'Период'}
              accent="indigo"
            />
          </div>
          <label className="flex min-w-[200px] flex-col gap-1 text-sm">
            <span className="font-medium text-slate-700">{t('status') ?? 'Статус'}</span>
            <select
              value={statusFilter}
              onChange={(event) => setStatusFilter(event.target.value === 'all' ? 'all' : (event.target.value as ActStatus))}
              className="w-full rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-200">
              <option value="all">{t('allStatuses') ?? 'Все статусы'}</option>
              {STATUS_ORDER.concat('Created' as ActStatus).map((status) => (
                <option key={status} value={status}>
                  {statusLabels[status]}
                </option>
              ))}
            </select>
          </label>
          <label className="flex min-w-[220px] flex-col gap-1 text-sm">
            <span className="font-medium text-slate-700">Статус документа</span>
            <select
              value={workflowStatusFilter}
              onChange={(event) => setWorkflowStatusFilter(event.target.value === 'all' ? 'all' : (event.target.value as ActWorkflowStatus))}
              className="w-full rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-200">
              <option value="all">{t('allStatuses') ?? 'Все статусы'}</option>
              {(['NotSigned', 'OnSigning', 'Signed', 'Draft', 'Cancelled'] as ActWorkflowStatus[]).map((status) => (
                <option key={status} value={status}>
                  {workflowStatusLabels[status]}
                </option>
              ))}
            </select>
          </label>
        </div>

        <div className="mb-6 flex flex-col gap-3 rounded-2xl border border-slate-200 bg-white p-4 shadow-sm">
          <div className="relative w-full">
            <Search className="pointer-events-none absolute left-3 top-2.5 h-4 w-4 text-slate-400" />
            <input
              type="search"
              value={search}
              onChange={(event) => setSearch(event.target.value)}
              placeholder={t('actSearchPlaceholder') ?? 'Номер акта или название'}
              className="w-full rounded-lg border border-slate-300 px-3 py-2 pl-10 text-sm focus:border-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-200"
            />
          </div>
          <div className="flex flex-wrap items-center justify-end gap-2">
            <div className="relative">
              <button
                type="button"
                onClick={() => setColumnsOpen((prev) => !prev)}
                className="inline-flex items-center gap-1 rounded-lg border border-slate-300 px-3 py-2 text-sm font-medium text-slate-600 transition hover:bg-slate-100">
                <SlidersHorizontal className="h-4 w-4" />
                {t('visibleColumns') ?? 'Колонки'}
              </button>
            </div>
            <button
              type="button"
              onClick={handleResetFilters}
              className="inline-flex items-center gap-1 rounded-lg border border-slate-300 px-3 py-2 text-sm font-medium text-slate-600 transition hover:bg-slate-100">
              <RotateCcw className="h-4 w-4" />
              {t('actFiltersReset') ?? 'Сбросить'}
            </button>
            <button
              type="button"
              onClick={() => void refresh()}
              className="inline-flex items-center gap-1 rounded-lg border border-indigo-200 bg-indigo-50 px-3 py-2 text-sm font-medium text-indigo-600 transition hover:bg-indigo-100">
              {refreshing ? <Loader2 className="h-4 w-4 animate-spin" /> : <CalendarRange className="h-4 w-4" />}
              {t('actFiltersApply') ?? 'Обновить'}
            </button>
          </div>
        </div>

        {columnsOpen ? (
          <div className="mb-6">
            <TableColumnSettings
              columns={columnsForSettings.map((column) => ({
                key: column.key,
                label: column.label,
              }))}
              columnOrder={columnOrder}
              visibleColumns={visibleColumns}
              draggingColumn={draggingColumn}
              onToggleColumn={toggleColumn}
              onMoveColumn={moveColumn}
              onDragStart={handleColumnDragStart}
              onDragOver={handleColumnDragOver}
              onDrop={handleColumnDrop}
              onDragEnd={handleColumnDragEnd}
              onReset={resetTableSettings}
              footer={
                <div className="flex items-center gap-2 text-sm text-slate-600">
                  <span>{t('rowsPerPage') ?? 'Строк на странице'}</span>
                  <select
                    value={pageSize}
                    onChange={(event) => setPageSize(Number(event.target.value) || pageSize)}
                    className="rounded-lg border border-slate-300 bg-white px-2 py-1 text-sm text-slate-700">
                    {PAGE_SIZE_OPTIONS.map((size) => (
                      <option key={size} value={size}>
                        {size}
                      </option>
                    ))}
                  </select>
                </div>
              }
            />
          </div>
        ) : null}

        <div className="overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-sm">
          {error && (
            <div className="border-b border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700">
              {error}
            </div>
          )}
          <div className="overflow-x-auto">
            <table
              className="min-w-full table-fixed divide-y divide-slate-200"
              style={{ width: getTableWidth(), minWidth: '100%' }}
            >
              <colgroup>
                {orderedVisibleColumnKeys.map((columnKey) => (
                  <col key={columnKey} style={{ width: getColumnWidth(columnKey) }} />
                ))}
              </colgroup>
              <thead className="bg-slate-50">
                <tr>
                  {visibleOrderedColumns.map((column) => {
                    const isSortable = column.sortable;
                    const isActive = sortState.key === column.key;
                    const direction = isActive ? sortState.direction : undefined;
                    return (
                      <th
                        key={column.key}
                        scope="col"
                        style={{ width: getColumnWidth(column.key) }}
                        onDragOver={handleColumnDragOver}
                        onDrop={(event) => handleColumnDrop(column.key, event)}
                        className={`relative px-4 py-3 text-left text-xs font-semibold uppercase tracking-wide ${
                          draggingColumn === column.key ? 'bg-indigo-50 text-indigo-700' : 'text-slate-600'
                        }`}>
                        <button
                          type="button"
                          draggable={column.key !== 'actions'}
                          onDragStart={(event) => handleColumnDragStart(column.key, event)}
                          onDragEnd={handleColumnDragEnd}
                          className="absolute left-2 top-1/2 z-10 inline-flex -translate-y-1/2 cursor-grab rounded p-1 text-slate-400 active:cursor-grabbing disabled:cursor-default"
                          title="Перетащите, чтобы изменить порядок"
                          aria-label={`Перетащите колонку ${column.label}, чтобы изменить порядок`}
                          disabled={column.key === 'actions'}>
                          <GripVertical className="h-4 w-4" />
                        </button>
                        {isSortable ? (
                          <button
                            type="button"
                            onClick={() => changeSort(column.key as ActsSortKey)}
                            className="inline-flex min-w-0 items-center gap-2 pl-8 text-slate-700">
                            <span>{column.label}</span>
                            {isActive && <span className="text-xs">{direction === 'asc' ? '▲' : '▼'}</span>}
                          </button>
                        ) : (
                          <span className="pl-8">{column.label}</span>
                        )}
                        <div
                          role="separator"
                          aria-label={`Изменить ширину колонки ${column.label}`}
                          className="absolute right-0 top-0 z-10 h-full w-3 cursor-col-resize"
                          onMouseDown={(event) => handleResizeStart(column.key, event)}
                          onDragStart={(event) => event.preventDefault()}
                        />
                      </th>
                    );
                  })}
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100 bg-white">
                {loading ? (
                  <tr>
                    <td colSpan={visibleOrderedColumns.length || 1} className="px-4 py-12 text-center text-slate-500">
                      <div className="flex flex-col items-center gap-3">
                        <Loader2 className="h-6 w-6 animate-spin text-indigo-500" />
                        <span>{t('loading') ?? 'Загрузка...'}...</span>
                      </div>
                    </td>
                  </tr>
                ) : acts.length === 0 ? (
                  <tr>
                    <td colSpan={visibleOrderedColumns.length || 1} className="px-4 py-12 text-center">
                      <div className="mx-auto max-w-md rounded-2xl border border-dashed border-slate-300 bg-slate-50 p-6">
                        <h3 className="text-lg font-semibold text-slate-700">{t('actsEmpty') ?? 'Акты не найдены'}</h3>
                        <p className="mt-2 text-sm text-slate-500">
                          {t('actsEmptyHint') ?? 'Измените условия фильтра или добавьте новый акт.'}
                        </p>
                        {canCreate && (
                          <button
                            type="button"
                            onClick={openCreateModal}
                            className="mt-4 inline-flex items-center gap-2 rounded-lg bg-indigo-600 px-4 py-2 text-sm font-medium text-white shadow hover:bg-indigo-500">
                            <Plus className="h-4 w-4" />
                            {t('addAct') ?? 'Добавить акт'}
                          </button>
                        )}
                      </div>
                    </td>
                  </tr>
                ) : (
                  acts.map((act) => (
                    <tr
                      key={act.id}
                      className={`hover:bg-slate-50/80 ${canOpenActPage ? 'cursor-pointer' : ''}`}
                      onClick={() => {
                        onOpenAct?.(act.id);
                      }}>
                      {visibleOrderedColumns.map((column) => renderActCell(act, column.key, column.key))}
                    </tr>
                  ))
                )}
              </tbody>
            </table>
          </div>
        </div>

        <TablePagination
          page={page}
          totalPages={totalPages}
          totalItems={pagination.total ?? 0}
          pageSize={pageSize}
          pageSizeOptions={PAGE_SIZE_OPTIONS}
          onPageChange={setPage}
          onPageSizeChange={setPageSize}
          recordsLabel={t('recordsFound') ?? 'Найдено записей'}
          pageSizeLabel={t('rowsPerPage') ?? 'Строк на странице'}
          previousLabel={t('previous') ?? 'Назад'}
          nextLabel={t('next') ?? 'Вперед'}
        />
      </div>

      <ActModal
        open={modalOpen}
        mode="create"
        act={selectedAct}
        onClose={closeModal}
        onSubmit={handleSubmit}
        submitting={submitting}
        errorMessage={modalError}
        clients={clients}
        responsibles={responsibles}
        lookupsLoading={lookupsLoading}
        lookupsError={lookupsError}
      />
    </div>
  );
}
