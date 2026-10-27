import { useCallback, useEffect, useRef, useState } from 'react';
import { apiService, type ContractTaskBillingMode } from '../../services/api';
import { useDialog } from '../../hooks/useDialog';
import { extractDisplayErrorMessage, isHttpErrorStatus } from '../../utils/errorUtils';
import { type TaskBillingBulkItemResult, type TaskBillingBulkResult, type TaskJournalItem } from '../../utils/taskJournalStore';
import { TaskBulkBillingDialog } from './TaskBulkBillingDialog';
import { billingPayload, checkedBillingResult, freezeBillingSelection, type BillingSnapshot, type BillingTarget } from './taskBulkBilling';

type Params = {
  tasks: TaskJournalItem[];
  scopeKey: string;
  scopeLabel: string;
  identityKey: string;
  canEdit: boolean;
  loading?: boolean;
  onApplied: (result: TaskBillingBulkResult) => void;
};

type ActiveSnapshot = BillingSnapshot & { identityKey: string; operationId: number };
type ContractPolicy = { operationId: number; authorityRevision: number; status: 'loading' | 'ready' | 'failed'; mode: ContractTaskBillingMode; error: string };

/** The controller survives hiding its dialog and changing the visible page. No financial draft is persisted to storage. */
export function useTaskBulkBilling(params: Params) {
  const { confirm } = useDialog();
  const [snapshot, setSnapshot] = useState<ActiveSnapshot | null>(null);
  const [open, setOpen] = useState(false);
  const [target, setTarget] = useState<BillingTarget>('internal');
  const [includeCompleted, setIncludeCompleted] = useState(false);
  const [allowContracts, setAllowContracts] = useState(false);
  const [policy, setPolicy] = useState<ContractPolicy | null>(null);
  const [outcomes, setOutcomes] = useState<TaskBillingBulkItemResult[]>([]);
  const [refreshRequired, setRefreshRequired] = useState(false);
  const [attempted, setAttempted] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [freshRetryIds, setFreshRetryIds] = useState<number[] | null>(null);
  const [readErrors, setReadErrors] = useState<Record<number, string>>({});
  const mounted = useRef(false);
  const saving = useRef(false);
  const live = useRef(params);
  live.current = params;
  const snapshotRef = useRef(snapshot);
  snapshotRef.current = snapshot;
  const operationId = useRef(0);
  const policyRequest = useRef(0);
  const authority = JSON.stringify([params.identityKey, params.scopeKey, params.canEdit]);
  const authorityRef = useRef({ key: authority, revision: 0 });
  if (authorityRef.current.key !== authority) authorityRef.current = { key: authority, revision: authorityRef.current.revision + 1 };
  const loadPolicy = useCallback(async (owner: ActiveSnapshot): Promise<ContractPolicy | null> => {
    const request = ++policyRequest.current;
    const revision = authorityRef.current.revision;
    const isCurrent = () => mounted.current && request === policyRequest.current && revision === authorityRef.current.revision
      && snapshotRef.current?.operationId === owner.operationId && live.current.identityKey === owner.identityKey
      && live.current.scopeKey === owner.scopeKey && live.current.canEdit;
    const pending: ContractPolicy = { operationId: owner.operationId, authorityRevision: revision, status: 'loading', mode: 'retainContractTasks', error: '' };
    setPolicy(pending);
    try {
      const settings = await apiService.getTaskJournalSettings();
      if (!isCurrent()) return null;
      if (!settings || typeof settings !== 'object' || Array.isArray(settings)) throw new Error('Не удалось подтвердить настройку учёта договорных задач.');
      const ready: ContractPolicy = { ...pending, status: 'ready', mode: settings.contractTaskBillingMode === 'allowFutureClassification'
        ? 'allowFutureClassification' : 'retainContractTasks' };
      setPolicy(ready); return ready;
    } catch (reason) {
      if (isCurrent()) {
        const failed: ContractPolicy = { ...pending, status: 'failed', error: extractDisplayErrorMessage(reason, 'Не удалось проверить настройку учёта договорных задач. Повторите проверку перед отправкой.') };
        setPolicy(failed);
      }
      return null;
    }
  }, []);
  useEffect(() => {
    const owner = snapshotRef.current;
    if (open && owner && params.canEdit && params.identityKey === owner.identityKey && params.scopeKey === owner.scopeKey) void loadPolicy(owner);
    return () => { policyRequest.current += 1; };
  }, [open, snapshot?.operationId, params.identityKey, params.scopeKey, params.canEdit, loadPolicy]);
  useEffect(() => { mounted.current = true; return () => { mounted.current = false; }; }, []);
  useEffect(() => {
    if (!busy) return;
    const guard = (event: BeforeUnloadEvent) => { event.preventDefault(); event.returnValue = ''; };
    window.addEventListener('beforeunload', guard);
    return () => window.removeEventListener('beforeunload', guard);
  }, [busy]);
  useEffect(() => {
    if (!open) return;
    // Also contain Escape from the provider's portalled confirmation before it reaches page/window shortcuts.
    const guard = (event: KeyboardEvent) => { if (event.key === 'Escape') event.stopPropagation(); };
    document.addEventListener('keydown', guard);
    return () => document.removeEventListener('keydown', guard);
  }, [open]);

  const settled = new Set(outcomes.filter((item) => item.outcome === 'updated' || item.outcome === 'unchanged').map((item) => item.id));
  const remaining = snapshot?.tasks.filter((task) => !settled.has(task.id)) ?? [];
  const candidates = freshRetryIds === null ? remaining : remaining.filter((task) => freshRetryIds.includes(task.id));
  // A row may legitimately disappear from the filtered page during realtime. Its membership is not an authority check.
  const currentOrigin = () => Boolean(snapshot && mounted.current && live.current.identityKey === snapshot.identityKey
    && live.current.scopeKey === snapshot.scopeKey && live.current.canEdit);
  const currentScope = () => currentOrigin() && !live.current.loading;
  const begin = () => {
    if (saving.current || !params.canEdit || params.loading) return;
    if (snapshot) { if (!open) { setPolicy(null); setOpen(true); } return; }
    try {
      setSnapshot({ ...freezeBillingSelection(params.tasks, params.scopeKey, params.scopeLabel), identityKey: params.identityKey, operationId: ++operationId.current });
      setAllowContracts(false);
      setOpen(true); setError('');
    } catch (reason) { setError(extractDisplayErrorMessage(reason, 'Не удалось зафиксировать выбор.')); }
  };
  const replace = async () => {
    if (saving.current || !params.canEdit || params.loading) return;
    saving.current = true; setBusy(true);
    const initial = live.current; const revision = authorityRef.current.revision;
    try {
      const next = freezeBillingSelection(initial.tasks, initial.scopeKey, initial.scopeLabel);
      if (!await confirm({ title: 'Заменить зафиксированный выбор?', description: 'Будет создана новая операция только для текущего выбора на странице. Прежний итог больше не показывается.', confirmLabel: 'Заменить выбор' })) return;
      const selectionKey = (tasks: TaskJournalItem[]) => JSON.stringify(tasks.map((task) => [task.id, task.updatedAt ?? null]));
      if (!mounted.current || revision !== authorityRef.current.revision || live.current.identityKey !== initial.identityKey || live.current.scopeKey !== initial.scopeKey
        || selectionKey(live.current.tasks) !== selectionKey(initial.tasks) || !live.current.canEdit || live.current.loading) return;
      setSnapshot({ ...next, identityKey: initial.identityKey, operationId: ++operationId.current }); setAllowContracts(false); setOutcomes([]); setRefreshRequired(false);
      setAttempted(false); setError(''); setFreshRetryIds(null); setReadErrors({});
    } catch (reason) { if (mounted.current) setError(extractDisplayErrorMessage(reason, 'Не удалось заменить выбор.')); }
    finally { saving.current = false; if (mounted.current) setBusy(false); }
  };
  const apply = async () => {
    if (saving.current || !snapshot || !candidates.length || refreshRequired || !currentScope() || !policyReady
      || (allowContracts && policy?.mode !== 'allowFutureClassification')) return;
    saving.current = true; setBusy(true); setError('');
    const fixed = candidates.map((task) => ({ ...task }));
    const owner = snapshot; const revision = authorityRef.current.revision;
    const fixedTarget = target; const fixedCompleted = includeCompleted; const fixedAllowContracts = allowContracts;
    try {
      if (!await confirm({ title: `Изменить учёт выбранных задач (${fixed.length})?`,
        description: `Тип: ${fixedTarget === 'billable' ? 'платная' : 'неоплачиваемая / внутренняя'}. ${fixedCompleted ? 'Завершённые включены явно.' : 'Завершённые исключены.'} ${fixedAllowContracts ? 'Договорные задачи включены явно: меняется только дальнейший учёт; договор, его сумма и способ расчёта сохраняются. Задачи по фиксированной сумме не включаются в отдельный счёт.' : 'Договорные задачи исключены.'} Сервер проверит права, версии, счета и выплаты каждой строки.`, confirmLabel: 'Изменить учёт' })) return;
      if (!currentScope() || revision !== authorityRef.current.revision) return;
      // Recheck after confirmation: the administrator can revoke the policy while the question is open.
      const freshPolicy = await loadPolicy(owner);
      if (!freshPolicy || !currentScope() || revision !== authorityRef.current.revision) return;
      if (fixedAllowContracts && freshPolicy.mode !== 'allowFutureClassification') {
        setError('Включение договорных задач отключено в настройках. Запрос не отправлен; выбор и параметры сохранены.'); return;
      }
      setAttempted(true);
      const result = checkedBillingResult(await apiService.bulkUpdateTaskJournalItems(billingPayload(fixed, fixedTarget, fixedCompleted, fixedAllowContracts)), fixed.map((task) => task.id));
      if (!mounted.current) return;
      const merged = new Map(outcomes.map((item) => [item.id, item]));
      result.items.forEach((item) => merged.set(item.id, item));
      setOutcomes([...merged.values()]); setFreshRetryIds(null);
      setRefreshRequired(Boolean(snapshot?.tasks.some((task) => !['updated', 'unchanged'].includes(merged.get(task.id)?.outcome ?? ''))));
      // Refresh only the originating visible scope. The frozen result remains available after navigation.
      if (currentOrigin() && revision === authorityRef.current.revision && (result.updated > 0 || result.unchanged > 0)) {
        live.current.onApplied(result);
      }
    } catch (reason) {
      if (mounted.current) {
        setRefreshRequired(true);
        setError(isHttpErrorStatus(reason, 409)
          ? `${extractDisplayErrorMessage(reason, 'Операция отменена из-за конфликта.')} Весь запрос отменён; обновите сведения и подтвердите заново.`
          : `${extractDisplayErrorMessage(reason, 'Не удалось подтвердить результат операции.')} Выбор и параметры сохранены. Перед повтором обновите сведения.`);
      }
    } finally { saving.current = false; if (mounted.current) setBusy(false); }
  };
  const refresh = async () => {
    if (saving.current || !remaining.length || !currentScope()) return;
    saving.current = true; setBusy(true); setError('');
    const owner = snapshot!; const revision = authorityRef.current.revision;
    const ids = remaining.map((task) => task.id);
    const fetched = new Map<number, TaskJournalItem>();
    const failures: Record<number, string> = {};
    let cursor = 0;
    let stopReads = false;
    try {
      const freshPolicy = await loadPolicy(owner);
      if (!freshPolicy || !currentScope() || revision !== authorityRef.current.revision) return;
      if (allowContracts && freshPolicy.mode !== 'allowFutureClassification') {
        setError('Включение договорных задач отключено в настройках. Повтор не отправлен; параметры сохранены.'); return;
      }
      // Existing API has no exact-ID batch read. Bound fresh reads to four in flight and the frozen remainder.
      const reads = await Promise.allSettled(Array.from({ length: Math.min(4, ids.length) }, async () => {
        while (cursor < ids.length) {
          if (!currentScope() || revision !== authorityRef.current.revision || stopReads) return;
          const id = ids[cursor++];
          try {
            const task = await apiService.getTaskJournalItem(id);
            if (!task || task.id !== id) throw new Error('Не удалось подтвердить свежие сведения выбранной задачи.');
            fetched.set(id, task);
          } catch (reason) {
            const unavailable = isHttpErrorStatus(reason, 403) || isHttpErrorStatus(reason, 404);
            failures[id] = unavailable ? 'Задача недоступна для свежего чтения; в повтор не включена.' : 'Не удалось обновить сведения; в повтор не включена.';
            if (!unavailable) stopReads = true;
          }
        }
      }));
      const failure = reads.find((read) => read.status === 'rejected');
      if (failure?.status === 'rejected') throw failure.reason;
      if (!currentScope() || revision !== authorityRef.current.revision) return;
      ids.forEach((id) => { if (!fetched.has(id) && !failures[id]) failures[id] = 'Свежие сведения не получены; в повтор не включена.'; });
      setSnapshot((previous) => previous ? { ...previous, tasks: previous.tasks.map((task) => fetched.get(task.id) ? { ...fetched.get(task.id)!, updatedAt: fetched.get(task.id)!.updatedAt ?? null } : task) } : previous);
      setFreshRetryIds([...fetched.keys()]); setReadErrors(failures); setRefreshRequired(fetched.size === 0);
      if (Object.keys(failures).length) setError(`Обновлены сведения ${fetched.size} из ${ids.length} оставшихся задач. В повтор войдут только успешно прочитанные строки; остальные сохранены в списке.`);
    } catch (reason) {
      if (mounted.current) setError(`${extractDisplayErrorMessage(reason, 'Не удалось обновить выбранные задачи.')} Повтор не отправлен; набор сохранён.`);
    } finally { saving.current = false; if (mounted.current) setBusy(false); }
  };

  const policyReady = policy?.status === 'ready' && policy.operationId === snapshot?.operationId
    && policy.authorityRevision === authorityRef.current.revision;
  const visibleIdentity = !snapshot || (snapshot.identityKey === params.identityKey && params.canEdit);
  return {
    begin, busy, hasDraft: Boolean(snapshot),
    dialog: snapshot && open ? <TaskBulkBillingDialog
      snapshot={visibleIdentity ? snapshot : null} outcomes={visibleIdentity ? outcomes : []} target={target} includeCompleted={includeCompleted}
      policyReady={policyReady} policyLoading={policy?.status === 'loading'} policyError={policy?.error ?? ''}
      contractMode={policyReady ? policy!.mode : 'retainContractTasks'} allowContracts={allowContracts} onAllowContracts={setAllowContracts}
      onPolicyRetry={() => { if (!saving.current && snapshot && currentScope()) void loadPolicy(snapshot); }}
      busy={busy} attempted={attempted} error={visibleIdentity ? error : snapshot.identityKey !== params.identityKey
        ? 'Сотрудник изменился. Исходный набор недоступен в этой сессии.' : 'Право редактирования отозвано. Исходный набор сохранён, отправка заблокирована.'}
      scopeCurrent={currentScope()} refreshRequired={refreshRequired} remaining={remaining.length} readyCount={candidates.length} readErrors={readErrors}
      canReplace={params.canEdit && !params.loading && params.tasks.length > 0}
      onTarget={setTarget} onIncludeCompleted={setIncludeCompleted} onApply={() => void apply()} onRefresh={() => void refresh()}
      onReplace={() => void replace()} onClose={() => { if (!saving.current) { policyRequest.current += 1; setPolicy(null); setOpen(false); } }} /> : null,
    error: !snapshot ? error : '',
  };
}
