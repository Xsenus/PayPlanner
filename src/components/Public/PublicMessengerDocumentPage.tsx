import { useMemo, useState } from 'react';
import { AlertCircle, CheckCircle2, Download, Loader2, ShieldCheck } from 'lucide-react';

import { apiService } from '../../services/api';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { getPublicMessengerDocumentId } from '../../utils/publicMessengerDocumentRoute';

function readToken(publicId: string) {
  const key = `pp:public-document:${publicId}`;
  const fromHash = new URLSearchParams(window.location.hash.replace(/^#/, '')).get('token')?.trim() ?? '';
  if (fromHash.length > 0 && fromHash.length <= 512) {
    sessionStorage.setItem(key, fromHash);
    window.history.replaceState(null, '', window.location.pathname);
    return fromHash;
  }
  return sessionStorage.getItem(key)?.trim() ?? '';
}

/**
 * Публичная промежуточная страница не загружает файл автоматически: превью-боты
 * мессенджеров не должны расходовать одноразовую ссылку вместо получателя.
 */
export function PublicMessengerDocumentPage() {
  const publicId = useMemo(() => getPublicMessengerDocumentId(), []);
  const [token] = useState(() => readToken(publicId));
  const [downloading, setDownloading] = useState(false);
  const [completed, setCompleted] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const download = async () => {
    if (!publicId || !token || downloading || completed) return;

    setDownloading(true);
    setError(null);
    try {
      const result = await apiService.downloadPublicMessengerDocument(publicId, token);
      const url = URL.createObjectURL(result.blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = result.fileName;
      link.rel = 'noreferrer';
      link.click();
      URL.revokeObjectURL(url);
      sessionStorage.removeItem(`pp:public-document:${publicId}`);
      setCompleted(true);
    } catch (err) {
      setError(extractDisplayErrorMessage(err, 'Ссылка недействительна, истекла или уже была использована.'));
    } finally {
      setDownloading(false);
    }
  };

  const linkAvailable = Boolean(publicId && token);

  return (
    <main className="grid min-h-screen place-items-center bg-slate-50 p-4">
      <section className="w-full max-w-lg rounded-2xl border border-slate-200 bg-white p-6 shadow-sm" aria-labelledby="document-title">
        <div className="mb-5 flex h-12 w-12 items-center justify-center rounded-xl bg-blue-50 text-blue-600">
          <ShieldCheck className="h-6 w-6" aria-hidden="true" />
        </div>
        <h1 id="document-title" className="text-xl font-semibold text-slate-900">Защищённый документ</h1>
        <p className="mt-2 text-sm leading-6 text-slate-600">
          Документ доступен по одноразовой ссылке. Нажмите кнопку лично — автоматическое превью мессенджера файл не откроет.
        </p>

        {!linkAvailable && (
          <div className="mt-5 flex gap-3 rounded-xl border border-red-200 bg-red-50 p-3 text-sm text-red-700" role="alert">
            <AlertCircle className="mt-0.5 h-5 w-5 shrink-0" aria-hidden="true" />
            Ссылка неполная или больше недоступна. Запросите отправку документа повторно.
          </div>
        )}

        {error && (
          <div className="mt-5 flex gap-3 rounded-xl border border-red-200 bg-red-50 p-3 text-sm text-red-700" role="alert">
            <AlertCircle className="mt-0.5 h-5 w-5 shrink-0" aria-hidden="true" />
            {error}
          </div>
        )}

        {completed ? (
          <div className="mt-5 flex gap-3 rounded-xl border border-emerald-200 bg-emerald-50 p-3 text-sm text-emerald-700" role="status">
            <CheckCircle2 className="mt-0.5 h-5 w-5 shrink-0" aria-hidden="true" />
            Загрузка началась. Повторно открыть эту ссылку нельзя.
          </div>
        ) : (
          <button
            type="button"
            onClick={() => void download()}
            disabled={!linkAvailable || downloading}
            className="mt-6 inline-flex w-full items-center justify-center gap-2 rounded-xl bg-slate-900 px-4 py-3 text-sm font-medium text-white transition hover:bg-slate-800 disabled:cursor-not-allowed disabled:opacity-50"
          >
            {downloading ? <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" /> : <Download className="h-4 w-4" aria-hidden="true" />}
            {downloading ? 'Проверяем ссылку…' : 'Скачать документ'}
          </button>
        )}
      </section>
    </main>
  );
}
