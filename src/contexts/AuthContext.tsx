/* eslint-disable react-refresh/only-export-components */
import { createContext, useContext, useEffect, useState, type ReactNode } from 'react';
import { authService, type User } from '../services/authService';
import { apiService } from '../services/api';
import type { UserActivityStatus } from '../types/userActivity';

interface AuthContextType {
  user: User | null;
  loading: boolean;
  signIn: (email: string, password: string) => Promise<void>;
  signInWithToken: (token: string, user?: User | null, provider?: string) => Promise<void>;
  signOut: () => Promise<void>;
  isAdmin: () => boolean;
  isManager: () => boolean;
  refreshUser: () => Promise<void>;
}

const AuthContext = createContext<AuthContextType | undefined>(undefined);

export const useAuth = () => {
  const context = useContext(AuthContext);
  if (!context) {
    throw new Error('useAuth must be used within AuthProvider');
  }

  return context;
};

export const AuthProvider = ({ children }: { children: ReactNode }) => {
  const [user, setUser] = useState<User | null>(null);
  const [loading, setLoading] = useState(true);

  const logAuthEvent = (action: string, description: string, status: UserActivityStatus) => {
    void apiService
      .logUserActivity({
        category: 'auth',
        action,
        section: 'auth',
        description,
        status,
      })
      .catch((error) => {
        if (import.meta.env.DEV) {
          console.warn('Не удалось зафиксировать событие авторизации', error);
        }
      });
  };

  const fetchUser = async () => {
    try {
      if (!authService.isAuthenticated()) {
        setUser(null);
        return;
      }

      const me = await authService.getCurrentUser();
      setUser(me);
    } catch (error) {
      console.error('Ошибка загрузки профиля:', error);
      authService.logout();
      setUser(null);
    }
  };

  const refreshUser = async () => {
    await fetchUser();
  };

  useEffect(() => {
    fetchUser().finally(() => setLoading(false));
  }, []);

  const signIn = async (email: string, password: string) => {
    const resp = await authService.login(email, password);
    const resolvedUser = resp.user ?? (await authService.getCurrentUser());
    setUser(resolvedUser);

    logAuthEvent('login', `Вход пользователя ${resolvedUser.email}`, 'Success');
  };

  const signInWithToken = async (token: string, nextUser?: User | null, provider?: string) => {
    const resolvedUser = await authService.completeSignIn(token, nextUser);
    setUser(resolvedUser);

    logAuthEvent(
      'login',
      `Вход пользователя ${resolvedUser.email}${provider ? ` (${provider})` : ''}`,
      'Success',
    );
  };

  const signOut = async () => {
    logAuthEvent('logout', `Выход пользователя ${user?.email ?? 'unknown'}`, 'Info');
    authService.logout();
    setUser(null);
  };

  const isAdmin = () => (user?.role?.name ?? '').toLowerCase() === 'admin';
  const isManager = () => {
    const roleName = (user?.role?.name ?? '').toLowerCase();
    return roleName === 'manager' || roleName === 'admin';
  };

  const value: AuthContextType = {
    user,
    loading,
    signIn,
    signInWithToken,
    signOut,
    isAdmin,
    isManager,
    refreshUser,
  };

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
};
