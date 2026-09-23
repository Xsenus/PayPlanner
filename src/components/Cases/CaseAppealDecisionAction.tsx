import { useEffect, useState } from 'react';

import { apiService } from '../../services/api';
import { useUserTimeZone } from '../../hooks/useUserTimeZone';
import type { CaseAppealDecision, ClientCase } from '../../types';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { getZonedParts } from '../../utils/timeZoneDate';

interface Props {
  clientCase: ClientCase;
  canEdit: boolean;
  datesSaved?: boolean;
  onTaskCreated?: (taskId: number) => void;
  onOpenTask?: (taskId: number) => void;
}

function formatCourtDate(value: string | null | undefined) {
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(value ?? '');
  return match ? `${match[3]}.${match[2]}.${match[1]}` : 'не указан';
}

export function CaseAppealDecisionAction({ clientCase, canEdit, datesSaved = true, onTaskCreated, onOpenTask }: Props) {
  const timeZoneId = useUserTimeZone();
  const [decision, setDecision] = useState<CaseAppealDecision | null | undefined>(undefined);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [reload, setReload] = useState(0);
  const decisionDate = clientCase.courtDecisionAt;
  const deadlineDate = clientCase.appealDeadlineAt;

  useEffect(() => {
    if (!clientCase.id || !decisionDate || !deadlineDate) return;
    const controller = new AbortController();
    setDecision(undefined);
    setError(null);
    void apiService.getCurrentCaseAppealDecision(clientCase.id, { signal: controller.signal })
      .then((result) => {
        if (!controller.signal.aborted) setDecision(result);
      })
      .catch((cause) => {
        if (!controller.signal.aborted) setError(extractDisplayErrorMessage(cause, 'Не удалось проверить решение об обжаловании.'));
      });
    return () => controller.abort();
  }, [clientCase.id, decisionDate, deadlineDate, reload]);

  if (!clientCase.id || !decisionDate || !deadlineDate) return null;

  const todayParts = getZonedParts(new Date(), timeZoneId);
  const todayKey = todayParts ? `${todayParts.year}-${String(todayParts.month).padStart(2, '0')}-${String(todayParts.day).padStart(2, '0')}` : '';
  const deadlineExpired = Boolean(todayKey && deadlineDate.slice(0, 10) < todayKey);

  const createTask = async () => {
    if (!datesSaved || !canEdit || saving) return;
    setSaving(true);
    setError(null);
    try {
      const result = await apiService.createCaseAppealTask(clientCase.id, {
        courtDecisionAt: decisionDate,
        appealDeadlineAt: deadlineDate,
      });
      setDecision(result);
      if (result.taskId) onTaskCreated?.(result.taskId);
    } catch (cause) {
      setError(extractDisplayErrorMessage(cause, 'Не удалось создать задачу на апелляционную жалобу. Обновите дело и попробуйте снова.'));
    } finally {
      setSaving(false);
    }
  };

  return (
    <section className="rounded-xl border border-amber-200 bg-amber-50/50 p-3 text-sm text-slate-800">
      <h2 className="font-semibold">Решение об обжаловании</h2>
      <p className="mt-1 text-xs">Решение суда: {formatCourtDate(decisionDate)} · срок обжалования: {formatCourtDate(deadlineDate)}.</p>
      {decision ? (
        <div className="mt-2 flex flex-wrap items-center gap-2 text-xs text-emerald-800">
          <span>Решено обжаловать{decision.decidedByUserName ? ` · ${decision.decidedByUserName}` : ''}. Задача на жалобу создана.</span>
          {decision.taskId && onOpenTask ? <button type="button" onClick={() => onOpenTask(decision.taskId!)} className="font-semibold text-blue-700 hover:underline">Открыть задачу</button> : null}
        </div>
      ) : decision === undefined && !error ? (
        <p className="mt-2 text-xs text-slate-500">Проверяем, принято ли решение по этому судебному акту…</p>
      ) : (
        <>
          {canEdit && !deadlineExpired && !['Closed', 'Lost'].includes(clientCase.status) ? (
            <button type="button" onClick={() => void createTask()} disabled={!datesSaved || saving || Boolean(error)} className="mt-2 rounded-lg bg-amber-700 px-3 py-1.5 text-xs font-semibold text-white disabled:cursor-not-allowed disabled:opacity-50">
              {saving ? 'Создаём задачу…' : 'Обжаловать — создать задачу'}
            </button>
          ) : null}
          {!datesSaved ? <p className="mt-1 text-xs text-amber-800">Сначала сохраните изменённые даты в карточке дела.</p> : null}
          {deadlineExpired ? <p className="mt-1 text-xs text-rose-700">Указанный срок обжалования уже прошёл. Проверьте дату и дальнейшие действия по делу.</p> : null}
        </>
      )}
      {error ? <p role="alert" className="mt-2 text-xs text-rose-700">{error} <button type="button" onClick={() => setReload((value) => value + 1)} className="font-semibold underline">Повторить проверку</button></p> : null}
      <p className="mt-2 text-xs text-slate-600">Рабочий срок задачи — за 7 дней до срока обжалования, если решение принято заранее. Позднее решение создаёт срочную задачу без уже прошедшего рабочего срока. Действие «Не обжаловать» ожидает согласования порядка утверждения.</p>
    </section>
  );
}
