import { ArrowLeft, Loader2, Pencil, Plus, Save, Search, X } from 'lucide-react';
import { useEffect, useMemo, useState } from 'react';
import { apiService } from '../../services/api';
import type { Act, Client, Contract, ContractClient, ContractInput, Invoice, Payment } from '../../types';
import { fromInputToApiDate, toDateInputValue, toRuDate } from '../../utils/dateUtils';

interface Props {
  contractId: number;
  onBack: () => void;
  onOpenClient?: (clientId: number) => void;
}

type Tab = 'clients' | 'invoices' | 'acts' | 'money';

const tabLabels: Record<Tab, string> = {
  clients: 'Клиенты',
  invoices: 'Счета',
  acts: 'Акты',
  money: 'Деньги',
};

const formatDate = (value?: string | null) => (value ? (toRuDate(value) || value) : '—');

export function ContractDetailPage({ contractId, onBack, onOpenClient }: Props) {
  const [contract, setContract] = useState<Contract | null>(null);
  const [clientsDirectory, setClientsDirectory] = useState<Client[]>([]);
  const [acts, setActs] = useState<Act[]>([]);
  const [invoices, setInvoices] = useState<Invoice[]>([]);
  const [payments, setPayments] = useState<Payment[]>([]);
  const [activeTab, setActiveTab] = useState<Tab>('clients');
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [editMode, setEditMode] = useState(false);

  const [pickerOpen, setPickerOpen] = useState(false);
  const [pickerSearch, setPickerSearch] = useState('');

  useEffect(() => {
    let cancel = false;
    const load = async () => {
      setLoading(true);
      try {
        const c = await apiService.getContract(contractId);
        if (cancel) return;
        setContract(c);

        const [clientsData, actsData, invoicesData, paymentsData] = await Promise.all([
          apiService.getClientsV1({ sortBy: 'name', sortDir: 'asc', limit: 1000 }),
          apiService.getActs({ pageSize: 1000 }),
          apiService.getInvoices({ pageSize: 1000 }),
          apiService.getPayments(),
        ]);

        if (cancel) return;

        const contractClientIds = new Set((c.clients ?? []).map((x) => x.id));
        setClientsDirectory(clientsData ?? []);
        setActs((actsData.items ?? []).filter((x) => x.clientId != null && contractClientIds.has(x.clientId)));
        setInvoices((invoicesData.items ?? []).filter((x) => x.clientId != null && contractClientIds.has(x.clientId)));
        setPayments((paymentsData ?? []).filter((x) => x.clientId != null && contractClientIds.has(x.clientId)));
      } finally {
        if (!cancel) setLoading(false);
      }
    };

    void load();
    return () => {
      cancel = true;
    };
  }, [contractId]);

  const selectedClientIds = useMemo(() => new Set((contract?.clients ?? []).map((x) => x.id)), [contract]);

  const availableClients = useMemo(() => {
    const term = pickerSearch.trim().toLowerCase();
    return clientsDirectory
      .filter((c) => !selectedClientIds.has(c.id))
      .filter((c) => {
        if (!term) return true;
        return `${c.name} ${c.company || ''}`.toLowerCase().includes(term);
      });
  }, [clientsDirectory, selectedClientIds, pickerSearch]);

  const save = async () => {
    if (!contract) return;

    const payload: ContractInput = {
      number: contract.number,
      date: contract.date,
      startDate: contract.startDate,
      title: contract.title,
      description: contract.description,
      amount: contract.amount,
      validUntil: contract.validUntil,
      terminationDate: contract.terminationDate,
      clients: (contract.clients ?? []).map((c) => ({ clientId: c.id, comment: c.comment ?? undefined })),
      actIds: contract.actIds ?? [],
      invoiceIds: contract.invoiceIds ?? [],
      paymentIds: contract.paymentIds ?? [],
    };

    setSaving(true);
    try {
      setContract(await apiService.updateContract(contract.id, payload));
      setEditMode(false);
    } finally {
      setSaving(false);
    }
  };

  const toggleId = (key: 'actIds' | 'invoiceIds' | 'paymentIds', id: number) => {
    if (!editMode) return;
    setContract((prev) => {
      if (!prev) return prev;
      const current = new Set(prev[key] ?? []);
      if (current.has(id)) current.delete(id);
      else current.add(id);
      return { ...prev, [key]: [...current] };
    });
  };

  const addClient = (client: Client) => {
    setContract((prev) => {
      if (!prev) return prev;
      const nextClient: ContractClient = {
        id: client.id,
        name: client.name,
        company: client.company,
        clientStatus: client.clientStatus,
        clientStatusId: client.clientStatusId,
        comment: '',
      };
      return { ...prev, clients: [...prev.clients, nextClient] };
    });
  };

  if (loading || !contract) {
    return (
      <div className="p-8">
        <Loader2 className="h-6 w-6 animate-spin" />
      </div>
    );
  }

  return (
    <div className="p-6">
      <div className="mb-4 flex items-center justify-between">
        <button onClick={onBack} className="inline-flex items-center gap-2 rounded border px-3 py-2">
          <ArrowLeft className="h-4 w-4" />
          Назад
        </button>

        <div className="flex items-center gap-2">
          {!editMode ? (
            <button onClick={() => setEditMode(true)} className="inline-flex items-center gap-2 rounded border px-3 py-2">
              <Pencil className="h-4 w-4" />
              Редактировать договор
            </button>
          ) : (
            <button onClick={() => setEditMode(false)} className="rounded border px-3 py-2">
              Отменить редактирование
            </button>
          )}

          <button
            onClick={() => void save()}
            disabled={saving || !editMode}
            className="inline-flex items-center gap-2 rounded bg-emerald-600 px-4 py-2 text-white disabled:opacity-60"
          >
            {saving ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
            Сохранить
          </button>
        </div>
      </div>

      <div className="mb-4 grid grid-cols-1 gap-3 rounded border bg-white p-4 md:grid-cols-2 lg:grid-cols-4">
        <label className="text-sm">
          Номер договора
          <input
            value={contract.number}
            disabled={!editMode}
            onChange={(e) => setContract((prev) => (prev ? { ...prev, number: e.target.value } : prev))}
            className="mt-1 w-full rounded border px-2 py-1"
          />
        </label>
        <label className="text-sm">
          Дата договора
          <input
            type="date"
            value={toDateInputValue(contract.date) ?? ''}
            disabled={!editMode}
            onChange={(e) => setContract((prev) => (prev ? { ...prev, date: fromInputToApiDate(e.target.value) } : prev))}
            className="mt-1 w-full rounded border px-2 py-1"
          />
        </label>
        <label className="text-sm">
          Дата начала действия
          <input
            type="date"
            value={toDateInputValue(contract.startDate) ?? ''}
            disabled={!editMode}
            onChange={(e) => setContract((prev) => (prev ? { ...prev, startDate: fromInputToApiDate(e.target.value) } : prev))}
            className="mt-1 w-full rounded border px-2 py-1"
          />
        </label>
        <label className="text-sm">
          Действует до
          <input
            type="date"
            value={toDateInputValue(contract.validUntil) ?? ''}
            disabled={!editMode}
            onChange={(e) => setContract((prev) => (prev ? { ...prev, validUntil: fromInputToApiDate(e.target.value) } : prev))}
            className="mt-1 w-full rounded border px-2 py-1"
          />
        </label>
        <label className="text-sm">
          Дата расторжения
          <input
            type="date"
            value={toDateInputValue(contract.terminationDate) ?? ''}
            disabled={!editMode}
            onChange={(e) => setContract((prev) => (prev ? { ...prev, terminationDate: fromInputToApiDate(e.target.value) } : prev))}
            className="mt-1 w-full rounded border px-2 py-1"
          />
        </label>
        <label className="text-sm md:col-span-2 lg:col-span-3">
          Описание
          <input
            value={contract.description ?? ''}
            disabled={!editMode}
            onChange={(e) => setContract((prev) => (prev ? { ...prev, description: e.target.value } : prev))}
            className="mt-1 w-full rounded border px-2 py-1"
          />
        </label>
      </div>

      <div className="mb-3 flex gap-2">
        {(Object.keys(tabLabels) as Tab[]).map((tab) => (
          <button
            key={tab}
            onClick={() => setActiveTab(tab)}
            className={`rounded px-3 py-1 ${activeTab === tab ? 'bg-emerald-600 text-white' : 'border'}`}
          >
            {tabLabels[tab]}
          </button>
        ))}
      </div>

      {activeTab === 'clients' && (
        <div className="rounded border bg-white">
          <div className="flex items-center justify-between border-b px-4 py-3">
            <h3 className="font-semibold">Клиенты договора</h3>
            <button
              onClick={() => setPickerOpen(true)}
              disabled={!editMode}
              className="inline-flex items-center gap-2 rounded border border-emerald-300 bg-emerald-50 px-3 py-1.5 text-sm text-emerald-700 disabled:opacity-60"
            >
              <Plus className="h-4 w-4" />
              Добавить клиента
            </button>
          </div>
          <div className="overflow-x-auto">
            <table className="min-w-full divide-y text-sm">
              <thead className="bg-slate-50 text-xs uppercase text-slate-500">
                <tr>
                  <th className="px-3 py-2 text-left">ФИО</th>
                  <th className="px-3 py-2 text-left">Компания</th>
                  <th className="px-3 py-2 text-left">Комментарий</th>
                  <th className="px-3 py-2 text-right">Действия</th>
                </tr>
              </thead>
              <tbody className="divide-y">
                {(contract.clients ?? []).map((c) => (
                  <tr key={c.id}>
                    <td className="px-3 py-2">
                      <button className="text-blue-700 hover:underline" onClick={() => onOpenClient?.(c.id)}>
                        {c.name}
                      </button>
                    </td>
                    <td className="px-3 py-2">{c.company || '—'}</td>
                    <td className="px-3 py-2">
                      <input
                        disabled={!editMode}
                        value={c.comment ?? ''}
                        onChange={(e) =>
                          setContract((prev) =>
                            prev
                              ? {
                                  ...prev,
                                  clients: prev.clients.map((x) =>
                                    x.id === c.id ? { ...x, comment: e.target.value } : x,
                                  ),
                                }
                              : prev,
                          )
                        }
                        className="w-full rounded border px-2 py-1"
                      />
                    </td>
                    <td className="px-3 py-2 text-right">
                      <button
                        disabled={!editMode}
                        onClick={() =>
                          setContract((prev) =>
                            prev ? { ...prev, clients: prev.clients.filter((x) => x.id !== c.id) } : prev,
                          )
                        }
                        className="rounded border border-rose-200 p-1 text-rose-600 disabled:opacity-60"
                      >
                        <X className="h-4 w-4" />
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}

      {activeTab === 'invoices' && (
        <div className="rounded border bg-white overflow-x-auto">
          <table className="min-w-full divide-y text-sm">
            <thead className="bg-slate-50 text-xs uppercase text-slate-500">
              <tr>
                <th className="px-3 py-2 text-left">Номер счета</th>
                <th className="px-3 py-2 text-left">Клиент</th>
                <th className="px-3 py-2 text-left">Дата</th>
                <th className="px-3 py-2 text-left">Сумма</th>
                <th className="px-3 py-2 text-left">Статус</th>
                <th className="px-3 py-2 text-right">В договоре</th>
              </tr>
            </thead>
            <tbody className="divide-y">
              {invoices.map((item) => (
                <tr key={item.id}>
                  <td className="px-3 py-2">{item.number}</td>
                  <td className="px-3 py-2">{item.clientName || '—'}</td>
                  <td className="px-3 py-2">{formatDate(item.date)}</td>
                  <td className="px-3 py-2">{item.amount}</td>
                  <td className="px-3 py-2">{item.status}</td>
                  <td className="px-3 py-2 text-right">
                    <input
                      type="checkbox"
                      disabled={!editMode}
                      checked={(contract.invoiceIds ?? []).includes(item.id)}
                      onChange={() => toggleId('invoiceIds', item.id)}
                    />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {activeTab === 'acts' && (
        <div className="rounded border bg-white overflow-x-auto">
          <table className="min-w-full divide-y text-sm">
            <thead className="bg-slate-50 text-xs uppercase text-slate-500">
              <tr>
                <th className="px-3 py-2 text-left">Номер акта</th>
                <th className="px-3 py-2 text-left">Клиент</th>
                <th className="px-3 py-2 text-left">Дата</th>
                <th className="px-3 py-2 text-left">Сумма</th>
                <th className="px-3 py-2 text-left">Статус</th>
                <th className="px-3 py-2 text-right">В договоре</th>
              </tr>
            </thead>
            <tbody className="divide-y">
              {acts.map((item) => (
                <tr key={item.id}>
                  <td className="px-3 py-2">{item.number}</td>
                  <td className="px-3 py-2">{item.clientName || '—'}</td>
                  <td className="px-3 py-2">{formatDate(item.date)}</td>
                  <td className="px-3 py-2">{item.amount}</td>
                  <td className="px-3 py-2">{item.status}</td>
                  <td className="px-3 py-2 text-right">
                    <input
                      type="checkbox"
                      disabled={!editMode}
                      checked={(contract.actIds ?? []).includes(item.id)}
                      onChange={() => toggleId('actIds', item.id)}
                    />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {activeTab === 'money' && (
        <div className="rounded border bg-white overflow-x-auto">
          <table className="min-w-full divide-y text-sm">
            <thead className="bg-slate-50 text-xs uppercase text-slate-500">
              <tr>
                <th className="px-3 py-2 text-left">ID</th>
                <th className="px-3 py-2 text-left">Дата</th>
                <th className="px-3 py-2 text-left">Клиент</th>
                <th className="px-3 py-2 text-left">Описание</th>
                <th className="px-3 py-2 text-left">Сумма</th>
                <th className="px-3 py-2 text-right">В договоре</th>
              </tr>
            </thead>
            <tbody className="divide-y">
              {payments.map((item) => (
                <tr key={item.id}>
                  <td className="px-3 py-2">{item.id}</td>
                  <td className="px-3 py-2">{formatDate(item.date)}</td>
                  <td className="px-3 py-2">{item.client?.name || '—'}</td>
                  <td className="px-3 py-2">{item.description || '—'}</td>
                  <td className="px-3 py-2">{item.amount}</td>
                  <td className="px-3 py-2 text-right">
                    <input
                      type="checkbox"
                      disabled={!editMode}
                      checked={(contract.paymentIds ?? []).includes(item.id)}
                      onChange={() => toggleId('paymentIds', item.id)}
                    />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

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
                  value={pickerSearch}
                  onChange={(e) => setPickerSearch(e.target.value)}
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
                      <th className="px-3 py-2 text-right">Действие</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y">
                    {availableClients.map((client) => (
                      <tr key={client.id}>
                        <td className="px-3 py-2">{client.name}</td>
                        <td className="px-3 py-2">{client.company || '—'}</td>
                        <td className="px-3 py-2 text-right">
                          <button
                            className="rounded border border-emerald-200 bg-emerald-50 px-2 py-1 text-xs text-emerald-700"
                            onClick={() => addClient(client)}
                          >
                            Добавить
                          </button>
                        </td>
                      </tr>
                    ))}
                    {availableClients.length === 0 && (
                      <tr>
                        <td colSpan={3} className="px-3 py-6 text-center text-slate-500">
                          Нет доступных клиентов
                        </td>
                      </tr>
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
