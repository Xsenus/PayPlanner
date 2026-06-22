export function toManagementReportApiFilters<T extends { exportDisabled?: boolean }>(
  filters?: T,
): Omit<T, 'exportDisabled'> {
  const apiFilters = { ...(filters ?? {}) };
  delete apiFilters.exportDisabled;
  return apiFilters as Omit<T, 'exportDisabled'>;
}
