import { useCallback, useEffect, useRef, useState } from 'react';
import { Plus, RefreshCw, Save, X } from 'lucide-react';
import { useAuth } from '../../contexts/AuthContext';
import { useDocumentDeliveryOwner } from '../../hooks/useDocumentDeliveryOwner';
import { useDebouncedValue } from '../../hooks/useDebouncedValue';
import { useToast } from '../../hooks/useToast';
import {
  apiService,
  type ContractExpirationRecipientOptionDto,
  type ContractExpirationRecipientOptionKind,
  type ContractExpirationRecipientSettingsDto,
} from '../../services/api';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { SystemCheckbox } from '../ui/SystemCheckbox';
import { TablePagination } from '../ui/TablePagination';

type Option = ContractExpirationRecipientOptionDto;
type Picker = { kind: ContractExpirationRecipientOptionKind; department?: Option };
const buttonClass = 'inline-flex items-center justify-center gap-2 rounded-lg border border-slate-300 px-3 py-2 text-sm disabled:cursor-not-allowed disabled:opacity-50';
const labelFor = (options: Option[], id: number, label: string) => options.find((item) => item.id === id)
  ?? { id, name: `${label} №${id}`, isAvailable: false };
const remember = (options: Option[], option: Option) => [...options.filter((item) => item.id !== option.id), option];

export function ContractExpirationRecipientSettings({ canEdit }: { canEdit: boolean }) {
  const { user } = useAuth();
  const scope = JSON.stringify([user?.id, user?.role?.id, user?.departmentId, user?.department, canEdit]);
  return <OwnedRecipientSettings key={scope} canEdit={canEdit} />;
}

function OwnedRecipientSettings({ canEdit }: { canEdit: boolean }) {
  const toast = useToast();
  const { capture, begin, busy } = useDocumentDeliveryOwner('contract-expiration-recipients');
  const [draft, setDraft] = useState<ContractExpirationRecipientSettingsDto | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [picker, setPicker] = useState<Picker | null>(null);
  const loadSequence = useRef(0);
  const pickerTrigger = useRef<HTMLButtonElement | null>(null);
  const openPicker = (next: Picker, trigger: HTMLButtonElement) => {
    pickerTrigger.current = trigger;
    setPicker(next);
  };
  const closePicker = () => {
    setPicker(null);
    pickerTrigger.current?.focus();
  };

  const load = useCallback(async () => {
    const owner = capture();
    if (!owner) return;
    const sequence = ++loadSequence.current;
    const current = () => owner.isCurrent() && sequence === loadSequence.current;
    setLoading(true);
    setError(null);
    try {
      const response = await apiService.getContractExpirationRecipientSettings();
      if (current()) setDraft(response);
    } catch (loadError) {
      if (current()) setError(extractDisplayErrorMessage(loadError, 'Не удалось загрузить получателей напоминаний.'));
    } finally {
      if (current()) setLoading(false);
    }
  }, [capture]);

  useEffect(() => { void load(); }, [load]);

  const save = async () => {
    if (!canEdit || !draft || loading || picker) return;
    const operation = begin('save');
    if (!operation) return;
    setError(null);
    try {
      const response = await apiService.updateContractExpirationRecipientSettings({
        departmentManagersEnabled: draft.departmentManagersEnabled,
        roleIds: draft.roleIds,
        userIds: draft.userIds,
        departmentManagers: draft.departmentManagers,
      });
      if (!operation.isCurrent()) return;
      setDraft(response);
      toast.success('Получатели напоминаний сохранены.', 'Договоры');
    } catch (saveError) {
      if (!operation.isCurrent()) return;
      const message = extractDisplayErrorMessage(saveError, 'Не удалось сохранить получателей напоминаний.');
      setError(message);
      toast.error(message, 'Договоры');
    } finally {
      operation.finish();
    }
  };

  const select = (option: Option) => {
    if (!canEdit || busy || !picker || !option.isAvailable) return;
    if (picker.kind === 'departments') {
      setPicker({ kind: 'users', department: option });
      return;
    }
    const target = picker;
    setDraft((current) => {
      if (!current) return current;
      if (target.kind === 'roles') return {
        ...current,
        roleIds: [...new Set([...current.roleIds, option.id])],
        selectedRoles: remember(current.selectedRoles, option),
      };
      const department = target.department;
      if (department) return {
        ...current,
        departmentManagers: [
          ...current.departmentManagers.filter((item) => item.departmentId !== department.id),
          { departmentId: department.id, managerUserId: option.id },
        ],
        selectedDepartments: remember(current.selectedDepartments, department),
        selectedUsers: remember(current.selectedUsers, option),
      };
      return {
        ...current,
        userIds: [...new Set([...current.userIds, option.id])],
        selectedUsers: remember(current.selectedUsers, option),
      };
    });
    closePicker();
  };

  const disabled = !canEdit || loading || busy;
  return (
    <section aria-labelledby="contract-expiration-recipients-title" className="min-w-0 space-y-3 rounded-xl border border-slate-200 bg-white p-3">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0">
          <h4 id="contract-expiration-recipients-title" className="text-base font-semibold text-slate-900">Напоминания об окончании договоров</h4>
          <p className="mt-1 text-sm text-slate-600">За 30, 14 и 7 дней. Ответственные получают напоминания всегда. Дополнительные получатели объединяются без повторов.</p>
        </div>
        <button type="button" className={buttonClass} disabled={loading || busy} onClick={() => { setPicker(null); void load(); }}>
          <RefreshCw className="h-4 w-4" /> Обновить получателей
        </button>
      </div>
      {loading && <p role="status" className="text-sm text-slate-500">Загружаем получателей…</p>}
      {error && <p role="alert" className="text-sm text-red-700">{error}</p>}
      {draft && (
        <>
          <label className="flex items-start gap-2 text-sm text-slate-800">
            <SystemCheckbox checked={draft.departmentManagersEnabled} disabled={disabled}
              onChange={(event) => setDraft({ ...draft, departmentManagersEnabled: event.target.checked })} />
            Руководители подразделений ответственных
          </label>
          <div className="space-y-2">
            <h5 className="text-sm font-medium text-slate-900">Руководители для этих напоминаний</h5>
            {draft.departmentManagers.map((item) => {
              const department = labelFor(draft.selectedDepartments, item.departmentId, 'Подразделение');
              const manager = labelFor(draft.selectedUsers, item.managerUserId, 'Сотрудник');
              return (
                <div key={item.departmentId} className="flex min-w-0 flex-wrap items-center gap-2 rounded-lg bg-slate-50 p-2 text-sm">
                  <span className="w-full min-w-0 break-words sm:w-auto sm:flex-1">{department.name} — {manager.name}{!department.isAvailable || !manager.isAvailable ? ' · Недоступен' : ''}</span>
                  <button type="button" className={buttonClass} disabled={disabled || !department.isAvailable}
                    onClick={(event) => openPicker({ kind: 'users', department }, event.currentTarget)} aria-label={`Изменить руководителя ${department.name}`}>Изменить</button>
                  <button type="button" className={buttonClass} disabled={disabled} aria-label={`Удалить руководителя ${department.name}`}
                    onClick={() => setDraft({ ...draft, departmentManagers: draft.departmentManagers.filter((mapping) => mapping.departmentId !== item.departmentId) })}><X className="h-4 w-4" /></button>
                </div>
              );
            })}
            <button type="button" className={buttonClass} disabled={disabled || draft.departmentManagers.length >= 500}
              onClick={(event) => openPicker({ kind: 'departments' }, event.currentTarget)}><Plus className="h-4 w-4" /> Назначить руководителя подразделения</button>
          </div>
          <div className="grid min-w-0 gap-3 md:grid-cols-2">
            <div className="min-w-0 space-y-2">
              <h5 className="text-sm font-medium text-slate-900">Роли получателей</h5>
              {draft.roleIds.map((id) => {
                const option = labelFor(draft.selectedRoles, id, 'Роль');
                return <SelectedRecipient key={id} option={option} disabled={disabled} removeLabel={`Удалить роль ${option.name}`}
                  onRemove={() => setDraft({ ...draft, roleIds: draft.roleIds.filter((value) => value !== id) })} />;
              })}
              <button type="button" className={buttonClass} disabled={disabled || draft.roleIds.length >= 500}
                onClick={(event) => openPicker({ kind: 'roles' }, event.currentTarget)}><Plus className="h-4 w-4" /> Добавить роль</button>
            </div>
            <div className="min-w-0 space-y-2">
              <h5 className="text-sm font-medium text-slate-900">Сотрудники получатели</h5>
              {draft.userIds.map((id) => {
                const option = labelFor(draft.selectedUsers, id, 'Сотрудник');
                return <SelectedRecipient key={id} option={option} disabled={disabled} removeLabel={`Удалить сотрудника ${option.name}`}
                  onRemove={() => setDraft({ ...draft, userIds: draft.userIds.filter((value) => value !== id) })} />;
              })}
              <button type="button" className={buttonClass} disabled={disabled || draft.userIds.length >= 500}
                onClick={(event) => openPicker({ kind: 'users' }, event.currentTarget)}><Plus className="h-4 w-4" /> Добавить сотрудника</button>
            </div>
          </div>
          {picker && canEdit && !busy && <RecipientDirectoryPicker key={JSON.stringify(picker)} picker={picker} onSelect={select} onClose={closePicker} />}
          <p className="text-xs text-slate-500">Доставка учитывает доступ сотрудника к договору и его настройки уведомлений.</p>
          {canEdit && <button type="button" className={`${buttonClass} bg-blue-600 text-white`} disabled={loading || busy || picker !== null} onClick={() => { void save(); }}>
            <Save className="h-4 w-4" /> {busy ? 'Сохраняем…' : 'Сохранить получателей'}
          </button>}
        </>
      )}
    </section>
  );
}

function SelectedRecipient({ option, disabled, removeLabel, onRemove }: { option: Option; disabled: boolean; removeLabel: string; onRemove: () => void }) {
  return <div className="flex min-w-0 items-center gap-2 rounded-lg bg-slate-50 p-2 text-sm">
    <span className="min-w-0 flex-1 break-words">{option.name}{option.isAvailable ? '' : ' · Недоступен'}</span>
    <button type="button" className={buttonClass} disabled={disabled} aria-label={removeLabel} onClick={onRemove}><X className="h-4 w-4" /></button>
  </div>;
}

function RecipientDirectoryPicker({ picker, onSelect, onClose }: { picker: Picker; onSelect: (option: Option) => void; onClose: () => void }) {
  const [search, setSearch] = useState('');
  const query = useDebouncedValue(search, 250).trim();
  const [position, setPosition] = useState({ query: '', page: 1 });
  const page = position.query === query ? position.page : 1;
  const [revision, setRevision] = useState(0);
  const [items, setItems] = useState<Option[]>([]);
  const [total, setTotal] = useState(0);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const { capture } = useDocumentDeliveryOwner(JSON.stringify(['contract-expiration-options', picker.kind, query, page]));
  const typing = search.trim() !== query;
  const title = picker.department ? `Руководитель ${picker.department.name}`
    : picker.kind === 'roles' ? 'Выбор роли' : picker.kind === 'departments' ? 'Выбор подразделения' : 'Выбор сотрудника';

  useEffect(() => {
    const owner = capture();
    if (!owner) return;
    const abort = new AbortController();
    let active = true;
    const current = () => active && owner.isCurrent();
    setLoading(true);
    setError(null);
    void apiService.getContractExpirationRecipientOptions({ kind: picker.kind, search: query, page, pageSize: 50 }, { signal: abort.signal })
      .then((response) => { if (current()) { setItems(response.items); setTotal(response.totalCount); } })
      .catch((loadError: unknown) => { if (current()) { setItems([]); setTotal(0); setError(extractDisplayErrorMessage(loadError, 'Не удалось загрузить список получателей.')); } })
      .finally(() => { if (current()) setLoading(false); });
    return () => { active = false; abort.abort(); };
  }, [capture, page, picker.kind, query, revision]);

  return (
    <div role="region" aria-label={title} className="min-w-0 space-y-2 rounded-lg border border-blue-200 p-3"
      onKeyDown={(event) => { if (event.key === 'Escape') onClose(); }}>
      <div className="flex items-start justify-between gap-2">
        <h5 className="break-words text-sm font-semibold">{title}</h5>
        <button type="button" className={buttonClass} aria-label="Закрыть выбор получателя" onClick={onClose}><X className="h-4 w-4" /></button>
      </div>
      <label className="block text-sm">Поиск
        <input autoFocus value={search} onChange={(event) => setSearch(event.target.value)} type="search" maxLength={200}
          className="mt-1 w-full rounded-lg border border-slate-300 px-3 py-2" />
      </label>
      {(loading || typing) && <p role="status" className="text-sm text-slate-500">Загружаем список…</p>}
      {error && <div className="space-y-2"><p role="alert" className="text-sm text-red-700">{error}</p>
        <button type="button" className={buttonClass} onClick={() => setRevision((value) => value + 1)}>Повторить загрузку списка</button></div>}
      {!loading && !typing && !error && <div className="max-h-64 space-y-1 overflow-y-auto">
        {items.length === 0 && <p className="text-sm text-slate-500">Ничего не найдено.</p>}
        {items.map((item) => <button key={item.id} type="button" disabled={!item.isAvailable}
          className="block w-full rounded-lg p-2 text-left text-sm hover:bg-blue-50 disabled:opacity-50" onClick={() => onSelect(item)}>
          <span className="block break-words font-medium">{item.name}</span>
          {(item.roleName || item.departmentName) && <span className="block break-words text-xs text-slate-500">{[item.roleName, item.departmentName].filter(Boolean).join(' · ')}</span>}
        </button>)}
      </div>}
      {!loading && !typing && !error && <TablePagination page={page} totalPages={Math.max(1, Math.ceil(total / 50))} totalItems={total}
        pageSize={50} pageSizeOptions={[50]} onPageChange={(next) => setPosition({ query, page: next })} onPageSizeChange={() => undefined}
        previousLabel="Предыдущие получатели" nextLabel="Следующие получатели" className="!gap-2 !px-2 !py-2" />}
    </div>
  );
}
