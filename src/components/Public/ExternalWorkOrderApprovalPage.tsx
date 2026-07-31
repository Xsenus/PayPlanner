import { useEffect, useMemo, useState } from 'react';
import { CheckCircle2, Clock3, FileCheck2, Loader2, RotateCcw } from 'lucide-react';

import { apiService } from '../../services/api';
import type { PublicWorkOrderApproval } from '../../types';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { getExternalWorkOrderApprovalPublicId } from '../../utils/externalWorkOrderApprovalRoute';

function formatMoney(value: number, currency: string) {
  return new Intl.NumberFormat('ru-RU', {
    style: 'currency',
    currency: currency || 'RUB',
    maximumFractionDigits: 2,
  }).format(value);
}

function formatDate(value?: string | null) {
  if (!value) return '—';
  return new Intl.DateTimeFormat('ru-RU', {
    dateStyle: 'medium',
    timeStyle: 'short',
  }).format(new Date(value));
}

function readToken(publicId: string) {
  const hash = new URLSearchParams(window.location.hash.replace(/^#/, ''));
  const fromLink = hash.get('token')?.trim();
  const key = `pp:external-work-order:${publicId}`;
  if (fromLink) {
    sessionStorage.setItem(key, fromLink);
    window.history.replaceState(null, '', window.location.pathname);
    return fromLink;
  }
  return sessionStorage.getItem(key) ?? '';
}

export function ExternalWorkOrderApprovalPage() {
  const publicId = useMemo(() => getExternalWorkOrderApprovalPublicId(), []);
  const [token] = useState(() => readToken(publicId));
  const [approval, setApproval] = useState<PublicWorkOrderApproval | null>(null);
  const [loading, setLoading] = useState(true);
  const [submitting, setSubmitting] = useState<'approved' | 'revision_requested' | null>(null);
  const [otp, setOtp] = useState('');
  const [comment, setComment] = useState('');
  const [confirmed, setConfirmed] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    document.title = 'Согласование заказ-наряда';
    if (!publicId || !token) {
      setError('Ссылка недействительна или больше недоступна.');
      setLoading(false);
      return;
    }

    apiService.openPublicWorkOrderApproval(publicId, token)
      .then(setApproval)
      .catch((err) => setError(extractDisplayErrorMessage(err, 'Не удалось открыть заказ-наряд.')))
      .finally(() => setLoading(false));
  }, [publicId, token]);

  const decide = async (decision: 'approved' | 'revision_requested') => {
    if (!confirmed) {
      setError('Подтвердите, что вы проверили состав и сумму заказ-наряда.');
      return;
    }
    if (!/^\d{6}$/.test(otp)) {
      setError('Введите шестизначный код из письма.');
      return;
    }
    if (decision === 'revision_requested' && !comment.trim()) {
      setError('Опишите, что нужно исправить.');
      return;
    }

    setError(null);
    setSubmitting(decision);
    try {
      const result = await apiService.decidePublicWorkOrderApproval(publicId, {
        token,
        otp,
        decision,
        comment: comment.trim(),
      });
      setApproval((current) => current ? {
        ...current,
        status: result.status,
        decision: result.decision,
        decisionComment: result.comment,
        decisionAt: result.decisionAt,
      } : current);
      sessionStorage.removeItem(`pp:external-work-order:${publicId}`);
    } catch (err) {
      setError(extractDisplayErrorMessage(err, 'Не удалось сохранить решение.'));
    } finally {
      setSubmitting(null);
    }
  };

  if (loading) {
    return (
      <main className="grid min-h-screen place-items-center bg-slate-50 p-4">
        <div className="flex items-center gap-3 text-slate-600"><Loader2 className="h-5 w-5 animate-spin" /> Проверяем ссылку…</div>
      </main>
    );
  }

  if (!approval) {
    return (
      <main className="grid min-h-screen place-items-center bg-slate-50 p-4">
        <section className="w-full max-w-lg rounded-2xl border border-rose-200 bg-white p-6 text-center shadow-sm">
          <FileCheck2 className="mx-auto h-10 w-10 text-rose-500" />
          <h1 className="mt-3 text-xl font-semibold text-slate-900">Заказ-наряд недоступен</h1>
          <p className="mt-2 text-sm text-slate-600">{error}</p>
        </section>
      </main>
    );
  }

  const snapshot = approval.snapshot;
  const decided = approval.status === 'approved' || approval.status === 'revision_requested';

  return (
    <main className="min-h-screen bg-slate-50 px-3 py-5 sm:px-5">
      <div className="mx-auto max-w-4xl space-y-3">
        <header className="rounded-2xl border border-slate-200 bg-white p-4 shadow-sm sm:p-5">
          <div className="flex flex-wrap items-start justify-between gap-3">
            <div>
              <div className="text-xs font-semibold uppercase tracking-[0.14em] text-blue-600">{snapshot.organizationName}</div>
              <h1 className="mt-1 text-xl font-semibold text-slate-950 sm:text-2xl">{snapshot.title}</h1>
              <p className="mt-1 text-sm text-slate-500">
                Версия {snapshot.version} · {snapshot.clientName || 'Клиент'} · {snapshot.caseTitle}
              </p>
            </div>
            <div className="rounded-xl bg-blue-50 px-4 py-2 text-right">
              <div className="text-xs text-blue-700">Итого</div>
              <div className="text-lg font-bold text-blue-950">{formatMoney(snapshot.totalAmount, snapshot.currency)}</div>
            </div>
          </div>
          <div className="mt-3 flex items-center gap-2 text-xs text-slate-500">
            <Clock3 className="h-4 w-4" /> Ссылка действует до {formatDate(approval.expiresAt)}
          </div>
        </header>

        <section className="overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-sm">
          <div className="border-b border-slate-200 px-4 py-3 text-sm font-semibold text-slate-800">Состав услуг</div>
          <div className="divide-y divide-slate-100">
            {snapshot.items.map((item, index) => (
              <article key={`${item.serviceName}-${index}`} className="grid gap-2 p-4 sm:grid-cols-[1fr_auto]">
                <div>
                  <h2 className="font-semibold text-slate-900">{item.serviceName}</h2>
                  {item.description ? <p className="mt-1 whitespace-pre-wrap text-sm text-slate-600">{item.description}</p> : null}
                  {item.dueDate ? <p className="mt-1 text-xs text-slate-500">Срок: {formatDate(item.dueDate)}</p> : null}
                </div>
                <div className="text-left sm:text-right">
                  <div className="font-semibold text-slate-900">{formatMoney(item.totalAmount, snapshot.currency)}</div>
                  <div className="text-xs text-slate-500">{item.quantity} × {formatMoney(item.unitPrice, snapshot.currency)}</div>
                </div>
              </article>
            ))}
          </div>
        </section>

        {decided ? (
          <section className={`rounded-2xl border p-5 shadow-sm ${
            approval.status === 'approved'
              ? 'border-emerald-200 bg-emerald-50'
              : 'border-amber-200 bg-amber-50'
          }`}>
            <div className="flex items-center gap-2 font-semibold text-slate-900">
              {approval.status === 'approved' ? <CheckCircle2 className="h-5 w-5 text-emerald-600" /> : <RotateCcw className="h-5 w-5 text-amber-600" />}
              {approval.status === 'approved' ? 'Заказ-наряд согласован' : 'Заказ-наряд возвращён с замечаниями'}
            </div>
            <p className="mt-1 text-sm text-slate-600">Решение зафиксировано {formatDate(approval.decisionAt)}.</p>
            {approval.decisionComment ? <p className="mt-3 whitespace-pre-wrap rounded-lg bg-white/70 p-3 text-sm text-slate-700">{approval.decisionComment}</p> : null}
          </section>
        ) : (
          <section className="rounded-2xl border border-slate-200 bg-white p-4 shadow-sm sm:p-5">
            <h2 className="font-semibold text-slate-900">Подтверждение решения</h2>
            <p className="mt-1 text-sm text-slate-500">Код отправлен на {approval.recipientHint}. После решения изменить эту версию будет нельзя.</p>
            <div className="mt-4 grid gap-3 sm:grid-cols-[180px_1fr]">
              <label className="text-sm font-medium text-slate-700">
                Код из письма
                <input
                  value={otp}
                  onChange={(event) => setOtp(event.target.value.replace(/\D/g, '').slice(0, 6))}
                  inputMode="numeric"
                  autoComplete="one-time-code"
                  className="mt-1 w-full rounded-lg border border-slate-300 px-3 py-2 text-center text-lg tracking-[0.3em] focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100"
                />
              </label>
              <label className="text-sm font-medium text-slate-700">
                Замечания
                <textarea
                  value={comment}
                  onChange={(event) => setComment(event.target.value)}
                  maxLength={2000}
                  rows={3}
                  placeholder="Обязательно при возврате на доработку"
                  className="mt-1 w-full rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100"
                />
              </label>
            </div>
            <label className="mt-3 flex items-start gap-2 text-sm text-slate-700">
              <input type="checkbox" checked={confirmed} onChange={(event) => setConfirmed(event.target.checked)} className="mt-0.5 h-4 w-4 rounded border-slate-300" />
              Я проверил(а) состав услуг, сроки и итоговую сумму этой версии заказ-наряда.
            </label>
            {error ? <div role="alert" className="mt-3 rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">{error}</div> : null}
            <div className="mt-4 grid gap-2 sm:grid-cols-2">
              <button
                type="button"
                onClick={() => void decide('revision_requested')}
                disabled={Boolean(submitting)}
                className="inline-flex items-center justify-center gap-2 rounded-lg border border-amber-300 px-4 py-2.5 text-sm font-semibold text-amber-800 hover:bg-amber-50 disabled:opacity-50">
                {submitting === 'revision_requested' ? <Loader2 className="h-4 w-4 animate-spin" /> : <RotateCcw className="h-4 w-4" />} Вернуть с замечаниями
              </button>
              <button
                type="button"
                onClick={() => void decide('approved')}
                disabled={Boolean(submitting)}
                className="inline-flex items-center justify-center gap-2 rounded-lg bg-emerald-600 px-4 py-2.5 text-sm font-semibold text-white hover:bg-emerald-700 disabled:opacity-50">
                {submitting === 'approved' ? <Loader2 className="h-4 w-4 animate-spin" /> : <CheckCircle2 className="h-4 w-4" />} Согласовать
              </button>
            </div>
          </section>
        )}
      </div>
    </main>
  );
}
