import { useEffect, useRef, useState } from 'react';
import { apiService } from '../../services/api';
import type { DatabaseBackupDiscoveryPage } from '../../services/api';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';

export function DatabaseBackupDiscovery({ disabled, onSelect }: { disabled: boolean; onSelect: (paths: string[]) => void }) {
  const [page, setPage] = useState<DatabaseBackupDiscoveryPage | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [issues, setIssues] = useState<DatabaseBackupDiscoveryPage['issues']>([]);
  const [issueCount, setIssueCount] = useState(0);
  const lifecycle = useRef({ mounted: false });
  const running = useRef(false);
  useEffect(() => {
    const mount = { mounted: true };
    lifecycle.current = mount;
    return () => { mount.mounted = false; };
  }, []);

  const find = async (cursor?: string) => {
    if (running.current || disabled) return;
    running.current = true;
    const mount = lifecycle.current;
    setBusy(true); setError(null);
    if (!cursor) { setIssues([]); setIssueCount(0); }
    try {
      const response = await apiService.discoverDatabaseBackups(cursor);
      if (mount.mounted) {
        setPage(response);
        setIssues(values => [...values, ...response.issues].slice(0, 20));
        setIssueCount(value => value + response.issues.length);
      }
    } catch (failure) {
      if (mount.mounted) { setPage(null); setError(extractDisplayErrorMessage(failure, 'Не удалось выполнить поиск копий.')); }
    } finally {
      running.current = false;
      if (mount.mounted) setBusy(false);
    }
  };
  const selectable = page?.items.filter(item => !item.alreadyRegistered && item.hasManifest).map(item => item.relativePath) || [];
  return <section aria-label="Поиск старых резервных копий" className="mt-2 rounded-lg border border-slate-200 p-3">
    <div className="flex flex-wrap gap-2">
      <button type="button" disabled={busy || disabled} onClick={() => void find()}
        className="rounded-lg border px-3 py-2 text-sm disabled:opacity-50">Найти копии в каталоге</button>
      <button type="button" disabled={busy || disabled || !page?.nextCursor || !!error} onClick={() => void find(page?.nextCursor || undefined)}
        className="rounded-lg border px-3 py-2 text-sm disabled:opacity-50">Продолжить поиск</button>
      <button type="button" disabled={busy || disabled || selectable.length === 0} onClick={() => onSelect(selectable)}
        className="rounded-lg border px-3 py-2 text-sm disabled:opacity-50">Передать найденные на проверку ({selectable.length})</button>
    </div>
    <p className="mt-2 text-xs text-gray-500">Поиск просматривает только настроенный каталог резервных копий, без перехода по символическим ссылкам. Наличие описания ещё не означает целостность. Передача заменяет список путей ниже и ничего не записывает в журнал.</p>
    {busy && <p role="status" className="mt-2 text-sm">Поиск файлов…</p>}
    {error && <p role="alert" className="mt-2 text-sm text-red-700">{error}</p>}
    {page && !busy && <>
      <ul className="mt-2 max-h-52 space-y-1 overflow-auto text-sm">{page.items.map(item => <li key={item.relativePath} className="break-all">
        {item.relativePath} — {item.alreadyRegistered ? 'Уже есть в журнале' : item.hasManifest ? 'Описание найдено, нужна проверка' : 'Нет описания комплекта'}
      </li>)}</ul>
      {issues.map((issue, index) => <p key={`${issue.relativePath}:${index}`} className="mt-2 break-all text-sm text-amber-800">{issue.relativePath || 'Корень каталога'}: {issue.error}</p>)}
      {issueCount > 0 && <p className="mt-2 text-sm text-amber-800">Всего проблем поиска: {issueCount}. {issueCount > 20 ? 'Показаны первые 20.' : ''} Непросмотренные каталоги нельзя считать проверенными.</p>}
      <p className="mt-2 text-xs text-gray-500">{page.nextCursor ? 'Есть продолжение поиска; нажмите «Продолжить поиск», даже если эта страница пуста.' : 'Обход доступных каталогов завершён. Если файлы менялись во время поиска, начните заново.'}</p>
    </>}
  </section>;
}
