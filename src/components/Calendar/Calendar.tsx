import { useMemo, useState, useDeferredValue, useEffect, useRef } from 'react';
import { ChevronLeft, ChevronRight, Plus, Search } from 'lucide-react';
import { CalendarGrid } from './CalendarGrid';
import { SummaryCards } from './SummaryCards';
import { PaymentModal } from './PaymentModal';
import { usePayments } from '../../hooks/usePayments';
import { useMonthlyStats } from '../../hooks/useMonthlyStats';
import { useTranslation } from '../../hooks/useTranslation';
import type { MonthlyStats, Payment } from '../../types';

type CreatePaymentDTO = Omit<Payment, 'id' | 'createdAt'>;
type UpdatePaymentDTO = { id: number } & CreatePaymentDTO;
type SubmitDTO = CreatePaymentDTO | UpdatePaymentDTO;
type StatusFilter = 'All' | 'Pending' | 'Completed' | 'Overdue';

type CalendarProps = {
  onOpenClient?: (clientId: number, caseId?: number) => void;
};

function computeMonthlyStatsFrom(payments: Payment[]): MonthlyStats {
  const completed = payments.filter((p) => p.isPaid).length;
  const pending = payments.filter((p) => !p.isPaid && p.status === 'Pending').length;
  const overdue = payments.filter((p) => !p.isPaid && p.status === 'Overdue').length;
  const total = payments.length;

  const income = payments
    .filter((p) => p.type === 'Income' && p.isPaid)
    .reduce((s, p) => s + (p.amount ?? 0), 0);

  const expense = payments
    .filter((p) => p.type === 'Expense' && p.isPaid)
    .reduce((s, p) => s + (p.amount ?? 0), 0);

  const profit = income - expense;
  const completionRate = total > 0 ? Math.round((completed / total) * 1000) / 10 : 0;

  const { completedAmount, pendingAmount, overdueAmount } = computeAmounts(payments);

  return {
    income,
    expense,
    profit,
    completionRate,
    counts: { completed, pending, overdue, total },
    completedAmount,
    pendingAmount,
    overdueAmount,
  };
}

function computeAmounts(payments: Payment[]) {
  const income = payments
    .filter((p) => p.type === 'Income' && p.isPaid)
    .reduce((s, p) => s + (p.amount ?? 0), 0);

  const expense = payments
    .filter((p) => p.type === 'Expense' && p.isPaid)
    .reduce((s, p) => s + (p.amount ?? 0), 0);

  const pendingAmount = payments
    .filter((p) => !p.isPaid && p.status === 'Pending')
    .reduce((s, p) => s + (p.amount ?? 0), 0);

  const overdueAmount = payments
    .filter((p) => !p.isPaid && p.status === 'Overdue')
    .reduce((s, p) => s + (p.amount ?? 0), 0);

  return {
    completedAmount: income - expense,
    pendingAmount,
    overdueAmount,
  };
}

export function Calendar({ onOpenClient }: CalendarProps) {
  const [currentDate, setCurrentDate] = useState(new Date());
  const [isModalOpen, setIsModalOpen] = useState(false);
  const [modalType, setModalType] = useState<'Income' | 'Expense'>('Income');
  const [editingPayment, setEditingPayment] = useState<Payment | null>(null);

  const [search, setSearch] = useState('');
  const deferredSearch = useDeferredValue(search);
  const [statusFilter, setStatusFilter] = useState<StatusFilter>('All');

  const { t, formatMonth } = useTranslation();

  const year = currentDate.getFullYear();
  const month = currentDate.getMonth() + 1;

  const startOfMonth = new Date(year, month - 1, 1);
  const endOfMonth = new Date(year, month, 0);

  const pollInterval = isModalOpen ? 0 : 5000;

  const {
    payments,
    createPayment,
    updatePayment,
    deletePayment,
    refresh: refreshPayments,
  } = usePayments(
    startOfMonth.toISOString().split('T')[0],
    endOfMonth.toISOString().split('T')[0],
    { pollInterval },
  );

  const { stats, refresh: refreshStats } = useMonthlyStats(year, month, {
    pollInterval,
  });

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
    await Promise.all([refreshPayments(), refreshStats()]);
  };

  const handleSubmit = async (payload: SubmitDTO) => {
    if ('id' in payload) await updatePayment(payload);
    else await createPayment(payload);
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

  const hasFilters = deferredSearch.trim().length > 0 || statusFilter !== 'All';

  const effectiveStats = useMemo<MonthlyStats | null>(() => {
    if (!stats && !payments.length) return null;
    const base = hasFilters ? computeMonthlyStatsFrom(filteredPayments) : (stats as MonthlyStats);
    const source = hasFilters ? filteredPayments : payments;
    const amounts = computeAmounts(source);

    return { ...base, ...amounts };
  }, [hasFilters, filteredPayments, stats, payments]);

  return (
    <div className="min-h-screen bg-gray-50">
      <div className="max-w-7xl mx-auto p-4 sm:p-6">
        <div className="mb-4 sm:mb-8">
          <div className="flex flex-col items-center gap-3 sm:flex-row sm:items-center sm:justify-between">
            <h1 className="text-2xl sm:text-3xl font-bold text-gray-900 leading-tight text-center sm:text-left">
              {t('payPlanner')}
            </h1>

            <div className="flex items-center gap-4 sm:gap-6">
              <button
                type="button"
                onClick={() => navigateMonth('prev')}
                className="p-2 rounded-lg border border-gray-300 hover:bg-gray-50 transition-colors"
                aria-label="Previous month">
                <ChevronLeft className="h-4 w-4 sm:h-5 sm:w-5" />
              </button>
              <h2 className="text-lg sm:text-xl font-semibold text-gray-900 text-center truncate max-w-[70vw] sm:max-w-none">
                {formatMonth(currentDate)}
              </h2>
              <button
                type="button"
                onClick={() => navigateMonth('next')}
                className="p-2 rounded-lg border border-gray-300 hover:bg-gray-50 transition-colors"
                aria-label="Next month">
                <ChevronRight className="h-4 w-4 sm:h-5 sm:w-5" />
              </button>
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
          </div>
        </div>

        <SummaryCards stats={effectiveStats} />

        <div className="grid grid-cols-1 sm:flex sm:flex-row gap-3 mb-6">
          <button
            type="button"
            onClick={() => handleAddPayment('Income')}
            className="inline-flex items-center justify-center gap-2 px-4 py-3 text-sm sm:text-base font-medium rounded-lg bg-emerald-600 text-white hover:bg-emerald-700 transition-colors shadow-sm">
            <Plus className="h-4 w-4" />
            {t('addIncome')}
          </button>

          <button
            type="button"
            onClick={() => handleAddPayment('Expense')}
            className="inline-flex items-center justify-center gap-2 px-4 py-3 text-sm sm:text-base font-medium rounded-lg bg-white text-gray-700 border border-gray-300 hover:bg-gray-50 transition-colors shadow-sm">
            <Plus className="h-4 w-4" />
            {t('addExpense')}
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
            await deletePayment(id);
          }}
        />
      </div>
    </div>
  );
}
