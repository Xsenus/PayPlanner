import { Pencil, Plus, X } from 'lucide-react';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';

import { useToast } from '../../hooks/useToast';
import { useDialog } from '../../hooks/useDialog';
import { useAsyncOperationOwner } from '../../hooks/useAsyncOperationOwner';
import { apiService } from '../../services/api';
import { captureAuthSessionIdentity, isAuthSessionIdentityCurrent, type AuthSessionIdentity } from '../../services/authService';
import type {
  CompanyBranch,
  CompanyBranchInput,
  CompanyBranchKind,
  LegalEntityBankAccount,
} from '../../types';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { awaitOwnedUiResult, StaleUiOperationError } from '../../utils/StaleUiOperationError';
import { SystemSelect } from '../ui/SystemSelect';

interface CompanyBranchesSectionProps {
  companyId: number;
  /** Счета компании из карточки; к филиалу можно привязать только сохранённый активный счёт. */
  bankAccounts: LegalEntityBankAccount[];
  readOnly: boolean;
  ownerScope?: string;
}

const INPUT_CLASS =
  'w-full rounded-xl border border-slate-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100';

const COMPANY_BRANCH_KIND_LABELS: Record<CompanyBranchKind, string> = {
  Branch: 'Филиал',
  Office: 'Офис',
  Point: 'Точка',
  Direction: 'Направление',
};

const EMPTY_FORM: CompanyBranchInput = {
  kind: 'Branch',
  name: '',
  code: '',
  status: 'Active',
  kpp: '',
  address: '',
  bankAccountId: '',
  notes: '',
};

const toInput = (branch: CompanyBranch): CompanyBranchInput => ({
  expectedVersion: branch.editVersion,
  kind: branch.kind,
  name: branch.name,
  code: branch.code ?? '',
  status: branch.status,
  kpp: branch.kpp ?? '',
  address: branch.address ?? '',
  bankAccountId: branch.bankAccountId ?? '',
  signerJson: branch.signerJson ?? null,
  notes: branch.notes ?? '',
});

const blankToNull = (value: string | null | undefined) => {
  const trimmed = value?.trim();
  return trimmed ? trimmed : null;
};

const validVersion = (value: unknown): value is string => typeof value === 'string' && /^[a-f\d]{64}$/i.test(value);
const VERSION_CONFLICT = 'Подразделение изменено после открытия формы. Ваш ввод сохранён. Загрузите сохранённые данные перед новым изменением.';
function isVersionConflict(error: unknown) {
  const raw = error instanceof Error ? error.message : '';
  const marker = raw.indexOf(' - ');
  try {
    const payload: unknown = JSON.parse(marker < 0 ? raw : raw.slice(marker + 3));
    return !!payload && typeof payload === 'object' && 'code' in payload && payload.code === 'CompanyBranchVersionConflict';
  } catch { return false; }
}

function isBranch(value: unknown, companyId: number): value is CompanyBranch {
  const branch = value as Partial<CompanyBranch> | null;
  return !!branch && Number.isInteger(branch.id) && branch.id! > 0 && branch.legalEntityId === companyId
    && typeof branch.name === 'string' && !!branch.name.trim()
    && ['Branch', 'Office', 'Point', 'Direction'].includes(branch.kind ?? '')
    && ['Active', 'Archived'].includes(branch.status ?? '');
}

export function CompanyBranchesSection({ companyId, bankAccounts, readOnly, ownerScope }: CompanyBranchesSectionProps) {
  const { epoch, actor, authenticated } = captureAuthSessionIdentity();
  const session = useMemo(() => ({ epoch, actor, authenticated }), [epoch, actor, authenticated]);
  if (!authenticated || !Number.isInteger(companyId) || companyId <= 0) return null;
  return <CompanyBranchesScreen key={JSON.stringify([companyId, readOnly, ownerScope, session])}
    companyId={companyId} bankAccounts={bankAccounts} readOnly={readOnly} session={session} />;
}

function CompanyBranchesScreen({ companyId, bankAccounts, readOnly, session }: Omit<CompanyBranchesSectionProps, 'ownerScope'> & { session: AuthSessionIdentity }) {
  const toast = useToast();
  const { confirm } = useDialog();
  const { capture, isCurrent } = useAsyncOperationOwner('company-branches');
  const owner = capture();
  const current = useCallback((value: number) => isCurrent(value) && isAuthSessionIdentityCurrent(session), [isCurrent, session]);
  const [branches, setBranches] = useState<CompanyBranch[]>([]);
  const [loading, setLoading] = useState(false);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [editor, setEditor] = useState<{ id: number | null; form: CompanyBranchInput } | null>(null);
  const [editorError, setEditorError] = useState<string | null>(null);
  const [editorNeedsReload, setEditorNeedsReload] = useState(false);
  const [listNeedsReload, setListNeedsReload] = useState(false);
  const [busy, setBusy] = useState(false);
  const mutation = useRef<object | null>(null);
  const listRequest = useRef(0);
  const listController = useRef<AbortController | null>(null);
  const reloadController = useRef<AbortController | null>(null);
  const editorGeneration = useRef(0);
  const editorAtRender = editorGeneration.current;
  const editorRef = useRef(editor);
  editorRef.current = editor;

  const selectableAccounts = useMemo(
    () => bankAccounts.filter((account) => account.id && !account.isArchived),
    [bankAccounts],
  );

  const load = useCallback(async (loadOwner: number) => {
    if (!current(loadOwner)) return;
    const request = ++listRequest.current;
    listController.current?.abort();
    const controller = new AbortController(); listController.current = controller;
    const belongs = () => current(loadOwner) && request === listRequest.current && !controller.signal.aborted;
    setLoading(true);
    setLoadError(null);
    setBranches([]);
    try {
      const items = await awaitOwnedUiResult(() => apiService.getCompanyBranches(companyId, true, controller.signal), belongs);
      if (!Array.isArray(items) || !items.every(branch => isBranch(branch, companyId)) || new Set(items.map(branch => branch.id)).size !== items.length)
        throw new Error('Не удалось подтвердить список подразделений этой компании. Повторите загрузку.');
      setBranches(items);
      setListNeedsReload(false);
      return items;
    } catch (error) {
      if (error instanceof StaleUiOperationError || !belongs()) return;
      setLoadError(extractDisplayErrorMessage(error, 'Не удалось загрузить подразделения.'));
    } finally {
      if (belongs()) setLoading(false);
    }
  }, [companyId, current]);

  useEffect(() => {
    const pending = listController;
    const prompt = reloadController;
    void load(capture());
    return () => { pending.current?.abort(); prompt.current?.abort(); };
  }, [capture, load]);

  const closeEditor = () => {
    if (!current(owner) || editorGeneration.current !== editorAtRender) return;
    ++editorGeneration.current;
    reloadController.current?.abort();
    setEditor(null);
    setEditorError(null);
    setEditorNeedsReload(false);
  };

  const openEditor = (branch?: CompanyBranch) => {
    if (!current(owner) || readOnly || loading || loadError || listNeedsReload || mutation.current || branch && !branches.includes(branch)) return;
    ++editorGeneration.current;
    setEditorError(null);
    setEditorNeedsReload(false);
    setEditor(branch ? { id: branch.id, form: toInput(branch) } : { id: null, form: { ...EMPTY_FORM } });
  };

  const updateForm = (patch: Partial<CompanyBranchInput>) => {
    if (!current(owner) || readOnly || mutation.current || editorGeneration.current !== editorAtRender) return;
    setEditor(value => value ? { ...value, form: { ...value.form, ...patch } } : value);
  };

  const save = async () => {
    if (!editor || editorRef.current !== editor || !current(owner) || readOnly || loading || loadError || listNeedsReload || editorNeedsReload || mutation.current || editorGeneration.current !== editorAtRender) {
      return;
    }

    if (editor.id !== null && !validVersion(editor.form.expectedVersion)) {
      setEditorNeedsReload(true); setEditorError(VERSION_CONFLICT); return;
    }

    if (!editor.form.name.trim()) {
      setEditorError('Укажите название подразделения.');
      return;
    }

    const payload: CompanyBranchInput = {
      ...editor.form,
      name: editor.form.name.trim(),
      code: blankToNull(editor.form.code),
      kpp: blankToNull(editor.form.kpp),
      address: blankToNull(editor.form.address),
      bankAccountId: blankToNull(editor.form.bankAccountId),
      notes: blankToNull(editor.form.notes),
    };

    const attempt = {}; mutation.current = attempt;
    const ownsEditor = () => current(owner) && editorGeneration.current === editorAtRender;
    setBusy(true);
    setEditorError(null);
    try {
      const saved = await awaitOwnedUiResult(() => editor.id === null
        ? apiService.createCompanyBranch(companyId, payload)
        : apiService.updateCompanyBranch(companyId, editor.id, payload), () => current(owner));
      if (!isBranch(saved, companyId) || editor.id !== null && saved.id !== editor.id)
        throw new Error('Не удалось подтвердить сохранение подразделения этой компании. Обновите список перед следующим изменением.');
      if (ownsEditor()) {
        toast.success('Подразделение сохранено.', 'Подразделения');
        closeEditor();
      }
      await load(owner);
    } catch (error) {
      if (error instanceof StaleUiOperationError || !current(owner)) return;
      if (ownsEditor()) {
        if (isVersionConflict(error)) setEditorNeedsReload(true);
        setEditorError(extractDisplayErrorMessage(error, 'Не удалось сохранить подразделение.'));
      }
      else await load(owner);
    } finally {
      if (mutation.current === attempt) {
        mutation.current = null;
        if (current(owner)) setBusy(false);
      }
    }
  };

  const reloadEditor = async () => {
    if (!editor || editor.id === null || editorRef.current !== editor || !current(owner) || readOnly || mutation.current || !editorNeedsReload) return;
    const generation = editorAtRender;
    const controller = new AbortController(); reloadController.current = controller;
    const ownsEditor = () => current(owner) && editorGeneration.current === generation && !controller.signal.aborted;
    const attempt = {}; mutation.current = attempt; setBusy(true);
    try {
      const approved = await awaitOwnedUiResult(() => confirm({ signal: controller.signal,
        title: 'Загрузить сохранённые данные?',
        description: 'Введённые в этой форме изменения будут заменены текущими данными подразделения. Можно отменить загрузку и сохранить свой ввод для сравнения.',
        confirmLabel: 'Заменить ввод', cancelLabel: 'Оставить ввод',
      }), ownsEditor);
      if (!approved) return;
      const items = await load(owner);
      if (!ownsEditor()) return;
      const saved = items?.find(item => item.id === editor.id);
      if (!saved || !validVersion(saved.editVersion)) {
        setEditorError('Не удалось получить актуальное подразделение. Ваш ввод сохранён; закройте форму и проверьте доступный список.'); return;
      }
      setEditor({ id: saved.id, form: toInput(saved) });
      setEditorError(null); setEditorNeedsReload(false);
    } catch (error) {
      if (!(error instanceof StaleUiOperationError) && ownsEditor())
        setEditorError(extractDisplayErrorMessage(error, 'Не удалось загрузить сохранённые данные. Ваш ввод сохранён.'));
    } finally {
      controller.abort();
      if (reloadController.current === controller) reloadController.current = null;
      if (mutation.current === attempt) { mutation.current = null; if (current(owner)) setBusy(false); }
    }
  };

  const toggleArchived = async (branch: CompanyBranch) => {
    if (!current(owner) || readOnly || loading || loadError || listNeedsReload || editorRef.current || mutation.current || !branches.includes(branch)) return;
    if (!validVersion(branch.editVersion)) { setListNeedsReload(true); return; }
    const attempt = {}; mutation.current = attempt; setBusy(true);
    const archived = branch.status !== 'Archived';
    try {
      const saved = await awaitOwnedUiResult(() => apiService.updateCompanyBranch(companyId, branch.id, {
        ...toInput(branch),
        status: archived ? 'Archived' : 'Active',
      }), () => current(owner));
      if (!isBranch(saved, companyId) || saved.id !== branch.id || saved.status !== (archived ? 'Archived' : 'Active'))
        throw new Error('Не удалось подтвердить статус подразделения этой компании. Обновите список перед следующим изменением.');
      toast.success(archived ? 'Подразделение перенесено в архив.' : 'Подразделение возвращено из архива.', 'Подразделения');
      await load(owner);
    } catch (error) {
      if (error instanceof StaleUiOperationError || !current(owner)) return;
      if (isVersionConflict(error)) setListNeedsReload(true);
      toast.error(extractDisplayErrorMessage(error, 'Не удалось изменить статус подразделения.'), 'Подразделения');
    } finally {
      if (mutation.current === attempt) {
        mutation.current = null;
        if (current(owner)) setBusy(false);
      }
    }
  };

  const accountLabel = (accountId?: string | null) => {
    if (!accountId) {
      return 'Основной счет компании';
    }

    const account = bankAccounts.find((item) => item.id === accountId);
    return account ? `${account.bank} · ${account.accountNumber}` : 'Счет не найден';
  };

  return (
    <section className="mt-4 rounded-2xl border border-slate-200 bg-white p-3" data-testid="company-branches-section">
      <div className="flex items-center justify-between gap-3">
        <div>
          <h3 className="text-base font-semibold text-slate-900">Филиалы, офисы и точки</h3>
          <p className="text-xs text-slate-500">
            Пустые поля берутся у компании: ИНН, название и реквизиты остаются общими.
          </p>
        </div>
        {!readOnly ? (
          <button
            type="button"
            onClick={() => openEditor()}
            disabled={busy || loading || !!loadError || listNeedsReload || !!editor}
            className="inline-flex items-center gap-2 rounded-xl bg-blue-600 px-3 py-2 text-sm font-semibold text-white transition hover:bg-blue-700"
          >
            <Plus className="h-4 w-4" />
            Добавить
          </button>
        ) : null}
      </div>

      {listNeedsReload ? <div role="alert" className="mt-3 text-sm text-rose-600"><p>Данные подразделений устарели. Обновите список перед изменением статуса.</p>
        <button type="button" className="mt-1 underline" disabled={busy || loading} onClick={() => void load(owner)}>Обновить подразделения</button>
      </div> : null}

      {loadError ? <div role="alert" className="mt-3 text-sm text-rose-600"><p>{loadError}</p>
        <button type="button" className="mt-1 underline" disabled={busy || loading} onClick={() => void load(owner)}>Повторить загрузку</button>
      </div> : null}

      <div className="mt-3 overflow-x-auto">
        <table className="min-w-full divide-y divide-slate-200 text-sm">
          <thead className="bg-slate-50 text-left text-slate-600">
            <tr>
              <th className="px-3 py-2 font-semibold">Название</th>
              <th className="px-3 py-2 font-semibold">Вид</th>
              <th className="px-3 py-2 font-semibold">КПП</th>
              <th className="px-3 py-2 font-semibold">Счет</th>
              <th className="px-3 py-2 font-semibold">Статус</th>
              {!readOnly ? <th className="px-3 py-2 text-right font-semibold">Действия</th> : null}
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100">
            {branches.length === 0 ? (
              <tr>
                <td colSpan={readOnly ? 5 : 6} className="px-3 py-6 text-center text-slate-500">
                  {loading ? 'Загрузка…' : loadError ? 'Список подразделений недоступен.' : 'Подразделения не добавлены.'}
                </td>
              </tr>
            ) : (
              branches.map((branch) => (
                <tr key={branch.id} className={branch.status === 'Archived' ? 'bg-slate-50 text-slate-400' : undefined}>
                  <td className="px-3 py-2 font-medium text-slate-900">{branch.name}</td>
                  <td className="px-3 py-2">{COMPANY_BRANCH_KIND_LABELS[branch.kind] ?? branch.kind}</td>
                  <td className="px-3 py-2">{branch.kpp || 'как у компании'}</td>
                  <td className="px-3 py-2">{accountLabel(branch.bankAccountId)}</td>
                  <td className="px-3 py-2">{branch.status === 'Archived' ? 'Архив' : 'Активно'}</td>
                  {!readOnly ? (
                    <td className="px-3 py-2">
                      <div className="flex items-center justify-end gap-2">
                        <button
                          type="button"
                          onClick={() => openEditor(branch)}
                          disabled={busy || loading || !!loadError || listNeedsReload || !!editor}
                          className="inline-flex h-8 w-8 items-center justify-center rounded-lg border border-slate-200 text-slate-500 transition hover:bg-slate-50"
                          title="Редактировать подразделение"
                        >
                          <Pencil className="h-4 w-4" />
                        </button>
                        <button
                          type="button"
                          onClick={() => void toggleArchived(branch)}
                          disabled={busy || loading || !!loadError || listNeedsReload || !!editor}
                          className="inline-flex h-8 items-center rounded-lg border border-slate-200 px-2 text-xs text-slate-500 transition hover:bg-slate-50"
                        >
                          {branch.status === 'Archived' ? 'Из архива' : 'В архив'}
                        </button>
                      </div>
                    </td>
                  ) : null}
                </tr>
              ))
            )}
          </tbody>
        </table>
      </div>

      {editor && typeof document !== 'undefined'
        ? createPortal(
            <div className="fixed inset-0 z-[2300] flex items-center justify-center bg-slate-900/45 p-4">
              <div className="w-full max-w-2xl rounded-3xl bg-white shadow-2xl" role="dialog" aria-label="Подразделение">
                <div className="flex items-start justify-between border-b border-slate-200 px-4 py-3">
                  <h3 className="text-lg font-semibold text-slate-900">
                    {editor.id === null ? 'Добавить подразделение' : 'Редактировать подразделение'}
                  </h3>
                  <button
                    type="button"
                    onClick={closeEditor}
                    className="rounded-xl p-2 text-slate-400 transition hover:bg-slate-100"
                    aria-label="Закрыть"
                  >
                    <X className="h-5 w-5" />
                  </button>
                </div>

                {editorError ? (
                  <p className="mx-4 mt-3 rounded-xl border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">
                    {editorError}
                  </p>
                ) : null}

                {editorNeedsReload ? <button type="button" className="mx-4 mt-2 text-sm text-blue-700 underline" disabled={busy || loading}
                  onClick={() => void reloadEditor()}>Загрузить сохранённые данные</button> : null}

                <fieldset disabled={busy} className="grid min-w-0 gap-3 px-4 py-3 md:grid-cols-2">
                  <label className="block">
                    <span className="mb-1 block text-sm font-medium text-slate-700">Вид</span>
                    <SystemSelect
                      value={editor.form.kind}
                      onChange={(event) => updateForm({ kind: event.target.value as CompanyBranchKind })}
                      className={INPUT_CLASS}
                    >
                      {(Object.keys(COMPANY_BRANCH_KIND_LABELS) as CompanyBranchKind[]).map((kind) => (
                        <option key={kind} value={kind}>
                          {COMPANY_BRANCH_KIND_LABELS[kind]}
                        </option>
                      ))}
                    </SystemSelect>
                  </label>
                  <label className="block">
                    <span className="mb-1 block text-sm font-medium text-slate-700">Название</span>
                    <input
                      type="text"
                      value={editor.form.name}
                      onChange={(event) => updateForm({ name: event.target.value })}
                      className={INPUT_CLASS}
                    />
                  </label>
                  <label className="block">
                    <span className="mb-1 block text-sm font-medium text-slate-700">КПП подразделения</span>
                    <input
                      type="text"
                      value={editor.form.kpp ?? ''}
                      onChange={(event) => updateForm({ kpp: event.target.value })}
                      className={INPUT_CLASS}
                      placeholder="Пусто — КПП компании"
                    />
                  </label>
                  <label className="block">
                    <span className="mb-1 block text-sm font-medium text-slate-700">Код</span>
                    <input
                      type="text"
                      value={editor.form.code ?? ''}
                      onChange={(event) => updateForm({ code: event.target.value })}
                      className={INPUT_CLASS}
                    />
                  </label>
                  <label className="block md:col-span-2">
                    <span className="mb-1 block text-sm font-medium text-slate-700">Адрес</span>
                    <input
                      type="text"
                      value={editor.form.address ?? ''}
                      onChange={(event) => updateForm({ address: event.target.value })}
                      className={INPUT_CLASS}
                      placeholder="Пусто — адрес компании"
                    />
                  </label>
                  <label className="block md:col-span-2">
                    <span className="mb-1 block text-sm font-medium text-slate-700">Банковский счет</span>
                    <SystemSelect
                      value={editor.form.bankAccountId ?? ''}
                      onChange={(event) => updateForm({ bankAccountId: event.target.value })}
                      className={INPUT_CLASS}
                    >
                      <option value="">Основной счет компании</option>
                      {selectableAccounts.map((account) => (
                        <option key={account.id} value={account.id}>
                          {account.bank} · {account.accountNumber}
                        </option>
                      ))}
                    </SystemSelect>
                    {selectableAccounts.length === 0 ? (
                      <span className="mt-1 block text-xs text-slate-500">
                        Сначала сохраните карточку компании с банковским счетом.
                      </span>
                    ) : null}
                  </label>
                </fieldset>

                <div className="flex items-center justify-end gap-3 border-t border-slate-200 px-4 py-3">
                  <button
                    type="button"
                    onClick={closeEditor}
                    className="rounded-xl border border-slate-200 px-4 py-2 text-sm font-semibold text-slate-600 transition hover:bg-slate-50"
                  >
                    Отмена
                  </button>
                  <button
                    type="button"
                    disabled={busy || loading || !!loadError || editorNeedsReload || listNeedsReload}
                    onClick={() => void save()}
                    className="rounded-xl bg-blue-600 px-4 py-2 text-sm font-semibold text-white transition hover:bg-blue-700 disabled:opacity-50"
                  >
                    Сохранить
                  </button>
                </div>
              </div>
            </div>,
            document.body,
          )
        : null}
    </section>
  );
}
