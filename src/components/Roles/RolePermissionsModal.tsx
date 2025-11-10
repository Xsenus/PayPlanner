import { useEffect, useState } from 'react';
import {
  X,
  ShieldCheck,
  CalendarDays,
  BarChart3,
  Calculator,
  Users,
  WalletCards,
  FileCheck2,
  FileSignature,
  Settings,
  Lock,
  Unlock,
  Loader2,
} from 'lucide-react';
import type { LucideIcon } from 'lucide-react';
import type { Role } from './Roles';
import {
  fetchRolePermissions,
  getRolePermissions,
  setRolePermissions,
  resetRolePermissions,
} from '../../services/permissionsService';
import {
  MENU_PERMISSION_KEYS,
  type MenuPermissionKey,
  type MenuSectionKey,
  type RolePermissions,
} from '../../types/permissions';
import { useTranslation } from '../../hooks/useTranslation';

interface RolePermissionsModalProps {
  role: Role;
  onClose: () => void;
}

type SectionConfig = {
  key: MenuSectionKey;
  titleKey: string;
  descriptionKey: string;
  icon: LucideIcon;
};

type PermissionMeta = {
  key: MenuPermissionKey;
  labelKey: string;
  descriptionKey: string;
};

const SECTION_CONFIG: SectionConfig[] = [
  {
    key: 'calendar',
    titleKey: 'calendar',
    descriptionKey: 'permissionSectionCalendar',
    icon: CalendarDays,
  },
  {
    key: 'reports',
    titleKey: 'reports',
    descriptionKey: 'permissionSectionReports',
    icon: BarChart3,
  },
  {
    key: 'calculator',
    titleKey: 'calculator',
    descriptionKey: 'permissionSectionCalculator',
    icon: Calculator,
  },
  {
    key: 'clients',
    titleKey: 'clients',
    descriptionKey: 'permissionSectionClients',
    icon: Users,
  },
  {
    key: 'accounts',
    titleKey: 'accounts',
    descriptionKey: 'permissionSectionAccounts',
    icon: WalletCards,
  },
  {
    key: 'acts',
    titleKey: 'acts',
    descriptionKey: 'permissionSectionActs',
    icon: FileCheck2,
  },
  {
    key: 'contracts',
    titleKey: 'contracts',
    descriptionKey: 'permissionSectionContracts',
    icon: FileSignature,
  },
  {
    key: 'dictionaries',
    titleKey: 'dictionaries',
    descriptionKey: 'permissionSectionDictionaries',
    icon: Settings,
  },
];

const PERMISSION_META: PermissionMeta[] = [
  {
    key: 'canView',
    labelKey: 'permissionView',
    descriptionKey: 'permissionViewDescription',
  },
  {
    key: 'canCreate',
    labelKey: 'permissionCreate',
    descriptionKey: 'permissionCreateDescription',
  },
  {
    key: 'canEdit',
    labelKey: 'permissionEdit',
    descriptionKey: 'permissionEditDescription',
  },
  {
    key: 'canDelete',
    labelKey: 'permissionDelete',
    descriptionKey: 'permissionDeleteDescription',
  },
  {
    key: 'canExport',
    labelKey: 'permissionExport',
    descriptionKey: 'permissionExportDescription',
  },
];

const CALENDAR_EXTRA_META = {
  key: 'canViewAnalytics' as const,
  labelKey: 'permissionCalendarAnalytics',
  descriptionKey: 'permissionCalendarAnalyticsDescription',
};

type PermissionToggleProps = {
  label: string;
  description?: string;
  checked: boolean;
  onChange: () => void;
  disabled?: boolean;
};

function PermissionToggle({ label, description, checked, onChange, disabled }: PermissionToggleProps) {
  return (
    <label
      className={`flex items-start gap-4 rounded-lg border border-slate-200 p-4 transition-colors ${
        disabled ? 'opacity-60 cursor-not-allowed' : 'hover:border-slate-300 cursor-pointer'
      }`}>
      <input
        type="checkbox"
        className="mt-1 h-4 w-4 rounded border-slate-300 text-slate-900 focus:ring-slate-900"
        checked={checked}
        onChange={onChange}
        disabled={disabled}
      />
      <div>
        <div className="text-sm font-semibold text-slate-900">{label}</div>
        {description ? <div className="text-xs text-slate-500 mt-1 leading-relaxed">{description}</div> : null}
      </div>
    </label>
  );
}

export function RolePermissionsModal({ role, onClose }: RolePermissionsModalProps) {
  const { t } = useTranslation();
  const [permissions, setPermissions] = useState<RolePermissions>(() => getRolePermissions(role.id));
  const [modalError, setModalError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    setPermissions(getRolePermissions(role.id));
    setModalError(null);
    setLoading(true);

    let cancelled = false;
    void fetchRolePermissions(role.id)
      .then((perms) => {
        if (!cancelled) {
          setPermissions(perms);
        }
      })
      .catch((error) => {
        if (!cancelled) {
          setModalError(
            error instanceof Error ? error.message : 'Не удалось загрузить права роли',
          );
        }
      })
      .finally(() => {
        if (!cancelled) {
          setLoading(false);
        }
      });

    return () => {
      cancelled = true;
    };
  }, [role.id]);

  const updateSection = <K extends MenuSectionKey>(
    key: K,
    updater: (section: RolePermissions[K]) => RolePermissions[K],
  ) => {
    setPermissions((prev) => ({
      ...prev,
      [key]: updater(prev[key]),
    }));
  };

  const toggleBasePermission = (sectionKey: MenuSectionKey, permissionKey: MenuPermissionKey) => {
    updateSection(sectionKey, (section) => {
      const nextValue = !section[permissionKey];
      const updated = {
        ...section,
        [permissionKey]: nextValue,
      } as typeof section;

      if (permissionKey === 'canView' && !nextValue) {
        MENU_PERMISSION_KEYS.forEach((key) => {
          if (key === 'canView') return;
          updated[key] = false;
        });
        if (sectionKey === 'calendar') {
          (updated as RolePermissions['calendar']).canViewAnalytics = false;
        }
      }

      return updated;
    });
  };

  const toggleCalendarExtra = () => {
    updateSection('calendar', (section) => ({
      ...section,
      canViewAnalytics: !section.canViewAnalytics,
    }));
  };

  const setSectionAll = (sectionKey: MenuSectionKey, value: boolean) => {
    updateSection(sectionKey, (section) => {
      const next = {
        ...section,
        canView: value,
        canCreate: value,
        canEdit: value,
        canDelete: value,
        canExport: value,
      } as typeof section;
      if (sectionKey === 'calendar') {
        (next as RolePermissions['calendar']).canViewAnalytics = value;
      }
      return next;
    });
  };

  const handleSave = async () => {
    try {
      setSaving(true);
      setModalError(null);
      await setRolePermissions(role.id, permissions);
      onClose();
    } catch (error) {
      setModalError(error instanceof Error ? error.message : 'Не удалось сохранить права роли');
    } finally {
      setSaving(false);
    }
  };

  const handleReset = async () => {
    try {
      setSaving(true);
      setModalError(null);
      const updated = await resetRolePermissions(role.id);
      setPermissions(updated);
    } catch (error) {
      setModalError(error instanceof Error ? error.message : 'Не удалось сбросить права');
    } finally {
      setSaving(false);
    }
  };

  const sectionTitle = (key: string) => t(key) ?? key;
  const sectionDescription = (key: string) => t(key) ?? key;

  return (
    <div className="fixed inset-0 bg-black/50 z-50 flex items-center justify-center p-4">
      <div className="bg-white rounded-xl shadow-xl w-full max-w-3xl">
        <div className="flex items-center justify-between px-6 py-4 border-b border-slate-200">
          <div className="flex items-center gap-2">
            <ShieldCheck className="w-5 h-5 text-slate-700" />
            <div>
              <h2 className="text-lg font-semibold text-slate-900">
                {t('permissionsForRole', { role: role.name }) ?? `Права роли «${role.name}»`}
              </h2>
              <p className="text-sm text-slate-500">
                {t('permissionsModalSubtitle') ?? 'Настройте доступ к разделам и операциям'}
              </p>
            </div>
          </div>
          <button
            type="button"
            onClick={onClose}
            className="p-2 rounded-lg text-slate-400 hover:text-slate-600 hover:bg-slate-100 transition-colors">
            <X className="w-5 h-5" />
          </button>
        </div>

        <div className="px-6 py-5 space-y-6 max-h-[70vh] overflow-y-auto">
          {modalError ? (
            <div className="rounded-lg border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700">
              {modalError}
            </div>
          ) : null}

          {loading ? (
            <div className="flex items-center justify-center py-10 text-slate-500">
              <Loader2 className="mr-2 h-5 w-5 animate-spin" />
              {t('loading') ?? 'Загрузка...'}
            </div>
          ) : (
            SECTION_CONFIG.map(({ key, icon: Icon, titleKey, descriptionKey }) => {
              const section = permissions[key];
              return (
                <section key={key} className="rounded-xl border border-slate-200">
                  <div className="flex flex-col gap-4 border-b border-slate-200 px-5 py-4 sm:flex-row sm:items-center sm:justify-between">
                    <div className="flex items-start gap-3">
                      <div className="mt-0.5 rounded-lg bg-slate-100 p-2 text-slate-600">
                        <Icon className="h-5 w-5" />
                      </div>
                      <div>
                        <h3 className="text-base font-semibold text-slate-900">{sectionTitle(titleKey)}</h3>
                        <p className="text-sm text-slate-500 mt-1">{sectionDescription(descriptionKey)}</p>
                      </div>
                    </div>
                  <div className="flex flex-wrap gap-2">
                    <button
                      type="button"
                      onClick={() => setSectionAll(key, true)}
                      disabled={loading || saving}
                      className="inline-flex items-center gap-1 rounded-lg border border-emerald-200 px-3 py-1.5 text-xs font-medium text-emerald-700 hover:bg-emerald-50 disabled:cursor-not-allowed disabled:opacity-50">
                      <Unlock className="h-3.5 w-3.5" />
                      {t('permissionAllowAll') ?? 'Разрешить всё'}
                    </button>
                    <button
                      type="button"
                      onClick={() => setSectionAll(key, false)}
                      disabled={loading || saving}
                      className="inline-flex items-center gap-1 rounded-lg border border-slate-200 px-3 py-1.5 text-xs font-medium text-slate-600 hover:bg-slate-100 disabled:cursor-not-allowed disabled:opacity-50">
                      <Lock className="h-3.5 w-3.5" />
                      {t('permissionDenyAll') ?? 'Запретить всё'}
                    </button>
                  </div>
                </div>
                <div className="grid gap-3 px-5 py-4 sm:grid-cols-2">
                  {PERMISSION_META.map(({ key: permKey, labelKey, descriptionKey: descKey }) => (
                    <PermissionToggle
                      key={permKey}
                      label={sectionTitle(labelKey)}
                      description={sectionDescription(descKey)}
                      checked={section[permKey]}
                      onChange={() => toggleBasePermission(key, permKey)}
                      disabled={
                        loading || saving || (permKey !== 'canView' && !section.canView)
                      }
                    />
                  ))}
                  {key === 'calendar' ? (
                    <PermissionToggle
                      label={sectionTitle(CALENDAR_EXTRA_META.labelKey)}
                      description={sectionDescription(CALENDAR_EXTRA_META.descriptionKey)}
                      checked={(section as RolePermissions['calendar']).canViewAnalytics}
                      onChange={toggleCalendarExtra}
                      disabled={loading || saving || !section.canView}
                    />
                  ) : null}
                </div>
              </section>
              );
            })
          )}
        </div>

        <div className="px-6 py-4 border-t border-slate-200 flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3">
          <button
            type="button"
            onClick={() => {
              void handleReset();
            }}
            disabled={loading || saving}
            className="inline-flex items-center justify-center gap-2 rounded-lg border border-slate-300 px-4 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-50">
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
              onClick={() => {
                void handleSave();
              }}
              disabled={saving || loading}
              className="inline-flex items-center justify-center rounded-lg bg-slate-900 px-4 py-2 text-sm font-medium text-white hover:bg-slate-800 disabled:cursor-not-allowed disabled:opacity-60">
              {saving && <Loader2 className="mr-2 h-4 w-4 animate-spin" />}
              {t('savePermissions') ?? 'Сохранить права'}
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}
