import type { NavigationGroupDto } from '../../services/api';
import type { MenuSectionKey, RolePermissions } from '../../types/permissions';

// Several menu entries share a permission; changes apply wherever that permission is used.
const SECTION_BY_ITEM: Record<string, MenuSectionKey[]> = {
  calendar: ['calendar'], tasks: ['tasks'], notes: ['notes'], reports: ['reports'],
  calculator: ['calculator'], legalEntities: ['legalEntities'], clients: ['clients'],
  cases: ['cases'], courtCases: ['cases'], contacts: ['clients', 'legalEntities'],
  accounts: ['accounts'], installments: ['accounts'], money: ['payments'], payments: ['payments'],
  acts: ['acts'], contracts: ['contracts'], dictionaries: ['dictionaries'], roles: ['roles'],
  audit: ['audit'], settings: ['audit', 'notifications', 'integrations', 'yandexDisk', 's3Storage'],
  requests: ['requests'],
  // These pages are accessible only to the protected administrator role.
  users: [], userActivity: [], payroll: [],
};

export function navigationGroupSections(group: NavigationGroupDto) {
  const supported = (id: string) => Object.hasOwn(SECTION_BY_ITEM, id);
  const unsupported = group.itemIds.filter((id) => !supported(id));
  const sections = [...new Set(group.itemIds.flatMap((id) => supported(id) ? SECTION_BY_ITEM[id] : []))];
  return { sections, unsupported };
}

export function revokeNavigationGroup(permissions: RolePermissions, sections: MenuSectionKey[]): RolePermissions {
  const next = { ...permissions };
  for (const key of sections) {
    Object.assign(next, { [key]: {
      ...next[key], canView: false, canCreate: false, canEdit: false, canDelete: false, canExport: false,
      ...(key === 'calendar' || key === 'requests' ? { canViewAnalytics: false } : {}),
      ...(key === 'tasks' ? { canEditOthers: false, canCompleteOthers: false, canDeleteOthers: false } : {}),
    } });
  }
  return next;
}
