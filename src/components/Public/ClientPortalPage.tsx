import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react';
import { Briefcase, CalendarDays, FileText, Loader2, LogOut, ReceiptText, ShieldCheck } from 'lucide-react';

import {
  apiService,
  type ClientPortalCase,
  type ClientPortalHome,
  type ClientPortalLoginPreview,
} from '../../services/api';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { getClientPortalChallengeId } from '../../utils/clientPortalRoute';

function formatDate(value?: string | null) {
  return value ? new Intl.DateTimeFormat('ru-RU', { dateStyle: 'medium' }).format(new Date(value)) : '—';
}

function readLoginToken(challengeId: string) {
  const key = `pp:client-portal:${challengeId}`;
  const fromHash = new URLSearchParams(window.location.hash.replace(/^#/, '')).get('token')?.trim();
  if (fromHash) {
    sessionStorage.setItem(key, fromHash);
    window.history.replaceState(null, '', window.location.pathname);
    return fromHash;
  }
  return sessionStorage.getItem(key) ?? '';
}

export function ClientPortalPage() {
  const challengeId = useMemo(() => getClientPortalChallengeId(), []);
  const [token] = useState(() => readLoginToken(challengeId));
  const [preview, setPreview] = useState<ClientPortalLoginPreview | null>(null);
  const [home, setHome] = useState<ClientPortalHome | null>(null);
  const [selectedCase, setSelectedCase] = useState<ClientPortalCase | null>(null);
  const [otp, setOtp] = useState('');
  const [loading, setLoading] = useState(true);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const loadHome = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      setHome(await apiService.getClientPortalHome());
      setPreview(null);
      window.history.replaceState(null, '', '/portal');
    } catch (err) {
      setError(extractDisplayErrorMessage(err, 'Войдите по ссылке из письма.'));
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    document.title = 'Клиентский кабинет';
    if (challengeId && token) {
      apiService.previewClientPortalLogin(challengeId, token)
        .then(setPreview)
        .catch((err) => setError(extractDisplayErrorMessage(err, 'Ссылка недействительна или больше недоступна.')))
        .finally(() => setLoading(false));
      return;
    }
    void loadHome();
  }, [challengeId, loadHome, token]);

  const verify = async () => {
    if (!/^\d{6}$/.test(otp)) {
      setError('Введите шестизначный код из письма.');
      return;
    }
    setSubmitting(true);
    setError(null);
    try {
      await apiService.verifyClientPortalLogin(challengeId, token, otp);
      sessionStorage.removeItem(`pp:client-portal:${challengeId}`);
      await loadHome();
    } catch (err) {
      setError(extractDisplayErrorMessage(err, 'Не удалось подтвердить вход.'));
    } finally {
      setSubmitting(false);
    }
  };

  const openCase = async (publicId: string) => {
    setError(null);
    try {
      setSelectedCase(await apiService.getClientPortalCase(publicId));
    } catch (err) {
      setError(extractDisplayErrorMessage(err, 'Дело недоступно.'));
    }
  };

  if (loading) {
    return <main className="grid min-h-screen place-items-center bg-slate-50"><Loader2 className="h-7 w-7 animate-spin text-blue-600" /></main>;
  }

  if (preview) {
    return (
      <main className="grid min-h-screen place-items-center bg-slate-50 p-4">
        <section className="w-full max-w-md rounded-2xl border border-slate-200 bg-white p-5 shadow-sm">
          <ShieldCheck className="h-10 w-10 text-blue-600" />
          <h1 className="mt-3 text-xl font-semibold text-slate-950">Вход в клиентский кабинет</h1>
          <p className="mt-1 text-sm text-slate-600">{preview.displayName} · код отправлен на {preview.emailHint}</p>
          <label className="mt-5 block text-sm font-medium text-slate-700">Код из письма</label>
          <input
            value={otp}
            onChange={(event) => setOtp(event.target.value.replace(/\D/g, '').slice(0, 6))}
            inputMode="numeric"
            autoComplete="one-time-code"
            className="mt-2 w-full rounded-xl border border-slate-300 px-4 py-3 text-center text-xl tracking-[0.35em] outline-none focus:border-blue-500"
          />
          {error ? <p className="mt-3 text-sm text-rose-600">{error}</p> : null}
          <button type="button" disabled={submitting} onClick={() => void verify()} className="mt-4 w-full rounded-xl bg-slate-950 px-4 py-3 text-sm font-semibold text-white disabled:opacity-60">
            {submitting ? 'Проверяем…' : 'Войти'}
          </button>
        </section>
      </main>
    );
  }

  if (!home) {
    return (
      <main className="grid min-h-screen place-items-center bg-slate-50 p-4">
        <section className="max-w-md rounded-2xl border border-rose-200 bg-white p-6 text-center shadow-sm">
          <ShieldCheck className="mx-auto h-10 w-10 text-rose-500" />
          <h1 className="mt-3 text-xl font-semibold">Кабинет недоступен</h1>
          <p className="mt-2 text-sm text-slate-600">{error}</p>
        </section>
      </main>
    );
  }

  return (
    <main className="min-h-screen bg-slate-50 px-3 py-4 sm:px-5">
      <div className="mx-auto max-w-6xl space-y-3">
        <header className="flex flex-wrap items-center justify-between gap-3 rounded-2xl border border-slate-200 bg-white p-4 shadow-sm">
          <div><div className="text-xs font-semibold uppercase tracking-[0.14em] text-blue-600">ЭлитПраво CRM</div><h1 className="text-xl font-semibold text-slate-950">Клиентский кабинет</h1><p className="text-sm text-slate-500">{home.displayName}</p></div>
          <button type="button" onClick={() => void apiService.logoutClientPortal().finally(() => window.location.reload())} className="inline-flex items-center gap-2 rounded-xl border border-slate-200 px-3 py-2 text-sm text-slate-700"><LogOut className="h-4 w-4" /> Выйти</button>
        </header>
        {error ? <div className="rounded-xl border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700">{error}</div> : null}
        <div className="grid gap-3 lg:grid-cols-[320px_1fr]">
          <section className="space-y-2 rounded-2xl border border-slate-200 bg-white p-3 shadow-sm">
            <h2 className="px-1 text-sm font-semibold text-slate-900">Доступные дела</h2>
            {home.items.length === 0 ? <p className="p-3 text-sm text-slate-500">Администратор пока не открыл ни одного дела.</p> : home.items.map((item) => (
              <button key={item.publicId} type="button" onClick={() => void openCase(item.publicId)} className="w-full rounded-xl border border-slate-200 p-3 text-left hover:border-blue-300 hover:bg-blue-50">
                <div className="font-semibold text-slate-900">{item.title}</div>
                <div className="mt-1 text-xs text-slate-500">{item.caseNumber || item.caseType || 'Дело'}</div>
              </button>
            ))}
          </section>
          <section className="rounded-2xl border border-slate-200 bg-white p-4 shadow-sm">
            {!selectedCase ? <div className="grid min-h-64 place-items-center text-center text-slate-500"><div><Briefcase className="mx-auto h-9 w-9" /><p className="mt-2 text-sm">Выберите дело слева.</p></div></div> : (
              <div className="space-y-4">
                <div><h2 className="text-xl font-semibold text-slate-950">{selectedCase.title}</h2><p className="text-sm text-slate-500">{selectedCase.caseNumber} · {selectedCase.stage || selectedCase.status}</p></div>
                <div className="grid gap-3 md:grid-cols-2">
                  <PortalBlock icon={<CalendarDays className="h-4 w-4" />} title="Сроки" empty="Нет открытых сроков" items={selectedCase.deadlines.map((item) => `${formatDate(item.dueAt)} — ${item.title}`)} />
                  <div className="rounded-xl border border-slate-200 p-3">
                    <h3 className="flex items-center gap-2 text-sm font-semibold text-slate-800"><FileText className="h-4 w-4" />Документы</h3>
                    {selectedCase.documents.length === 0 ? <p className="mt-2 text-sm text-slate-500">Нет доступных документов</p> : (
                      <ul className="mt-2 space-y-2">{selectedCase.documents.map((item) => (
                        <li key={item.publicId}><a className="text-sm text-blue-700 hover:underline" href={apiService.getClientPortalDocumentUrl(item.publicId)}>{item.name} · версия {item.version}</a></li>
                      ))}</ul>
                    )}
                  </div>
                  <PortalBlock icon={<ReceiptText className="h-4 w-4" />} title="Финансы" empty="Нет доступных начислений" items={selectedCase.invoices.map((item) => `${item.number || 'Счёт'} — ${item.amount.toLocaleString('ru-RU')} ₽`)} />
                  <PortalBlock icon={<ShieldCheck className="h-4 w-4" />} title="Согласования" empty="Нет доступных согласований" items={selectedCase.approvals.map((item) => `Версия ${item.version} · ${item.status}`)} />
                </div>
              </div>
            )}
          </section>
        </div>
      </div>
    </main>
  );
}

function PortalBlock({ icon, title, empty, items }: { icon: ReactNode; title: string; empty: string; items: string[] }) {
  return <div className="rounded-xl border border-slate-200 p-3"><h3 className="flex items-center gap-2 text-sm font-semibold text-slate-800">{icon}{title}</h3>{items.length === 0 ? <p className="mt-2 text-sm text-slate-500">{empty}</p> : <ul className="mt-2 space-y-2">{items.map((item, index) => <li key={`${item}-${index}`} className="text-sm text-slate-700">{item}</li>)}</ul>}</div>;
}
