import { useEffect, useState } from 'react';
import { AlertTriangle, Loader2, X } from 'lucide-react';

import { apiService } from '../../services/api';
import type { CourtWorkload } from '../../types';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';

interface Props {
  open: boolean;
  caseId?: number;
  caseTitle?: string;
  onClose: () => void;
  onAssigned: () => void;
}

function loadLevel(row: CourtWorkload) {
  const score = row.activeCases + row.hearingsNextSevenDays * 2 + row.overdueCourtTasks * 2;
  if (score > 10) return { label: 'Высокая', className: 'border-rose-200 bg-rose-50 text-rose-700' };
  if (score > 5) return { label: 'Средняя', className: 'border-amber-200 bg-amber-50 text-amber-800' };
  return { label: 'Низкая', className: 'border-emerald-200 bg-emerald-50 text-emerald-700' };
}

export function CourtWorkloadDialog({ open, caseId, caseTitle, onClose, onAssigned }: Props) {
  const [rows, setRows] = useState<CourtWorkload[]>([]);
  const [loading, setLoading] = useState(false);
  const [assigningUserId, setAssigningUserId] = useState<number | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!open) return;
    const controller = new AbortController();
    setRows([]);
    setLoading(true);
    setError(null);
    void apiService.getCourtWorkload(caseId, { signal: controller.signal })
      .then((result) => { if (!controller.signal.aborted) setRows(result ?? []); })
      .catch((loadError) => {
        if (!controller.signal.aborted) setError(extractDisplayErrorMessage(loadError, 'Не удалось загрузить нагрузку сотрудников.'));
      })
      .finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, [open, caseId]);

  if (!open) return null;

  const assign = async (userId: number) => {
    if (!caseId || assigningUserId !== null) return;
    setAssigningUserId(userId);
    setError(null);
    try {
      await apiService.assignCourtCase(caseId, userId);
      onAssigned();
    } catch (assignError) {
      setError(extractDisplayErrorMessage(assignError, 'Не удалось назначить ответственного.'));
    } finally {
      setAssigningUserId(null);
    }
  };

  return <div className="fixed inset-0 z-[1200] flex items-center justify-center bg-slate-950/50 p-3" role="presentation" onMouseDown={(event) => { if (event.currentTarget === event.target && assigningUserId === null) onClose(); }}>
    <section role="dialog" aria-modal="true" aria-labelledby="court-workload-title" className="max-h-[90vh] w-full max-w-3xl overflow-y-auto rounded-xl border border-slate-200 bg-white p-3 shadow-2xl">
      <div className="flex items-start justify-between gap-3"><div><h2 id="court-workload-title" className="text-lg font-bold text-slate-950">Распределение нагрузки</h2><p className="mt-0.5 text-sm text-slate-600">{caseId ? `Выберите ответственного для дела «${caseTitle || `#${caseId}`}».` : 'Сводка по сотрудникам с доступом к судебным делам.'}</p></div><button type="button" onClick={onClose} disabled={assigningUserId !== null} aria-label="Закрыть распределение нагрузки" className="rounded-lg p-1.5 text-slate-500 hover:bg-slate-100 disabled:opacity-50"><X className="h-5 w-5" /></button></div>
      <p className="mt-2 rounded-lg bg-slate-50 px-2.5 py-2 text-xs text-slate-600">Оценка загрузки ориентировочная: дело — 1 балл, заседание на ближайшие 7 дней и просроченная судебная задача — по 2 балла. До 5 баллов — низкая, 6–10 — средняя, больше 10 — высокая.</p>
      {error ? <div role="alert" className="mt-2 flex items-start gap-2 rounded-lg border border-rose-200 bg-rose-50 p-2 text-sm text-rose-800"><AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" />{error}</div> : null}
      {loading ? <div className="flex items-center justify-center gap-2 py-10 text-sm text-slate-600"><Loader2 className="h-4 w-4 animate-spin" /> Загружаем данные</div> : null}
      {!loading && rows.length === 0 && !error ? <div className="py-8 text-center text-sm text-slate-600">Нет сотрудников с доступом к судебным делам.</div> : null}
      {!loading && rows.length > 0 ? <>
        <div className="mt-3 space-y-2 sm:hidden">{rows.map((row) => { const level = loadLevel(row); return <article key={row.userId} className="rounded-lg border border-slate-200 p-2.5 text-sm"><div className="flex items-start justify-between gap-2"><span className="font-semibold text-slate-900">{row.userName}</span><span className={`shrink-0 rounded-full border px-2 py-0.5 text-xs font-semibold ${level.className}`}>{level.label}</span></div><div className="mt-2 grid grid-cols-3 gap-1 border-t border-slate-100 pt-2 text-center text-xs"><div><div className="font-bold text-slate-800">{row.activeCases}</div><div className="text-slate-500">Дел</div></div><div><div className="font-bold text-slate-800">{row.hearingsNextSevenDays}</div><div className="text-slate-500">Заседаний</div></div><div><div className="font-bold text-slate-800">{row.overdueCourtTasks}</div><div className="text-slate-500">Просрочено</div></div></div>{caseId ? <div className="mt-2">{row.canAssign ? <button type="button" onClick={() => void assign(row.userId)} disabled={assigningUserId !== null} className="w-full rounded-lg bg-blue-600 px-2.5 py-2 text-xs font-semibold text-white disabled:opacity-50">{assigningUserId === row.userId ? 'Назначаем…' : 'Назначить'}</button> : <span className="text-xs text-rose-700">Нет доступа к делу</span>}</div> : null}</article>; })}</div>
        <div className="mt-3 hidden overflow-x-auto sm:block"><table className="w-full min-w-[620px] text-left text-sm"><thead><tr className="border-b border-slate-200 text-xs text-slate-500"><th className="px-2 py-2">Сотрудник</th><th className="px-2 py-2">Активных дел</th><th className="px-2 py-2">Заседаний за 7 дней</th><th className="px-2 py-2">Просроченных судебных задач</th><th className="px-2 py-2">Загрузка</th>{caseId ? <th className="px-2 py-2">Действие</th> : null}</tr></thead><tbody>{rows.map((row) => { const level = loadLevel(row); return <tr key={row.userId} className="border-b border-slate-100"><td className="px-2 py-2 font-semibold text-slate-900">{row.userName}</td><td className="px-2 py-2">{row.activeCases}</td><td className="px-2 py-2">{row.hearingsNextSevenDays}</td><td className="px-2 py-2">{row.overdueCourtTasks}</td><td className="px-2 py-2"><span className={`rounded-full border px-2 py-0.5 text-xs font-semibold ${level.className}`}>{level.label}</span></td>{caseId ? <td className="px-2 py-2">{row.canAssign ? <button type="button" onClick={() => void assign(row.userId)} disabled={assigningUserId !== null} className="rounded-lg bg-blue-600 px-2.5 py-1.5 text-xs font-semibold text-white hover:bg-blue-700 disabled:opacity-50">{assigningUserId === row.userId ? 'Назначаем…' : 'Назначить'}</button> : <span className="text-xs text-rose-700" title="Это дело скрыто от исполнителя">Нет доступа к делу</span>}</td> : null}</tr>; })}</tbody></table></div>
      </> : null}
    </section>
  </div>;
}
