/* eslint-disable react-refresh/only-export-components */
import { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from 'react';
import { authService, captureAuthSessionIdentity, isAuthSessionIdentityCurrent, getAuthOperationEpoch, type AuthSessionIdentity, type User } from '../services/authService';
import { AUTH_SESSION_EXPIRED_EVENT, apiService } from '../services/api';
import { API_BASE_URL } from '../services/apiBaseUrl';
import { bindPaymentCreateOwner } from '../services/paymentCreateAttempts';
import {
  activateOfflineCalendarUser,
  clearOfflineCalendarCache,
} from '../services/offlineCalendarStore';
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
  const operation = useRef(0);
  const mounted = useRef(true);
  const assignUser = useCallback((next: User | null) => {
    bindPaymentCreateOwner(next?.id ?? null, API_BASE_URL);
    setUser(next);
  }, []);

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

  const fetchUser = useCallback(async () => {
    const version = ++operation.current;
    let session: AuthSessionIdentity;
    try { session = captureAuthSessionIdentity(); }
    catch { if (mounted.current && operation.current === version) setLoading(false); return; }
    const current = () => mounted.current && operation.current === version && isAuthSessionIdentityCurrent(session);
    try {
      if (!authService.isAuthenticated()) { if (current()) assignUser(null); return; }
      const me = await authService.getCurrentUser();
      if (!current()) return;
      await activateOfflineCalendarUser(me.id, current);
      if (current()) assignUser(me);
    } catch (error) {
      if (!current()) return;
      console.error('Ошибка загрузки профиля:', error);
      await clearOfflineCalendarCache(current);
      if (!current()) return;
      authService.logout();
      assignUser(null);
    } finally { if (mounted.current && operation.current === version) setLoading(false); }
  }, [assignUser]);

  const refreshUser = async () => { await fetchUser(); };

  useEffect(() => {
    mounted.current = true;
    void fetchUser();
    return () => {
      mounted.current = false;
      operation.current += 1;
      bindPaymentCreateOwner(null, API_BASE_URL);
    };
  }, [fetchUser]);

  useEffect(() => {
    const handleSessionExpired = () => {
      const version = ++operation.current;
      bindPaymentCreateOwner(null, API_BASE_URL);
      authService.logout();
      assignUser(null);
      setLoading(false);
      const session = captureAuthSessionIdentity();
      void clearOfflineCalendarCache(() => mounted.current && operation.current === version && isAuthSessionIdentityCurrent(session));
    };
    window.addEventListener(AUTH_SESSION_EXPIRED_EVENT, handleSessionExpired);
    return () => window.removeEventListener(AUTH_SESSION_EXPIRED_EVENT, handleSessionExpired);
  }, [assignUser]);

  const signIn = async (email: string, password: string) => {
    const version = ++operation.current;
    bindPaymentCreateOwner(null, API_BASE_URL);
    const pending = authService.login(email, password);
    const epoch = getAuthOperationEpoch();
    const current = () => mounted.current && operation.current === version && getAuthOperationEpoch() === epoch;
    try {
      const resp = await pending;
      if (!current()) return;
      const session = captureAuthSessionIdentity();
      const same = () => current() && isAuthSessionIdentityCurrent(session);
      const resolvedUser = resp.user ?? (await authService.getCurrentUser());
      if (!same()) return;
      await activateOfflineCalendarUser(resolvedUser.id, same);
      if (!same()) return;
      assignUser(resolvedUser);
      logAuthEvent('login', `Вход пользователя ${resolvedUser.email}`, 'Success');
    } catch (error) { if (current()) throw error; }
    finally { if (mounted.current && operation.current === version) setLoading(false); }
  };

  const signInWithToken = async (token: string, nextUser?: User | null, provider?: string) => {
    const version = ++operation.current;
    bindPaymentCreateOwner(null, API_BASE_URL);
    const pending = authService.completeSignIn(token, nextUser);
    const session = captureAuthSessionIdentity();
    const current = () => mounted.current && operation.current === version && isAuthSessionIdentityCurrent(session);
    try {
      const resolvedUser = await pending;
      if (!current()) return;
      await activateOfflineCalendarUser(resolvedUser.id, current);
      if (!current()) return;
      assignUser(resolvedUser);
      logAuthEvent('login', `Вход пользователя ${resolvedUser.email}${provider ? ` (${provider})` : ''}`, 'Success');
    } catch (error) { if (current()) throw error; }
    finally { if (mounted.current && operation.current === version) setLoading(false); }
  };

  const signOut = async () => {
    const version = ++operation.current;
    const session = captureAuthSessionIdentity();
    const current = () => mounted.current && operation.current === version && isAuthSessionIdentityCurrent(session);
    bindPaymentCreateOwner(null, API_BASE_URL);
    try { await authService.revokeSessions(); }
    catch (error) { if (current() && import.meta.env.DEV) console.warn('Не удалось отозвать серверные сессии перед выходом', error); }
    if (!current()) return;
    await clearOfflineCalendarCache(current);
    if (!current()) return;
    authService.logout();
    assignUser(null);
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
