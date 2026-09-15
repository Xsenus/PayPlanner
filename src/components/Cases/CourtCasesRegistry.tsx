import { useEffect, useMemo, useState } from 'react';
import {
  AlertTriangle,
  ArrowRight,
  CalendarClock,
  CheckCircle2,
  CircleDot,
  ExternalLink,
  Gavel,
  Loader2,
  MapPin,
  RefreshCw,
  Scale,
  Search,
  ShieldCheck,
  UserRound,
  UsersRound,
} from 'lucide-react';

import { apiService } from '../../services/api';
import type { ActResponsible, ClientCase, CourtCasesRegistrySummary } from '../../types';
import { useDebouncedValue } from '../../hooks/useDebouncedValue';
import { useUserTimeZone } from '../../hooks/useUserTimeZone';
import { formatDateTimeInTimeZone } from '../../utils/timeZoneDate';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { SystemSelect } from '../ui/SystemSelect';

type CourtState = 'all' | 'upcoming' | 'soon' | 'overdue' | 'without-hearing' | 'under-control';
type CaseScope = 'active' | 'all' | 'mine' | 'archive';
type SortKey = 'nextHearingAt' | 'createdAt' | 'court' | 'title';

interface CourtCasesRegistryProps {
  onOpenCaseCourt: (caseId: number) => void;
}

const EMPTY_SUMMARY: CourtCasesRegistrySummary = {
  total: 0,
  active: 0,
  upcoming: 0,
  soon: 0,
  withoutHearing: 0,
  underControl: 0,
};

const PAGE_SIZE = 12;

function getOwnerName(item: ClientCase) {
  return item.client?.name || item.legalEntity?.shortName || item.legalEntity?.fullName || 'Владелец не указан';
}

function getResponsibleName(item: ClientCase, responsibles: ActResponsible[]) {
  return item.responsibleUser?.fullName
    || item.responsibleUserName
    || responsibles.find((candidate) => candidate.id === item.responsibleUserId)?.fullName
    || 'Ответственный владельца';
}

function formatDateTime(value: string | null | undefined, timeZoneId: string) {
  return value ? formatDateTimeInTimeZone(value, timeZoneId) : 'Дата не назначена';
}

function getHearingState(value: string | null | undefined) {
  if (!value) {
    return {
      label: 'Заседание не назначено',
      hint: 'Добавьте дату или проверьте судебный источник',
      className: 'border-slate-200 bg-slate-50 text-slate-700',
      icon: CalendarClock,
    };
  }

  const diffDays = Math.ceil((new Date(value).getTime() - Date.now()) / 86_400_000);
  if (diffDays < 0) {
    return {
      label: 'Дата заседания прошла',
      hint: 'Проверьте результат и следующее событие',
      className: 'border-rose-200 bg-rose-50 text-rose-800',
      icon: AlertTriangle,
    };
  }

  if (diffDays <= 7) {
    return {
      label: diffDays === 0 ? 'Заседание сегодня' : `До заседания ${diffDays} дн.`,
      hint: 'Требуется проверить подготовку',
      className: 'border-amber-200 bg-amber-50 text-amber-900',
      icon: AlertTriangle,
    };
  }

  return {
    label: `До заседания ${diffDays} дн.`,
    hint: 'Заседание стоит на контроле',
    className: 'border-blue-200 bg-blue-50 text-blue-800',
    icon: CheckCircle2,
  };
}

function getStatusLabel(status: string) {
  if (status === 'Closed') return 'Закрыто';
  if (status === 'Lost') return 'Проиграно';
  if (status === 'OnHold') return 'Приостановлено';
  return 'В работе';
}

function getStatusClass(status: string) {
  if (status === 'Closed') return 'border-emerald-200 bg-emerald-50 text-emerald-700';
  if (status === 'Lost') return 'border-rose-200 bg-rose-50 text-rose-700';
  if (status === 'OnHold') return 'border-amber-200 bg-amber-50 text-amber-800';
  return 'border-blue-200 bg-blue-50 text-blue-700';
}

export function CourtCasesRegistry({ onOpenCaseCourt }: CourtCasesRegistryProps) {
  const timeZoneId = useUserTimeZone();
  const [items, setItems] = useState<ClientCase[]>([]);
  const [filteredTotal, setFilteredTotal] = useState(0);
  const [summary, setSummary] = useState<CourtCasesRegistrySummary>(EMPTY_SUMMARY);
  const [responsibles, setResponsibles] = useState<ActResponsible[]>([]);
  const [search, setSearch] = useState('');
  const debouncedSearch = useDebouncedValue(search.trim(), 300);
  const [scope, setScope] = useState<CaseScope>('active');
  const [status, setStatus] = useState('');
  const [responsibleUserId, setResponsibleUserId] = useState('');
  const [courtState, setCourtState] = useState<CourtState>('all');
  const [sortBy, setSortBy] = useState<SortKey>('nextHearingAt');
  const [page, setPage] = useState(1);
  const [reloadToken, setReloadToken] = useState(0);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    void apiService.getActResponsibles()
      .then((result) => {
        if (!cancelled) setResponsibles(result ?? []);
      })
      .catch(() => {
        if (!cancelled) setResponsibles([]);
      });
    return () => { cancelled = true; };
  }, []);

  useEffect(() => {
    setPage(1);
  }, [debouncedSearch, scope, status, responsibleUserId, courtState, sortBy]);

  useEffect(() => {
    const controller = new AbortController();
    const responsibleId = responsibleUserId ? Number(responsibleUserId) : undefined;
    const common = {
      search: debouncedSearch || undefined,
      status: status || undefined,
      responsibleUserId: responsibleId,
      scope,
    };

    setLoading(true);
    setError(null);
    void Promise.all([
      apiService.getCasesV2({
        ...common,
        courtOnly: true,
        courtState,
        sortBy,
        sortDir: sortBy === 'nextHearingAt' ? 'asc' : 'desc',
        page,
        pageSize: PAGE_SIZE,
      }, { signal: controller.signal }),
      apiService.getCourtCasesRegistrySummary(common, { signal: controller.signal }),
    ])
      .then(([pageResult, summaryResult]) => {
        setItems(pageResult.items ?? []);
        setFilteredTotal(pageResult.total ?? 0);
        setSummary(summaryResult ?? EMPTY_SUMMARY);
      })
      .catch((loadError) => {
        if (controller.signal.aborted) return;
        setItems([]);
        setFilteredTotal(0);
        setSummary(EMPTY_SUMMARY);
        setError(extractDisplayErrorMessage(loadError, 'Не удалось загрузить судебные дела.'));
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false);
      });

    return () => controller.abort();
  }, [debouncedSearch, page, reloadToken, responsibleUserId, scope, sortBy, status, courtState]);

  const pageCount = Math.max(1, Math.ceil(filteredTotal / PAGE_SIZE));
  const cards = useMemo(() => items.map((item) => {
    const hearing = getHearingState(item.nextHearingAt);
    const HearingIcon = hearing.icon;
    return (
      <article key={item.id} className="group flex min-h-[330px] flex-col overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm transition hover:-translate-y-0.5 hover:border-blue-300 hover:shadow-md">
        <div className="border-b border-slate-100 bg-gradient-to-r from-slate-50 to-blue-50/70 px-4 py-3">
          <div className="flex items-start justify-between gap-3">
            <div className="min-w-0">
              <div className="flex flex-wrap items-center gap-2 text-xs font-semibold uppercase tracking-[0.08em] text-slate-500">
                <span>{item.stage || item.caseType || 'Судебное дело'}</span>
                <span className={`rounded-full border px-2 py-0.5 normal-case tracking-normal ${getStatusClass(item.status)}`}>{getStatusLabel(item.status)}</span>
                {item.isUnderControl ? <span className="inline-flex items-center gap-1 rounded-full border border-violet-200 bg-violet-50 px-2 py-0.5 normal-case tracking-normal text-violet-700"><ShieldCheck className="h-3 w-3" /> На контроле</span> : null}
              </div>
              <button type="button" onClick={() => onOpenCaseCourt(item.id)} className="mt-2 line-clamp-2 text-left text-base font-bold leading-snug text-slate-950 hover:text-blue-700 hover:underline">
                {item.title || `Дело #${item.id}`}
              </button>
              <div className="mt-1 flex flex-wrap gap-x-3 gap-y-1 text-xs text-slate-500">
                <span>CRM № {item.caseNumber || item.id}</span>
                {item.courtCaseNumber ? <span className="font-semibold text-slate-700">Суд № {item.courtCaseNumber}</span> : null}
              </div>
            </div>
            <Scale className="h-6 w-6 shrink-0 text-blue-600" />
          </div>
        </div>

        <div className="flex flex-1 flex-col gap-3 p-4">
          <div className="flex items-start gap-2 text-sm">
            <UsersRound className="mt-0.5 h-4 w-4 shrink-0 text-slate-400" />
            <div className="min-w-0"><div className="text-xs text-slate-500">Клиент</div><div className="truncate font-semibold text-slate-800">{getOwnerName(item)}</div></div>
          </div>
          <div className="flex items-start gap-2 text-sm">
            <Gavel className="mt-0.5 h-4 w-4 shrink-0 text-slate-400" />
            <div className="min-w-0"><div className="text-xs text-slate-500">Суд и судья</div><div className="line-clamp-2 font-medium text-slate-800">{item.courtName || 'Суд не указан'}</div>{item.judgeName ? <div className="mt-0.5 truncate text-xs text-slate-500">Судья: {item.judgeName}</div> : null}</div>
          </div>
          {(item.claimant || item.defendant) ? (
            <div className="grid grid-cols-2 gap-2 rounded-lg bg-slate-50 p-2.5 text-xs">
              <div className="min-w-0"><span className="block text-slate-400">Истец</span><span className="line-clamp-2 font-medium text-slate-700">{item.claimant || '—'}</span></div>
              <div className="min-w-0 border-l border-slate-200 pl-2"><span className="block text-slate-400">Ответчик</span><span className="line-clamp-2 font-medium text-slate-700">{item.defendant || '—'}</span></div>
            </div>
          ) : null}
          <div className={`rounded-lg border p-3 ${hearing.className}`}>
            <div className="flex items-start gap-2"><HearingIcon className="mt-0.5 h-4 w-4 shrink-0" /><div><div className="font-bold">{formatDateTime(item.nextHearingAt, timeZoneId)}</div><div className="mt-0.5 text-xs font-medium">{hearing.label}</div><div className="mt-1 text-xs opacity-80">{hearing.hint}{item.courtroom ? ` · Зал ${item.courtroom}` : ''}</div></div></div>
          </div>
          <div className="mt-auto flex flex-wrap items-center justify-between gap-2 border-t border-slate-100 pt-3 text-xs text-slate-500">
            <span className="inline-flex items-center gap-1"><UserRound className="h-3.5 w-3.5" /> {getResponsibleName(item, responsibles)}</span>
            <span>Задачи: {item.openTaskCount ?? 0}{(item.overdueTaskCount ?? 0) > 0 ? ` · просрочено ${item.overdueTaskCount}` : ''}</span>
          </div>
          {item.judicialUid ? <div className="truncate rounded-md bg-cyan-50 px-2 py-1 font-mono text-[11px] font-semibold text-cyan-800" title={item.judicialUid}>UID {item.judicialUid}</div> : null}
          <div className="flex gap-2">
            <button type="button" onClick={() => onOpenCaseCourt(item.id)} className="inline-flex flex-1 items-center justify-center gap-2 rounded-lg bg-blue-600 px-3 py-2 text-sm font-semibold text-white hover:bg-blue-700">
              Открыть судебную карточку <ArrowRight className="h-4 w-4" />
            </button>
            {item.courtUrl ? <a href={item.courtUrl} target="_blank" rel="noreferrer" aria-label="Открыть карточку на сайте суда" className="inline-flex items-center justify-center rounded-lg border border-slate-200 px-3 text-slate-600 hover:border-blue-300 hover:text-blue-700"><ExternalLink className="h-4 w-4" /></a> : null}
          </div>
        </div>
      </article>
    );
  }), [items, onOpenCaseCourt, responsibles, timeZoneId]);

  const resetFilters = () => {
    setSearch('');
    setScope('active');
    setStatus('');
    setResponsibleUserId('');
    setCourtState('all');
    setSortBy('nextHearingAt');
  };

  return (
    <div className="space-y-3 p-3 lg:p-4">
      <section className="overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm">
        <div className="flex flex-col gap-3 bg-gradient-to-r from-slate-950 via-slate-900 to-blue-950 p-4 text-white lg:flex-row lg:items-center lg:justify-between">
          <div>
            <div className="flex items-center gap-2 text-xs font-semibold uppercase tracking-[0.18em] text-blue-200"><Scale className="h-4 w-4" /> Судебная работа</div>
            <h1 className="mt-1 text-2xl font-bold">Судебные дела</h1>
            <p className="mt-1 max-w-3xl text-sm text-slate-300">Карточки судов, сторон, заседаний и ответственных в одном рабочем реестре.</p>
          </div>
          <button type="button" onClick={() => setReloadToken((value) => value + 1)} className="inline-flex items-center justify-center gap-2 rounded-lg border border-white/20 bg-white/10 px-3 py-2 text-sm font-semibold hover:bg-white/20"><RefreshCw className={`h-4 w-4 ${loading ? 'animate-spin' : ''}`} /> Обновить</button>
        </div>

        <div className="grid grid-cols-2 border-b border-slate-200 lg:grid-cols-6">
          {[
            { label: 'Всего судебных', value: summary.total, state: 'all' as CourtState, scope: 'all' as CaseScope, icon: Scale },
            { label: 'Активные', value: summary.active, state: 'all' as CourtState, scope: 'active' as CaseScope, icon: CircleDot },
            { label: 'Заседания впереди', value: summary.upcoming, state: 'upcoming' as CourtState, icon: CalendarClock },
            { label: 'В ближайшие 14 дней', value: summary.soon, state: 'soon' as CourtState, icon: AlertTriangle },
            { label: 'Без даты заседания', value: summary.withoutHearing, state: 'without-hearing' as CourtState, icon: MapPin },
            { label: 'На особом контроле', value: summary.underControl, state: 'under-control' as CourtState, icon: ShieldCheck },
          ].map((metric) => {
            const MetricIcon = metric.icon;
            const active = courtState === metric.state && (!metric.scope || scope === metric.scope);
            return <button key={metric.label} type="button" onClick={() => { setCourtState(metric.state); if (metric.scope) setScope(metric.scope); }} className={`border-r border-t border-slate-100 p-3 text-left transition hover:bg-blue-50 ${active ? 'bg-blue-50 ring-1 ring-inset ring-blue-200' : 'bg-white'}`}><div className="flex items-center justify-between gap-2"><span className="text-xs font-medium text-slate-500">{metric.label}</span><MetricIcon className="h-4 w-4 text-blue-500" /></div><div className="mt-1 text-2xl font-bold text-slate-950">{metric.value}</div></button>;
          })}
        </div>

        <div className="grid gap-2 p-3 lg:grid-cols-[minmax(280px,1fr)_170px_180px_210px_190px_auto]">
          <label className="relative">
            <Search className="pointer-events-none absolute left-3 top-2.5 h-4 w-4 text-slate-400" />
            <input value={search} onChange={(event) => setSearch(event.target.value)} placeholder="Клиент, дело, суд, номер или UID" className="w-full rounded-lg border border-slate-200 py-2 pl-9 pr-3 text-sm outline-none focus:border-blue-400 focus:ring-2 focus:ring-blue-100" />
          </label>
          <SystemSelect aria-label="Область судебных дел" value={scope} onChange={(event) => setScope(event.target.value as CaseScope)} className="rounded-lg border border-slate-200 px-3 py-2 text-sm"><option value="active">Активные дела</option><option value="all">Все дела</option><option value="mine">Мои дела</option><option value="archive">Архив</option></SystemSelect>
          <SystemSelect aria-label="Статус судебного дела" value={status} onChange={(event) => setStatus(event.target.value)} className="rounded-lg border border-slate-200 px-3 py-2 text-sm"><option value="">Все статусы</option><option value="Open">В работе</option><option value="OnHold">Приостановлено</option><option value="Closed">Закрыто</option><option value="Lost">Проиграно</option></SystemSelect>
          <SystemSelect aria-label="Ответственный по судебному делу" value={responsibleUserId} onChange={(event) => setResponsibleUserId(event.target.value)} className="rounded-lg border border-slate-200 px-3 py-2 text-sm"><option value="">Все ответственные</option>{responsibles.map((person) => <option key={person.id} value={person.id}>{person.fullName}</option>)}</SystemSelect>
          <SystemSelect aria-label="Состояние заседания" value={courtState} onChange={(event) => setCourtState(event.target.value as CourtState)} className="rounded-lg border border-slate-200 px-3 py-2 text-sm"><option value="all">Все заседания</option><option value="upcoming">Предстоящие</option><option value="soon">Ближайшие 14 дней</option><option value="overdue">Дата прошла</option><option value="without-hearing">Без даты заседания</option><option value="under-control">На контроле</option></SystemSelect>
          <button type="button" onClick={resetFilters} className="rounded-lg border border-slate-200 px-3 py-2 text-sm font-semibold text-slate-600 hover:bg-slate-50">Сбросить</button>
        </div>
        <div className="flex items-center justify-between gap-3 border-t border-slate-100 px-3 py-2">
          <div className="text-xs text-slate-500">Показано {items.length} из {filteredTotal}</div>
          <SystemSelect aria-label="Сортировка судебных дел" value={sortBy} onChange={(event) => setSortBy(event.target.value as SortKey)} className="rounded-lg border border-slate-200 px-3 py-1.5 text-sm"><option value="nextHearingAt">По ближайшему заседанию</option><option value="createdAt">Сначала новые дела</option><option value="court">По суду</option><option value="title">По названию</option></SystemSelect>
        </div>
      </section>

      {error ? <div role="alert" className="rounded-xl border border-rose-200 bg-rose-50 p-4 text-sm text-rose-800">{error}</div> : null}
      {loading ? <div className="flex min-h-[280px] items-center justify-center rounded-xl border border-slate-200 bg-white"><Loader2 className="h-7 w-7 animate-spin text-blue-600" /><span className="ml-3 text-sm font-medium text-slate-600">Загружаем судебные карточки</span></div> : null}
      {!loading && !error && cards.length === 0 ? <div className="flex min-h-[280px] flex-col items-center justify-center rounded-xl border border-dashed border-slate-300 bg-white p-6 text-center"><Scale className="h-10 w-10 text-slate-300" /><div className="mt-3 font-semibold text-slate-800">Судебных дел по выбранным условиям нет</div><div className="mt-1 max-w-lg text-sm text-slate-500">В раздел попадают дела с судом, судебным номером, UID, инстанцией или судебным событием.</div><button type="button" onClick={resetFilters} className="mt-4 rounded-lg border border-slate-200 px-3 py-2 text-sm font-semibold text-blue-700 hover:bg-blue-50">Показать все судебные дела</button></div> : null}
      {!loading && !error && cards.length > 0 ? <div className="grid gap-3 md:grid-cols-2 2xl:grid-cols-3">{cards}</div> : null}

      {!loading && !error && filteredTotal > 0 ? <div className="flex items-center justify-between rounded-xl border border-slate-200 bg-white px-3 py-2"><button type="button" onClick={() => setPage((value) => Math.max(1, value - 1))} disabled={page <= 1} className="rounded-lg border border-slate-200 px-3 py-2 text-sm font-semibold text-slate-700 disabled:opacity-40">Назад</button><span className="text-sm font-medium text-slate-600">Страница {page} из {pageCount}</span><button type="button" onClick={() => setPage((value) => Math.min(pageCount, value + 1))} disabled={page >= pageCount} className="rounded-lg border border-slate-200 px-3 py-2 text-sm font-semibold text-slate-700 disabled:opacity-40">Вперёд</button></div> : null}
    </div>
  );
}
