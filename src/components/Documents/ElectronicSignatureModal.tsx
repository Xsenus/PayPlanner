import { useCallback, useEffect, useMemo, useState } from 'react';
import { Download, FileSignature, Loader2, ShieldCheck, X } from 'lucide-react';

import {
  apiService,
  type ElectronicSignatureConfigurationDto,
  type ElectronicSignatureEnvelopeDto,
  type ElectronicSignatureRecipientDto,
} from '../../services/api';
import { SystemCheckbox } from '../ui/SystemCheckbox';
import { ConfirmDialog } from '../ui/ConfirmDialog';
import { SystemSelect } from '../ui/SystemSelect';
import { useDocumentDeliveryOwner } from '../../hooks/useDocumentDeliveryOwner';
import { captureAuthSessionIdentity } from '../../services/authService';

type Props = {
  ownerScope?: string;
  documentType: 'invoice' | 'act' | 'contract';
  documentId: number;
  documentName: string;
  clientId: number;
  preparePdf: () => Promise<Blob>;
  onClose: () => void;
};

const statusLabels: Record<string, string> = {
  prepared: 'Подготовлено', sending: 'Отправляется', pending: 'Ожидает подписания', completed: 'Подписано',
  declined: 'Отказ', expired: 'Истекло', cancelled: 'Отменено', failed: 'Ошибка', 'verification-failed': 'Ошибка проверки',
};

export function ElectronicSignatureModal(props: Props) {
  return <ElectronicSignatureForm key={JSON.stringify([props.ownerScope, props.documentType, props.documentId, props.documentName, props.clientId, captureAuthSessionIdentity()])} {...props} />;
}

function ElectronicSignatureForm({ documentType, documentId, documentName, clientId, preparePdf, onClose, ownerScope }: Props) {
  const delivery = useDocumentDeliveryOwner(JSON.stringify([ownerScope, documentType, documentId, clientId]));
  const { capture: captureDelivery } = delivery;
  const [configuration, setConfiguration] = useState<ElectronicSignatureConfigurationDto | null>(null);
  const [recipients, setRecipients] = useState<ElectronicSignatureRecipientDto[]>([]);
  const [history, setHistory] = useState<ElectronicSignatureEnvelopeDto[]>([]);
  const [signerIds, setSignerIds] = useState<number[]>([]);
  const [signatureType, setSignatureType] = useState('qualified');
  const [signingOrder, setSigningOrder] = useState('sequential');
  const [provider, setProvider] = useState('KonturDiadoc');
  const [archiveYears, setArchiveYears] = useState(10);
  const [requiresMchd, setRequiresMchd] = useState(false);
  const [mchdReference, setMchdReference] = useState('');
  const [confirmed, setConfirmed] = useState(false);
  const [loading, setLoading] = useState(true);
  const submitting = delivery.busy;
  const [downloadingId, setDownloadingId] = useState<number | null>(null);
  const [cancelCandidate, setCancelCandidate] = useState<ElectronicSignatureEnvelopeDto | null>(null);
  const [error, setError] = useState('');
  const [success, setSuccess] = useState('');

  const load = useCallback(async () => {
    const owner = captureDelivery();
    if (!owner) return;
    setLoading(true);
    setError('');
    try {
      const [config, nextRecipients, nextHistory] = await Promise.all([
        apiService.getElectronicSignatureConfiguration(),
        apiService.getElectronicSignatureRecipients(clientId),
        apiService.getElectronicSignatureHistory(documentType, documentId),
      ]);
      if (!owner.isCurrent()) return;
      setConfiguration(config);
      setRecipients(nextRecipients);
      setHistory(nextHistory);
      setSignatureType(config.defaultSignatureType);
      setSigningOrder(config.defaultSigningOrder);
      setArchiveYears(config.defaultArchiveRetentionYears);
      setProvider(config.providers[0]?.name ?? 'KonturDiadoc');
    } catch (cause) {
      if (!owner.isCurrent()) return;
      setError(cause instanceof Error ? cause.message : 'Не удалось загрузить контур электронной подписи.');
    } finally {
      if (owner.isCurrent()) setLoading(false);
    }
  }, [captureDelivery, clientId, documentId, documentType]);

  useEffect(() => { void load(); }, [load]);

  const selectedProvider = useMemo(
    () => configuration?.providers.find((item) => item.name === provider),
    [configuration, provider],
  );

  const toggleSigner = (id: number) => setSignerIds((current) => current.includes(id)
    ? current.filter((item) => item !== id)
    : [...current, id]);

  const prepare = async () => {
    if (!configuration?.preparationEnabled) return setError('Подготовка электронной подписи выключена администратором.');
    if (signerIds.length === 0 || !confirmed) return setError('Выберите подписантов и подтвердите неизменяемую версию документа.');
    if (requiresMchd && !mchdReference.trim()) return setError('Укажите ссылку или номер МЧД.');
    if (loading) return;
    const operation = delivery.begin('prepare');
    if (!operation) return;
    setError('');
    setSuccess('');
    try {
      const pdf = await preparePdf();
      if (!operation.isCurrent()) return;
      if (pdf.size <= 0 || (pdf.type && pdf.type !== 'application/pdf')) throw new Error('CRM не сформировала корректный PDF.');
      await apiService.prepareElectronicSignatureEnvelope(documentType, documentId, pdf, {
        clientId,
        signerAccountIds: signerIds,
        signatureType,
        signingOrder,
        provider,
        archiveRetentionYears: archiveYears,
        requiresMachineReadablePowerOfAttorney: requiresMchd,
        powerOfAttorneyReference: mchdReference.trim() || undefined,
        confirmSnapshotAndSigners: true,
      });
      if (!operation.isCurrent()) return;
      setConfirmed(false);
      setSuccess(selectedProvider?.enabled && configuration.enabled
        ? 'Неизменяемая версия подготовлена для отправки провайдеру.'
        : 'Неизменяемая версия подготовлена. Отправка останется выключенной до подключения официального провайдера.');
      const nextHistory = await apiService.getElectronicSignatureHistory(documentType, documentId);
      if (operation.isCurrent()) setHistory(nextHistory);
    } catch (cause) {
      if (!operation.isCurrent()) return;
      setError(cause instanceof Error ? cause.message : 'Не удалось подготовить документ к подписанию.');
    } finally {
      operation.finish();
    }
  };

  const cancelPrepared = async (item: ElectronicSignatureEnvelopeDto) => {
    const operation = delivery.begin('cancel');
    if (!operation) return;
    setError('');
    setSuccess('');
    try {
      await apiService.cancelElectronicSignatureEnvelope(item.publicId);
      if (!operation.isCurrent()) return;
      setSuccess('Подготовленная версия отменена. PDF и история сохранены.');
      const nextHistory = await apiService.getElectronicSignatureHistory(documentType, documentId);
      if (operation.isCurrent()) setHistory(nextHistory);
    } catch (cause) {
      if (!operation.isCurrent()) return;
      setError(cause instanceof Error ? cause.message : 'Не удалось отменить подготовленную версию.');
    } finally {
      operation.finish();
    }
  };

  const downloadEvidence = async (item: ElectronicSignatureEnvelopeDto) => {
    const operation = delivery.begin('archive');
    if (!operation) return;
    setDownloadingId(item.id);
    setError('');
    try {
      const result = await apiService.downloadElectronicSignatureEvidencePackage(item.publicId);
      if (!operation.isCurrent()) return;
      const url = URL.createObjectURL(result.blob);
      const anchor = document.createElement('a');
      anchor.href = url;
      anchor.download = result.fileName || `electronic-signature-${item.documentId}-v${item.documentVersion}.zip`;
      anchor.click();
      URL.revokeObjectURL(url);
    } catch (cause) {
      if (!operation.isCurrent()) return;
      setError(cause instanceof Error ? cause.message : 'Не удалось сформировать доказательный пакет.');
    } finally {
      if (operation.isCurrent()) setDownloadingId(null);
      operation.finish();
    }
  };

  return (
    <div className="fixed inset-0 z-[2600] flex items-center justify-center bg-slate-950/55 p-3" role="dialog" aria-modal="true" aria-label="Электронная подпись">
      <div className="max-h-[94vh] w-full max-w-3xl overflow-y-auto rounded-2xl bg-white shadow-2xl">
        <header className="sticky top-0 z-10 flex items-start justify-between gap-3 border-b border-slate-200 bg-white px-4 py-3">
          <div><h2 className="flex items-center gap-2 font-semibold text-slate-900"><FileSignature className="h-5 w-5 text-blue-600" /> Электронная подпись</h2><p className="mt-1 text-xs text-slate-500">{documentName}</p></div>
          <button type="button" onClick={() => { delivery.invalidate(); onClose(); }} aria-label="Закрыть" className="rounded-lg p-2 text-slate-500 hover:bg-slate-100"><X className="h-4 w-4" /></button>
        </header>
        <div className="space-y-3 p-4">
          {loading ? <div className="flex items-center gap-2 text-sm text-slate-500"><Loader2 className="h-4 w-4 animate-spin" />Загрузка…</div> : null}
          {!loading && recipients.length === 0 ? <div className="rounded-xl border border-amber-200 bg-amber-50 p-3 text-sm text-amber-800">Сначала выдайте клиенту или представителю явный доступ к делу в клиентском кабинете.</div> : null}
          {recipients.length > 0 ? <section className="rounded-xl border border-slate-200 p-3"><h3 className="text-sm font-semibold text-slate-800">Подписанты</h3><div className="mt-2 grid gap-2 sm:grid-cols-2">{recipients.map((item) => <label key={item.id} className="flex items-start gap-2 rounded-lg bg-slate-50 p-2 text-sm"><SystemCheckbox checked={signerIds.includes(item.id)} onChange={() => toggleSigner(item.id)} /><span><strong className="block text-slate-800">{item.displayName}</strong><span className="text-xs text-slate-500">{item.email} · {item.isRepresentative ? 'представитель' : 'клиент'}</span></span></label>)}</div></section> : null}
          <section className="grid gap-3 rounded-xl border border-slate-200 p-3 sm:grid-cols-2">
            <label className="text-xs font-semibold text-slate-600">Вид подписи<SystemSelect value={signatureType} onChange={(event) => setSignatureType(event.target.value)} className="mt-1 w-full"><option value="simple">ПЭП</option><option value="enhanced-unqualified">НЭП</option><option value="qualified">УКЭП</option></SystemSelect></label>
            <label className="text-xs font-semibold text-slate-600">Порядок<SystemSelect value={signingOrder} onChange={(event) => setSigningOrder(event.target.value)} className="mt-1 w-full"><option value="sequential">Последовательно</option><option value="parallel">Параллельно</option></SystemSelect></label>
            <label className="text-xs font-semibold text-slate-600">Провайдер<SystemSelect value={provider} onChange={(event) => setProvider(event.target.value)} className="mt-1 w-full">{configuration?.providers.map((item) => <option key={item.name} value={item.name}>{item.name}{item.enabled ? '' : ' — выключен'}</option>)}</SystemSelect></label>
            <label className="text-xs font-semibold text-slate-600">Архив<SystemSelect value={String(archiveYears)} onChange={(event) => setArchiveYears(Number(event.target.value))} className="mt-1 w-full"><option value="5">5 лет</option><option value="10">10 лет</option><option value="75">75 лет</option></SystemSelect></label>
          </section>
          <section className="rounded-xl border border-slate-200 p-3"><label className="flex items-center gap-2 text-sm font-medium text-slate-700"><SystemCheckbox checked={requiresMchd} onChange={() => setRequiresMchd((value) => !value)} />Требуется МЧД</label>{requiresMchd ? <input value={mchdReference} onChange={(event) => setMchdReference(event.target.value)} placeholder="Номер или ссылка МЧД" className="mt-2 w-full rounded-lg border border-slate-200 px-3 py-2 text-sm" /> : null}</section>
          <label className="flex items-start gap-2 rounded-xl border border-blue-100 bg-blue-50 p-3 text-sm text-blue-900"><SystemCheckbox checked={confirmed} onChange={() => setConfirmed((value) => !value)} /><span>Подтверждаю выбранную неизменяемую PDF-версию и порядок подписантов. Статус документа не станет «Подписан» без проверенных криптографических артефактов.</span></label>
          {!configuration?.enabled ? <div className="flex gap-2 rounded-xl border border-slate-200 bg-slate-50 p-3 text-xs text-slate-600"><ShieldCheck className="h-4 w-4 shrink-0" />Внешняя отправка безопасно выключена. Можно подготовить снимок, но он не покинет CRM до подключения официального провайдера.</div> : null}
          {error ? <div className="rounded-lg bg-rose-50 p-3 text-sm text-rose-700">{error}</div> : null}{success ? <div className="rounded-lg bg-emerald-50 p-3 text-sm text-emerald-700">{success}</div> : null}
          <button type="button" disabled={submitting || loading || recipients.length === 0} onClick={() => void prepare()} className="w-full rounded-xl bg-slate-900 px-4 py-2.5 text-sm font-semibold text-white disabled:opacity-50">{submitting ? 'Подготовка…' : 'Подготовить неизменяемую версию'}</button>
          {history.length > 0 ? <section className="rounded-xl border border-slate-200 p-3"><h3 className="text-sm font-semibold text-slate-800">История версий</h3><div className="mt-2 space-y-2">{history.map((item) => <div key={item.id} className="rounded-lg bg-slate-50 p-2 text-xs text-slate-600"><div className="flex items-start justify-between gap-2"><div className="font-semibold text-slate-800">Версия {item.documentVersion} · {statusLabels[item.status] ?? item.status}</div><div className="flex gap-1"><button type="button" disabled={downloadingId === item.id} onClick={() => void downloadEvidence(item)} className="inline-flex items-center gap-1 rounded-md border border-slate-200 bg-white px-2 py-1 font-semibold text-slate-700 disabled:opacity-50"><Download className="h-3 w-3" />{downloadingId === item.id ? 'Сборка…' : 'Архив'}</button>{item.status === 'prepared' ? <button type="button" disabled={submitting} onClick={() => setCancelCandidate(item)} className="rounded-md border border-rose-200 bg-white px-2 py-1 font-semibold text-rose-700 disabled:opacity-50">Отменить</button> : null}</div></div><div>{item.provider} · {item.signers.map((signer) => `${signer.displayName} — ${statusLabels[signer.status] ?? signer.status}`).join(', ')}</div><div className="font-mono text-[10px] text-slate-400">SHA-256 {item.checksumSha256}</div></div>)}</div></section> : null}
        </div>
      </div>
      <ConfirmDialog
        open={cancelCandidate !== null}
        title="Отменить подготовленную версию?"
        description={cancelCandidate
          ? `Версия ${cancelCandidate.documentVersion} будет отменена. PDF и история останутся в CRM.`
          : undefined}
        confirmLabel="Отменить версию"
        cancelLabel="Оставить"
        variant="danger"
        onCancel={() => setCancelCandidate(null)}
        onConfirm={() => {
          const item = cancelCandidate;
          setCancelCandidate(null);
          if (item) void cancelPrepared(item);
        }}
      />
    </div>
  );
}
