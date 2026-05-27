import { useState } from 'react';
import { AlertTriangle, CheckCircle2, History, Info, RefreshCcw, Sparkles, Wrench, X } from 'lucide-react';
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
  onToggleHistory,
}: {
  release: AppReleaseDto;
  currentVersion: string;
  isCurrent: boolean;
  onShowCurrent: () => void;
  onToggleHistory: () => void;
}) {
  return (
    <section className="flex min-h-0 flex-1 flex-col">
      <div className="shrink-0 border-b border-slate-200 px-5 py-4 pr-14 sm:px-6 sm:pr-24">
        <div className="flex items-start justify-between gap-3">
          <div className="min-w-0">
            <h2 className="text-xl font-semibold text-slate-950">Что нового</h2>
            <p className="mt-1 text-sm text-slate-500">
              Версия {release.version || currentVersion} · {formatReleaseDateTime(release.releasedAt)}
            </p>
          </div>
          <div className="flex shrink-0 flex-wrap items-center justify-end gap-2">
            <button
              type="button"
              onClick={onToggleHistory}
              className="inline-flex items-center gap-1.5 rounded-lg border border-slate-200 px-2.5 py-1.5 text-xs font-semibold text-slate-700 transition hover:bg-slate-50 md:hidden"
              aria-label="Показать историю обновлений"
              data-testid="app-version-mobile-history-toggle"
            >
              <History className="h-4 w-4" />
              История
            </button>
            {!isCurrent ? (
              <button
                type="button"
                onClick={onShowCurrent}
                className="inline-flex items-center gap-1.5 whitespace-nowrap rounded-lg border border-emerald-200 bg-emerald-50 px-3 py-1.5 text-xs font-semibold text-emerald-700 shadow-sm shadow-emerald-100/70 transition hover:border-emerald-300 hover:bg-emerald-100 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-emerald-400/40"
              >
                <CheckCircle2 className="h-3.5 w-3.5" />
                Показать текущее
              </button>
            ) : null}
          </div>
        </div>
        {release.summary ? <p className="mt-3 text-sm leading-6 text-slate-600">{release.summary}</p> : null}
      </div>

      <div className="min-h-0 flex-1 overflow-y-auto px-5 py-4 sm:px-6">
        <div className="space-y-5">
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
    </section>
  );
}

function HistoryList({
  items,
  loading,
  selectedReleaseId,
  currentReleaseId,
  onSelectRelease,
  onShowHistory,
}: {
  items: AppReleaseDto[];
  loading: boolean;
  selectedReleaseId: string;
  currentReleaseId: string;
  onSelectRelease: (release: AppReleaseDto) => void;
  onShowHistory: () => void;
}) {
  return (
    <aside
      className="flex max-h-[190px] w-full shrink-0 flex-col border-b border-slate-200 bg-slate-50/70 md:max-h-none md:w-[360px] md:border-b-0 md:border-r"
      data-testid="app-version-history"
    >
      <div className="flex shrink-0 items-center justify-between gap-3 px-4 py-4">
        <div className="flex items-center gap-2 text-sm font-semibold text-slate-900">
          <History className="h-4 w-4 text-slate-500" />
          История обновлений
        </div>
        <button
          type="button"
          onClick={onShowHistory}
          className="hidden rounded-lg p-1.5 text-slate-400 transition hover:bg-white hover:text-slate-700 md:inline-flex"
          aria-label="Обновить историю"
        >
          <RefreshCcw className={`h-4 w-4 ${loading ? 'animate-spin' : ''}`} />
        </button>
      </div>
      <div className="min-h-0 flex-1 space-y-2 overflow-y-auto px-4 pb-4">
        {items.map((release) => (
          <button
            key={release.releaseId}
            type="button"
            onClick={() => onSelectRelease(release)}
            aria-current={release.releaseId === selectedReleaseId ? 'true' : undefined}
            className={`relative w-full rounded-lg border px-3 py-2 text-left transition ${
              release.releaseId === currentReleaseId ? 'pr-11' : ''
            } ${
              release.releaseId === selectedReleaseId
                ? 'border-blue-200 bg-blue-50'
                : 'border-slate-200 bg-white hover:border-blue-200 hover:bg-slate-50'
            }`}
          >
            <div className="text-sm font-medium text-slate-900">
              <span>Версия {release.version} · {formatReleaseDateTime(release.releasedAt)}</span>
              {release.releaseId === currentReleaseId ? (
                <span
                  className="absolute right-2 top-2 inline-flex h-6 w-6 items-center justify-center rounded-full bg-emerald-50 text-emerald-700"
                  data-testid="app-version-current-badge"
                  role="img"
                  aria-label="Текущее обновление"
                  title="Текущее обновление"
                >
                  <CheckCircle2 className="h-3.5 w-3.5" />
                </span>
              ) : null}
            </div>
            <div className="mt-1 text-xs text-slate-500">{release.title}</div>
          </button>
        ))}
        {loading && items.length === 0 ? (
          <div className="rounded-lg border border-dashed border-slate-200 bg-white px-3 py-6 text-center text-sm text-slate-500">
            Загружаем историю...
          </div>
        ) : null}
      </div>
    </aside>
  );
}

export function AppVersionModal({ state, onClose, onShowHistory, onSelectRelease, onShowCurrent }: AppVersionModalProps) {
  const [isMobileHistoryOpen, setIsMobileHistoryOpen] = useState(false);
  const isViewingCurrent = state.status === 'ready' && state.selectedRelease.releaseId === state.currentRelease.releaseId;
  const historyItems = state.status === 'ready'
    ? (state.history && state.history.length > 0 ? state.history : [state.currentRelease])
    : [];
  const historyClassName = isMobileHistoryOpen ? 'block md:flex' : 'hidden md:flex';

  const handleSelectRelease = (release: AppReleaseDto) => {
    onSelectRelease(release);
    setIsMobileHistoryOpen(false);
  };

  return (
    <div className="fixed inset-0 z-[2350] flex items-center justify-center bg-slate-900/40 p-3 sm:p-4" role="dialog" aria-modal="true" aria-labelledby="app-version-title">
      <div
        className="relative flex h-[min(760px,calc(100vh-1.5rem))] w-full max-w-[1080px] flex-col overflow-hidden rounded-2xl bg-white shadow-2xl sm:h-[min(760px,calc(100vh-2rem))]"
        data-testid="app-version-dialog"
      >
        <div className="sr-only" id="app-version-title">Что нового</div>
        <button
          type="button"
          onClick={onClose}
          className="absolute right-4 top-4 z-10 rounded-full p-1.5 text-slate-400 transition hover:bg-slate-100 hover:text-slate-700"
          aria-label="Закрыть"
        >
          <X className="h-5 w-5" />
        </button>

        {state.status === 'loading' ? (
          <div className="flex min-h-0 flex-1 flex-col items-center justify-center px-6 py-12 text-center">
            <div className="h-10 w-10 animate-spin rounded-full border-4 border-slate-200 border-t-blue-600" />
            <p className="mt-4 text-sm text-slate-600">Загружаем информацию об обновлении...</p>
          </div>
        ) : null}

        {state.status === 'error' ? (
          <div className="flex min-h-0 flex-1 flex-col items-center justify-center px-6 py-10 text-center">
            <div className="flex h-12 w-12 items-center justify-center rounded-full bg-slate-100 text-slate-600">
              <Info className="h-6 w-6" />
            </div>
            <h2 className="mt-4 text-xl font-semibold text-slate-950">Что нового</h2>
            <p className="mt-2 text-sm text-slate-600">{state.message}</p>
          </div>
        ) : null}

        {state.status === 'ready' ? (
          <div className="flex min-h-0 flex-1 flex-col md:flex-row">
            <div className={historyClassName}>
              <HistoryList
                items={historyItems}
                loading={Boolean(state.historyLoading && !state.history)}
                selectedReleaseId={state.selectedRelease.releaseId}
                currentReleaseId={state.currentRelease.releaseId}
                onSelectRelease={handleSelectRelease}
                onShowHistory={onShowHistory}
              />
            </div>
            <ReleaseDetails
              release={state.selectedRelease}
              currentVersion={state.currentVersion}
              isCurrent={isViewingCurrent}
              onShowCurrent={onShowCurrent}
              onToggleHistory={() => setIsMobileHistoryOpen((current) => !current)}
            />
          </div>
        ) : null}

        <div className="flex shrink-0 justify-end border-t border-slate-200 px-5 py-4 sm:px-6">
          <button
            type="button"
            onClick={onClose}
            className="inline-flex min-h-10 items-center justify-center rounded-lg bg-blue-600 px-5 py-2 text-sm font-semibold text-white transition hover:bg-blue-700"
          >
            Понятно
          </button>
        </div>
      </div>
    </div>
  );
}
