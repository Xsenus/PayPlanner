import { useCallback, useEffect, useId, useMemo, useRef, useState } from 'react';
import { useAsyncOperationOwner } from '../../hooks/useAsyncOperationOwner';
import { useDialog } from '../../hooks/useDialog';
import { useToast } from '../../hooks/useToast';
import { apiService } from '../../services/api';
import { isAuthSessionIdentityCurrent, type AuthSessionIdentity } from '../../services/authService';
import type { CompanyBankActiveBinding, CompanyBankMatchAccount, CompanyBankMatchReport } from '../../services/companyBankBindings';
import type { BankConnectionItem } from '../../types';
import { extractDisplayErrorMessage, isHttpErrorStatus } from '../../utils/errorUtils';
import { awaitOwnedUiResult } from '../../utils/StaleUiOperationError';
import { SystemSelect } from '../ui/SystemSelect';

export type BankConnectionRun = (action: (current: () => boolean) => Promise<void>, failure: string, write?: boolean, refresh?: boolean) => Promise<void>;
interface Props { companyId: number; connection: BankConnectionItem; session: AuthSessionIdentity; readOnly: boolean; profileDirty: boolean; busy: boolean; run: BankConnectionRun }

const ISSUE_LABELS: Record<string, string> = {
  CompanyNotOwn: 'Компания исключена из собственных: доступен только отзыв сохранённых связей.',
  CompanyUnavailable: 'Профиль компании не активен: новые связи недоступны.',
  InvalidProfileAccounts: 'Список счетов профиля повреждён.',
  DuplicateProfileId: 'В профиле повторяются идентификаторы счетов.',
  MissingStableProfileId: 'Сначала сохраните стабильные идентификаторы всех счетов профиля.',
  IncompleteProfileIdentity: 'У части счетов профиля не заполнены номер, БИК или валюта.',
  ProfileAccountsTooLarge: 'Список счетов профиля слишком большой для сверки.',
  IncompleteBankIdentity: 'У банковского счёта не заполнены номер, БИК или валюта.',
  ConnectionUnavailable: 'Подключение банка недоступно.',
  BankAccountUnavailable: 'Состояние банковского счёта не разрешает новую связь.',
  BankAccountHidden: 'Банковский счёт скрыт.',
  ArchivedProfileCandidates: 'Есть архивные счета профиля с такими реквизитами.',
  MultipleBankSources: 'С такими реквизитами найдено несколько банковских источников. Выберите нужный источник явно.',
  LinkedMoneyIdentityMismatch: 'Явная денежная ссылка не совпадает по компании или реквизитам.',
  MultipleLinkedMoneyAccounts: 'У банковского счёта несколько денежных ссылок: требуется проверка.',
  LinkedMoneyAccountInactive: 'Связанный денежный счёт не активен.',
  ManualMoneyCandidatesRequireReview: 'Есть совпадающие ручные денежные счета. Они не объединяются автоматически.',
  SourceMetadataUnavailable: 'Реквизиты банковского источника требуют уточнения. Новая связь недоступна.',
};
const ADVISORY = new Set(['ArchivedProfileCandidates', 'MultipleBankSources', 'ManualMoneyCandidatesRequireReview']);
const buttonClass = 'rounded-lg border border-slate-300 px-2 py-1 text-sm disabled:opacity-50';
const label = (issue: string) => ISSUE_LABELS[issue] ?? 'Сверка содержит неизвестное ограничение. Обновите данные перед подтверждением.';
const PROVIDERS: Record<string, string> = { Sber: 'Сбербанк', Tochka: 'Точка' };
const providerLabel = (provider: string) => PROVIDERS[provider] ?? provider;
const sourceLabel = (source: CompanyBankMatchAccount, multiple = false) => `${providerLabel(source.provider)} · ${source.accountNumber || 'Номер не указан'} · ${source.currency || 'Валюта не указана'} · БИК ${source.bik || 'не указан'}${multiple ? ` · источник ${source.externalAccountId || 'не подтверждён'}` : ''}`;

export function CompanyBankBindingsSection({ companyId, connection, session, readOnly, profileDirty, busy, run }: Props) {
  const toast = useToast(); const dialog = useDialog();
  const controlId = useId();
  const { capture, isCurrent } = useAsyncOperationOwner(JSON.stringify([companyId, connection.id, readOnly, profileDirty, session]));
  const owner = capture();
  const current = useCallback(() => isCurrent(owner) && isAuthSessionIdentityCurrent(session), [isCurrent, owner, session]);
  const [expanded, setExpanded] = useState(false);
  const [report, setReport] = useState<CompanyBankMatchReport | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [bankId, setBankId] = useState(''); const [profileId, setProfileId] = useState('');
  const [visibleBindings, setVisibleBindings] = useState(20);
  const read = useRef<AbortController | null>(null); const attempt = useRef<AbortController | null>(null);
  const readSequence = useRef(0);
  useEffect(() => {
    const readers = read; const writers = attempt;
    return () => { readers.current?.abort(); writers.current?.abort(); };
  }, [companyId, connection.id, readOnly, profileDirty, session]);

  const load = async (belongs = current) => {
    if (!belongs() || !current()) return null;
    read.current?.abort(); const controller = new AbortController(); read.current = controller;
    const sequence = ++readSequence.current;
    const owned = () => belongs() && current() && !controller.signal.aborted && sequence === readSequence.current;
    setLoading(true); setError(null); setReport(null); setBankId(''); setProfileId(''); setVisibleBindings(20);
    try {
      const value = await apiService.getCompanyBankAccountMatches(companyId, connection.id, controller.signal);
      if (!owned()) return null;
      setReport(value); return value;
    } catch (failure) {
      if (owned()) setError(isHttpErrorStatus(failure, 401) || isHttpErrorStatus(failure, 403)
        ? 'Доступ к банковской сверке отозван.' : extractDisplayErrorMessage(failure, 'Не удалось загрузить сверку счетов.'));
      return null;
    } finally {
      if (owned()) setLoading(false);
      if (read.current === controller) read.current = null;
    }
  };
  const groups = useMemo(() => new Map(report?.groups.map(x => [x.id, x]) ?? []), [report]);
  const selected = report?.accounts.find(x => String(x.bankAccountId) === bankId);
  const group = selected?.groupId ? groups.get(selected.groupId) : undefined;
  const candidate = group?.profileCandidates.find(x => x.id === profileId);
  const alreadyBound = report?.activeBindings.some(x => x.bankAccountId === selected?.bankAccountId || x.profileAccountId === profileId);
  const knownSource = selected && ['Sber', 'Tochka'].includes(selected.provider) && selected.externalAccountId.trim()
    && selected.accountNumber.trim() && selected.bik?.trim() && selected.currency.trim()
    && selected.status === 'Open' && !selected.isHidden && selected.connectionStatus === 'Active';
  const canBind = Boolean(report?.canBind && selected && candidate && knownSource && !alreadyBound
    && !report.profileIssues.some(x => x !== 'IncompleteProfileIdentity') && !selected.issues.some(x => !ADVISORY.has(x)));

  const change = (kind: 'bind' | 'revoke', binding?: CompanyBankActiveBinding) => {
    if (!current() || busy || loading || attempt.current || readOnly || profileDirty || !report || (kind === 'bind' && !canBind)) return;
    if (kind === 'revoke' && (!binding || !report.activeBindings.some(x => x.id === binding.id))) return;
    const version = report.version; const selectedBank = selected?.bankAccountId; const selectedProfile = candidate?.id;
    void run(async parentCurrent => {
      if (!current()) return;
      const controller = new AbortController(); attempt.current = controller;
      const owned = () => current() && parentCurrent() && !controller.signal.aborted;
      try {
        const confirmed = await awaitOwnedUiResult(() => dialog.confirm({ signal: controller.signal,
          title: kind === 'bind' ? 'Подтвердить банковскую связь?' : 'Снять банковскую связь?',
          description: kind === 'bind'
            ? `Банковский счёт: ${sourceLabel(selected!, (group?.bankAccountIds.length ?? 0) > 1)}; счёт профиля «${candidate?.name || selectedProfile}». Связь подтверждается по сохранённым реквизитам.`
            : `Счёт ${binding!.accountNumber} (${binding!.currency}). Сохранённые документы и денежная история сохранятся.`,
          confirmLabel: kind === 'bind' ? 'Подтвердить связь' : 'Снять связь', cancelLabel: 'Отмена', variant: kind === 'bind' ? 'primary' : 'danger',
        }), owned);
        if (!confirmed) return;
        const receipt = await awaitOwnedUiResult(() => kind === 'bind'
          ? apiService.bindCompanyBankAccount(connection.id, { bankAccountId: selectedBank!, profileAccountId: selectedProfile!, expectedVersion: version }, controller.signal)
          : apiService.revokeCompanyBankAccount(connection.id, binding!.id, version, controller.signal), owned);
        const fresh = await load(owned);
        if (!owned()) return;
        const present = fresh?.activeBindings.some(x => x.id === receipt.bindingId && (kind === 'revoke'
          || x.bankAccountId === selectedBank && x.profileAccountId === selectedProfile));
        if (fresh && (kind === 'bind' ? present : !present)) toast.success(kind === 'bind' ? 'Банковская связь подтверждена.' : 'Банковская связь снята.', 'Сверка счетов');
        else setError('Сервер подтвердил действие, но сверку нужно проверить заново. Обновите сверку перед следующим действием.');
      } catch (failure) {
        if (owned()) {
          setReport(null); setBankId(''); setProfileId('');
          setError(isHttpErrorStatus(failure, 401) || isHttpErrorStatus(failure, 403) ? 'Доступ к банковской сверке отозван.'
            : 'Данные или результат действия требуют сверки. Обновите сверку перед следующим действием.');
        }
        throw failure;
      } finally {
        controller.abort(); if (attempt.current === controller) attempt.current = null;
      }
    }, 'Не удалось подтвердить изменение банковской связи. Обновите сверку.', true, false);
  };

  return <div className="mt-3 border-t border-slate-200 pt-3" data-testid={`company-bank-bindings-${connection.id}`}>
    <button type="button" className={buttonClass} disabled={busy || loading} onClick={() => {
      if (!current() || busy || loading || attempt.current) return;
      setExpanded(true); void load();
    }}>{loading ? 'Загружаем сверку…' : expanded ? 'Обновить сверку счетов' : 'Сверить счета профиля'}</button>
    {expanded ? <div className="mt-2 space-y-2 text-sm">
      <p className="text-xs text-slate-500">Показаны сохранённые реквизиты на момент сверки. Выбор счёта не переносит прошлые операции между денежными счетами.</p>
      {readOnly ? <p className="text-xs text-slate-500">Подтверждение и снятие связи недоступны в режиме просмотра.</p> : null}
      {profileDirty ? <p role="status" className="text-amber-800">Сначала сохраните изменения счетов и состояния профиля.</p> : null}
      {error ? <p role="alert" className="text-red-700">{error}</p> : null}
      {report ? <>
        {report.profileIssues.length ? <ul className="list-inside list-disc text-amber-800">{report.profileIssues.map(x => <li key={x}>{label(x)}</li>)}</ul> : null}
        {report.canBind ? <div className="space-y-2">
          <div><label className="block" htmlFor={`${controlId}-bank`}>Банковский источник</label>
            <SystemSelect id={`${controlId}-bank`} className="mt-1 w-full min-w-0 rounded-lg border p-1.5" value={bankId} disabled={busy || loading} onChange={e => {
              if (current() && !busy && !attempt.current) { setBankId(e.target.value); setProfileId(''); }
            }}><option value="">Выберите банковский счёт</option>{report.accounts.map(x => <option key={x.bankAccountId} value={x.bankAccountId}>{sourceLabel(x, (x.groupId ? groups.get(x.groupId)?.bankAccountIds.length ?? 0 : 0) > 1)}</option>)}</SystemSelect>
          </div>
          {selected ? <>
            {!knownSource ? <p className="text-amber-800">Банковский источник недоступен для новой связи. Проверьте реквизиты и состояние счёта.</p> : null}
            {selected.issues.length ? <ul className="list-inside list-disc text-amber-800">{selected.issues.map(x => <li key={x}>{label(x)}</li>)}</ul> : null}
            {selected.linkedMoneyAccountIds.length ? <p className="text-xs text-slate-600">В денежном журнале есть явная ссылка на этот банковский источник.</p> : <p className="text-xs text-slate-600">Явной денежной ссылки нет. Совпадающий ручной счёт не будет выбран автоматически.</p>}
            {group?.profileMatchStatus === 'Ambiguous' ? <p className="text-amber-800">В профиле несколько счетов с одинаковыми реквизитами. Выберите нужную запись явно; идентификатор помогает различить одинаковые названия.</p> : null}
            {group?.profileCandidates.length ? <div><label className="block" htmlFor={`${controlId}-profile`}>Счёт профиля</label>
              <SystemSelect id={`${controlId}-profile`} title={candidate?.id} className="mt-1 w-full min-w-0 rounded-lg border p-1.5" value={profileId} disabled={busy || loading || readOnly || profileDirty} onChange={e => {
                if (current() && !busy && !attempt.current && !readOnly && !profileDirty) setProfileId(e.target.value);
              }}><option value="">Выберите счёт профиля</option>{group.profileCandidates.map(x => <option key={x.id} value={x.id}>{x.name || 'Счёт профиля'} — {x.id}</option>)}</SystemSelect>
            </div> : <p className="text-amber-800">Активный счёт профиля с полным совпадением номера, БИК и валюты не найден.</p>}
            {alreadyBound ? <p className="text-slate-600">У выбранного банковского счёта или записи профиля уже есть связь. Для изменения сначала снимите прежнюю связь.</p> : null}
            <button type="button" className={buttonClass} disabled={busy || loading || readOnly || profileDirty || !canBind} onClick={() => change('bind')}>Подтвердить связь</button>
          </> : null}
        </div> : <p className="text-slate-600">Новые связи недоступны. Можно просмотреть и снять сохранённое подтверждение.</p>}
        <div className="space-y-2">
          <p className="font-medium">Подтверждённые связи компании ({report.activeBindings.length})</p>
          {report.activeBindings.length === 0 ? <p className="text-slate-500">Подтверждённых связей пока нет.</p> : <ul className="space-y-2">{report.activeBindings.slice(0, visibleBindings).map(x => <li key={x.id} className="rounded-lg border p-2">
            <p className="break-all">{x.accountNumber || 'Номер не указан'} · {x.currency || 'Валюта не указана'} · БИК {x.bik || 'не указан'}</p>
            <p className="max-h-16 overflow-auto break-all text-xs text-slate-500">Счёт профиля: {x.profileAccountId || 'Идентификатор недоступен'}</p>
            <button type="button" className={`${buttonClass} mt-1`} disabled={busy || loading || readOnly || profileDirty} onClick={() => change('revoke', x)}>Снять связь</button>
          </li>)}</ul>}
          {report.activeBindings.length > visibleBindings ? <button type="button" className={buttonClass} onClick={() => { if (current()) setVisibleBindings(x => x + 20); }}>Показать ещё связи</button> : null}
        </div>
      </> : null}
    </div> : null}
  </div>;
}
