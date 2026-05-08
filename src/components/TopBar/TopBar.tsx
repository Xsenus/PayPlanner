import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Bell, Building2, LogOut, User } from 'lucide-react';
import { useAuth } from '../../contexts/AuthContext';
import { useTabNavigation } from '../../contexts/TabContext';
import { useDialog } from '../../hooks/useDialog';
import { apiService } from '../../services/api';
import { setStoredCompanyProfileCompanyId, setStoredCompanyProfileMode } from '../../utils/companyProfileBridge';
import {
  TASK_JOURNAL_EVENT,
  type TaskJournalNotification,
} from '../../utils/taskJournalStore';

type TopBarProps = {
  title: string;
  onSignOut: () => Promise<void>;
  onOpenProfile: () => void;
  onOpenTask: (taskId: number) => void;
};

const TASK_NOTIFICATION_SEEN_KEY_PREFIX = 'pp.task_notifications.seen.v1';
const NOTIFICATION_BADGE_HIDE_MS = 220;

const getInitials = (fullName?: string | null) =>
  (fullName ?? 'U')
    .split(' ')
    .map((part) => part.trim()[0])
    .filter(Boolean)
    .slice(0, 2)
    .join('')
    .toUpperCase();

const readSeenNotificationIds = (storageKey: string) => {
  if (typeof window === 'undefined') {
    return new Set<string>();
  }

  try {
    const rawValue = window.localStorage.getItem(storageKey);
    if (!rawValue) {
      return new Set<string>();
    }

    const parsed = JSON.parse(rawValue);
    return new Set(
      Array.isArray(parsed)
        ? parsed.filter((value): value is string => typeof value === 'string' && value.length > 0)
        : [],
    );
  } catch {
    return new Set<string>();
  }
};

export default function TopBar({ title, onSignOut, onOpenProfile, onOpenTask }: TopBarProps) {
  const { user, isAdmin } = useAuth();
  const { setActiveTab } = useTabNavigation();
  const { confirm } = useDialog();
  const [userMenuOpen, setUserMenuOpen] = useState(false);
  const [notificationsOpen, setNotificationsOpen] = useState(false);
  const [taskNotifications, setTaskNotifications] = useState<TaskJournalNotification[]>([]);
  const [notificationsLoaded, setNotificationsLoaded] = useState(false);
  const [seenNotificationIds, setSeenNotificationIds] = useState<Set<string>>(() => new Set());
  const [visibleBadgeCount, setVisibleBadgeCount] = useState(0);
  const [badgeHiding, setBadgeHiding] = useState(false);
  const [avatarLoadFailed, setAvatarLoadFailed] = useState(false);
  const userMenuRef = useRef<HTMLDivElement | null>(null);
  const notificationsRef = useRef<HTMLDivElement | null>(null);

  const initials = useMemo(() => getInitials(user?.fullName), [user?.fullName]);
  const notificationSeenStorageKey = useMemo(() => {
    const subject = user?.id ? `user:${user.id}` : user?.email ? `email:${user.email}` : 'anonymous';
    return `${TASK_NOTIFICATION_SEEN_KEY_PREFIX}:${subject}`;
  }, [user?.email, user?.id]);

  const persistSeenNotificationIds = useCallback(
    (ids: Set<string>) => {
      if (typeof window === 'undefined') {
        return;
      }

      try {
        window.localStorage.setItem(notificationSeenStorageKey, JSON.stringify(Array.from(ids).slice(-200)));
      } catch {
        // ignore storage issues
      }
    },
    [notificationSeenStorageKey],
  );

  const markNotificationsSeen = useCallback(
    (notifications: TaskJournalNotification[]) => {
      if (notifications.length === 0) {
        return;
      }

      setSeenNotificationIds((previous) => {
        const next = new Set(previous);
        let changed = false;

        notifications.forEach((notification) => {
          if (!next.has(notification.id)) {
            next.add(notification.id);
            changed = true;
          }
        });

        if (!changed) {
          return previous;
        }

        persistSeenNotificationIds(next);
        return next;
      });
    },
    [persistSeenNotificationIds],
  );

  const unreadNotificationCount = useMemo(
    () => taskNotifications.reduce((count, notification) => count + (seenNotificationIds.has(notification.id) ? 0 : 1), 0),
    [seenNotificationIds, taskNotifications],
  );

  useEffect(() => {
    setAvatarLoadFailed(false);
  }, [user?.photoUrl]);

  useEffect(() => {
    setSeenNotificationIds(readSeenNotificationIds(notificationSeenStorageKey));
  }, [notificationSeenStorageKey]);

  useEffect(() => {
    let alive = true;
    const refresh = () => {
      apiService.getTaskJournalNotifications()
        .then((items) => {
          if (alive) {
            setTaskNotifications(items);
            setNotificationsLoaded(true);
          }
        })
        .catch(() => {
          if (alive) {
            setTaskNotifications([]);
            setNotificationsLoaded(true);
          }
        });
    };
    refresh();
    const timer = window.setInterval(refresh, 60_000);
    window.addEventListener(TASK_JOURNAL_EVENT, refresh);
    return () => {
      alive = false;
      window.clearInterval(timer);
      window.removeEventListener(TASK_JOURNAL_EVENT, refresh);
    };
  }, []);

  useEffect(() => {
    if (!notificationsLoaded) {
      return;
    }

    const currentIds = new Set(taskNotifications.map((notification) => notification.id));

    setSeenNotificationIds((previous) => {
      let changed = false;
      const next = new Set<string>();

      previous.forEach((id) => {
        if (currentIds.has(id)) {
          next.add(id);
        } else {
          changed = true;
        }
      });

      if (!changed) {
        return previous;
      }

      persistSeenNotificationIds(next);
      return next;
    });
  }, [notificationsLoaded, persistSeenNotificationIds, taskNotifications]);

  useEffect(() => {
    if (notificationsOpen) {
      markNotificationsSeen(taskNotifications);
    }
  }, [markNotificationsSeen, notificationsOpen, taskNotifications]);

  useEffect(() => {
    if (unreadNotificationCount > 0) {
      setVisibleBadgeCount(unreadNotificationCount);
      setBadgeHiding(false);
      return;
    }

    if (visibleBadgeCount === 0) {
      setBadgeHiding(false);
      return;
    }

    setBadgeHiding(true);
    const timer = window.setTimeout(() => {
      setVisibleBadgeCount(0);
      setBadgeHiding(false);
    }, NOTIFICATION_BADGE_HIDE_MS);

    return () => window.clearTimeout(timer);
  }, [unreadNotificationCount, visibleBadgeCount]);

  useEffect(() => {
    const handleClickOutside = (event: MouseEvent) => {
      const target = event.target as Node;
      if (userMenuRef.current && !userMenuRef.current.contains(target)) {
        setUserMenuOpen(false);
      }
      if (notificationsRef.current && !notificationsRef.current.contains(target)) {
        setNotificationsOpen(false);
      }
    };

    document.addEventListener('mousedown', handleClickOutside);
    return () => document.removeEventListener('mousedown', handleClickOutside);
  }, []);

  const handleSignOut = async () => {
    setUserMenuOpen(false);
    const accepted = await confirm({
      title: 'Выход из системы',
      description: 'Подтвердите, что хотите завершить текущую сессию.',
      confirmLabel: 'Выйти',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });

    if (accepted) {
      await onSignOut();
    }
  };

  const handleToggleNotifications = () => {
    const nextOpen = !notificationsOpen;
    setNotificationsOpen(nextOpen);

    if (nextOpen) {
      markNotificationsSeen(taskNotifications);
    }
  };

  const handleNotificationClick = (notification: TaskJournalNotification) => {
    markNotificationsSeen([notification]);
    setNotificationsOpen(false);
    onOpenTask(notification.taskId);
  };

  return (
    <header className="sticky top-0 z-20 border-b border-slate-200 bg-white/90 backdrop-blur">
      <div className="flex h-16 items-center justify-between px-6">
        <div className="text-sm font-semibold text-slate-700">{title}</div>

        <div className="flex items-center gap-3">
          <div className="relative" ref={notificationsRef}>
            <button
              type="button"
              onClick={handleToggleNotifications}
              className="relative flex h-10 w-10 items-center justify-center rounded-full border border-slate-200 text-slate-500 transition-colors hover:border-slate-300 hover:text-slate-700"
              aria-label="Уведомления"
            >
              <Bell className="h-5 w-5" />
              {visibleBadgeCount > 0 ? (
                <span
                  className={`absolute -right-1 -top-1 flex h-5 min-w-5 items-center justify-center rounded-full bg-rose-600 px-1 text-[11px] font-semibold text-white transition-all duration-200 ${
                    badgeHiding ? 'scale-75 opacity-0' : 'scale-100 opacity-100'
                  }`}
                >
                  {visibleBadgeCount > 9 ? '9+' : visibleBadgeCount}
                </span>
              ) : null}
            </button>

            {notificationsOpen ? (
              <div className="absolute right-0 mt-4 w-[min(92vw,24rem)] overflow-hidden rounded-xl border border-slate-200 bg-white shadow-xl">
                <div className="border-b border-slate-100 px-4 py-3">
                  <div className="text-sm font-semibold text-slate-900">Уведомления</div>
                  <div className="mt-1 text-xs text-slate-500">Сроки и напоминания из журнала задач</div>
                </div>

                {taskNotifications.length > 0 ? (
                  <div className="max-h-96 overflow-y-auto py-2">
                    {taskNotifications.slice(0, 6).map((notification) => (
                      <button
                        key={notification.id}
                        type="button"
                        onClick={() => handleNotificationClick(notification)}
                        className="block w-full px-4 py-3 text-left transition-colors hover:bg-slate-50"
                      >
                        <div className="flex items-start gap-3">
                          <span
                            className={`mt-1 h-2.5 w-2.5 shrink-0 rounded-full ${
                              notification.tone === 'rose'
                                ? 'bg-rose-500'
                                : notification.tone === 'amber'
                                  ? 'bg-amber-500'
                                  : 'bg-blue-500'
                            }`}
                          />
                          <span className="min-w-0">
                            <span className="block text-sm font-semibold text-slate-900">{notification.title}</span>
                            <span className="mt-1 block text-sm leading-5 text-slate-600">{notification.description}</span>
                          </span>
                        </div>
                      </button>
                    ))}
                  </div>
                ) : (
                  <div className="px-4 py-5 text-sm text-slate-500">Новых уведомлений пока нет</div>
                )}
              </div>
            ) : null}
          </div>

          <div className="relative" ref={userMenuRef}>
            <button
              type="button"
              onClick={() => setUserMenuOpen((prev) => !prev)}
              className="flex items-center gap-2 rounded-full border border-slate-200 px-2 py-1.5 text-slate-700 transition-colors hover:border-slate-300"
              aria-label="Меню пользователя"
            >
              {user?.photoUrl && !avatarLoadFailed ? (
                <img
                  src={user.photoUrl}
                  alt={user.fullName ?? 'Профиль'}
                  className="h-8 w-8 rounded-full object-cover"
                  onError={() => setAvatarLoadFailed(true)}
                />
              ) : (
                <div className="flex h-8 w-8 items-center justify-center rounded-full bg-gradient-to-br from-blue-500 to-blue-600 text-xs font-semibold text-white">
                  {initials}
                </div>
              )}
            </button>

            {userMenuOpen ? (
              <div className="absolute right-0 mt-3 w-60 overflow-hidden rounded-xl border border-slate-200 bg-white shadow-xl">
                <div className="border-b border-slate-100 px-4 py-3">
                  <div className="text-sm font-semibold text-slate-900">
                    {user?.fullName ?? 'Пользователь'}
                  </div>
                  <div className="text-xs text-slate-500">{user?.email ?? '—'}</div>
                </div>

                <div className="py-2">
                  {isAdmin() ? (
                    <button
                      type="button"
                      onClick={() => {
                        setUserMenuOpen(false);
                        setStoredCompanyProfileCompanyId(null);
                        setStoredCompanyProfileMode('view');
                        setActiveTab('companyProfile');
                      }}
                      className="flex w-full items-center gap-3 px-4 py-2 text-sm text-slate-700 transition-colors hover:bg-slate-50"
                    >
                      <Building2 className="h-4 w-4 text-slate-400" />
                      Профиль компании
                    </button>
                  ) : null}

                  <button
                    type="button"
                    onClick={() => {
                      setUserMenuOpen(false);
                      onOpenProfile();
                    }}
                    className="flex w-full items-center gap-3 px-4 py-2 text-sm text-slate-700 transition-colors hover:bg-slate-50"
                  >
                    <User className="h-4 w-4 text-slate-400" />
                    Ваш профиль
                  </button>
                </div>

                <div className="border-t border-slate-100">
                  <button
                    type="button"
                    onClick={() => void handleSignOut()}
                    className="flex w-full items-center gap-3 px-4 py-2 text-sm text-red-600 transition-colors hover:bg-red-50"
                  >
                    <LogOut className="h-4 w-4" />
                    Выйти
                  </button>
                </div>
              </div>
            ) : null}
          </div>
        </div>
      </div>
    </header>
  );
}
