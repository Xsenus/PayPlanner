import type { ReactNode } from 'react';
import { CalendarDays, Download, FileText, Loader2, ReceiptText, ShieldCheck } from 'lucide-react';
import type { ClientPortalCase } from '../../services/api';
import { formatMediumRussianDate } from '../../utils/dateUtils';

export function ClientPortalCaseDetails({
  details,
  onDownload,
  downloadingDocumentId,
}: {
  details: ClientPortalCase;
  onDownload?: (publicId: string, name: string) => void;
  downloadingDocumentId?: string | null;
}) {
  return (
    <div className="space-y-4">
      <div>
        <h2 className="text-xl font-semibold text-slate-950">{details.title}</h2>
        <p className="text-sm text-slate-500">{details.caseNumber} · {details.stage || details.status}</p>
      </div>
      <div className="grid gap-3 md:grid-cols-2">
        <PortalBlock icon={<CalendarDays className="h-4 w-4" />} title="Сроки" empty="Нет открытых сроков" items={details.deadlines.map((item) => `${formatMediumRussianDate(item.dueAt)} — ${item.title}`)} />
        <div className="rounded-xl border border-slate-200 p-3">
          <h3 className="flex items-center gap-2 text-sm font-semibold text-slate-800"><FileText className="h-4 w-4" />Документы</h3>
          {details.documents.length === 0 ? <p className="mt-2 text-sm text-slate-500">Нет доступных документов</p> : (
            <ul className="mt-2 space-y-2">{details.documents.map((item, index) => (
              <li key={`${item.publicId}-${index}`}>
                {onDownload ? (
                  <button type="button" onClick={() => onDownload(item.publicId, item.name)} disabled={downloadingDocumentId === item.publicId} className="inline-flex items-center gap-2 text-left text-sm text-blue-700 hover:underline disabled:opacity-60">
                    {downloadingDocumentId === item.publicId ? <Loader2 className="h-4 w-4 animate-spin" /> : <Download className="h-4 w-4" />}
                    {item.name} · версия {item.version}
                  </button>
                ) : <span className="text-sm text-slate-700">{item.name} · версия {item.version}</span>}
              </li>
            ))}</ul>
          )}
        </div>
        <PortalBlock icon={<ReceiptText className="h-4 w-4" />} title="Финансы" empty="Нет доступных начислений" items={details.invoices.map((item) => `${item.number || 'Счёт'} — ${item.amount.toLocaleString('ru-RU')} ₽`)} />
        <PortalBlock icon={<FileText className="h-4 w-4" />} title="Акты и договоры" empty="Нет доступных актов и договоров" items={[
          ...(details.acts ?? []).map((item) => `Акт ${item.number || 'без номера'} — ${item.amount.toLocaleString('ru-RU')} ₽`),
          ...(details.contracts ?? []).map((item) => `Договор ${item.number || 'без номера'}${item.amount != null ? ` — ${item.amount.toLocaleString('ru-RU')} ₽` : ''}`),
        ]} />
        <PortalBlock icon={<CalendarDays className="h-4 w-4" />} title="События" empty="Нет доступных событий" items={(details.events ?? []).map((item) => `${formatMediumRussianDate(item.occurredAt)} — ${item.title}`)} />
        <PortalBlock icon={<ShieldCheck className="h-4 w-4" />} title="Согласования" empty="Нет доступных согласований" items={details.approvals.map((item) => `Версия ${item.version} · ${item.status}`)} />
      </div>
    </div>
  );
}

function PortalBlock({ icon, title, empty, items }: { icon: ReactNode; title: string; empty: string; items: string[] }) {
  return <div className="rounded-xl border border-slate-200 p-3"><h3 className="flex items-center gap-2 text-sm font-semibold text-slate-800">{icon}{title}</h3>{items.length === 0 ? <p className="mt-2 text-sm text-slate-500">{empty}</p> : <ul className="mt-2 space-y-2">{items.map((item, index) => <li key={`${item}-${index}`} className="text-sm text-slate-700">{item}</li>)}</ul>}</div>;
}
