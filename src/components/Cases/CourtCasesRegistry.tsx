import { useEffect, useMemo, useRef, useState } from 'react';
import { AlertTriangle, Archive, ArrowRight, BarChart3, CalendarDays, CalendarClock, CheckCircle2, ExternalLink, Folder, FolderOpen, Gavel, Grid2X2, Layers3, Loader2, Plus, RefreshCw, Scale, Search, ShieldCheck, UserRound, UsersRound, X } from 'lucide-react';

import { apiService } from '../../services/api';
import type { ActResponsible, ClientCase, ClientCaseCourtSyncProvider, CourtCasesRegistrySummary } from '../../types';
import { useDebouncedValue } from '../../hooks/useDebouncedValue';
import { useUserTimeZone } from '../../hooks/useUserTimeZone';
import { formatDateTimeInTimeZone } from '../../utils/timeZoneDate';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { SystemSelect } from '../ui/SystemSelect';
import { CounterpartyPickerModal, type CounterpartyKind, type CounterpartySelection } from '../ui/CounterpartyPickerModal';
import { CourtWorkloadDialog } from './CourtWorkloadDialog';
import { CourtSyncHealthDialog } from './CourtSyncHealthDialog';
import { CourtReportOverviewDialog } from './CourtReportOverviewDialog';

type CourtState = 'all' | 'upcoming' | 'soon' | 'overdue' | 'without-hearing' | 'under-control';
type CaseScope = 'active' | 'all' | 'mine' | 'archive';
type SortKey = 'nextHearingAt' | 'createdAt' | 'court' | 'title';
type CourtInstanceFilter = '' | 'first' | 'appeal' | 'cassation' | 'supervision' | 'supreme';

interface CourtCasesRegistryProps {
  onOpenCaseCourt: (caseId: number) => void;
  allowCreate?: boolean;
  allowAssign?: boolean;
  ownerScopes?: CounterpartyKind[];
}

const EMPTY_SUMMARY: CourtCasesRegistrySummary = { total: 0, active: 0, upcoming: 0, soon: 0, withoutHearing: 0, underControl: 0, categories: [] };
const PAGE_SIZE = 18;
const INSTANCE_OPTIONS: Array<{ value: CourtInstanceFilter; label: string }> = [
  { value: '', label: 'Все инстанции' },
  { value: 'first', label: 'Первая' },
  { value: 'appeal', label: 'Апелляция' },
  { value: 'cassation', label: 'Кассация' },
  { value: 'supervision', label: 'Надзор' },
  { value: 'supreme', label: 'Верховный суд' },
];
const INSTANCE_LABELS: Record<string, string> = { first: 'Первая инстанция', appeal: 'Апелляция', cassation: 'Кассация', supervision: 'Надзор', supreme: 'Верховный суд' };

function getOwnerName(item: ClientCase) {
  return item.client?.name || item.legalEntity?.shortName || item.legalEntity?.fullName || 'Владелец не указан';
}

function getResponsibleName(item: ClientCase, responsibles: ActResponsible[]) {
  return item.responsibleUser?.fullName || item.responsibleUserName || responsibles.find((candidate) => candidate.id === item.responsibleUserId)?.fullName || 'Ответственный владельца';
}

function getStatusLabel(status: string) {
  if (status === 'Closed') return 'Завершено';
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

function getHearingState(value: string | null | undefined, outcome?: ClientCase['nextHearingOutcome']) {
  if (!value) return { label: 'Заседание не назначено', className: 'border-slate-200 bg-slate-50 text-slate-600', icon: CalendarClock };
  const diffDays = Math.ceil((new Date(value).getTime() - Date.now()) / 86_400_000);
  if (diffDays < 0 && outcome) return {
    label: outcome === 'postponed' ? 'Итог: заседание отложено' : outcome === 'considered' ? 'Итог: дело рассмотрено по существу' : 'Итог: промежуточное решение',
    className: 'border-emerald-200 bg-emerald-50 text-emerald-800',
    icon: CheckCircle2,
  };
  if (diffDays < 0) return { label: 'Дата прошла — проверьте результат', className: 'border-rose-200 bg-rose-50 text-rose-800', icon: AlertTriangle };
  if (diffDays <= 7) return { label: diffDays === 0 ? 'Заседание сегодня' : `До заседания ${diffDays} дн.`, className: 'border-amber-200 bg-amber-50 text-amber-900', icon: AlertTriangle };
  return { label: `До заседания ${diffDays} дн.`, className: 'border-blue-200 bg-blue-50 text-blue-800', icon: CheckCircle2 };
}

function getPrimaryInstance(item: ClientCase) {
  const keys = item.courtInstanceKeys ?? [];
  const key = ['supreme', 'supervision', 'cassation', 'appeal', 'first'].find((candidate) => keys.includes(candidate)) ?? 'first';
  return INSTANCE_LABELS[key];
}

export function CourtCasesRegistry({ onOpenCaseCourt, allowCreate = false, allowAssign = false, ownerScopes = ['client', 'legalEntity'] }: CourtCasesRegistryProps) {
  const timeZoneId = useUserTimeZone();
  const [items, setItems] = useState<ClientCase[]>([]);
  const [filteredTotal, setFilteredTotal] = useState(0);
  const [summary, setSummary] = useState<CourtCasesRegistrySummary>(EMPTY_SUMMARY);
  const [responsibles, setResponsibles] = useState<ActResponsible[]>([]);
  const [providers, setProviders] = useState<ClientCaseCourtSyncProvider[]>([]);
  const [search, setSearch] = useState('');
  const debouncedSearch = useDebouncedValue(search.trim(), 300);
  const [scope, setScope] = useState<CaseScope>('active');
  const [category, setCategory] = useState('');
  const [instance, setInstance] = useState<CourtInstanceFilter>('');
  const [responsibleUserId, setResponsibleUserId] = useState('');
  const [courtState, setCourtState] = useState<CourtState>('all');
  const [ourCourtRole, setOurCourtRole] = useState('');
  const [sortBy, setSortBy] = useState<SortKey>('nextHearingAt');
  const [viewMode, setViewMode] = useState<'cards' | 'schedule'>('cards');
  const [page, setPage] = useState(1);
  const [reloadToken, setReloadToken] = useState(0);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [ownerPickerOpen, setOwnerPickerOpen] = useState(false);
  const [selectedOwner, setSelectedOwner] = useState<CounterpartySelection | null>(null);
  const [trackingOpen, setTrackingOpen] = useState(false);
  const [trackingTitle, setTrackingTitle] = useState('');
  const [trackingNumber, setTrackingNumber] = useState('');
  const [trackingInstance, setTrackingInstance] = useState<CourtInstanceFilter>('first');
  const [trackingProvider, setTrackingProvider] = useState('');
  const [creating, setCreating] = useState(false);
  const [workloadTarget, setWorkloadTarget] = useState<{ caseId?: number; title?: string } | null>(null);
  const [newlyCreatedCaseId, setNewlyCreatedCaseId] = useState<number | null>(null);
  const [syncHealthOpen, setSyncHealthOpen] = useState(false);
  const [reportOpen, setReportOpen] = useState(false);
  const [syncHealthReloadToken, setSyncHealthReloadToken] = useState(0);
  const [failedSyncCount, setFailedSyncCount] = useState(0);
  const [syncHealthCountError, setSyncHealthCountError] = useState(false);
  const hasAutoOpenedSyncHealth = useRef(false);

  useEffect(() => {
    if (!allowAssign) return;
    const controller = new AbortController();
    void apiService.getCourtSyncHealth('failed', 1, { signal: controller.signal })
      .then((result) => {
        if (controller.signal.aborted) return;
        setFailedSyncCount(result.total ?? 0);
        setSyncHealthCountError(false);
        if (result.total > 0 && !hasAutoOpenedSyncHealth.current) {
          hasAutoOpenedSyncHealth.current = true;
          setSyncHealthOpen(true);
        }
      })
      .catch(() => { if (!controller.signal.aborted) setSyncHealthCountError(true); });
    return () => controller.abort();
  }, [allowAssign, syncHealthReloadToken]);

  useEffect(() => {
    let cancelled = false;
    void Promise.all([apiService.getActResponsibles(), apiService.getCourtSyncProviders()])
      .then(([people, providerRows]) => {
        if (cancelled) return;
        setResponsibles(people ?? []);
        setProviders((providerRows ?? []).filter((provider) => provider.isAvailable || provider.status === 'manual-only'));
      })
      .catch(() => { if (!cancelled) setResponsibles([]); });
    return () => { cancelled = true; };
  }, []);

  useEffect(() => { setPage(1); }, [debouncedSearch, scope, category, instance, responsibleUserId, courtState, ourCourtRole, sortBy]);

  useEffect(() => {
    const controller = new AbortController();
    const common = { search: debouncedSearch || undefined, responsibleUserId: responsibleUserId ? Number(responsibleUserId) : undefined, scope, ourCourtRole: ourCourtRole || undefined };
    setLoading(true);
    setError(null);
    void Promise.all([
      apiService.getCasesV2({ ...common, caseType: category || undefined, courtOnly: true, courtState, courtInstance: instance || undefined, sortBy, sortDir: sortBy === 'nextHearingAt' ? 'asc' : 'desc', page, pageSize: PAGE_SIZE }, { signal: controller.signal }),
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
      .finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, [category, courtState, debouncedSearch, instance, ourCourtRole, page, reloadToken, responsibleUserId, scope, sortBy]);

  const pageCount = Math.max(1, Math.ceil(filteredTotal / PAGE_SIZE));
  const categories = summary.categories ?? [];
  const scheduleGroups = useMemo(() => {
    const groups = new Map<string, ClientCase[]>();
    items.filter((item) => item.nextHearingAt).forEach((item) => {
      const key = new Intl.DateTimeFormat('ru-RU', { dateStyle: 'long', timeZone: timeZoneId }).format(new Date(item.nextHearingAt!));
      groups.set(key, [...(groups.get(key) ?? []), item]);
    });
    return [...groups.entries()];
  }, [items, timeZoneId]);
  const resetFilters = () => { setSearch(''); setScope('active'); setCategory(''); setInstance(''); setResponsibleUserId(''); setCourtState('all'); setOurCourtRole(''); setSortBy('nextHearingAt'); };
  const reportFilters = useMemo(() => ({
    search: debouncedSearch || undefined,
    responsibleUserId: responsibleUserId ? Number(responsibleUserId) : undefined,
    caseType: category || undefined,
    courtState,
    courtInstance: instance || undefined,
    ourCourtRole: ourCourtRole || undefined,
  }), [category, courtState, debouncedSearch, instance, ourCourtRole, responsibleUserId]);

  const startTracking = (owner: CounterpartySelection | null) => {
    setOwnerPickerOpen(false);
    if (!owner) return;
    setSelectedOwner(owner);
    setTrackingTitle(owner.name ? `${owner.name} — судебное дело` : '');
    setTrackingNumber('');
    setTrackingInstance('first');
    setTrackingProvider(providers[0]?.key ?? '');
    setTrackingOpen(true);
  };

  const createTrackedCase = async () => {
    if (!selectedOwner || !trackingTitle.trim() || !trackingNumber.trim() || !trackingInstance) return;
    setCreating(true);
    setError(null);
    try {
      const provider = providers.find((item) => item.key === trackingProvider);
      const created = await apiService.createCaseV2({
        clientId: selectedOwner.kind === 'client' ? selectedOwner.id : null,
        legalEntityId: selectedOwner.kind === 'legalEntity' ? selectedOwner.id : null,
        title: trackingTitle.trim(), description: 'Судебное дело добавлено из рабочего раздела для автоматического контроля.', status: 'Open', caseNumber: '', caseType: 'Судебное дело', stage: INSTANCE_LABELS[trackingInstance], responsibleUserId: null, startDate: new Date().toISOString(), dueDate: null, feeAmount: 0, priority: 'normal', courtName: '', ourCourtRole: '', courtCaseNumber: trackingNumber.trim(), judicialUid: '', courtUrl: '', nextHearingAt: null, claimant: '', defendant: '', thirdParties: '', representatives: '', judgeName: '', courtroom: '', courtSecretaryContacts: '', claimFiledAt: null, courtDecisionAt: null, appealDeadlineAt: null, cassationDeadlineAt: null, motionDeadlineAt: null, objectionDeadlineAt: null, documentsSummary: '', eventHistory: '', strategy: '',
      });
      await apiService.updateCaseCourtInstance(created.id, trackingInstance, { courtCaseNumber: trackingNumber.trim(), notes: provider ? `Источник контроля: ${provider.label}` : 'Источник контроля будет выбран при первой синхронизации.' });
      setTrackingOpen(false);
      setSelectedOwner(null);
      setReloadToken((value) => value + 1);
      if (allowAssign) {
        setNewlyCreatedCaseId(created.id);
        setWorkloadTarget({ caseId: created.id, title: created.title });
      } else {
        onOpenCaseCourt(created.id);
      }
    } catch (createError) {
      setError(extractDisplayErrorMessage(createError, 'Не удалось поставить дело на отслеживание.'));
    } finally {
      setCreating(false);
    }
  };

  const closeWorkload = () => {
    setWorkloadTarget(null);
    if (newlyCreatedCaseId) {
      onOpenCaseCourt(newlyCreatedCaseId);
      setNewlyCreatedCaseId(null);
    }
  };

  const cards = useMemo(() => items.map((item) => {
    const hearing = getHearingState(item.nextHearingAt, item.nextHearingOutcome);
    const HearingIcon = hearing.icon;
    const instanceKeys = item.courtInstanceKeys ?? [];
    return <article key={item.id} className="group flex flex-col overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm transition hover:border-blue-300 hover:shadow-md">
      <div className="border-b border-slate-100 bg-slate-50/80 px-3 py-2.5">
        <div className="flex items-start justify-between gap-2"><div className="min-w-0"><div className="flex flex-wrap items-center gap-1.5 text-[11px] font-semibold"><span className={`rounded-full border px-2 py-0.5 ${getStatusClass(item.status)}`}>{getStatusLabel(item.status)}</span><span className="rounded-full border border-violet-200 bg-violet-50 px-2 py-0.5 text-violet-700">{getPrimaryInstance(item)}</span>{item.isUnderControl ? <span className="inline-flex items-center gap-1 rounded-full border border-rose-200 bg-rose-50 px-2 py-0.5 text-rose-700"><ShieldCheck className="h-3 w-3" /> На контроле</span> : null}</div><button type="button" onClick={() => onOpenCaseCourt(item.id)} className="mt-2 line-clamp-2 text-left text-sm font-bold leading-snug text-slate-950 hover:text-blue-700 hover:underline">{item.title || `Дело #${item.id}`}</button></div><Scale className="h-5 w-5 shrink-0 text-blue-600" /></div>
        <div className="mt-1.5 flex flex-wrap gap-x-2 gap-y-1 text-[11px] text-slate-500"><span>CRM № {item.caseNumber || item.id}</span><span className="font-semibold text-slate-700">Суд № {item.courtCaseNumber || 'не указан'}</span></div>
      </div>
      <div className="flex flex-1 flex-col gap-2.5 p-3">
        <div className="flex items-start gap-2 text-xs"><UsersRound className="mt-0.5 h-3.5 w-3.5 shrink-0 text-slate-400" /><div className="min-w-0"><div className="text-[11px] text-slate-400">Клиент</div><div className="truncate font-semibold text-slate-800">{getOwnerName(item)}</div></div></div>
        <div className="flex items-start gap-2 text-xs"><Gavel className="mt-0.5 h-3.5 w-3.5 shrink-0 text-slate-400" /><div className="min-w-0"><div className="line-clamp-2 font-medium text-slate-800">{item.courtName || 'Суд уточняется'}</div><div className="mt-0.5 truncate text-[11px] text-slate-500">{item.judgeName ? `Судья: ${item.judgeName}` : 'Судья не указан'}</div></div></div>
        <div className={`rounded-lg border px-2.5 py-2 text-xs ${hearing.className}`}><div className="flex items-start gap-2"><HearingIcon className="mt-0.5 h-3.5 w-3.5 shrink-0" /><div><div className="font-bold">{item.nextHearingAt ? formatDateTimeInTimeZone(item.nextHearingAt, timeZoneId) : 'Дата не назначена'}</div><div className="mt-0.5 text-[11px]">{hearing.label}{item.courtroom ? ` · зал ${item.courtroom}` : ''}</div></div></div></div>
        {instanceKeys.length > 1 ? <div className="flex flex-wrap gap-1">{instanceKeys.map((key) => <span key={key} className="rounded bg-slate-100 px-1.5 py-0.5 text-[10px] font-medium text-slate-600">{INSTANCE_LABELS[key] ?? key}</span>)}</div> : null}
        <div className="mt-auto flex items-center justify-between gap-2 border-t border-slate-100 pt-2 text-[11px] text-slate-500"><span className="inline-flex min-w-0 items-center gap-1 truncate"><UserRound className="h-3 w-3 shrink-0" /> {getResponsibleName(item, responsibles)}</span><span className="shrink-0">Задачи {item.openTaskCount ?? 0}{(item.overdueTaskCount ?? 0) > 0 ? ` / !${item.overdueTaskCount}` : ''}</span></div>
        {item.judicialUid ? <div className="truncate rounded-md bg-cyan-50 px-2 py-1 font-mono text-[10px] font-semibold text-cyan-800" title={item.judicialUid}>UID {item.judicialUid}</div> : null}
        {allowAssign && item.status !== 'Closed' && item.status !== 'Lost' ? <button type="button" onClick={() => setWorkloadTarget({ caseId: item.id, title: item.title })} className="rounded-lg border border-blue-200 px-3 py-1.5 text-xs font-semibold text-blue-700 hover:bg-blue-50">Назначить ответственного</button> : null}
        <div className="flex gap-2"><button type="button" onClick={() => onOpenCaseCourt(item.id)} className="inline-flex flex-1 items-center justify-center gap-1.5 rounded-lg bg-blue-600 px-3 py-2 text-xs font-semibold text-white hover:bg-blue-700">Открыть дело <ArrowRight className="h-3.5 w-3.5" /></button>{item.courtUrl ? <a href={item.courtUrl} target="_blank" rel="noreferrer" aria-label="Открыть карточку на сайте суда" className="inline-flex items-center justify-center rounded-lg border border-slate-200 px-2.5 text-slate-600 hover:border-blue-300 hover:text-blue-700"><ExternalLink className="h-4 w-4" /></a> : null}</div>
      </div>
    </article>;
  }), [allowAssign, items, onOpenCaseCourt, responsibles, timeZoneId]);

  return <div className="space-y-3 p-3 lg:p-4">
    <div className="flex flex-wrap justify-end gap-2"><button type="button" onClick={() => setReportOpen(true)} className="inline-flex items-center gap-2 rounded-lg border border-blue-200 bg-white px-3 py-2 text-sm font-semibold text-blue-700 hover:bg-blue-50"><BarChart3 className="h-4 w-4" /> Сводка по делам</button>{allowAssign ? <><button type="button" onClick={() => setSyncHealthOpen(true)} className="inline-flex items-center gap-2 rounded-lg border border-amber-200 bg-white px-3 py-2 text-sm font-semibold text-amber-800 hover:bg-amber-50"><AlertTriangle className="h-4 w-4" /> Синхронизация {failedSyncCount > 0 ? `· ${failedSyncCount}` : ''}{syncHealthCountError ? ' · проверить' : ''}</button><button type="button" onClick={() => setWorkloadTarget({})} className="inline-flex items-center gap-2 rounded-lg border border-blue-200 bg-white px-3 py-2 text-sm font-semibold text-blue-700 hover:bg-blue-50"><UsersRound className="h-4 w-4" /> Распределение нагрузки</button></> : null}</div>
    <section className="rounded-xl border border-slate-200 bg-white p-3 shadow-sm">
      <div className="flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between"><div><div className="flex items-center gap-2 text-xs font-semibold uppercase tracking-[0.14em] text-blue-700"><Scale className="h-4 w-4" /> Судебная работа</div><h1 className="mt-1 text-2xl font-bold text-slate-950">Судебные дела</h1><p className="mt-0.5 text-sm text-slate-500">Инстанции, заседания, движение дела и задачи в одном рабочем разделе.</p></div><div className="flex flex-wrap gap-2"><button type="button" onClick={() => setScope(scope === 'archive' ? 'active' : 'archive')} className={`inline-flex items-center gap-2 rounded-lg border px-3 py-2 text-sm font-semibold ${scope === 'archive' ? 'border-blue-300 bg-blue-50 text-blue-700' : 'border-slate-200 text-slate-700 hover:bg-slate-50'}`}><Archive className="h-4 w-4" /> {scope === 'archive' ? 'Активные' : 'Завершённые'}</button><button type="button" onClick={() => setReloadToken((value) => value + 1)} className="inline-flex items-center gap-2 rounded-lg border border-slate-200 px-3 py-2 text-sm font-semibold text-slate-700 hover:bg-slate-50"><RefreshCw className={`h-4 w-4 ${loading ? 'animate-spin' : ''}`} /> Обновить</button>{allowCreate ? <button type="button" onClick={() => setOwnerPickerOpen(true)} className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-3 py-2 text-sm font-semibold text-white hover:bg-blue-700"><Plus className="h-4 w-4" /> Поставить дело на отслеживание</button> : null}</div></div>
      <div className="mt-3 grid gap-2 lg:grid-cols-[minmax(260px,1fr)_180px_190px_210px_auto]"><label className="relative"><Search className="pointer-events-none absolute left-3 top-2.5 h-4 w-4 text-slate-400" /><input value={search} onChange={(event) => setSearch(event.target.value)} placeholder="Название, клиент, суд, номер или UID" className="w-full rounded-lg border border-slate-200 py-2 pl-9 pr-3 text-sm outline-none focus:border-blue-400 focus:ring-2 focus:ring-blue-100" /></label><SystemSelect aria-label="Судебная инстанция" value={instance} onChange={(event) => setInstance(event.target.value as CourtInstanceFilter)} className="rounded-lg border border-slate-200 px-3 py-2 text-sm">{INSTANCE_OPTIONS.map((option) => <option key={option.value || 'all'} value={option.value}>{option.label}</option>)}</SystemSelect><SystemSelect aria-label="Состояние заседания" value={courtState} onChange={(event) => setCourtState(event.target.value as CourtState)} className="rounded-lg border border-slate-200 px-3 py-2 text-sm"><option value="all">Все заседания</option><option value="upcoming">Предстоящие</option><option value="soon">Ближайшие 14 дней</option><option value="overdue">Дата прошла</option><option value="without-hearing">Без даты заседания</option><option value="under-control">На контроле</option></SystemSelect><SystemSelect aria-label="Ответственный по судебному делу" value={responsibleUserId} onChange={(event) => setResponsibleUserId(event.target.value)} className="rounded-lg border border-slate-200 px-3 py-2 text-sm"><option value="">Все ответственные</option>{responsibles.map((person) => <option key={person.id} value={person.id}>{person.fullName}</option>)}</SystemSelect><button type="button" onClick={resetFilters} className="rounded-lg border border-slate-200 px-3 py-2 text-sm font-semibold text-slate-600 hover:bg-slate-50">Сбросить</button></div>
      <div className="mt-2 flex flex-wrap items-center gap-2">
        <label htmlFor="court-our-role" className="text-xs font-semibold text-slate-600">Наша роль в деле</label>
        <SystemSelect id="court-our-role" aria-label="Наша роль в деле" value={ourCourtRole} onChange={(event) => setOurCourtRole(event.target.value)} className="rounded-lg border border-slate-200 px-3 py-2 text-sm">
          <option value="">Все роли</option><option value="claimant">Истец</option><option value="respondent">Ответчик</option><option value="third-party">Третье лицо</option><option value="unknown">Не указана</option>
        </SystemSelect>
      </div>
    </section>

    <div className="grid gap-3 xl:grid-cols-[220px_minmax(0,1fr)]"><aside className="h-fit rounded-xl border border-slate-200 bg-white p-2 shadow-sm xl:sticky xl:top-3"><div className="flex items-center gap-2 px-2 py-2 text-xs font-semibold uppercase tracking-[0.12em] text-slate-500"><FolderOpen className="h-4 w-4 text-blue-600" /> Разделы дел</div><button type="button" onClick={() => setCategory('')} className={`flex w-full items-center justify-between rounded-lg px-2.5 py-2 text-left text-sm ${category === '' ? 'bg-blue-50 font-semibold text-blue-700' : 'text-slate-700 hover:bg-slate-50'}`}><span className="inline-flex items-center gap-2"><Layers3 className="h-4 w-4" /> Все дела</span><span>{summary.total}</span></button>{categories.filter((entry) => entry.name !== 'Без категории').map((entry) => <button key={entry.name} type="button" onClick={() => setCategory(entry.name)} className={`mt-1 flex w-full items-center justify-between rounded-lg px-2.5 py-2 text-left text-sm ${category === entry.name ? 'bg-blue-50 font-semibold text-blue-700' : 'text-slate-700 hover:bg-slate-50'}`}><span className="inline-flex min-w-0 items-center gap-2"><Folder className="h-4 w-4 shrink-0" /><span className="truncate">{entry.name}</span></span><span className="ml-2 text-xs text-slate-500">{entry.count}</span></button>)}<div className="mt-2 border-t border-slate-100 pt-2"><button type="button" onClick={() => setCourtState('under-control')} className={`flex w-full items-center justify-between rounded-lg px-2.5 py-2 text-left text-sm ${courtState === 'under-control' ? 'bg-rose-50 font-semibold text-rose-700' : 'text-slate-700 hover:bg-slate-50'}`}><span className="inline-flex items-center gap-2"><ShieldCheck className="h-4 w-4" /> На контроле</span><span>{summary.underControl}</span></button></div></aside>
      <main className="min-w-0"><div className="mb-2 flex flex-wrap items-center justify-between gap-2 px-1"><div className="text-sm font-medium text-slate-700">{scope === 'archive' ? 'Завершённые дела' : 'Активные дела'} · {filteredTotal}</div><div className="flex flex-wrap items-center gap-2"><div className="flex rounded-lg border border-slate-200 bg-white p-0.5"><button type="button" onClick={() => setViewMode('cards')} className={`inline-flex items-center gap-1.5 rounded-md px-2.5 py-1.5 text-xs font-semibold ${viewMode === 'cards' ? 'bg-blue-50 text-blue-700' : 'text-slate-600'}`}><Grid2X2 className="h-3.5 w-3.5" /> Карточки</button><button type="button" onClick={() => setViewMode('schedule')} className={`inline-flex items-center gap-1.5 rounded-md px-2.5 py-1.5 text-xs font-semibold ${viewMode === 'schedule' ? 'bg-blue-50 text-blue-700' : 'text-slate-600'}`}><CalendarDays className="h-3.5 w-3.5" /> Расписание</button></div><SystemSelect aria-label="Сортировка судебных дел" value={sortBy} onChange={(event) => setSortBy(event.target.value as SortKey)} className="rounded-lg border border-slate-200 bg-white px-3 py-1.5 text-sm"><option value="nextHearingAt">По ближайшему заседанию</option><option value="createdAt">Сначала новые</option><option value="court">По суду</option><option value="title">По названию</option></SystemSelect></div></div>{error ? <div role="alert" className="rounded-xl border border-rose-200 bg-rose-50 p-4 text-sm text-rose-800">{error}</div> : null}{loading ? <div className="flex min-h-[260px] items-center justify-center rounded-xl border border-slate-200 bg-white"><Loader2 className="h-7 w-7 animate-spin text-blue-600" /><span className="ml-3 text-sm font-medium text-slate-600">Загружаем судебные карточки</span></div> : null}{!loading && !error && cards.length === 0 ? <div className="flex min-h-[260px] flex-col items-center justify-center rounded-xl border border-dashed border-slate-300 bg-white p-6 text-center"><Scale className="h-10 w-10 text-slate-300" /><div className="mt-3 font-semibold text-slate-800">Судебных дел по выбранным условиям нет</div><div className="mt-1 max-w-lg text-sm text-slate-500">Измените фильтры или поставьте новое дело на отслеживание.</div><button type="button" onClick={resetFilters} className="mt-4 rounded-lg border border-slate-200 px-3 py-2 text-sm font-semibold text-blue-700 hover:bg-blue-50">Показать активные дела</button></div> : null}{!loading && !error && cards.length > 0 && viewMode === 'cards' ? <div className="grid gap-3 md:grid-cols-2 2xl:grid-cols-3">{cards}</div> : null}{!loading && !error && cards.length > 0 && viewMode === 'schedule' ? <div className="space-y-3">{scheduleGroups.length === 0 ? <div className="rounded-xl border border-dashed border-slate-300 bg-white p-6 text-center text-sm text-slate-500">В выбранных делах нет назначенных заседаний.</div> : scheduleGroups.map(([date, dateItems]) => <section key={date} className="overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm"><div className="border-b border-blue-100 bg-blue-50 px-3 py-2 text-sm font-bold capitalize text-blue-900">{date}</div><div className="divide-y divide-slate-100">{dateItems.map((item) => <button key={item.id} type="button" onClick={() => onOpenCaseCourt(item.id)} className="grid w-full gap-2 px-3 py-3 text-left hover:bg-slate-50 sm:grid-cols-[90px_minmax(0,1fr)_minmax(180px,0.7fr)_auto]"><div className="text-sm font-bold text-blue-700">{new Intl.DateTimeFormat('ru-RU', { hour: '2-digit', minute: '2-digit', timeZone: timeZoneId }).format(new Date(item.nextHearingAt!))}</div><div className="min-w-0"><div className="truncate text-sm font-bold text-slate-900">{item.title}</div><div className="mt-0.5 text-xs text-slate-500">№ {item.courtCaseNumber || item.caseNumber || item.id}</div></div><div className="min-w-0 text-xs text-slate-600"><div className="truncate font-semibold">{item.courtName || 'Суд не указан'}</div><div className="mt-0.5 truncate">{item.judgeName ? `Судья: ${item.judgeName}` : 'Судья не указан'}</div></div><ArrowRight className="h-4 w-4 self-center text-slate-400" /></button>)}</div></section>)}</div> : null}{!loading && !error && filteredTotal > PAGE_SIZE ? <div className="mt-3 flex items-center justify-between rounded-xl border border-slate-200 bg-white px-3 py-2"><button type="button" onClick={() => setPage((value) => Math.max(1, value - 1))} disabled={page <= 1} className="rounded-lg border border-slate-200 px-3 py-2 text-sm font-semibold text-slate-700 disabled:opacity-40">Назад</button><span className="text-sm font-medium text-slate-600">Страница {page} из {pageCount}</span><button type="button" onClick={() => setPage((value) => Math.min(pageCount, value + 1))} disabled={page >= pageCount} className="rounded-lg border border-slate-200 px-3 py-2 text-sm font-semibold text-slate-700 disabled:opacity-40">Вперёд</button></div> : null}</main></div>

    <CounterpartyPickerModal open={ownerPickerOpen} title="Выберите владельца судебного дела" counterpartyRole="client" allowedScopes={ownerScopes} onClose={() => setOwnerPickerOpen(false)} onSelect={startTracking} />
    <CourtWorkloadDialog open={workloadTarget !== null} caseId={workloadTarget?.caseId} caseTitle={workloadTarget?.title} onClose={closeWorkload} onAssigned={() => { setReloadToken((value) => value + 1); closeWorkload(); }} />
    <CourtSyncHealthDialog open={syncHealthOpen} onClose={() => setSyncHealthOpen(false)} onOpenCase={(caseId) => { setSyncHealthOpen(false); onOpenCaseCourt(caseId); }} onChanged={() => { setSyncHealthReloadToken((value) => value + 1); setReloadToken((value) => value + 1); }} />
    {reportOpen ? <CourtReportOverviewDialog filters={reportFilters} onClose={() => setReportOpen(false)} /> : null}
    {trackingOpen ? <div className="fixed inset-0 z-[1200] flex items-center justify-center bg-slate-950/50 p-4" role="presentation" onMouseDown={(event) => { if (event.currentTarget === event.target && !creating) setTrackingOpen(false); }}><section role="dialog" aria-modal="true" aria-labelledby="court-tracking-title" className="w-full max-w-xl rounded-2xl border border-slate-200 bg-white p-4 shadow-2xl"><div className="flex items-start justify-between gap-3"><div><h2 id="court-tracking-title" className="text-lg font-bold text-slate-950">Поставить дело на отслеживание</h2><p className="mt-1 text-sm text-slate-500">{allowAssign ? 'После создания можно назначить ответственного с учётом нагрузки, затем откроется судебная карточка.' : 'После создания откроется судебная карточка для поиска в источнике и проверки найденных данных.'}</p></div><button type="button" aria-label="Закрыть" onClick={() => setTrackingOpen(false)} disabled={creating} className="rounded-lg p-2 text-slate-500 hover:bg-slate-100"><X className="h-5 w-5" /></button></div><div className="mt-4 rounded-lg bg-blue-50 px-3 py-2 text-sm text-blue-900"><span className="font-semibold">Владелец:</span> {selectedOwner?.name}</div><div className="mt-4 grid gap-3"><label className="text-sm font-medium text-slate-700">Источник судебных данных<SystemSelect value={trackingProvider} onChange={(event) => setTrackingProvider(event.target.value)} className="mt-1 w-full rounded-lg border border-slate-200 px-3 py-2"><option value="">Определить при синхронизации</option>{providers.map((provider) => <option key={provider.key} value={provider.key}>{provider.label}</option>)}</SystemSelect></label><label className="text-sm font-medium text-slate-700">Название дела *<input value={trackingTitle} onChange={(event) => setTrackingTitle(event.target.value)} className="mt-1 w-full rounded-lg border border-slate-200 px-3 py-2" placeholder="Иванов — раздел имущества" /></label><div className="grid gap-3 sm:grid-cols-2"><label className="text-sm font-medium text-slate-700">Номер дела в суде *<input value={trackingNumber} onChange={(event) => setTrackingNumber(event.target.value)} className="mt-1 w-full rounded-lg border border-slate-200 px-3 py-2" placeholder="А55-12345/2026" /></label><label className="text-sm font-medium text-slate-700">Инстанция *<SystemSelect value={trackingInstance} onChange={(event) => setTrackingInstance(event.target.value as CourtInstanceFilter)} className="mt-1 w-full rounded-lg border border-slate-200 px-3 py-2">{INSTANCE_OPTIONS.filter((option) => option.value).map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}</SystemSelect></label></div></div><div className="mt-4 flex justify-end gap-2"><button type="button" onClick={() => setTrackingOpen(false)} disabled={creating} className="rounded-lg border border-slate-200 px-4 py-2 text-sm font-semibold text-slate-700">Отмена</button><button type="button" onClick={() => void createTrackedCase()} disabled={creating || !trackingTitle.trim() || !trackingNumber.trim() || !trackingInstance} className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-4 py-2 text-sm font-semibold text-white disabled:opacity-50">{creating ? <Loader2 className="h-4 w-4 animate-spin" /> : <Plus className="h-4 w-4" />} Поставить на отслеживание</button></div></section></div> : null}
  </div>;
}
