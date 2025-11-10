import { useEffect, useState } from 'react';
import { X, ShieldCheck } from 'lucide-react';
import type { Role } from './Roles';
import {
  getRolePermissions,
  setRolePermissions,
  resetRolePermissions,
} from '../../services/permissionsService';
import { defaultRolePermissions, type CalendarPermissionKey } from '../../types/permissions';
import { useTranslation } from '../../hooks/useTranslation';

interface RolePermissionsModalProps {
  role: Role;
  onClose: () => void;
}

const CALENDAR_PERMISSIONS: Array<{
  key: CalendarPermissionKey;
  labelKey: string;
  descriptionKey: string;
}> = [
  {
    key: 'canAddPayments',
    labelKey: 'permissionCalendarAdd',
    descriptionKey: 'permissionCalendarAddDescription',
  },
  {
    key: 'canEditPayments',
    labelKey: 'permissionCalendarEdit',
    descriptionKey: 'permissionCalendarEditDescription',
  },
  {
    key: 'canDeletePayments',
    labelKey: 'permissionCalendarDelete',
    descriptionKey: 'permissionCalendarDeleteDescription',
  },
  {
    key: 'canViewAnalytics',
    labelKey: 'permissionCalendarAnalytics',
    descriptionKey: 'permissionCalendarAnalyticsDescription',
  },
];

export function RolePermissionsModal({ role, onClose }: RolePermissionsModalProps) {
  const { t } = useTranslation();
  const [permissions, setPermissions] = useState(() => getRolePermissions(role.id));

  useEffect(() => {
    setPermissions(getRolePermissions(role.id));
  }, [role.id]);

  const togglePermission = (key: CalendarPermissionKey) => {
    setPermissions((prev) => ({
      calendar: {
        ...prev.calendar,
        [key]: !prev.calendar[key],
      },
    }));
  };

  const handleSave = () => {
    setRolePermissions(role.id, permissions);
    onClose();
  };

  const handleReset = () => {
    resetRolePermissions(role.id);
    setPermissions({
      calendar: { ...defaultRolePermissions.calendar },
    });
  };

  return (
    <div className="fixed inset-0 bg-black/50 z-50 flex items-center justify-center p-4">
      <div className="bg-white rounded-xl shadow-xl w-full max-w-2xl">
        <div className="flex items-center justify-between px-6 py-4 border-b border-slate-200">
          <div className="flex items-center gap-2">
            <ShieldCheck className="w-5 h-5 text-slate-700" />
            <div>
              <h2 className="text-lg font-semibold text-slate-900">
                {t('permissionsForRole', { role: role.name }) ?? `Права роли «${role.name}»`}
              </h2>
              <p className="text-sm text-slate-500">{t('calendarPermissionsTitle') ?? 'Управление доступом к функциям календаря'}</p>
            </div>
          </div>
          <button
            type="button"
            onClick={onClose}
            className="p-2 rounded-lg text-slate-400 hover:text-slate-600 hover:bg-slate-100 transition-colors">
            <X className="w-5 h-5" />
          </button>
        </div>

        <div className="px-6 py-4 space-y-4">
          {CALENDAR_PERMISSIONS.map(({ key, labelKey, descriptionKey }) => {
            const checked = permissions.calendar[key];
            return (
              <label
                key={key}
                className="flex items-start gap-4 rounded-lg border border-slate-200 p-4 hover:border-slate-300 transition-colors">
                <input
                  type="checkbox"
                  checked={checked}
                  onChange={() => togglePermission(key)}
                  className="mt-1 h-4 w-4 rounded border-slate-300 text-slate-900 focus:ring-slate-900"
                />
                <div>
                  <div className="text-sm font-semibold text-slate-900">
                    {t(labelKey) ?? labelKey}
                  </div>
                  <div className="text-sm text-slate-500 mt-1">
                    {t(descriptionKey) ?? descriptionKey}
                  </div>
                </div>
              </label>
            );
          })}
        </div>

        <div className="px-6 py-4 border-t border-slate-200 flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3">
          <button
            type="button"
            onClick={handleReset}
            className="inline-flex items-center justify-center gap-2 rounded-lg border border-slate-300 px-4 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50">
            {t('resetPermissions') ?? 'Сбросить по умолчанию'}
          </button>

          <div className="flex flex-col sm:flex-row gap-3">
            <button
              type="button"
              onClick={onClose}
              className="inline-flex items-center justify-center rounded-lg border border-slate-300 px-4 py-2 text-sm font-medium text-slate-600 hover:bg-slate-50">
              {t('cancel') ?? 'Отмена'}
            </button>
            <button
              type="button"
              onClick={handleSave}
              className="inline-flex items-center justify-center rounded-lg bg-slate-900 px-4 py-2 text-sm font-medium text-white hover:bg-slate-800">
              {t('savePermissions') ?? 'Сохранить права'}
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}
