import { useCallback, useMemo } from 'react';
import { apiService } from '../services/api';
import { captureAuthSessionIdentity } from '../services/authService';
import { normalizeUserTimeZoneId, USER_TIME_ZONE_SETTING_KEY } from '../utils/timeZoneOptions';
import { useDocumentDeliveryOwner } from './useDocumentDeliveryOwner';
import { useDictionarySnapshot } from './useDictionarySnapshot';
import { awaitOwnedUiResult } from '../utils/StaleUiOperationError';

// At most one best-effort initialization per login. A settled/unknown outcome is
// not an invitation to repeat a preference write from another mounted consumer.
type AutoSaveCandidate = { current: () => boolean; signal: AbortSignal };
let detectedSave: { scope: string; dispatched: boolean; candidates: AutoSaveCandidate[] } | null = null;
function autoSaveDetectedTimeZone(timeZoneId: string, scope: string, current: () => boolean, signal: AbortSignal) {
  if (!current() || typeof apiService.initializeUserSetting !== 'function') return;
  if (detectedSave?.scope === scope) {
    if (!detectedSave.dispatched) detectedSave.candidates.push({ current, signal });
    return;
  }
  const attempt = { scope, dispatched: false, candidates: [{ current, signal }] };
  detectedSave = attempt;
  void Promise.resolve().then(async () => {
    const candidate = attempt.candidates.find(item => item.current());
    attempt.candidates.length = 0;
    if (!candidate) { if (detectedSave === attempt) detectedSave = null; return; }
    attempt.dispatched = true;
    await apiService.initializeUserSetting(USER_TIME_ZONE_SETTING_KEY, timeZoneId, { signal: candidate.signal });
  }).catch(() => undefined);
}

export function useUserTimeZone() {
  const fallback = useMemo(() => normalizeUserTimeZoneId(), []);
  const { capture, scope } = useDocumentDeliveryOwner('profile-time-zone');
  const enabled = captureAuthSessionIdentity().authenticated && typeof apiService.getUserSetting === 'function';
  const load = useCallback(async (signal: AbortSignal) => {
    const owner = capture(); const current = () => owner?.isCurrent() === true && !signal.aborted;
    let response;
    try { response = await awaitOwnedUiResult(() => apiService.getUserSetting(USER_TIME_ZONE_SETTING_KEY, { signal }), current); }
    catch (error) { if (!current() || !(error instanceof Error && /^HTTP\s+404(?:\s|$)/i.test(error.message.trim()))) throw error; response = null; }
    if (response === null) {
      autoSaveDetectedTimeZone(fallback, scope, current, signal);
      return fallback;
    }
    if (!response || typeof response !== 'object' || Array.isArray(response)
      || response.key !== USER_TIME_ZONE_SETTING_KEY || typeof response.value !== 'string')
      throw new Error('Не удалось подтвердить часовой пояс профиля.');
    return normalizeUserTimeZoneId(response.value);
  }, [capture, fallback, scope]);
  const state = useDictionarySnapshot<string | null>(load, null, { enabled, ownerScope: scope }, false);
  return state.error ? fallback : state.value ?? fallback;
}
