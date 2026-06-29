import { useEffect, useState } from 'react';
import {
  getRolePermissions,
  hasLoadedRolePermissions,
  subscribeOnPermissionsChange,
} from '../services/permissionsService';
import type { RolePermissions } from '../types/permissions';

export function useRolePermissions(roleId?: number | null) {
  const [permissions, setPermissions] = useState<RolePermissions>(() =>
    getRolePermissions(roleId),
  );

  useEffect(() => {
    setPermissions(getRolePermissions(roleId));
  }, [roleId]);

  useEffect(() => {
    const unsubscribe = subscribeOnPermissionsChange((changedRoleId) => {
      if (changedRoleId === undefined || changedRoleId === roleId) {
        setPermissions(getRolePermissions(roleId));
      }
    });
    return unsubscribe;
  }, [roleId]);

  return permissions;
}

export function useRolePermissionsLoaded(roleId?: number | null) {
  const [loaded, setLoaded] = useState(() => hasLoadedRolePermissions(roleId));

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

  return loaded;
}
