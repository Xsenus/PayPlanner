import { useMemo, useState, useDeferredValue, useEffect, useRef } from 'react';
import { CalendarDays, ChevronLeft, ChevronRight, Plus, Search, Table, X } from 'lucide-react';
import { CalendarGrid } from './CalendarGrid';
import { PaymentModal } from './PaymentModal';
import { PaymentsTable } from './PaymentsTable';
import { usePayments } from '../../hooks/usePayments';
import { useTranslation } from '../../hooks/useTranslation';
import { MonthRangePicker, type MonthRange } from '../MonthRange/MonthRangePicker';
import type { Payment } from '../../types';
import { formatLocalYMD } from '../../utils/dateUtils';
import { TwoTypeStats } from '../Statistics/TwoTypeStats';
import { useAuth } from '../../contexts/AuthContext';
import { useRolePermissions } from '../../hooks/useRolePermissions';

type CreatePaymentDTO = Omit<Payment, 'id' | 'createdAt'>;
type UpdatePaymentDTO = { id: number } & CreatePaymentDTO;
type SubmitDTO = CreatePaymentDTO | UpdatePaymentDTO;
type StatusFilter = 'All' | 'Pending' | 'Completed' | 'Overdue';

type StatMetric = 'completed' | 'pending' | 'overdue' | 'overall' | 'debt';
type CalendarViewMode = 'calendar' | 'table';
type TableFilter = {
  type?: 'Income' | 'Expense';
  statuses?: Payment['status'][];
  label: string;
  metric: StatMetric;
};

const VIEW_MODE_STORAGE_KEY = 'pp.calendar_view';

type CalendarProps = {
  onOpenClient?: (clientId: number, caseId?: number) => void;
};

function makeStatsSignature(payments: Payment[]): string {
  if (!payments || payments.length === 0) return 'empty';
  return payments
    .map((p) => `${p.id}:${p.amount}:${p.status}:${p.type}`)
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

export function Calendar({ onOpenClient }: CalendarProps) {
  const { user } = useAuth();
  const [currentDate, setCurrentDate] = useState(new Date());
  const [isModalOpen, setIsModalOpen] = useState(false);
  const [modalType, setModalType] = useState<'Income' | 'Expense'>('Income');
  const [editingPayment, setEditingPayment] = useState<Payment | null>(null);

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
  const permissions = useRolePermissions(user?.role?.id);
  const calendarPermissions = permissions.calendar;
  const [permissionMessage, setPermissionMessage] = useState<string | null>(null);

  const [statsReloadKey, setStatsReloadKey] = useState(0);
  const bumpStats = () => setStatsReloadKey((x) => x + 1);

  const { t, formatMonth } = useTranslation();
  const permissionTimerRef = useRef<number | null>(null);

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
  } = usePayments(fromDateStr, toDateStr, { pollInterval });

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
    if (!calendarPermissions.canAddPayments) {
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
    if (!calendarPermissions.canEditPayments) {
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
    if (!calendarPermissions.canDeletePayments) {
      showPermissionNotice(
        t('permissionNoDeletePayment') ?? 'Недостаточно прав для удаления платежей.',
      );
      return;
    }
    const ok = window.confirm(
      t('confirmDeletePayment') ?? 'Вы уверены, что хотите удалить этот платёж?',
    );
    if (!ok) return;
    await deletePayment(payment.id);
    await refreshPayments();
    bumpStats();
  };

  const handleCloseModal = async () => {
    setIsModalOpen(false);
    setEditingPayment(null);
    await refreshPayments();
    bumpStats();
  };

  const handleSubmit = async (payload: SubmitDTO) => {
    if ('id' in payload) await updatePayment(payload);
    else await createPayment(payload);

    await refreshPayments();
    bumpStats();
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

  const handleViewModeChange = (mode: CalendarViewMode) => {
    setViewMode(mode);
  };

  const handleClearTableFilter = () => setTableFilter(null);

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

  return (
    <div className="min-h-screen bg-gray-50">
      <div className="max-w-[calc(100vw-2rem)] mx-auto p-4 sm:p-6">
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

            <select
              value={statusFilter}
              onChange={(e) => setStatusFilter(e.target.value as StatusFilter)}
              className="w-full sm:w-auto sm:min-w-[220px] bg-white border border-gray-300 rounded-lg px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-emerald-500 focus:border-emerald-500"
              title={t('status') ?? 'Статус'}
              aria-label={t('status') ?? 'Статус'}>
              <option value="All">{t('allStatuses') ?? 'All statuses'}</option>
              <option value="Pending">{t('pending') ?? 'Pending'}</option>
              <option value="Completed">{t('completed') ?? 'Completed'}</option>
              <option value="Overdue">{t('overdue') ?? 'Overdue'}</option>
            </select>

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
            disabled={!calendarPermissions.canAddPayments}
            className="inline-flex items-center justify-center gap-2 px-4 py-3 text-sm sm:text-base font-medium rounded-lg bg-emerald-600 text-white hover:bg-emerald-700 transition-colors shadow-sm disabled:opacity-60 disabled:cursor-not-allowed"
            title={
              !calendarPermissions.canAddPayments
                ? t('permissionNoAddPayment') ?? 'Недостаточно прав для добавления платежей.'
                : undefined
            }
            aria-disabled={!calendarPermissions.canAddPayments}
          >
            <Plus className="h-4 w-4" /> {t('addIncome')}
          </button>

          <button
            type="button"
            onClick={() => handleAddPayment('Expense')}
            disabled={!calendarPermissions.canAddPayments}
            className="inline-flex items-center justify-center gap-2 px-4 py-3 text-sm sm:text-base font-medium rounded-lg bg-white text-gray-700 border border-gray-300 hover:bg-gray-50 transition-colors shadow-sm disabled:opacity-60 disabled:cursor-not-allowed"
            title={
              !calendarPermissions.canAddPayments
                ? t('permissionNoAddPayment') ?? 'Недостаточно прав для добавления платежей.'
                : undefined
            }
            aria-disabled={!calendarPermissions.canAddPayments}
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
            {tableFilter ? (
              <button
                type="button"
                onClick={handleClearTableFilter}
                className="mt-3 sm:mt-0 inline-flex items-center gap-2 rounded-lg border border-emerald-200 px-3 py-2 text-sm font-medium text-emerald-700 hover:bg-emerald-50">
                <X className="h-4 w-4" /> {t('clear') ?? 'Очистить'}
              </button>
            ) : null}
          </div>
        )}

        {viewMode === 'calendar' ? (
          <CalendarGrid
            currentDate={currentDate}
            payments={filteredPayments}
            onEditPayment={handleEditPayment}
            newPaymentIds={newIds}
            onOpenClient={onOpenClient}
          />
        ) : (
          <PaymentsTable
            payments={tableFilteredPayments}
            onEditPayment={handleEditPayment}
            onOpenClient={onOpenClient}
            onAddPayment={handleAddPayment}
            onDeletePayment={handleDeletePayment}
            canAdd={calendarPermissions.canAddPayments}
            canEdit={calendarPermissions.canEditPayments}
            canDelete={calendarPermissions.canDeletePayments}
          />
        )}

        <PaymentModal
          isOpen={isModalOpen}
          onClose={handleCloseModal}
          type={modalType}
          onSubmit={handleSubmit}
          payment={editingPayment}
          onDelete={
            calendarPermissions.canDeletePayments
              ? async (id) => {
                  await deletePayment(id);
                  await refreshPayments();
                  setIsModalOpen(false);
                  setEditingPayment(null);
                  setTimeout(() => bumpStats(), 0);
                }
              : undefined
          }
        />
      </div>
    </div>
  );
}
