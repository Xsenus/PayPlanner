import { useEffect, useState } from 'react';
import { Loader2, Mail, ShieldCheck, Trash2 } from 'lucide-react';

import { apiService, type ClientPortalAudit, type ClientPortalCase, type ClientPortalCaseAccess, type ClientPortalCasePreviewRequest } from '../../services/api';
import type { ClientCase, ClientCaseDocument } from '../../types';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { SystemCheckbox } from '../ui/SystemCheckbox';
import { ClientPortalCaseDetails } from '../Public/ClientPortalCaseDetails';

export function ClientPortalAccessPanel({ caseItem, documents, canPreview = false }: {
  caseItem: ClientCase;
  documents: ClientCaseDocument[];
  canPreview?: boolean;
}) {
  const ownerName = caseItem.client?.name || caseItem.legalEntity?.shortName || '';
  const ownerEmail = caseItem.client?.email || caseItem.legalEntity?.email || '';
  const [available, setAvailable] = useState(false);
  const [accesses, setAccesses] = useState<ClientPortalCaseAccess[]>([]);
  const [audit, setAudit] = useState<ClientPortalAudit[]>([]);
  const [displayName, setDisplayName] = useState(ownerName);
  const [email, setEmail] = useState(ownerEmail);
  const [accountType, setAccountType] = useState<'client' | 'representative'>('client');
  const [rights, setRights] = useState({ deadlines: true, documents: false, finance: false, approvals: false });
  const [documentIds, setDocumentIds] = useState<number[]>([]);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [preview, setPreview] = useState<{ details: ClientPortalCase; heading: string } | null>(null);

  const load = async () => {
    const flags = await apiService.getFeatureFlags();
    if (!flags.legal.clientPortal) return;
    setAvailable(true);
    const [loadedAccesses, loadedAudit] = await Promise.all([
      apiService.getClientPortalCaseAccess(caseItem.id),
      apiService.getClientPortalAudit(caseItem.id),
    ]);
    setAccesses(loadedAccesses);
    setAudit(loadedAudit);
  };

  useEffect(() => {
    void load().catch(() => setAvailable(false));
    // The case id is the stable boundary for the panel.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [caseItem.id]);

  if (!available && !canPreview) return null;

  const openPreview = async (request: ClientPortalCasePreviewRequest, heading: string) => {
    setBusy(true);
    setError(null);
    try {
      const details = await apiService.previewClientPortalCase(caseItem.id, request);
      setPreview({ details, heading });
    } catch (err) {
      setError(extractDisplayErrorMessage(err, 'Не удалось открыть предпросмотр кабинета.'));
    } finally {
      setBusy(false);
    }
  };

  if (preview && canPreview) {
    return (
      <section aria-label="Предпросмотр клиентского кабинета" className="space-y-3 rounded-xl border border-blue-200 bg-white p-3 shadow-sm">
        <div className="flex flex-wrap items-center justify-between gap-2 rounded-lg bg-blue-50 p-2">
          <div>
            <h2 className="font-semibold text-slate-900">{preview.heading}</h2>
            <p className="text-sm text-slate-700">Только просмотр от имени вашей учётной записи</p>
          </div>
          <button type="button" onClick={() => setPreview(null)} className="rounded-lg border border-blue-200 bg-white px-3 py-1.5 text-sm font-semibold">Закрыть предпросмотр</button>
        </div>
        <ClientPortalCaseDetails details={preview.details} />
      </section>
    );
  }

  const saveAndSend = async () => {
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      const account = await apiService.saveClientPortalAccess({
        email: email.trim(),
        displayName: displayName.trim(),
        accountType,
        clientId: accountType === 'client' ? caseItem.clientId : null,
        legalEntityId: accountType === 'client' ? caseItem.legalEntityId : null,
        clientCaseId: caseItem.id,
        canViewCaseSummary: true,
        canViewDeadlines: rights.deadlines,
        canViewDocuments: rights.documents,
        canViewFinance: rights.finance,
        canViewApprovals: rights.approvals,
        canViewMessages: false,
        documentIds: rights.documents ? documentIds : [],
      });
      const sent = await apiService.sendClientPortalLogin(account.id);
      setNotice(`Доступ сохранён, письмо отправлено на ${sent.emailHint}.`);
      const [loadedAccesses, loadedAudit] = await Promise.all([
        apiService.getClientPortalCaseAccess(caseItem.id),
        apiService.getClientPortalAudit(caseItem.id),
      ]);
      setAccesses(loadedAccesses);
      setAudit(loadedAudit);
    } catch (err) {
      setError(extractDisplayErrorMessage(err, 'Не удалось выдать доступ.'));
    } finally {
      setBusy(false);
    }
  };

  const revoke = async (accountId: number) => {
    setBusy(true);
    setError(null);
    try {
      await apiService.revokeClientPortalCaseAccess(accountId, caseItem.id, 'Доступ к делу отозван из карточки.');
      const [loadedAccesses, loadedAudit] = await Promise.all([
        apiService.getClientPortalCaseAccess(caseItem.id),
        apiService.getClientPortalAudit(caseItem.id),
      ]);
      setAccesses(loadedAccesses);
      setAudit(loadedAudit);
      setNotice('Доступ и активные сессии отозваны.');
    } catch (err) {
      setError(extractDisplayErrorMessage(err, 'Не удалось отозвать доступ.'));
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="rounded-xl border border-blue-200 bg-white p-3 shadow-sm">
      <div className="flex items-center gap-2"><ShieldCheck className="h-5 w-5 text-blue-600" /><h2 className="font-semibold text-slate-900">Клиентский кабинет</h2></div>
      <p className="mt-1 text-xs text-slate-500">По умолчанию закрыто всё. Отметьте только данные, которые можно показать получателю.</p>
      {!available && canPreview ? <p className="mt-1 text-xs text-slate-700">Вход клиентов сейчас недоступен. Вы можете проверить выбранные настройки доступа.</p> : null}
      {accesses.length > 0 ? <div className="mt-3 space-y-2">{accesses.map((item) => (
        <div key={item.accountId} className="flex flex-wrap items-center justify-between gap-2 rounded-lg bg-slate-50 px-3 py-2 text-sm">
          <div className="min-w-0">
            <div><span className="font-semibold text-slate-800">{item.displayName}</span><span className="ml-2 text-slate-500">{item.email}</span><span className={`ml-2 text-xs ${item.isActive ? 'text-emerald-600' : 'text-rose-600'}`}>{item.isActive ? 'доступен' : 'отозван'}</span></div>
            <div className="mt-1 flex flex-wrap gap-1" aria-label={`Разрешения: ${item.displayName}`}>
              {portalRightLabels(item).map((label) => <span key={label} className="rounded-full border border-blue-200 bg-blue-50 px-2 py-0.5 text-[11px] text-blue-700">{label}</span>)}
            </div>
          </div>
          {item.isActive ? <div className="flex flex-wrap items-center gap-3">
            {canPreview && item.canViewCaseSummary ? (
              <button type="button" disabled={busy} onClick={() => void openPreview(
                { accountId: item.accountId }, `Кабинет клиента · ${item.displayName}`,
              )} className="text-xs font-semibold text-blue-700 hover:underline">Посмотреть как клиент</button>
            ) : null}
            <button type="button" disabled={busy} onClick={() => void revoke(item.accountId)} className="inline-flex items-center gap-1 text-xs text-rose-600"><Trash2 className="h-3.5 w-3.5" /> Отозвать</button>
          </div> : null}
        </div>
      ))}</div> : null}
      <div className="mt-3 grid gap-2 md:grid-cols-3">
        <select value={accountType} onChange={(event) => setAccountType(event.target.value as 'client' | 'representative')} className="rounded-lg border border-slate-300 px-3 py-2 text-sm"><option value="client">Клиент</option><option value="representative">Представитель</option></select>
        <input value={displayName} onChange={(event) => setDisplayName(event.target.value)} placeholder="ФИО получателя" className="rounded-lg border border-slate-300 px-3 py-2 text-sm" />
        <input value={email} onChange={(event) => setEmail(event.target.value)} type="email" placeholder="Email" className="rounded-lg border border-slate-300 px-3 py-2 text-sm" />
      </div>
      <div className="mt-3 flex flex-wrap gap-4 text-sm text-slate-700">
        <CheckLabel label="Сроки" checked={rights.deadlines} onChange={(value) => setRights((old) => ({ ...old, deadlines: value }))} />
        <CheckLabel label="Документы" checked={rights.documents} onChange={(value) => setRights((old) => ({ ...old, documents: value }))} />
        <CheckLabel label="Финансы" checked={rights.finance} onChange={(value) => setRights((old) => ({ ...old, finance: value }))} />
        <CheckLabel label="Согласования" checked={rights.approvals} onChange={(value) => setRights((old) => ({ ...old, approvals: value }))} />
      </div>
      {rights.documents ? <div className="mt-3 grid gap-2 sm:grid-cols-2">{documents.map((document) => (
        <CheckLabel key={document.id} checked={documentIds.includes(document.id)} onChange={(value) => setDocumentIds((old) => value ? [...old, document.id] : old.filter((id) => id !== document.id))} label={document.originalFileName} />
      ))}{documents.length === 0 ? <span className="text-sm text-slate-500">В деле пока нет документов.</span> : null}</div> : null}
      {error ? <p className="mt-2 text-sm text-rose-600">{error}</p> : null}
      {notice ? <p className="mt-2 text-sm text-emerald-700">{notice}</p> : null}
      <div className="mt-3 flex flex-wrap gap-2">
        {canPreview ? <button type="button" disabled={busy} onClick={() => void openPreview({
          email: email.trim(),
          canViewDeadlines: rights.deadlines,
          canViewDocuments: rights.documents,
          canViewFinance: rights.finance,
          canViewApprovals: rights.approvals,
          documentIds: rights.documents ? documentIds : [],
        }, 'Предпросмотр настроек доступа')} className="rounded-lg border border-blue-200 px-4 py-2 text-sm font-semibold text-blue-800 disabled:opacity-50">
          {accesses.length > 0 ? 'Предпросмотр настроек' : 'Посмотреть как клиент'}
        </button> : null}
        {available ? <button type="button" disabled={busy || !displayName.trim() || !email.trim()} onClick={() => void saveAndSend()} className="inline-flex items-center gap-2 rounded-lg bg-slate-950 px-4 py-2 text-sm font-semibold text-white disabled:opacity-50">
          {busy ? <Loader2 className="h-4 w-4 animate-spin" /> : <Mail className="h-4 w-4" />} Сохранить и отправить вход
        </button> : null}
      </div>
      {audit.length > 0 ? <details className="mt-3 rounded-lg border border-slate-200 px-3 py-2"><summary className="cursor-pointer text-sm font-medium text-slate-700">Последние действия ({audit.length})</summary><div className="mt-2 max-h-48 space-y-1 overflow-auto">{audit.map((item, index) => <div key={`${item.occurredAt}-${index}`} className="text-xs text-slate-500">{new Date(item.occurredAt).toLocaleString('ru-RU')} · {item.displayName || item.email || 'Система'} · {item.eventType}</div>)}</div></details> : null}
    </section>
  );
}

function portalRightLabels(item: ClientPortalCaseAccess) {
  const labels = ['Карточка дела'];
  if (item.canViewDeadlines) labels.push('Сроки и события');
  if (item.canViewDocuments) labels.push('Документы');
  if (item.canViewFinance) labels.push('Финансы');
  if (item.canViewApprovals) labels.push('Согласования');
  if (item.canViewMessages) labels.push('История сообщений');
  return labels;
}

function CheckLabel({ label, checked, onChange }: { label: string; checked: boolean; onChange: (value: boolean) => void }) {
  return <label className="inline-flex min-w-0 items-center gap-2"><SystemCheckbox checked={checked} onChange={(event) => onChange(event.target.checked)} /><span className="truncate">{label}</span></label>;
}
