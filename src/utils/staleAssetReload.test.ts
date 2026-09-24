import { afterEach, describe, expect, it, vi } from 'vitest';

import { installStaleAssetReload, isStaleAssetLoadError } from './staleAssetReload';

function createTestWindow() {
  const target = new EventTarget();
  const reload = vi.fn();
  Object.defineProperties(target, {
    document: { value: document },
    location: { value: { reload } },
  });
  return { win: target as Window, reload };
}

afterEach(() => {
  document.getElementById('payplanner-stale-asset-notice')?.remove();
});

describe('isStaleAssetLoadError', () => {
  it('detects stale dynamic import errors from deployed chunks', () => {
    expect(isStaleAssetLoadError(new Error('Failed to fetch dynamically imported module: /assets/TaskJournal-old.js'))).toBe(true);
    expect(isStaleAssetLoadError('Importing a module script failed.')).toBe(true);
    expect(isStaleAssetLoadError({ message: 'error loading dynamically imported module' })).toBe(true);
  });

  it('ignores ordinary application errors', () => {
    expect(isStaleAssetLoadError(new Error('Validation failed'))).toBe(false);
    expect(isStaleAssetLoadError({ message: 'HTTP 403 Forbidden' })).toBe(false);
    expect(isStaleAssetLoadError(null)).toBe(false);
  });

  it('offers a manual refresh without reloading or interrupting unsaved work', () => {
    const { win, reload } = createTestWindow();
    installStaleAssetReload(win);

    const event = new Event('vite:preloadError', { cancelable: true }) as Event & { payload: Error };
    event.payload = new Error('Failed to fetch dynamically imported module: /assets/TaskJournal-old.js');
    win.dispatchEvent(event);

    expect(event.defaultPrevented).toBe(true);
    expect(reload).not.toHaveBeenCalled();
    expect(document.querySelectorAll('#payplanner-stale-asset-notice')).toHaveLength(1);
    expect(document.getElementById('payplanner-stale-asset-notice')?.textContent).toContain('Сначала сохраните несохранённые данные');

    win.dispatchEvent(event);
    expect(document.querySelectorAll('#payplanner-stale-asset-notice')).toHaveLength(1);
    (document.querySelector('#payplanner-stale-asset-notice button') as HTMLButtonElement).click();
    expect(document.getElementById('payplanner-stale-asset-notice')).toBeNull();
    expect(reload).not.toHaveBeenCalled();
  });

  it('does not show an update notice for an unrelated application error', () => {
    const { win, reload } = createTestWindow();
    installStaleAssetReload(win);

    const event = new Event('unhandledrejection', { cancelable: true }) as Event & { reason: Error };
    event.reason = new Error('Validation failed');
    win.dispatchEvent(event);

    expect(event.defaultPrevented).toBe(false);
    expect(document.getElementById('payplanner-stale-asset-notice')).toBeNull();
    expect(reload).not.toHaveBeenCalled();
  });

  it('refreshes only after the user chooses the action', () => {
    const { win, reload } = createTestWindow();
    installStaleAssetReload(win);

    const event = new Event('unhandledrejection', { cancelable: true }) as Event & { reason: Error };
    event.reason = new Error('Importing a module script failed.');
    win.dispatchEvent(event);

    expect(reload).not.toHaveBeenCalled();
    const buttons = document.querySelectorAll<HTMLButtonElement>('#payplanner-stale-asset-notice button');
    buttons[1].click();
    expect(reload).toHaveBeenCalledTimes(1);
  });
});
