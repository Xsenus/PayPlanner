import { useCallback, useEffect, useRef, useState } from 'react';
import { Bot, Loader2, RefreshCw, Unlink2 } from 'lucide-react';
import { apiService, type MaxEmployeeLink, type MaxEmployeeLinkStatus } from '../../services/api';
import { useDialog } from '../../hooks/useDialog';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';

const STATUS_LABELS: Record<MaxEmployeeLinkStatus['status'], string> = {
  unlinked: 'Не подключён', pending: 'Ожидается подтверждение в MAX', expired: 'Срок ссылки истёк',
  verified: 'Подключён', 'opted-out': 'Отключён', blocked: 'Привязка отозвана',
};

function safeBotLink(value: string | null): string | null {
  if (!value) return null;
  try {
    const url = new URL(value);
    return url.protocol === 'https:' && url.hostname === 'max.ru' && !url.username && !url.password
      ? url.href : null;
  } catch { return null; }
}

export function MaxMessengerPanel() {
  const { confirm } = useDialog();
  const [status, setStatus] = useState<MaxEmployeeLinkStatus | null>(null);
  const [link, setLink] = useState<MaxEmployeeLink | null>(null);
  const [busy, setBusy] = useState(true);
  const [error, setError] = useState('');
  const mounted = useRef(false);
  const actionPending = useRef(false);
  const requestId = useRef(0);
  const invalidateRequests = useCallback(() => { ++requestId.current; }, []);

  const loadStatus = useCallback(async () => {
    const currentRequest = ++requestId.current;
    let next: MaxEmployeeLinkStatus;
    try { next = await apiService.getEmployeeMaxLinkStatus(); }
    catch (err) { if (mounted.current && currentRequest === requestId.current) throw err; return; }
    if (!mounted.current || currentRequest !== requestId.current) return;
    setStatus(next);
    setLink(previous => next.status === 'pending' && previous?.linkId === next.linkId ? previous : null);
    setError('');
  }, []);

  useEffect(() => {
    mounted.current = true;
    const initialLoad = loadStatus();
    const initialRequest = requestId.current;
    void initialLoad
      .catch(err => { if (mounted.current) setError(extractDisplayErrorMessage(err, 'Не удалось загрузить состояние MAX.')); })
      .finally(() => { if (mounted.current && initialRequest === requestId.current) setBusy(false); });
    return () => { mounted.current = false; invalidateRequests(); };
  }, [loadStatus, invalidateRequests]);

  const refresh = useCallback(async () => {
    if (actionPending.current) return;
    actionPending.current = true;
    setBusy(true);
    try { await loadStatus(); }
    catch (err) { if (mounted.current) setError(extractDisplayErrorMessage(err, 'Не удалось обновить состояние MAX.')); }
    finally { actionPending.current = false; if (mounted.current) setBusy(false); }
  }, [loadStatus]);

  useEffect(() => {
    if (status?.status !== 'pending' || !status.expiresAt) return;
    const expiresAt = Date.parse(status.expiresAt);
    if (!Number.isFinite(expiresAt)) return;
    const timer = window.setTimeout(() => {
      setLink(null);
      setStatus(previous => previous?.status === 'pending' ? { ...previous, status: 'expired' } : previous);
      void refresh();
    }, Math.max(0, expiresAt - Date.now()));
    const onFocus = () => { void refresh(); };
    window.addEventListener('focus', onFocus);
    return () => { window.clearTimeout(timer); window.removeEventListener('focus', onFocus); };
  }, [status?.status, status?.expiresAt, refresh]);

  const connect = async () => {
    if (actionPending.current || !status?.channelReady) return;
    actionPending.current = true;
    setBusy(true);
    setError('');
    setLink(null);
    ++requestId.current;
    try {
      const created = await apiService.createEmployeeMaxLink();
      const url = safeBotLink(created.deepLinkUrl);
      if (!url || !Number.isFinite(Date.parse(created.expiresAt)) || Date.parse(created.expiresAt) <= Date.now()) {
        throw new Error('Ссылка подключения MAX недоступна. Создайте новую ссылку.');
      }
      if (!mounted.current) return;
      setLink({ linkId: created.linkId, deepLinkUrl: url, expiresAt: created.expiresAt });
      setStatus({ linkId: created.linkId, status: 'pending', channelReady: true, expiresAt: created.expiresAt, verifiedAt: null });
    } catch (err) { if (mounted.current) setError(extractDisplayErrorMessage(err, 'Не удалось создать ссылку MAX.')); }
    finally { actionPending.current = false; if (mounted.current) setBusy(false); }
  };

  const disconnect = async () => {
    if (actionPending.current) return;
    actionPending.current = true;
    setBusy(true);
    ++requestId.current;
    try {
      if (!await confirm({ title: 'Отключить MAX?', description: 'Уведомления CRM перестанут поступать в ваш MAX. Настройки других сотрудников сохранятся.', confirmLabel: 'Отключить', cancelLabel: 'Отмена' })) return;
      if (!mounted.current) return;
      await apiService.deleteEmployeeMaxLink();
      if (!mounted.current) return;
      setLink(null);
      setStatus(previous => previous ? { ...previous, status: 'opted-out', expiresAt: null, verifiedAt: null } : previous);
      await loadStatus();
    } catch (err) { if (mounted.current) setError(extractDisplayErrorMessage(err, 'Не удалось отключить MAX.')); }
    finally { actionPending.current = false; if (mounted.current) setBusy(false); }
  };

  return (
    <section aria-label="Уведомления в MAX" className="space-y-3 rounded-2xl border border-slate-200 bg-white p-3 shadow-sm">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 className="inline-flex items-center gap-2 text-base font-semibold text-slate-900"><Bot className="h-5 w-5" />Уведомления в MAX</h2>
        <span role="status" className="text-sm text-slate-600">{status ? STATUS_LABELS[status.status] : busy ? 'Загрузка…' : 'Состояние недоступно'}</span>
      </div>
      <p className="text-sm text-slate-600">Подключите свой аккаунт MAX, чтобы получать уведомления CRM от бота организации.</p>
      {status && !status.channelReady ? <p className="text-sm text-amber-700">Канал MAX отключён или требует настройки администратором.</p> : null}
      {status?.status === 'pending' ? <p className="text-sm text-slate-600">Откройте бота по персональной ссылке и нажмите «Начать». Ссылка действует 15 минут. После подтверждения нажмите «Обновить статус». Не передавайте ссылку другим людям.</p> : null}
      {status?.status === 'pending' && !link ? <p className="text-sm text-slate-600">Чтобы снова открыть бота, создайте новую ссылку.</p> : null}
      {error ? <p role="alert" className="text-sm text-red-700">{error}</p> : null}
      <div className="flex flex-wrap gap-2">
        {status && status.status !== 'verified' ? <button type="button" disabled={busy || !status.channelReady} onClick={() => void connect()} className="rounded-lg bg-violet-600 px-3 py-2 text-sm font-medium text-white disabled:opacity-50">{status.status === 'unlinked' || status.status === 'opted-out' ? 'Подключить MAX' : 'Создать новую ссылку'}</button> : null}
        {status?.status === 'pending' && link?.deepLinkUrl && !busy ? <a href={link.deepLinkUrl} target="_blank" rel="noopener noreferrer" referrerPolicy="no-referrer" className="rounded-lg border border-violet-300 px-3 py-2 text-sm font-medium text-violet-700">Открыть бота MAX</a> : null}
        <button type="button" disabled={busy} onClick={() => void refresh()} className="inline-flex items-center gap-2 rounded-lg border border-slate-300 px-3 py-2 text-sm disabled:opacity-50">{busy ? <Loader2 className="h-4 w-4 animate-spin" /> : <RefreshCw className="h-4 w-4" />}Обновить статус</button>
        {status?.status === 'verified' || status?.status === 'pending' ? <button type="button" disabled={busy} onClick={() => void disconnect()} className="inline-flex items-center gap-2 rounded-lg border border-slate-300 px-3 py-2 text-sm disabled:opacity-50"><Unlink2 className="h-4 w-4" />{status.status === 'pending' ? 'Отменить привязку' : 'Отключить MAX'}</button> : null}
      </div>
    </section>
  );
}
