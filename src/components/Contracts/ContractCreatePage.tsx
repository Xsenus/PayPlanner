import { ArrowLeft, Loader2, Plus, Save, Search } from 'lucide-react';
import { useEffect, useMemo, useState } from 'react';
import { useTranslation } from '../../hooks/useTranslation';
import { useDebouncedValue } from '../../hooks/useDebouncedValue';
import { apiService } from '../../services/api';
import type { ContractClient, ContractInput } from '../../types';
import { fromInputToApiDate, toDateInputValue } from '../../utils/dateUtils';
import { ClientStatusBadge } from '../Clients/ClientStatusBadge';

interface ContractCreatePageProps {
  onBack: () => void;
  onCreated: () => void;
}

type FormState = {
  number: string;
  title: string;
  date: string;
  amount: string;
  description: string;
  validUntil: string;
};

const createInitialForm = (): FormState => ({
  number: '',
  title: '',
  date: toDateInputValue(new Date()),
  amount: '',
  description: '',
  validUntil: '',
});

export function ContractCreatePage({ onBack, onCreated }: ContractCreatePageProps) {
  const { t } = useTranslation();
  const [form, setForm] = useState<FormState>(createInitialForm);
  const [localError, setLocalError] = useState<string | null>(null);
  const [selectedClients, setSelectedClients] = useState<ContractClient[]>([]);
  const [clientSearch, setClientSearch] = useState('');
  const debouncedClientSearch = useDebouncedValue(clientSearch.trim(), 350);
  const [clientOptions, setClientOptions] = useState<ContractClient[]>([]);
  const [clientOptionsLoading, setClientOptionsLoading] = useState(false);
  const [clientOptionsError, setClientOptionsError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

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
      if (Number.isNaN(parsed) || parsed < 0) {
        setLocalError(t('contractAmountInvalid') ?? 'Введите корректную сумму');
        return;
      }
      amountValue = parsed;
    }

    const payload: ContractInput = {
      number: form.number.trim(),
      title: form.title.trim() ? form.title.trim() : undefined,
      date: fromInputToApiDate(form.date),
      amount: amountValue,
      description: form.description.trim() ? form.description.trim() : undefined,
      validUntil: form.validUntil ? fromInputToApiDate(form.validUntil) : undefined,
      clientIds: selectedClients.map((client) => client.id),
    };

    setSubmitting(true);
    try {
      await apiService.createContract(payload);
      onCreated();
    } catch (error) {
      setLocalError(error instanceof Error ? error.message : (t('failedToSave') ?? 'Не удалось сохранить'));
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <div className="min-h-screen bg-gray-50 pb-12">
      <div className="mx-auto w-full max-w-[calc(100vw-2rem)] px-4 pt-8 sm:px-6 lg:px-8">
        <div className="mb-6 flex items-center justify-between gap-3">
          <div>
            <h1 className="text-2xl font-bold text-slate-900">{t('contractCreateTitle') ?? 'Новый договор'}</h1>
            <p className="text-sm text-slate-600">
              {t('contractModalSubtitle') ?? 'Укажите ключевую информацию и выберите клиентов.'}
            </p>
          </div>
          <button
            type="button"
            onClick={onBack}
            className="inline-flex items-center gap-2 rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 shadow-sm hover:bg-slate-50"
          >
            <ArrowLeft className="h-4 w-4" />
            <span>{t('back') ?? 'Назад'}</span>
          </button>
        </div>

        <form onSubmit={handleSubmit} className="rounded-2xl border border-slate-200 bg-white p-6 shadow-sm">
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">{t('contractNumber') ?? 'Номер договора'}</span>
              <input type="text" value={form.number} onChange={(event) => setForm((prev) => ({ ...prev, number: event.target.value }))} className="rounded-lg border border-slate-200 px-3 py-2 text-sm" required />
            </label>
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">{t('contractDate') ?? 'Дата договора'}</span>
              <input type="date" value={form.date} onChange={(event) => setForm((prev) => ({ ...prev, date: event.target.value }))} className="rounded-lg border border-slate-200 px-3 py-2 text-sm" required />
            </label>
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">{t('contractTitle') ?? 'Название'}</span>
              <input type="text" value={form.title} onChange={(event) => setForm((prev) => ({ ...prev, title: event.target.value }))} className="rounded-lg border border-slate-200 px-3 py-2 text-sm" />
            </label>
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">{t('contractAmount') ?? 'Сумма договора'}</span>
              <input type="text" value={form.amount} onChange={(event) => setForm((prev) => ({ ...prev, amount: event.target.value }))} className="rounded-lg border border-slate-200 px-3 py-2 text-sm" placeholder="0,00" />
            </label>
            <label className="flex flex-col gap-1 text-sm sm:col-span-2">
              <span className="font-medium text-slate-700">{t('contractDescription') ?? 'Описание'}</span>
              <textarea value={form.description} onChange={(event) => setForm((prev) => ({ ...prev, description: event.target.value }))} className="min-h-[96px] rounded-lg border border-slate-200 px-3 py-2 text-sm" />
            </label>
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">{t('contractValidUntil') ?? 'Действует до'}</span>
              <input type="date" value={form.validUntil} onChange={(event) => setForm((prev) => ({ ...prev, validUntil: event.target.value }))} className="rounded-lg border border-slate-200 px-3 py-2 text-sm" />
            </label>

            <div className="flex flex-col gap-2 text-sm sm:col-span-2">
              <span className="font-medium text-slate-700">{t('contractClients') ?? 'Клиенты'}</span>
              <div className="relative">
                <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
                <input type="search" value={clientSearch} onChange={(event) => setClientSearch(event.target.value)} placeholder={t('contractClientsSearchPlaceholder') ?? 'Введите имя или компанию клиента для поиска'} className="w-full rounded-lg border border-slate-200 py-2 pl-10 pr-3 text-sm" />
              </div>

              {selectedClients.length > 0 && (
                <div className="flex flex-wrap gap-2">
                  {selectedClients.map((client) => (
                    <button key={client.id} type="button" onClick={() => handleRemoveClient(client.id)} className="inline-flex items-center gap-2 rounded-full border border-emerald-200 bg-emerald-50 px-3 py-1 text-xs font-medium text-emerald-700" title={t('delete') ?? 'Удалить'}>
                      <span>{client.name}{client.company ? ` · ${client.company}` : ''}</span>
                      <ClientStatusBadge status={client.clientStatus} />
                    </button>
                  ))}
                </div>
              )}

              <div className="max-h-56 overflow-y-auto rounded-lg border border-slate-200">
                {clientOptionsLoading ? (
                  <div className="flex items-center justify-center gap-2 px-3 py-6 text-sm text-slate-500">
                    <Loader2 className="h-4 w-4 animate-spin" />
                    <span>{t('loading') ?? 'Загрузка...'}</span>
                  </div>
                ) : availableClientOptions.length === 0 ? (
                  <div className="px-3 py-6 text-center text-sm text-slate-500">
                    {t('contractClientsSearchEmpty') ?? 'По данному запросу ничего не найдено.'}
                  </div>
                ) : (
                  <div className="divide-y divide-slate-100">
                    {availableClientOptions.map((client) => (
                      <button key={client.id} type="button" onClick={() => handleAddClient(client)} className="flex w-full items-center justify-between gap-3 px-3 py-2 text-left text-sm hover:bg-slate-50">
                        <span>
                          <span className="font-medium text-slate-800">{client.name}</span>
                          {client.company ? <span className="text-slate-500"> · {client.company}</span> : null}
                        </span>
                        <span className="inline-flex items-center gap-2">
                          <ClientStatusBadge status={client.clientStatus} />
                          <Plus className="h-4 w-4 text-emerald-600" />
                        </span>
                      </button>
                    ))}
                  </div>
                )}
              </div>
              {clientOptionsError && <div className="text-xs text-rose-600">{clientOptionsError}</div>}
            </div>
          </div>

          {localError && <div className="mt-4 rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">{localError}</div>}

          <div className="mt-6 flex items-center justify-end gap-3">
            <button type="button" onClick={onBack} className="rounded-lg border border-slate-200 px-4 py-2 text-sm font-medium text-slate-700">
              {t('cancel') ?? 'Отмена'}
            </button>
            <button type="submit" disabled={submitting} className="inline-flex items-center gap-2 rounded-lg bg-emerald-600 px-4 py-2 text-sm font-semibold text-white disabled:opacity-70">
              {submitting ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
              <span>{t('save') ?? 'Сохранить'}</span>
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}
