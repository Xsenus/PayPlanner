import { useCallback, useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { apiService, type AppReleaseDto, type AppVersionLatestDto } from '../../services/api';
import { AppVersionModal } from './AppVersionModal';

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

type AppVersionGateProps = {
  userId?: number | null;
  manualOpenSignal: number;
  onManualOpenHandled: () => void;
};

const sessionDismissed = new Set<string>();
const localDismissedPrefix = 'payplanner.appVersion.dismissed';

function getDismissedStorageKey(userId: number, releaseId: string) {
  return `${localDismissedPrefix}.${userId}.${releaseId}`;
}

function hasLocalDismissal(userId: number, releaseId: string) {
  if (typeof window === 'undefined') {
    return false;
  }

  try {
    return window.localStorage.getItem(getDismissedStorageKey(userId, releaseId)) === '1';
  } catch {
    return false;
  }
}

function rememberLocalDismissal(userId: number, releaseId: string) {
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

export function AppVersionGate({ userId, manualOpenSignal, onManualOpenHandled }: AppVersionGateProps) {
  const [latest, setLatest] = useState<AppVersionLatestDto | null>(null);
  const [modalState, setModalState] = useState<ModalState | null>(null);
  const autoOpenedReleaseRef = useRef<string | null>(null);

  const openFromLatest = useCallback((data: AppVersionLatestDto, force: boolean) => {
    if (!data.latestRelease) {
      if (force) {
        setModalState({ status: 'error', message: 'Информация о последнем релизе пока не опубликована.' });
      }
      return;
    }

    setModalState({
      status: 'ready',
      currentVersion: data.currentVersion,
      currentRelease: data.latestRelease,
      selectedRelease: data.latestRelease,
    });
  }, []);

  useEffect(() => {
    if (!userId) {
      setLatest(null);
      setModalState(null);
      autoOpenedReleaseRef.current = null;
      return;
    }

    let cancelled = false;
    apiService.getLatestAppVersion()
      .then((data) => {
        if (cancelled) return;
        setLatest(data);

        const releaseId = data.latestRelease?.releaseId;
        if (
          releaseId
          && !data.seenByCurrentUser
          && !sessionDismissed.has(releaseId)
          && !hasLocalDismissal(userId, releaseId)
          && autoOpenedReleaseRef.current !== releaseId
        ) {
          autoOpenedReleaseRef.current = releaseId;
          openFromLatest(data, false);
        }
      })
      .catch(() => {
        if (!cancelled) {
          setLatest(null);
        }
      });

    return () => {
      cancelled = true;
    };
  }, [openFromLatest, userId]);

  useEffect(() => {
    if (!manualOpenSignal || !userId) {
      return;
    }

    onManualOpenHandled();
    if (latest?.latestRelease) {
      openFromLatest(latest, true);
      return;
    }

    setModalState({ status: 'loading' });
    apiService.getLatestAppVersion()
      .then((data) => {
        setLatest(data);
        openFromLatest(data, true);
      })
      .catch(() => {
        setModalState({ status: 'error', message: 'Не удалось загрузить информацию об обновлениях. Попробуйте открыть окно позже.' });
      });
  }, [latest, manualOpenSignal, onManualOpenHandled, openFromLatest, userId]);

  const handleClose = useCallback(() => {
    const releaseId = modalState?.status === 'ready' ? modalState.currentRelease.releaseId : null;
    setModalState(null);

    if (!releaseId) {
      return;
    }

    sessionDismissed.add(releaseId);
    if (userId) {
      rememberLocalDismissal(userId, releaseId);
    }
    apiService.markAppVersionSeen(releaseId)
      .then(() => {
        setLatest((current) => current?.latestRelease?.releaseId === releaseId
          ? { ...current, seenByCurrentUser: true }
          : current);
      })
      .catch(() => {
        sessionDismissed.add(releaseId);
      });
  }, [modalState, userId]);

  const handleShowHistory = useCallback(() => {
    if (modalState?.status !== 'ready') {
      return;
    }

    if (modalState.history) {
      setModalState({ ...modalState, historyLoading: false });
      return;
    }

    setModalState({ ...modalState, historyLoading: true });
    apiService.getAppVersionHistory()
      .then((history) => {
        setModalState((current) => current?.status === 'ready'
          ? { ...current, history, historyLoading: false }
          : current);
      })
      .catch(() => {
        setModalState((current) => current?.status === 'ready'
          ? { ...current, historyLoading: false }
          : current);
      });
  }, [modalState]);

  useEffect(() => {
    if (modalState?.status === 'ready' && !modalState.history && !modalState.historyLoading) {
      handleShowHistory();
    }
  }, [handleShowHistory, modalState]);

  const handleSelectRelease = useCallback((release: AppReleaseDto) => {
    setModalState((current) => current?.status === 'ready'
      ? { ...current, selectedRelease: release }
      : current);
  }, []);

  const handleShowCurrent = useCallback(() => {
    setModalState((current) => current?.status === 'ready'
      ? { ...current, selectedRelease: current.currentRelease }
      : current);
  }, []);

  if (!modalState || typeof document === 'undefined') {
    return null;
  }

  return createPortal(
    <AppVersionModal
      state={modalState}
      onClose={handleClose}
      onShowHistory={handleShowHistory}
      onSelectRelease={handleSelectRelease}
      onShowCurrent={handleShowCurrent}
    />,
    document.body,
  );
}
