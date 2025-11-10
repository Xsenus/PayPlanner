import { useEffect, useState } from 'react';
import {
  fetchRolePermissions,
  getRolePermissions,
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
    let cancelled = false;
    if (!roleId) {
      return () => {
        cancelled = true;
      };
    }

    void fetchRolePermissions(roleId)
      .then((perms) => {
        if (!cancelled) {
          setPermissions(perms);
        }
      })
      .catch(() => undefined);

    return () => {
      cancelled = true;
    };
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
