import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react';
import { Briefcase, CalendarDays, Download, FileText, Loader2, LogOut, MessageSquare, ReceiptText, Search, ShieldCheck, UserRound } from 'lucide-react';

import {
  apiService,
  type ClientPortalCase,
  type ClientPortalHome,
  type ClientPortalLoginPreview,
  type ClientPortalProfile,
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
  const [search, setSearch] = useState('');
  const [status, setStatus] = useState('');
  const [profile, setProfile] = useState<ClientPortalProfile | null>(null);
  const [downloadingDocumentId, setDownloadingDocumentId] = useState<string | null>(null);
  const [maxLinking, setMaxLinking] = useState(false);

  const loadHome = useCallback(async (nextSearch = '', nextStatus = '') => {
    setLoading(true);
    setError(null);
    try {
      setHome(await apiService.getClientPortalHome(1, 20, nextSearch, nextStatus));
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

  const downloadDocument = async (publicId: string, fileName: string) => {
    setDownloadingDocumentId(publicId);
    setError(null);
    try {
      const grant = await apiService.createClientPortalDocumentGrant(publicId);
      const blob = await apiService.downloadClientPortalDocument(publicId, grant.grantToken);
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = fileName;
      link.rel = 'noreferrer';
      link.click();
      URL.revokeObjectURL(url);
    } catch (err) {
      setError(extractDisplayErrorMessage(err, 'Не удалось скачать документ.'));
    } finally {
      setDownloadingDocumentId(null);
    }
  };

  const toggleProfile = async () => {
    if (profile) {
      setProfile(null);
      return;
    }
    try {
      setProfile(await apiService.getClientPortalProfile());
    } catch (err) {
      setError(extractDisplayErrorMessage(err, 'Не удалось открыть профиль.'));
    }
  };

  const connectMax = async () => {
    setMaxLinking(true);
    setError(null);
    try {
      const link = await apiService.createClientPortalMaxLink();
      if (!link.deepLinkUrl) throw new Error('Ссылка MAX временно недоступна.');
      window.open(link.deepLinkUrl, '_blank', 'noopener,noreferrer');
    } catch (err) {
      setError(extractDisplayErrorMessage(err, 'Не удалось начать подключение MAX.'));
    } finally {
      setMaxLinking(false);
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
          <div className="flex flex-wrap gap-2">
            <button type="button" onClick={() => void toggleProfile()} className="inline-flex items-center gap-2 rounded-xl border border-slate-200 px-3 py-2 text-sm text-slate-700"><UserRound className="h-4 w-4" /> Профиль</button>
            <button type="button" onClick={() => void apiService.logoutClientPortal().finally(() => window.location.reload())} className="inline-flex items-center gap-2 rounded-xl border border-slate-200 px-3 py-2 text-sm text-slate-700"><LogOut className="h-4 w-4" /> Выйти</button>
          </div>
        </header>
        {profile ? (
          <section className="rounded-2xl border border-blue-200 bg-blue-50/50 p-4 text-sm text-slate-700">
            <div className="font-semibold text-slate-900">{profile.displayName}</div>
            <div className="mt-1">{profile.email} · {profile.accountType === 'representative' ? 'представитель' : 'клиент'}</div>
          <div className="mt-1 text-xs text-slate-500">Доступно дел: {profile.activeCaseCount} · активных входов: {profile.activeSessionCount}</div>
          {profile.securityNotificationsEnabled ? <div className="mt-1 text-xs text-emerald-700">Уведомления безопасности о новых входах включены.</div> : null}
            <div className="mt-3 flex flex-wrap gap-2">
              <button type="button" disabled={maxLinking} onClick={() => void connectMax()} className="inline-flex items-center gap-1.5 rounded-lg border border-violet-200 bg-white px-3 py-2 text-xs font-semibold text-violet-700 disabled:opacity-60"><MessageSquare className="h-3.5 w-3.5" />{maxLinking ? 'Открываем…' : 'Подключить MAX'}</button>
              <button type="button" onClick={() => void apiService.logoutClientPortal(true).finally(() => window.location.reload())} className="rounded-lg border border-rose-200 bg-white px-3 py-2 text-xs font-semibold text-rose-700">Выйти на всех устройствах</button>
            </div>
          </section>
        ) : null}
        {home.upcomingActions?.length ? (
          <section className="rounded-2xl border border-amber-200 bg-amber-50/60 p-4">
            <h2 className="text-sm font-semibold text-slate-900">Ближайшие сроки</h2>
            <div className="mt-2 grid gap-2 md:grid-cols-2">
              {home.upcomingActions.map((item) => (
                <button key={`${item.casePublicId}-${item.dueAt}-${item.title}`} type="button" onClick={() => void openCase(item.casePublicId)} className="rounded-xl bg-white p-3 text-left text-sm shadow-sm">
                  <span className="font-semibold text-slate-900">{formatDate(item.dueAt)} · {item.title}</span>
                  <span className="mt-1 block text-xs text-slate-500">{item.caseTitle}</span>
                </button>
              ))}
            </div>
          </section>
        ) : null}
        {error ? <div className="rounded-xl border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700">{error}</div> : null}
        <div className="grid gap-3 lg:grid-cols-[320px_1fr]">
          <section className="space-y-2 rounded-2xl border border-slate-200 bg-white p-3 shadow-sm">
            <h2 className="px-1 text-sm font-semibold text-slate-900">Доступные дела</h2>
            <form className="grid gap-2" onSubmit={(event) => { event.preventDefault(); void loadHome(search, status); }}>
              <label className="relative">
                <Search className="pointer-events-none absolute left-3 top-2.5 h-4 w-4 text-slate-400" />
                <input aria-label="Поиск дел" value={search} onChange={(event) => setSearch(event.target.value)} placeholder="Название или номер" maxLength={100} className="h-9 w-full rounded-lg border border-slate-200 pl-9 pr-3 text-sm" />
              </label>
              <div className="grid grid-cols-[1fr_auto] gap-2">
                <select aria-label="Статус дела" value={status} onChange={(event) => setStatus(event.target.value)} className="h-9 rounded-lg border border-slate-200 px-2 text-sm">
                  <option value="">Все статусы</option>
                  {[...new Set(home.items.map((item) => item.status).filter(Boolean))].map((itemStatus) => <option key={itemStatus} value={itemStatus}>{itemStatus}</option>)}
                </select>
                <button type="submit" className="rounded-lg bg-slate-900 px-3 text-sm font-semibold text-white">Найти</button>
              </div>
            </form>
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
                        <li key={item.publicId}><button type="button" onClick={() => void downloadDocument(item.publicId, item.name)} disabled={downloadingDocumentId === item.publicId} className="inline-flex items-center gap-2 text-left text-sm text-blue-700 hover:underline disabled:opacity-60">{downloadingDocumentId === item.publicId ? <Loader2 className="h-4 w-4 animate-spin" /> : <Download className="h-4 w-4" />}{item.name} · версия {item.version}</button></li>
                      ))}</ul>
                    )}
                  </div>
                  <PortalBlock icon={<ReceiptText className="h-4 w-4" />} title="Финансы" empty="Нет доступных начислений" items={selectedCase.invoices.map((item) => `${item.number || 'Счёт'} — ${item.amount.toLocaleString('ru-RU')} ₽`)} />
                  <PortalBlock icon={<FileText className="h-4 w-4" />} title="Акты и договоры" empty="Нет доступных актов и договоров" items={[
                    ...(selectedCase.acts ?? []).map((item) => `Акт ${item.number || 'без номера'} — ${item.amount.toLocaleString('ru-RU')} ₽`),
                    ...(selectedCase.contracts ?? []).map((item) => `Договор ${item.number || 'без номера'}${item.amount != null ? ` — ${item.amount.toLocaleString('ru-RU')} ₽` : ''}`),
                  ]} />
                  <PortalBlock icon={<CalendarDays className="h-4 w-4" />} title="События" empty="Нет доступных событий" items={(selectedCase.events ?? []).map((item) => `${formatDate(item.occurredAt)} — ${item.title}`)} />
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
