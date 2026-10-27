/* eslint-disable react-refresh/only-export-components */
import { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from 'react';
import { authService, captureAuthSessionIdentity, isAuthSessionIdentityCurrent, getAuthOperationEpoch, type AuthSessionIdentity, type User } from '../services/authService';
import { AUTH_SESSION_EXPIRED_EVENT, apiService } from '../services/api';
import { API_BASE_URL } from '../services/apiBaseUrl';
import { bindPaymentCreateOwner } from '../services/paymentCreateAttempts';
import {
  activateOfflineCalendarUser,
  clearOfflineCalendarCache,
  revokeOfflineCalendarSnapshot,
} from '../services/offlineCalendarStore';
import { USER_NOTIFICATIONS_EVENT, type UserNotificationsChangedDetail } from '../utils/userNotifications';
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
  const userRef = useRef<User | null>(null);
  const roleRefreshPendingRef = useRef(false);
  const signingOutRef = useRef(false);
  const [loading, setLoading] = useState(true);
  const operation = useRef(0);
  const mounted = useRef(true);
  const assignUser = useCallback((next: User | null) => {
    bindPaymentCreateOwner(next?.id ?? null, API_BASE_URL);
    userRef.current = next;
    roleRefreshPendingRef.current = false;
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

  const fetchUser = useCallback(async (preserveOnTransientFailure = false) => {
    const version = ++operation.current;
    let session: AuthSessionIdentity;
    try { session = captureAuthSessionIdentity(); }
    catch { if (mounted.current && operation.current === version) setLoading(false); return; }
    const current = () => mounted.current && operation.current === version && isAuthSessionIdentityCurrent(session);
    try {
      if (!authService.isAuthenticated()) { if (current()) assignUser(null); return; }
      const me = await authService.getCurrentUser();
      if (!current()) return;
      const previous = userRef.current;
      if (previous?.id === me.id && previous.role?.id !== me.role?.id) {
        await revokeOfflineCalendarSnapshot(me.id);
        if (!current()) return;
      }
      await activateOfflineCalendarUser(me.id, current);
      if (current()) {
        assignUser(me);
      }
    } catch (error) {
      if (!current()) return;
      const status = typeof error === 'object' && error !== null && 'status' in error ? error.status : undefined;
      const code = typeof error === 'object' && error !== null && 'code' in error ? error.code : undefined;
      if (preserveOnTransientFailure && status !== 401 && status !== 403 && status !== 404
        && code !== 'UserInactive' && code !== 'PendingApproval' && code !== 'InvalidCredentials') return;
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
    const onUserRoleChanged = (event: Event) => {
      const detail = event instanceof CustomEvent ? event.detail as UserNotificationsChangedDetail | null : null;
      if (detail?.source === 'realtime'
        && (detail.reason === 'user-role-changed' || detail.reason === 'reconnected') && userRef.current && !signingOutRef.current) {
        roleRefreshPendingRef.current = true;
        void fetchUser(true);
      }
    };
    const retryPendingRoleRefresh = () => {
      if (roleRefreshPendingRef.current && userRef.current && !signingOutRef.current) void fetchUser(true);
    };
    window.addEventListener(USER_NOTIFICATIONS_EVENT, onUserRoleChanged);
    window.addEventListener('online', retryPendingRoleRefresh);
    window.addEventListener('focus', retryPendingRoleRefresh);
    return () => {
      window.removeEventListener(USER_NOTIFICATIONS_EVENT, onUserRoleChanged);
      window.removeEventListener('online', retryPendingRoleRefresh);
      window.removeEventListener('focus', retryPendingRoleRefresh);
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
    signingOutRef.current = false;
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
    signingOutRef.current = false;
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
    signingOutRef.current = true;
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
