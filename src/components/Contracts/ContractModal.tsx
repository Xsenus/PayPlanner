import { useEffect, useMemo, useState } from 'react';
import { Loader2, Search, X } from 'lucide-react';
import type { Contract, ContractClient, ContractInput } from '../../types';
import { fromInputToApiDate, toDateInputValue } from '../../utils/dateUtils';
import { useTranslation } from '../../hooks/useTranslation';
import { useDebouncedValue } from '../../hooks/useDebouncedValue';
import { apiService } from '../../services/api';

interface ContractModalProps {
  open: boolean;
  mode: 'create' | 'edit';
  contract: Contract | null;
  onClose: () => void;
  onSubmit: (payload: ContractInput) => Promise<void>;
  submitting: boolean;
  errorMessage?: string | null;
}

type FormState = {
  number: string;
  title: string;
  date: string;
  amount: string;
  description: string;
  validUntil: string;
};

type SelectedClient = {
  id: number;
  name: string;
  company?: string | null;
  isActive?: boolean;
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
  };
}

function normalizeContractClients(contract: Contract | null): SelectedClient[] {
  return (contract?.clients ?? []).map((client: ContractClient) => ({
    id: client.id,
    name: client.name,
    company: client.company ?? undefined,
  }));
}

export function ContractModal({
  open,
  mode,
  contract,
  onClose,
  onSubmit,
  submitting,
  errorMessage,
}: ContractModalProps) {
  const { t } = useTranslation();
  const [form, setForm] = useState<FormState>(() => normalizeContractToForm(contract));
  const [localError, setLocalError] = useState<string | null>(null);
  const [selectedClients, setSelectedClients] = useState<SelectedClient[]>(() =>
    normalizeContractClients(contract),
  );
  const [clientQuery, setClientQuery] = useState('');
  const debouncedQuery = useDebouncedValue(clientQuery.trim(), 300);
  const selectedClientIds = useMemo(
    () => selectedClients.map((client) => client.id),
    [selectedClients],
  );
  const selectedClientIdsKey = useMemo(
    () => selectedClientIds.join(','),
    [selectedClientIds],
  );
  const [clientOptions, setClientOptions] = useState<SelectedClient[]>([]);
  const [clientOptionsLoading, setClientOptionsLoading] = useState(false);
  const [clientOptionsError, setClientOptionsError] = useState<string | null>(null);

  useEffect(() => {
    if (open) {
      setForm(normalizeContractToForm(contract));
      setLocalError(null);
      setSelectedClients(normalizeContractClients(contract));
      setClientQuery('');
    }
  }, [open, contract]);

  const isEdit = mode === 'edit';

  useEffect(() => {
    if (!open) return;

    let cancelled = false;
    setClientOptionsLoading(true);
    setClientOptionsError(null);

    const loadOptions = async () => {
      try {
        const lookup = await apiService.lookupClients({
          search: debouncedQuery || undefined,
          includeInactive: true,
          limit: 20,
          ids: selectedClientIds,
        });
        if (cancelled) return;
        setSelectedClients((prev) => {
          let changed = false;
          const enriched = prev.map((client) => {
            const match = lookup.find((item) => item.id === client.id);
            if (!match) return client;
            const company = match.company ?? undefined;
            const isActive = match.isActive;
            if (client.company !== company || client.isActive !== isActive) {
              changed = true;
              return {
                ...client,
                company,
                isActive,
              };
            }
            return client;
          });
          return changed ? enriched : prev;
        });
        setClientOptions(
          lookup.map((client) => ({
            id: client.id,
            name: client.name,
            company: client.company ?? undefined,
            isActive: client.isActive,
          })),
        );
        setClientOptionsError(null);
      } catch (error) {
        if (cancelled) return;
        setClientOptionsError(
          error instanceof Error
            ? error.message
            : t('contractClientsLoadError') ?? 'Не удалось загрузить клиентов',
        );
        setClientOptions([]);
      } finally {
        if (!cancelled) {
          setClientOptionsLoading(false);
        }
      }
    };

    void loadOptions();

    return () => {
      cancelled = true;
    };
  }, [open, debouncedQuery, selectedClientIdsKey, t]);

  const availableClients = useMemo(
    () =>
      clientOptions.filter(
        (option) => !selectedClients.some((selected) => selected.id === option.id),
      ),
    [clientOptions, selectedClients],
  );

  const removeClient = (id: number) => {
    setSelectedClients((prev) => prev.filter((client) => client.id !== id));
  };

  const handleSelectClient = (client: SelectedClient) => {
    setSelectedClients((prev) => {
      if (prev.some((existing) => existing.id === client.id)) {
        return prev;
      }
      const next = [...prev, client];
      return next.sort((a, b) => a.name.localeCompare(b.name));
    });
    setClientQuery('');
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

    if (selectedClients.length === 0) {
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
      clientIds: selectedClients.map((client) => client.id),
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
                {clientOptionsLoading && <Loader2 className="h-4 w-4 animate-spin text-slate-400" />}
              </div>
              <div className="space-y-3 rounded-2xl border border-slate-200 bg-slate-50/60 p-3">
                <div className="flex flex-wrap gap-2">
                  {selectedClients.length === 0 ? (
                    <span className="text-sm text-slate-500">
                      {t('contractClientsEmpty') ?? 'Клиенты не выбраны.'}
                    </span>
                  ) : (
                    selectedClients.map((client) => (
                      <span
                        key={client.id}
                        className="inline-flex items-center gap-2 rounded-full bg-white px-3 py-1 text-sm text-slate-700 shadow-sm"
                      >
                        <span className="flex flex-col text-left">
                          <span className="font-medium text-slate-800">{client.name}</span>
                          {client.company && (
                            <span className="text-xs text-slate-500">{client.company}</span>
                          )}
                          {client.isActive === false && (
                            <span className="text-[11px] font-medium uppercase tracking-wide text-amber-600">
                              {t('contractClientInactive') ?? 'Неактивен'}
                            </span>
                          )}
                        </span>
                        <button
                          type="button"
                          onClick={() => removeClient(client.id)}
                          className="rounded-full p-1 text-slate-400 transition hover:bg-slate-100 hover:text-slate-600"
                          aria-label={t('remove') ?? 'Удалить'}
                        >
                          <X className="h-3.5 w-3.5" />
                        </button>
                      </span>
                    ))
                  )}
                </div>
                <div className="relative">
                  <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
                  <input
                    type="search"
                    value={clientQuery}
                    onChange={(event) => setClientQuery(event.target.value)}
                    className="w-full rounded-lg border border-slate-200 py-2 pl-10 pr-3 text-sm focus:border-emerald-500 focus:outline-none focus:ring-2 focus:ring-emerald-500/20"
                    placeholder={
                      t('contractClientsSearchPlaceholder') ?? 'Начните вводить имя или компанию клиента'
                    }
                  />
                </div>
                {clientOptionsError ? (
                  <div className="rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">
                    {clientOptionsError}
                  </div>
                ) : (
                  <div className="max-h-48 overflow-y-auto rounded-xl border border-slate-200 bg-white">
                    {availableClients.length === 0 ? (
                      <p className="px-3 py-3 text-sm text-slate-500">
                        {clientQuery
                          ? t('contractClientsNoMatches') ?? 'Не найдено клиентов по запросу'
                          : t('contractClientsStartTyping') ??
                            'Начните вводить имя или компанию, чтобы найти клиента'}
                      </p>
                    ) : (
                      <ul className="divide-y divide-slate-100">
                        {availableClients.map((client) => (
                          <li key={client.id}>
                            <button
                              type="button"
                              onClick={() => handleSelectClient(client)}
                              className="flex w-full items-start justify-between gap-3 px-3 py-2 text-left text-sm text-slate-700 hover:bg-emerald-50"
                            >
                              <span className="flex flex-col">
                                <span className="font-medium text-slate-800">{client.name}</span>
                                {client.company && (
                                  <span className="text-xs text-slate-500">{client.company}</span>
                                )}
                                {client.isActive === false && (
                                  <span className="text-[11px] font-medium uppercase tracking-wide text-amber-600">
                                    {t('contractClientInactive') ?? 'Неактивен'}
                                  </span>
                                )}
                              </span>
                              <span className="text-xs font-semibold uppercase tracking-wide text-emerald-600">
                                {t('add') ?? 'Добавить'}
                              </span>
                            </button>
                          </li>
                        ))}
                      </ul>
                    )}
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
