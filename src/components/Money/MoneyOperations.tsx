import { useEffect, useMemo, useState } from 'react';
import { createPortal } from 'react-dom';
import {
  Calendar,
  ChevronDown,
  Filter,
  Plus,
  Search,
  SlidersHorizontal,
  WalletCards,
  UploadCloud,
} from 'lucide-react';
import { useDebouncedValue } from '../../hooks/useDebouncedValue';
import { useMoneyOperations } from '../../hooks/useMoneyOperations';
import { apiService } from '../../services/api';
import type {
  MoneyOperation,
  MoneyOperationAccountingType,
  MoneyOperationDirection,
  MoneyOperationSource,
} from '../../types';

type MoneyTab = MoneyOperationSource | 'Total';

const SOURCE_LABELS: Record<MoneyTab, string> = {
  Bank: 'Банк',
  Cash: 'Касса',
  Card: 'Карты',
  Total: 'Итого',
};

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
  const [year, month, day] = value.split('-');
  if (!year || !month || !day) return value;
  return `${day}.${month}.${year}`;
}

function MoneyOperationModal({
  source,
  open,
  onClose,
  onSave,
  isSaving,
}: {
  source: MoneyOperationSource;
  open: boolean;
  onClose: () => void;
  onSave: (payload: MoneyOperation) => Promise<void>;
  isSaving: boolean;
}) {
  const [form, setForm] = useState<MoneyOperation>(() => ({ ...DEFAULT_FORM, source }));
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (open) {
      setForm({ ...DEFAULT_FORM, source });
      setError(null);
    }
  }, [open, source]);

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

  const title = `Добавить операцию по ${SOURCE_LABELS[source].toLowerCase()}`;

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
  const [showFilters, setShowFilters] = useState(false);
  const [showAdvancedSearch, setShowAdvancedSearch] = useState(false);
  const [search, setSearch] = useState('');
  const [modalOpen, setModalOpen] = useState(false);
  const [saving, setSaving] = useState(false);

  const currentYear = new Date().getFullYear();
  const [from, setFrom] = useState(`${currentYear}-01-01`);
  const [to, setTo] = useState(`${currentYear}-12-31`);

  const handleCurrentYear = () => {
    const year = new Date().getFullYear();
    setFrom(`${year}-01-01`);
    setTo(`${year}-12-31`);
  };

  const debouncedSearch = useDebouncedValue(search, 300);

  const apiSource = source === 'Total' ? undefined : source;
  const { items, loading, error, setItems } = useMoneyOperations({
    from,
    to,
    source: apiSource,
    search: debouncedSearch,
  });

  const summary = useMemo(() => {
    const startBalance = 0;
    const income = items
      .filter((item) => item.direction === 'Income' && item.accountingType === 'Fact')
      .reduce((sum, item) => sum + item.amount, 0);
    const expense = items
      .filter((item) => item.direction === 'Expense' && item.accountingType === 'Fact')
      .reduce((sum, item) => sum + item.amount, 0);
    return {
      startBalance,
      income,
      expense,
      endBalance: startBalance + income - expense,
    };
  }, [items]);

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
      const created = await apiService.createMoneyOperation({
        ...prepared,
        id: 0,
        createdAt: new Date().toISOString(),
      });
      setItems((prev) => [created, ...prev]);
      setModalOpen(false);
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="px-6 py-6">
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
            <div className="mt-2 text-xs text-slate-400">Всего поступлений: {items.length}</div>
          </div>
          <div className="rounded-xl border border-slate-200 bg-white p-4">
            <div className="text-sm text-slate-500">Расход Факт</div>
            <div className="text-lg font-semibold text-red-500">{formatMoney(summary.expense)} ₽</div>
            <div className="mt-3 h-0.5 w-full rounded-full bg-red-400"></div>
            <div className="mt-2 text-xs text-slate-400">Всего списаний: {items.length}</div>
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
            onClick={() => setModalOpen(true)}
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
            <button className="rounded-lg border border-slate-200 p-2 text-slate-500 hover:bg-slate-50">
              <SlidersHorizontal className="h-4 w-4" />
            </button>
            <button className="rounded-lg border border-slate-200 p-2 text-slate-500 hover:bg-slate-50">
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

        <div className="overflow-x-auto">
          <table className="w-full min-w-[1100px] text-sm">
            <thead className="bg-slate-50 text-slate-500">
              <tr>
                {[
                  'Дата',
                  'Дата признания',
                  'Приход',
                  'Расход',
                  'Валюта',
                  'Счета',
                  'Контрагент',
                  'Проект',
                  'Точка продаж',
                  'Направление',
                  'Приоритет в оплате',
                  'Назначение',
                  'Опл. счета',
                  'Статья',
                ].map((label) => (
                  <th key={label} className="px-3 py-2 text-left font-medium">
                    {label}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100">
              {loading ? (
                <tr>
                  <td colSpan={14} className="px-4 py-10 text-center text-slate-500">
                    Загрузка операций...
                  </td>
                </tr>
              ) : error ? (
                <tr>
                  <td colSpan={14} className="px-4 py-10 text-center text-red-500">
                    {error}
                  </td>
                </tr>
              ) : items.length === 0 ? (
                <tr>
                  <td colSpan={14} className="px-4 py-10 text-center text-slate-500">
                    В указанном периоде нет операций. Добавить операцию или импортировать из банка
                  </td>
                </tr>
              ) : (
                items.map((item) => (
                  <tr key={item.id} className="hover:bg-slate-50">
                    <td className="px-3 py-2">{formatDate(item.date)}</td>
                    <td className="px-3 py-2">{item.recognitionDate ? formatDate(item.recognitionDate) : '—'}</td>
                    <td className="px-3 py-2 text-emerald-600">
                      {item.direction === 'Income' ? `${formatMoney(item.amount)} ₽` : '—'}
                    </td>
                    <td className="px-3 py-2 text-red-600">
                      {item.direction === 'Expense' ? `${formatMoney(item.amount)} ₽` : '—'}
                    </td>
                    <td className="px-3 py-2">{item.currency ?? 'RUB'}</td>
                    <td className="px-3 py-2">{item.accountName ?? '—'}</td>
                    <td className="px-3 py-2">{item.counterpartyName ?? '—'}</td>
                    <td className="px-3 py-2">{item.projectName ?? '—'}</td>
                    <td className="px-3 py-2">{item.salesPointName ?? '—'}</td>
                    <td className="px-3 py-2">{item.directionName ?? '—'}</td>
                    <td className="px-3 py-2">{item.paymentPriority ?? '—'}</td>
                    <td className="px-3 py-2">{item.purpose ?? '—'}</td>
                    <td className="px-3 py-2">{item.paidAccountName ?? '—'}</td>
                    <td className="px-3 py-2">{item.categoryName ?? '—'}</td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>
      </div>

      <MoneyOperationModal
        source={apiSource ?? 'Bank'}
        open={modalOpen}
        onClose={() => setModalOpen(false)}
        onSave={handleSave}
        isSaving={saving}
      />
    </div>
  );
}
