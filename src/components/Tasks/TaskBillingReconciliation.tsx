import { useEffect, useRef, useState } from 'react';
import { apiService } from '../../services/api';
import type { Invoice } from '../../types';
import type { TaskJournalItem } from '../../utils/taskJournalStore';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { useDialog } from '../../hooks/useDialog';
import { useModalFocusTrap } from '../../hooks/useModalFocusTrap';
import { SystemSelect } from '../ui/SystemSelect';
import { useTaskBulkBilling } from './useTaskBulkBilling';

const taskPageSize = 100;
const invoicePageSize = 25;
const dateFormatter = new Intl.DateTimeFormat('ru-RU');
const amountFormatter = new Intl.NumberFormat('ru-RU', { style: 'currency', currency: 'RUB' });

type Props = {
  anchor: TaskJournalItem;
  initialIds: number[];
  identityKey: string;
  canLink: boolean;
  canCreate: boolean;
  canEdit: boolean;
  canEditTask: (task: TaskJournalItem) => boolean;
  onChanged: (ids: number[]) => void;
  onCreate: (tasks: TaskJournalItem[]) => void;
  onClose: () => void;
};

const isActiveIncomeInvoice = (invoice: Invoice) =>
  invoice.type === 'Income' && invoice.status !== 'Cancelled'
  && invoice.workflowStatus !== 'Cancelled' && !invoice.auditIsDeleted;

export function TaskBillingReconciliation({ anchor, initialIds, identityKey, canLink, canCreate, canEdit, canEditTask, onChanged, onCreate, onClose }: Props) {
  const { confirm } = useDialog();
  const root = useRef<HTMLDivElement>(null);
  const active = useRef(false);
  const saving = useRef(false);
  const [selectedIds, setSelectedIds] = useState(() => new Set(initialIds));
  const [taskPage, setTaskPage] = useState(1);
  const [invoicePage, setInvoicePage] = useState(1);
  const [invoiceId, setInvoiceId] = useState<number | null>(null);
  const [reload, setReload] = useState(0);
  const [search, setSearch] = useState('');
  const [invoiceSearch, setInvoiceSearch] = useState('');
  const [tasks, setTasks] = useState<{ items: TaskJournalItem[]; total: number } | null>(null);
  const [invoices, setInvoices] = useState<{ items: Invoice[]; total: number } | null>(null);
  const [invoice, setInvoice] = useState<Invoice | null>(null);
  const [taskError, setTaskError] = useState('');
  const [invoiceError, setInvoiceError] = useState('');
  const [detailError, setDetailError] = useState('');
  const [result, setResult] = useState('');
  const [writeError, setWriteError] = useState('');
  const [busy, setBusy] = useState(false);

  const { clientId, legalEntityId } = anchor;
  const hasOwner = (item: { clientId?: number | null; legalEntityId?: number | null }) =>
    (item.clientId ?? null) === (clientId ?? null) && (item.legalEntityId ?? null) === (legalEntityId ?? null);
  const selected = tasks?.items.filter(task => selectedIds.has(task.id)) ?? [];
  const allEditable = selected.length > 0 && selected.every(canEditTask);
  const hasCases = selected.length > 0 && selected.every(task => Boolean(task.clientCaseId));
  const invoiceCases = new Set([invoice?.clientCaseId, ...(invoice?.clientCaseIds ?? []), ...(invoice?.clientCases ?? []).map(item => item.id)]
    .filter((id): id is number => typeof id === 'number' && id > 0));
  const matchesInvoice = hasCases && (!invoiceCases.size || selected.every(task => invoiceCases.has(task.clientCaseId!)));
  const pageGroups = new Map<number | null, TaskJournalItem[]>();
  for (const task of tasks?.items ?? []) {
    const key = task.clientCaseId ?? null;
    const group = pageGroups.get(key);
    if (group) group.push(task); else pageGroups.set(key, [task]);
  }
  const reloadRows = (ids: number[]) => {
    const changed = new Set(ids);
    setSelectedIds(previous => new Set([...previous].filter(id => !changed.has(id))));
    setTaskPage(1);
    setTasks(null);
    setInvoice(null);
    setInvoices(null);
    setReload(value => value + 1);
    onChanged(ids);
  };
  const bulk = useTaskBulkBilling({
    tasks: selected,
    scopeKey: identityKey,
    scopeLabel: `Сверка по клиенту, ${anchor.objectName}`,
    identityKey,
    canEdit,
    loading: busy || tasks === null,
    resetOnClose: true,
    onApplied: (response) => {
      const ids = response.items.filter(item => item.outcome === 'updated' || item.outcome === 'unchanged').map(item => item.id);
      if (ids.length) reloadRows(ids);
    },
  });
  const close = () => { if (!saving.current && !bulk.busy) onClose(); };
  const keyDown = useModalFocusTrap(root, !bulk.dialog, close);

  useEffect(() => { active.current = true; return () => { active.current = false; }; }, []);
  useEffect(() => {
    const controller = new AbortController();
    setTaskError('');
    void apiService.getTaskJournalItems({ clientId: clientId ?? undefined, legalEntityId: legalEntityId ?? undefined,
      billingStatus: 'unbilled', page: taskPage, pageSize: taskPageSize }, { signal: controller.signal })
      .then(page => {
        if (controller.signal.aborted) return;
        const items = page.items.filter(task => (task.clientId ?? null) === (clientId ?? null)
          && (task.legalEntityId ?? null) === (legalEntityId ?? null) && task.billingStatus === 'unbilled');
        setTasks({ items, total: page.total });
      })
      .catch(error => { if (!controller.signal.aborted) setTaskError(extractDisplayErrorMessage(error, 'Не удалось загрузить задачи.')); });
    return () => controller.abort();
  }, [clientId, legalEntityId, taskPage, reload]);

  useEffect(() => {
    const controller = new AbortController();
    setInvoiceError('');
    void apiService.getInvoices({ type: 'Income', clientRole: 'counterparty', clientId: clientId ?? undefined,
      legalEntityId: legalEntityId ?? undefined, page: invoicePage, pageSize: invoicePageSize,
      search: invoiceSearch || undefined }, { signal: controller.signal })
      .then(page => {
        if (controller.signal.aborted) return;
        const items = page.items.filter(item => isActiveIncomeInvoice(item)
          && (item.clientId ?? null) === (clientId ?? null) && (item.legalEntityId ?? null) === (legalEntityId ?? null));
        setInvoices({ items, total: page.total });
      })
      .catch(error => { if (!controller.signal.aborted) setInvoiceError(extractDisplayErrorMessage(error, 'Не удалось загрузить счета.')); });
    return () => controller.abort();
  }, [clientId, legalEntityId, invoicePage, invoiceSearch, reload]);

  useEffect(() => {
    if (invoiceId === null) return;
    const controller = new AbortController();
    setDetailError('');
    void apiService.getInvoice(invoiceId, { signal: controller.signal }).then(item => {
      if (controller.signal.aborted) return;
      if (item.id !== invoiceId || !isActiveIncomeInvoice(item) || (item.clientId ?? null) !== (clientId ?? null)
          || (item.legalEntityId ?? null) !== (legalEntityId ?? null)) {
        setDetailError('Счёт изменился или недоступен. Выберите счёт заново.');
        return;
      }
      setInvoice(item);
    }).catch(error => { if (!controller.signal.aborted) setDetailError(extractDisplayErrorMessage(error, 'Не удалось загрузить состав счёта.')); });
    return () => controller.abort();
  }, [clientId, legalEntityId, invoiceId, reload]);

  const setTaskSelection = (ids: number[], checked: boolean) => setSelectedIds(previous => {
    const next = new Set(previous);
    for (const id of ids) {
      if (checked) next.add(id); else next.delete(id);
    }
    return next;
  });
  const changeTaskPage = (page: number) => {
    setSelectedIds(new Set());
    setTasks(null);
    setTaskPage(page);
  };
  const changeInvoicePage = (page: number) => {
    setInvoices(null); setInvoice(null); setInvoiceId(null); setInvoicePage(page);
  };
  const refresh = () => {
    setWriteError(''); setResult(''); setInvoice(null); setInvoices(null); setTasks(null);
    setReload(value => value + 1);
  };
  const linkSelected = async () => {
    if (saving.current || !canLink || !allEditable || !matchesInvoice || !invoice || invoice.id !== invoiceId || !hasOwner(invoice) || writeError) return;
    saving.current = true;
    setBusy(true);
    setResult('');
    try {
      const accepted = await confirm({
        title: `Привязать выбранные задачи к счёту №${invoice.number}?`,
        description: `Подтвердите, что все выбранные работы (${selected.length}) уже учтены в этом счёте.`,
        confirmLabel: 'Привязать к счёту',
      });
      if (!accepted || !active.current) return;
      const versions = Object.fromEntries(selected.map(task => [task.id, task.updatedAt ?? null]));
      const linked = await apiService.bulkLinkTaskBillingInvoice(invoice.id, invoice.billingReconciliationUpdatedAt ?? null, versions);
      if (!active.current) return;
      const ids = new Set(selected.map(task => task.id));
      if (linked.length !== ids.size || !linked.every(task => ids.delete(task.id) && task.billingInvoiceId === invoice.id)) {
        throw new Error('Результат привязки не подтверждён. Обновите данные перед повтором.');
      }
      setResult(`К счёту №${invoice.number} привязано задач: ${linked.length}.`);
      reloadRows(linked.map(task => task.id));
    } catch (error) {
      if (active.current) setWriteError(extractDisplayErrorMessage(error, 'Не удалось подтвердить привязку. Обновите данные.'));
    } finally {
      saving.current = false;
      if (active.current) setBusy(false);
    }
  };

  return <>
    <div hidden={Boolean(bulk.dialog)} className="fixed inset-0 z-[2200] items-center justify-center bg-slate-950/50 p-3" style={bulk.dialog ? undefined : { display: 'flex' }}>
      <div ref={root} role="dialog" aria-modal="true" aria-label="Сверка по клиенту" tabIndex={-1} onKeyDown={keyDown}
        className="max-h-[calc(100dvh-1.5rem)] w-full max-w-6xl space-y-3 overflow-y-auto rounded-xl bg-white p-3 text-slate-900 shadow-xl">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <div><h2 className="text-lg font-bold">Сверка по клиенту</h2><p className="text-sm">{anchor.clientName || anchor.legalEntityName || anchor.objectName}</p></div>
          <div className="flex gap-2">
            <button type="button" className="rounded border px-3 py-1.5 text-sm" disabled={busy} onClick={refresh}>Обновить</button>
            <button type="button" className="rounded border px-3 py-1.5 text-sm" disabled={busy} onClick={close}>Закрыть</button>
          </div>
        </div>
        <div className="grid gap-3 lg:grid-cols-2">
          <section className="space-y-2 rounded-lg border p-3">
            <h3 className="font-semibold">Платные задачи без счёта{tasks ? ` (${tasks.total})` : ''}</h3>
            {taskError ? <p role="alert" className="text-sm text-rose-700">{taskError}</p> : null}
            {!tasks && !taskError ? <p role="status" className="text-sm">Загружаем задачи…</p> : null}
            {tasks?.items.length === 0 ? <p className="text-sm">На этой странице задач без счёта нет.</p> : null}
            {pageGroups.size ? <div className="max-h-80 space-y-2 overflow-y-auto">
              {[...pageGroups].map(([caseId, group]) => {
                const groupName = group[0].clientCaseTitle || (caseId ? `Дело №${caseId}` : 'Без дела');
                const allSelected = group.every(task => selectedIds.has(task.id));
                return <div key={caseId ?? 'no-case'}>
                  <div className="flex flex-wrap items-center justify-between gap-1 rounded bg-slate-50 p-2 text-xs">
                    <span className="font-semibold">{groupName}</span>
                    {group.length > 1 ? <button type="button" className="underline" aria-label={`${allSelected ? 'Снять отметки' : 'Выбрать все задачи'}: ${groupName} на странице`}
                      disabled={busy} onClick={() => setTaskSelection(group.map(task => task.id), !allSelected)}>
                      {allSelected ? 'Снять отметки' : 'Выбрать все'}
                    </button> : null}
                  </div>
                  <ul className="divide-y">
                    {group.map(task => <li key={task.id}>
                      <label className="flex items-start gap-2 py-2 text-sm">
                        <input type="checkbox" checked={selectedIds.has(task.id)} disabled={busy} onChange={event => setTaskSelection([task.id], event.target.checked)} />
                        <span><span className="block font-medium">{task.title}</span>
                          <span className="block text-xs text-slate-600">№{task.id} от {dateFormatter.format(new Date(task.createdAt))}{task.billingContractNumber ? ` · Договор №${task.billingContractNumber}` : ''}</span>
                        </span>
                      </label>
                      {task.description || task.result ? <details className="mb-2 ml-5 text-xs text-slate-700">
                        <summary className="cursor-pointer">Описание и результат</summary>
                        {task.description ? <p className="mt-1 whitespace-pre-wrap">{task.description}</p> : null}
                        {task.result ? <p className="mt-1 whitespace-pre-wrap">Результат: {task.result}</p> : null}
                      </details> : null}
                    </li>)}
                  </ul>
                </div>;
              })}
            </div> : null}
            {tasks && (taskPage > 1 || tasks.total > taskPageSize) ? <div className="flex items-center gap-2 text-sm">
              <button type="button" className="rounded border px-2 py-1" disabled={busy || taskPage === 1} onClick={() => changeTaskPage(taskPage - 1)}>Предыдущие задачи</button>
              <span>{taskPage}</span>
              <button type="button" className="rounded border px-2 py-1" disabled={busy || taskPage * taskPageSize >= tasks.total} onClick={() => changeTaskPage(taskPage + 1)}>Следующие задачи</button>
            </div> : null}
          </section>
          <section className="space-y-2 rounded-lg border p-3">
            <h3 className="font-semibold">Существующие счета</h3>
            <form className="flex gap-2" onSubmit={event => {
              event.preventDefault();
              if (search.trim() === invoiceSearch && invoicePage === 1) return;
              changeInvoicePage(1);
              setInvoiceSearch(search.trim());
            }}>
              <input aria-label="Поиск по номеру счёта" className="form-input min-w-0 flex-1" placeholder="Номер счёта" value={search} disabled={busy} onChange={event => setSearch(event.target.value)} />
              <button type="submit" className="rounded border px-2 text-sm" disabled={busy}>Найти</button>
            </form>
            {invoiceError ? <p role="alert" className="text-sm text-rose-700">{invoiceError}</p> : null}
            <SystemSelect aria-label="Счёт для сверки" className="form-input w-full" value={invoiceId ?? ''} disabled={busy || !invoices?.items.length}
              onChange={event => { setInvoice(null); setInvoiceId(event.target.value ? Number(event.target.value) : null); }}>
              <option value="">{!invoices && !invoiceError ? 'Загружаем счета…' : invoices?.items.length ? 'Выберите счёт' : 'На этой странице счетов нет'}</option>
              {invoices?.items.map(item => <option key={item.id} value={item.id}>№{item.number} от {dateFormatter.format(new Date(item.date))} · {amountFormatter.format(item.amount)}</option>)}
            </SystemSelect>
            {invoices && (invoicePage > 1 || invoices.total > invoicePageSize) ? <div className="flex items-center gap-2 text-sm">
              <button type="button" className="rounded border px-2 py-1" disabled={busy || invoicePage === 1} onClick={() => changeInvoicePage(invoicePage - 1)}>Предыдущие счета</button>
              <span>{invoicePage}</span>
              <button type="button" className="rounded border px-2 py-1" disabled={busy || invoicePage * invoicePageSize >= invoices.total} onClick={() => changeInvoicePage(invoicePage + 1)}>Следующие счета</button>
            </div> : null}
            {invoiceId && detailError ? <p role="alert" className="text-sm text-rose-700">{detailError}</p> : null}
            {invoiceId && !invoice && !detailError ? <p role="status" className="text-sm">Загружаем состав счёта…</p> : null}
            {invoice ? <div className="max-h-64 space-y-2 overflow-y-auto rounded bg-slate-50 p-2 text-sm">
              <p className="font-medium">Состав счёта №{invoice.number}</p>
              {invoice.clientCases?.length ? <p className="text-xs text-slate-600">{invoice.clientCases.map(item => item.title).join(', ')}</p> : null}
              {invoice.lineItems?.length ? <ol className="list-decimal space-y-1 pl-5">
                {invoice.lineItems.map((item, index) => <li key={item.id ?? index}>{item.name} · {item.quantity} {item.unit}</li>)}
              </ol> : <p className="whitespace-pre-wrap">{invoice.description || 'У счёта нет описания или позиций.'}</p>}
            </div> : null}
          </section>
        </div>
        {selected.length > 0 && !hasCases ? <p className="text-sm text-amber-800">Для работы со счётом у каждой выбранной задачи должно быть указано дело.</p> : null}
        {selected.length > 0 && invoice && hasCases && !matchesInvoice ? <p className="text-sm text-amber-800">Выбранный счёт относится к другому делу.</p> : null}
        {writeError || bulk.error ? <p role="alert" className="text-sm text-rose-700">{writeError || bulk.error}</p> : null}
        {result ? <p role="status" className="text-sm text-emerald-800">{result}</p> : null}
        <div className="flex flex-wrap items-center justify-end gap-2 text-sm">
          <span className="mr-auto">Выбрано задач: {selected.length}</span>
          {canLink ? <button type="button" className="rounded bg-emerald-700 px-3 py-1.5 text-white disabled:opacity-50"
            disabled={busy || !invoice || !allEditable || !matchesInvoice || Boolean(writeError)} onClick={() => void linkSelected()}>Привязать к счёту</button> : null}
          {canCreate ? <button type="button" className="rounded bg-blue-700 px-3 py-1.5 text-white disabled:opacity-50"
            disabled={busy || !hasCases} onClick={() => onCreate(selected)}>Подготовить новый счёт</button> : null}
          {canEdit ? <button type="button" className="rounded border px-3 py-1.5 disabled:opacity-50" disabled={busy || !allEditable} onClick={bulk.begin}>Отметить как внутренние</button> : null}
        </div>
      </div>
    </div>
    {bulk.dialog}
  </>;
}
