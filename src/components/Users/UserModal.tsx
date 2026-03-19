import { useCallback, useEffect, useState, type FormEvent } from 'react';
import { X } from 'lucide-react';
import { authService, type Role, type UpdateUserDto, type User } from '../../services/authService';
import { useToast } from '../../hooks/useToast';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';

interface UserModalProps {
  user: User | null;
  onClose: () => void;
  onSaved?: () => void;
}

export const UserModal = ({ user, onClose, onSaved }: UserModalProps) => {
  const toast = useToast();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [fullName, setFullName] = useState('');
  const [firstName, setFirstName] = useState('');
  const [lastName, setLastName] = useState('');
  const [middleName, setMiddleName] = useState('');
  const [dateOfBirth, setDateOfBirth] = useState('');
  const [photoUrl, setPhotoUrl] = useState('');
  const [phoneNumber, setPhoneNumber] = useState('');
  const [whatsApp, setWhatsApp] = useState('');
  const [telegram, setTelegram] = useState('');
  const [instagram, setInstagram] = useState('');
  const [messenger, setMessenger] = useState('');
  const [viber, setViber] = useState('');
  const [isEmployee, setIsEmployee] = useState(false);
  const [employmentStartDate, setEmploymentStartDate] = useState('');
  const [employmentEndDate, setEmploymentEndDate] = useState('');
  const [roleId, setRoleId] = useState(0);
  const [isActive, setIsActive] = useState(true);
  const [roles, setRoles] = useState<Role[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');

  const fetchRoles = useCallback(async () => {
    try {
      const data = await authService.getRoles();
      setRoles(data);
      if (data.length > 0 && !user) {
        setRoleId(data.find((role) => role.name === 'user')?.id || data[0].id);
      }
    } catch (err) {
      const message = extractDisplayErrorMessage(err, 'Не удалось загрузить роли');
      setError(message);
      toast.error(message);
    }
  }, [toast, user]);

  useEffect(() => {
    void fetchRoles();
  }, [fetchRoles]);

  useEffect(() => {
    if (!user) {
      setEmail('');
      setPassword('');
      setFullName('');
      setFirstName('');
      setLastName('');
      setMiddleName('');
      setDateOfBirth('');
      setPhotoUrl('');
      setPhoneNumber('');
      setWhatsApp('');
      setTelegram('');
      setInstagram('');
      setMessenger('');
      setViber('');
      setIsEmployee(false);
      setEmploymentStartDate('');
      setEmploymentEndDate('');
      setIsActive(true);
      setError('');
      return;
    }

    setEmail(user.email);
    setPassword('');
    setFullName(user.fullName);
    setFirstName(user.firstName || '');
    setLastName(user.lastName || '');
    setMiddleName(user.middleName || '');
    setDateOfBirth(user.dateOfBirth ? user.dateOfBirth.split('T')[0] : '');
    setPhotoUrl(user.photoUrl || '');
    setPhoneNumber(user.phoneNumber || '');
    setWhatsApp(user.whatsApp || '');
    setTelegram(user.telegram || '');
    setInstagram(user.instagram || '');
    setMessenger(user.messenger || '');
    setViber(user.viber || '');
    setIsEmployee(Boolean(user.isEmployee));
    setEmploymentStartDate(user.employmentStartDate ? user.employmentStartDate.split('T')[0] : '');
    setEmploymentEndDate(user.employmentEndDate ? user.employmentEndDate.split('T')[0] : '');
    setRoleId(user.role.id);
    setIsActive(user.isActive);
    setError('');
  }, [user]);

  const handleSubmit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    setError('');
    setLoading(true);

    try {
      const normalizedFullName = fullName.trim();
      const updateData: UpdateUserDto = {
        fullName: normalizedFullName || undefined,
        firstName: firstName.trim() || undefined,
        lastName: lastName.trim() || undefined,
        middleName: middleName.trim() || undefined,
        dateOfBirth: dateOfBirth || undefined,
        photoUrl: photoUrl.trim() || undefined,
        phoneNumber: phoneNumber.trim() || undefined,
        whatsApp: whatsApp.trim() || undefined,
        telegram: telegram.trim() || undefined,
        instagram: instagram.trim() || undefined,
        messenger: messenger.trim() || undefined,
        viber: viber.trim() || undefined,
        isEmployee,
        employmentStartDate: employmentStartDate || undefined,
        employmentEndDate: employmentEndDate || undefined,
        roleId,
        isActive,
      };

      if (user) {
        await authService.updateUser(user.id, updateData);
        toast.success('Пользователь обновлен');
      } else {
        await authService.createUser({
          email: email.trim(),
          password,
          fullName: normalizedFullName,
          roleId,
          isActive,
        });
        toast.success('Пользователь создан');
      }

      onSaved?.();
      onClose();
    } catch (err) {
      const message = extractDisplayErrorMessage(err, 'Не удалось сохранить пользователя');
      setError(message);
      toast.error(message);
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center overflow-y-auto bg-black/50 p-4">
      <div className="my-8 w-full max-w-2xl rounded-xl bg-white shadow-xl">
        <div className="flex items-center justify-between border-b border-slate-200 p-6">
          <h2 className="text-xl font-bold text-slate-900">
            {user ? 'Редактировать пользователя' : 'Новый пользователь'}
          </h2>
          <button
            type="button"
            onClick={onClose}
            className="rounded-lg p-2 transition-colors hover:bg-slate-100"
          >
            <X className="h-5 w-5" />
          </button>
        </div>

        <form
          onSubmit={handleSubmit}
          className="max-h-[calc(100vh-12rem)] space-y-4 overflow-y-auto p-6"
        >
          {error ? (
            <div className="rounded-lg border border-red-200 bg-red-50 p-4 text-sm text-red-700">
              {error}
            </div>
          ) : null}

          <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
            <div className="md:col-span-2">
              <label className="mb-2 block text-sm font-medium text-slate-700">Полное имя</label>
              <input
                type="text"
                value={fullName}
                onChange={(event) => setFullName(event.target.value)}
                className="w-full rounded-lg border border-slate-200 bg-slate-50 px-4 py-2 focus:outline-none focus:ring-2 focus:ring-slate-900"
                required
              />
            </div>

            <div>
              <label className="mb-2 block text-sm font-medium text-slate-700">Имя</label>
              <input
                type="text"
                value={firstName}
                onChange={(event) => setFirstName(event.target.value)}
                className="w-full rounded-lg border border-slate-200 bg-slate-50 px-4 py-2 focus:outline-none focus:ring-2 focus:ring-slate-900"
              />
            </div>

            <div>
              <label className="mb-2 block text-sm font-medium text-slate-700">Фамилия</label>
              <input
                type="text"
                value={lastName}
                onChange={(event) => setLastName(event.target.value)}
                className="w-full rounded-lg border border-slate-200 bg-slate-50 px-4 py-2 focus:outline-none focus:ring-2 focus:ring-slate-900"
              />
            </div>

            <div>
              <label className="mb-2 block text-sm font-medium text-slate-700">Отчество</label>
              <input
                type="text"
                value={middleName}
                onChange={(event) => setMiddleName(event.target.value)}
                className="w-full rounded-lg border border-slate-200 bg-slate-50 px-4 py-2 focus:outline-none focus:ring-2 focus:ring-slate-900"
              />
            </div>

            <div>
              <label className="mb-2 block text-sm font-medium text-slate-700">Дата рождения</label>
              <input
                type="date"
                value={dateOfBirth}
                onChange={(event) => setDateOfBirth(event.target.value)}
                className="w-full rounded-lg border border-slate-200 bg-slate-50 px-4 py-2 focus:outline-none focus:ring-2 focus:ring-slate-900"
              />
            </div>
          </div>

          <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
            <div>
              <label className="mb-2 block text-sm font-medium text-slate-700">Email</label>
              <input
                type="email"
                value={email}
                onChange={(event) => setEmail(event.target.value)}
                className="w-full rounded-lg border border-slate-200 bg-slate-50 px-4 py-2 focus:outline-none focus:ring-2 focus:ring-slate-900"
                disabled={Boolean(user)}
                required
              />
            </div>

            <div>
              <label className="mb-2 block text-sm font-medium text-slate-700">Телефон</label>
              <input
                type="tel"
                value={phoneNumber}
                onChange={(event) => setPhoneNumber(event.target.value)}
                className="w-full rounded-lg border border-slate-200 bg-slate-50 px-4 py-2 focus:outline-none focus:ring-2 focus:ring-slate-900"
                placeholder="+7 (999) 123-45-67"
              />
            </div>
          </div>

          {!user ? (
            <div>
              <label className="mb-2 block text-sm font-medium text-slate-700">Пароль</label>
              <input
                type="password"
                value={password}
                onChange={(event) => setPassword(event.target.value)}
                className="w-full rounded-lg border border-slate-200 bg-slate-50 px-4 py-2 focus:outline-none focus:ring-2 focus:ring-slate-900"
                required
                minLength={6}
              />
              <p className="mt-1 text-xs text-slate-500">Минимум 6 символов</p>
            </div>
          ) : null}

          <div>
            <label className="mb-2 block text-sm font-medium text-slate-700">Фото (URL)</label>
            <input
              type="url"
              value={photoUrl}
              onChange={(event) => setPhotoUrl(event.target.value)}
              className="w-full rounded-lg border border-slate-200 bg-slate-50 px-4 py-2 focus:outline-none focus:ring-2 focus:ring-slate-900"
              placeholder="https://example.com/photo.jpg"
            />
          </div>

          <div>
            <h3 className="mb-3 text-sm font-semibold text-slate-900">Социальные сети</h3>
            <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
              <div>
                <label className="mb-2 block text-sm font-medium text-slate-700">WhatsApp</label>
                <input
                  type="text"
                  value={whatsApp}
                  onChange={(event) => setWhatsApp(event.target.value)}
                  className="w-full rounded-lg border border-slate-200 bg-slate-50 px-4 py-2 focus:outline-none focus:ring-2 focus:ring-slate-900"
                  placeholder="+7 (999) 123-45-67"
                />
              </div>

              <div>
                <label className="mb-2 block text-sm font-medium text-slate-700">Telegram</label>
                <input
                  type="text"
                  value={telegram}
                  onChange={(event) => setTelegram(event.target.value)}
                  className="w-full rounded-lg border border-slate-200 bg-slate-50 px-4 py-2 focus:outline-none focus:ring-2 focus:ring-slate-900"
                  placeholder="@username"
                />
              </div>

              <div>
                <label className="mb-2 block text-sm font-medium text-slate-700">Instagram</label>
                <input
                  type="text"
                  value={instagram}
                  onChange={(event) => setInstagram(event.target.value)}
                  className="w-full rounded-lg border border-slate-200 bg-slate-50 px-4 py-2 focus:outline-none focus:ring-2 focus:ring-slate-900"
                  placeholder="@username"
                />
              </div>

              <div>
                <label className="mb-2 block text-sm font-medium text-slate-700">Messenger</label>
                <input
                  type="text"
                  value={messenger}
                  onChange={(event) => setMessenger(event.target.value)}
                  className="w-full rounded-lg border border-slate-200 bg-slate-50 px-4 py-2 focus:outline-none focus:ring-2 focus:ring-slate-900"
                  placeholder="Имя или ID"
                />
              </div>

              <div>
                <label className="mb-2 block text-sm font-medium text-slate-700">Viber</label>
                <input
                  type="text"
                  value={viber}
                  onChange={(event) => setViber(event.target.value)}
                  className="w-full rounded-lg border border-slate-200 bg-slate-50 px-4 py-2 focus:outline-none focus:ring-2 focus:ring-slate-900"
                  placeholder="+7 (999) 123-45-67"
                />
              </div>
            </div>
          </div>

          <div className="border-t border-slate-200 pt-4">
            <div className="mb-4 flex items-center gap-3">
              <input
                type="checkbox"
                id="isEmployee"
                checked={isEmployee}
                onChange={(event) => setIsEmployee(event.target.checked)}
                className="h-4 w-4 rounded border-slate-300 text-slate-900 focus:ring-slate-900"
              />
              <label htmlFor="isEmployee" className="text-sm font-medium text-slate-700">
                Является сотрудником
              </label>
            </div>

            {isEmployee ? (
              <div className="ml-7 grid grid-cols-1 gap-4 md:grid-cols-2">
                <div>
                  <label className="mb-2 block text-sm font-medium text-slate-700">
                    Дата начала работы
                  </label>
                  <input
                    type="date"
                    value={employmentStartDate}
                    onChange={(event) => setEmploymentStartDate(event.target.value)}
                    className="w-full rounded-lg border border-slate-200 bg-slate-50 px-4 py-2 focus:outline-none focus:ring-2 focus:ring-slate-900"
                  />
                </div>

                <div>
                  <label className="mb-2 block text-sm font-medium text-slate-700">
                    Дата увольнения
                  </label>
                  <input
                    type="date"
                    value={employmentEndDate}
                    onChange={(event) => setEmploymentEndDate(event.target.value)}
                    className="w-full rounded-lg border border-slate-200 bg-slate-50 px-4 py-2 focus:outline-none focus:ring-2 focus:ring-slate-900"
                  />
                </div>
              </div>
            ) : null}
          </div>

          <div>
            <label className="mb-2 block text-sm font-medium text-slate-700">Роль</label>
            <select
              value={roleId}
              onChange={(event) => setRoleId(Number(event.target.value))}
              className="w-full rounded-lg border border-slate-200 bg-slate-50 px-4 py-2 focus:outline-none focus:ring-2 focus:ring-slate-900"
              required
            >
              {roles.map((role) => (
                <option key={role.id} value={role.id}>
                  {role.name.charAt(0).toUpperCase() + role.name.slice(1)}
                  {role.description ? ` - ${role.description}` : ''}
                </option>
              ))}
            </select>
          </div>

          <div className="flex items-center gap-3">
            <input
              type="checkbox"
              id="isActive"
              checked={isActive}
              onChange={(event) => setIsActive(event.target.checked)}
              className="h-4 w-4 rounded border-slate-300 text-slate-900 focus:ring-slate-900"
            />
            <label htmlFor="isActive" className="text-sm font-medium text-slate-700">
              Активный пользователь
            </label>
          </div>

          <div className="flex gap-3 pt-4">
            <button
              type="button"
              onClick={onClose}
              className="flex-1 rounded-lg border border-slate-300 px-4 py-2 text-slate-700 transition-colors hover:bg-slate-50"
            >
              Отмена
            </button>
            <button
              type="submit"
              disabled={loading}
              className="flex-1 rounded-lg bg-slate-900 px-4 py-2 text-white transition-colors hover:bg-slate-800 disabled:opacity-50"
            >
              {loading ? 'Сохраняем...' : user ? 'Обновить' : 'Создать'}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
};
