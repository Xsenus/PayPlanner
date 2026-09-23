import { useEffect, useState } from 'react';
import { AlertTriangle, Loader2, RefreshCw, X } from 'lucide-react';

import { apiService } from '../../services/api';
import type { CourtSyncHealthCase, CourtSyncHealthPage } from '../../types';
import { useUserTimeZone } from '../../hooks/useUserTimeZone';
import { formatDateTimeInTimeZone } from '../../utils/timeZoneDate';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';

interface Props {
  open: boolean;
  onClose: () => void;
  onOpenCase: (caseId: number) => void;
  onChanged: () => void;
}

const EMPTY_PAGE: CourtSyncHealthPage = { items: [], total: 0, page: 1, pageSize: 50 };

export function CourtSyncHealthDialog({ open, onClose, onOpenCase, onChanged }: Props) {
  const timeZoneId = useUserTimeZone();
  const [status, setStatus] = useState<'failed' | 'paused'>('failed');
  const [page, setPage] = useState(1);
  const [data, setData] = useState<CourtSyncHealthPage>(EMPTY_PAGE);
  const [reloadToken, setReloadToken] = useState(0);
  const [loading, setLoading] = useState(false);
  const [busyCaseId, setBusyCaseId] = useState<number | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [editingCase, setEditingCase] = useState<CourtSyncHealthCase | null>(null);
  const [newNumber, setNewNumber] = useState('');
  const [newUrl, setNewUrl] = useState('');
  const [confirmStopId, setConfirmStopId] = useState<number | null>(null);

  useEffect(() => {
    if (!open) return;
    const controller = new AbortController();
    setLoading(true);
    setError(null);
    setData(EMPTY_PAGE);
    void apiService.getCourtSyncHealth(status, page, { signal: controller.signal })
      .then((result) => {
        if (controller.signal.aborted) return;
        const next = result ?? EMPTY_PAGE;
        if (page > 1 && next.total <= (page - 1) * next.pageSize) setPage(page - 1);
        else setData(next);
      })
      .catch((loadError) => {
        if (!controller.signal.aborted) setError(extractDisplayErrorMessage(loadError, 'Не удалось загрузить состояние судебной синхронизации.'));
      })
      .finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, [open, status, page, reloadToken]);

  if (!open) return null;

  const refresh = () => { setReloadToken((value) => value + 1); onChanged(); };
  const runAction = async (caseId: number, action: () => Promise<void>) => {
    if (busyCaseId !== null) return;
    setBusyCaseId(caseId);
    setError(null);
    setMessage(null);
    try {
      await action();
      refresh();
    } catch (actionError) {
      setError(extractDisplayErrorMessage(actionError, 'Не удалось изменить состояние синхронизации.'));
    } finally {
      setBusyCaseId(null);
    }
  };

  const retry = (item: CourtSyncHealthCase) => void runAction(item.caseId, async () => {
    const result = await apiService.runCaseCourtSync(item.caseId);
    setMessage(result.status === 'external-sync-completed'
      ? `Дело «${item.title}» успешно проверено.`
      : `Проверка дела «${item.title}» не восстановилась: ${result.message}`);
  });

  const saveNumber = () => {
    if (!editingCase || !newNumber.trim() || !newUrl.trim()) return;
    const item = editingCase;
    void runAction(item.caseId, async () => {
      await apiService.updateCourtSyncCaseNumber(item.caseId, newNumber.trim(), newUrl.trim());
      setEditingCase(null);
      const result = await apiService.runCaseCourtSync(item.caseId);
      setMessage(result.status === 'external-sync-completed'
        ? `Номер дела «${item.title}» обновлён, судебный источник проверен.`
        : `Номер обновлён, но проверка пока не восстановилась: ${result.message}`);
    });
  };

  const changeStatus = (nextStatus: 'failed' | 'paused') => {
    setStatus(nextStatus);
    setPage(1);
    setLoading(true);
    setData(EMPTY_PAGE);
    setEditingCase(null);
    setConfirmStopId(null);
    setMessage(null);
  };

  const formatDate = (value: string | null) => value ? formatDateTimeInTimeZone(value, timeZoneId) : 'Нет успешной проверки';
  const pageCount = Math.max(1, Math.ceil(data.total / data.pageSize));

  return <div className="fixed inset-0 z-[1200] flex items-center justify-center bg-slate-950/50 p-3" role="presentation" onMouseDown={(event) => { if (event.currentTarget === event.target && busyCaseId === null) onClose(); }}>
    <section role="dialog" aria-modal="true" aria-labelledby="court-sync-health-title" className="max-h-[90vh] w-full max-w-3xl overflow-y-auto rounded-xl border border-slate-200 bg-white p-3 shadow-2xl">
      <div className="flex items-start justify-between gap-3"><div><h2 id="court-sync-health-title" className="text-lg font-bold text-slate-950">Состояние судебной синхронизации</h2><p className="mt-0.5 text-sm text-slate-600">Дела с непрерывной ошибкой более 24 часов и дела, снятые с автоматического мониторинга.</p></div><button type="button" aria-label="Закрыть состояние судебной синхронизации" onClick={onClose} disabled={busyCaseId !== null} className="rounded-lg p-1.5 text-slate-500 hover:bg-slate-100 disabled:opacity-50"><X className="h-5 w-5" /></button></div>
      <div className="mt-3 flex gap-1 border-b border-slate-200"><button type="button" onClick={() => changeStatus('failed')} className={`border-b-2 px-3 py-2 text-sm font-semibold ${status === 'failed' ? 'border-rose-500 text-rose-700' : 'border-transparent text-slate-600'}`}>Ошибки более суток</button><button type="button" onClick={() => changeStatus('paused')} className={`border-b-2 px-3 py-2 text-sm font-semibold ${status === 'paused' ? 'border-blue-500 text-blue-700' : 'border-transparent text-slate-600'}`}>Без мониторинга</button></div>
      {error ? <div role="alert" className="mt-2 rounded-lg border border-rose-200 bg-rose-50 p-2 text-sm text-rose-800">{error}</div> : null}
      {message ? <div role="status" className="mt-2 rounded-lg border border-blue-200 bg-blue-50 p-2 text-sm text-blue-800">{message}</div> : null}
      {loading ? <div className="flex items-center justify-center gap-2 py-8 text-sm text-slate-600"><Loader2 className="h-4 w-4 animate-spin" /> Загружаем дела</div> : null}
      {!loading && !error && data.items.length === 0 ? <p className="py-8 text-center text-sm text-slate-600">{status === 'failed' ? 'Ошибок синхронизации старше суток нет.' : 'Все доступные судебные дела находятся на мониторинге.'}</p> : null}
      {!loading && data.items.length > 0 ? <div className="mt-2 space-y-2">{data.items.map((item) => <article key={item.caseId} className="rounded-lg border border-slate-200 p-3 text-sm">
        <div className="flex flex-wrap items-start justify-between gap-2"><div><div className="font-semibold text-slate-900">{item.title || `Дело #${item.caseId}`}</div><div className="text-xs text-slate-500">№ {item.caseNumber || 'не указан'} · {item.providerName}</div></div>{status === 'failed' ? <span className="inline-flex items-center gap-1 rounded-full bg-rose-50 px-2 py-1 text-xs font-semibold text-rose-700"><AlertTriangle className="h-3.5 w-3.5" /> Ошибка более суток</span> : <span className="rounded-full bg-slate-100 px-2 py-1 text-xs font-semibold text-slate-600">Остановлено</span>}</div>
        <div className="mt-2 grid gap-1 text-xs text-slate-600 sm:grid-cols-2"><div>Последняя успешная проверка: {formatDate(item.lastSuccessAt)}</div><div>Последняя попытка: {item.lastAttemptAt ? formatDate(item.lastAttemptAt) : 'Нет данных'}</div></div>
        {status === 'failed' ? <p className="mt-2 rounded-md bg-rose-50 p-2 text-xs text-rose-800">Причина: {item.lastError || 'Не установлена'}</p> : null}
        {editingCase?.caseId === item.caseId ? <div className="mt-2 grid gap-2 rounded-lg bg-slate-50 p-2 sm:grid-cols-2"><label className="text-xs font-medium text-slate-700">Новый номер дела<input value={newNumber} onChange={(event) => setNewNumber(event.target.value)} maxLength={100} className="mt-1 w-full rounded-md border border-slate-300 bg-white px-2 py-1.5 text-sm" /></label><label className="text-xs font-medium text-slate-700">Ссылка на карточку суда<input value={newUrl} onChange={(event) => setNewUrl(event.target.value)} maxLength={1000} className="mt-1 w-full rounded-md border border-slate-300 bg-white px-2 py-1.5 text-sm" /></label><div className="flex gap-2 sm:col-span-2"><button type="button" onClick={saveNumber} disabled={busyCaseId !== null || !newNumber.trim() || !newUrl.trim()} className="rounded-md bg-blue-600 px-3 py-1.5 text-xs font-semibold text-white disabled:opacity-50">Сохранить и проверить</button><button type="button" onClick={() => setEditingCase(null)} className="rounded-md border border-slate-300 px-3 py-1.5 text-xs">Отмена</button></div></div> : null}
        {confirmStopId === item.caseId ? <div className="mt-2 flex flex-wrap items-center gap-2 rounded-md bg-amber-50 p-2 text-xs text-amber-900"><span>Фоновая проверка этого дела прекратится.</span><button type="button" onClick={() => void runAction(item.caseId, async () => { await apiService.setCourtSyncMonitoring(item.caseId, false); setConfirmStopId(null); setMessage(`Мониторинг дела «${item.title}» остановлен.`); })} disabled={busyCaseId !== null} className="rounded-md bg-amber-600 px-2 py-1 font-semibold text-white">Подтвердить</button><button type="button" onClick={() => setConfirmStopId(null)} className="underline">Отмена</button></div> : null}
        <div className="mt-2 flex flex-wrap gap-2"><button type="button" onClick={() => onOpenCase(item.caseId)} className="rounded-md border border-slate-300 px-2.5 py-1.5 text-xs font-semibold text-slate-700 hover:bg-slate-50">Открыть дело</button>{status === 'failed' ? <><button type="button" onClick={() => retry(item)} disabled={busyCaseId !== null} className="inline-flex items-center gap-1 rounded-md border border-blue-200 px-2.5 py-1.5 text-xs font-semibold text-blue-700 disabled:opacity-50"><RefreshCw className="h-3.5 w-3.5" /> Повторить синхронизацию</button><button type="button" onClick={() => { setEditingCase(item); setNewNumber(item.caseNumber); setNewUrl(item.courtUrl); setConfirmStopId(null); }} className="rounded-md border border-slate-300 px-2.5 py-1.5 text-xs font-semibold">Указать новый номер</button><button type="button" onClick={() => { setConfirmStopId(item.caseId); setEditingCase(null); }} className="rounded-md border border-amber-200 px-2.5 py-1.5 text-xs font-semibold text-amber-800">Снять с мониторинга</button></> : <button type="button" onClick={() => void runAction(item.caseId, async () => { await apiService.setCourtSyncMonitoring(item.caseId, true); const result = await apiService.runCaseCourtSync(item.caseId); setMessage(result.status === 'external-sync-completed' ? `Мониторинг дела «${item.title}» возобновлён, источник проверен.` : `Мониторинг возобновлён, но проверка пока не удалась: ${result.message}`); })} disabled={busyCaseId !== null} className="rounded-md border border-blue-200 px-2.5 py-1.5 text-xs font-semibold text-blue-700 disabled:opacity-50">Возобновить мониторинг</button>}</div>
      </article>)}</div> : null}
      {data.total > data.pageSize ? <div className="mt-3 flex items-center justify-between text-xs text-slate-600"><button type="button" disabled={page <= 1} onClick={() => setPage((value) => value - 1)} className="rounded-md border border-slate-300 px-3 py-1.5 disabled:opacity-40">Назад</button><span>Страница {page} из {pageCount}</span><button type="button" disabled={page >= pageCount} onClick={() => setPage((value) => value + 1)} className="rounded-md border border-slate-300 px-3 py-1.5 disabled:opacity-40">Вперёд</button></div> : null}
    </section>
  </div>;
}
