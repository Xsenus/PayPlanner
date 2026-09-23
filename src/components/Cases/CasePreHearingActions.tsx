import { useState } from 'react';

import { apiService } from '../../services/api';
import type { ClientCase } from '../../types';
import { buildCasePreHearingTaskPayload, type CasePreHearingAction } from '../../utils/caseTaskPayload';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';

interface Props {
  caseItem: ClientCase;
  timeZoneId: string;
  canCreateTask: boolean;
  datesSaved?: boolean;
  onTaskCreated?: (taskId: number) => void;
  onOpenTask?: (taskId: number) => void;
}

export function CasePreHearingActions({ caseItem, timeZoneId, canCreateTask, datesSaved = true, onTaskCreated, onOpenTask }: Props) {
  const [creating, setCreating] = useState<CasePreHearingAction | null>(null);
  const [createdTaskId, setCreatedTaskId] = useState<number | null>(null);
  const [error, setError] = useState<string | null>(null);
  const hearingAt = Date.parse(caseItem.nextHearingAt ?? '');

  if (!caseItem.id || !Number.isFinite(hearingAt) || hearingAt <= Date.now()
      || ['Closed', 'Lost'].includes(caseItem.status) || !canCreateTask) {
    return null;
  }

  const create = async (action: CasePreHearingAction) => {
    if (creating || !datesSaved || !caseItem.responsibleUserId) return;
    setCreating(action);
    setError(null);
    setCreatedTaskId(null);
    try {
      const task = await apiService.createTaskJournalItem(buildCasePreHearingTaskPayload(caseItem, action, timeZoneId));
      setCreatedTaskId(task.id);
      onTaskCreated?.(task.id);
    } catch (cause) {
      setError(extractDisplayErrorMessage(cause, 'Не удалось создать задачу по заседанию.'));
    } finally {
      setCreating(null);
    }
  };

  const disabled = Boolean(creating) || !datesSaved || !caseItem.responsibleUserId;

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
      {!datesSaved ? <p className="mt-2 text-xs text-amber-900">Сначала сохраните изменённую дату заседания.</p> : null}
      {error ? <p role="alert" className="mt-2 text-xs text-rose-700">{error}</p> : null}
      {createdTaskId ? <p className="mt-2 text-xs text-emerald-800">Задача создана. {onOpenTask ? <button type="button" onClick={() => onOpenTask(createdTaskId)} className="font-semibold text-blue-700 hover:underline">Открыть задачу</button> : null}</p> : null}
    </section>
  );
}
