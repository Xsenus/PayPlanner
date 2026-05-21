import { AlertTriangle, CheckCircle2, History, Info, Sparkles, Wrench, X } from 'lucide-react';
import { type AppReleaseDto, type AppReleaseItemDto } from '../../services/api';

type ModalState =
  | { status: 'loading' }
  | { status: 'error'; message: string }
  | { status: 'ready'; currentVersion: string; release: AppReleaseDto; history?: AppReleaseDto[]; historyLoading?: boolean };

type AppVersionModalProps = {
  state: ModalState;
  onClose: () => void;
  onShowHistory: () => void;
};

const ITEM_GROUPS: Array<{
  type: string;
  title: string;
  icon: typeof Sparkles;
  className: string;
}> = [
  { type: 'new', title: 'Новое', icon: Sparkles, className: 'text-blue-600 bg-blue-50 border-blue-100' },
  { type: 'improved', title: 'Улучшено', icon: Wrench, className: 'text-emerald-600 bg-emerald-50 border-emerald-100' },
  { type: 'fixed', title: 'Исправлено', icon: CheckCircle2, className: 'text-slate-600 bg-slate-50 border-slate-100' },
  { type: 'important', title: 'Важно', icon: AlertTriangle, className: 'text-amber-600 bg-amber-50 border-amber-100' },
];

function formatReleaseDate(value: string) {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return value;
  }

  return new Intl.DateTimeFormat('ru-RU', {
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
  }).format(date);
}

function groupItems(items: AppReleaseItemDto[], type: string) {
  return items.filter((item) => item.type === type && item.text.trim());
}

function ReleaseDetails({ release, currentVersion }: { release: AppReleaseDto; currentVersion: string }) {
  return (
    <>
      <div className="border-b border-slate-200 px-5 py-4 sm:px-6">
        <div className="flex items-start justify-between gap-4">
          <div>
            <h2 className="text-xl font-semibold text-slate-950">Что нового</h2>
            <p className="mt-1 text-sm text-slate-500">
              Версия {release.version || currentVersion} · {formatReleaseDate(release.releasedAt)}
            </p>
          </div>
          <span className="rounded-full border border-blue-100 bg-blue-50 px-3 py-1 text-xs font-semibold text-blue-700">
            {release.title}
          </span>
        </div>
        {release.summary ? <p className="mt-3 text-sm leading-6 text-slate-600">{release.summary}</p> : null}
      </div>

      <div className="max-h-[48vh] overflow-y-auto px-5 py-4 sm:px-6">
        <div className="space-y-4">
          {ITEM_GROUPS.map((group) => {
            const items = groupItems(release.items, group.type);
            if (items.length === 0) {
              return null;
            }

            const Icon = group.icon;
            return (
              <section key={group.type}>
                <div className="mb-2 flex items-center gap-2">
                  <span className={`inline-flex h-7 w-7 items-center justify-center rounded-lg border ${group.className}`}>
                    <Icon className="h-4 w-4" />
                  </span>
                  <h3 className="text-sm font-semibold text-slate-900">{group.title}</h3>
                </div>
                <ul className="space-y-1.5 pl-9 text-sm leading-6 text-slate-700">
                  {items.map((item, index) => (
                    <li key={`${group.type}-${index}`} className="list-disc">
                      {item.text}
                    </li>
                  ))}
                </ul>
              </section>
            );
          })}
        </div>
      </div>
    </>
  );
}

function HistoryList({ items }: { items: AppReleaseDto[] }) {
  if (items.length <= 1) {
    return null;
  }

  return (
    <div className="border-t border-slate-200 px-5 py-4 sm:px-6">
      <div className="mb-3 flex items-center gap-2 text-sm font-semibold text-slate-900">
        <History className="h-4 w-4 text-slate-500" />
        История обновлений
      </div>
      <div className="max-h-36 space-y-2 overflow-y-auto">
        {items.slice(1).map((release) => (
          <div key={release.releaseId} className="rounded-lg border border-slate-200 px-3 py-2">
            <div className="text-sm font-medium text-slate-900">
              Версия {release.version} · {formatReleaseDate(release.releasedAt)}
            </div>
            <div className="mt-1 text-xs text-slate-500">{release.title}</div>
          </div>
        ))}
      </div>
    </div>
  );
}

export function AppVersionModal({ state, onClose, onShowHistory }: AppVersionModalProps) {
  return (
    <div className="fixed inset-0 z-[2350] flex items-center justify-center bg-slate-900/40 p-4" role="dialog" aria-modal="true" aria-labelledby="app-version-title">
      <div className="flex max-h-[calc(100vh-2rem)] w-full max-w-2xl flex-col overflow-hidden rounded-2xl bg-white shadow-2xl">
        <div className="sr-only" id="app-version-title">Что нового</div>
        <button
          type="button"
          onClick={onClose}
          className="absolute right-5 top-5 rounded-full p-1.5 text-slate-400 transition hover:bg-white/80 hover:text-slate-700"
          aria-label="Закрыть"
        >
          <X className="h-5 w-5" />
        </button>

        {state.status === 'loading' ? (
          <div className="px-6 py-12 text-center">
            <div className="mx-auto h-10 w-10 animate-spin rounded-full border-4 border-slate-200 border-t-blue-600" />
            <p className="mt-4 text-sm text-slate-600">Загружаем информацию об обновлении...</p>
          </div>
        ) : null}

        {state.status === 'error' ? (
          <div className="px-6 py-10 text-center">
            <div className="mx-auto flex h-12 w-12 items-center justify-center rounded-full bg-slate-100 text-slate-600">
              <Info className="h-6 w-6" />
            </div>
            <h2 className="mt-4 text-xl font-semibold text-slate-950">Что нового</h2>
            <p className="mt-2 text-sm text-slate-600">{state.message}</p>
          </div>
        ) : null}

        {state.status === 'ready' ? (
          <>
            <ReleaseDetails release={state.release} currentVersion={state.currentVersion} />
            {state.historyLoading ? (
              <div className="border-t border-slate-200 px-5 py-3 text-sm text-slate-500 sm:px-6">Загружаем историю...</div>
            ) : state.history ? (
              <HistoryList items={state.history} />
            ) : null}
          </>
        ) : null}

        <div className="flex flex-col-reverse gap-2 border-t border-slate-200 px-5 py-4 sm:flex-row sm:items-center sm:justify-between sm:px-6">
          {state.status === 'ready' ? (
            <button
              type="button"
              onClick={onShowHistory}
              className="inline-flex items-center justify-center gap-2 rounded-lg border border-slate-200 px-4 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
            >
              <History className="h-4 w-4" />
              История обновлений
            </button>
          ) : <span />}
          <button
            type="button"
            onClick={onClose}
            className="inline-flex items-center justify-center rounded-lg bg-blue-600 px-4 py-2 text-sm font-semibold text-white transition hover:bg-blue-700"
          >
            Понятно
          </button>
        </div>
      </div>
    </div>
  );
}
