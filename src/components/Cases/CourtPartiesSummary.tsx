import { useMemo, useState } from 'react';
import { Loader2 } from 'lucide-react';
import type { ClientCaseCourtParty } from '../../types';

const PREVIEW_SIZE = 6;
const PAGE_SIZE = 20;

export function CourtPartiesReadError({ message, onRetry }: { message: string; onRetry: () => void }) {
  return (
    <div role="alert" className="mt-3 rounded-lg border border-amber-200 bg-amber-50 p-3 text-sm text-amber-900">
      <p>{message}</p>
      <button type="button" onClick={onRetry} className="mt-2 rounded-lg border border-amber-300 bg-white px-3 py-1.5 text-xs font-semibold hover:bg-amber-100">
        Повторить загрузку участников
      </button>
    </div>
  );
}

export function CourtPartiesSummary({ parties, loading, error, onRetry }: {
  parties: ClientCaseCourtParty[];
  loading: boolean;
  error: string | null;
  onRetry: () => void;
}) {
  const activeParties = useMemo(() => parties.filter(party => !party.isArchived), [parties]);
  const [visibleCount, setVisibleCount] = useState(PREVIEW_SIZE);
  const visibleParties = activeParties.slice(0, visibleCount);
  const remaining = Math.max(0, activeParties.length - visibleParties.length);

  return (
    <section aria-label="Участники процесса" className="min-w-0 rounded-xl border border-slate-200 bg-white p-3 shadow-sm">
      <div className="flex items-center justify-between gap-2">
        <h3 className="font-bold text-slate-900">Участники процесса</h3>
        <span className="rounded-full bg-slate-100 px-2.5 py-1 text-xs font-semibold text-slate-500">
          {loading ? 'Загрузка' : error ? 'Недоступно' : activeParties.length}
        </span>
      </div>
      {loading ? (
        <div role="status" className="mt-3 flex items-center gap-2 text-sm text-slate-500">
          <Loader2 aria-hidden="true" className="h-4 w-4 animate-spin text-blue-600" />
          Загружаем участников процесса…
        </div>
      ) : error ? (
        <CourtPartiesReadError message={error} onRetry={onRetry} />
      ) : activeParties.length === 0 ? (
        <p className="mt-2 text-sm text-slate-500">Активные участники процесса не добавлены.</p>
      ) : (
        <>
          <ul aria-label="Список участников процесса" tabIndex={activeParties.length > PREVIEW_SIZE ? 0 : undefined} className="mt-2 max-h-96 space-y-2 overflow-y-auto pr-1">
            {visibleParties.map(party => (
              <li key={party.id} className="min-w-0 rounded-lg bg-slate-50 px-2.5 py-2">
                <div className="break-words text-sm font-semibold text-slate-800">{party.displayName}</div>
                <div className="break-words text-xs text-slate-500">{party.sideLabel || party.processRole || 'Участник'}</div>
              </li>
            ))}
          </ul>
          {activeParties.length > PREVIEW_SIZE && (
            <div className="mt-2 flex flex-wrap items-center justify-between gap-2 text-xs">
              <span className="text-slate-500">Показано {visibleParties.length} из {activeParties.length}</span>
              <div className="flex flex-wrap gap-2">
                {remaining > 0 && <button type="button" onClick={() => setVisibleCount(count => count + PAGE_SIZE)} className="rounded-lg border border-slate-200 px-2.5 py-1.5 font-semibold text-blue-700 hover:bg-blue-50">Показать ещё {Math.min(PAGE_SIZE, remaining)}</button>}
                {visibleCount > PREVIEW_SIZE && <button type="button" onClick={() => setVisibleCount(PREVIEW_SIZE)} className="rounded-lg border border-slate-200 px-2.5 py-1.5 font-semibold text-slate-600 hover:bg-slate-50">Свернуть список</button>}
              </div>
            </div>
          )}
        </>
      )}
    </section>
  );
}
