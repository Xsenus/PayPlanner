import { useEffect, useMemo, useState } from 'react';
import { AlertTriangle, CalendarDays, CheckCircle2, ChevronLeft, ChevronRight, Loader2 } from 'lucide-react';

import { apiService } from '../../services/api';
import type { CaseCalendarEvent } from '../../types';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { getZonedParts, toTimeZoneDateKey } from '../../utils/timeZoneDate';

type Period = 'day' | 'week' | 'month';

function utcDate(key: string) {
  return new Date(`${key}T12:00:00Z`);
}

function dateKey(date: Date) {
  return date.toISOString().slice(0, 10);
}

function addDays(key: string, days: number) {
  const date = utcDate(key);
  date.setUTCDate(date.getUTCDate() + days);
  return dateKey(date);
}

function periodRange(anchor: string, period: Period) {
  const date = utcDate(anchor);
  if (period === 'day') return { from: anchor, to: anchor };
  if (period === 'week') {
    const weekday = (date.getUTCDay() + 6) % 7;
    const from = addDays(anchor, -weekday);
    return { from, to: addDays(from, 6) };
  }
  const from = `${anchor.slice(0, 7)}-01`;
  const to = dateKey(new Date(Date.UTC(date.getUTCFullYear(), date.getUTCMonth() + 1, 0, 12)));
  return { from, to };
}

function shiftPeriod(anchor: string, period: Period, direction: number) {
  if (period === 'day') return addDays(anchor, direction);
  if (period === 'week') return addDays(anchor, direction * 7);
  const date = utcDate(anchor);
  // Keep the anchor at the first of the month, so a 31st never skips February.
  return dateKey(new Date(Date.UTC(date.getUTCFullYear(), date.getUTCMonth() + direction, 1, 12)));
}

function periodDays(from: string, to: string) {
  const days: string[] = [];
  for (let day = from; day <= to; day = addDays(day, 1)) days.push(day);
  return days;
}

function getEventDay(event: CaseCalendarEvent, timeZoneId: string) {
  return event.allDay ? event.startAt.slice(0, 10) : toTimeZoneDateKey(event.startAt, timeZoneId);
}

function getReadiness(event: CaseCalendarEvent) {
  if (event.status === 'postponed') return { label: 'Перенесено', className: 'border-slate-200 bg-slate-100 text-slate-600' };
  if (event.status === 'held' || event.status === 'completed') return { label: 'Состоялось', className: 'border-slate-200 bg-slate-100 text-slate-600' };
  if (event.documentsPrepared === true) return { label: 'Документы подготовлены', className: 'border-emerald-200 bg-emerald-50 text-emerald-800' };
  return { label: 'Требуется внимание', className: 'border-amber-200 bg-amber-50 text-amber-900' };
}

function formatDay(key: string, options: Intl.DateTimeFormatOptions) {
  return new Intl.DateTimeFormat('ru-RU', { ...options, timeZone: 'UTC' }).format(utcDate(key));
}

function formatTime(event: CaseCalendarEvent, timeZoneId: string) {
  if (event.allDay) return 'Весь день';
  const parts = getZonedParts(event.startAt, timeZoneId);
  return parts ? `${String(parts.hour).padStart(2, '0')}:${String(parts.minute).padStart(2, '0')}` : 'Время не указано';
}

function formatSourceCheck(event: CaseCalendarEvent, timeZoneId: string) {
  if (!event.sourceCheckedAt) return null;
  const checkedAt = new Date(event.sourceCheckedAt);
  if (Number.isNaN(checkedAt.getTime())) return null;
  return `Источник проверен ${new Intl.DateTimeFormat('ru-RU', { day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit', timeZone: timeZoneId }).format(checkedAt)}`;
}

function hearingCountLabel(count: number) {
  const value = count % 100;
  const ending = count % 10;
  return `${count} ${value >= 11 && value <= 14 ? 'заседаний' : ending === 1 ? 'заседание' : ending >= 2 && ending <= 4 ? 'заседания' : 'заседаний'}`;
}

interface CourtHearingCalendarProps {
  timeZoneId: string;
  responsibleUserId?: number;
  filters?: {
    search?: string;
    scope?: string;
    caseType?: string;
    courtState?: string;
    courtInstance?: string;
    ourCourtRole?: string;
    minClaimAmount?: number;
    maxClaimAmount?: number;
  };
  filterError?: string | null;
  reloadToken: number;
  onOpenCase: (caseId: number) => void;
}

export function CourtHearingCalendar({ timeZoneId, responsibleUserId, filters, filterError, reloadToken, onOpenCase }: CourtHearingCalendarProps) {
  const today = toTimeZoneDateKey(new Date(), timeZoneId);
  const [period, setPeriod] = useState<Period>('week');
  const [anchor, setAnchor] = useState(today);
  const [selectedDay, setSelectedDay] = useState(today);
  const [events, setEvents] = useState<CaseCalendarEvent[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const range = useMemo(() => periodRange(anchor, period), [anchor, period]);
  const { search, scope, caseType, courtState, courtInstance, ourCourtRole, minClaimAmount, maxClaimAmount } = filters ?? {};

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);
    if (filterError) {
      setLoading(false);
      setError(filterError);
      return () => { cancelled = true; };
    }
    void apiService.getCaseCalendarEvents({ from: range.from, to: range.to, responsibleUserId, includeTasks: false,
      search, scope, caseType, courtState, courtInstance, ourCourtRole, minClaimAmount, maxClaimAmount })
      .then((result) => {
        if (cancelled) return;
        setEvents((result ?? []).filter((item) => item.kind === 'court' && item.sourceType === 'hearing'));
      })
      .catch((loadError) => {
        if (cancelled) return;
        setEvents([]);
        setError(extractDisplayErrorMessage(loadError, 'Не удалось загрузить заседания.'));
      })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [range.from, range.to, responsibleUserId, search, scope, caseType, courtState, courtInstance, ourCourtRole, minClaimAmount, maxClaimAmount, filterError, reloadToken]);

  const eventsByDay = useMemo(() => {
    const grouped = new Map<string, CaseCalendarEvent[]>();
    events.forEach((event) => {
      const key = getEventDay(event, timeZoneId);
      if (!grouped.has(key)) grouped.set(key, []);
      grouped.get(key)!.push(event);
    });
    return grouped;
  }, [events, timeZoneId]);
  const days = useMemo(() => periodDays(range.from, range.to), [range.from, range.to]);
  const mobileDays = useMemo(() => days.filter((day) => (eventsByDay.get(day)?.length ?? 0) > 0), [days, eventsByDay]);
  const monthOffset = period === 'month' ? (utcDate(range.from).getUTCDay() + 6) % 7 : 0;
  const dayEvents = eventsByDay.get(selectedDay) ?? [];
  const preparedCount = events.filter((event) => event.documentsPrepared === true && event.status !== 'postponed').length;
  const attentionCount = events.filter((event) => getReadiness(event).label === 'Требуется внимание').length;
  const heading = period === 'day'
    ? formatDay(range.from, { day: 'numeric', month: 'long', year: 'numeric' })
    : period === 'week'
      ? `${formatDay(range.from, { day: 'numeric', month: 'short' })} — ${formatDay(range.to, { day: 'numeric', month: 'short', year: 'numeric' })}`
      : formatDay(range.from, { month: 'long', year: 'numeric' });

  return <section aria-label="Календарь судебных заседаний" className="rounded-xl border border-slate-200 bg-white p-3 shadow-sm">
    <div className="flex flex-wrap items-center justify-between gap-2">
      <div><h2 className="flex items-center gap-2 text-base font-bold text-slate-900"><CalendarDays className="h-4 w-4 text-blue-600" /> Заседания</h2><p className="text-xs text-slate-500">Доступные заседания за период с учётом фильтров судебного реестра.</p></div>
      <div className="flex items-center gap-1 rounded-lg border border-slate-200 p-0.5">{(['day', 'week', 'month'] as const).map((value) => <button key={value} type="button" onClick={() => { setAnchor(selectedDay); setPeriod(value); }} className={`rounded-md px-2.5 py-1.5 text-xs font-semibold ${period === value ? 'bg-blue-50 text-blue-700' : 'text-slate-600 hover:bg-slate-50'}`}>{value === 'day' ? 'День' : value === 'week' ? 'Неделя' : 'Месяц'}</button>)}</div>
    </div>
    <div className="mt-3 flex flex-wrap items-center justify-between gap-2"><div className="flex items-center gap-1"><button type="button" aria-label="Предыдущий период" onClick={() => { const next = shiftPeriod(anchor, period, -1); setAnchor(next); setSelectedDay(next); }} className="rounded-lg border border-slate-200 p-1.5 hover:bg-slate-50"><ChevronLeft className="h-4 w-4" /></button><button type="button" onClick={() => { setAnchor(today); setSelectedDay(today); }} className="rounded-lg border border-slate-200 px-2.5 py-1.5 text-xs font-semibold hover:bg-slate-50">Сегодня</button><button type="button" aria-label="Следующий период" onClick={() => { const next = shiftPeriod(anchor, period, 1); setAnchor(next); setSelectedDay(next); }} className="rounded-lg border border-slate-200 p-1.5 hover:bg-slate-50"><ChevronRight className="h-4 w-4" /></button><span className="ml-2 text-sm font-semibold text-slate-800">{heading[0].toUpperCase() + heading.slice(1)}</span></div><div className="flex gap-1.5 text-xs"><span className="rounded-full bg-emerald-50 px-2 py-1 text-emerald-800">Подготовлено: {preparedCount}</span><span className="rounded-full bg-amber-50 px-2 py-1 text-amber-900">Внимание: {attentionCount}</span></div></div>
    {error ? <div role="alert" className="mt-3 rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-800">{error}</div> : null}
    {loading ? <div className="flex items-center gap-2 py-6 text-sm text-slate-500"><Loader2 className="h-4 w-4 animate-spin" /> Загружаем заседания...</div> : null}
    {!loading && !error ? <>
      <div aria-label="Расписание заседаний по датам" className="mt-3 space-y-2 sm:hidden">
        {mobileDays.length === 0 ? <p className="rounded-lg border border-slate-200 px-3 py-2 text-sm text-slate-500">{period === 'day' ? 'На выбранный день заседаний нет.' : period === 'week' ? 'На этой неделе заседаний нет.' : 'В этом месяце заседаний нет.'}</p> : mobileDays.map((day) => {
          const dayRows = eventsByDay.get(day) ?? [];
          return <div key={day} className="rounded-lg border border-slate-200 p-2">
            <div className="flex items-center justify-between gap-2 border-b border-slate-100 pb-1.5 text-sm font-semibold text-slate-800"><span>{formatDay(day, { weekday: 'short', day: 'numeric', month: 'long' })}</span><span className="text-xs font-normal text-slate-500">{hearingCountLabel(dayRows.length)}</span></div>
            <div className="space-y-1.5 pt-2">{dayRows.map((event) => { const readiness = getReadiness(event); const sourceCheck = formatSourceCheck(event, timeZoneId); return <button key={event.id} type="button" onClick={() => onOpenCase(event.clientCaseId)} className="flex w-full items-start gap-2 rounded-md border border-slate-100 p-2 text-left hover:border-blue-300 hover:bg-blue-50"><span className="w-10 shrink-0 text-xs font-bold text-blue-700">{formatTime(event, timeZoneId)}</span><span className="min-w-0 flex-1"><span className="block break-words text-sm font-semibold text-slate-900">{event.caseTitle}</span><span className="block text-xs text-slate-500">{event.courtName || 'Суд не указан'}{event.courtCaseNumber ? ` · № ${event.courtCaseNumber}` : ''}</span><span className={`mt-1 inline-flex rounded-full border px-2 py-0.5 text-[11px] font-medium ${readiness.className}`}>{readiness.label}</span>{sourceCheck ? <span className="mt-1 block text-[11px] text-slate-500">{sourceCheck}</span> : null}</span></button>; })}</div>
          </div>;
        })}
      </div>
      <div className="mt-3 hidden overflow-x-auto sm:block"><div className={`grid gap-1.5 ${period === 'day' ? 'grid-cols-1' : 'min-w-[700px] grid-cols-7'}`}>
        {period === 'month' ? ['Пн', 'Вт', 'Ср', 'Чт', 'Пт', 'Сб', 'Вс'].map((weekday) => <div key={weekday} className="px-2 text-xs font-semibold text-slate-500">{weekday}</div>) : null}
        {Array.from({ length: monthOffset }, (_, index) => <div key={`pad-${index}`} aria-hidden="true" />)}
        {days.map((day) => {
          const dayRows = eventsByDay.get(day) ?? [];
          return <button key={day} type="button" onClick={() => setSelectedDay(day)} aria-label={`${formatDay(day, { weekday: 'long', day: 'numeric', month: 'long' })}, ${hearingCountLabel(dayRows.length)}`} className={`${period === 'month' ? 'min-h-16' : 'min-h-24'} rounded-lg border p-2 text-left transition hover:border-blue-300 ${selectedDay === day ? 'border-blue-400 bg-blue-50' : 'border-slate-200 bg-white'}`}><span className="text-xs font-bold text-slate-700">{formatDay(day, { weekday: period === 'month' ? undefined : 'short', day: 'numeric', month: period === 'day' ? 'long' : undefined })}</span><div className="mt-1 space-y-1">{dayRows.slice(0, period === 'month' ? 2 : 4).map((event) => <div key={event.id} className={`truncate rounded border px-1.5 py-0.5 text-[10px] font-medium ${getReadiness(event).className}`} title={`${formatTime(event, timeZoneId)} · ${event.caseTitle} · ${getReadiness(event).label}`}>{formatTime(event, timeZoneId)} · {event.caseTitle}</div>)}{dayRows.length > (period === 'month' ? 2 : 4) ? <div className="text-[10px] font-semibold text-blue-700">Ещё {dayRows.length - (period === 'month' ? 2 : 4)}</div> : null}</div></button>;
        })}
      </div></div>
      <div className="mt-3 hidden border-t border-slate-100 pt-3 sm:block"><h3 className="text-sm font-bold text-slate-800">{formatDay(selectedDay, { day: 'numeric', month: 'long', year: 'numeric' })} · {hearingCountLabel(dayEvents.length)}</h3>{dayEvents.length === 0 ? <p className="mt-2 text-sm text-slate-500">Заседаний на этот день нет.</p> : <div className="mt-2 space-y-1.5">{dayEvents.map((event) => { const readiness = getReadiness(event); const sourceCheck = formatSourceCheck(event, timeZoneId); return <button key={event.id} type="button" onClick={() => onOpenCase(event.clientCaseId)} className="flex w-full flex-wrap items-center gap-2 rounded-lg border border-slate-200 px-3 py-2 text-left hover:border-blue-300 hover:bg-blue-50"><span className="w-16 shrink-0 text-xs font-bold text-blue-700">{formatTime(event, timeZoneId)}</span><span className="min-w-0 flex-1 text-sm font-semibold text-slate-900">{event.caseTitle}<span className="ml-2 font-normal text-slate-500">{event.courtName || 'Суд не указан'}{event.courtCaseNumber ? ` · № ${event.courtCaseNumber}` : ''}</span>{sourceCheck ? <span className="block text-[11px] font-normal text-slate-500">{sourceCheck}</span> : null}</span><span className={`inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-xs font-medium ${readiness.className}`}>{event.documentsPrepared === true ? <CheckCircle2 className="h-3 w-3" /> : readiness.label === 'Требуется внимание' ? <AlertTriangle className="h-3 w-3" /> : null}{readiness.label}</span></button>; })}</div>}</div>
    </> : null}
  </section>;
}
