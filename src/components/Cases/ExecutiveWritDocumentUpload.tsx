import { useState } from 'react';
import { Download, Loader2, UploadCloud } from 'lucide-react';

import { apiService } from '../../services/api';
import type { ClientCaseDocument } from '../../types';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';

export const EXECUTIVE_WRIT_DOCUMENT_TYPE = 'Исполнительный лист';

interface ExecutiveWritDocumentUploadProps {
  caseId: number;
  canUpload: boolean;
  documents: ClientCaseDocument[];
  onUploaded: () => Promise<void> | void;
  onAttemptSettled?: () => void;
}

export function ExecutiveWritDocumentUpload({ caseId, canUpload, documents, onUploaded, onAttemptSettled }: ExecutiveWritDocumentUploadProps) {
  const [uploading, setUploading] = useState(false);
  const [downloadingId, setDownloadingId] = useState<number | null>(null);
  const [error, setError] = useState<string | null>(null);
  const scans = documents.filter((document) =>
    document.documentType?.trim() === EXECUTIVE_WRIT_DOCUMENT_TYPE
    && document.status?.trim() === 'Получен'
    && document.isLatestVersion !== false);

  const download = async (document: ClientCaseDocument) => {
    if (downloadingId !== null || caseId <= 0) return;
    setDownloadingId(document.id);
    setError(null);
    try {
      const result = await apiService.downloadCaseDocument(caseId, document.id);
      const url = URL.createObjectURL(result.blob);
      try {
        const link = window.document.createElement('a');
        link.href = url;
        link.download = result.fileName || document.originalFileName;
        window.document.body.appendChild(link);
        link.click();
        link.remove();
      } finally {
        URL.revokeObjectURL(url);
      }
    } catch (downloadError) {
      setError(extractDisplayErrorMessage(downloadError, 'Не удалось скачать скан исполнительного листа.'));
    } finally {
      setDownloadingId(null);
    }
  };

  const upload = async (file: File | null, input: HTMLInputElement) => {
    if (!file || uploading || !canUpload || caseId <= 0) return;
    setUploading(true);
    setError(null);
    try {
      await apiService.uploadCaseDocument(caseId, file, {
        documentType: EXECUTIVE_WRIT_DOCUMENT_TYPE,
        status: 'Получен',
      });
    } catch (uploadError) {
      setError(extractDisplayErrorMessage(uploadError, 'Не удалось загрузить скан исполнительного листа.'));
      input.value = '';
      setUploading(false);
      onAttemptSettled?.();
      return;
    }
    try {
      await onUploaded();
    } catch {
      setError('Скан загружен, но список документов не обновился. Проверьте его перед повторной загрузкой.');
    } finally {
      input.value = '';
      setUploading(false);
      onAttemptSettled?.();
    }
  };

  return <div className="rounded-lg border border-blue-100 bg-blue-50/40 p-2.5 text-xs text-slate-700">
    <div className="font-semibold text-slate-800">Скан исполнительного листа</div>
    {scans.length > 0 ? <ul className="mt-1 space-y-0.5">{scans.map((document) => <li key={document.id}>
      <button type="button" onClick={() => void download(document)} disabled={downloadingId !== null} aria-label={`Скачать скан ${document.originalFileName}`} title={document.originalFileName} className="inline-flex max-w-full items-center gap-1 text-left font-medium text-blue-700 hover:underline disabled:opacity-60">
        {downloadingId === document.id ? <Loader2 className="h-3.5 w-3.5 shrink-0 animate-spin" /> : <Download className="h-3.5 w-3.5 shrink-0" />}
        <span className="truncate">{document.originalFileName}</span>
      </button>
    </li>)}</ul>
      : <p className="mt-1 text-slate-500">Скан пока не прикреплён.</p>}
    {canUpload && caseId > 0 ? <label className="mt-2 inline-flex cursor-pointer items-center gap-1 rounded-md border border-blue-200 bg-white px-2 py-1.5 font-semibold text-blue-700 hover:bg-blue-50">
      {uploading ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <UploadCloud className="h-3.5 w-3.5" />}
      {uploading ? 'Загружаем...' : 'Добавить скан'}
      <input aria-label="Загрузить скан исполнительного листа" type="file" accept=".pdf,.png,.jpg,.jpeg,.tif,.tiff" disabled={uploading} onChange={(event) => void upload(event.currentTarget.files?.[0] ?? null, event.currentTarget)} className="sr-only" />
    </label> : caseId <= 0 ? <p className="mt-1 text-slate-500">Сначала сохраните дело.</p> : null}
    <p className="mt-1 text-slate-500">Файл сохранится в документах дела с типом «Исполнительный лист».</p>
    {error ? <p role="alert" className="mt-1 text-rose-700">{error}</p> : null}
  </div>;
}
