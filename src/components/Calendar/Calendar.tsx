import { useMemo, useState, useDeferredValue, useEffect, useRef } from 'react';
import { ChevronLeft, ChevronRight, Plus, Search, X } from 'lucide-react';
import { CalendarGrid } from './CalendarGrid';
import { PaymentModal } from './PaymentModal';
import { usePayments } from '../../hooks/usePayments';
import { useTranslation } from '../../hooks/useTranslation';
import { MonthRangePicker, type MonthRange } from '../MonthRange/MonthRangePicker';
import type { Payment } from '../../types';
import { formatLocalYMD } from '../../utils/dateUtils';
import { TwoTypeStats } from '../Statistics/TwoTypeStats';

type CreatePaymentDTO = Omit<Payment, 'id' | 'createdAt'>;
type UpdatePaymentDTO = { id: number } & CreatePaymentDTO;
type SubmitDTO = CreatePaymentDTO | UpdatePaymentDTO;
type StatusFilter = 'All' | 'Pending' | 'Completed' | 'Overdue';

type CalendarProps = {
  onOpenClient?: (clientId: number, caseId?: number) => void;
};

function ymStart(ym: string): string {
  const [y, m] = ym.split('-').map(Number);
  return formatLocalYMD(new Date(y, (m ?? 1) - 1, 1));
}
function ymEnd(ym: string): string {
  const [y, m] = ym.split('-').map(Number);
  return formatLocalYMD(new Date(y, m ?? 1, 0));
}

export function Calendar({ onOpenClient }: CalendarProps) {
  const [currentDate, setCurrentDate] = useState(new Date());
  const [isModalOpen, setIsModalOpen] = useState(false);
  const [modalType, setModalType] = useState<'Income' | 'Expense'>('Income');
  const [editingPayment, setEditingPayment] = useState<Payment | null>(null);

  const [search, setSearch] = useState('');
  const deferredSearch = useDeferredValue(search);
  const [statusFilter, setStatusFilter] = useState<StatusFilter>('All');
  const [range, setRange] = useState<MonthRange>({});

  const [statsReloadKey, setStatsReloadKey] = useState(0);
  const bumpStats = () => setStatsReloadKey((x) => x + 1);

  const { t, formatMonth } = useTranslation();

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

  const navigateMonth = (direction: 'prev' | 'next') => {
    setCurrentDate((prev) => {
      const d = new Date(prev);
      d.setMonth(prev.getMonth() + (direction === 'next' ? 1 : -1));
      return d;
    });
  };

  const handleAddPayment = (type: 'Income' | 'Expense') => {
    setModalType(type);
    setEditingPayment(null);
    setIsModalOpen(true);
  };

  const handleEditPayment = (payment: Payment) => {
    setEditingPayment(payment);
    setModalType(payment.type);
    setIsModalOpen(true);
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

  return (
    <div className="min-h-screen bg-gray-50">
      <div className="max-w-7xl mx-auto p-4 sm:p-6">
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

          <div className="mt-3 w-full flex flex-col sm:flex-row gap-3 items-center sm:justify-end">
            <div className="relative w-full max-w-md sm:w-80">
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
              title={t('statusFilter') ?? 'Status filter'}
              aria-label={t('statusFilter') ?? 'Status filter'}>
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
        </div>

        <TwoTypeStats
          from={fromDateStr}
          to={toDateStr}
          statusFilter={statusFilter}
          search={deferredSearch}
          reloadToken={statsReloadKey}
        />

        <div className="grid grid-cols-1 sm:flex sm:flex-row gap-3 mb-6 mt-4">
          <button
            type="button"
            onClick={() => handleAddPayment('Income')}
            className="inline-flex items-center justify-center gap-2 px-4 py-3 text-sm sm:text-base font-medium rounded-lg bg-emerald-600 text-white hover:bg-emerald-700 transition-colors shadow-sm">
            <Plus className="h-4 w-4" /> {t('addIncome')}
          </button>

          <button
            type="button"
            onClick={() => handleAddPayment('Expense')}
            className="inline-flex items-center justify-center gap-2 px-4 py-3 text-sm sm:text-base font-medium rounded-lg bg-white text-gray-700 border border-gray-300 hover:bg-gray-50 transition-colors shadow-sm">
            <Plus className="h-4 w-4" /> {t('addExpense')}
          </button>
        </div>

        <CalendarGrid
          currentDate={currentDate}
          payments={filteredPayments}
          onEditPayment={handleEditPayment}
          newPaymentIds={newIds}
          onOpenClient={onOpenClient}
        />

        <PaymentModal
          isOpen={isModalOpen}
          onClose={handleCloseModal}
          type={modalType}
          onSubmit={handleSubmit}
          payment={editingPayment}
          onDelete={async (id) => {
            if (!window.confirm('Удалить платёж?')) return;
            await deletePayment(id);
            await refreshPayments();
            setIsModalOpen(false);
            setEditingPayment(null);
            setTimeout(() => bumpStats(), 0);
          }}
        />
      </div>
    </div>
  );
}
