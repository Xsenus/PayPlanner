import { useEffect, useMemo, useRef, useState } from 'react';
import { Bell, Building2, IdCard, LogOut, User } from 'lucide-react';
import { useAuth } from '../../contexts/AuthContext';

type TopBarProps = {
  title: string;
  onSignOut: () => Promise<void>;
};

const getInitials = (fullName?: string | null) =>
  (fullName ?? 'U')
    .split(' ')
    .map((part) => part.trim()[0])
    .filter(Boolean)
    .slice(0, 2)
    .join('')
    .toUpperCase();

export default function TopBar({ title, onSignOut }: TopBarProps) {
  const { user } = useAuth();
  const [userMenuOpen, setUserMenuOpen] = useState(false);
  const [notificationsOpen, setNotificationsOpen] = useState(false);
  const userMenuRef = useRef<HTMLDivElement | null>(null);
  const notificationsRef = useRef<HTMLDivElement | null>(null);

  const initials = useMemo(() => getInitials(user?.fullName), [user?.fullName]);

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
    if (confirm('Вы действительно хотите выйти из системы?')) {
      await onSignOut();
    }
  };

  return (
    <header className="sticky top-0 z-20 border-b border-slate-200 bg-white/90 backdrop-blur">
      <div className="flex h-16 items-center justify-between px-6">
        <div className="text-sm font-semibold text-slate-700">{title}</div>
        <div className="flex items-center gap-3">
          <div className="relative" ref={notificationsRef}>
            <button
              type="button"
              onClick={() => setNotificationsOpen((prev) => !prev)}
              className="flex h-10 w-10 items-center justify-center rounded-full border border-slate-200 text-slate-500 transition-colors hover:border-slate-300 hover:text-slate-700"
              aria-label="Уведомления">
              <Bell className="h-5 w-5" />
            </button>
            {notificationsOpen && (
              <div className="absolute right-0 mt-3 w-64 rounded-xl border border-slate-200 bg-white p-4 text-sm text-slate-600 shadow-xl">
                Нет уведомлений
              </div>
            )}
          </div>

          <div className="relative" ref={userMenuRef}>
            <button
              type="button"
              onClick={() => setUserMenuOpen((prev) => !prev)}
              className="flex items-center gap-2 rounded-full border border-slate-200 px-2 py-1.5 text-slate-700 transition-colors hover:border-slate-300"
              aria-label="Меню пользователя">
              {user?.photoUrl ? (
                <img
                  src={user.photoUrl}
                  alt={user.fullName ?? 'Профиль'}
                  className="h-8 w-8 rounded-full object-cover"
                />
              ) : (
                <div className="flex h-8 w-8 items-center justify-center rounded-full bg-gradient-to-br from-blue-500 to-blue-600 text-xs font-semibold text-white">
                  {initials}
                </div>
              )}
            </button>

            {userMenuOpen && (
              <div className="absolute right-0 mt-3 w-72 overflow-hidden rounded-xl border border-slate-200 bg-white shadow-xl">
                <div className="border-b border-slate-100 px-4 py-3">
                  <div className="text-sm font-semibold text-slate-900">{user?.fullName ?? 'Пользователь'}</div>
                  <div className="text-xs text-slate-500">{user?.email ?? '—'}</div>
                </div>
                <div className="py-2">
                  <button
                    type="button"
                    onClick={() => setUserMenuOpen(false)}
                    className="flex w-full items-center gap-3 px-4 py-2 text-sm text-slate-700 transition-colors hover:bg-slate-50">
                    <Building2 className="h-4 w-4 text-slate-400" />
                    Профиль компании
                  </button>
                  <button
                    type="button"
                    onClick={() => setUserMenuOpen(false)}
                    className="flex w-full items-center gap-3 px-4 py-2 text-sm text-slate-700 transition-colors hover:bg-slate-50">
                    <IdCard className="h-4 w-4 text-slate-400" />
                    Визитка Компании
                  </button>
                  <button
                    type="button"
                    onClick={() => setUserMenuOpen(false)}
                    className="flex w-full items-center gap-3 px-4 py-2 text-sm text-slate-700 transition-colors hover:bg-slate-50">
                    <User className="h-4 w-4 text-slate-400" />
                    Ваш профиль
                  </button>
                </div>
                <div className="border-t border-slate-100">
                  <button
                    type="button"
                    onClick={handleSignOut}
                    className="flex w-full items-center gap-3 px-4 py-2 text-sm text-red-600 transition-colors hover:bg-red-50">
                    <LogOut className="h-4 w-4" />
                    Выход
                  </button>
                </div>
              </div>
            )}
          </div>
        </div>
      </div>
    </header>
  );
}
