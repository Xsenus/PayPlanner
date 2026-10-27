import { useEffect, useState } from 'react';
import {
  getRolePermissions,
  hasLoadedRolePermissions,
  refreshRolePermissions,
  refreshRolePermissionsAfterSignal,
  subscribeOnPermissionsChange,
} from '../services/permissionsService';
import type { RolePermissions } from '../types/permissions';
import { USER_NOTIFICATIONS_EVENT, type UserNotificationsChangedDetail } from '../utils/userNotifications';

export function useRolePermissions(roleId?: number | null) {
  const [state, setState] = useState<{ roleId: number | null | undefined; permissions: RolePermissions }>(() => ({
    roleId, permissions: getRolePermissions(roleId),
  }));

  useEffect(() => {
    setState({ roleId, permissions: getRolePermissions(roleId) });
  }, [roleId]);

  useEffect(() => {
    const unsubscribe = subscribeOnPermissionsChange((changedRoleId) => {
      if (changedRoleId === undefined || changedRoleId === roleId) {
        setState({ roleId, permissions: getRolePermissions(roleId) });
      }
    });
    return unsubscribe;
  }, [roleId]);

  useEffect(() => {
    if (!roleId) return;
    const refresh = () => { void refreshRolePermissions(roleId); };
    const onRealtimeNotification = (event: Event) => {
      const detail = event instanceof CustomEvent ? event.detail as UserNotificationsChangedDetail | null : null;
      if (detail?.source === 'realtime'
        && (detail.reason === 'reconnected' || detail.reason === 'role-permissions-changed'
          || detail.notificationType === 'calendarAccessChanged')) {
        void refreshRolePermissionsAfterSignal(roleId, event);
      }
    };
    window.addEventListener('focus', refresh);
    window.addEventListener(USER_NOTIFICATIONS_EVENT, onRealtimeNotification);
    return () => {
      window.removeEventListener('focus', refresh);
      window.removeEventListener(USER_NOTIFICATIONS_EVENT, onRealtimeNotification);
    };
  }, [roleId]);

  return state.roleId === roleId ? state.permissions : getRolePermissions(roleId);
}

export function useRolePermissionsLoaded(roleId?: number | null) {
  const [loaded, setLoaded] = useState(() => hasLoadedRolePermissions(roleId));
  const loadedForCurrentRole = hasLoadedRolePermissions(roleId);

  useEffect(() => {
    setLoaded(hasLoadedRolePermissions(roleId));
  }, [roleId]);

  useEffect(() => {
    const unsubscribe = subscribeOnPermissionsChange((changedRoleId) => {
      if (changedRoleId === undefined || changedRoleId === roleId) {
        setLoaded(hasLoadedRolePermissions(roleId));
      }
    });
    return unsubscribe;
  }, [roleId]);

  return loaded && loadedForCurrentRole;
}
