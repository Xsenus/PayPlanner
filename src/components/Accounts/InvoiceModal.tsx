import { useCallback, useEffect, useMemo, useState } from 'react';
import { Loader2, Search, X } from 'lucide-react';
import type { Act, ActStatus, Client, Invoice, InvoiceInput, PaymentStatus } from '../../types';
import { fromInputToApiDate, formatLocalYMD, toDateInputValue } from '../../utils/dateUtils';
import { useTranslation } from '../../hooks/useTranslation';
import { useDebouncedValue } from '../../hooks/useDebouncedValue';
import { apiService } from '../../services/api';
import { formatCurrencySmart } from '../../utils/formatters';

type FormState = {
  number: string;
  date: string;
  dueDate: string;
  amount: string;
  status: PaymentStatus;
  clientId: string;
  description: string;
  actReference: string;
  paidDate: string;
};

type ActOption = Pick<Act, 'id' | 'number' | 'title' | 'date' | 'amount' | 'invoiceNumber' | 'clientId' | 'clientName' | 'status'>;

interface InvoiceModalProps {
  open: boolean;
  mode: 'create' | 'edit';
  invoice: Invoice | null;
  onClose: () => void;
  onSubmit: (payload: InvoiceInput) => Promise<void>;
  submitting: boolean;
  errorMessage?: string | null;
  clients: Client[];
  lookupsLoading: boolean;
  lookupsError?: string | null;
  defaultClientId?: number;
}

function normalizeInvoiceToForm(invoice: Invoice | null, defaultClientId?: number): FormState {
  const today = formatLocalYMD(new Date());
  if (!invoice) {
    return {
      number: '',
      date: today,
      dueDate: '',
      amount: '',
      status: 'Pending',
      clientId: defaultClientId ? String(defaultClientId) : '',
      description: '',
      actReference: '',
      paidDate: '',
    };
  }

  return {
    number: invoice.number ?? '',
    date: toDateInputValue(invoice.date) || today,
    dueDate: toDateInputValue(invoice.dueDate) || '',
    amount: invoice.amount !== undefined && invoice.amount !== null ? String(invoice.amount) : '',
    status: invoice.status ?? 'Pending',
    clientId: invoice.clientId ? String(invoice.clientId) : defaultClientId ? String(defaultClientId) : '',
    description: invoice.description ?? '',
    actReference: invoice.actReference ?? '',
    paidDate: toDateInputValue(invoice.paidDate) || '',
  };
}

function actLabel(act: ActOption | null): string {
  if (!act) return '';
  const parts: string[] = [];
  if (act.number) parts.push(`№${act.number}`);
  if (act.title) parts.push(act.title);
  return parts.join(' · ');
}

function formatDate(value?: string | null): string {
  if (!value) return '';
  const normalized = toDateInputValue(value);
  if (!normalized) return value;
  const [y, m, d] = normalized.split('-');
  return `${d}.${m}.${y}`;
}

export function InvoiceModal({
  open,
  mode,
  invoice,
  onClose,
  onSubmit,
  submitting,
  errorMessage,
  clients,
  lookupsLoading,
  lookupsError,
  defaultClientId,
}: InvoiceModalProps) {
  const { t } = useTranslation();
  const [form, setForm] = useState<FormState>(() => normalizeInvoiceToForm(invoice, defaultClientId));
  const [localError, setLocalError] = useState<string | null>(null);

  const initialAct = useMemo<ActOption | null>(() => {
    if (!invoice?.actId) return null;
    return {
      id: invoice.actId,
      number: invoice.actNumber ?? invoice.number,
      title: invoice.actTitle ?? undefined,
      date: invoice.date,
      amount: invoice.amount,
      invoiceNumber: invoice.actNumber ?? invoice.number,
      clientId: invoice.clientId ?? undefined,
      clientName: invoice.clientName ?? undefined,
      status: (invoice.actStatus ?? 'Created') as ActStatus,
    };
  }, [invoice]);

  const [actSearch, setActSearch] = useState<string>(() => actLabel(initialAct));
  const [actSuggestions, setActSuggestions] = useState<ActOption[]>([]);
  const [actsLoading, setActsLoading] = useState(false);
  const [actsError, setActsError] = useState<string | null>(null);
  const [actDropdownOpen, setActDropdownOpen] = useState(false);

  const debouncedActSearch = useDebouncedValue(actSearch.trim(), 300);

  useEffect(() => {
    if (open) {
      setForm(normalizeInvoiceToForm(invoice, defaultClientId));
      const nextInitial = invoice?.actId ? {
        id: invoice.actId,
        number: invoice.actNumber ?? invoice.number,
        title: invoice.actTitle ?? undefined,
        date: invoice.date,
        amount: invoice.amount,
        invoiceNumber: invoice.actNumber ?? invoice.number,
        clientId: invoice.clientId ?? undefined,
        clientName: invoice.clientName ?? undefined,
        status: (invoice.actStatus ?? 'Created') as ActStatus,
      } : null;
      setActSearch(actLabel(nextInitial));
      setActsError(null);
      setLocalError(null);
    }
  }, [open, invoice, defaultClientId]);

  const fetchActs = useCallback(async () => {
    if (debouncedActSearch.length < 2) {
      setActSuggestions([]);
      return;
    }
    setActsLoading(true);
    try {
      const response = await apiService.getActs({
        search: debouncedActSearch,
        page: 1,
        pageSize: 5,
        sortBy: 'date',
        sortDir: 'desc',
        clientId: form.clientId ? Number(form.clientId) : undefined,
      });
      const items = response.items ?? [];
      const options: ActOption[] = items.map((act) => ({
        id: act.id,
        number: act.number,
        title: act.title ?? undefined,
        date: act.date,
        amount: act.amount,
        invoiceNumber: act.invoiceNumber ?? undefined,
        clientId: act.clientId ?? undefined,
        clientName: act.clientName ?? undefined,
        status: act.status,
      }));
      setActSuggestions(options);
      setActsError(null);
    } catch (err) {
      setActsError(err instanceof Error ? err.message : 'Не удалось загрузить акты');
      setActSuggestions([]);
    } finally {
      setActsLoading(false);
    }
  }, [debouncedActSearch, form.clientId]);

  useEffect(() => {
    if (!open) return;
    void fetchActs();
  }, [fetchActs, open]);

  const handleActSelect = (act: ActOption) => {
    setActSearch(actLabel(act));
    setActSuggestions([]);
    setActDropdownOpen(false);
    setForm((prev) => ({
      ...prev,
      number: act.invoiceNumber ?? act.number ?? prev.number,
      date: toDateInputValue(act.date) || prev.date,
      dueDate: prev.dueDate || toDateInputValue(act.date),
      amount: act.amount ? String(act.amount) : prev.amount,
      clientId: act.clientId ? String(act.clientId) : prev.clientId,
    }));
  };

  const clearSelectedAct = () => {
    setActSearch('');
  };

  const handleChange = (field: keyof FormState) => (
    event: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement>,
  ) => {
    const value = event.target.value;
    setForm((prev) => ({ ...prev, [field]: value }));
  };

  const handleSubmit = async (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    setLocalError(null);

    const number = form.number.trim();
    if (!number) {
      setLocalError(t('invoiceNumber') ?? 'Номер счёта обязателен');
      return;
    }
    if (!form.date) {
      setLocalError(t('invoiceDate') ?? 'Дата обязательна');
      return;
    }
    if (!form.clientId) {
      setLocalError(t('invoiceClientPlaceholder') ?? 'Выберите клиента');
      return;
    }

    const parsedAmount = Number.parseFloat(form.amount.replace(',', '.'));
    if (Number.isNaN(parsedAmount) || parsedAmount < 0) {
      setLocalError(t('invoiceAmount') ?? 'Введите корректную сумму');
      return;
    }

    const payload: InvoiceInput = {
      number,
      date: fromInputToApiDate(form.date) ?? form.date,
      dueDate: form.dueDate ? fromInputToApiDate(form.dueDate) ?? form.dueDate : undefined,
      amount: parsedAmount,
      status: form.status,
      clientId: Number(form.clientId),
      description: form.description.trim() ? form.description.trim() : undefined,
      actReference: form.actReference.trim() ? form.actReference.trim() : undefined,
      paidDate: form.paidDate ? fromInputToApiDate(form.paidDate) ?? form.paidDate : undefined,
    };

    try {
      await onSubmit(payload);
    } catch (err) {
      if (err instanceof Error && !errorMessage) {
        setLocalError(err.message);
      }
    }
  };

  if (!open) return null;

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-900/60 px-4 py-8">
      <div className="relative w-full max-w-3xl overflow-hidden rounded-2xl bg-white shadow-2xl">
        <div className="flex items-center justify-between border-b border-slate-200 px-6 py-4">
          <div>
            <h2 className="text-xl font-semibold text-slate-900">
              {mode === 'edit'
                ? t('invoiceEdit') ?? 'Редактировать счёт'
                : t('invoiceCreate') ?? 'Создать счёт'}
            </h2>
            <p className="text-sm text-slate-500">
              {t('invoiceModalSubtitle') ?? 'Укажите параметры исходящего счёта'}
            </p>
          </div>
          <button
            type="button"
            onClick={onClose}
            className="rounded-full p-2 text-slate-500 hover:bg-slate-100 hover:text-slate-700"
            aria-label={t('close') ?? 'Закрыть'}
          >
            <X className="h-5 w-5" />
          </button>
        </div>

        <form onSubmit={handleSubmit} className="max-h-[75vh] overflow-y-auto px-6 py-5 space-y-4">
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">{t('invoiceNumber') ?? 'Номер счёта'}</span>
              <input
                type="text"
                value={form.number}
                onChange={handleChange('number')}
                className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                required
              />
            </label>

            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">{t('invoiceDate') ?? 'Дата выставления'}</span>
              <input
                type="date"
                value={form.date}
                onChange={handleChange('date')}
                className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                required
              />
            </label>

            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">{t('invoiceDueDate') ?? 'Срок оплаты'}</span>
              <input
                type="date"
                value={form.dueDate}
                onChange={handleChange('dueDate')}
                className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
              />
            </label>

            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">{t('invoiceAmount') ?? 'Сумма'}</span>
              <input
                type="number"
                step="0.01"
                value={form.amount}
                onChange={handleChange('amount')}
                className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                placeholder="0.00"
              />
            </label>

            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">{t('invoiceStatus') ?? 'Статус'}</span>
              <select
                value={form.status}
                onChange={handleChange('status')}
                className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
              >
                <option value="Pending">{t('invoicePendingBadge') ?? t('pending') ?? 'Ожидается'}</option>
                <option value="Completed">{t('invoicePaidBadge') ?? t('completedStatus') ?? 'Оплачен'}</option>
                <option value="Overdue">{t('invoiceOverdueBadge') ?? t('overdue') ?? 'Просрочен'}</option>
                <option value="Processing">{t('processingStatus') ?? 'В обработке'}</option>
                <option value="Cancelled">{t('cancelledStatus') ?? 'Отменён'}</option>
              </select>
            </label>

            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">{t('invoiceClient') ?? 'Контрагент'}</span>
              <select
                value={form.clientId}
                onChange={handleChange('clientId')}
                className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
              >
                <option value="">{t('invoiceClientPlaceholder') ?? 'Выберите клиента'}</option>
                {clients.map((client) => (
                  <option key={client.id} value={client.id}>
                    {client.name}
                  </option>
                ))}
              </select>
              {lookupsLoading && (
                <span className="text-xs text-slate-500">{t('loading') ?? 'Загрузка...'}</span>
              )}
              {lookupsError && (
                <span className="text-xs text-rose-600">{lookupsError}</span>
              )}
            </label>
          </div>

          <label className="flex flex-col gap-1 text-sm">
            <span className="font-medium text-slate-700">{t('invoiceDescription') ?? t('description') ?? 'Описание'}</span>
            <textarea
              value={form.description}
              onChange={handleChange('description')}
              rows={3}
              className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
            />
          </label>

          <div className="flex flex-col gap-1 text-sm">
            <span className="font-medium text-slate-700">{t('invoiceAct') ?? 'Акт'}</span>
            <div className="relative">
              <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
              <input
                type="text"
                value={actSearch}
                onChange={(event) => {
                  setActSearch(event.target.value);
                  setActDropdownOpen(true);
                }}
                onFocus={() => setActDropdownOpen(true)}
                onBlur={() => setTimeout(() => setActDropdownOpen(false), 150)}
                placeholder={t('invoiceActSearchPlaceholder') ?? 'Поиск акта по номеру или названию'}
                className="w-full rounded-lg border border-slate-300 px-9 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
              />
              {actSearch && (
                <button
                  type="button"
                  onClick={clearSelectedAct}
                  className="absolute right-3 top-1/2 -translate-y-1/2 rounded-full p-1 text-slate-500 hover:bg-slate-100"
                  aria-label={t('clear') ?? 'Очистить'}
                >
                  <X className="h-3.5 w-3.5" />
                </button>
              )}
              {actDropdownOpen && (actsLoading || actSuggestions.length > 0 || actsError) && (
                <div className="absolute z-20 mt-1 max-h-60 w-full overflow-auto rounded-lg border border-slate-200 bg-white shadow-lg">
                  {actsLoading ? (
                    <div className="flex items-center gap-2 px-3 py-2 text-sm text-slate-500">
                      <Loader2 className="h-4 w-4 animate-spin" /> {t('loading') ?? 'Загрузка...'}
                    </div>
                  ) : actsError ? (
                    <div className="px-3 py-2 text-sm text-rose-600">{actsError}</div>
                  ) : actSuggestions.length === 0 ? (
                    <div className="px-3 py-2 text-sm text-slate-500">
                      {t('nothingFound') ?? 'Ничего не найдено'}
                    </div>
                  ) : (
                    actSuggestions.map((act) => (
                      <button
                        key={act.id}
                        type="button"
                        onClick={() => handleActSelect(act)}
                        className="w-full border-b border-slate-100 px-3 py-2 text-left last:border-b-0 hover:bg-slate-50"
                      >
                        <div className="font-medium text-slate-900">{actLabel(act)}</div>
                        <div className="mt-1 text-xs text-slate-500">
                          <span>{formatDate(act.date)}</span>
                          <span className="mx-2">·</span>
                          <span>{formatCurrencySmart(act.amount ?? 0).full}</span>
                        </div>
                      </button>
                    ))
                  )}
                </div>
              )}
            </div>
          </div>

          <label className="flex flex-col gap-1 text-sm">
            <span className="font-medium text-slate-700">{t('invoiceManualActLabel') ?? 'Комментарий по акту'}</span>
            <textarea
              value={form.actReference}
              onChange={handleChange('actReference')}
              rows={2}
              placeholder={t('invoiceActManualPlaceholder') ?? 'Например: акт будет подписан позже'}
              className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
            />
          </label>

          <label className="flex flex-col gap-1 text-sm">
            <span className="font-medium text-slate-700">{t('invoicePaidDate') ?? 'Дата оплаты'}</span>
            <input
              type="date"
              value={form.paidDate}
              onChange={handleChange('paidDate')}
              className="w-full rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
            />
          </label>

          {(localError || errorMessage) && (
            <div className="rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">
              {localError ?? errorMessage}
            </div>
          )}

          <div className="flex items-center justify-end gap-3 pt-2">
            <button
              type="button"
              onClick={onClose}
              className="rounded-lg border border-slate-200 px-4 py-2 text-sm text-slate-600 hover:bg-slate-100"
            >
              {t('cancel') ?? 'Отмена'}
            </button>
            <button
              type="submit"
              disabled={submitting}
              className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-70"
            >
              {submitting && <Loader2 className="h-4 w-4 animate-spin" />}
              {mode === 'edit' ? t('invoiceEdit') ?? 'Сохранить изменения' : t('invoiceCreate') ?? 'Создать счёт'}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}
