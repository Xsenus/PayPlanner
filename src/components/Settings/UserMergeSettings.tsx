import { SystemSelect } from '../ui/SystemSelect';
import { useCallback, useEffect, useMemo, useState } from 'react';
import { Check, GitMerge, Loader2, RefreshCw } from 'lucide-react';
import { authService, type User } from '../../services/authService';
import { useDialog } from '../../hooks/useDialog';
import { useToast } from '../../hooks/useToast';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';

type MergeFormState = {
  targetUserId: number | null;
  sourceUserIds: number[];
  targetSearch: string;
  sourceSearch: string;
  primaryEmail: string;
  primaryPhoneNumber: string;
};

const EMPTY_FORM: MergeFormState = {
  targetUserId: null,
  sourceUserIds: [],
  targetSearch: '',
  sourceSearch: '',
  primaryEmail: '',
  primaryPhoneNumber: '',
};

function normalize(value?: string | null) {
  return value?.trim().toLowerCase() ?? '';
}

function providerLabel(provider?: string | null) {
  if (provider === 'vk') return 'VK ID';
  if (provider === 'yandex') return 'Яндекс ID';
  if (provider === 'telegram') return 'Telegram';
  return 'Google';
}

function userSearchValue(user: User) {
  return [
    user.fullName,
    user.email,
    user.phoneNumber,
    ...(user.externalIdentities ?? []).flatMap((identity) => [
      identity.provider,
      identity.providerEmail,
      identity.providerUsername,
      identity.providerUserId,
    ]),
  ]
    .filter(Boolean)
    .join(' ')
    .toLowerCase();
}

function displayUserName(user: User) {
  return user.fullName || user.email || `Пользователь #${user.id}`;
}

export function UserMergeSettings() {
  const toast = useToast();
  const { confirm } = useDialog();
  const [users, setUsers] = useState<User[]>([]);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [form, setForm] = useState<MergeFormState>(EMPTY_FORM);

  const loadUsers = useCallback(async () => {
    const nextUsers = await authService.getUsers();
    setUsers(nextUsers);
    return nextUsers;
  }, []);

  useEffect(() => {
    setLoading(true);
    void loadUsers()
      .catch((error) => {
        toast.error(extractDisplayErrorMessage(error, 'Не удалось загрузить пользователей для объединения.'));
      })
      .finally(() => setLoading(false));
  }, [loadUsers, toast]);

  const usersById = useMemo(() => new Map(users.map((user) => [user.id, user])), [users]);

  const targetUser = useMemo(
    () => (form.targetUserId ? usersById.get(form.targetUserId) ?? null : null),
    [form.targetUserId, usersById],
  );
  const sourceUsers = useMemo(
    () =>
      form.sourceUserIds
        .map((userId) => usersById.get(userId) ?? null)
        .filter(Boolean) as User[],
    [form.sourceUserIds, usersById],
  );

  const targetCandidates = useMemo(() => {
    const query = normalize(form.targetSearch);
    return users.filter((user) => !query || userSearchValue(user).includes(query));
  }, [form.targetSearch, users]);

  const sourceCandidates = useMemo(() => {
    const query = normalize(form.sourceSearch);
    return users.filter((user) => {
      if (user.id === form.targetUserId) {
        return false;
      }

      return !query || userSearchValue(user).includes(query);
    });
  }, [form.sourceSearch, form.targetUserId, users]);

  const emailOptions = useMemo(() => {
    const values = new Set<string>();
    [targetUser, ...sourceUsers].forEach((user) => {
      if (!user) {
        return;
      }

      if (user.email?.trim()) {
        values.add(user.email.trim());
      }

      (user.externalIdentities ?? []).forEach((identity) => {
        if (identity.providerEmail?.trim()) {
          values.add(identity.providerEmail.trim());
        }
      });
    });

    return Array.from(values);
  }, [sourceUsers, targetUser]);

  const phoneOptions = useMemo(() => {
    const values = new Set<string>();
    [targetUser, ...sourceUsers].forEach((user) => {
      if (user?.phoneNumber?.trim()) {
        values.add(user.phoneNumber.trim());
      }
    });
    return Array.from(values);
  }, [sourceUsers, targetUser]);

  useEffect(() => {
    if (!targetUser) {
      setForm((current) => {
        if (
          current.sourceUserIds.length === 0 &&
          current.primaryEmail === '' &&
          current.primaryPhoneNumber === ''
        ) {
          return current;
        }

        return {
          ...current,
          sourceUserIds: [],
          primaryEmail: '',
          primaryPhoneNumber: '',
        };
      });
      return;
    }

    setForm((current) => {
      const primaryEmail =
        current.primaryEmail && emailOptions.includes(current.primaryEmail)
          ? current.primaryEmail
          : emailOptions[0] ?? '';
      const primaryPhoneNumber =
        current.primaryPhoneNumber && phoneOptions.includes(current.primaryPhoneNumber)
          ? current.primaryPhoneNumber
          : phoneOptions[0] ?? '';

      if (
        primaryEmail === current.primaryEmail &&
        primaryPhoneNumber === current.primaryPhoneNumber
      ) {
        return current;
      }

      return { ...current, primaryEmail, primaryPhoneNumber };
    });
  }, [emailOptions, phoneOptions, targetUser]);

  const toggleSourceUser = (userId: number) =>
    setForm((current) => ({
      ...current,
      sourceUserIds: current.sourceUserIds.includes(userId)
        ? current.sourceUserIds.filter((item) => item !== userId)
        : [...current.sourceUserIds, userId],
    }));

  const handleMerge = async () => {
    if (!targetUser || sourceUsers.length === 0) {
      toast.error('Нужно выбрать основной профиль и хотя бы один профиль-источник.');
      return;
    }

    const approved = await confirm({
      title: 'Объединить пользователей?',
      description: `В профиль "${displayUserName(targetUser)}" будут перенесены данные из ${sourceUsers.length} выбранных аккаунтов. Источники после объединения будут удалены.`,
      confirmLabel: 'Объединить',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });
    if (!approved) {
      return;
    }

    setSaving(true);
    try {
      const mergedUser = await authService.mergeUsers({
        targetUserId: targetUser.id,
        sourceUserIds: sourceUsers.map((user) => user.id),
        primaryEmail: form.primaryEmail || undefined,
        primaryPhoneNumber: form.primaryPhoneNumber || undefined,
      });

      await loadUsers();
      setForm({
        ...EMPTY_FORM,
        targetUserId: mergedUser.id,
        primaryEmail: mergedUser.email ?? '',
        primaryPhoneNumber: mergedUser.phoneNumber ?? '',
      });
      toast.success('Пользователи успешно объединены.');
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось объединить пользователей.'));
    } finally {
      setSaving(false);
    }
  };

  if (loading) {
    return (
      <div className="rounded-3xl border border-slate-200 bg-white p-6 text-sm text-slate-600 shadow-sm">
        Загружаем данные для объединения пользователей...
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <div className="rounded-3xl border border-slate-200 bg-white p-6 shadow-sm">
        <div className="flex flex-wrap items-start justify-between gap-4">
          <div>
            <h3 className="text-xl font-semibold text-slate-900">Объединение пользователей</h3>
            <p className="mt-2 max-w-3xl text-sm leading-6 text-slate-600">
              Если пользователь случайно зарегистрировался через разные провайдеры и получил
              несколько аккаунтов, здесь можно собрать их в единый профиль и выбрать основные
              контактные данные.
            </p>
          </div>

          <button
            type="button"
            onClick={() => void loadUsers()}
            className="inline-flex items-center gap-2 rounded-2xl border border-slate-300 px-4 py-2.5 text-sm font-semibold text-slate-700 transition hover:bg-slate-50"
          >
            <RefreshCw className="h-4 w-4" />
            Обновить список
          </button>
        </div>
      </div>

      <div className="grid gap-6 xl:grid-cols-[minmax(0,1fr)_minmax(0,1fr)_340px]">
        <div className="rounded-3xl border border-slate-200 bg-white p-6 shadow-sm">
          <div className="text-sm font-semibold text-slate-900">Основной профиль</div>
          <input
            type="text"
            value={form.targetSearch}
            onChange={(event) => setForm((current) => ({ ...current, targetSearch: event.target.value }))}
            placeholder="Поиск по имени, email, телефону или провайдеру"
            className="mt-3 w-full rounded-2xl border border-slate-300 px-3 py-3"
          />

          <div className="mt-4 max-h-[28rem] space-y-3 overflow-auto pr-1">
            {targetCandidates.map((user) => {
              const selected = user.id === form.targetUserId;
              return (
                <button
                  key={`target-${user.id}`}
                  type="button"
                  onClick={() =>
                    setForm((current) => ({
                      ...current,
                      targetUserId: user.id,
                      sourceUserIds: current.sourceUserIds.filter((item) => item !== user.id),
                    }))
                  }
                  className={`w-full rounded-2xl border px-4 py-4 text-left transition ${
                    selected
                      ? 'border-blue-200 bg-blue-50 text-blue-900'
                      : 'border-slate-200 hover:border-slate-300 hover:bg-slate-50'
                  }`}
                >
                  <div className="font-semibold">{displayUserName(user)}</div>
                  <div className="mt-1 text-sm text-slate-600">{user.email || 'Без email'}</div>
                  <div className="mt-3 flex flex-wrap gap-2">
                    {(user.externalIdentities ?? []).map((identity) => (
                      <span
                        key={`${user.id}-${identity.id}`}
                        className="inline-flex rounded-full border border-slate-200 px-2.5 py-1 text-xs font-semibold text-slate-600"
                      >
                        {providerLabel(identity.provider)}
                      </span>
                    ))}
                  </div>
                </button>
              );
            })}
          </div>
        </div>

        <div className="rounded-3xl border border-slate-200 bg-white p-6 shadow-sm">
          <div className="flex items-center justify-between gap-3">
            <div className="text-sm font-semibold text-slate-900">Профили для объединения</div>
            <div className="text-xs text-slate-500">Выбрано: {sourceUsers.length}</div>
          </div>
          <input
            type="text"
            value={form.sourceSearch}
            onChange={(event) => setForm((current) => ({ ...current, sourceSearch: event.target.value }))}
            placeholder={targetUser ? 'Поиск дублей' : 'Сначала выберите основной профиль'}
            className="mt-3 w-full rounded-2xl border border-slate-300 px-3 py-3"
            disabled={!targetUser}
          />

          <div className="mt-4 max-h-[28rem] space-y-3 overflow-auto pr-1">
            {!targetUser ? (
              <div className="rounded-2xl border border-dashed border-slate-300 p-4 text-sm text-slate-500">
                Сначала выберите основной профиль, затем отметьте аккаунты-источники.
              </div>
            ) : null}

            {targetUser
              ? sourceCandidates.map((user) => {
                  const selected = form.sourceUserIds.includes(user.id);
                  return (
                    <button
                      key={`source-${user.id}`}
                      type="button"
                      onClick={() => toggleSourceUser(user.id)}
                      className={`w-full rounded-2xl border px-4 py-4 text-left transition ${
                        selected
                          ? 'border-emerald-200 bg-emerald-50 text-emerald-900'
                          : 'border-slate-200 hover:border-slate-300 hover:bg-slate-50'
                      }`}
                    >
                      <div className="flex items-start justify-between gap-3">
                        <div>
                          <div className="font-semibold">{displayUserName(user)}</div>
                          <div className="mt-1 text-sm text-slate-600">{user.email || 'Без email'}</div>
                        </div>
                        {selected ? <Check className="h-5 w-5 text-emerald-600" /> : null}
                      </div>

                      <div className="mt-3 flex flex-wrap gap-2">
                        {(user.externalIdentities ?? []).map((identity) => (
                          <span
                            key={`${user.id}-${identity.id}`}
                            className="inline-flex rounded-full border border-slate-200 px-2.5 py-1 text-xs font-semibold text-slate-600"
                          >
                            {providerLabel(identity.provider)}
                          </span>
                        ))}
                      </div>
                    </button>
                  );
                })
              : null}
          </div>
        </div>

        <div className="rounded-3xl border border-slate-200 bg-white p-6 shadow-sm">
          <div className="text-sm font-semibold text-slate-900">Итоговые контакты</div>
          <p className="mt-2 text-sm leading-6 text-slate-600">
            После объединения можно сразу выбрать, какой email и какой телефон станут основными в общем профиле.
          </p>

          <div className="mt-5 space-y-4">
            <label className="space-y-2">
              <span className="text-sm font-medium text-slate-700">Основной email</span>
              <SystemSelect
                value={form.primaryEmail}
                onChange={(event) => setForm((current) => ({ ...current, primaryEmail: event.target.value }))}
                className="w-full rounded-2xl border border-slate-300 px-3 py-3"
                disabled={emailOptions.length === 0}
              >
                {emailOptions.length === 0 ? (
                  <option value="">Нет доступных вариантов</option>
                ) : null}
                {emailOptions.map((option) => (
                  <option key={option} value={option}>
                    {option}
                  </option>
                ))}
              </SystemSelect>
            </label>

            <label className="space-y-2">
              <span className="text-sm font-medium text-slate-700">Основной телефон</span>
              <SystemSelect
                value={form.primaryPhoneNumber}
                onChange={(event) =>
                  setForm((current) => ({ ...current, primaryPhoneNumber: event.target.value }))
                }
                className="w-full rounded-2xl border border-slate-300 px-3 py-3"
                disabled={phoneOptions.length === 0}
              >
                {phoneOptions.length === 0 ? (
                  <option value="">Нет доступных вариантов</option>
                ) : null}
                {phoneOptions.map((option) => (
                  <option key={option} value={option}>
                    {option}
                  </option>
                ))}
              </SystemSelect>
            </label>
          </div>

          <button
            type="button"
            onClick={() => void handleMerge()}
            disabled={saving || !targetUser || sourceUsers.length === 0}
            className="mt-6 inline-flex w-full items-center justify-center gap-2 rounded-2xl bg-slate-900 px-4 py-3 text-sm font-semibold text-white transition hover:bg-slate-800 disabled:cursor-not-allowed disabled:opacity-60"
          >
            {saving ? <Loader2 className="h-4 w-4 animate-spin" /> : <GitMerge className="h-4 w-4" />}
            Объединить пользователей
          </button>
        </div>
      </div>
    </div>
  );
}
