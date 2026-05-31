const sessionDismissed = new Set<string>();
const localDismissedPrefix = 'payplanner.appVersion.dismissed';

function getDismissedStorageKey(userId: number, releaseId: string) {
  return `${localDismissedPrefix}.${userId}.${releaseId}`;
}

export function hasAppVersionSessionDismissal(releaseId: string) {
  return sessionDismissed.has(releaseId);
}

export function rememberAppVersionSessionDismissal(releaseId: string) {
  sessionDismissed.add(releaseId);
}

export function hasAppVersionLocalDismissal(userId: number, releaseId: string) {
  if (typeof window === 'undefined') {
    return false;
  }

  try {
    return window.localStorage.getItem(getDismissedStorageKey(userId, releaseId)) === '1';
  } catch {
    return false;
  }
}

export function rememberAppVersionLocalDismissal(userId: number, releaseId: string) {
  if (typeof window === 'undefined') {
    return;
  }

  try {
    window.localStorage.setItem(getDismissedStorageKey(userId, releaseId), '1');
  } catch {
    // Local storage can be unavailable in private mode; server-side seen mark still works.
  }
}

export function resetAppVersionSessionDismissalsForTests() {
  sessionDismissed.clear();
}
