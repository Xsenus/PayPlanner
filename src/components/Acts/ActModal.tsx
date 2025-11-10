import { useEffect, useMemo, useState } from 'react';
import type { Act, ActInput, ActResponsible, ActStatus, Client } from '../../types';
import { fromInputToApiDate, toDateInputValue } from '../../utils/dateUtils';
import { useTranslation } from '../../hooks/useTranslation';
import { Loader2, X } from 'lucide-react';

interface ActModalProps {
  open: boolean;
  mode: 'create' | 'edit';
  act: Act | null;
  onClose: () => void;
  onSubmit: (payload: ActInput) => Promise<void>;
  submitting: boolean;
  errorMessage?: string | null;
  clients: Client[];
  responsibles: ActResponsible[];
  lookupsLoading: boolean;
  lookupsError?: string | null;
}

type FormState = {
  number: string;
  title: string;
  date: string;
  amount: string;
  invoiceNumber: string;
  counterpartyInn: string;
  status: ActStatus;
  clientId: string;
  responsibleId: string;
  comment: string;
};

const STATUS_VALUES: ActStatus[] = ['Created', 'Transferred', 'Signed', 'Terminated'];

function normalizeActToForm(act: Act | null): FormState {
  if (!act) {
    const today = toDateInputValue(new Date());
    return {
      number: '',
      title: '',
      date: today,
      amount: '',
      invoiceNumber: '',
      counterpartyInn: '',
      status: 'Created',
      clientId: '',
      responsibleId: '',
      comment: '',
    };
  }

  return {
    number: act.number ?? '',
    title: act.title ?? '',
    date: toDateInputValue(act.date) || toDateInputValue(new Date()),
    amount: act.amount !== undefined && act.amount !== null ? String(act.amount) : '',
    invoiceNumber: act.invoiceNumber ?? '',
    counterpartyInn: act.counterpartyInn ?? '',
    status: act.status,
    clientId: act.clientId ? String(act.clientId) : '',
    responsibleId: act.responsibleId ? String(act.responsibleId) : '',
    comment: act.comment ?? '',
  };
}

export function ActModal({
  open,
  mode,
  act,
  onClose,
  onSubmit,
  submitting,
  errorMessage,
  clients,
  responsibles,
  lookupsLoading,
  lookupsError,
}: ActModalProps) {
  const { t } = useTranslation();
  const [form, setForm] = useState<FormState>(() => normalizeActToForm(act));
  const [localError, setLocalError] = useState<string | null>(null);

  useEffect(() => {
    if (open) {
      setForm(normalizeActToForm(act));
      setLocalError(null);
    }
  }, [open, act]);

  const isEdit = mode === 'edit';

  const statusOptions = useMemo(
    () =>
      STATUS_VALUES.map((status) => ({
        value: status,
        label:
          status === 'Created'
            ? t('actStatusCreated') ?? 'Создан'
            : status === 'Transferred'
            ? t('actStatusTransferred') ?? 'Передано'
            : status === 'Signed'
            ? t('actStatusSigned') ?? 'Подписано'
            : t('actStatusTerminated') ?? 'Расторгнуто',
      })),
    [t],
  );

  const handleChange = (field: keyof FormState) => (event: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement>) => {
    const value = event.target.value;
    setForm((prev) => ({ ...prev, [field]: value }));
  };

  const handleSubmit = async (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    setLocalError(null);

    if (!form.number.trim()) {
      setLocalError(t('actNumber') ?? 'Номер акта обязателен');
      return;
    }

    if (!form.date) {
      setLocalError(t('actDate') ?? 'Дата акта обязательна');
      return;
    }

    const parsedAmount = Number(String(form.amount).replace(/\s+/g, '').replace(',', '.'));
    if (Number.isNaN(parsedAmount)) {
      setLocalError(t('actAmount') ?? 'Введите корректную сумму');
      return;
    }

    const payload: ActInput = {
      number: form.number.trim(),
      title: form.title.trim() ? form.title.trim() : undefined,
      date: fromInputToApiDate(form.date) ?? form.date,
      amount: parsedAmount,
      invoiceNumber: form.invoiceNumber.trim() ? form.invoiceNumber.trim() : undefined,
      counterpartyInn: form.counterpartyInn.trim() ? form.counterpartyInn.trim() : undefined,
      status: form.status,
      clientId: form.clientId ? Number(form.clientId) : undefined,
      responsibleId: form.responsibleId ? Number(form.responsibleId) : undefined,
      comment: form.comment.trim() ? form.comment.trim() : undefined,
    };

    try {
      await onSubmit(payload);
    } catch (error) {
      if (error instanceof Error && !errorMessage) {
        setLocalError(error.message);
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
              {isEdit ? t('actEdit') ?? 'Редактировать акт' : t('actCreate') ?? 'Создать акт'}
            </h2>
            <p className="text-sm text-slate-500">
              {t('actModalSubtitle') ?? 'Создайте или обновите данные по акту'}
            </p>
          </div>
          <button
            type="button"
            onClick={onClose}
            className="rounded-full p-2 text-slate-500 hover:bg-slate-100 hover:text-slate-700"
            aria-label={t('close') ?? 'Закрыть'}>
            <X className="h-5 w-5" />
          </button>
        </div>

        <form onSubmit={handleSubmit} className="max-h-[75vh] overflow-y-auto px-6 py-5">
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">{t('actNumber') ?? 'Номер акта'}</span>
              <input
                type="text"
                value={form.number}
                onChange={handleChange('number')}
                className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-200"
                required
              />
            </label>

            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">{t('actDate') ?? 'Дата'}</span>
              <input
                type="date"
                value={form.date}
                onChange={handleChange('date')}
                className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-200"
                required
              />
            </label>

            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">{t('actAmount') ?? 'Сумма'}</span>
              <input
                type="number"
                step="0.01"
                value={form.amount}
                onChange={handleChange('amount')}
                className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-200"
                placeholder="0.00"
              />
            </label>

            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">{t('actInvoice') ?? 'Счёт'}</span>
              <input
                type="text"
                value={form.invoiceNumber}
                onChange={handleChange('invoiceNumber')}
                className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-200"
                placeholder=""
              />
            </label>

            <label className="flex flex-col gap-1 text-sm sm:col-span-2">
              <span className="font-medium text-slate-700">{t('actTitle') ?? 'Название'}</span>
              <input
                type="text"
                value={form.title}
                onChange={handleChange('title')}
                className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-200"
              />
            </label>

            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">{t('actClient') ?? 'Контрагент'}</span>
              <select
                value={form.clientId}
                onChange={handleChange('clientId')}
                className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-200">
                <option value="">{t('selectClient') ?? 'Выберите клиента'}</option>
                {clients.map((client) => (
                  <option key={client.id} value={client.id}>
                    {client.name}
                  </option>
                ))}
              </select>
            </label>

            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">{t('actInn') ?? 'ИНН контрагента'}</span>
              <input
                type="text"
                value={form.counterpartyInn}
                onChange={handleChange('counterpartyInn')}
                className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-200"
              />
            </label>

            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">{t('actStatus') ?? 'Статус'}</span>
              <select
                value={form.status}
                onChange={handleChange('status')}
                className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-200">
                {statusOptions.map((option) => (
                  <option key={option.value} value={option.value}>
                    {option.label}
                  </option>
                ))}
              </select>
            </label>

            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">{t('actResponsible') ?? 'Ответственный'}</span>
              <select
                value={form.responsibleId}
                onChange={handleChange('responsibleId')}
                className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-200">
                <option value="">{t('actResponsibleNotSelected') ?? 'Ответственный не выбран'}</option>
                {responsibles.map((employee) => (
                  <option key={employee.id} value={employee.id}>
                    {employee.fullName}
                  </option>
                ))}
              </select>
            </label>

            <label className="flex flex-col gap-1 text-sm sm:col-span-2">
              <span className="font-medium text-slate-700">{t('actComment') ?? 'Комментарий'}</span>
              <textarea
                value={form.comment}
                onChange={handleChange('comment')}
                rows={3}
                className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-200"
              />
            </label>
          </div>

          {lookupsLoading && (
            <div className="mt-4 flex items-center gap-2 text-sm text-slate-500">
              <Loader2 className="h-4 w-4 animate-spin" />
              <span>{t('loading') ?? 'Загрузка...'}...</span>
            </div>
          )}

          {lookupsError && (
            <div className="mt-4 rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-700">
              {lookupsError}
            </div>
          )}

          {(localError || errorMessage) && (
            <div className="mt-4 rounded-lg border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700">
              {localError || errorMessage}
            </div>
          )}

          <div className="mt-6 flex flex-col-reverse gap-3 sm:flex-row sm:justify-end">
            <button
              type="button"
              onClick={onClose}
              className="inline-flex items-center justify-center rounded-lg border border-slate-300 px-4 py-2 text-sm font-medium text-slate-700 transition-colors hover:bg-slate-100">
              {t('cancel') ?? 'Отмена'}
            </button>
            <button
              type="submit"
              disabled={submitting}
              className="inline-flex items-center justify-center rounded-lg bg-indigo-600 px-4 py-2 text-sm font-medium text-white shadow-sm transition-colors hover:bg-indigo-500 disabled:cursor-not-allowed disabled:bg-indigo-300">
              {submitting && <Loader2 className="mr-2 h-4 w-4 animate-spin" />} 
              {submitting
                ? isEdit
                  ? t('saving') ?? 'Сохранение...'
                  : t('adding') ?? 'Добавление...'
                : isEdit
                ? t('save') ?? 'Сохранить'
                : t('create') ?? 'Создать'}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}
