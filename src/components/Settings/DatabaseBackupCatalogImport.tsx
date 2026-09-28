import { useEffect, useRef, useState } from 'react';
import { apiService } from '../../services/api';
import type { DatabaseBackupCatalogImportResult } from '../../services/api';
import { useDialog } from '../../hooks/useDialog';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { DatabaseBackupDiscovery } from './DatabaseBackupDiscovery';

const labels = { ready: 'Можно добавить', imported: 'Добавлено', alreadyRegistered: 'Уже есть в журнале', failed: 'Не добавлено' };

export function DatabaseBackupCatalogImport({ onImported }: { onImported: () => void }) {
  const { confirm } = useDialog();
  const [text, setText] = useState('');
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<DatabaseBackupCatalogImportResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const lifecycle = useRef({ mounted: false });
  const running = useRef(false);
  const revision = useRef(0);
  const paths = [...new Set(text.split(/\r?\n/).map(value => value.trim()).filter(Boolean))];
  const ready = result?.dryRun ? result.items.filter(item => item.status === 'ready').map(item => item.relativePath) : [];

  useEffect(() => {
    const mount = { mounted: true };
    lifecycle.current = mount;
    return () => { mount.mounted = false; };
  }, []);

  const run = async (dryRun: boolean) => {
    if (running.current) return;
    running.current = true;
    const mount = lifecycle.current;
    const token = revision.current;
    setBusy(true);
    setError(null);
    try {
      if (!dryRun && !await confirm({
        title: 'Добавить старые копии в журнал',
        description: `Будут повторно проверены и зарегистрированы локальные комплекты: ${ready.length}. Файлы не изменяются, облачные копии не подтверждаются.`,
        confirmLabel: 'Добавить', cancelLabel: 'Отмена',
      })) return;
      if (!mount.mounted || token !== revision.current) return;
      const response = await apiService.importDatabaseBackupCatalog(dryRun ? paths : ready, dryRun);
      if (!mount.mounted || token !== revision.current) return;
      setResult(response);
      if (!dryRun && response.items.some(item => item.status === 'imported')) onImported();
    } catch (failure) {
      if (mount.mounted && token === revision.current)
        setError(extractDisplayErrorMessage(failure, 'Не удалось обработать старые копии.'));
    } finally {
      running.current = false;
      if (mount.mounted) setBusy(false);
    }
  };

  return <details className="mt-3 rounded-xl border border-slate-200 p-3">
    <summary className="cursor-pointer text-sm font-medium">Добавить старые локальные копии</summary>
    <p className="mt-2 text-xs text-gray-600">Скопируйте относительные пути из списка файлов ниже, по одному на строку. До 20 копий за раз. Сначала проверка без записи, затем добавление подтверждённых комплектов. Копии без полного описания и обязательных частей не добавляются.</p>
    <DatabaseBackupDiscovery disabled={busy} onSelect={values => {
      revision.current++; setText(values.join('\n')); setResult(null); setError(null);
    }} />
    <label className="mt-2 block text-sm" htmlFor="backup-import-paths">Пути старых копий</label>
    <textarea id="backup-import-paths" value={text} disabled={busy} rows={3} maxLength={40980}
      className="mt-1 w-full rounded-lg border border-slate-300 p-2 text-sm disabled:opacity-60"
      onChange={event => { revision.current++; setText(event.target.value); setResult(null); setError(null); }} />
    {paths.length > 20 && <p role="alert" className="text-sm text-red-700">Выберите не больше 20 копий за раз.</p>}
    <div className="mt-2 flex flex-wrap gap-2">
      <button type="button" disabled={busy || paths.length === 0 || paths.length > 20} onClick={() => void run(true)}
        className="rounded-lg border px-3 py-2 text-sm disabled:opacity-50">Проверить перед добавлением</button>
      <button type="button" disabled={busy || !ready?.length} onClick={() => void run(false)}
        className="rounded-lg border border-emerald-300 px-3 py-2 text-sm text-emerald-800 disabled:opacity-50">Добавить проверенные ({ready?.length || 0})</button>
    </div>
    {busy && <p role="status" className="mt-2 text-sm">Проверка и обработка комплектов…</p>}
    {error && <p role="alert" className="mt-2 text-sm text-red-700">{error}</p>}
    {result && <ul className="mt-2 max-h-60 space-y-2 overflow-auto text-sm">{result.items.map((item, index) =>
      <li key={`${item.relativePath}:${index}`} className="break-all">
        <span className="font-medium">{item.relativePath}</span> — {labels[item.status]}
        {item.error && <p className="text-red-700">{item.error}</p>}
      </li>)}</ul>}
    <p className="mt-2 text-xs text-gray-500">Сведения о старых облачных отправках не импортируются как проверенные копии. Для этого нужна проверенная повторная отправка.</p>
  </details>;
}
