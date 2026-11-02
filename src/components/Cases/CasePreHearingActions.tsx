import { useRef, useState } from 'react';
import { useNestedCaseOperationOwner, type CaseOperationParent, type NestedCaseOperationOwner } from '../../hooks/useNestedCaseOperationOwner';
import { awaitOwnedUiResult, StaleUiOperationError } from '../../utils/StaleUiOperationError';

import { apiService } from '../../services/api';
import type { ClientCase } from '../../types';
import { buildCasePreHearingTaskPayload, type CasePreHearingAction } from '../../utils/caseTaskPayload';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';

interface Props {
  actionOwner?: CaseOperationParent;
  caseItem: ClientCase;
  timeZoneId: string;
  canCreateTask: boolean;
  datesSaved?: boolean;
  onTaskCreated?: (taskId: number) => void | Promise<void>;
  onOpenTask?: (taskId: number) => void;
}

export function CasePreHearingActions(props: Props) {
  const item = props.caseItem;
  const owner = useNestedCaseOperationOwner(JSON.stringify(['pre-hearing-tasks', item.id, item.clientId, item.legalEntityId, item.title, item.stage, item.priority, item.responsibleUserId, item.nextHearingAt, item.status, props.canCreateTask, props.datesSaved ?? true, props.timeZoneId, !!props.onOpenTask]), props.actionOwner);
  return owner.active ? <OwnedPreHearingActions key={owner.scope} {...props} owner={owner} /> : null;
}
function OwnedPreHearingActions({ caseItem, timeZoneId, canCreateTask, datesSaved = true, onTaskCreated, onOpenTask, owner }: Props & { owner: NestedCaseOperationOwner }) {
  const [createdTaskId, setCreatedTaskId] = useState<number | null>(null);
  const [error, setError] = useState<string | null>(null);
  const callbacks = useRef({ onTaskCreated, onOpenTask }); callbacks.current = { onTaskCreated, onOpenTask };
  const hearingAt = Date.parse(caseItem.nextHearingAt ?? '');
  const available = () => Number.isSafeInteger(caseItem.id) && caseItem.id > 0 && Number.isFinite(hearingAt) && hearingAt > Date.now() && !['Closed', 'Lost'].includes(caseItem.status) && canCreateTask;
  if (!available()) return null;
  const creating = owner.kind?.startsWith('pre-hearing:') ? owner.kind.slice('pre-hearing:'.length) : null;
  const create = async (action: CasePreHearingAction) => {
    if (!available() || !datesSaved || !caseItem.responsibleUserId) return;
    const operation = owner.begin(`pre-hearing:${action}`); if (!operation) return;
    setError(null); setCreatedTaskId(null);
    let confirmed = false;
    try {
      const task = await awaitOwnedUiResult(() => apiService.createTaskJournalItem(buildCasePreHearingTaskPayload(caseItem, action, timeZoneId)), operation.isCurrent);
      if (!task || !Number.isSafeInteger(task.id) || task.id <= 0 || task.clientCaseId !== caseItem.id) throw new Error('Не удалось подтвердить задачу текущего дела. Обновите список задач перед повторным созданием.');
      confirmed = true; setCreatedTaskId(task.id);
      await awaitOwnedUiResult(async () => { await callbacks.current.onTaskCreated?.(task.id); }, operation.isCurrent);
    } catch (cause) {
      if (operation.isCurrent() && !(cause instanceof StaleUiOperationError)) setError(extractDisplayErrorMessage(cause, confirmed ? 'Задача создана, но список задач не обновился. Откройте его повторно.' : 'Не удалось создать задачу по заседанию. Перед повтором проверьте список задач.'));
    } finally { operation.finish(); }
  };
  const openTask = () => {
    if (!createdTaskId || !owner.capture() || owner.busy || !callbacks.current.onOpenTask) return;
    callbacks.current.onOpenTask(createdTaskId);
  };
  const disabled = owner.busy || !datesSaved || !caseItem.responsibleUserId;

  return (
    <section className="rounded-xl border border-amber-200 bg-amber-50/60 p-3 text-sm text-slate-800">
      <h3 className="font-semibold">Подготовка к заседанию</h3>
      <p className="mt-1 text-xs text-slate-600">Если нужны доверенность или поездка в другой город, создайте связанную задачу ответственному по делу. Срок будет не позднее заседания.</p>
      <div className="mt-2 flex flex-wrap gap-2">
        <button type="button" onClick={() => void create('authority')} disabled={disabled} className="rounded-lg border border-amber-300 bg-white px-3 py-1.5 text-xs font-semibold text-amber-900 hover:bg-amber-100 disabled:cursor-not-allowed disabled:opacity-50">
          {creating === 'authority' ? 'Создаём…' : 'Запросить доверенность'}
        </button>
        <button type="button" onClick={() => void create('travel')} disabled={disabled} className="rounded-lg border border-amber-300 bg-white px-3 py-1.5 text-xs font-semibold text-amber-900 hover:bg-amber-100 disabled:cursor-not-allowed disabled:opacity-50">
          {creating === 'travel' ? 'Создаём…' : 'Запланировать командировку'}
        </button>
      </div>
      {!caseItem.responsibleUserId ? <p className="mt-2 text-xs text-amber-900">Сначала назначьте ответственного по делу.</p> : null}
      {!datesSaved ? <p className="mt-2 text-xs text-amber-900">Сначала сохраните изменённую дату заседания и ответственного по делу.</p> : null}
      {error ? <p role="alert" className="mt-2 text-xs text-rose-700">{error}</p> : null}
      {createdTaskId ? <p className="mt-2 text-xs text-emerald-800">Задача создана. {onOpenTask ? <button type="button" onClick={openTask} disabled={owner.busy} className="font-semibold text-blue-700 hover:underline">Открыть задачу</button> : null}</p> : null}
    </section>
  );
}
