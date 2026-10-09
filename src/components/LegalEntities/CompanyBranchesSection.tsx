import { Pencil, Plus, X } from 'lucide-react';
import { useCallback, useEffect, useMemo, useState } from 'react';
import { createPortal } from 'react-dom';

import { useToast } from '../../hooks/useToast';
import { apiService } from '../../services/api';
import type {
  CompanyBranch,
  CompanyBranchInput,
  CompanyBranchKind,
  LegalEntityBankAccount,
} from '../../types';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { SystemSelect } from '../ui/SystemSelect';

interface CompanyBranchesSectionProps {
  companyId: number;
  /** Счета компании из карточки; к филиалу можно привязать только сохранённый активный счёт. */
  bankAccounts: LegalEntityBankAccount[];
  readOnly: boolean;
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

export function CompanyBranchesSection({ companyId, bankAccounts, readOnly }: CompanyBranchesSectionProps) {
  const toast = useToast();
  const [branches, setBranches] = useState<CompanyBranch[]>([]);
  const [loading, setLoading] = useState(false);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [editor, setEditor] = useState<{ id: number | null; form: CompanyBranchInput } | null>(null);
  const [editorError, setEditorError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  const selectableAccounts = useMemo(
    () => bankAccounts.filter((account) => account.id && !account.isArchived),
    [bankAccounts],
  );

  const load = useCallback(async () => {
    setLoading(true);
    setLoadError(null);
    try {
      setBranches(await apiService.getCompanyBranches(companyId, true));
    } catch (error) {
      setLoadError(extractDisplayErrorMessage(error, 'Не удалось загрузить подразделения.'));
    } finally {
      setLoading(false);
    }
  }, [companyId]);

  useEffect(() => {
    void load();
  }, [load]);

  const closeEditor = () => {
    setEditor(null);
    setEditorError(null);
  };

  const updateForm = (patch: Partial<CompanyBranchInput>) =>
    setEditor((current) => (current ? { ...current, form: { ...current.form, ...patch } } : current));

  const save = async () => {
    if (!editor) {
      return;
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

    setSaving(true);
    setEditorError(null);
    try {
      if (editor.id === null) {
        await apiService.createCompanyBranch(companyId, payload);
      } else {
        await apiService.updateCompanyBranch(companyId, editor.id, payload);
      }
      toast.success('Подразделение сохранено.', 'Подразделения');
      closeEditor();
      await load();
    } catch (error) {
      setEditorError(extractDisplayErrorMessage(error, 'Не удалось сохранить подразделение.'));
    } finally {
      setSaving(false);
    }
  };

  const toggleArchived = async (branch: CompanyBranch) => {
    const archived = branch.status !== 'Archived';
    try {
      await apiService.updateCompanyBranch(companyId, branch.id, {
        ...toInput(branch),
        status: archived ? 'Archived' : 'Active',
      });
      toast.success(archived ? 'Подразделение перенесено в архив.' : 'Подразделение возвращено из архива.', 'Подразделения');
      await load();
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось изменить статус подразделения.'), 'Подразделения');
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
            onClick={() => setEditor({ id: null, form: { ...EMPTY_FORM } })}
            className="inline-flex items-center gap-2 rounded-xl bg-blue-600 px-3 py-2 text-sm font-semibold text-white transition hover:bg-blue-700"
          >
            <Plus className="h-4 w-4" />
            Добавить
          </button>
        ) : null}
      </div>

      {loadError ? <p className="mt-3 text-sm text-rose-600">{loadError}</p> : null}

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
                  {loading ? 'Загрузка…' : 'Подразделения не добавлены.'}
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
                          onClick={() => setEditor({ id: branch.id, form: toInput(branch) })}
                          className="inline-flex h-8 w-8 items-center justify-center rounded-lg border border-slate-200 text-slate-500 transition hover:bg-slate-50"
                          title="Редактировать подразделение"
                        >
                          <Pencil className="h-4 w-4" />
                        </button>
                        <button
                          type="button"
                          onClick={() => void toggleArchived(branch)}
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

                <div className="grid gap-3 px-4 py-3 md:grid-cols-2">
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
                </div>

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
                    disabled={saving}
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
