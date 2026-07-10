const STALE_ASSET_RELOAD_STORAGE_KEY = 'payplanner:stale-asset-reload-at';
const STALE_ASSET_RELOAD_COOLDOWN_MS = 30_000;

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
  const reload = () => reloadOnce(win);

  win.addEventListener('vite:preloadError', (event) => {
    const payload = (event as Event & { payload?: unknown }).payload ?? event;
    if (!isStaleAssetLoadError(payload)) {
      return;
    }

    event.preventDefault();
    reload();
  });

  win.addEventListener('unhandledrejection', (event) => {
    if (!isStaleAssetLoadError(event.reason)) {
      return;
    }

    event.preventDefault();
    reload();
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

function reloadOnce(win: Window) {
  const now = Date.now();

  try {
    const previous = Number(win.sessionStorage.getItem(STALE_ASSET_RELOAD_STORAGE_KEY) ?? '0');
    if (Number.isFinite(previous) && now - previous < STALE_ASSET_RELOAD_COOLDOWN_MS) {
      return;
    }

    win.sessionStorage.setItem(STALE_ASSET_RELOAD_STORAGE_KEY, String(now));
  } catch {
    // Storage may be blocked; reloading is still the safest recovery path.
  }

  win.location.reload();
}
