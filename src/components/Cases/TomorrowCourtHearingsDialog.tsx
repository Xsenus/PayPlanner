import { useEffect, useMemo, useState } from 'react';
import { ArrowRight, CalendarDays, Gavel, X } from 'lucide-react';

import { apiService } from '../../services/api';
import type { CourtTomorrowHearing } from '../../types';
import { useUserTimeZone } from '../../hooks/useUserTimeZone';
import { formatDateTimeInTimeZone, getZonedParts } from '../../utils/timeZoneDate';

interface Props {
  userId: number;
  enabled: boolean;
  onOpenCase: (caseId: number) => void;
  onOpenCalendar: () => void;
  calendarAvailable?: boolean;
  onDone?: () => void;
}

function tomorrowKey(timeZoneId: string) {
  const local = getZonedParts(new Date(), timeZoneId);
  if (!local) return '';
  const tomorrow = new Date(Date.UTC(local.year, local.month - 1, local.day + 1));
  return tomorrow.toISOString().slice(0, 10);
}

export function TomorrowCourtHearingsDialog({ userId, enabled, onOpenCase, onOpenCalendar, calendarAvailable = true, onDone }: Props) {
  const timeZoneId = useUserTimeZone();
  const dateKey = useMemo(() => tomorrowKey(timeZoneId), [timeZoneId]);
  const storageKey = `pp.court.tomorrow-hearings.${userId}.${dateKey}`;
  const [hearings, setHearings] = useState<CourtTomorrowHearing[]>([]);
  const [open, setOpen] = useState(false);

  useEffect(() => {
    if (!enabled || !dateKey) {
      setOpen(false);
      return;
    }
    try {
      if (window.localStorage.getItem(storageKey) === 'seen') {
        onDone?.();
        return;
      }
    } catch {
      // The reminder remains available when browser storage is disabled.
    }

    const controller = new AbortController();
    void apiService.getTomorrowCourtHearings({ signal: controller.signal })
      .then((rows) => {
        if (!controller.signal.aborted && rows.length > 0) {
          setHearings(rows);
          setOpen(true);
        } else if (!controller.signal.aborted) {
          onDone?.();
        }
      })
      .catch(() => {
        // An optional reminder must not block the rest of the CRM.
        if (!controller.signal.aborted) onDone?.();
      });
    return () => controller.abort();
  }, [dateKey, enabled, onDone, storageKey]);

  const dismiss = () => {
    try {
      window.localStorage.setItem(storageKey, 'seen');
    } catch {
      // Close the dialog even when storage is unavailable.
    }
    setOpen(false);
    onDone?.();
  };

  if (!open) return null;

  return (
    <div className="fixed inset-0 z-[85] flex items-center justify-center bg-slate-950/55 p-3" role="presentation">
      <section role="dialog" aria-modal="true" aria-labelledby="tomorrow-court-title" className="w-full max-w-2xl overflow-hidden rounded-2xl bg-white shadow-2xl">
        <div className="flex items-start justify-between gap-3 border-b border-blue-100 bg-blue-50 p-4">
          <div className="flex items-center gap-2">
            <Gavel className="h-5 w-5 text-blue-700" />
            <div>
              <h2 id="tomorrow-court-title" className="text-lg font-bold text-slate-900">Заседания завтра</h2>
              <p className="text-xs text-slate-600">До пяти ближайших заседаний по доступным вам делам.</p>
            </div>
          </div>
          <button type="button" onClick={dismiss} aria-label="Закрыть" className="rounded-lg p-1.5 text-slate-500 hover:bg-white"><X className="h-5 w-5" /></button>
        </div>
        <div className="max-h-[60vh] divide-y divide-slate-100 overflow-y-auto">
          {hearings.map((hearing, index) => (
            <div key={`${hearing.caseId}-${hearing.hearingAt}-${index}`} className="grid gap-2 p-3 sm:grid-cols-[105px_minmax(0,1fr)_auto] sm:items-center">
              <div className="text-sm font-bold text-blue-700">{hearing.allDay ? 'Весь день' : formatDateTimeInTimeZone(hearing.hearingAt, timeZoneId).slice(-5)}</div>
              <div className="min-w-0">
                <div className="truncate text-sm font-semibold text-slate-900">{hearing.caseTitle}</div>
                <div className="text-xs text-slate-600">{hearing.courtName || 'Суд не указан'}{hearing.judgeName ? ` · ${hearing.judgeName}` : ' · судья не указан'}{hearing.courtroom ? ` · зал ${hearing.courtroom}` : ''}</div>
                <div className={`text-xs font-semibold ${hearing.documentsPrepared ? 'text-emerald-700' : 'text-amber-700'}`}>
                  {hearing.documentsPrepared ? 'Контрольная задача подготовки завершена' : 'Контрольная задача «Подготовиться к заседанию» не завершена.'}
                </div>
                <div className={`text-xs font-semibold ${hearing.materialsReviewed ? 'text-emerald-700' : 'text-amber-700'}`}>
                  {hearing.materialsReviewed ? 'Материалы к этому заседанию проверены' : 'Материалы не подтверждены в чек-листе дела.'}
                </div>
                <div className={`text-xs font-semibold ${hearing.positionReviewed ? 'text-emerald-700' : 'text-amber-700'}`}>
                  {hearing.positionReviewed ? 'Позиция по делу проверена' : 'Позиция по делу не проверена — откройте дело и подтвердите её после проверки.'}
                </div>
              </div>
              <button type="button" onClick={() => { dismiss(); onOpenCase(hearing.caseId); }} className="inline-flex items-center justify-center gap-1 rounded-lg border border-blue-200 px-2.5 py-1.5 text-xs font-semibold text-blue-700 hover:bg-blue-50">К делу <ArrowRight className="h-3.5 w-3.5" /></button>
            </div>
          ))}
        </div>
        <div className="flex flex-wrap justify-end gap-2 border-t border-slate-100 p-3">
          <button type="button" onClick={dismiss} className="rounded-lg border border-slate-200 px-3 py-2 text-sm font-medium text-slate-700">Позже</button>
          <button type="button" onClick={() => { dismiss(); onOpenCalendar(); }} className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-3 py-2 text-sm font-semibold text-white hover:bg-blue-700"><CalendarDays className="h-4 w-4" /> {calendarAvailable ? 'В календарь дел' : 'К судебным делам'}</button>
        </div>
      </section>
    </div>
  );
}
