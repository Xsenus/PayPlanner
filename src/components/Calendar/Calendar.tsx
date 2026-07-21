import { SystemSelect } from '../ui/SystemSelect';
import { lazy, Suspense, useMemo, useState, useDeferredValue, useEffect, useRef } from 'react';
import { CalendarDays, ChevronLeft, ChevronRight, Plus, Search, SlidersHorizontal, Table, X } from 'lucide-react';
import { CalendarGrid } from './CalendarGrid';
import { PaymentModal } from './PaymentModal';
import { PaymentsTable, type PaymentTableColumnKey } from './PaymentsTable';
import { usePayments } from '../../hooks/usePayments';
import { usePersistedTableSettings } from '../../hooks/usePersistedTableSettings';
import { useTranslation } from '../../hooks/useTranslation';
import { MonthRangePicker, type MonthRange } from '../MonthRange/MonthRangePicker';
import type { Payment, PaymentPayload } from '../../types';
import { formatLocalYMD } from '../../utils/dateUtils';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { TwoTypeStats } from '../Statistics/TwoTypeStats';
import { useAuth } from '../../contexts/AuthContext';
import { useDialog } from '../../hooks/useDialog';
import { useToast } from '../../hooks/useToast';
import { useRolePermissions } from '../../hooks/useRolePermissions';
import { TableColumnSettings } from '../ui/TableColumnSettings';
import { TablePagination } from '../ui/TablePagination';
import { isInvoiceManagedPayment, isPartialRemainderSourcePayment, isTransferredRemainderPayment } from '../../utils/invoicePayments';
import { apiService } from '../../services/api';

type SubmitDTO = PaymentPayload | ({ id: number } & PaymentPayload);
type StatusFilter = 'All' | 'Pending' | 'Completed' | 'Overdue';

type StatMetric = 'completed' | 'pending' | 'overdue' | 'overall' | 'debt';
type CalendarViewMode = 'calendar' | 'table';
type CalendarSectionMode = 'finance' | 'crm';
type TableFilter = {
  type?: 'Income' | 'Expense';
  statuses?: Payment['status'][];
  label: string;
  metric: StatMetric;
};

const VIEW_MODE_STORAGE_KEY = 'pp.calendar_view';
const SECTION_MODE_STORAGE_KEY = 'pp.calendar_section_mode';
const CALENDAR_TABLE_PAGE_SIZE_OPTIONS = [10, 25, 50, 100];
const TaskJournal = lazy(() => import('../Tasks/TaskJournal').then((module) => ({ default: module.TaskJournal })));
const CALENDAR_PAYMENT_COLUMN_CONFIGS = [
  { key: 'date', defaultVisible: true, defaultWidth: 140 },
  { key: 'plannedDate', defaultVisible: false, defaultWidth: 150 },
  { key: 'client', defaultVisible: true, defaultWidth: 260 },
  { key: 'description', defaultVisible: true, defaultWidth: 300 },
  { key: 'type', defaultVisible: true, defaultWidth: 130 },
  { key: 'status', defaultVisible: true, defaultWidth: 150 },
  { key: 'category', defaultVisible: true, defaultWidth: 220 },
  { key: 'amount', defaultVisible: true, defaultWidth: 150 },
  { key: 'paidDate', defaultVisible: false, defaultWidth: 150 },
  { key: 'createdAt', defaultVisible: false, defaultWidth: 150 },
] satisfies ReadonlyArray<{
  key: PaymentTableColumnKey;
  defaultVisible?: boolean;
  defaultWidth?: number;
}>;

type CalendarProps = {
  onOpenClient?: (clientId: number, caseId?: number) => void;
  onOpenUser?: (userId: number) => void;
};

function makeStatsSignature(payments: Payment[]): string {
  if (!payments || payments.length === 0) return 'empty';
  return payments
    .map(
      (p) =>
        `${p.id}:${p.amount}:${p.paidAmount}:${p.outstandingAmount}:${p.status}:${p.type}:${p.date}:${p.plannedDate}`,
    )
    .sort()
    .join('|');
}
function ymStart(ym: string): string {
  const [y, m] = ym.split('-').map(Number);
  return formatLocalYMD(new Date(y, (m ?? 1) - 1, 1));
}
function ymEnd(ym: string): string {
  const [y, m] = ym.split('-').map(Number);
  return formatLocalYMD(new Date(y, m ?? 1, 0));
}

export function Calendar({ onOpenClient, onOpenUser }: CalendarProps) {
  const { user } = useAuth();
  const { confirm } = useDialog();
  const toast = useToast();
  const [currentDate, setCurrentDate] = useState(new Date());
  const [isModalOpen, setIsModalOpen] = useState(false);
  const [modalType, setModalType] = useState<'Income' | 'Expense'>('Income');
  const [editingPayment, setEditingPayment] = useState<Payment | null>(null);
  const [sectionMode, setSectionMode] = useState<CalendarSectionMode>(() => {
    try {
      const stored = localStorage.getItem(SECTION_MODE_STORAGE_KEY);
      if (stored === 'finance' || stored === 'crm') {
        return stored;
      }
    } catch {
      /** */
    }
    return 'finance';
  });

  const [search, setSearch] = useState('');
  const deferredSearch = useDeferredValue(search);
  const [statusFilter, setStatusFilter] = useState<StatusFilter>('All');
  const [range, setRange] = useState<MonthRange>({});
  const [viewMode, setViewMode] = useState<CalendarViewMode>(() => {
    try {
      const stored = localStorage.getItem(VIEW_MODE_STORAGE_KEY);
      if (stored === 'calendar' || stored === 'table') {
        return stored;
      }
    } catch {
      /** */
    }
    return 'calendar';
  });
  const [tableFilter, setTableFilter] = useState<TableFilter | null>(null);
  const [tablePage, setTablePage] = useState(1);
  const [tableSortBy, setTableSortBy] = useState<PaymentTableColumnKey>('date');
  const [tableSortDir, setTableSortDir] = useState<'asc' | 'desc'>('asc');
  const [tableSettingsOpen, setTableSettingsOpen] = useState(false);
  const permissions = useRolePermissions(user?.role?.id);
  const calendarPermissions = permissions.calendar;
  const canViewFinancialCalendar = permissions.payments?.canView === true;
  const canViewTaskCalendar = permissions.tasks?.canView === true;
  const canOpenLinkedClients = permissions.clients?.canView === true;
  const canOpenLinkedCases = permissions.cases?.canView === true;
  const [permissionMessage, setPermissionMessage] = useState<string | null>(null);

  const [statsReloadKey, setStatsReloadKey] = useState(0);
  const bumpStats = () => setStatsReloadKey((x) => x + 1);

  const { t, formatMonth } = useTranslation();
  const permissionTimerRef = useRef<number | null>(null);
  const {
    columnOrder,
    orderedVisibleColumns,
    visibleColumns,
    pageSize,
    setPageSize,
    draggingColumn,
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
  } = usePersistedTableSettings<PaymentTableColumnKey>({
    storageKey: `pp.calendar_table_settings.${user?.id ?? 'guest'}.v1`,
    settingsKey: 'calendar_table_settings',
    columns: CALENDAR_PAYMENT_COLUMN_CONFIGS,
    defaultPageSize: 25,
    minPageSize: 10,
    maxPageSize: 100,
    minColumnWidth: 120,
  });

  const showPermissionNotice = (message: string) => {
    setPermissionMessage(message);
    if (permissionTimerRef.current) {
      window.clearTimeout(permissionTimerRef.current);
    }
    permissionTimerRef.current = window.setTimeout(() => {
      setPermissionMessage(null);
      permissionTimerRef.current = null;
    }, 4000);
  };

  useEffect(() => {
    return () => {
      if (permissionTimerRef.current) {
        window.clearTimeout(permissionTimerRef.current);
      }
    };
  }, []);

  useEffect(() => {
    if (!calendarPermissions.canViewAnalytics && tableFilter) {
      setTableFilter(null);
    }
  }, [calendarPermissions.canViewAnalytics, tableFilter]);

  useEffect(() => {
    const nextMode = sectionMode === 'finance' && !canViewFinancialCalendar && canViewTaskCalendar
      ? 'crm'
      : sectionMode === 'crm' && !canViewTaskCalendar && canViewFinancialCalendar
        ? 'finance'
        : sectionMode;
    if (nextMode === sectionMode) {
      return;
    }

    setSectionMode(nextMode);
    try {
      localStorage.setItem(SECTION_MODE_STORAGE_KEY, nextMode);
    } catch {
      /** */
    }
  }, [canViewFinancialCalendar, canViewTaskCalendar, sectionMode]);

  const year = currentDate.getFullYear();
  const m0 = currentDate.getMonth();
  const startOfMonth = new Date(year, m0, 1);
  const endOfMonth = new Date(year, m0 + 1, 0);

  const fromDateStr =
    range.from && !range.to
      ? ymStart(range.from)
      : range.from && range.to
      ? ymStart(range.from)
      : formatLocalYMD(startOfMonth);

  const toDateStr =
    range.to && !range.from
      ? ymEnd(range.to)
      : range.from && range.to
      ? ymEnd(range.to)
      : formatLocalYMD(endOfMonth);

  const formatRange = () => {
    if (range.from && range.to) return `${range.from} — ${range.to}`;
    if (range.from) return `${range.from} →`;
    if (range.to) return `→ ${range.to}`;
    return formatMonth(currentDate);
  };

  const pollInterval = isModalOpen ? 0 : 5000;

  const {
    payments,
    createPayment,
    updatePayment,
    deletePayment,
    refresh: refreshPayments,
  } = usePayments(fromDateStr, toDateStr, {
    enabled: sectionMode === 'finance' && canViewFinancialCalendar,
    pollInterval,
    excludeInstallmentParents: true,
    sortBy: tableSortBy,
    sortDir: tableSortDir,
  });

  const [newIds, setNewIds] = useState<Set<number>>(new Set());

  const prevStatsSigRef = useRef<string>('init');
  useEffect(() => {
    const sig = makeStatsSignature(payments);
    if (prevStatsSigRef.current !== sig) {
      prevStatsSigRef.current = sig;
      bumpStats();
    }
  }, [payments]);

  const prevIdsRef = useRef<Set<number>>(new Set());
  useEffect(() => {
    if (isModalOpen) return;
    const currentIds = new Set(payments.map((p) => p.id));
    const prevIds = prevIdsRef.current;
    const added = new Set<number>();
    currentIds.forEach((id) => {
      if (!prevIds.has(id)) added.add(id);
    });
    prevIdsRef.current = currentIds;

    if (added.size > 0) {
      setNewIds(added);
      const timer = setTimeout(() => setNewIds(new Set()), 2000);
      return () => clearTimeout(timer);
    }
  }, [payments, isModalOpen]);

  useEffect(() => {
    try {
      localStorage.setItem(VIEW_MODE_STORAGE_KEY, viewMode);
    } catch {
      /** */
    }
  }, [viewMode]);

  const navigateMonth = (direction: 'prev' | 'next') => {
    setCurrentDate((prev) => {
      const d = new Date(prev);
      d.setMonth(prev.getMonth() + (direction === 'next' ? 1 : -1));
      return d;
    });
  };

  const handleAddPayment = (type: 'Income' | 'Expense') => {
    if (!calendarPermissions.canCreate) {
      showPermissionNotice(
        t('permissionNoAddPayment') ?? 'Недостаточно прав для добавления платежей.',
      );
      return;
    }
    setModalType(type);
    setEditingPayment(null);
    setIsModalOpen(true);
  };

  const handleEditPayment = (payment: Payment) => {
    if (!calendarPermissions.canEdit) {
      showPermissionNotice(
        t('permissionNoEditPayment') ?? 'Недостаточно прав для редактирования платежей.',
      );
      return;
    }
    setEditingPayment(payment);
    setModalType(payment.type);
    setIsModalOpen(true);
  };

  const handleDeletePayment = async (payment: Payment) => {
    const isTransferredRemainder = isTransferredRemainderPayment(payment);
    const isRemainderSource = isPartialRemainderSourcePayment(payment);
    if (payment.documentKind === 'Invoice' || isTransferredRemainder || isRemainderSource) {
      toast.error(
        isTransferredRemainder || isRemainderSource
          ? 'Перенесенный остаток нельзя удалить отдельно.'
          : 'Документ счета удаляется через раздел "Счета".',
        'Платежи',
      );
      return;
    }
    if (!calendarPermissions.canDelete) {
      showPermissionNotice(
        t('permissionNoDeletePayment') ?? 'Недостаточно прав для удаления платежей.',
      );
      return;
    }
    const ok = await confirm({
      title: 'Подтверждение удаления',
      description: t('confirmDeletePayment') ?? 'Вы уверены, что хотите удалить этот платёж?',
      confirmLabel: 'Удалить',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });
    if (!ok) return;
    try {
      await deletePayment(payment.id);
      await refreshPayments();
      bumpStats();
      toast.error('Платёж удалён.', 'Платежи');
    } catch (error) {
      toast.error(
        extractDisplayErrorMessage(error, 'Не удалось удалить платёж.'),
        'Платежи',
      );
    }
  };

  const handleCloseModal = async () => {
    setIsModalOpen(false);
    setEditingPayment(null);
    await refreshPayments();
    bumpStats();
  };

  const handleSubmit = async (payload: SubmitDTO) => {
    const isUpdate = 'id' in payload;

    try {
      if (isUpdate && editingPayment && isInvoiceManagedPayment(editingPayment)) {
        await apiService.updateInvoiceManagedPayment(payload.id, payload);
      } else if (isUpdate) await updatePayment(payload);
      else await createPayment(payload);

      await refreshPayments();
      bumpStats();
      toast.success(isUpdate ? 'Платёж обновлён.' : 'Платёж создан.', 'Платежи');
    } catch (error) {
      toast.error(
        extractDisplayErrorMessage(
          error,
          isUpdate ? 'Не удалось обновить платёж.' : 'Не удалось создать платёж.',
        ),
        'Платежи',
      );
      throw error;
    }
  };

  const handleTransferRemainder = async (payload: SubmitDTO & { id: number }, remainderDate: string) => {
    try {
      await apiService.transferPaymentRemainder(payload.id, payload, remainderDate);
      await refreshPayments();
      bumpStats();
      toast.success('Остаток платежа перенесён.', 'Платежи');
    } catch (error) {
      toast.error(
        extractDisplayErrorMessage(error, 'Не удалось перенести остаток платежа.'),
        'Платежи',
      );
      throw error;
    }
  };

  const handleRollbackRemainderTransfer = async (paymentId: number) => {
    try {
      await apiService.rollbackPaymentRemainderTransfer(paymentId);
      await refreshPayments();
      bumpStats();
      toast.success('Перенос остатка отменен.', 'Платежи');
    } catch (error) {
      toast.error(
        extractDisplayErrorMessage(error, 'Не удалось откатить перенос остатка.'),
        'Платежи',
      );
      throw error;
    }
  };

  const handleAdjustRemainderTransfer = async (paymentId: number, paidAmount: number, remainderDate: string) => {
    try {
      await apiService.adjustPaymentRemainderTransfer(paymentId, paidAmount, remainderDate);
      await refreshPayments();
      bumpStats();
      toast.success('Разделение платежа обновлено.', 'Платежи');
    } catch (error) {
      toast.error(
        extractDisplayErrorMessage(error, 'Не удалось изменить разделение платежа.'),
        'Платежи',
      );
      throw error;
    }
  };

  const filteredPayments = useMemo(() => {
    const q = deferredSearch.trim().toLowerCase();
    return payments.filter((p) => {
      const statusOk = statusFilter === 'All' || p.status === statusFilter;
      if (!q) return statusOk;
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
      const match = fields.some((v) => (v ?? '').toLowerCase().includes(q));
      return statusOk && match;
    });
  }, [payments, deferredSearch, statusFilter]);

  const clearRange = () => setRange({});

  const tableFilteredPayments = useMemo(() => {
    if (!tableFilter) return filteredPayments;
    return filteredPayments.filter((p) => {
      if (tableFilter.type && p.type !== tableFilter.type) return false;
      if (tableFilter.statuses && tableFilter.statuses.length > 0) {
        return tableFilter.statuses.includes(p.status);
      }
      return true;
    });
  }, [filteredPayments, tableFilter]);

  const paymentColumns = useMemo(
    () => [
      { key: 'date' as const, label: t('date') ?? 'Дата' },
      { key: 'plannedDate' as const, label: 'Плановая дата' },
      { key: 'client' as const, label: t('clients') ?? 'Клиенты' },
      { key: 'description' as const, label: t('description') ?? 'Описание' },
      { key: 'type' as const, label: t('type') ?? 'Тип' },
      { key: 'status' as const, label: t('status') ?? 'Статус' },
      { key: 'category' as const, label: t('category') ?? 'Категория' },
      { key: 'amount' as const, label: t('amount') ?? 'Сумма' },
      { key: 'paidDate' as const, label: 'Дата оплаты' },
      { key: 'createdAt' as const, label: t('createdAt') ?? 'Создан' },
    ],
    [t],
  );

  const paymentColumnsByKey = useMemo(
    () =>
      Object.fromEntries(paymentColumns.map((column) => [column.key, column])) as Record<
        PaymentTableColumnKey,
        (typeof paymentColumns)[number]
      >,
    [paymentColumns],
  );

  const visiblePaymentColumns = useMemo(
    () => orderedVisibleColumns.map((key) => paymentColumnsByKey[key]).filter(Boolean),
    [orderedVisibleColumns, paymentColumnsByKey],
  );

  const tableTotalPages = Math.max(1, Math.ceil(tableFilteredPayments.length / pageSize));

  const pagedTablePayments = useMemo(() => {
    const start = (tablePage - 1) * pageSize;
    return tableFilteredPayments.slice(start, start + pageSize);
  }, [pageSize, tableFilteredPayments, tablePage]);

  const handleViewModeChange = (mode: CalendarViewMode) => {
    setViewMode(mode);
  };

  const handleSectionModeChange = (mode: CalendarSectionMode) => {
    if (mode === 'finance' && !canViewFinancialCalendar) {
      showPermissionNotice('Доступ к финансовому календарю запрещен для вашей роли.');
      return;
    }

    if (mode === 'crm' && !canViewTaskCalendar) {
      showPermissionNotice('Доступ к рабочему CRM-календарю задач запрещен для вашей роли.');
      return;
    }

    setSectionMode(mode);
    try {
      localStorage.setItem(SECTION_MODE_STORAGE_KEY, mode);
    } catch {
      /** */
    }
  };

  const handleClearTableFilter = () => setTableFilter(null);

  const handleTableSortChange = (columnKey: PaymentTableColumnKey) => {
    setTablePage(1);
    if (tableSortBy === columnKey) {
      setTableSortDir((current) => (current === 'asc' ? 'desc' : 'asc'));
      return;
    }

    setTableSortBy(columnKey);
    setTableSortDir(columnKey === 'date' || columnKey === 'plannedDate' || columnKey === 'paidDate' || columnKey === 'createdAt' ? 'desc' : 'asc');
  };

  const handleStatCardSelect = ({
    kind,
    metric,
    title,
  }: {
    kind: 'Income' | 'Expense';
    metric: StatMetric;
    title: string;
  }) => {
    if (!calendarPermissions.canViewAnalytics) return;
    const statusesByMetric: Record<StatMetric, Payment['status'][] | undefined> = {
      completed: ['Completed'],
      pending: ['Pending'],
      overdue: ['Overdue'],
      overall: undefined,
      debt: ['Pending', 'Overdue'],
    };
    const typeLabel =
      kind === 'Income'
        ? t('incomePlural') ?? 'Доходы'
        : t('expensePlural') ?? 'Расходы';
    const label = `${typeLabel} · ${title}`;
    setTableFilter({
      type: kind,
      statuses: statusesByMetric[metric],
      label,
      metric,
    });
    setViewMode('table');
  };

  useEffect(() => {
    if (viewMode !== 'table') {
      setTableSettingsOpen(false);
    }
  }, [viewMode]);

  useEffect(() => {
    setTablePage(1);
  }, [deferredSearch, fromDateStr, pageSize, statusFilter, tableFilter, toDateStr]);

  useEffect(() => {
    if (tablePage > tableTotalPages) {
      setTablePage(tableTotalPages);
    }
  }, [tablePage, tableTotalPages]);

  return (
    <div className="min-h-screen bg-gray-50">
      <div className="max-w-[calc(100vw-2rem)] mx-auto p-4 sm:p-6">
        <div className="mb-4 flex flex-col gap-3 rounded-2xl border border-slate-200 bg-white p-3 shadow-sm sm:flex-row sm:items-center sm:justify-between">
          <div>
            <div className="text-sm font-semibold text-slate-900">Календарь</div>
            <div className="text-xs text-slate-500">
              {canViewFinancialCalendar && canViewTaskCalendar
                ? 'Финансы и рабочий CRM-календарь разделены, чтобы платежи не смешивались с задачами и сроками.'
                : canViewTaskCalendar
                  ? 'Рабочий календарь показывает доступные задачи и сроки.'
                  : canViewFinancialCalendar
                    ? 'Финансовый календарь показывает платежи и сроки оплаты по доступным правам.'
                    : 'Для вашей роли пока не открыт доступ к данным календаря.'}
            </div>
          </div>
          <div className="inline-flex rounded-xl bg-slate-100 p-1">
            {canViewFinancialCalendar ? (
            <button
              type="button"
              onClick={() => handleSectionModeChange('finance')}
              aria-pressed={sectionMode === 'finance'}
              className={[
                'rounded-lg px-4 py-2 text-sm font-semibold transition',
                sectionMode === 'finance'
                  ? 'bg-white text-emerald-700 shadow-sm'
                  : 'text-slate-500 hover:text-slate-800',
              ].join(' ')}
            >
              Финансы
            </button>
            ) : null}
            {canViewTaskCalendar ? (
              <button
                type="button"
                onClick={() => handleSectionModeChange('crm')}
                aria-pressed={sectionMode === 'crm'}
                className={[
                  'rounded-lg px-4 py-2 text-sm font-semibold transition',
                  sectionMode === 'crm'
                    ? 'bg-white text-blue-700 shadow-sm'
                    : 'text-slate-500 hover:text-slate-800',
                ].join(' ')}
              >
                CRM
              </button>
            ) : null}
          </div>
        </div>

        {!canViewFinancialCalendar && !canViewTaskCalendar ? (
          <div className="rounded-xl border border-amber-200 bg-amber-50 px-4 py-5 text-sm text-amber-700 shadow-sm">
            Данные календаря недоступны для вашей роли. Обратитесь к администратору, если этот раздел нужен для работы.
          </div>
        ) : sectionMode === 'crm' && canViewTaskCalendar ? (
          <Suspense
            fallback={
              <div className="rounded-2xl border border-slate-200 bg-white p-6 text-sm text-slate-500 shadow-sm">
                Загружаем рабочий календарь...
              </div>
            }
          >
            <TaskJournal
              initialView="timeline"
              onOpenClient={(clientId, caseId) => onOpenClient?.(clientId, caseId ?? undefined)}
            />
          </Suspense>
        ) : canViewFinancialCalendar ? (
          <>
        <div className="mb-4 sm:mb-8">
          <div className="flex flex-col items-center gap-3 sm:flex-row sm:items-center sm:justify-between">
            <h1 className="text-2xl sm:text-3xl font-bold text-gray-900 leading-tight text-center sm:text-left">
              {t('payPlanner')}
            </h1>

            <div className="flex items-center gap-4 sm:gap-6">
              {!range.from && !range.to && (
                <button
                  type="button"
                  onClick={() => navigateMonth('prev')}
                  className="p-2 rounded-lg border border-gray-300 hover:bg-gray-50 transition-colors"
                  aria-label="Previous month">
                  <ChevronLeft className="h-4 w-4 sm:h-5 sm:w-5" />
                </button>
              )}

              <h2 className="text-lg sm:text-xl font-semibold text-gray-900 text-center truncate max-w-[70vw] sm:max-w-none">
                {range.from || range.to ? formatRange() : formatMonth(currentDate)}
              </h2>

              {!range.from && !range.to && (
                <button
                  type="button"
                  onClick={() => navigateMonth('next')}
                  className="p-2 rounded-lg border border-gray-300 hover:bg-gray-50 transition-colors"
                  aria-label="Next month">
                  <ChevronRight className="h-4 w-4 sm:h-5 sm:w-5" />
                </button>
              )}
            </div>
          </div>

        <div className="mt-3 w-full grid gap-3 xl:grid-cols-[1fr_auto] items-start">
          <div className="flex flex-col sm:flex-row flex-wrap gap-3 items-stretch sm:items-center">
            <div className="relative w-full sm:w-72 lg:w-80 max-w-full">
              <Search
                className="absolute left-3 top-1/2 -translate-y-1/2 text-gray-400"
                size={18}
              />
              <input
                value={search}
                onChange={(e) => setSearch(e.target.value)}
                placeholder={t('search') ?? 'Search payments...'}
                className="w-full pl-9 pr-3 py-2 bg-white border border-gray-300 rounded-lg text-sm focus:outline-none focus:ring-2 focus:ring-emerald-500 focus:border-emerald-500"
                aria-label={t('search') ?? 'Search'}
              />
            </div>

            <SystemSelect
              value={statusFilter}
              onChange={(e) => setStatusFilter(e.target.value as StatusFilter)}
              className="w-full sm:w-auto sm:min-w-[220px] bg-white border border-gray-300 rounded-lg px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-emerald-500 focus:border-emerald-500"
              title={t('status') ?? 'Статус'}
              aria-label={t('status') ?? 'Статус'}>
              <option value="All">{t('allStatuses') ?? 'All statuses'}</option>
              <option value="Pending">{t('pending') ?? 'Pending'}</option>
              <option value="Completed">{t('completed') ?? 'Completed'}</option>
              <option value="Overdue">{t('overdue') ?? 'Overdue'}</option>
            </SystemSelect>

            <div className="flex items-center gap-2 w-full sm:w-auto">
              <MonthRangePicker
                value={{ from: range.from, to: range.to }}
                onChange={(r) => setRange(r)}
                yearsBack={8}
                yearsForward={1}
              />
              {(range.from || range.to) && (
                <button
                  type="button"
                  onClick={clearRange}
                  className="inline-flex items-center gap-1 px-2 py-2 text-sm rounded-lg bg-white border border-gray-300 hover:bg-gray-50"
                  title={t('clear') ?? 'Clear'}>
                  <X className="w-4 h-4" />
                </button>
              )}
            </div>
          </div>

          <div className="flex justify-end items-center">
            <div className="flex w-full sm:w-auto justify-end gap-1 bg-gray-100 p-1 rounded-lg">
              <button
                type="button"
                onClick={() => handleViewModeChange('calendar')}
                aria-pressed={viewMode === 'calendar'}
                className={[
                  'inline-flex items-center gap-2 px-3 py-2 rounded-lg text-sm font-medium transition-colors',
                  viewMode === 'calendar'
                    ? 'bg-white text-emerald-600 shadow-sm'
                    : 'text-gray-600 hover:text-gray-900',
                  'focus:outline-none focus-visible:ring-2 focus-visible:ring-emerald-500/60',
                ].join(' ')}
                title={t('calendarView') ?? 'Календарь'}>
                <CalendarDays className="h-4 w-4" />
                <span className="hidden sm:inline">{t('calendarView') ?? 'Календарь'}</span>
              </button>
              <button
                type="button"
                onClick={() => handleViewModeChange('table')}
                aria-pressed={viewMode === 'table'}
                className={[
                  'inline-flex items-center gap-2 px-3 py-2 rounded-lg text-sm font-medium transition-colors',
                  viewMode === 'table'
                    ? 'bg-white text-emerald-600 shadow-sm'
                    : 'text-gray-600 hover:text-gray-900',
                  'focus:outline-none focus-visible:ring-2 focus-visible:ring-emerald-500/60',
                ].join(' ')}
                title={t('tableView') ?? 'Таблица'}>
                <Table className="h-4 w-4" />
                <span className="hidden sm:inline">{t('tableView') ?? 'Таблица'}</span>
              </button>
            </div>
          </div>
        </div>
        </div>

        {permissionMessage ? (
          <div className="mt-3 rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-700">
            {permissionMessage}
          </div>
        ) : null}

        {calendarPermissions.canViewAnalytics ? (
          <TwoTypeStats
            from={fromDateStr}
            to={toDateStr}
            statusFilter={statusFilter}
            search={deferredSearch}
            reloadToken={statsReloadKey}
            rawPayments={payments}
            storageKey={`pp.calendar.stats_panels.${user?.id ?? 'guest'}`}
            onCardSelect={handleStatCardSelect}
          />
        ) : (
          <div className="mt-6 rounded-xl border border-amber-200 bg-amber-50 px-4 py-5 text-sm text-amber-700 shadow-sm">
            {t('permissionNoAnalytics') ?? 'Просмотр аналитики недоступен для вашей роли.'}
          </div>
        )}

        <div className="grid grid-cols-1 sm:flex sm:flex-row gap-3 mb-6 mt-4">
          <button
            type="button"
            onClick={() => handleAddPayment('Income')}
            disabled={!calendarPermissions.canCreate}
            className="inline-flex items-center justify-center gap-2 px-4 py-3 text-sm sm:text-base font-medium rounded-lg bg-emerald-600 text-white hover:bg-emerald-700 transition-colors shadow-sm disabled:opacity-60 disabled:cursor-not-allowed"
            title={
              !calendarPermissions.canCreate
                ? t('permissionNoAddPayment') ?? 'Недостаточно прав для добавления платежей.'
                : undefined
            }
            aria-disabled={!calendarPermissions.canCreate}
          >
            <Plus className="h-4 w-4" /> {t('addIncome')}
          </button>

          <button
            type="button"
            onClick={() => handleAddPayment('Expense')}
            disabled={!calendarPermissions.canCreate}
            className="inline-flex items-center justify-center gap-2 px-4 py-3 text-sm sm:text-base font-medium rounded-lg bg-white text-gray-700 border border-gray-300 hover:bg-gray-50 transition-colors shadow-sm disabled:opacity-60 disabled:cursor-not-allowed"
            title={
              !calendarPermissions.canCreate
                ? t('permissionNoAddPayment') ?? 'Недостаточно прав для добавления платежей.'
                : undefined
            }
            aria-disabled={!calendarPermissions.canCreate}
          >
            <Plus className="h-4 w-4" /> {t('addExpense')}
          </button>
        </div>

        {viewMode === 'table' && (
          <div className="mb-6 -mt-2 rounded-xl border border-emerald-100 bg-white/80 px-4 py-3 sm:flex sm:items-center sm:justify-between">
            <div className="text-sm text-gray-600 space-y-1 sm:space-y-0">
              <div className="font-semibold text-gray-900">
                {tableFilter
                  ? `${t('activeFilter') ?? 'Активный фильтр:'} ${tableFilter.label}`
                  : t('filteredSummary') ?? 'Сводка по текущему фильтру'}
              </div>
              <div className="text-gray-500">
                {(t('recordsFound') ?? 'Найдено записей:')} {tableFilteredPayments.length}
              </div>
            </div>
            <div className="mt-3 flex items-center justify-end gap-2 sm:mt-0">
              <button
                type="button"
                onClick={() => setTableSettingsOpen((prev) => !prev)}
                className={`inline-flex items-center gap-2 rounded-lg border px-3 py-2 text-sm font-medium ${
                  tableSettingsOpen
                    ? 'border-blue-200 bg-blue-50 text-blue-700'
                    : 'border-slate-200 bg-white text-slate-700 hover:bg-slate-50'
                }`}>
                <SlidersHorizontal className="h-4 w-4" />
                {tableSettingsOpen ? 'Скрыть колонки' : 'Настроить таблицу'}
              </button>
              {tableFilter ? (
                <button
                  type="button"
                  onClick={handleClearTableFilter}
                  className="inline-flex items-center gap-2 rounded-lg border border-emerald-200 px-3 py-2 text-sm font-medium text-emerald-700 hover:bg-emerald-50">
                  <X className="h-4 w-4" /> {t('clear') ?? 'Очистить'}
                </button>
              ) : null}
            </div>
          </div>
        )}

        {viewMode === 'table' && tableSettingsOpen ? (
          <div className="mb-6">
            <TableColumnSettings
              columns={paymentColumns}
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
                  <span>Строк на странице</span>
                  <SystemSelect
                    value={pageSize}
                    onChange={(event) => setPageSize(Number(event.target.value) || pageSize)}
                    className="rounded-lg border border-slate-300 bg-white px-2 py-1 text-sm text-slate-700">
                    {CALENDAR_TABLE_PAGE_SIZE_OPTIONS.map((size) => (
                      <option key={size} value={size}>
                        {size}
                      </option>
                    ))}
                  </SystemSelect>
                </div>
              }
            />
          </div>
        ) : null}

        {viewMode === 'calendar' ? (
          <CalendarGrid
            currentDate={currentDate}
            payments={filteredPayments}
            onEditPayment={handleEditPayment}
            newPaymentIds={newIds}
            onOpenClient={
              canOpenLinkedClients
                ? (clientId, caseId) => onOpenClient?.(clientId, canOpenLinkedCases ? caseId : undefined)
                : undefined
            }
            onOpenUser={onOpenUser}
          />
        ) : (
          <>
            <PaymentsTable
              payments={pagedTablePayments}
              onEditPayment={handleEditPayment}
              onOpenClient={onOpenClient}
              onOpenUser={onOpenUser}
              onAddPayment={handleAddPayment}
              onDeletePayment={handleDeletePayment}
              canAdd={calendarPermissions.canCreate}
              canEdit={calendarPermissions.canEdit}
              canDelete={calendarPermissions.canDelete}
              canOpenClients={canOpenLinkedClients}
              canOpenCases={canOpenLinkedCases}
              canOpenUsers={Boolean(onOpenUser)}
              canEditPayment={() => true}
              canDeletePayment={(payment) => payment.documentKind !== 'Invoice' && !isTransferredRemainderPayment(payment) && !isPartialRemainderSourcePayment(payment)}
              columns={visiblePaymentColumns}
              draggingColumn={draggingColumn}
              onColumnDragStart={handleColumnDragStart}
              onColumnDragOver={handleColumnDragOver}
              onColumnDrop={handleColumnDrop}
              onColumnDragEnd={handleColumnDragEnd}
              onResizeStart={handleResizeStart}
              getColumnWidth={getColumnWidth}
              getTableWidth={getTableWidth}
              sortBy={tableSortBy}
              sortDir={tableSortDir}
              onSortChange={handleTableSortChange}
            />
            {tableFilteredPayments.length > 0 ? (
              <TablePagination
                page={tablePage}
                totalPages={tableTotalPages}
                totalItems={tableFilteredPayments.length}
                pageSize={pageSize}
                pageSizeOptions={CALENDAR_TABLE_PAGE_SIZE_OPTIONS}
                onPageChange={setTablePage}
                onPageSizeChange={setPageSize}
              />
            ) : null}
          </>
        )}

        <PaymentModal
          isOpen={isModalOpen}
          onClose={handleCloseModal}
          type={modalType}
          onSubmit={handleSubmit}
          onCreateRemainder={createPayment}
          onTransferRemainder={handleTransferRemainder}
          onRollbackRemainderTransfer={handleRollbackRemainderTransfer}
          onAdjustRemainderTransfer={handleAdjustRemainderTransfer}
          canViewLinkedInvoices={permissions.accounts?.canView === true}
          canCreateLinkedInvoices={permissions.accounts?.canView === true && permissions.accounts?.canCreate === true}
          onBalanceApplied={async () => {
            await refreshPayments();
            bumpStats();
          }}
          payment={editingPayment}
          onDelete={
            calendarPermissions.canDelete
            && (!editingPayment || (editingPayment.documentKind !== 'Invoice' && !isTransferredRemainderPayment(editingPayment) && !isPartialRemainderSourcePayment(editingPayment)))
              ? async (id) => {
                  try {
                    await deletePayment(id);
                    await refreshPayments();
                    setIsModalOpen(false);
                    setEditingPayment(null);
                    setTimeout(() => bumpStats(), 0);
                    toast.error('Платёж удалён.', 'Платежи');
                  } catch (error) {
                    toast.error(
                      extractDisplayErrorMessage(error, 'Не удалось удалить платёж.'),
                      'Платежи',
                    );
                    throw error;
                  }
                }
              : undefined
          }
        />
          </>
        ) : null}
      </div>
    </div>
  );
}
