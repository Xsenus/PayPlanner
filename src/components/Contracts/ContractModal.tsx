import { useEffect, useMemo, useState } from 'react';
import { Loader2, X } from 'lucide-react';
import type { Client, Contract, ContractInput } from '../../types';
import { fromInputToApiDate, toDateInputValue } from '../../utils/dateUtils';
import { useTranslation } from '../../hooks/useTranslation';

interface ContractModalProps {
  open: boolean;
  mode: 'create' | 'edit';
  contract: Contract | null;
  onClose: () => void;
  onSubmit: (payload: ContractInput) => Promise<void>;
  submitting: boolean;
  errorMessage?: string | null;
  clients: Client[];
  clientsLoading: boolean;
  clientsError?: string | null;
}

type FormState = {
  number: string;
  title: string;
  date: string;
  amount: string;
  description: string;
  validUntil: string;
  clientIds: string[];
};

function normalizeContractToForm(contract: Contract | null): FormState {
  if (!contract) {
    const today = toDateInputValue(new Date());
    return {
      number: '',
      title: '',
      date: today,
      amount: '',
      description: '',
      validUntil: '',
      clientIds: [],
    };
  }

  return {
    number: contract.number ?? '',
    title: contract.title ?? '',
    date: toDateInputValue(contract.date) || toDateInputValue(new Date()),
    amount:
      contract.amount !== undefined && contract.amount !== null
        ? String(contract.amount)
        : '',
    description: contract.description ?? '',
    validUntil: toDateInputValue(contract.validUntil) ?? '',
    clientIds: (contract.clients ?? []).map((client) => String(client.id)),
  };
}

export function ContractModal({
  open,
  mode,
  contract,
  onClose,
  onSubmit,
  submitting,
  errorMessage,
  clients,
  clientsLoading,
  clientsError,
}: ContractModalProps) {
  const { t } = useTranslation();
  const [form, setForm] = useState<FormState>(() => normalizeContractToForm(contract));
  const [localError, setLocalError] = useState<string | null>(null);

  useEffect(() => {
    if (open) {
      setForm(normalizeContractToForm(contract));
      setLocalError(null);
    }
  }, [open, contract]);

  const isEdit = mode === 'edit';

  const sortedClients = useMemo(
    () => [...clients].sort((a, b) => a.name.localeCompare(b.name)),
    [clients],
  );

  const toggleClient = (id: string) => {
    setForm((prev) => {
      const exists = prev.clientIds.includes(id);
      return {
        ...prev,
        clientIds: exists
          ? prev.clientIds.filter((value) => value !== id)
          : [...prev.clientIds, id],
      };
    });
  };

  const handleSubmit = async (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    setLocalError(null);

    if (!form.number.trim()) {
      setLocalError(t('contractNumberRequired') ?? 'Номер договора обязателен');
      return;
    }

    if (!form.date) {
      setLocalError(t('contractDateRequired') ?? 'Дата договора обязательна');
      return;
    }

    if (form.clientIds.length === 0) {
      setLocalError(t('contractClientsRequired') ?? 'Выберите хотя бы одного клиента');
      return;
    }

    let amountValue: number | undefined;
    if (form.amount.trim()) {
      const normalized = form.amount.replace(/\s+/g, '').replace(',', '.');
      const parsed = Number(normalized);
      if (Number.isNaN(parsed)) {
        setLocalError(t('contractAmountInvalid') ?? 'Введите корректную сумму');
        return;
      }
      if (parsed < 0) {
        setLocalError(t('contractAmountInvalid') ?? 'Введите корректную сумму');
        return;
      }
      amountValue = parsed;
    }

    const payload: ContractInput = {
      number: form.number.trim(),
      title: form.title.trim() ? form.title.trim() : undefined,
      date: fromInputToApiDate(form.date) ?? form.date,
      description: form.description.trim() ? form.description.trim() : undefined,
      amount: amountValue,
      validUntil: form.validUntil ? fromInputToApiDate(form.validUntil) ?? form.validUntil : undefined,
      clientIds: form.clientIds.map((id) => Number(id)).filter((id) => !Number.isNaN(id)),
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
              {isEdit ? t('contractEditTitle') ?? 'Редактировать договор' : t('contractCreateTitle') ?? 'Новый договор'}
            </h2>
            <p className="text-sm text-slate-500">
              {t('contractModalSubtitle') ?? 'Укажите ключевую информацию и выберите клиентов.'}
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

        <form onSubmit={handleSubmit} className="max-h-[75vh] overflow-y-auto px-6 py-5">
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">{t('contractNumber') ?? 'Номер договора'}</span>
              <input
                type="text"
                value={form.number}
                onChange={(event) => setForm((prev) => ({ ...prev, number: event.target.value }))}
                className="rounded-lg border border-slate-200 px-3 py-2 text-sm focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-500/20"
                required
              />
            </label>
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">{t('contractDate') ?? 'Дата договора'}</span>
              <input
                type="date"
                value={form.date}
                onChange={(event) => setForm((prev) => ({ ...prev, date: event.target.value }))}
                className="rounded-lg border border-slate-200 px-3 py-2 text-sm focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-500/20"
                required
              />
            </label>
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">{t('contractTitle') ?? 'Название'}</span>
              <input
                type="text"
                value={form.title}
                onChange={(event) => setForm((prev) => ({ ...prev, title: event.target.value }))}
                className="rounded-lg border border-slate-200 px-3 py-2 text-sm focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-500/20"
              />
            </label>
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">{t('contractAmount') ?? 'Сумма договора'}</span>
              <input
                type="text"
                value={form.amount}
                onChange={(event) => setForm((prev) => ({ ...prev, amount: event.target.value }))}
                className="rounded-lg border border-slate-200 px-3 py-2 text-sm focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-500/20"
                placeholder="0,00"
              />
            </label>
            <label className="flex flex-col gap-1 text-sm sm:col-span-2">
              <span className="font-medium text-slate-700">{t('contractDescription') ?? 'Описание'}</span>
              <textarea
                value={form.description}
                onChange={(event) => setForm((prev) => ({ ...prev, description: event.target.value }))}
                className="min-h-[96px] rounded-lg border border-slate-200 px-3 py-2 text-sm focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-500/20"
                placeholder={t('contractDescriptionPlaceholder') ?? 'Условия, комментарии и другая важная информация'}
              />
            </label>
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">{t('contractValidUntil') ?? 'Действует до'}</span>
              <input
                type="date"
                value={form.validUntil}
                onChange={(event) => setForm((prev) => ({ ...prev, validUntil: event.target.value }))}
                className="rounded-lg border border-slate-200 px-3 py-2 text-sm focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-500/20"
              />
            </label>
            <div className="flex flex-col gap-2 text-sm sm:col-span-2">
              <div className="flex items-center justify-between">
                <span className="font-medium text-slate-700">{t('contractClients') ?? 'Клиенты'}</span>
                {clientsLoading && <Loader2 className="h-4 w-4 animate-spin text-slate-400" />}
              </div>
              <div className="max-h-48 overflow-y-auto rounded-xl border border-slate-200 bg-slate-50/60 p-3">
                {clientsError ? (
                  <div className="rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">
                    {clientsError}
                  </div>
                ) : sortedClients.length === 0 ? (
                  <p className="text-sm text-slate-500">
                    {t('contractNoClients') ?? 'Нет доступных клиентов для выбора.'}
                  </p>
                ) : (
                  <div className="grid grid-cols-1 gap-2 sm:grid-cols-2">
                    {sortedClients.map((client) => {
                      const value = String(client.id);
                      const checked = form.clientIds.includes(value);
                      return (
                        <label
                          key={client.id}
                          className="flex items-center gap-2 rounded-lg border border-transparent bg-white px-3 py-2 text-sm text-slate-600 shadow-sm transition hover:border-emerald-200"
                        >
                          <input
                            type="checkbox"
                            checked={checked}
                            onChange={() => toggleClient(value)}
                            className="h-4 w-4 rounded border-slate-300 text-emerald-600 focus:ring-emerald-500"
                          />
                          <span className="flex flex-col">
                            <span className="font-medium text-slate-700">{client.name}</span>
                            {client.company && (
                              <span className="text-xs text-slate-500">{client.company}</span>
                            )}
                          </span>
                        </label>
                      );
                    })}
                  </div>
                )}
              </div>
            </div>
          </div>

          {(localError || errorMessage) && (
            <div className="mt-4 rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">
              {localError || errorMessage}
            </div>
          )}

          <div className="mt-6 flex flex-col-reverse gap-3 sm:flex-row sm:items-center sm:justify-between">
            <button
              type="button"
              onClick={onClose}
              className="inline-flex items-center justify-center rounded-lg border border-slate-200 px-4 py-2 text-sm font-medium text-slate-600 hover:bg-slate-50"
              disabled={submitting}
            >
              {t('cancel') ?? 'Отмена'}
            </button>
            <button
              type="submit"
              disabled={submitting}
              className="inline-flex items-center justify-center gap-2 rounded-lg bg-emerald-600 px-4 py-2 text-sm font-semibold text-white shadow-sm hover:bg-emerald-500 disabled:opacity-70"
            >
              {submitting && <Loader2 className="h-4 w-4 animate-spin" />}
              <span>{isEdit ? t('update') ?? 'Обновить' : t('create') ?? 'Создать'}</span>
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}
