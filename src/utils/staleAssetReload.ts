const STALE_ASSET_NOTICE_ID = 'payplanner-stale-asset-notice';

const STALE_ASSET_ERROR_MARKERS = [
  'failed to fetch dynamically imported module',
  'error loading dynamically imported module',
  'importing a module script failed',
  'unable to preload css',
  'vite:preloaderror',
];

export function isStaleAssetLoadError(reason: unknown) {
  const message = extractErrorMessage(reason).toLowerCase();
  return STALE_ASSET_ERROR_MARKERS.some((marker) => message.includes(marker));
}

export function installStaleAssetReload(win: Window = window) {
  win.addEventListener('vite:preloadError', (event) => {
    const payload = (event as Event & { payload?: unknown }).payload ?? event;
    if (!isStaleAssetLoadError(payload)) {
      return;
    }

    event.preventDefault();
    showReloadNotice(win);
  });

  win.addEventListener('unhandledrejection', (event) => {
    if (!isStaleAssetLoadError(event.reason)) {
      return;
    }

    event.preventDefault();
    showReloadNotice(win);
  });
}

function extractErrorMessage(reason: unknown) {
  if (reason instanceof Error) {
    return reason.message;
  }

  if (typeof reason === 'string') {
    return reason;
  }

  if (typeof reason === 'object' && reason !== null) {
    const record = reason as Record<string, unknown>;
    if (typeof record.message === 'string') {
      return record.message;
    }

    if (typeof record.type === 'string') {
      return record.type;
    }
  }

  return '';
}

function showReloadNotice(win: Window) {
  const document = win.document;
  if (document.getElementById(STALE_ASSET_NOTICE_ID)) return;

  const notice = document.createElement('aside');
  notice.id = STALE_ASSET_NOTICE_ID;
  notice.setAttribute('role', 'alert');
  notice.className = 'fixed bottom-4 right-4 z-[1000] w-[min(24rem,calc(100vw-2rem))] rounded-xl border border-amber-300 bg-amber-50 p-4 text-amber-950 shadow-xl';

  const title = document.createElement('strong');
  title.className = 'block text-sm';
  title.textContent = 'Доступно обновление страницы';
  const explanation = document.createElement('p');
  explanation.className = 'mt-1 text-xs leading-5';
  explanation.textContent = 'Сначала сохраните несохранённые данные. Затем обновите страницу, чтобы открыть новую версию раздела.';

  const actions = document.createElement('div');
  actions.className = 'mt-3 flex justify-end gap-2';
  const later = document.createElement('button');
  later.type = 'button';
  later.className = 'rounded-lg border border-amber-300 bg-white px-3 py-1.5 text-xs font-semibold';
  later.textContent = 'Позже';
  later.addEventListener('click', () => notice.remove());
  const reload = document.createElement('button');
  reload.type = 'button';
  reload.className = 'rounded-lg bg-amber-900 px-3 py-1.5 text-xs font-semibold text-white';
  reload.textContent = 'Обновить страницу';
  reload.addEventListener('click', () => win.location.reload());

  actions.append(later, reload);
  notice.append(title, explanation, actions);
  document.body.appendChild(notice);
}
