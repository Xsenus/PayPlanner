import { useCallback, useEffect, useMemo, useRef, useState } from 'react';

import { useDialog } from '../../hooks/useDialog';
import { useToast } from '../../hooks/useToast';
import { apiService } from '../../services/api';
import type {
  SalesRequestClientCaseOption,
  SalesRequestClientOption,
  SalesRequestDetail,
  SalesRequestItem,
  SalesRequestSource,
  SalesRequestStatusItem,
} from '../../types';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { formatLocalYMD } from '../../utils/dateUtils';
import { SalesRequestFunnelSettings } from './SalesRequestFunnelSettings';
import type { TaskJournalAssignee } from '../../utils/taskJournalStore';

interface SalesRequestsPageProps {
  allowCreate: boolean;
  allowEdit: boolean;
  allowAnalytics: boolean;
  allowConfigure?: boolean;
  initialRequestId?: number | null;
}

const SOURCE_LABELS: Record<SalesRequestSource, string> = {
  Call: 'Звонок',
  Max: 'Макс',
  Avito: 'Авито',
  Site: 'Сайт',
  Other: 'Другое',
};

const CONTRACT_MODE_LABELS: Record<string, string> = { Fixed: 'фиксированный', Actual: 'по факту', Mixed: 'разные условия', None: 'нет действующего' };

const TYPE_LABELS: Record<string, string> = { New: 'Новый', Fixed: 'Фиксированный', PerCase: 'По факту' };

const EVENT_LABELS: Record<string, string> = {
  Created: 'Заявка создана',
  StatusChanged: 'Смена статуса',
  FieldsChanged: 'Изменены данные',
  TaskCreated: 'Создана задача',
  TasksForceClosed: 'Задачи закрыты принудительно',
};

const money = (value: number) => value.toLocaleString('ru-RU', { minimumFractionDigits: 0, maximumFractionDigits: 2 });

const emptyForm = () => ({
  source: '' as SalesRequestSource | '',
  query: '',
  clientId: null as number | null,
  newClient: false,
  newName: '',
  newPhone: '',
  newEmail: '',
  caseId: null as number | null,
  summary: '',
  amount: '',
});

/**
 * Раздел «Заявки»: список, создание с поиском клиента и проверкой дел, карточка с переходами по статусам.
 * Доступ задаёт серверный раздел прав «requests»; здесь только отображение и подсказки.
 */
export function SalesRequestsPage({ allowCreate, allowEdit, allowAnalytics, allowConfigure = false, initialRequestId = null }: SalesRequestsPageProps) {
  const toast = useToast();
  const dialog = useDialog();
  const [statuses, setStatuses] = useState<SalesRequestStatusItem[]>([]);
  const [items, setItems] = useState<SalesRequestItem[]>([]);
  const [total, setTotal] = useState(0);
  const [filters, setFilters] = useState({ statusId: '', source: '', responsibleUserId: '', q: '', closed: false, page: 1 });
  const [assignees, setAssignees] = useState<TaskJournalAssignee[]>([]);
  const [detail, setDetail] = useState<SalesRequestDetail | null>(null);
  const [creating, setCreating] = useState(false);
  const [form, setForm] = useState(emptyForm);
  const [options, setOptions] = useState<SalesRequestClientOption[]>([]);
  const [selectedClient, setSelectedClient] = useState<SalesRequestClientOption | null>(null);
  const [clientCases, setClientCases] = useState<{ items: SalesRequestClientCaseOption[]; hasMore: boolean }>({ items: [], hasMore: false });
  const [casesLoading, setCasesLoading] = useState(false);
  const [editing, setEditing] = useState<{ summary: string; type: string; source: string; amount: string; comment: string; responsibleUserId: string } | null>(null);
  const [conflict, setConflict] = useState(false);
  const [busy, setBusy] = useState(false);
  const [paymentId, setPaymentId] = useState('');
  const [taskForm, setTaskForm] = useState({ assigneeUserId: '', title: '' });
  const [report, setReport] = useState<string | null>(null);
  const [settingsOpen, setSettingsOpen] = useState(false);

  const listSequence = useRef(0);
  const detailSequence = useRef(0);

  const statusById = useMemo(() => new Map(statuses.map((item) => [item.id, item])), [statuses]);
  const userName = useCallback((id?: number | null) => assignees.find((item) => item.id === id)?.name ?? (id ? `№${id}` : ''), [assignees]);

  const loadList = useCallback(async () => {
    const sequence = ++listSequence.current;
    try {
      const page = await apiService.getSalesRequests({
        statusId: filters.statusId ? Number(filters.statusId) : undefined,
        source: filters.source || undefined,
        responsibleUserId: filters.responsibleUserId ? Number(filters.responsibleUserId) : undefined,
        q: filters.q.trim() || undefined,
        closed: filters.closed,
        pageSize: 50,
        page: filters.page,
      });
      if (sequence !== listSequence.current) return;
      setItems(page.items);
      setTotal(page.total);
    } catch (error) {
      if (sequence === listSequence.current) toast.error(extractDisplayErrorMessage(error, 'Не удалось загрузить заявки.'), 'Заявки');
    }
  }, [filters, toast]);

  useEffect(() => {
    if (initialRequestId) {
      void openDetail(initialRequestId);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [initialRequestId]);

  const loadStatuses = useCallback(() => {
    void apiService.getSalesRequestStatuses().then(setStatuses).catch(() => setStatuses([]));
  }, []);

  useEffect(() => {
    loadStatuses();
    void apiService.getTaskJournalAssignees().then(setAssignees).catch(() => setAssignees([]));
  }, [loadStatuses]);

  useEffect(() => {
    void loadList();
    const sequence = listSequence;
    return () => { sequence.current++; };
  }, [loadList]);

  useEffect(() => {
    const text = form.query.trim();
    if (!creating || form.clientId || form.newClient || text.length < 2) {
      setOptions([]);
      return;
    }
    let active = true;
    const handle = window.setTimeout(() => {
      void apiService.searchSalesRequestClients(text).then((items) => { if (active) setOptions(items); }).catch(() => { if (active) setOptions([]); });
    }, 250);
    return () => { active = false; window.clearTimeout(handle); };
  }, [creating, form.clientId, form.newClient, form.query]);

  const openDetail = async (id: number) => {
    const sequence = ++detailSequence.current;
    try {
      const loaded = await apiService.getSalesRequest(id);
      if (sequence !== detailSequence.current) return;
      setDetail(loaded);
      setEditing(null);
      setConflict(false);
      setTaskForm({ assigneeUserId: '', title: '' });
      setPaymentId('');
    } catch (error) {
      if (sequence === detailSequence.current) toast.error(extractDisplayErrorMessage(error, 'Не удалось открыть заявку.'), 'Заявки');
    }
  };

  const closeDetail = () => {
    // Ответ запроса, отправленного до закрытия карточки, не должен её снова открыть.
    detailSequence.current++;
    setDetail(null);
    setEditing(null);
    setConflict(false);
  };

  const caseSequence = useRef(0);
  const loadClientCases = async (clientId: number, skip: number) => {
    const sequence = ++caseSequence.current;
    setCasesLoading(true);
    try {
      const page = await apiService.getSalesRequestClientCases(clientId, skip, 20);
      if (sequence !== caseSequence.current) return;
      setClientCases((prev) => ({ items: skip === 0 ? page.items : [...prev.items, ...page.items], hasMore: page.hasMore }));
    } catch (error) {
      if (sequence === caseSequence.current) toast.error(extractDisplayErrorMessage(error, 'Не удалось загрузить дела клиента.'), 'Заявки');
    } finally {
      if (sequence === caseSequence.current) setCasesLoading(false);
    }
  };

  const closeEditAfterReload = () => {
    setEditing(null);
    setConflict(false);
  };

  const startEdit = (request: SalesRequestItem) => {
    setConflict(false);
    setEditing({
      summary: request.summary,
      type: request.type,
      source: request.source,
      amount: request.amount ? String(request.amount) : '',
      comment: request.comment ?? '',
      responsibleUserId: String(request.responsibleUserId),
    });
  };

  const saveEdit = () =>
    run(async () => {
      if (!detail || !editing) return;
      const amount = editing.amount.trim() === '' ? undefined : Number(editing.amount.replace(',', '.'));
      if (amount !== undefined && (!Number.isFinite(amount) || amount < 0 || amount > 1_000_000_000_000)) {
        toast.error('Укажите сумму от 0 до 1 000 000 000 000.', 'Заявки');
        return;
      }
      const sequence = detailSequence.current;
      try {
        await apiService.updateSalesRequest(detail.request.id, {
          version: detail.request.version,
          summary: editing.summary.trim(),
          type: editing.type,
          source: editing.source,
          amount,
          comment: editing.comment,
          responsibleUserId: Number(editing.responsibleUserId),
        });
      } catch (error) {
        // Версия устарела: данные никто не затирает, сотрудник сначала перечитывает актуальную карточку.
        if (error instanceof Error && error.message.includes('уже изменили')) {
          if (sequence === detailSequence.current) setConflict(true);
          return;
        }
        throw error;
      }
      if (sequence === detailSequence.current) setEditing(null);
      await loadList();
      if (sequence === detailSequence.current) await openDetail(detail.request.id);
    }, 'Не удалось сохранить заявку.');

  const run = async (action: () => Promise<void>, failure: string) => {
    setBusy(true);
    try {
      await action();
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, failure), 'Заявки');
    } finally {
      setBusy(false);
    }
  };

  const amountValue = Number(form.amount.replace(',', '.'));
  const canSubmit =
    !busy &&
    form.source !== '' &&
    form.summary.trim() !== '' &&
    (form.clientId !== null || (form.newClient && form.newName.trim() !== '' && form.newPhone.trim() !== ''));

  const submitCreate = () =>
    run(async () => {
      if (form.amount.trim() !== '' && (!Number.isFinite(amountValue) || amountValue < 0 || amountValue > 1_000_000_000_000)) {
        toast.error('Укажите сумму от 0 до 1 000 000 000 000.', 'Заявки');
        return;
      }
      const created = await apiService.createSalesRequest({
        source: form.source as SalesRequestSource,
        ...(form.clientId ? { clientId: form.clientId } : { newClientName: form.newName.trim(), newClientPhone: form.newPhone.trim(), newClientEmail: form.newEmail.trim() || undefined }),
        clientCaseId: form.caseId ?? undefined,
        summary: form.summary.trim(),
        amount: form.amount.trim() === '' ? undefined : amountValue,
      });
      setCreating(false);
      setForm(emptyForm());
      setSelectedClient(null);
      setClientCases({ items: [], hasMore: false });
      caseSequence.current++;
      await loadList();
      await openDetail(created.id);
    }, 'Не удалось создать заявку.');

  const transition = (target: SalesRequestStatusItem, force = false) =>
    run(async () => {
      if (!detail) return;
      const sequence = detailSequence.current;
      try {
        const updated = await apiService.transitionSalesRequest(detail.request.id, {
          toStatusId: target.id,
          version: detail.request.version,
          paidPaymentId: target.finalKind === 'Success' && paymentId ? Number(paymentId) : undefined,
          forceCloseTasks: force || undefined,
        });
        await loadList();
        if (sequence === detailSequence.current) await openDetail(updated.id);
      } catch (error) {
        const message = error instanceof Error ? error.message : '';
        if (!force && message.includes('незакрытые задачи')) {
          const confirmed = await dialog.confirm({
            title: 'По заявке есть незакрытые задачи',
            description: 'Принудительно закрытые задачи будут отмечены как отменённые, а не выполненные. Закрыть заявку?',
            confirmLabel: 'Закрыть принудительно',
            cancelLabel: 'Отмена',
            variant: 'danger',
          });
          if (confirmed) {
            await transition(target, true);
          }
          return;
        }
        throw error;
      }
    }, 'Не удалось сменить статус.');

  const openStatuses = detail ? statuses.filter((item) => item.id !== detail.request.statusId) : [];
  const finalClosed = detail ? detail.request.finalKind !== 'None' : false;

  return (
    <section className="space-y-3 p-3 lg:p-4" data-testid="sales-requests-page">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h1 className="text-xl font-semibold text-slate-900">Заявки</h1>
        <div className="flex gap-2">
          {allowConfigure ? (
            <button type="button" className="rounded-lg border border-slate-200 px-3 py-1.5 text-sm hover:bg-slate-50" onClick={() => setSettingsOpen((value) => !value)}>
              Настройка воронки
            </button>
          ) : null}
          {allowAnalytics ? (
            <button
              type="button"
              className="rounded-lg border border-slate-200 px-3 py-1.5 text-sm hover:bg-slate-50"
              onClick={() => {
                const to = new Date();
                const from = new Date(to.getFullYear(), to.getMonth(), 1);
                void apiService
                  .getSalesRequestReport(formatLocalYMD(from), formatLocalYMD(to))
                  .then((r) =>
                    setReport(
                      `За месяц: заявок ${r.total}, оплачено ${r.succeeded}, брошено/отказ ${r.failed}, в работе ${r.inProgress}, конверсия ${r.conversionPercent}% · сумма оплаченных заявок ${money(r.wonAmount)}, сумма потерянных заявок ${money(r.lostAmount)}` +
                      (r.lossByStage.length > 0 ? `. Провалы по этапам: ${r.lossByStage.map((item) => `${item.stage} — ${item.count} (${money(item.lostAmount)})`).join(', ')}` : ''),
                    ),
                  )
                  .catch((error) => toast.error(extractDisplayErrorMessage(error, 'Не удалось построить отчёт.'), 'Заявки'));
              }}
            >
              Отчёт за месяц
            </button>
          ) : null}
          {allowCreate ? (
            <button type="button" className="rounded-lg bg-blue-600 px-3 py-1.5 text-sm text-white hover:bg-blue-700" onClick={() => setCreating(true)}>
              Новая заявка
            </button>
          ) : null}
        </div>
      </div>
      {settingsOpen && allowConfigure ? <SalesRequestFunnelSettings statuses={statuses} assignees={assignees} onChanged={loadStatuses} /> : null}
      {report ? <p className="rounded-lg bg-slate-50 p-2 text-sm text-slate-700" data-testid="sales-requests-report">{report}</p> : null}

      <div className="flex flex-wrap items-end gap-2 text-sm">
        <select aria-label="Статус" value={filters.statusId} onChange={(event) => setFilters((prev) => ({ ...prev, statusId: event.target.value, page: 1 }))} className="rounded-lg border border-slate-300 px-2 py-1">
          <option value="">Все статусы</option>
          {statuses.map((item) => (
            <option key={item.id} value={item.id}>{item.title}</option>
          ))}
        </select>
        <select aria-label="Источник" value={filters.source} onChange={(event) => setFilters((prev) => ({ ...prev, source: event.target.value, page: 1 }))} className="rounded-lg border border-slate-300 px-2 py-1">
          <option value="">Все источники</option>
          {Object.entries(SOURCE_LABELS).map(([key, label]) => (
            <option key={key} value={key}>{label}</option>
          ))}
        </select>
        <select aria-label="Ответственный" value={filters.responsibleUserId} onChange={(event) => setFilters((prev) => ({ ...prev, responsibleUserId: event.target.value, page: 1 }))} className="rounded-lg border border-slate-300 px-2 py-1">
          <option value="">Все ответственные</option>
          {assignees.map((item) => (
            <option key={item.id} value={item.id}>{item.name}</option>
          ))}
        </select>
        <input aria-label="Поиск" placeholder="Клиент, телефон, суть" value={filters.q} onChange={(event) => setFilters((prev) => ({ ...prev, q: event.target.value, page: 1 }))} className="w-56 rounded-lg border border-slate-300 px-2 py-1" />
        <label className="flex items-center gap-1">
          <input type="checkbox" checked={filters.closed} onChange={(event) => setFilters((prev) => ({ ...prev, closed: event.target.checked, page: 1 }))} />
          Закрытые
        </label>
        <span className="text-slate-500">Всего: {total}</span>
      </div>

      <div className="overflow-x-auto">
        <table className="min-w-full divide-y divide-slate-200 text-sm">
          <thead className="bg-slate-50 text-left text-slate-600">
            <tr>
              <th className="px-2 py-1 font-semibold">№</th>
              <th className="px-2 py-1 font-semibold">Дата</th>
              <th className="px-2 py-1 font-semibold">Клиент</th>
              <th className="px-2 py-1 font-semibold">Источник</th>
              <th className="px-2 py-1 font-semibold">Статус</th>
              <th className="px-2 py-1 font-semibold">Ответственный</th>
              <th className="px-2 py-1 text-right font-semibold">Сумма</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100">
            {items.map((item) => (
              <tr key={item.id} className="cursor-pointer hover:bg-slate-50" onClick={() => void openDetail(item.id)}>
                <td className="px-2 py-1">{item.id}</td>
                <td className="px-2 py-1">{item.createdAt.slice(0, 10)}</td>
                <td className="px-2 py-1">{item.clientName}<span className="ml-1 text-xs text-slate-500">{item.clientPhone}</span></td>
                <td className="px-2 py-1">{SOURCE_LABELS[item.source] ?? item.source}</td>
                <td className="px-2 py-1">
                  {statusById.get(item.statusId)?.title ?? item.statusId}
                  {item.slaDueAt && new Date(item.slaDueAt).getTime() < Date.now() ? <span className="ml-1 text-xs text-rose-600" title="Срок этапа истёк">просрочено</span> : null}
                </td>
                <td className="px-2 py-1">{userName(item.responsibleUserId)}</td>
                <td className="px-2 py-1 text-right">{item.amount ? money(item.amount) : ''}</td>
              </tr>
            ))}
          </tbody>
        </table>
        {items.length === 0 ? <p className="py-3 text-sm text-slate-500">Заявок нет.</p> : null}
      </div>

      {total > 50 ? (
        <div className="flex items-center gap-3 text-sm">
          <button type="button" disabled={filters.page === 1} onClick={() => setFilters((prev) => ({ ...prev, page: prev.page - 1 }))}>Предыдущие заявки</button>
          <span>Страница {filters.page} из {Math.ceil(total / 50)}</span>
          <button type="button" disabled={filters.page * 50 >= total} onClick={() => setFilters((prev) => ({ ...prev, page: prev.page + 1 }))}>Следующие заявки</button>
        </div>
      ) : null}

      {creating ? (
        <div className="rounded-2xl border border-slate-200 bg-white p-3" data-testid="sales-request-create">
          <h2 className="font-semibold text-slate-900">Новая заявка</h2>
          <div className="mt-2 grid gap-2 text-sm md:grid-cols-2">
            <label className="flex flex-col gap-1">
              Источник
              <select aria-label="Источник заявки" value={form.source} onChange={(event) => setForm((prev) => ({ ...prev, source: event.target.value as SalesRequestSource, page: 1 }))} className="rounded-lg border border-slate-300 px-2 py-1">
                <option value="">Выберите</option>
                {Object.entries(SOURCE_LABELS).map(([key, label]) => (
                  <option key={key} value={key}>{label}</option>
                ))}
              </select>
            </label>
            <label className="flex flex-col gap-1">
              Сумма (если уже известна)
              <input aria-label="Сумма заявки" inputMode="decimal" value={form.amount} onChange={(event) => setForm((prev) => ({ ...prev, amount: event.target.value }))} className="rounded-lg border border-slate-300 px-2 py-1" />
            </label>
          </div>

          {form.clientId ? (
            <p className="mt-2 text-sm">
              Клиент: <b>{selectedClient?.name ?? form.query}</b>{' '}
              <button type="button" className="text-blue-600 underline" onClick={() => {
                  setSelectedClient(null);
                  caseSequence.current++;
                  setClientCases({ items: [], hasMore: false });
                  setCasesLoading(false);
                  setForm((prev) => ({ ...prev, clientId: null, caseId: null }));
                }}>изменить</button>
            </p>
          ) : form.newClient ? (
            <div className="mt-2 grid gap-2 text-sm md:grid-cols-3">
              <input aria-label="ФИО нового клиента" placeholder="ФИО" value={form.newName} onChange={(event) => setForm((prev) => ({ ...prev, newName: event.target.value }))} className="rounded-lg border border-slate-300 px-2 py-1" />
              <input aria-label="Телефон нового клиента" placeholder="Телефон" value={form.newPhone} onChange={(event) => setForm((prev) => ({ ...prev, newPhone: event.target.value }))} className="rounded-lg border border-slate-300 px-2 py-1" />
              <input aria-label="Email нового клиента" placeholder="Email (необязательно)" value={form.newEmail} onChange={(event) => setForm((prev) => ({ ...prev, newEmail: event.target.value }))} className="rounded-lg border border-slate-300 px-2 py-1" />
            </div>
          ) : (
            <div className="mt-2 text-sm">
              <input aria-label="Поиск клиента" placeholder="Клиент: ФИО, телефон или email" value={form.query} onChange={(event) => setForm((prev) => ({ ...prev, query: event.target.value }))} className="w-full rounded-lg border border-slate-300 px-2 py-1" />
              <ul className="mt-1 divide-y divide-slate-100 rounded-lg border border-slate-100">
                {options.map((option) => (
                  <li key={option.id}>
                    <button type="button" className="w-full px-2 py-1 text-left hover:bg-slate-50" onClick={() => {
                        setSelectedClient(option);
                        setClientCases({ items: [], hasMore: false });
                        void loadClientCases(option.id, 0);
                        setForm((prev) => ({ ...prev, clientId: option.id }));
                      }}>
                      {option.name} <span className="text-xs text-slate-500">{option.phone}</span>
                      <span className="ml-2 text-xs text-slate-500">дел: {option.casesCount}</span>
                    </button>
                  </li>
                ))}
              </ul>
              <button type="button" className="mt-1 text-blue-600 underline" onClick={() => setForm((prev) => ({ ...prev, newClient: true, newName: prev.query }))}>+ Добавить нового</button>
            </div>
          )}

          {selectedClient && selectedClient.casesCount > 0 ? (
            <fieldset className="mt-2 text-sm" data-testid="sales-request-cases">
              <legend className="font-medium">У клиента дел: {selectedClient.casesCount} — по какому обращение?</legend>
              <label className="flex items-center gap-2">
                <input type="radio" name="request-case" checked={form.caseId === null} onChange={() => setForm((prev) => ({ ...prev, caseId: null }))} />
                Новое обращение
              </label>
              {clientCases.items.map((item) => (
                <label key={item.id} className="flex flex-wrap items-center gap-2">
                  <input type="radio" name="request-case" checked={form.caseId === item.id} onChange={() => setForm((prev) => ({ ...prev, caseId: item.id }))} />
                  №{item.caseNumber} {item.title}
                  <span className="text-xs text-slate-500">
                    {item.status}{item.lawyerName ? ` · юрист ${item.lawyerName}` : ''} · договор: {CONTRACT_MODE_LABELS[item.contractMode]}
                    {item.lastActivityAt ? ` · последнее действие ${item.lastActivityAt.slice(0, 10)}` : ''}
                  </span>
                </label>
              ))}
              {clientCases.hasMore ? (
                <button type="button" disabled={casesLoading} className="mt-1 text-blue-600 underline" onClick={() => void loadClientCases(selectedClient.id, clientCases.items.length)}>
                  Показать ещё дела
                </button>
              ) : null}
            </fieldset>
          ) : null}

          <label className="mt-2 flex flex-col gap-1 text-sm">
            Суть запроса
            <textarea aria-label="Суть запроса" rows={3} value={form.summary} onChange={(event) => setForm((prev) => ({ ...prev, summary: event.target.value }))} className="rounded-lg border border-slate-300 px-2 py-1" />
          </label>
          <div className="mt-2 flex gap-2">
            <button type="button" disabled={!canSubmit} onClick={() => void submitCreate()} className="rounded-lg bg-blue-600 px-3 py-1.5 text-sm text-white hover:bg-blue-700 disabled:opacity-50">Сохранить</button>
            <button type="button" disabled={busy} onClick={() => {
              setCreating(false);
              setForm(emptyForm());
              setSelectedClient(null);
              caseSequence.current++;
              setClientCases({ items: [], hasMore: false });
              setCasesLoading(false);
            }} className="rounded-lg border border-slate-200 px-3 py-1.5 text-sm">Отмена</button>
          </div>
        </div>
      ) : null}

      {detail ? (
        <div className="rounded-2xl border border-slate-200 bg-white p-3" data-testid="sales-request-detail">
          <div className="flex items-start justify-between gap-2">
            <div>
              <h2 className="font-semibold text-slate-900">Заявка №{detail.request.id} · {detail.request.clientName}</h2>
              <p className="text-sm text-slate-600">
                {statusById.get(detail.request.statusId)?.title} · {SOURCE_LABELS[detail.request.source]} · {TYPE_LABELS[detail.request.type]}
                {detail.request.linkedCaseWasClosed ? ' · по закрытому делу' : ''}
              </p>
            </div>
            <button type="button" className="text-sm text-slate-500 hover:text-slate-800" onClick={closeDetail}>Закрыть</button>
          </div>
          <p className="mt-2 whitespace-pre-wrap text-sm">{detail.request.summary}</p>
          {detail.request.comment ? <p className="mt-1 text-xs text-slate-500">{detail.request.comment}</p> : null}
          {allowEdit && !finalClosed ? (
            editing ? (
              <div className="mt-2 grid gap-2 rounded-lg border border-slate-200 p-2 text-sm" data-testid="sales-request-edit">
                {conflict ? (
                  <p role="alert" className="text-rose-700">
                    Заявку уже изменил другой сотрудник. Ваши правки не сохранены.{' '}
                    <button type="button" className="underline" onClick={() => void openDetail(detail.request.id).then(closeEditAfterReload)}>Загрузить актуальную карточку</button>
                  </p>
                ) : null}
                <textarea aria-label="Суть запроса (правка)" rows={3} value={editing.summary} onChange={(event) => setEditing((prev) => (prev ? { ...prev, summary: event.target.value } : prev))} className="rounded-lg border border-slate-300 px-2 py-1" />
                <div className="flex flex-wrap gap-2">
                  <select aria-label="Источник (правка)" value={editing.source} onChange={(event) => setEditing((prev) => (prev ? { ...prev, source: event.target.value } : prev))} className="rounded-lg border border-slate-300 px-2 py-1">
                    {Object.entries(SOURCE_LABELS).map(([key, label]) => (
                      <option key={key} value={key}>{label}</option>
                    ))}
                  </select>
                  <select aria-label="Тип (правка)" value={editing.type} onChange={(event) => setEditing((prev) => (prev ? { ...prev, type: event.target.value } : prev))} className="rounded-lg border border-slate-300 px-2 py-1">
                    {Object.entries(TYPE_LABELS).map(([key, label]) => (
                      <option key={key} value={key}>{label}</option>
                    ))}
                  </select>
                  <select aria-label="Ответственный (правка)" value={editing.responsibleUserId} onChange={(event) => setEditing((prev) => (prev ? { ...prev, responsibleUserId: event.target.value } : prev))} className="rounded-lg border border-slate-300 px-2 py-1">
                    {assignees.map((item) => (
                      <option key={item.id} value={item.id}>{item.name}</option>
                    ))}
                  </select>
                  <input aria-label="Сумма (правка)" inputMode="decimal" value={editing.amount} onChange={(event) => setEditing((prev) => (prev ? { ...prev, amount: event.target.value } : prev))} className="w-32 rounded-lg border border-slate-300 px-2 py-1" />
                </div>
                <textarea aria-label="Комментарий (правка)" rows={2} value={editing.comment} onChange={(event) => setEditing((prev) => (prev ? { ...prev, comment: event.target.value } : prev))} className="rounded-lg border border-slate-300 px-2 py-1" />
                <div className="flex gap-2">
                  <button type="button" disabled={busy || editing.summary.trim() === ''} onClick={() => void saveEdit()} className="rounded-lg bg-blue-600 px-3 py-1 text-white disabled:opacity-50">Сохранить правки</button>
                  <button type="button" onClick={() => { setEditing(null); setConflict(false); }} className="rounded-lg border border-slate-200 px-3 py-1">Отмена</button>
                </div>
              </div>
            ) : (
              <button type="button" className="mt-2 text-sm text-blue-600 underline" onClick={() => startEdit(detail.request)}>Изменить данные заявки</button>
            )
          ) : null}

          {allowEdit && !finalClosed ? (
            <div className="mt-3 flex flex-wrap items-end gap-2 text-sm">
              <label className="flex flex-col gap-1">
                Платёж клиента в CRM (для «Оплачено»)
                <input aria-label="Номер платежа" inputMode="numeric" value={paymentId} onChange={(event) => setPaymentId(event.target.value)} className="w-32 rounded-lg border border-slate-300 px-2 py-1" />
              </label>
              {openStatuses.map((item) => (
                <button
                  key={item.id}
                  type="button"
                  disabled={busy || (item.finalKind === 'Success' && !paymentId)}
                  onClick={() => void transition(item)}
                  className={`rounded-lg border px-2 py-1 disabled:opacity-50 ${item.finalKind === 'Failure' ? 'border-rose-300 text-rose-700' : item.finalKind === 'Success' ? 'border-emerald-300 text-emerald-700' : 'border-slate-200 text-slate-700'}`}
                >
                  {item.title}
                </button>
              ))}
            </div>
          ) : null}
          {finalClosed ? (
            <p className="mt-2 text-sm text-slate-600">
              Заявка закрыта {detail.request.closedAt?.slice(0, 10)}.
              {detail.request.resultClientCaseId ? ` Создано дело №${detail.request.resultClientCaseId}.` : ''}
            </p>
          ) : null}

          <h3 className="mt-3 text-sm font-semibold text-slate-700">Задачи по заявке</h3>
          <ul className="mt-1 space-y-0.5 text-sm" data-testid="sales-request-tasks">
            {detail.tasks.map((task) => (
              <li key={task.id}>
                {task.status === 'done' ? '✅' : task.status === 'cancelled' ? '⛔' : '•'} {task.title} · {task.assigneeName} · до {task.dueAt.slice(0, 10)}
              </li>
            ))}
            {detail.tasks.length === 0 ? <li className="text-slate-500">Задач нет.</li> : null}
          </ul>
          {allowEdit && !finalClosed ? (
            <div className="mt-2 flex flex-wrap items-end gap-2 text-sm">
              <select aria-label="Исполнитель задачи" value={taskForm.assigneeUserId} onChange={(event) => setTaskForm((prev) => ({ ...prev, assigneeUserId: event.target.value }))} className="rounded-lg border border-slate-300 px-2 py-1">
                <option value="">Исполнитель</option>
                {assignees.map((item) => (
                  <option key={item.id} value={item.id}>{item.name}</option>
                ))}
              </select>
              <input aria-label="Текст задачи" placeholder="Поставить задачу" value={taskForm.title} onChange={(event) => setTaskForm((prev) => ({ ...prev, title: event.target.value }))} className="w-64 rounded-lg border border-slate-300 px-2 py-1" />
              <button
                type="button"
                disabled={busy || !taskForm.assigneeUserId || !taskForm.title.trim()}
                onClick={() =>
                  void run(async () => {
                    const sequence = detailSequence.current;
                    await apiService.createSalesRequestTask(detail.request.id, { assigneeUserId: Number(taskForm.assigneeUserId), title: taskForm.title.trim() });
                    if (sequence === detailSequence.current) await openDetail(detail.request.id);
                  }, 'Не удалось поставить задачу.')
                }
                className="rounded-lg border border-slate-200 px-2 py-1 disabled:opacity-50"
              >
                Поставить задачу
              </button>
            </div>
          ) : null}

          <h3 className="mt-3 text-sm font-semibold text-slate-700">История</h3>
          <ul className="mt-1 space-y-0.5 text-xs text-slate-600">
            {detail.events.map((event) => (
              <li key={event.id}>
                {event.createdAt.slice(0, 16).replace('T', ' ')} · {EVENT_LABELS[event.kind] ?? event.kind}
                {event.toStatusId ? ` → ${statusById.get(event.toStatusId)?.title ?? ''}` : ''}
                {event.comment ? ` · ${event.comment}` : ''}
                {event.userId ? ` · ${userName(event.userId)}` : ''}
              </li>
            ))}
          </ul>
        </div>
      ) : null}
    </section>
  );
}
