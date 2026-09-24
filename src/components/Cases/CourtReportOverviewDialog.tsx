import { useEffect, useState } from 'react';
import { Loader2, X } from 'lucide-react';

import { apiService } from '../../services/api';
import type { CourtReportGroup, CourtReportOverview } from '../../types';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { formatCurrencySmart } from '../../utils/formatters';
import { useUserTimeZone } from '../../hooks/useUserTimeZone';
import { fromTimeZoneDateTimeInput } from '../../utils/timeZoneDate';

interface CourtReportOverviewDialogProps {
  onClose: () => void;
  filters: {
    search?: string;
    responsibleUserId?: number;
    caseType?: string;
    courtState?: string;
    courtInstance?: string;
    ourCourtRole?: string;
    minClaimAmount?: number;
    maxClaimAmount?: number;
  };
}

function ReportTable({ title, rows, otherCases }: { title: string; rows: CourtReportGroup[]; otherCases: number }) {
  return <section className="min-w-0 rounded-xl border border-slate-200 bg-white">
    <h3 className="border-b border-slate-100 px-3 py-2 text-sm font-semibold text-slate-900">{title}</h3>
    {rows.length === 0 ? <p className="p-3 text-sm text-slate-500">Данных по выбранным условиям нет.</p> : <div className="overflow-x-auto"><table className="w-full text-left text-xs">
      <thead className="bg-slate-50 text-slate-500"><tr><th className="px-3 py-2 font-medium">{title === 'По юристам' ? 'Ответственный' : 'Суд'}</th><th className="px-2 py-2 text-right font-medium">Всего</th><th className="px-2 py-2 text-right font-medium">В работе</th><th className="px-2 py-2 text-right font-medium">Завершено</th><th className="px-2 py-2 text-right font-medium">Сумма иска</th><th className="px-2 py-2 text-right font-medium">Присуждено судом</th><th className="px-2 py-2 text-right font-medium">Госпошлина</th><th className="px-3 py-2 text-right font-medium" title="Среднее число календарных дней от даты подачи иска до даты решения суда">От иска до решения</th></tr></thead>
      <tbody className="divide-y divide-slate-100">{rows.map((row, index) => <tr key={`${row.name}-${index}`}><td className="max-w-64 truncate px-3 py-2 font-medium text-slate-800" title={row.name}>{row.name}</td><td className="px-2 py-2 text-right">{row.total}</td><td className="px-2 py-2 text-right">{row.active}</td><td className="px-2 py-2 text-right">{row.completed}</td><td className="whitespace-nowrap px-2 py-2 text-right" title={`Дел с указанной суммой иска: ${row.amounts.claimCases}`}>{row.amounts.claimCases ? formatCurrencySmart(row.amounts.claimTotal).full : '—'}</td><td className="whitespace-nowrap px-2 py-2 text-right" title={`Дел с указанной взысканной суммой: ${row.amounts.awardedCases}`}>{row.amounts.awardedCases ? formatCurrencySmart(row.amounts.awardedTotal).full : '—'}</td><td className="whitespace-nowrap px-2 py-2 text-right" title={`Дел с указанной госпошлиной: ${row.amounts.stateFeeCases}`}>{row.amounts.stateFeeCases ? formatCurrencySmart(row.amounts.stateFeeTotal).full : '—'}</td><td className="whitespace-nowrap px-3 py-2 text-right" title={row.decisionDurationSampleCount ? `Количество дел с обеими датами: ${row.decisionDurationSampleCount}` : 'Даты подачи иска и решения суда не заполнены'}>{row.averageDaysFromClaimToDecision === null ? '—' : `${row.averageDaysFromClaimToDecision} дн. · выборка: ${row.decisionDurationSampleCount}`}</td></tr>)}</tbody>
    </table></div>}
    {otherCases > 0 ? <p className="border-t border-slate-100 px-3 py-2 text-xs text-slate-500">Ещё {otherCases} дел в остальных группах. Уточните период или фильтры.</p> : null}
  </section>;
}

export function CourtReportOverviewDialog({ onClose, filters }: CourtReportOverviewDialogProps) {
  const timeZoneId = useUserTimeZone();
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [report, setReport] = useState<CourtReportOverview | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setError(null);
    const nextDay = to ? new Date(Date.parse(`${to}T00:00:00Z`) + 86_400_000).toISOString().slice(0, 10) : '';
    void apiService.getCourtReportOverview({
      ...filters,
      from: from ? fromTimeZoneDateTimeInput(`${from}T00:00`, timeZoneId) : undefined,
      toExclusive: nextDay ? fromTimeZoneDateTimeInput(`${nextDay}T00:00`, timeZoneId) : undefined,
    }, { signal: controller.signal })
      .then((result) => setReport(result))
      .catch((loadError) => { if (!controller.signal.aborted) setError(extractDisplayErrorMessage(loadError, 'Не удалось загрузить сводку судебных дел.')); })
      .finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, [filters, from, timeZoneId, to]);

  return <div className="fixed inset-0 z-[1200] flex items-center justify-center bg-slate-950/50 p-3" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) onClose(); }}>
    <section role="dialog" aria-modal="true" aria-labelledby="court-report-title" className="max-h-[90vh] w-full max-w-5xl overflow-y-auto rounded-2xl border border-slate-200 bg-slate-50 p-3 shadow-2xl lg:p-4">
      <div className="flex items-start justify-between gap-3"><div><h2 id="court-report-title" className="text-lg font-bold text-slate-950">Сводка судебных дел</h2><p className="mt-0.5 text-xs text-slate-600">Количество дел по ответственным и судам. Учитываются доступные вам дела, включая завершённые.</p></div><button type="button" aria-label="Закрыть сводку" onClick={onClose} className="rounded-lg p-2 text-slate-500 hover:bg-slate-200"><X className="h-5 w-5" /></button></div>
      <div className="mt-3 flex flex-wrap gap-2"><label className="text-xs font-medium text-slate-700">Созданы с<input type="date" aria-label="Дела созданы с" value={from} max={to || undefined} onChange={(event) => setFrom(event.target.value)} className="mt-1 block rounded-lg border border-slate-200 bg-white px-2 py-1.5" /></label><label className="text-xs font-medium text-slate-700">Созданы по<input type="date" aria-label="Дела созданы по" value={to} min={from || undefined} onChange={(event) => setTo(event.target.value)} className="mt-1 block rounded-lg border border-slate-200 bg-white px-2 py-1.5" /></label></div>
      {error ? <div role="alert" className="mt-3 rounded-lg border border-rose-200 bg-rose-50 p-3 text-sm text-rose-800">{error}</div> : null}
      {loading ? <div className="flex h-36 items-center justify-center gap-2 text-sm text-slate-600"><Loader2 className="h-5 w-5 animate-spin" /> Загружаем сводку</div> : null}
      {!loading && !error && report ? <><div className="mt-3 grid gap-2 sm:grid-cols-3">{[{ label: 'Всего', value: report.total }, { label: 'В работе', value: report.active }, { label: 'Завершено в CRM', value: report.completed }].map((item) => <div key={item.label} className="rounded-lg border border-slate-200 bg-white p-3"><div className="text-xs text-slate-500">{item.label}</div><div className="text-xl font-bold text-slate-900">{item.value}</div></div>)}</div><div className="mt-2 grid gap-2 sm:grid-cols-3">{[{ label: 'Сумма иска', total: report.amounts.claimTotal, cases: report.amounts.claimCases }, { label: 'Присуждено судом', total: report.amounts.awardedTotal, cases: report.amounts.awardedCases }, { label: 'Госпошлина', total: report.amounts.stateFeeTotal, cases: report.amounts.stateFeeCases }].map((item) => <div key={item.label} className="rounded-lg border border-blue-100 bg-blue-50/50 p-3"><div className="text-xs text-slate-600">{item.label}</div><div className="text-base font-bold text-slate-900">{item.cases ? formatCurrencySmart(item.total).full : '—'}</div><div className="text-xs text-slate-500">Дел с заполненным полем: {item.cases}</div></div>)}</div><div className="mt-3 grid gap-3 lg:grid-cols-2"><ReportTable title="По юристам" rows={report.byResponsible} otherCases={report.otherResponsibleCases} /><ReportTable title="По судам" rows={report.byCourt} otherCases={report.otherCourtCases} /></div><p className="mt-3 text-xs text-slate-500">Срок «от иска до решения» — среднее число календарных дней только по делам с обеими датами; ошибочные даты в расчёт не входят. Судебные суммы складываются только по делам с заполненными полями; ноль учитывается, пустое поле — нет. «Завершено» означает закрытое дело в CRM, а не выигрыш в суде. Присуждённая судом сумма не означает фактическое поступление денег; исход спора здесь не определяется.</p></> : null}
    </section>
  </div>;
}
