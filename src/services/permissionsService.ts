import { defaultRolePermissions, type RolePermissions } from '../types/permissions';

const STORAGE_KEY = 'pp.role.permissions';
const CHANGE_EVENT = 'pp.permissions.changed';

type PermissionsMap = Record<string, RolePermissions>;

type ChangeListener = (roleId?: number) => void;

function readStorage(): PermissionsMap {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return {};
    const parsed = JSON.parse(raw);
    if (typeof parsed !== 'object' || parsed === null) return {};
    return parsed as PermissionsMap;
  } catch (error) {
    console.warn('Не удалось прочитать настройки прав ролей', error);
    return {};
  }
}

function writeStorage(data: PermissionsMap) {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(data));
  } catch (error) {
    console.warn('Не удалось сохранить настройки прав ролей', error);
  }
}

function clonePermissions(perms: RolePermissions): RolePermissions {
  return {
    calendar: { ...perms.calendar },
  };
}

export function getRolePermissions(roleId?: number | null): RolePermissions {
  if (!roleId) {
    return clonePermissions(defaultRolePermissions);
  }
  const map = readStorage();
  const key = String(roleId);
  const stored = map[key];
  return stored ? clonePermissions(stored) : clonePermissions(defaultRolePermissions);
}

export function setRolePermissions(roleId: number, permissions: RolePermissions) {
  const map = readStorage();
  map[String(roleId)] = clonePermissions(permissions);
  writeStorage(map);
  try {
    window.dispatchEvent(new CustomEvent(CHANGE_EVENT, { detail: { roleId } }));
  } catch {
    /* ignore */
  }
}

export function resetRolePermissions(roleId: number) {
  const map = readStorage();
  delete map[String(roleId)];
  writeStorage(map);
  try {
    window.dispatchEvent(new CustomEvent(CHANGE_EVENT, { detail: { roleId } }));
  } catch {
    /* ignore */
  }
}

export function subscribeOnPermissionsChange(listener: ChangeListener) {
  if (typeof window === 'undefined') {
    return () => undefined;
  }
  const handler = (event: Event) => {
    const detail = (event as CustomEvent<{ roleId?: number }>).detail;
    listener(detail?.roleId);
  };
  window.addEventListener(CHANGE_EVENT, handler as EventListener);
  return () => window.removeEventListener(CHANGE_EVENT, handler as EventListener);
}
