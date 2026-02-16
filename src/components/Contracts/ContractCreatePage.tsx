import { ArrowLeft, Loader2, Plus, Save, Search, X } from 'lucide-react';
import { useEffect, useMemo, useState } from 'react';
import { useTranslation } from '../../hooks/useTranslation';
import { useDebouncedValue } from '../../hooks/useDebouncedValue';
import { apiService } from '../../services/api';
import type { ContractClient, ContractInput } from '../../types';
import { fromInputToApiDate, toDateInputValue } from '../../utils/dateUtils';

interface ContractCreatePageProps {
  onBack: () => void;
  onCreated: () => void;
}

type FormState = {
  number: string;
  title: string;
  date: string;
  startDate: string;
  amount: string;
  description: string;
  validUntil: string;
  terminationDate: string;
};

const createInitialForm = (): FormState => {
  const today = toDateInputValue(new Date());
  return {
    number: '',
    title: '',
    date: today,
    startDate: today,
    amount: '',
    description: '',
    validUntil: '',
    terminationDate: '',
  };
};

export function ContractCreatePage({ onBack, onCreated }: ContractCreatePageProps) {
  const { t } = useTranslation();
  const [form, setForm] = useState<FormState>(createInitialForm);
  const [localError, setLocalError] = useState<string | null>(null);
  const [selectedClients, setSelectedClients] = useState<ContractClient[]>([]);
  const [clientSearch, setClientSearch] = useState('');
  const [pickerOpen, setPickerOpen] = useState(false);
  const debouncedClientSearch = useDebouncedValue(clientSearch.trim(), 250);
  const [clientOptions, setClientOptions] = useState<ContractClient[]>([]);
  const [clientOptionsLoading, setClientOptionsLoading] = useState(false);
  const [submitting, setSubmitting] = useState(false);

  useEffect(() => {
    if (!pickerOpen) return;
    let cancelled = false;
    const fetchClients = async () => {
      setClientOptionsLoading(true);
      try {
        const response = await apiService.getClientsV1({
          search: debouncedClientSearch || undefined,
          sortBy: 'name',
          sortDir: 'asc',
          limit: 50,
        });
        if (cancelled) return;
        setClientOptions(
          (response ?? []).map((client) => ({
            id: client.id,
            name: client.name,
            company: client.company,
            clientStatusId: client.clientStatusId ?? null,
            clientStatus: client.clientStatus ?? null,
            comment: '',
          })),
        );
      } finally {
        if (!cancelled) setClientOptionsLoading(false);
      }
    };

    void fetchClients();
    return () => {
      cancelled = true;
    };
  }, [pickerOpen, debouncedClientSearch]);

  const selectedClientIds = useMemo(() => new Set(selectedClients.map((x) => x.id)), [selectedClients]);
  const availableClientOptions = useMemo(
    () => clientOptions.filter((x) => !selectedClientIds.has(x.id)),
    [clientOptions, selectedClientIds],
  );

  const handleSubmit = async (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    setLocalError(null);

    if (!form.number.trim()) {
      setLocalError('Номер договора обязателен');
      return;
    }
    if (!form.date) {
      setLocalError('Дата договора обязательна');
      return;
    }
    if (selectedClients.length === 0) {
      setLocalError('Выберите хотя бы одного клиента');
      return;
    }

    let amountValue: number | undefined;
    if (form.amount.trim()) {
      const parsed = Number(form.amount.replace(/\s+/g, '').replace(',', '.'));
      if (Number.isNaN(parsed) || parsed < 0) {
        setLocalError('Введите корректную сумму');
        return;
      }
      amountValue = parsed;
    }

    const payload: ContractInput = {
      number: form.number.trim(),
      title: form.title.trim() || undefined,
      date: fromInputToApiDate(form.date),
      startDate: form.startDate ? fromInputToApiDate(form.startDate) : undefined,
      amount: amountValue,
      description: form.description.trim() || undefined,
      validUntil: form.validUntil ? fromInputToApiDate(form.validUntil) : undefined,
      terminationDate: form.terminationDate ? fromInputToApiDate(form.terminationDate) : undefined,
      clients: selectedClients.map((client) => ({ clientId: client.id, comment: client.comment ?? undefined })),
      actIds: [],
      invoiceIds: [],
      paymentIds: [],
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
            <h1 className="text-2xl font-bold text-slate-900">Новый договор</h1>
            <p className="text-sm text-slate-600">Укажите ключевую информацию и добавьте клиентов.</p>
          </div>
          <button
            type="button"
            onClick={onBack}
            className="inline-flex items-center gap-2 rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 shadow-sm hover:bg-slate-50"
          >
            <ArrowLeft className="h-4 w-4" />
            Назад
          </button>
        </div>

        <form onSubmit={handleSubmit} className="rounded-2xl border border-slate-200 bg-white p-5 shadow-sm">
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">Номер договора</span>
              <input type="text" value={form.number} onChange={(e) => setForm((prev) => ({ ...prev, number: e.target.value }))} className="rounded-lg border border-slate-200 px-3 py-2 text-sm" required />
            </label>
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">Дата договора</span>
              <input type="date" value={form.date} onChange={(e) => setForm((prev) => ({ ...prev, date: e.target.value }))} className="rounded-lg border border-slate-200 px-3 py-2 text-sm" required />
            </label>
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">Дата начала действия</span>
              <input type="date" value={form.startDate} onChange={(e) => setForm((prev) => ({ ...prev, startDate: e.target.value }))} className="rounded-lg border border-slate-200 px-3 py-2 text-sm" />
            </label>
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">Действует до</span>
              <input type="date" value={form.validUntil} onChange={(e) => setForm((prev) => ({ ...prev, validUntil: e.target.value }))} className="rounded-lg border border-slate-200 px-3 py-2 text-sm" />
            </label>
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">Дата расторжения</span>
              <input type="date" value={form.terminationDate} onChange={(e) => setForm((prev) => ({ ...prev, terminationDate: e.target.value }))} className="rounded-lg border border-slate-200 px-3 py-2 text-sm" />
            </label>
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">Сумма договора</span>
              <input type="text" value={form.amount} onChange={(e) => setForm((prev) => ({ ...prev, amount: e.target.value }))} className="rounded-lg border border-slate-200 px-3 py-2 text-sm" placeholder="0,00" />
            </label>
            <label className="flex flex-col gap-1 text-sm sm:col-span-2">
              <span className="font-medium text-slate-700">Название договора</span>
              <input type="text" value={form.title} onChange={(e) => setForm((prev) => ({ ...prev, title: e.target.value }))} className="rounded-lg border border-slate-200 px-3 py-2 text-sm" />
            </label>
            <label className="flex flex-col gap-1 text-sm sm:col-span-2">
              <span className="font-medium text-slate-700">Описание</span>
              <textarea value={form.description} onChange={(e) => setForm((prev) => ({ ...prev, description: e.target.value }))} className="min-h-[96px] rounded-lg border border-slate-200 px-3 py-2 text-sm" />
            </label>
          </div>

          <div className="mt-5 rounded-xl border border-slate-200">
            <div className="flex items-center justify-between border-b px-4 py-3">
              <h3 className="font-semibold text-slate-900">Клиенты договора</h3>
              <button
                type="button"
                onClick={() => setPickerOpen(true)}
                className="inline-flex items-center gap-2 rounded-lg border border-emerald-300 bg-emerald-50 px-3 py-1.5 text-sm text-emerald-700"
              >
                <Plus className="h-4 w-4" />
                Добавить клиента
              </button>
            </div>
            <div className="overflow-x-auto">
              <table className="min-w-full divide-y divide-slate-200 text-sm">
                <thead className="bg-slate-50 text-xs uppercase text-slate-500">
                  <tr>
                    <th className="px-3 py-2 text-left">ФИО</th>
                    <th className="px-3 py-2 text-left">Компания</th>
                    <th className="px-3 py-2 text-left">Комментарий</th>
                    <th className="px-3 py-2 text-right">Действия</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-100">
                  {selectedClients.length === 0 ? (
                    <tr>
                      <td colSpan={4} className="px-3 py-6 text-center text-slate-500">
                        Клиенты не добавлены
                      </td>
                    </tr>
                  ) : (
                    selectedClients.map((client) => (
                      <tr key={client.id}>
                        <td className="px-3 py-2">{client.name}</td>
                        <td className="px-3 py-2">{client.company || '—'}</td>
                        <td className="px-3 py-2">
                          <input
                            value={client.comment ?? ''}
                            onChange={(e) =>
                              setSelectedClients((prev) =>
                                prev.map((x) => (x.id === client.id ? { ...x, comment: e.target.value } : x)),
                              )
                            }
                            className="w-full rounded border border-slate-200 px-2 py-1"
                            placeholder="Комментарий к клиенту"
                          />
                        </td>
                        <td className="px-3 py-2 text-right">
                          <button
                            type="button"
                            onClick={() => setSelectedClients((prev) => prev.filter((x) => x.id !== client.id))}
                            className="rounded border border-rose-200 p-1 text-rose-600"
                          >
                            <X className="h-4 w-4" />
                          </button>
                        </td>
                      </tr>
                    ))
                  )}
                </tbody>
              </table>
            </div>
          </div>

          {localError && <div className="mt-4 rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">{localError}</div>}

          <div className="mt-6 flex items-center justify-end gap-3">
            <button type="button" onClick={onBack} className="rounded-lg border border-slate-200 px-4 py-2 text-sm font-medium text-slate-700">
              Отмена
            </button>
            <button type="submit" disabled={submitting} className="inline-flex items-center gap-2 rounded-lg bg-emerald-600 px-4 py-2 text-sm font-semibold text-white disabled:opacity-70">
              {submitting ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
              <span>Сохранить</span>
            </button>
          </div>
        </form>
      </div>

      {pickerOpen && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4">
          <div className="w-full max-w-3xl rounded-xl bg-white shadow-2xl">
            <div className="flex items-center justify-between border-b px-4 py-3">
              <h3 className="text-lg font-semibold">Выбор клиента</h3>
              <button className="rounded border px-2 py-1 text-sm" onClick={() => setPickerOpen(false)}>
                Закрыть
              </button>
            </div>
            <div className="p-4">
              <div className="relative mb-3">
                <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
                <input
                  type="search"
                  value={clientSearch}
                  onChange={(e) => setClientSearch(e.target.value)}
                  className="w-full rounded-lg border border-slate-200 py-2 pl-10 pr-3"
                  placeholder="Поиск клиента"
                />
              </div>

              <div className="max-h-80 overflow-auto rounded border">
                <table className="min-w-full divide-y text-sm">
                  <thead className="bg-slate-50 text-xs uppercase text-slate-500">
                    <tr>
                      <th className="px-3 py-2 text-left">ФИО</th>
                      <th className="px-3 py-2 text-left">Компания</th>
                      <th className="px-3 py-2 text-right">Действия</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y">
                    {clientOptionsLoading ? (
                      <tr>
                        <td colSpan={3} className="px-3 py-8 text-center text-slate-500">
                          Загрузка...
                        </td>
                      </tr>
                    ) : availableClientOptions.length === 0 ? (
                      <tr>
                        <td colSpan={3} className="px-3 py-8 text-center text-slate-500">
                          Клиенты не найдены
                        </td>
                      </tr>
                    ) : (
                      availableClientOptions.map((client) => (
                        <tr key={client.id}>
                          <td className="px-3 py-2">{client.name}</td>
                          <td className="px-3 py-2">{client.company || '—'}</td>
                          <td className="px-3 py-2 text-right">
                            <button
                              type="button"
                              onClick={() => setSelectedClients((prev) => [...prev, client])}
                              className="rounded border border-emerald-200 bg-emerald-50 px-2 py-1 text-xs text-emerald-700"
                            >
                              Добавить
                            </button>
                          </td>
                        </tr>
                      ))
                    )}
                  </tbody>
                </table>
              </div>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
