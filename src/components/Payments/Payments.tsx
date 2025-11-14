import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Plus, RotateCcw, Search } from 'lucide-react';
import { useAuth } from '../../contexts/AuthContext';
import { useRolePermissions } from '../../hooks/useRolePermissions';
import { useTranslation } from '../../hooks/useTranslation';
import { usePayments } from '../../hooks/usePayments';
import type { Payment, PaymentPayload, PaymentStatus } from '../../types';
import { PaymentsTable } from '../Calendar/PaymentsTable';
import { PaymentModal } from '../Calendar/PaymentModal';
import { formatCurrencySmart } from '../../utils/formatters';

interface PaymentsProps {
  onOpenClient?: (clientId: number, caseId?: number) => void;
}

type SubmitDTO = PaymentPayload | ({ id: number } & PaymentPayload);

type TypeFilter = 'all' | Payment['type'];

type StatusFilter = 'all' | PaymentStatus;

const STATUS_ORDER: PaymentStatus[] = ['Pending', 'Processing', 'Overdue', 'Completed', 'Cancelled'];

function formatCountLabel(template: string, count: number) {
  return template.replace('{{count}}', count.toLocaleString('ru-RU'));
}

export function Payments({ onOpenClient }: PaymentsProps) {
  const { user } = useAuth();
  const permissions = useRolePermissions(user?.role?.id);
  const paymentsPermissions = permissions.payments;
  const { t } = useTranslation();

  const {
    payments,
    loading,
    refreshing,
    refresh,
    createPayment,
    updatePayment,
    deletePayment,
  } = usePayments();

  const [typeFilter, setTypeFilter] = useState<TypeFilter>('all');
  const [statusFilter, setStatusFilter] = useState<StatusFilter>('all');
  const [search, setSearch] = useState('');
  const [isModalOpen, setIsModalOpen] = useState(false);
  const [modalType, setModalType] = useState<Payment['type']>('Income');
  const [editingPayment, setEditingPayment] = useState<Payment | null>(null);
  const [permissionMessage, setPermissionMessage] = useState<string | null>(null);
  const permissionTimerRef = useRef<number | null>(null);
  const [lastUpdated, setLastUpdated] = useState<Date | null>(null);

  useEffect(() => {
    setLastUpdated(new Date());
  }, [payments]);

  useEffect(() => () => {
    if (permissionTimerRef.current) {
      window.clearTimeout(permissionTimerRef.current);
    }
  }, []);

  const showPermissionNotice = useCallback(
    (message: string) => {
      setPermissionMessage(message);
      if (permissionTimerRef.current) {
        window.clearTimeout(permissionTimerRef.current);
      }
      permissionTimerRef.current = window.setTimeout(() => {
        setPermissionMessage(null);
        permissionTimerRef.current = null;
      }, 4000);
    },
    [],
  );

  const statusLabels = useMemo(
    () => ({
      Completed: t('statusCompleted') ?? 'Выполнено',
      Pending: t('pending') ?? 'Ожидается',
      Overdue: t('overdue') ?? 'Просрочено',
      Processing: t('processingStatus') ?? 'В обработке',
      Cancelled: t('cancelledStatus') ?? 'Отменено',
    }),
    [t],
  );

  const availableStatuses = useMemo(() => {
    const uniq = new Set<PaymentStatus>();
    payments.forEach((payment) => {
      uniq.add(payment.status);
    });
    const ordered = Array.from(uniq);
    ordered.sort((a, b) => STATUS_ORDER.indexOf(a) - STATUS_ORDER.indexOf(b));
    return ordered;
  }, [payments]);

  const searchQuery = search.trim().toLowerCase();

  const filteredPayments = useMemo(() => {
    return payments.filter((payment) => {
      const matchesType = typeFilter === 'all' || payment.type === typeFilter;
      const matchesStatus = statusFilter === 'all' || payment.status === statusFilter;
      if (!matchesType || !matchesStatus) {
        return false;
      }
      if (!searchQuery) {
        return true;
      }
      const fields: Array<string | null | undefined> = [
        payment.description,
        payment.notes,
        payment.account,
        payment.accountDate,
        payment.client?.name,
        payment.clientCase?.title,
        payment.dealType?.name,
        payment.incomeType?.name,
        payment.paymentSource?.name,
      ];
      return fields.some((field) => field?.toLowerCase().includes(searchQuery));
    });
  }, [payments, typeFilter, statusFilter, searchQuery]);

  const summary = useMemo(() => {
    let income = 0;
    let expense = 0;
    let incomeCount = 0;
    let expenseCount = 0;
    filteredPayments.forEach((payment) => {
      if (payment.type === 'Income') {
        income += payment.amount;
        incomeCount += 1;
      } else {
        expense += payment.amount;
        expenseCount += 1;
      }
    });
    return {
      income,
      expense,
      balance: income - expense,
      incomeCount,
      expenseCount,
      total: filteredPayments.length,
    };
  }, [filteredPayments]);

  const handleAddPayment = useCallback(
    (type: Payment['type']) => {
      if (!paymentsPermissions.canCreate) {
        showPermissionNotice(t('permissionNoAddPayment') ?? 'Недостаточно прав для добавления платежей.');
        return;
      }
      setModalType(type);
      setEditingPayment(null);
      setIsModalOpen(true);
    },
    [paymentsPermissions.canCreate, showPermissionNotice, t],
  );

  const handleEditPayment = useCallback(
    (payment: Payment) => {
      if (!paymentsPermissions.canEdit) {
        showPermissionNotice(t('permissionNoEditPayment') ?? 'Недостаточно прав для редактирования платежей.');
        return;
      }
      setEditingPayment(payment);
      setModalType(payment.type);
      setIsModalOpen(true);
    },
    [paymentsPermissions.canEdit, showPermissionNotice, t],
  );

  const handleDeletePayment = useCallback(
    async (payment: Payment) => {
      if (!paymentsPermissions.canDelete) {
        showPermissionNotice(t('permissionNoDeletePayment') ?? 'Недостаточно прав для удаления платежей.');
        return;
      }
      const confirmed = window.confirm(t('confirmDeletePayment') ?? 'Удалить платёж?');
      if (!confirmed) {
        return;
      }
      await deletePayment(payment.id);
      await refresh();
    },
    [deletePayment, paymentsPermissions.canDelete, refresh, showPermissionNotice, t],
  );

  const handleSubmit = useCallback(
    async (payload: SubmitDTO) => {
      if ('id' in payload) {
        await updatePayment(payload);
      } else {
        await createPayment(payload);
      }
      await refresh();
      setIsModalOpen(false);
      setEditingPayment(null);
    },
    [createPayment, refresh, updatePayment],
  );

  const handleCloseModal = useCallback(() => {
    setIsModalOpen(false);
    setEditingPayment(null);
    void refresh();
  }, [refresh]);

  const handleRefresh = useCallback(async () => {
    await refresh();
  }, [refresh]);

  const lastUpdatedText = useMemo(() => {
    if (!lastUpdated) {
      return null;
    }
    const template = t('paymentsLastUpdated') ?? 'Обновлено {{time}}';
    const time = lastUpdated.toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });
    return template.replace('{{time}}', time);
  }, [lastUpdated, t]);

  const typeButtonClass = useCallback(
    (target: TypeFilter) => {
      const common = 'px-4 py-2 text-sm font-medium rounded-full border transition-colors focus:outline-none';
      if (target === 'all') {
        const active = typeFilter === 'all';
        return [
          common,
          active
            ? 'bg-slate-900 text-white border-slate-900 shadow'
            : 'bg-white text-slate-600 border-slate-200 hover:border-slate-300',
        ].join(' ');
      }
      if (target === 'Income') {
        const active = typeFilter === 'Income';
        return [
          common,
          active
            ? 'bg-emerald-600 text-white border-emerald-600 shadow'
            : 'bg-emerald-50 text-emerald-700 border-emerald-200 hover:border-emerald-300',
        ].join(' ');
      }
      const active = typeFilter === 'Expense';
      return [
        common,
        active
          ? 'bg-rose-600 text-white border-rose-600 shadow'
          : 'bg-rose-50 text-rose-700 border-rose-200 hover:border-rose-300',
      ].join(' ');
    },
    [typeFilter],
  );

  const initialLoading = loading && payments.length === 0;

  return (
    <div className="p-8">
      <div className="max-w-7xl mx-auto space-y-6">
        <div className="flex flex-col md:flex-row md:items-start md:justify-between gap-4">
          <div>
            <h1 className="text-3xl font-bold text-slate-900">{t('payments') ?? 'Платежи'}</h1>
            <p className="mt-2 text-slate-600 max-w-2xl">
              {t('paymentsSubtitle') ?? 'Журнал всех платежей'}
            </p>
            {lastUpdatedText ? (
              <p className="mt-2 text-xs text-slate-500">{lastUpdatedText}</p>
            ) : null}
          </div>
          <div className="flex flex-wrap items-center gap-3">
            <button
              type="button"
              onClick={handleRefresh}
              className={`inline-flex items-center gap-2 rounded-lg border border-slate-200 px-4 py-2 text-sm font-medium text-slate-600 transition-colors hover:border-slate-300 hover:text-slate-900 ${
                refreshing ? 'opacity-70 cursor-wait' : ''
              }`}
              disabled={refreshing}>
              <RotateCcw className={`h-4 w-4 ${refreshing ? 'animate-spin' : ''}`} />
              {t('paymentsRefresh') ?? t('invoiceRefresh') ?? 'Обновить'}
            </button>
          </div>
        </div>

        {permissionMessage ? (
          <div className="rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800">
            {permissionMessage}
          </div>
        ) : null}

        <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-4">
          <div className="rounded-xl border border-emerald-100 bg-emerald-50 p-4 shadow-sm">
            <div className="text-sm font-medium text-emerald-700">
              {t('paymentsSummaryIncome') ?? t('incomePlural') ?? 'Доходы'}
            </div>
            <div className="mt-2 text-2xl font-semibold text-emerald-800">
              {formatCurrencySmart(summary.income).full}
            </div>
            <div className="mt-1 text-xs text-emerald-700/80">
              {formatCountLabel(t('paymentsEntriesCount') ?? 'Платежей: {{count}}', summary.incomeCount)}
            </div>
          </div>

          <div className="rounded-xl border border-rose-100 bg-rose-50 p-4 shadow-sm">
            <div className="text-sm font-medium text-rose-700">
              {t('paymentsSummaryExpense') ?? t('expensePlural') ?? 'Расходы'}
            </div>
            <div className="mt-2 text-2xl font-semibold text-rose-700">
              {formatCurrencySmart(summary.expense).full}
            </div>
            <div className="mt-1 text-xs text-rose-700/80">
              {formatCountLabel(t('paymentsEntriesCount') ?? 'Платежей: {{count}}', summary.expenseCount)}
            </div>
          </div>

          <div className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
            <div className="text-sm font-medium text-slate-600">
              {t('paymentsSummaryBalance') ?? 'Сальдо'}
            </div>
            <div
              className={`mt-2 text-2xl font-semibold ${
                summary.balance >= 0 ? 'text-emerald-600' : 'text-rose-600'
              }`}>
              {formatCurrencySmart(summary.balance).full}
            </div>
            <div className="mt-1 text-xs text-slate-500">
              {t('paymentsTotalCount') ?? 'Всего платежей'}: {summary.total.toLocaleString('ru-RU')}
            </div>
          </div>

          <div className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
            <div className="text-sm font-medium text-slate-600">
              {t('paymentsTotalCount') ?? 'Всего платежей'}
            </div>
            <div className="mt-2 text-3xl font-semibold text-slate-900">
              {summary.total.toLocaleString('ru-RU')}
            </div>
            <div className="mt-1 text-xs text-slate-500">
              {t('recordsFound') ?? 'Найдено записей:'} {filteredPayments.length.toLocaleString('ru-RU')}
            </div>
          </div>
        </div>

        <div className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm space-y-4">
          <div className="flex flex-col gap-4 lg:flex-row lg:items-center lg:justify-between">
            <div className="flex flex-wrap items-center gap-2">
              <button type="button" className={typeButtonClass('all')} onClick={() => setTypeFilter('all')}>
                {t('paymentsFilterAll') ?? 'Все'}
              </button>
              <button type="button" className={typeButtonClass('Income')} onClick={() => setTypeFilter('Income')}>
                {t('paymentsFilterIncome') ?? t('incomePlural') ?? 'Доходы'}
              </button>
              <button type="button" className={typeButtonClass('Expense')} onClick={() => setTypeFilter('Expense')}>
                {t('paymentsFilterExpense') ?? t('expensePlural') ?? 'Расходы'}
              </button>
            </div>

            <div className="flex flex-wrap items-center gap-3">
              <select
                className="min-w-[180px] rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-700 focus:border-slate-400 focus:outline-none"
                value={statusFilter}
                onChange={(event) => {
                  const value = event.target.value as StatusFilter;
                  setStatusFilter(value);
                }}>
                <option value="all">{t('paymentsStatusAll') ?? t('allStatuses') ?? 'Все статусы'}</option>
                {availableStatuses.map((status) => (
                  <option key={status} value={status}>
                    {statusLabels[status] ?? status}
                  </option>
                ))}
              </select>

              <div className="relative">
                <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
                <input
                  type="search"
                  value={search}
                  onChange={(event) => setSearch(event.target.value)}
                  placeholder={t('paymentsSearchPlaceholder') ?? t('search') ?? 'Поиск'}
                  className="w-64 rounded-lg border border-slate-200 bg-white py-2 pl-10 pr-3 text-sm text-slate-700 placeholder:text-slate-400 focus:border-slate-400 focus:outline-none"
                />
              </div>
            </div>
          </div>

          {paymentsPermissions.canCreate ? (
            <div className="flex flex-wrap gap-3">
              <button
                type="button"
                onClick={() => handleAddPayment('Income')}
                className="inline-flex items-center gap-2 rounded-lg bg-emerald-600 px-4 py-2 text-sm font-medium text-white shadow-sm transition-colors hover:bg-emerald-700 focus:outline-none">
                <Plus className="h-4 w-4" />
                {t('addIncome') ?? 'Добавить доход'}
              </button>
              <button
                type="button"
                onClick={() => handleAddPayment('Expense')}
                className="inline-flex items-center gap-2 rounded-lg bg-rose-600 px-4 py-2 text-sm font-medium text-white shadow-sm transition-colors hover:bg-rose-700 focus:outline-none">
                <Plus className="h-4 w-4" />
                {t('addExpense') ?? 'Добавить расход'}
              </button>
            </div>
          ) : null}
        </div>

        <div className="space-y-4">
          {initialLoading ? (
            <div className="flex justify-center py-12">
              <div className="flex flex-col items-center gap-3 text-slate-500">
                <div className="h-10 w-10 animate-spin rounded-full border-4 border-slate-200 border-t-slate-900" />
                <span className="text-sm">{t('loading') ?? 'Загрузка...'}</span>
              </div>
            </div>
          ) : (
            <PaymentsTable
              payments={filteredPayments}
              onEditPayment={handleEditPayment}
              onOpenClient={onOpenClient}
              onAddPayment={handleAddPayment}
              onDeletePayment={handleDeletePayment}
              canAdd={paymentsPermissions.canCreate}
              canEdit={paymentsPermissions.canEdit}
              canDelete={paymentsPermissions.canDelete}
            />
          )}
        </div>

        {!initialLoading && filteredPayments.length === 0 ? (
          <div className="rounded-xl border border-slate-200 bg-white p-6 text-center text-slate-500">
            {t('paymentsEmpty') ?? t('noPaymentsForFilter') ?? 'Платежи не найдены'}
          </div>
        ) : null}
      </div>

      <PaymentModal
        isOpen={isModalOpen}
        onClose={handleCloseModal}
        onSubmit={handleSubmit}
        payment={editingPayment}
        type={modalType}
        onDelete={
          paymentsPermissions.canDelete
            ? async (id) => {
                await deletePayment(id);
                await refresh();
                setIsModalOpen(false);
                setEditingPayment(null);
              }
            : undefined
        }
      />
    </div>
  );
}
