import { useEffect, useRef, useState } from 'react';
import { Download, Loader2, UploadCloud } from 'lucide-react';

import { apiService } from '../../services/api';
import { useNestedCaseOperationOwner, type CaseOperationParent, type NestedCaseOperationOwner } from '../../hooks/useNestedCaseOperationOwner';
import { awaitOwnedUiResult, StaleUiOperationError } from '../../utils/StaleUiOperationError';
import { downloadBlob } from '../../utils/downloadBlob';
import type { ClientCaseDocument } from '../../types';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';

export const EXECUTIVE_WRIT_DOCUMENT_TYPE = 'Исполнительный лист';

interface ExecutiveWritDocumentUploadProps {
  caseId: number;
  actionOwner?: CaseOperationParent;
  canUpload: boolean;
  documents: ClientCaseDocument[];
  onUploaded: () => Promise<void> | void;
  onAttemptSettled?: () => void;
}

export function ExecutiveWritDocumentUpload(props: ExecutiveWritDocumentUploadProps) {
  const owner = useNestedCaseOperationOwner(JSON.stringify(['writ', props.caseId, props.canUpload]), props.actionOwner);
  return owner.active ? <OwnedWritUpload key={owner.scope} {...props} owner={owner} /> : null;
}
function OwnedWritUpload({ caseId, canUpload, documents, onUploaded, onAttemptSettled, owner }: ExecutiveWritDocumentUploadProps & { owner: NestedCaseOperationOwner }) {
  const uploading = owner.kind === 'writ:upload';
  const downloadingId = owner.kind?.startsWith('writ:download:') ? Number(owner.kind.split(':')[2]) : null;
  const [error, setError] = useState<string | null>(null);
  const downloadController = useRef<AbortController | null>(null);
  useEffect(() => () => downloadController.current?.abort(), []);
  const scans = documents.filter(document => document.clientCaseId === caseId && Number.isSafeInteger(document.id) && document.id > 0
    && document.documentType?.trim() === EXECUTIVE_WRIT_DOCUMENT_TYPE && document.status?.trim() === 'Получен' && document.isLatestVersion !== false);
  const currentScans = useRef(scans); currentScans.current = scans;
  const download = async (document: ClientCaseDocument) => {
    if (caseId <= 0 || !currentScans.current.some(row => row.id === document.id && row.documentKey === document.documentKey && row.version === document.version)) return;
    const operation = owner.begin(`writ:download:${document.id}`); if (!operation) return;
    const controller = new AbortController(); downloadController.current = controller;
    setError(null);
    try {
      const result = await awaitOwnedUiResult(() => apiService.downloadCaseDocument(caseId, document.id, { signal: controller.signal }), operation.isCurrent);
      if (!currentScans.current.some(row => row.id === document.id && row.documentKey === document.documentKey && row.version === document.version)) throw new StaleUiOperationError();
      downloadBlob(result.blob, result.fileName || document.originalFileName);
    } catch (cause) {
      if (operation.isCurrent() && !(cause instanceof StaleUiOperationError)) setError(extractDisplayErrorMessage(cause, 'Не удалось скачать скан исполнительного листа.'));
    } finally {
      if (downloadController.current === controller) downloadController.current = null;
      operation.finish();
    }
  };
  const upload = async (file: File | null) => {
    if (!file || !canUpload || caseId <= 0) return;
    const operation = owner.begin('writ:upload'); if (!operation) return;
    let uploaded = false;
    setError(null);
    try {
      await awaitOwnedUiResult(() => apiService.uploadCaseDocument(caseId, file, { documentType: EXECUTIVE_WRIT_DOCUMENT_TYPE, status: 'Получен' }), operation.isCurrent);
      uploaded = true;
      await awaitOwnedUiResult(() => Promise.resolve(onUploaded()), operation.isCurrent);
    } catch (cause) {
      if (operation.isCurrent() && !(cause instanceof StaleUiOperationError)) setError(uploaded ? 'Скан загружен, но список документов не обновился. Проверьте его перед повторной загрузкой.' : extractDisplayErrorMessage(cause, 'Не удалось загрузить скан исполнительного листа.'));
    } finally {
      try { if (operation.isCurrent()) onAttemptSettled?.(); }
      catch { if (operation.isCurrent()) setError('Не удалось обновить состояние загрузки. Проверьте список документов.'); }
      operation.finish();
    }
  };

  return <div className="rounded-lg border border-blue-100 bg-blue-50/40 p-2.5 text-xs text-slate-700">
    <div className="font-semibold text-slate-800">Скан исполнительного листа</div>
    {scans.length > 0 ? <ul className="mt-1 space-y-0.5">{scans.map((document) => <li key={document.id}>
      <button type="button" onClick={() => void download(document)} disabled={owner.busy} aria-label={`Скачать скан ${document.originalFileName}`} title={document.originalFileName} className="inline-flex max-w-full items-center gap-1 text-left font-medium text-blue-700 hover:underline disabled:opacity-60">
        {downloadingId === document.id ? <Loader2 className="h-3.5 w-3.5 shrink-0 animate-spin" /> : <Download className="h-3.5 w-3.5 shrink-0" />}
        <span className="truncate">{document.originalFileName}</span>
      </button>
    </li>)}</ul>
      : <p className="mt-1 text-slate-500">Скан пока не прикреплён.</p>}
    {canUpload && caseId > 0 ? <label className="mt-2 inline-flex cursor-pointer items-center gap-1 rounded-md border border-blue-200 bg-white px-2 py-1.5 font-semibold text-blue-700 hover:bg-blue-50">
      {uploading ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <UploadCloud className="h-3.5 w-3.5" />}
      {uploading ? 'Загружаем...' : 'Добавить скан'}
      <input aria-label="Загрузить скан исполнительного листа" type="file" accept=".pdf,.png,.jpg,.jpeg,.tif,.tiff" disabled={owner.busy} onChange={(event) => { const file = event.currentTarget.files?.[0] ?? null; event.currentTarget.value = ''; void upload(file); }} className="sr-only" />
    </label> : caseId <= 0 ? <p className="mt-1 text-slate-500">Сначала сохраните дело.</p> : null}
    <p className="mt-1 text-slate-500">Файл сохранится в документах дела с типом «Исполнительный лист».</p>
    {error ? <p role="alert" className="mt-1 text-rose-700">{error}</p> : null}
  </div>;
}
