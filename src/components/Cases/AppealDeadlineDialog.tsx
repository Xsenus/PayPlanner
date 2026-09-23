import { useEffect, useMemo, useState } from 'react';
import { ArrowRight, Bell, FilePlus2, Scale, X } from 'lucide-react';

import { apiService } from '../../services/api';
import type { ExpiringCaseAppeal } from '../../types';
import { useUserTimeZone } from '../../hooks/useUserTimeZone';
import { getZonedParts } from '../../utils/timeZoneDate';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';

interface Props {
  userId: number;
  enabled: boolean;
  onOpenCase: (caseId: number) => void;
  onOpenTask?: (taskId: number) => void;
}

function formatCourtDate(value: string) {
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(value);
  return match ? `${match[3]}.${match[2]}.${match[1]}` : value;
}

function localDateKey(timeZoneId: string) {
  const parts = getZonedParts(new Date(), timeZoneId);
  return parts ? `${parts.year}-${String(parts.month).padStart(2, '0')}-${String(parts.day).padStart(2, '0')}` : '';
}

function seenKey(userId: number, dateKey: string, appeal: ExpiringCaseAppeal) {
  return `pp.court.appeal.${userId}.${dateKey}.${appeal.caseId}.${appeal.courtDecisionAt.slice(0, 10)}`;
}

export function AppealDeadlineDialog({ userId, enabled, onOpenCase, onOpenTask }: Props) {
  const timeZoneId = useUserTimeZone();
  const dateKey = useMemo(() => localDateKey(timeZoneId), [timeZoneId]);
  const [appeals, setAppeals] = useState<ExpiringCaseAppeal[]>([]);
  const [open, setOpen] = useState(false);
  const [busyCaseId, setBusyCaseId] = useState<number | null>(null);
  const [createdTasks, setCreatedTasks] = useState<Record<number, number>>({});
  const [remindedCases, setRemindedCases] = useState<number[]>([]);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!enabled || !dateKey) {
      setOpen(false);
      return;
    }
    const controller = new AbortController();
    void apiService.getExpiringCaseAppeals({ signal: controller.signal })
      .then((rows) => {
        if (controller.signal.aborted) return;
        const unseen = rows.filter((appeal) => {
          try {
            return window.localStorage.getItem(seenKey(userId, dateKey, appeal)) !== 'seen';
          } catch {
            return true;
          }
        });
        setAppeals(unseen);
        setOpen(unseen.length > 0);
      })
      .catch(() => {
        // An optional startup reminder must not prevent the CRM from opening.
      });
    return () => controller.abort();
  }, [dateKey, enabled, userId]);

  const dismiss = () => {
    for (const appeal of appeals) {
      try {
        window.localStorage.setItem(seenKey(userId, dateKey, appeal), 'seen');
      } catch {
        // The dialog still closes if browser storage is unavailable.
      }
    }
    setOpen(false);
  };

  const createTask = async (appeal: ExpiringCaseAppeal) => {
    setBusyCaseId(appeal.caseId);
    setError(null);
    try {
      const result = await apiService.createCaseAppealTask(appeal.caseId, {
        courtDecisionAt: appeal.courtDecisionAt,
        appealDeadlineAt: appeal.appealDeadlineAt,
      });
      if (result.taskId) setCreatedTasks((current) => ({ ...current, [appeal.caseId]: result.taskId! }));
    } catch (cause) {
      setError(extractDisplayErrorMessage(cause, 'Не удалось создать задачу на обжалование. Обновите дело и попробуйте снова.'));
    } finally {
      setBusyCaseId(null);
    }
  };

  const remindAssignee = async (appeal: ExpiringCaseAppeal) => {
    setBusyCaseId(appeal.caseId);
    setError(null);
    try {
      await apiService.remindCaseAppealAssignee(appeal.caseId, {
        courtDecisionAt: appeal.courtDecisionAt,
        appealDeadlineAt: appeal.appealDeadlineAt,
      });
      setRemindedCases((current) => [...current, appeal.caseId]);
    } catch (cause) {
      setError(extractDisplayErrorMessage(cause, 'Не удалось отправить напоминание исполнителю.'));
    } finally {
      setBusyCaseId(null);
    }
  };

  if (!open) return null;

  return (
    <div className="fixed inset-0 z-[86] flex items-center justify-center bg-slate-950/55 p-3" role="presentation">
      <section role="dialog" aria-modal="true" aria-labelledby="appeal-deadline-title" className="w-full max-w-2xl overflow-hidden rounded-2xl bg-white shadow-2xl">
        <div className="flex items-start justify-between gap-3 border-b border-amber-100 bg-amber-50 p-4">
          <div className="flex items-start gap-2">
            <Scale className="mt-0.5 h-5 w-5 text-amber-700" />
            <div>
              <h2 id="appeal-deadline-title" className="text-lg font-bold text-slate-900">Срок обжалования истекает</h2>
              <p className="text-xs text-slate-700">По этим делам осталось не более трёх дней. Проверьте решение и следующий шаг.</p>
            </div>
          </div>
          <button type="button" onClick={dismiss} aria-label="Закрыть" className="rounded-lg p-1.5 text-slate-600 hover:bg-white"><X className="h-5 w-5" /></button>
        </div>
        <div className="max-h-[65vh] space-y-3 overflow-y-auto p-3">
          {appeals.map((appeal) => {
            const taskId = createdTasks[appeal.caseId];
            const busy = busyCaseId === appeal.caseId;
            return (
              <article key={`${appeal.caseId}-${appeal.courtDecisionAt}`} className="rounded-xl border border-amber-200 bg-amber-50/40 p-3">
                <div className="flex flex-wrap items-start justify-between gap-2">
                  <div>
                    <div className="text-sm font-semibold text-slate-900">{appeal.caseNumber ? `№${appeal.caseNumber} · ` : ''}{appeal.caseTitle}</div>
                    <div className="mt-1 text-xs text-slate-700">Решение: {formatCourtDate(appeal.courtDecisionAt)} · Срок обжалования: <span className="font-semibold text-amber-800">{formatCourtDate(appeal.appealDeadlineAt)}</span></div>
                  </div>
                  <button type="button" onClick={() => { dismiss(); onOpenCase(appeal.caseId); }} className="inline-flex items-center gap-1 text-xs font-semibold text-blue-700 hover:underline">К делу <ArrowRight className="h-3.5 w-3.5" /></button>
                </div>
                <p className="mt-2 text-xs text-slate-700">{appeal.decisionSummary || 'Результат судебного решения уточните в карточке дела.'}</p>
                <div className="mt-3 flex flex-wrap items-center gap-2">
                  {taskId ? (
                    <>
                      <span className="text-xs font-semibold text-emerald-700">Задача на жалобу создана.</span>
                      {onOpenTask && <button type="button" onClick={() => { dismiss(); onOpenTask(taskId); }} className="rounded-lg bg-blue-600 px-2.5 py-1.5 text-xs font-semibold text-white hover:bg-blue-700">К задаче</button>}
                    </>
                  ) : (
                    <button type="button" disabled={busy} onClick={() => void createTask(appeal)} className="inline-flex items-center gap-1 rounded-lg bg-amber-700 px-2.5 py-1.5 text-xs font-semibold text-white hover:bg-amber-800 disabled:opacity-50"><FilePlus2 className="h-3.5 w-3.5" /> Создать задачу на обжалование</button>
                  )}
                  <button type="button" disabled={busy || remindedCases.includes(appeal.caseId) || Boolean(taskId)} onClick={() => void remindAssignee(appeal)} className="inline-flex items-center gap-1 rounded-lg border border-amber-300 px-2.5 py-1.5 text-xs font-semibold text-amber-900 hover:bg-amber-50 disabled:opacity-50"><Bell className="h-3.5 w-3.5" /> {remindedCases.includes(appeal.caseId) ? 'Напоминание отправлено' : 'Напомнить исполнителю'}</button>
                </div>
              </article>
            );
          })}
          {error && <p role="alert" className="rounded-lg bg-rose-50 p-2 text-xs text-rose-700">{error}</p>}
        </div>
        <div className="flex flex-wrap items-center justify-between gap-2 border-t border-slate-100 p-3">
          <p className="max-w-md text-xs text-slate-600">Решение «не обжаловать» пока фиксируется только после согласования порядка утверждения. Дату судебного акта и срок проверьте в деле.</p>
          <button type="button" onClick={dismiss} className="rounded-lg border border-slate-200 px-3 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50">Позже</button>
        </div>
      </section>
    </div>
  );
}
