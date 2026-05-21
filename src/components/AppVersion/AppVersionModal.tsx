import { AlertTriangle, CheckCircle2, History, Info, Sparkles, Wrench, X } from 'lucide-react';
import { type AppReleaseDto, type AppReleaseItemDto } from '../../services/api';

type ModalState =
  | { status: 'loading' }
  | { status: 'error'; message: string }
  | {
    status: 'ready';
    currentVersion: string;
    currentRelease: AppReleaseDto;
    selectedRelease: AppReleaseDto;
    history?: AppReleaseDto[];
    historyLoading?: boolean;
  };

type AppVersionModalProps = {
  state: ModalState;
  onClose: () => void;
  onShowHistory: () => void;
  onSelectRelease: (release: AppReleaseDto) => void;
  onShowCurrent: () => void;
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

function formatReleaseDateTime(value: string) {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return value;
  }

  return new Intl.DateTimeFormat('ru-RU', {
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  }).format(date);
}

function groupItems(items: AppReleaseItemDto[], type: string) {
  return items.filter((item) => item.type === type && item.text.trim());
}

function ReleaseDetails({
  release,
  currentVersion,
  isCurrent,
  onShowCurrent,
}: {
  release: AppReleaseDto;
  currentVersion: string;
  isCurrent: boolean;
  onShowCurrent: () => void;
}) {
  return (
    <>
      <div className="border-b border-slate-200 px-5 py-4 sm:px-6">
        <div className="flex items-start justify-between gap-4">
          <div>
            <h2 className="text-xl font-semibold text-slate-950">Что нового</h2>
            <p className="mt-1 text-sm text-slate-500">
              Версия {release.version || currentVersion} · {formatReleaseDateTime(release.releasedAt)}
            </p>
          </div>
          <div className="flex flex-col items-end gap-2">
            <span className="rounded-full border border-blue-100 bg-blue-50 px-3 py-1 text-xs font-semibold text-blue-700">
              {release.title}
            </span>
            {!isCurrent ? (
              <button
                type="button"
                onClick={onShowCurrent}
                className="rounded-lg border border-slate-200 px-3 py-1.5 text-xs font-semibold text-slate-700 transition hover:bg-slate-50"
              >
                Текущее обновление
              </button>
            ) : null}
          </div>
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

function HistoryList({
  items,
  selectedReleaseId,
  currentReleaseId,
  onSelectRelease,
}: {
  items: AppReleaseDto[];
  selectedReleaseId: string;
  currentReleaseId: string;
  onSelectRelease: (release: AppReleaseDto) => void;
}) {
  if (items.length === 0) {
    return null;
  }

  return (
    <div className="border-t border-slate-200 px-5 py-4 sm:px-6">
      <div className="mb-3 flex items-center gap-2 text-sm font-semibold text-slate-900">
        <History className="h-4 w-4 text-slate-500" />
        История обновлений
      </div>
      <div className="max-h-36 space-y-2 overflow-y-auto">
        {items.map((release) => (
          <button
            key={release.releaseId}
            type="button"
            onClick={() => onSelectRelease(release)}
            aria-current={release.releaseId === selectedReleaseId ? 'true' : undefined}
            className={`w-full rounded-lg border px-3 py-2 text-left transition ${
              release.releaseId === selectedReleaseId
                ? 'border-blue-200 bg-blue-50'
                : 'border-slate-200 hover:border-blue-200 hover:bg-slate-50'
            }`}
          >
            <div className="flex flex-wrap items-center gap-2 text-sm font-medium text-slate-900">
              <span>Версия {release.version} · {formatReleaseDateTime(release.releasedAt)}</span>
              {release.releaseId === currentReleaseId ? (
                <span className="rounded-full bg-emerald-50 px-2 py-0.5 text-[11px] font-semibold text-emerald-700">
                  текущее
                </span>
              ) : null}
            </div>
            <div className="mt-1 text-xs text-slate-500">{release.title}</div>
          </button>
        ))}
      </div>
    </div>
  );
}

export function AppVersionModal({ state, onClose, onShowHistory, onSelectRelease, onShowCurrent }: AppVersionModalProps) {
  const isViewingCurrent = state.status === 'ready' && state.selectedRelease.releaseId === state.currentRelease.releaseId;

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
            <ReleaseDetails
              release={state.selectedRelease}
              currentVersion={state.currentVersion}
              isCurrent={isViewingCurrent}
              onShowCurrent={onShowCurrent}
            />
            {state.historyLoading ? (
              <div className="border-t border-slate-200 px-5 py-3 text-sm text-slate-500 sm:px-6">Загружаем историю...</div>
            ) : state.history ? (
              <HistoryList
                items={state.history}
                selectedReleaseId={state.selectedRelease.releaseId}
                currentReleaseId={state.currentRelease.releaseId}
                onSelectRelease={onSelectRelease}
              />
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
