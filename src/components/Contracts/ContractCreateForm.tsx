import { Loader2, Search, X } from 'lucide-react';
import { useEffect, useMemo, useState } from 'react';
import { useDebouncedValue } from '../../hooks/useDebouncedValue';
import { useTranslation } from '../../hooks/useTranslation';
import { apiService } from '../../services/api';
import type { ContractClient, ContractInput } from '../../types';
import { fromInputToApiDate, toDateInputValue } from '../../utils/dateUtils';
import { ClientStatusBadge } from '../Clients/ClientStatusBadge';

type FormState = {
  number: string;
  title: string;
  date: string;
  amount: string;
  description: string;
  validUntil: string;
};

interface ContractCreateFormProps {
  onSubmit: (payload: ContractInput) => Promise<boolean>;
  submitting: boolean;
  errorMessage?: string | null;
}

function getDefaultFormState(): FormState {
  return {
    number: '',
    title: '',
    date: toDateInputValue(new Date()),
    amount: '',
    description: '',
    validUntil: '',
  };
}

export function ContractCreateForm({ onSubmit, submitting, errorMessage }: ContractCreateFormProps) {
  const { t } = useTranslation();
  const [form, setForm] = useState<FormState>(getDefaultFormState);
  const [localError, setLocalError] = useState<string | null>(null);
  const [selectedClients, setSelectedClients] = useState<ContractClient[]>([]);
  const [clientSearch, setClientSearch] = useState('');
  const debouncedClientSearch = useDebouncedValue(clientSearch.trim(), 350);
  const [clientOptions, setClientOptions] = useState<ContractClient[]>([]);
  const [clientOptionsLoading, setClientOptionsLoading] = useState(false);
  const [clientOptionsError, setClientOptionsError] = useState<string | null>(null);
  const [successMessage, setSuccessMessage] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;

    const fetchClients = async () => {
      setClientOptionsLoading(true);
      try {
        const response = await apiService.getClientsV1({
          search: debouncedClientSearch || undefined,
          sortBy: 'name',
          sortDir: 'asc',
          limit: 20,
        });
        if (cancelled) return;
        const mapped: ContractClient[] = (response ?? []).map((client) => ({
          id: client.id,
          name: client.name,
          company: client.company,
          clientStatusId: client.clientStatusId ?? null,
          clientStatus: client.clientStatus ?? null,
        }));
        setClientOptions(mapped);
        setClientOptionsError(null);
      } catch (error) {
        if (cancelled) return;
        const fallback = t('contractsClientsLoadError') ?? 'Не удалось загрузить клиентов';
        setClientOptions([]);
        setClientOptionsError(error instanceof Error ? error.message : fallback);
      } finally {
        if (!cancelled) {
          setClientOptionsLoading(false);
        }
      }
    };

    void fetchClients();

    return () => {
      cancelled = true;
    };
  }, [debouncedClientSearch, t]);

  const availableClientOptions = useMemo(() => {
    const selectedIds = new Set(selectedClients.map((client) => client.id));
    return clientOptions.filter((client) => !selectedIds.has(client.id));
  }, [clientOptions, selectedClients]);

  const handleAddClient = (client: ContractClient) => {
    setSelectedClients((prev) => {
      if (prev.some((item) => item.id === client.id)) {
        return prev;
      }
      return [...prev, client];
    });
  };

  const handleRemoveClient = (id: number) => {
    setSelectedClients((prev) => prev.filter((client) => client.id !== id));
  };

  const resetForm = () => {
    setForm(getDefaultFormState());
    setSelectedClients([]);
    setClientSearch('');
    setLocalError(null);
    setSuccessMessage(null);
  };

  const handleSubmit = async (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    setLocalError(null);
    setSuccessMessage(null);

    if (!form.number.trim()) {
      setLocalError(t('contractNumberRequired') ?? 'Номер договора обязателен');
      return;
    }

    if (!form.date) {
      setLocalError(t('contractDateRequired') ?? 'Дата договора обязательна');
      return;
    }

    if (selectedClients.length === 0) {
      setLocalError(t('contractClientsRequired') ?? 'Выберите хотя бы одного клиента');
      return;
    }

    let amountValue: number | undefined;
    if (form.amount.trim()) {
      const normalized = form.amount.replace(/\s+/g, '').replace(',', '.');
      const parsed = Number(normalized);
      if (Number.isNaN(parsed) || parsed < 0) {
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
      clientIds: selectedClients.map((client) => client.id),
    };

    const success = await onSubmit(payload);
    if (success) {
      resetForm();
      setSuccessMessage(t('savedSuccessfully') ?? 'Успешно сохранено');
    }
  };

  return (
    <div className="rounded-2xl border border-slate-200 bg-white shadow-sm">
      <div className="border-b border-slate-200 px-6 py-4">
        <h2 className="text-xl font-semibold text-slate-900">{t('contractCreateTitle') ?? 'Новый договор'}</h2>
        <p className="text-sm text-slate-500">
          {t('contractModalSubtitle') ?? 'Укажите ключевую информацию и выберите клиентов.'}
        </p>
      </div>
      <form onSubmit={handleSubmit} className="px-6 py-5">
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
            <span className="font-medium text-slate-700">{t('contractClients') ?? 'Клиенты'}</span>
            <div className="relative">
              <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
              <input
                type="search"
                value={clientSearch}
                onChange={(event) => setClientSearch(event.target.value)}
                placeholder={t('contractClientsSearchPlaceholder') ?? 'Введите имя или компанию клиента для поиска'}
                className="w-full rounded-lg border border-slate-200 py-2 pl-10 pr-3 text-sm focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-500/20"
              />
            </div>
            {selectedClients.length > 0 && (
              <div className="flex flex-wrap gap-2">
                {selectedClients.map((client) => (
                  <span
                    key={client.id}
                    className="inline-flex items-center gap-2 rounded-full border border-emerald-200 bg-emerald-50 px-3 py-1 text-xs font-medium text-emerald-700"
                  >
                    <span className="flex flex-col items-start gap-1 sm:flex-row sm:items-center">
                      <span>
                        {client.name}
                        {client.company ? ` · ${client.company}` : ''}
                      </span>
                      <ClientStatusBadge status={client.clientStatus} />
                    </span>
                    <button
                      type="button"
                      onClick={() => handleRemoveClient(client.id)}
                      className="rounded-full p-0.5 text-emerald-600 transition hover:bg-emerald-100 hover:text-emerald-800"
                      aria-label={t('remove') ?? 'Удалить'}
                    >
                      <X className="h-3 w-3" />
                    </button>
                  </span>
                ))}
              </div>
            )}
            <div className="relative">
              <div className="max-h-56 overflow-y-auto rounded-xl border border-slate-200 bg-white shadow-inner">
                {clientOptionsError ? (
                  <div className="px-3 py-4 text-sm text-rose-600">{clientOptionsError}</div>
                ) : availableClientOptions.length === 0 ? (
                  <div className="px-3 py-4 text-sm text-slate-500">
                    {debouncedClientSearch
                      ? t('contractClientsSearchEmpty') ?? 'По данному запросу ничего не найдено.'
                      : t('contractClientsSearchHint') ?? 'Начните вводить имя клиента для поиска.'}
                  </div>
                ) : (
                  <ul className="divide-y divide-slate-100">
                    {availableClientOptions.map((client) => (
                      <li key={client.id}>
                        <button
                          type="button"
                          onClick={() => handleAddClient(client)}
                          className="flex w-full items-center justify-between gap-3 px-3 py-2 text-left text-sm transition hover:bg-emerald-50"
                        >
                          <span className="flex flex-col">
                            <span className="font-medium text-slate-700">{client.name}</span>
                            {client.company && <span className="text-xs text-slate-500">{client.company}</span>}
                            {client.clientStatus?.name && (
                              <span className="mt-1">
                                <ClientStatusBadge status={client.clientStatus} />
                              </span>
                            )}
                          </span>
                          <span className="text-xs font-semibold uppercase tracking-wide text-emerald-600">
                            {t('contractClientsAdd') ?? 'Добавить'}
                          </span>
                        </button>
                      </li>
                    ))}
                  </ul>
                )}
              </div>
              {clientOptionsLoading && (
                <div className="absolute inset-0 flex items-center justify-center rounded-xl bg-white/70">
                  <Loader2 className="h-5 w-5 animate-spin text-emerald-500" />
                </div>
              )}
            </div>
          </div>
        </div>

        {successMessage && (
          <div className="mt-4 rounded-lg border border-emerald-200 bg-emerald-50 px-3 py-2 text-sm text-emerald-700">
            {successMessage}
          </div>
        )}

        {(localError || errorMessage) && (
          <div className="mt-4 rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">
            {localError || errorMessage}
          </div>
        )}

        <div className="mt-6 flex flex-wrap gap-3">
          <button
            type="button"
            onClick={resetForm}
            className="inline-flex items-center justify-center rounded-lg border border-slate-200 px-4 py-2 text-sm font-medium text-slate-600 hover:bg-slate-50"
            disabled={submitting}
          >
            {t('contractFormClear') ?? 'Очистить форму'}
          </button>
          <button
            type="submit"
            disabled={submitting}
            className="inline-flex items-center justify-center gap-2 rounded-lg bg-emerald-600 px-4 py-2 text-sm font-semibold text-white shadow-sm hover:bg-emerald-500 disabled:opacity-70"
          >
            {submitting && <Loader2 className="h-4 w-4 animate-spin" />}
            <span>{t('create') ?? 'Создать'}</span>
          </button>
        </div>
      </form>
    </div>
  );
}
