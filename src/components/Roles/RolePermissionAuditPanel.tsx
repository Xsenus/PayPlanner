import { useCallback, useEffect, useMemo, useState } from 'react';
import { AlertTriangle, CheckCircle2, Download, RefreshCw, ShieldAlert } from 'lucide-react';
import { apiService, type RolePermissionAuditReportDto, type RolePermissionAuditRoleDto } from '../../services/api';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';

function formatDateTime(value?: string | null) {
  if (!value) return '—';

  return new Date(value).toLocaleString('ru-RU', {
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  });
}

function countRoleWarnings(role: RolePermissionAuditRoleDto) {
  return role.broadDataScopeSections.length;
}

function countRolesWithBroadDataScope(report: RolePermissionAuditReportDto | null) {
  return report?.rolesWithBroadDataScope ?? report?.roles.filter((role) => countRoleWarnings(role) > 0).length ?? 0;
}

function summarizeRoleIssues(role: RolePermissionAuditRoleDto) {
  const parts: string[] = [];

  if (role.missingSections.length > 0) parts.push(`нет секций: ${role.missingSections.length}`);
  if (role.unknownSections.length > 0) parts.push(`устаревшие секции: ${role.unknownSections.length}`);
  if (role.duplicateSections.length > 0) parts.push(`дубли: ${role.duplicateSections.length}`);
  if (role.invalidViewScopeSections.length > 0) parts.push(`scope: ${role.invalidViewScopeSections.length}`);
  if (role.capabilityWithoutViewSections.length > 0) parts.push(`операции без просмотра: ${role.capabilityWithoutViewSections.length}`);
  if (role.restrictedCapabilitySections.length > 0) parts.push(`запрещенные операции: ${role.restrictedCapabilitySections.length}`);

  return parts.join(', ') || 'без критичных замечаний';
}

function summarizeStrictWarnings(warningCount: number) {
  if (warningCount === 0) return 'Strict-проверка: замечаний по широкой видимости нет.';

  return `Strict-проверка: требуется согласовать роли с широкой видимостью данных (${warningCount}).`;
}

export function RolePermissionAuditPanel() {
  const [report, setReport] = useState<RolePermissionAuditReportDto | null>(null);
  const [loading, setLoading] = useState(true);
  const [exporting, setExporting] = useState(false);
  const [errorMessage, setErrorMessage] = useState<string | null>(null);

  const loadAudit = useCallback(async () => {
    setLoading(true);
    setErrorMessage(null);

    try {
      setReport(await apiService.getRolePermissionsAudit());
    } catch (error) {
      setErrorMessage(extractDisplayErrorMessage(error, 'Не удалось загрузить аудит ролей'));
      setReport(null);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void loadAudit();
  }, [loadAudit]);

  const exportAuditCsv = useCallback(async () => {
    setExporting(true);
    setErrorMessage(null);

    try {
      const response = await apiService.exportRolePermissionsAuditCsv();
      const url = URL.createObjectURL(response.blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = response.fileName || 'role-permissions-audit.csv';
      document.body.appendChild(link);
      link.click();
      link.remove();
      URL.revokeObjectURL(url);
    } catch (error) {
      setErrorMessage(extractDisplayErrorMessage(error, 'Не удалось выгрузить аудит ролей'));
    } finally {
      setExporting(false);
    }
  }, []);

  const rolesWithWarnings = useMemo(
    () => report?.roles.filter((role) => countRoleWarnings(role) > 0) ?? [],
    [report],
  );
  const rolesWithBroadDataScope = countRolesWithBroadDataScope(report);

  const visibleRoles = useMemo(
    () =>
      report?.roles
        .filter((role) => role.hasIssues || countRoleWarnings(role) > 0)
        .slice(0, 5) ?? [],
    [report],
  );

  const hasStrictWarnings = rolesWithBroadDataScope > 0;
  const statusTone = report && report.rolesWithIssues === 0 && !hasStrictWarnings ? 'emerald' : 'amber';
  const StatusIcon = report && report.rolesWithIssues === 0 && !hasStrictWarnings ? CheckCircle2 : ShieldAlert;

  return (
    <section className="mb-6 rounded-lg border border-slate-200 bg-white p-5 shadow-sm">
      <div className="flex flex-col gap-4 lg:flex-row lg:items-start lg:justify-between">
        <div className="flex items-start gap-3">
          <div
            className={`rounded-lg p-2 ${
              statusTone === 'emerald' ? 'bg-emerald-50 text-emerald-700' : 'bg-amber-50 text-amber-700'
            }`}
          >
            <StatusIcon className="h-5 w-5" />
          </div>
          <div>
            <h2 className="text-lg font-semibold text-slate-900">Аудит готовности ролей</h2>
            <p className="mt-1 text-sm leading-6 text-slate-600">
              {loading
                ? 'Проверяем каталог прав и области видимости.'
                : report
                  ? `Готово: ${report.readyRoles} из ${report.totalRoles}. Проблемы: ${report.rolesWithIssues}. Широкий доступ: ${rolesWithBroadDataScope}.`
                  : 'Аудит ролей недоступен.'}
            </p>
            {report ? (
              <>
                <div className="mt-1 text-xs text-slate-500">
                  Обновлено: {formatDateTime(report.generatedAtUtc)}
                </div>
                <div className={`mt-2 text-xs font-medium ${hasStrictWarnings ? 'text-amber-700' : 'text-emerald-700'}`}>
                  {summarizeStrictWarnings(rolesWithBroadDataScope)}
                </div>
              </>
            ) : null}
          </div>
        </div>

        <div className="flex flex-wrap gap-2">
          <button
            type="button"
            onClick={() => void exportAuditCsv()}
            disabled={loading || exporting}
            className="inline-flex items-center justify-center gap-2 rounded-lg border border-slate-300 px-3 py-2 text-sm font-medium text-slate-700 transition-colors hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-60"
          >
            <Download className={`h-4 w-4 ${exporting ? 'animate-pulse' : ''}`} />
            CSV
          </button>
          <button
            type="button"
            onClick={() => void loadAudit()}
            disabled={loading}
            className="inline-flex items-center justify-center gap-2 rounded-lg border border-slate-300 px-3 py-2 text-sm font-medium text-slate-700 transition-colors hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-60"
          >
            <RefreshCw className={`h-4 w-4 ${loading ? 'animate-spin' : ''}`} />
            Обновить
          </button>
        </div>
      </div>

      {errorMessage ? (
        <div className="mt-4 rounded-lg border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700">
          {errorMessage}
        </div>
      ) : null}

      {visibleRoles.length > 0 ? (
        <div className="mt-5 grid gap-3 xl:grid-cols-2">
          {visibleRoles.map((role) => (
            <div key={role.roleId} className="rounded-lg border border-slate-200 bg-slate-50 px-4 py-3">
              <div className="flex items-center justify-between gap-3">
                <div className="min-w-0">
                  <div className="truncate text-sm font-semibold text-slate-900">{role.roleName}</div>
                  <div className="mt-1 text-xs text-slate-500">
                    {role.configuredSectionsCount} из {role.totalSectionsCount} секций, пользователей: {role.assignedUsersCount}
                  </div>
                </div>
                {role.hasIssues ? (
                  <span className="inline-flex shrink-0 items-center gap-1 rounded-full border border-amber-200 bg-amber-50 px-2 py-1 text-xs font-medium text-amber-700">
                    <AlertTriangle className="h-3.5 w-3.5" />
                    Проверить
                  </span>
                ) : (
                  <span className="inline-flex shrink-0 items-center rounded-full border border-blue-200 bg-blue-50 px-2 py-1 text-xs font-medium text-blue-700">
                    Strict
                  </span>
                )}
              </div>
              <div className="mt-3 text-sm leading-6 text-slate-600">{summarizeRoleIssues(role)}</div>
              {role.broadDataScopeSections.length > 0 ? (
                <div className="mt-2 text-xs text-slate-500">
                  Полная видимость: {role.broadDataScopeSections.join(', ')}
                </div>
              ) : null}
            </div>
          ))}
        </div>
      ) : null}
    </section>
  );
}
