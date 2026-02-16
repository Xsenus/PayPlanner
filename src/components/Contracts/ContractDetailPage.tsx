import { ArrowLeft, Loader2, Plus, Save, X } from 'lucide-react';
import { useEffect, useMemo, useState } from 'react';
import { apiService } from '../../services/api';
import type { Act, Client, Contract, ContractInput, Invoice, Payment } from '../../types';

interface Props { contractId: number; onBack: () => void; }
type Tab = 'clients' | 'acts' | 'invoices' | 'payments';

export function ContractDetailPage({ contractId, onBack }: Props) {
  const [contract, setContract] = useState<Contract | null>(null);
  const [clients, setClients] = useState<Client[]>([]);
  const [acts, setActs] = useState<Act[]>([]);
  const [invoices, setInvoices] = useState<Invoice[]>([]);
  const [payments, setPayments] = useState<Payment[]>([]);
  const [activeTab, setActiveTab] = useState<Tab>('clients');
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    let cancel = false;
    const load = async () => {
      setLoading(true);
      try {
        const c = await apiService.getContract(contractId);
        if (cancel) return;
        setContract(c);
        const clientData = await apiService.getClientsV1({ sortBy: 'name', sortDir: 'asc', limit: 1000 });
        const clientIds = (c.clients ?? []).map((x) => x.id);
        const [actData, invoiceData, paymentData] = await Promise.all([
          apiService.getActs({ pageSize: 1000 }),
          apiService.getInvoices({ pageSize: 1000 }),
          apiService.getPayments(),
        ]);
        if (cancel) return;
        setClients(clientData ?? []);
        setActs((actData.items ?? []).filter((x) => x.clientId != null && clientIds.includes(x.clientId)));
        setInvoices((invoiceData.items ?? []).filter((x) => x.clientId != null && clientIds.includes(x.clientId)));
        setPayments((paymentData ?? []).filter((x) => x.clientId != null && clientIds.includes(x.clientId)));
      } finally {
        if (!cancel) setLoading(false);
      }
    };
    void load();
    return () => { cancel = true; };
  }, [contractId]);

  const selectedClientIds = useMemo(() => new Set((contract?.clients ?? []).map((x) => x.id)), [contract]);

  const save = async () => {
    if (!contract) return;
    const payload: ContractInput = {
      number: contract.number,
      date: contract.date,
      title: contract.title,
      description: contract.description,
      amount: contract.amount,
      validUntil: contract.validUntil,
      clients: (contract.clients ?? []).map((c) => ({ clientId: c.id, comment: c.comment ?? undefined })),
      actIds: contract.actIds ?? [],
      invoiceIds: contract.invoiceIds ?? [],
      paymentIds: contract.paymentIds ?? [],
    };
    setSaving(true);
    try { setContract(await apiService.updateContract(contract.id, payload)); } finally { setSaving(false); }
  };

  const toggleId = (key: 'actIds' | 'invoiceIds' | 'paymentIds', id: number) => {
    setContract((prev) => {
      if (!prev) return prev;
      const current = new Set(prev[key] ?? []);
      if (current.has(id)) {
        current.delete(id);
      } else {
        current.add(id);
      }
      return { ...prev, [key]: [...current] };
    });
  };

  if (loading || !contract) return <div className="p-8"><Loader2 className="h-6 w-6 animate-spin" /></div>;

  return (
    <div className="p-6">
      <div className="mb-4 flex items-center justify-between"><button onClick={onBack} className="inline-flex items-center gap-2 rounded border px-3 py-2"><ArrowLeft className="h-4 w-4" />Назад</button><button onClick={() => void save()} className="inline-flex items-center gap-2 rounded bg-emerald-600 px-4 py-2 text-white">{saving ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}Сохранить</button></div>
      <div className="mb-4 rounded border bg-white p-4">Договор: <b>{contract.number}</b></div>
      <div className="mb-3 flex gap-2">{(['clients','acts','invoices','payments'] as Tab[]).map((tab) => <button key={tab} onClick={() => setActiveTab(tab)} className={`rounded px-3 py-1 ${activeTab===tab?'bg-emerald-600 text-white':'border'}`}>{tab}</button>)}</div>

      {activeTab === 'clients' && (
        <div className="space-y-2 rounded border bg-white p-4">
          {(contract.clients ?? []).map((c) => (
            <div key={c.id} className="flex items-center gap-2"><span className="w-64">{c.name}</span><input value={c.comment ?? ''} onChange={(e) => setContract((prev) => prev ? ({ ...prev, clients: prev.clients.map((x) => x.id === c.id ? ({ ...x, comment: e.target.value }) : x) }) : prev)} className="rounded border px-2 py-1" placeholder="Комментарий к клиенту" /><button onClick={() => setContract((prev) => prev ? ({ ...prev, clients: prev.clients.filter((x) => x.id !== c.id) }) : prev)}><X className="h-4 w-4" /></button></div>
          ))}
          <div className="flex flex-wrap gap-2">{clients.filter((c) => !selectedClientIds.has(c.id)).slice(0, 20).map((c) => <button key={c.id} className="rounded border px-2 py-1 text-sm" onClick={() => setContract((prev) => prev ? ({ ...prev, clients: [...prev.clients, { id: c.id, name: c.name, company: c.company, comment: '' }] }) : prev)}><Plus className="mr-1 inline h-3 w-3" />{c.name}</button>)}</div>
        </div>
      )}

      {activeTab === 'acts' && <div className="rounded border bg-white p-4 space-y-1">{acts.map((a) => <label key={a.id} className="block"><input type="checkbox" checked={(contract.actIds ?? []).includes(a.id)} onChange={() => toggleId('actIds', a.id)} /> {a.number} {a.clientName}</label>)}</div>}
      {activeTab === 'invoices' && <div className="rounded border bg-white p-4 space-y-1">{invoices.map((a) => <label key={a.id} className="block"><input type="checkbox" checked={(contract.invoiceIds ?? []).includes(a.id)} onChange={() => toggleId('invoiceIds', a.id)} /> {a.number} {a.clientName}</label>)}</div>}
      {activeTab === 'payments' && <div className="rounded border bg-white p-4 space-y-1">{payments.map((a) => <label key={a.id} className="block"><input type="checkbox" checked={(contract.paymentIds ?? []).includes(a.id)} onChange={() => toggleId('paymentIds', a.id)} /> #{a.id} {a.description}</label>)}</div>}
    </div>
  );
}
