import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Bell, Building2, Info, LogOut, User } from 'lucide-react';
import { useAuth } from '../../contexts/AuthContext';
import { useTabNavigation } from '../../contexts/TabContext';
import { useDialog } from '../../hooks/useDialog';
import { apiService } from '../../services/api';
import { setStoredCompanyProfileCompanyId, setStoredCompanyProfileMode } from '../../utils/companyProfileBridge';
import { USER_NOTIFICATIONS_EVENT } from '../../utils/userNotifications';

type TopBarProps = {
  title: string;
  onSignOut: () => Promise<void>;
  onOpenProfile: () => void;
  onOpenNotifications: () => void;
  onOpenAppVersion: () => void;
};

const getInitials = (fullName?: string | null) =>
  (fullName ?? 'U')
    .split(' ')
    .map((part) => part.trim()[0])
    .filter(Boolean)
    .slice(0, 2)
    .join('')
    .toUpperCase();

export default function TopBar({ title, onSignOut, onOpenProfile, onOpenNotifications, onOpenAppVersion }: TopBarProps) {
  const { user, isAdmin } = useAuth();
  const { setActiveTab } = useTabNavigation();
  const { confirm } = useDialog();
  const [userMenuOpen, setUserMenuOpen] = useState(false);
  const [unreadCount, setUnreadCount] = useState(0);
  const [avatarLoadFailed, setAvatarLoadFailed] = useState(false);
  const userMenuRef = useRef<HTMLDivElement | null>(null);

  const initials = useMemo(() => getInitials(user?.fullName), [user?.fullName]);

  const refreshUnreadCount = useCallback(() => {
    apiService.getUnreadUserNotificationCount()
      .then((count) => setUnreadCount(count))
      .catch(() => setUnreadCount(0));
  }, []);

  useEffect(() => {
    setAvatarLoadFailed(false);
  }, [user?.photoUrl]);

  useEffect(() => {
    refreshUnreadCount();
    const timer = window.setInterval(refreshUnreadCount, 60_000);
    window.addEventListener(USER_NOTIFICATIONS_EVENT, refreshUnreadCount);

    return () => {
      window.clearInterval(timer);
      window.removeEventListener(USER_NOTIFICATIONS_EVENT, refreshUnreadCount);
    };
  }, [refreshUnreadCount]);

  useEffect(() => {
    const handleClickOutside = (event: MouseEvent) => {
      const target = event.target as Node;
      if (userMenuRef.current && !userMenuRef.current.contains(target)) {
        setUserMenuOpen(false);
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

  return (
    <header className="sticky top-0 z-20 border-b border-slate-200 bg-white/90 backdrop-blur">
      <div className="flex h-16 items-center justify-between px-6">
        <div className="text-sm font-semibold text-slate-700">{title}</div>

        <div className="flex items-center gap-3">
          <button
            type="button"
            onClick={onOpenNotifications}
            className="relative flex h-10 w-10 items-center justify-center rounded-full border border-slate-200 text-slate-500 transition-colors hover:border-slate-300 hover:text-slate-700"
            aria-label="Уведомления"
            title="Центр уведомлений"
          >
            <Bell className="h-5 w-5" />
            {unreadCount > 0 ? (
              <span className="absolute -right-1 -top-1 flex h-5 min-w-5 items-center justify-center rounded-full bg-rose-600 px-1 text-[11px] font-semibold text-white">
                {unreadCount > 9 ? '9+' : unreadCount}
              </span>
            ) : null}
          </button>

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
                  <button
                    type="button"
                    onClick={() => {
                      setUserMenuOpen(false);
                      onOpenAppVersion();
                    }}
                    className="flex w-full items-center gap-3 px-4 py-2 text-sm text-slate-700 transition-colors hover:bg-slate-50"
                  >
                    <Info className="h-4 w-4 text-slate-400" />
                    Что нового
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
