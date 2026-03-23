import { useEffect, useMemo, useState } from 'react';
import { createPortal } from 'react-dom';
import {
  Calendar,
  ChevronDown,
  Filter,
  GripVertical,
  Plus,
  Pencil,
  Search,
  SlidersHorizontal,
  Trash2,
  WalletCards,
  UploadCloud,
} from 'lucide-react';
import { useAuth } from '../../contexts/AuthContext';
import { useDebouncedValue } from '../../hooks/useDebouncedValue';
import { usePersistedTableSettings } from '../../hooks/usePersistedTableSettings';
import { useMoneyOperations } from '../../hooks/useMoneyOperations';
import { useToast } from '../../hooks/useToast';
import { apiService } from '../../services/api';
import { useTabNavigation } from '../../contexts/TabContext';
import { ConfirmDialog } from '../ui/ConfirmDialog';
import { TableColumnSettings } from '../ui/TableColumnSettings';
import { TablePagination } from '../ui/TablePagination';
import type {
  MoneyOperation,
  MoneyOperationAccountingType,
  MoneyOperationDirection,
  MoneyOperationSource,
} from '../../types';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';

type MoneyTab = MoneyOperationSource | 'Total';

const SOURCE_LABELS: Record<MoneyTab, string> = {
  Bank: 'Банк',
  Cash: 'Касса',
  Card: 'Карты',
  Total: 'Итого',
};

const PAGE_SIZE_OPTIONS = [10, 25, 50, 100];

const MONEY_OPERATION_COLUMNS = [
  { key: 'date', label: 'Дата', defaultVisible: true, defaultWidth: 120 },
  { key: 'recognitionDate', label: 'Дата признания', defaultVisible: true, defaultWidth: 150 },
  { key: 'income', label: 'Приход', defaultVisible: true, defaultWidth: 130 },
  { key: 'expense', label: 'Расход', defaultVisible: true, defaultWidth: 130 },
  { key: 'currency', label: 'Валюта', defaultVisible: true, defaultWidth: 90 },
  { key: 'accountName', label: 'Счета', defaultVisible: true, defaultWidth: 180 },
  { key: 'counterpartyName', label: 'Контрагент', defaultVisible: true, defaultWidth: 180 },
  { key: 'projectName', label: 'Проект', defaultVisible: true, defaultWidth: 160 },
  { key: 'salesPointName', label: 'Точка продаж', defaultVisible: false, defaultWidth: 160 },
  { key: 'directionName', label: 'Направление', defaultVisible: false, defaultWidth: 160 },
  { key: 'paymentPriority', label: 'Приоритет в оплате', defaultVisible: false, defaultWidth: 150 },
  { key: 'purpose', label: 'Назначение', defaultVisible: true, defaultWidth: 240 },
  { key: 'paidAccountName', label: 'Опл. счета', defaultVisible: false, defaultWidth: 160 },
  { key: 'categoryName', label: 'Статья', defaultVisible: true, defaultWidth: 160 },
] as const;

type MoneyOperationColumnKey = (typeof MONEY_OPERATION_COLUMNS)[number]['key'];

const DEFAULT_FORM: MoneyOperation = {
  id: 0,
  source: 'Bank',
  direction: 'Income',
  accountingType: 'Fact',
  accountName: '',
  counterpartyName: '',
  categoryName: '',
  amount: 0,
  currency: 'RUB',
  paymentOrderNumber: '',
  date: new Date().toISOString().slice(0, 10),
  recognitionDate: '',
  isAdvancePayment: false,
  purpose: '',
  paidInvoiceNumber: '',
  paidAmount: 0,
  projectName: '',
  directionName: '',
  salesPointName: '',
  paymentPriority: undefined,
  paidAccountName: '',
  createdAt: new Date().toISOString(),
  updatedAt: null,
};

function formatMoney(value: number) {
  return new Intl.NumberFormat('ru-RU', { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(value);
}

function formatDate(value: string) {
  if (!value) return '—';

  const isoDateMatch = value.match(/^(\d{4})-(\d{2})-(\d{2})/);
  if (isoDateMatch) {
    return `${isoDateMatch[3]}.${isoDateMatch[2]}.${isoDateMatch[1]}`;
  }

  const dotIsoDateMatch = value.match(/^(\d{4})\.(\d{2})\.(\d{2})/);
  if (dotIsoDateMatch) {
    return `${dotIsoDateMatch[3]}.${dotIsoDateMatch[2]}.${dotIsoDateMatch[1]}`;
  }

  const malformedDateMatch = value.match(/^(\d{2})T\d{2}:\d{2}:\d{2}\.(\d{2})\.(\d{4})$/);
  if (malformedDateMatch) {
    return `${malformedDateMatch[1]}.${malformedDateMatch[2]}.${malformedDateMatch[3]}`;
  }

  const ruDateMatch = value.match(/^(\d{2})\.(\d{2})\.(\d{4})/);
  if (ruDateMatch) {
    return `${ruDateMatch[1]}.${ruDateMatch[2]}.${ruDateMatch[3]}`;
  }

  const parsedDate = new Date(value);
  if (!Number.isNaN(parsedDate.getTime())) {
    return parsedDate.toLocaleDateString('ru-RU');
  }

  return value;
}

function MoneyOperationModal({
  source,
  initialData,
  open,
  onClose,
  onSave,
  isSaving,
}: {
  source: MoneyOperationSource;
  initialData?: MoneyOperation | null;
  open: boolean;
  onClose: () => void;
  onSave: (payload: MoneyOperation) => Promise<void>;
  isSaving: boolean;
}) {
  const [form, setForm] = useState<MoneyOperation>(() => ({ ...DEFAULT_FORM, source }));
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (open) {
      setForm(initialData ? { ...initialData } : { ...DEFAULT_FORM, source });
      setError(null);
    }
  }, [initialData, open, source]);

  if (!open) return null;

  const setField = <K extends keyof MoneyOperation>(key: K, value: MoneyOperation[K]) => {
    setForm((prev) => ({ ...prev, [key]: value }));
  };

  const handleSubmit = async () => {
    setError(null);
    try {
      await onSave(form);
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Не удалось сохранить операцию.';
      setError(message);
    }
  };

  const title = initialData
    ? `Редактировать операцию по ${SOURCE_LABELS[source].toLowerCase()}`
    : `Добавить операцию по ${SOURCE_LABELS[source].toLowerCase()}`;

  return createPortal(
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-900/40 p-4">
      <div className="flex max-h-[90vh] w-full max-w-4xl flex-col overflow-hidden rounded-2xl bg-white shadow-xl">
        <div className="flex items-center justify-between border-b border-slate-200 px-6 py-4">
          <div>
            <h2 className="text-xl font-semibold text-slate-900">{title}</h2>
            <p className="text-sm text-slate-500">Заполните основные параметры операции.</p>
          </div>
          <button onClick={onClose} className="text-slate-400 hover:text-slate-600">✕</button>
        </div>

        <div className="flex-1 space-y-6 overflow-y-auto p-6">
          <div className="flex flex-wrap items-center gap-4">
            <div className="flex items-center gap-2 rounded-xl border border-slate-200 px-3 py-2">
              <span className="text-sm text-slate-500">Операция</span>
              <select className="border-none bg-transparent text-sm font-medium text-slate-700 focus:outline-none">
                <option>1 операция</option>
                <option>2 операции</option>
              </select>
            </div>
            <div className="flex items-center gap-2 rounded-xl border border-slate-200 px-3 py-2">
              <span className="text-sm text-slate-500">Учёт</span>
              <select
                value={form.accountingType}
                onChange={(event) => setField('accountingType', event.target.value as MoneyOperationAccountingType)}
                className="border-none bg-transparent text-sm font-medium text-slate-700 focus:outline-none">
                <option value="Fact">Факт</option>
                <option value="Plan">План</option>
              </select>
            </div>
          </div>

          <div className="flex flex-wrap items-center gap-6 text-sm font-medium text-slate-700">
            {(['Income', 'Expense', 'Transfer'] as MoneyOperationDirection[]).map((direction) => (
              <label key={direction} className="flex items-center gap-2">
                <input
                  type="radio"
                  name="direction"
                  checked={form.direction === direction}
                  onChange={() => setField('direction', direction)}
                />
                {direction === 'Income' ? 'Приход' : direction === 'Expense' ? 'Расход' : 'Между счетами'}
              </label>
            ))}
          </div>

          <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
            <label className="flex flex-col gap-1 text-sm text-slate-600">
              Банковский счёт
              <input
                value={form.accountName ?? ''}
                onChange={(event) => setField('accountName', event.target.value)}
                className="rounded-lg border border-slate-200 px-3 py-2 focus:border-blue-500 focus:outline-none"
                placeholder="Основной счёт"
              />
            </label>
            <label className="flex flex-col gap-1 text-sm text-slate-600">
              {form.direction === 'Income' ? 'Покупатель' : 'Поставщик'}
              <input
                value={form.counterpartyName ?? ''}
                onChange={(event) => setField('counterpartyName', event.target.value)}
                className="rounded-lg border border-slate-200 px-3 py-2 focus:border-blue-500 focus:outline-none"
                placeholder="Контрагент"
              />
            </label>
            <label className="flex flex-col gap-1 text-sm text-slate-600">
              {form.direction === 'Income' ? 'Статья прихода' : 'Статья расхода'}
              <input
                value={form.categoryName ?? ''}
                onChange={(event) => setField('categoryName', event.target.value)}
                className="rounded-lg border border-slate-200 px-3 py-2 focus:border-blue-500 focus:outline-none"
                placeholder="Статья"
              />
            </label>
            <label className="flex flex-col gap-1 text-sm text-slate-600">
              Номер ПП
              <input
                value={form.paymentOrderNumber ?? ''}
                onChange={(event) => setField('paymentOrderNumber', event.target.value)}
                className="rounded-lg border border-slate-200 px-3 py-2 focus:border-blue-500 focus:outline-none"
                placeholder="Например 873864"
              />
            </label>
            <label className="flex flex-col gap-1 text-sm text-slate-600">
              Сумма, RUB
              <input
                type="number"
                value={form.amount}
                onChange={(event) => setField('amount', Number(event.target.value))}
                className="rounded-lg border border-slate-200 px-3 py-2 focus:border-blue-500 focus:outline-none"
              />
            </label>
            <label className="flex flex-col gap-1 text-sm text-slate-600">
              Дата
              <input
                type="date"
                value={form.date}
                onChange={(event) => setField('date', event.target.value)}
                className="rounded-lg border border-slate-200 px-3 py-2 focus:border-blue-500 focus:outline-none"
              />
            </label>
            <label className="flex flex-col gap-1 text-sm text-slate-600">
              Дата признания {form.direction === 'Income' ? 'дохода' : 'расхода'}
              <input
                type="date"
                value={form.recognitionDate ?? ''}
                onChange={(event) => setField('recognitionDate', event.target.value)}
                className="rounded-lg border border-slate-200 px-3 py-2 focus:border-blue-500 focus:outline-none"
              />
            </label>
            <label className="flex items-center gap-2 text-sm text-slate-600">
              <input
                type="checkbox"
                checked={form.isAdvancePayment}
                onChange={(event) => setField('isAdvancePayment', event.target.checked)}
              />
              Авансовый платёж
            </label>
          </div>

          <label className="flex flex-col gap-1 text-sm text-slate-600">
            Назначение
            <textarea
              value={form.purpose ?? ''}
              onChange={(event) => setField('purpose', event.target.value)}
              className="min-h-[90px] rounded-lg border border-slate-200 px-3 py-2 focus:border-blue-500 focus:outline-none"
              placeholder="Назначение платежа"
            />
          </label>

          <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
            <label className="flex flex-col gap-1 text-sm text-slate-600">
              Оплаченные счета
              <input
                value={form.paidInvoiceNumber ?? ''}
                onChange={(event) => setField('paidInvoiceNumber', event.target.value)}
                className="rounded-lg border border-slate-200 px-3 py-2 focus:border-blue-500 focus:outline-none"
                placeholder="Номер счёта"
              />
            </label>
            <label className="flex flex-col gap-1 text-sm text-slate-600">
              На сумму
              <input
                type="number"
                value={form.paidAmount ?? 0}
                onChange={(event) => setField('paidAmount', Number(event.target.value))}
                className="rounded-lg border border-slate-200 px-3 py-2 focus:border-blue-500 focus:outline-none"
              />
            </label>
            <label className="flex flex-col gap-1 text-sm text-slate-600">
              Проект
              <input
                value={form.projectName ?? ''}
                onChange={(event) => setField('projectName', event.target.value)}
                className="rounded-lg border border-slate-200 px-3 py-2 focus:border-blue-500 focus:outline-none"
                placeholder="Без проекта"
              />
            </label>
            <label className="flex flex-col gap-1 text-sm text-slate-600">
              Направление
              <input
                value={form.directionName ?? ''}
                onChange={(event) => setField('directionName', event.target.value)}
                className="rounded-lg border border-slate-200 px-3 py-2 focus:border-blue-500 focus:outline-none"
                placeholder="Без направления"
              />
            </label>
            <label className="flex flex-col gap-1 text-sm text-slate-600">
              Точка продаж
              <input
                value={form.salesPointName ?? ''}
                onChange={(event) => setField('salesPointName', event.target.value)}
                className="rounded-lg border border-slate-200 px-3 py-2 focus:border-blue-500 focus:outline-none"
                placeholder="Без точки продаж"
              />
            </label>
          </div>
        </div>

        <div className="flex items-center justify-between border-t border-slate-200 px-6 py-4">
          {error ? <div className="text-sm text-red-500">{error}</div> : <span />}
          <button
            onClick={handleSubmit}
            disabled={isSaving}
            className="rounded-lg bg-blue-600 px-5 py-2 text-sm font-semibold text-white hover:bg-blue-700 disabled:opacity-60">
            {isSaving ? 'Сохранение...' : 'Сохранить'}
          </button>
          <button
            onClick={onClose}
            className="rounded-lg border border-slate-200 px-4 py-2 text-sm font-medium text-slate-600 hover:border-slate-300">
            Отменить
          </button>
        </div>
      </div>
    </div>,
    document.body,
  );
}

export function MoneyOperations({ source }: { source: MoneyTab }) {
  const { user } = useAuth();
  const { setActiveTab } = useTabNavigation();
  const toast = useToast();
  const [showFilters, setShowFilters] = useState(false);
  const [showAdvancedSearch, setShowAdvancedSearch] = useState(false);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [search, setSearch] = useState('');
  const [modalOpen, setModalOpen] = useState(false);
  const [saving, setSaving] = useState(false);
  const [editingOperation, setEditingOperation] = useState<MoneyOperation | null>(null);
  const [totalTab, setTotalTab] = useState<'total' | 'cashflow'>('total');
  const [confirmDelete, setConfirmDelete] = useState<MoneyOperation | null>(null);
  const [page, setPage] = useState(1);

  const currentYear = new Date().getFullYear();
  const [from, setFrom] = useState(`${currentYear}-01-01`);
  const [to, setTo] = useState(`${currentYear}-12-31`);

  const handleCurrentYear = () => {
    const year = new Date().getFullYear();
    setFrom(`${year}-01-01`);
    setTo(`${year}-12-31`);
  };

  const debouncedSearch = useDebouncedValue(search, 300);
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
    resetTableSettings,
  } = usePersistedTableSettings<MoneyOperationColumnKey>({
    storageKey: `pp.money_operations_table_settings.${user?.id ?? 'guest'}.v1`,
    settingsKey: 'money_operations_table_settings',
    columns: MONEY_OPERATION_COLUMNS.map((column) => ({
      key: column.key,
      defaultVisible: column.defaultVisible,
      defaultWidth: column.defaultWidth,
    })),
    defaultPageSize: 25,
    minPageSize: 10,
    maxPageSize: 100,
    minColumnWidth: 110,
  });

  const apiSource = source === 'Total' ? undefined : source;
  const effectivePageSize = source === 'Total' ? 1 : pageSize;
  const { items, total, summary, loading, error, reload } = useMoneyOperations({
    from,
    to,
    source: apiSource,
    search: debouncedSearch,
    page: source === 'Total' ? 1 : page,
    pageSize: effectivePageSize,
  });
  const totalPages = useMemo(
    () => (effectivePageSize > 0 ? Math.max(1, Math.ceil(total / effectivePageSize)) : 1),
    [effectivePageSize, total],
  );
  const moneyColumnsByKey = useMemo(
    () =>
      Object.fromEntries(MONEY_OPERATION_COLUMNS.map((column) => [column.key, column])) as Record<
        MoneyOperationColumnKey,
        (typeof MONEY_OPERATION_COLUMNS)[number]
      >,
    [],
  );
  const visibleMoneyColumns = useMemo(
    () => orderedVisibleColumns.map((key) => moneyColumnsByKey[key]).filter(Boolean),
    [moneyColumnsByKey, orderedVisibleColumns],
  );

  const totalRows = useMemo(() => {
    const bySource = new Map(summary.bySource.map((row) => [row.source, row]));

    const rows = ([
      { key: 'Bank', label: 'Банк' },
    ] as const).map((row) => {
      const data = bySource.get(row.key) ?? {
        startBalance: 0,
        income: 0,
        expense: 0,
        endBalance: 0,
      };
      return {
        label: row.label,
        start: data.startBalance,
        income: data.income,
        expense: data.expense,
        end: data.endBalance,
      };
    });

    const total = rows.reduce(
      (acc, row) => ({
        start: acc.start + row.start,
        income: acc.income + row.income,
        expense: acc.expense + row.expense,
        end: acc.end + row.end,
      }),
      { start: 0, income: 0, expense: 0, end: 0 },
    );

    return { rows, total };
  }, [summary.bySource]);

  const renderCell = (item: MoneyOperation, columnKey: MoneyOperationColumnKey) => {
    switch (columnKey) {
      case 'date':
        return formatDate(item.date);
      case 'recognitionDate':
        return item.recognitionDate ? formatDate(item.recognitionDate) : '—';
      case 'income':
        return (
          <span className="text-emerald-600">
            {item.direction === 'Income' ? `${formatMoney(item.amount)} ₽` : '—'}
          </span>
        );
      case 'expense':
        return (
          <span className="text-red-600">
            {item.direction === 'Expense' ? `${formatMoney(item.amount)} ₽` : '—'}
          </span>
        );
      case 'currency':
        return item.currency ?? 'RUB';
      case 'accountName':
        return item.accountName ? (
          <button
            type="button"
            onClick={() => setActiveTab('accounts')}
            className="text-blue-600 underline-offset-2 hover:underline">
            {item.accountName}
          </button>
        ) : (
          '—'
        );
      case 'counterpartyName':
        return item.counterpartyName ?? '—';
      case 'projectName':
        return item.projectName ? (
          <button
            type="button"
            onClick={() => setActiveTab('clients')}
            className="text-blue-600 underline-offset-2 hover:underline">
            {item.projectName}
          </button>
        ) : (
          '—'
        );
      case 'salesPointName':
        return item.salesPointName ?? '—';
      case 'directionName':
        return item.directionName ?? '—';
      case 'paymentPriority':
        return item.paymentPriority ?? '—';
      case 'purpose':
        return (
          <div className="line-clamp-3 text-slate-700" title={item.purpose ?? undefined}>
            {item.purpose ?? '—'}
          </div>
        );
      case 'paidAccountName':
        return item.paidAccountName ?? '—';
      case 'categoryName':
        return item.categoryName ?? '—';
      default:
        return '—';
    }
  };

  useEffect(() => {
    if (source === 'Total') return;
    setPage(1);
  }, [debouncedSearch, from, pageSize, source, to]);

  useEffect(() => {
    if (source === 'Total') return;
    if (page > totalPages) {
      setPage(totalPages);
    }
  }, [page, source, totalPages]);

  const handleSave = async (payload: MoneyOperation) => {
    setSaving(true);
    try {
      const normalizedRecognitionDate =
        payload.recognitionDate && payload.recognitionDate.trim() !== '' ? payload.recognitionDate : null;
      const normalizedPaidAmount = Number.isFinite(payload.paidAmount ?? NaN) ? payload.paidAmount : null;
      const prepared = {
        ...payload,
        source: apiSource ?? payload.source,
        recognitionDate: normalizedRecognitionDate,
        paidAmount: normalizedPaidAmount,
      };
      if (editingOperation) {
        await apiService.updateMoneyOperation(editingOperation.id, prepared);
        await reload();
        toast.success('Операция обновлена.', 'Деньги');
      } else {
        await apiService.createMoneyOperation({
          ...prepared,
          id: 0,
          createdAt: new Date().toISOString(),
        });
        toast.success('Операция создана.', 'Деньги');
        if (source !== 'Total' && page !== 1) {
          setPage(1);
        } else {
          await reload();
        }
      }
      setModalOpen(false);
      setEditingOperation(null);
    } catch (error) {
      const message = extractDisplayErrorMessage(error, 'Не удалось сохранить операцию.');
      toast.error(message, 'Деньги');
      throw new Error(message);
    } finally {
      setSaving(false);
    }
  };

  const handleEdit = (item: MoneyOperation) => {
    if (item.isReadonly || item.id <= 0) return;
    setEditingOperation(item);
    setModalOpen(true);
  };


  useEffect(() => {
    if (items.length === 0) return;
    let raw: string | null = null;
    try {
      raw = localStorage.getItem('pp.openMoneyOperationId');
    } catch {
      raw = null;
    }
    if (!raw) return;
    const targetId = Number(raw);
    if (Number.isNaN(targetId)) {
      try { localStorage.removeItem('pp.openMoneyOperationId'); } catch { /** */ }
      return;
    }
    const target = items.find((item) => item.id === targetId);
    if (!target) return;
    handleEdit(target);
    try { localStorage.removeItem('pp.openMoneyOperationId'); } catch { /** */ }
  }, [items]);

  const handleDelete = (item: MoneyOperation) => {
    if (item.isReadonly || item.id <= 0) return;
    setConfirmDelete(item);
  };

  const confirmDeleteOperation = async () => {
    if (!confirmDelete) return;
    try {
      await apiService.deleteMoneyOperation(confirmDelete.id);
      toast.success('Операция удалена.', 'Деньги');
      setConfirmDelete(null);
      if (source !== 'Total' && items.length === 1 && page > 1) {
        setPage((prev) => Math.max(1, prev - 1));
      } else {
        await reload();
      }
    } catch (error) {
      const message = extractDisplayErrorMessage(error, 'Не удалось удалить операцию.');
      toast.error(message, 'Деньги');
    }
  };

  return (
    <div className="px-6 py-6">
      {source === 'Total' ? (
        <div className="space-y-4">
          <div className="flex items-center gap-6 border-b border-slate-200">
            <button
              onClick={() => setTotalTab('total')}
              className={[
                'px-1 py-3 text-sm font-medium',
                totalTab === 'total' ? 'border-b-2 border-blue-600 text-blue-600' : 'text-slate-500',
              ].join(' ')}>
              Итого
            </button>
            <button
              onClick={() => setTotalTab('cashflow')}
              className={[
                'px-1 py-3 text-sm font-medium',
                totalTab === 'cashflow' ? 'border-b-2 border-blue-600 text-blue-600' : 'text-slate-500',
              ].join(' ')}>
              ОДДС
            </button>
          </div>

          <div className="flex flex-wrap items-center justify-between gap-3 rounded-xl border border-slate-200 bg-white px-6 py-4">
            <div className="text-lg font-semibold text-slate-900">Обороты</div>
            <button
              onClick={handleCurrentYear}
              className="flex items-center gap-2 rounded-lg border border-slate-200 px-3 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50">
              <Calendar className="h-4 w-4" />
              Текущий год
            </button>
          </div>

          <div className="rounded-xl border border-slate-200 bg-white">
            <div className="overflow-x-auto">
              <table className="w-full min-w-[900px] table-fixed text-sm">
                <colgroup>
                  <col style={{ width: '24%' }} />
                  <col style={{ width: '19%' }} />
                  <col style={{ width: '19%' }} />
                  <col style={{ width: '19%' }} />
                  <col style={{ width: '19%' }} />
                </colgroup>
                <thead className="bg-slate-50 text-slate-500">
                  <tr>
                    <th className="px-4 py-3 text-left font-medium">Кошелёк</th>
                    <th className="px-4 py-3 text-left font-medium">Баланс на начало периода</th>
                    <th className="px-4 py-3 text-left font-medium">Приход</th>
                    <th className="px-4 py-3 text-left font-medium">Расход</th>
                    <th className="px-4 py-3 text-left font-medium">Баланс на конец периода</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-100">
                  {totalRows.rows.map((row) => (
                    <tr key={row.label} className="hover:bg-slate-50">
                      <td className="px-4 py-3 font-medium text-slate-800">{row.label}</td>
                      <td className="px-4 py-3">{formatMoney(row.start)}</td>
                      <td className="px-4 py-3">{formatMoney(row.income)}</td>
                      <td className="px-4 py-3">{formatMoney(row.expense)}</td>
                      <td className="px-4 py-3">{formatMoney(row.end)}</td>
                    </tr>
                  ))}
                  <tr className="bg-slate-50 font-semibold">
                    <td className="px-4 py-3 text-slate-900">Итого</td>
                    <td className="px-4 py-3">{formatMoney(totalRows.total.start)}</td>
                    <td className="px-4 py-3">{formatMoney(totalRows.total.income)}</td>
                    <td className="px-4 py-3">{formatMoney(totalRows.total.expense)}</td>
                    <td className="px-4 py-3">{formatMoney(totalRows.total.end)}</td>
                  </tr>
                </tbody>
              </table>
            </div>
          </div>
        </div>
      ) : (
        <>
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div>
          <h1 className="text-xl font-semibold text-slate-900">Добавить счёт или кассу</h1>
          <p className="text-sm text-slate-500">Операции по деньгам ({SOURCE_LABELS[source]})</p>
        </div>
        <div className="flex flex-wrap items-center gap-3">
          <button
            className="flex items-center gap-2 rounded-lg border border-blue-500 px-4 py-2 text-sm font-medium text-blue-600 hover:bg-blue-50"
            type="button">
            <UploadCloud className="h-4 w-4" />
            Загрузить выписку
          </button>
        </div>
      </div>

      <div className="mt-6 grid gap-4 lg:grid-cols-[1fr_260px]">
        <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
          <div className="rounded-xl border border-slate-200 bg-white p-4">
            <div className="text-sm text-slate-500">Баланс на начало</div>
            <div className="text-lg font-semibold text-amber-500">{formatMoney(summary.startBalance)} ₽</div>
            <div className="mt-3 h-0.5 w-full rounded-full bg-amber-400"></div>
            <div className="mt-2 text-xs text-slate-400">{formatDate(from)}</div>
          </div>
          <div className="rounded-xl border border-slate-200 bg-white p-4">
            <div className="text-sm text-slate-500">Приход Факт</div>
            <div className="text-lg font-semibold text-cyan-500">{formatMoney(summary.income)} ₽</div>
            <div className="mt-3 h-0.5 w-full rounded-full bg-cyan-400"></div>
            <div className="mt-2 text-xs text-slate-400">Всего поступлений: {summary.incomeCount}</div>
          </div>
          <div className="rounded-xl border border-slate-200 bg-white p-4">
            <div className="text-sm text-slate-500">Расход Факт</div>
            <div className="text-lg font-semibold text-red-500">{formatMoney(summary.expense)} ₽</div>
            <div className="mt-3 h-0.5 w-full rounded-full bg-red-400"></div>
            <div className="mt-2 text-xs text-slate-400">Всего списаний: {summary.expenseCount}</div>
          </div>
          <div className="rounded-xl border border-slate-200 bg-white p-4">
            <div className="text-sm text-slate-500">Баланс на конец</div>
            <div className="text-lg font-semibold text-amber-500">{formatMoney(summary.endBalance)} ₽</div>
            <div className="mt-3 h-0.5 w-full rounded-full bg-amber-400"></div>
            <div className="mt-2 text-xs text-slate-400">{formatDate(to)}</div>
          </div>
        </div>

        <div className="flex flex-col gap-3 rounded-xl border border-slate-200 bg-white p-4">
          <button
            onClick={handleCurrentYear}
            className="flex items-center justify-center gap-2 rounded-lg border border-slate-200 px-3 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50">
            <Calendar className="h-4 w-4" />
            Текущий год
          </button>
          <button
            onClick={() => {
              setEditingOperation(null);
              setModalOpen(true);
            }}
            className="flex items-center justify-center gap-2 rounded-lg border border-slate-200 px-3 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50">
            <Plus className="h-4 w-4" />
            Добавить
          </button>
          <button className="flex items-center justify-center gap-2 rounded-lg border border-slate-200 px-3 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50">
            Ещё
            <ChevronDown className="h-4 w-4" />
          </button>
        </div>
      </div>

      <div className="mt-6 rounded-xl border border-slate-200 bg-white">
        <div className="flex flex-wrap items-center justify-between gap-4 border-b border-slate-200 px-4 py-3">
          <div className="flex items-center gap-2">
            <button className="rounded-lg bg-blue-600 p-2 text-white">
              <WalletCards className="h-4 w-4" />
            </button>
            <button
              type="button"
              onClick={() => setSettingsOpen((prev) => !prev)}
              className={`rounded-lg border p-2 transition-colors ${
                settingsOpen
                  ? 'border-slate-900 bg-slate-900 text-white'
                  : 'border-slate-200 text-slate-500 hover:bg-slate-50'
              }`}>
              <SlidersHorizontal className="h-4 w-4" />
            </button>
            <button
              type="button"
              onClick={() => setShowAdvancedSearch((prev) => !prev)}
              className={`rounded-lg border p-2 transition-colors ${
                showAdvancedSearch
                  ? 'border-slate-900 bg-slate-900 text-white'
                  : 'border-slate-200 text-slate-500 hover:bg-slate-50'
              }`}>
              <Search className="h-4 w-4" />
            </button>
          </div>

          <div className="flex flex-1 flex-wrap items-center justify-end gap-3">
            <div className="relative">
              <button
                onClick={() => setShowFilters((prev) => !prev)}
                className="flex items-center gap-2 rounded-lg border border-slate-200 px-3 py-2 text-sm text-slate-600 hover:bg-slate-50">
                <Filter className="h-4 w-4" />
                Фильтр
                <ChevronDown className="h-4 w-4" />
              </button>
              {showFilters && (
                <div className="absolute right-0 top-12 z-10 w-80 rounded-xl border border-slate-200 bg-white p-4 shadow-xl">
                  <div className="grid gap-3 text-sm text-slate-600">
                    <label className="flex flex-col gap-1">
                      Приход/Расход
                      <select className="rounded-lg border border-slate-200 px-3 py-2">
                        <option>Все</option>
                        <option>Приход</option>
                        <option>Расход</option>
                      </select>
                    </label>
                    <label className="flex flex-col gap-1">
                      Статья
                      <select className="rounded-lg border border-slate-200 px-3 py-2">
                        <option>Все статьи</option>
                      </select>
                    </label>
                    <label className="flex flex-col gap-1">
                      Контрагент
                      <select className="rounded-lg border border-slate-200 px-3 py-2">
                        <option>Все контрагенты</option>
                      </select>
                    </label>
                    <div className="flex items-center justify-between pt-2">
                      <button className="rounded-lg border border-slate-200 px-3 py-2 text-sm text-slate-600 hover:bg-slate-50">
                        Сбросить
                      </button>
                      <button className="rounded-lg bg-blue-600 px-3 py-2 text-sm text-white hover:bg-blue-700">
                        Применить
                      </button>
                    </div>
                  </div>
                </div>
              )}
            </div>

            <input
              value={search}
              onChange={(event) => setSearch(event.target.value)}
              placeholder="Поиск по контрагенту..."
              className="min-w-[220px] rounded-lg border border-slate-200 px-3 py-2 text-sm text-slate-600 focus:border-blue-500 focus:outline-none"
            />
            <button
              onClick={() => setShowAdvancedSearch((prev) => !prev)}
              className="rounded-lg border border-slate-200 p-2 text-slate-500 hover:bg-slate-50"
              title="Расширенный поиск">
              <SlidersHorizontal className="h-4 w-4" />
            </button>
            <button className="rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700">
              Найти
            </button>
          </div>
        </div>

        {showAdvancedSearch && (
          <div className="border-b border-slate-200 bg-slate-50 px-4 py-4">
            <div className="grid gap-4 md:grid-cols-4">
              <label className="flex flex-col gap-1 text-sm text-slate-600">
                Контрагент
                <input className="rounded-lg border border-slate-200 px-3 py-2" placeholder="Название или ИНН" />
              </label>
              <label className="flex flex-col gap-1 text-sm text-slate-600">
                Назначение
                <input className="rounded-lg border border-slate-200 px-3 py-2" placeholder="Поиск по словам" />
              </label>
              <label className="flex flex-col gap-1 text-sm text-slate-600">
                Сумма
                <select className="rounded-lg border border-slate-200 px-3 py-2">
                  <option>Равно</option>
                  <option>Больше</option>
                  <option>Меньше</option>
                </select>
              </label>
              <label className="flex flex-col gap-1 text-sm text-slate-600">
                Поиск
                <select className="rounded-lg border border-slate-200 px-3 py-2">
                  <option>Приход и Расход</option>
                  <option>Приход</option>
                  <option>Расход</option>
                </select>
              </label>
            </div>
          </div>
        )}

        {settingsOpen && (
          <div className="border-b border-slate-200 bg-slate-50 px-4 py-4">
            <TableColumnSettings
              columns={MONEY_OPERATION_COLUMNS.map((column) => ({
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
                  <span>Строк на странице</span>
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
        )}

        <div className="overflow-x-auto">
          <table className="w-full min-w-[1500px] table-fixed text-sm">
            <colgroup>
              {orderedVisibleColumns.map((columnKey) => (
                <col key={columnKey} style={{ width: getColumnWidth(columnKey) }} />
              ))}
              <col style={{ width: 140 }} />
            </colgroup>
            <thead className="bg-slate-50 text-slate-500">
              <tr>
                {visibleMoneyColumns.map((column) => (
                  <th
                    key={column.key}
                    style={{ width: getColumnWidth(column.key) }}
                    className={`relative px-3 py-2 text-left font-medium ${
                      draggingColumn === column.key ? 'bg-blue-50 text-blue-700' : ''
                    }`}
                    onDragOver={handleColumnDragOver}
                    onDrop={(event) => handleColumnDrop(column.key, event)}>
                    <button
                      type="button"
                      className="inline-flex min-w-0 items-center gap-2 rounded p-1 text-left text-slate-700 transition-colors cursor-grab active:cursor-grabbing"
                      draggable
                      onDragStart={(event) => handleColumnDragStart(column.key, event)}
                      onDragEnd={handleColumnDragEnd}
                      title="Перетащите, чтобы изменить порядок">
                      <GripVertical className="h-4 w-4 shrink-0 text-slate-400" />
                      <span className="truncate">{column.label}</span>
                    </button>
                    <div
                      role="separator"
                      aria-label={`Изменить ширину колонки ${column.label}`}
                      className="absolute right-0 top-0 z-10 h-full w-3 cursor-col-resize"
                      onMouseDown={(event) => handleResizeStart(column.key, event)}
                      onDragStart={(event) => event.preventDefault()}
                    />
                  </th>
                ))}
                <th className="px-3 py-2 text-left font-medium">Действия</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100">
              {loading ? (
                <tr>
                  <td colSpan={visibleMoneyColumns.length + 1} className="px-4 py-10 text-center text-slate-500">
                    Загрузка операций...
                  </td>
                </tr>
              ) : error ? (
                <tr>
                  <td colSpan={visibleMoneyColumns.length + 1} className="px-4 py-10 text-center text-red-500">
                    {error}
                  </td>
                </tr>
              ) : items.length === 0 ? (
                <tr>
                  <td colSpan={visibleMoneyColumns.length + 1} className="px-4 py-10 text-center text-slate-500">
                    В указанном периоде нет операций. Добавить операцию или импортировать из банка
                  </td>
                </tr>
              ) : (
                items.map((item) => (
                  <tr key={item.id} className="hover:bg-slate-50">
                    {visibleMoneyColumns.map((column) => (
                      <td
                        key={column.key}
                        style={{ width: getColumnWidth(column.key) }}
                        className="max-w-0 overflow-hidden px-3 py-2 align-top">
                        {renderCell(item, column.key)}
                      </td>
                    ))}
                    <td className="px-3 py-2">
                      <div className="flex items-center gap-2">
                        {item.isReadonly && (
                          <span className="rounded-full bg-slate-100 px-2 py-1 text-xs text-slate-500">Из графика</span>
                        )}
                        <button
                          onClick={() => handleEdit(item)}
                          disabled={Boolean(item.isReadonly) || item.id <= 0}
                          className="rounded-md border border-slate-200 p-2 text-slate-600 hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-40"
                          aria-label="Изменить">
                          <Pencil className="h-4 w-4" />
                        </button>
                        <button
                          onClick={() => handleDelete(item)}
                          disabled={Boolean(item.isReadonly) || item.id <= 0}
                          className="rounded-md border border-red-200 p-2 text-red-600 hover:bg-red-50 disabled:cursor-not-allowed disabled:opacity-40"
                          aria-label="Удалить">
                          <Trash2 className="h-4 w-4" />
                        </button>
                      </div>
                    </td>
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
        totalItems={total}
        pageSize={pageSize}
        pageSizeOptions={PAGE_SIZE_OPTIONS}
        onPageChange={setPage}
        onPageSizeChange={setPageSize}
        recordsLabel="Найдено записей"
        pageSizeLabel="Строк на странице"
        previousLabel="Назад"
        nextLabel="Вперед"
      />

      <MoneyOperationModal
        source={apiSource ?? 'Bank'}
        initialData={editingOperation}
        open={modalOpen}
        onClose={() => {
          setModalOpen(false);
          setEditingOperation(null);
        }}
        onSave={handleSave}
        isSaving={saving}
      />
      <ConfirmDialog
        open={Boolean(confirmDelete)}
        title="Удалить операцию?"
        description="Действие необратимо. Вы уверены, что хотите удалить операцию?"
        confirmLabel="Удалить"
        cancelLabel="Отмена"
        variant="danger"
        onConfirm={confirmDeleteOperation}
        onCancel={() => setConfirmDelete(null)}
      />
        </>
      )}
    </div>
  );
}
