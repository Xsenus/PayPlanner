import { useCallback, useRef, useState } from 'react';
import { useDictionarySnapshot } from '../../hooks/useDictionarySnapshot';
import { useNestedCaseOperationOwner, type CaseOperationParent, type NestedCaseOperationOwner } from '../../hooks/useNestedCaseOperationOwner';
import { awaitOwnedUiResult, StaleUiOperationError } from '../../utils/StaleUiOperationError';

import { apiService } from '../../services/api';
import { useUserTimeZone } from '../../hooks/useUserTimeZone';
import type { CaseAppealDecision, ClientCase } from '../../types';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { getZonedParts } from '../../utils/timeZoneDate';

interface Props {
  actionOwner?: CaseOperationParent;
  clientCase: ClientCase;
  canEdit: boolean;
  datesSaved?: boolean;
  onTaskCreated?: (taskId: number) => void | Promise<void>;
  onOpenTask?: (taskId: number) => void;
}

function formatCourtDate(value: string | null | undefined) {
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(value ?? '');
  return match ? `${match[3]}.${match[2]}.${match[1]}` : 'не указан';
}

function expired(deadline: string, timeZoneId: string) {
  const today = getZonedParts(new Date(), timeZoneId);
  const key = today ? `${today.year}-${String(today.month).padStart(2, '0')}-${String(today.day).padStart(2, '0')}` : '';
  return Boolean(key && deadline.slice(0, 10) < key);
}
function validateDecision(value: CaseAppealDecision | null, caseId: number, requireTask = false) {
  if (value === null && !requireTask) return null;
  if (!value || value.caseId !== caseId || !Number.isSafeInteger(value.id) || value.id <= 0 || value.choice !== 'appeal'
    || (value.taskId !== null && (!Number.isSafeInteger(value.taskId) || value.taskId <= 0)) || (requireTask && !value.taskId)) throw new Error('Не удалось подтвердить решение об обжаловании текущего дела. Повторите проверку.');
  return value;
}
export function CaseAppealDecisionAction(props: Props) {
  const timeZoneId = useUserTimeZone();
  const item = props.clientCase;
  const owner = useNestedCaseOperationOwner(JSON.stringify(['appeal-task', item.id, item.clientId, item.legalEntityId, item.responsibleUserId, item.status, item.courtDecisionAt, item.appealDeadlineAt, props.canEdit, props.datesSaved ?? true, !!props.onOpenTask]), props.actionOwner);
  return owner.active ? <OwnedAppealDecision key={owner.scope} {...props} owner={owner} timeZoneId={timeZoneId} /> : null;
}
function OwnedAppealDecision({ clientCase, canEdit, datesSaved = true, onTaskCreated, onOpenTask, owner, timeZoneId }: Props & { owner: NestedCaseOperationOwner; timeZoneId: string }) {
  const [actionError, setActionError] = useState<string | null>(null);
  const callbacks = useRef({ onTaskCreated, onOpenTask }); callbacks.current = { onTaskCreated, onOpenTask };
  const decisionDate = clientCase.courtDecisionAt;
  const deadlineDate = clientCase.appealDeadlineAt;
  const available = Number.isSafeInteger(clientCase.id) && clientCase.id > 0 && !!decisionDate && !!deadlineDate;
  const { capture } = owner;
  const load = useCallback(async (signal: AbortSignal) => {
    const read = capture(); if (!read) throw new StaleUiOperationError();
    return validateDecision(await awaitOwnedUiResult(() => apiService.getCurrentCaseAppealDecision(clientCase.id, { signal, expectedCourtDecisionAt: decisionDate!, expectedAppealDeadlineAt: deadlineDate! }), () => read.isCurrent() && !signal.aborted), clientCase.id);
  }, [capture, clientCase.id, decisionDate, deadlineDate]);
  const snapshot = useDictionarySnapshot<CaseAppealDecision | null>(load, null, { enabled: available, ownerScope: owner.scope }, false);
  const decision = snapshot.value;
  const error = actionError ?? snapshot.error;
  if (!available) return null;
  const deadlineExpired = expired(deadlineDate!, timeZoneId);
  const saving = owner.kind === 'appeal:create';
  const createTask = async () => {
    if (!datesSaved || !canEdit || snapshot.loading || error || decision || expired(deadlineDate!, timeZoneId) || ['Closed', 'Lost'].includes(clientCase.status)) return;
    const operation = owner.begin('appeal:create'); if (!operation) return;
    setActionError(null);
    let confirmed = false;
    try {
      const result = validateDecision(await awaitOwnedUiResult(() => apiService.createCaseAppealTask(clientCase.id, { courtDecisionAt: decisionDate!, appealDeadlineAt: deadlineDate! }), operation.isCurrent), clientCase.id, true)!;
      confirmed = true; snapshot.updateValue(() => result);
      await awaitOwnedUiResult(async () => { await callbacks.current.onTaskCreated?.(result.taskId!); }, operation.isCurrent);
    } catch (cause) {
      if (operation.isCurrent() && !(cause instanceof StaleUiOperationError)) setActionError(extractDisplayErrorMessage(cause, confirmed ? 'Задача создана, но список задач не обновился. Откройте его повторно.' : 'Не удалось подтвердить создание задачи. Перед повтором проверьте решение об обжаловании.'));
    } finally { operation.finish(); }
  };
  const retry = async () => {
    const operation = owner.begin('appeal:read'); if (!operation) return;
    setActionError(null);
    try { await awaitOwnedUiResult(snapshot.refresh, operation.isCurrent); }
    catch (cause) { if (operation.isCurrent() && !(cause instanceof StaleUiOperationError)) setActionError(extractDisplayErrorMessage(cause, 'Не удалось проверить решение об обжаловании.')); }
    finally { operation.finish(); }
  };
  const openTask = () => {
    if (!decision?.taskId || !owner.capture() || owner.busy || !callbacks.current.onOpenTask) return;
    callbacks.current.onOpenTask(decision.taskId);
  };

  return (
    <section className="rounded-xl border border-amber-200 bg-amber-50/50 p-3 text-sm text-slate-800">
      <h2 className="font-semibold">Решение об обжаловании</h2>
      <p className="mt-1 text-xs">Решение суда: {formatCourtDate(decisionDate)} · срок обжалования: {formatCourtDate(deadlineDate)}.</p>
      {decision ? (
        <div className="mt-2 flex flex-wrap items-center gap-2 text-xs text-emerald-800">
          <span>Решено обжаловать{decision.decidedByUserName ? ` · ${decision.decidedByUserName}` : ''}. Задача на жалобу создана.</span>
          {decision.taskId && onOpenTask ? <button type="button" onClick={openTask} disabled={owner.busy} className="font-semibold text-blue-700 hover:underline">Открыть задачу</button> : null}
        </div>
      ) : snapshot.loading && !error ? (
        <p className="mt-2 text-xs text-slate-500">Проверяем, принято ли решение по этому судебному акту…</p>
      ) : (
        <>
          {canEdit && !deadlineExpired && !['Closed', 'Lost'].includes(clientCase.status) ? (
            <button type="button" onClick={() => void createTask()} disabled={!datesSaved || owner.busy || snapshot.loading || Boolean(error)} className="mt-2 rounded-lg bg-amber-700 px-3 py-1.5 text-xs font-semibold text-white disabled:cursor-not-allowed disabled:opacity-50">
              {saving ? 'Создаём задачу…' : 'Обжаловать — создать задачу'}
            </button>
          ) : null}
          {!datesSaved ? <p className="mt-1 text-xs text-amber-800">Сначала сохраните изменённые даты в карточке дела.</p> : null}
          {deadlineExpired ? <p className="mt-1 text-xs text-rose-700">Указанный срок обжалования уже прошёл. Проверьте дату и дальнейшие действия по делу.</p> : null}
        </>
      )}
      {error ? <p role="alert" className="mt-2 text-xs text-rose-700">{error} <button type="button" disabled={owner.busy} onClick={() => void retry()} className="font-semibold underline">Повторить проверку</button></p> : null}
      <p className="mt-2 text-xs text-slate-600">Рабочий срок задачи — за 7 дней до срока обжалования, если решение принято заранее. Позднее решение создаёт срочную задачу без уже прошедшего рабочего срока. Действие «Не обжаловать» ожидает согласования порядка утверждения.</p>
    </section>
  );
}
