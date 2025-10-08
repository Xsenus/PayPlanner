import { useState, useEffect } from 'react';
import { authService, User, Role } from '../../services/authService';
import { X } from 'lucide-react';

interface UserModalProps {
  user: User | null;
  onClose: () => void;
}

export const UserModal = ({ user, onClose }: UserModalProps) => {
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

  useEffect(() => {
    fetchRoles();
    if (user) {
      setEmail(user.email);
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
      setIsEmployee(user.isEmployee || false);
      setEmploymentStartDate(user.employmentStartDate ? user.employmentStartDate.split('T')[0] : '');
      setEmploymentEndDate(user.employmentEndDate ? user.employmentEndDate.split('T')[0] : '');
      setRoleId(user.role.id);
      setIsActive(user.isActive);
    }
  }, [user]);

  const fetchRoles = async () => {
    try {
      const data = await authService.getRoles();
      setRoles(data);
      if (data.length > 0 && !user) {
        setRoleId(data.find((r) => r.name === 'user')?.id || data[0].id);
      }
    } catch (err) {
      console.error('Ошибка загрузки ролей:', err);
    }
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError('');
    setLoading(true);

    try {
      const updateData: any = {
        fullName: fullName.trim() || undefined,
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
        isActive
      };

      if (user) {
        await authService.updateUser(user.id, updateData);
      } else {
        await authService.createUser({ ...updateData, email, password });
      }
      onClose();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Произошла ошибка');
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center p-4 z-50 overflow-y-auto">
      <div className="bg-white rounded-xl shadow-xl max-w-2xl w-full my-8">
        <div className="flex items-center justify-between p-6 border-b border-slate-200">
          <h2 className="text-xl font-bold text-slate-900">
            {user ? 'Редактировать пользователя' : 'Новый пользователь'}
          </h2>
          <button onClick={onClose} className="p-2 hover:bg-slate-100 rounded-lg transition-colors">
            <X className="w-5 h-5" />
          </button>
        </div>

        <form onSubmit={handleSubmit} className="p-6 space-y-4 max-h-[calc(100vh-12rem)] overflow-y-auto">
          {error && (
            <div className="p-4 bg-red-50 border border-red-200 rounded-lg text-red-700 text-sm">
              {error}
            </div>
          )}

          <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
            <div className="md:col-span-2">
              <label className="block text-sm font-medium text-slate-700 mb-2">Полное имя</label>
              <input
                type="text"
                value={fullName}
                onChange={(e) => setFullName(e.target.value)}
                className="w-full px-4 py-2 bg-slate-50 border border-slate-200 rounded-lg focus:outline-none focus:ring-2 focus:ring-slate-900"
                required
              />
            </div>

            <div>
              <label className="block text-sm font-medium text-slate-700 mb-2">Имя</label>
              <input
                type="text"
                value={firstName}
                onChange={(e) => setFirstName(e.target.value)}
                className="w-full px-4 py-2 bg-slate-50 border border-slate-200 rounded-lg focus:outline-none focus:ring-2 focus:ring-slate-900"
              />
            </div>

            <div>
              <label className="block text-sm font-medium text-slate-700 mb-2">Фамилия</label>
              <input
                type="text"
                value={lastName}
                onChange={(e) => setLastName(e.target.value)}
                className="w-full px-4 py-2 bg-slate-50 border border-slate-200 rounded-lg focus:outline-none focus:ring-2 focus:ring-slate-900"
              />
            </div>

            <div>
              <label className="block text-sm font-medium text-slate-700 mb-2">Отчество</label>
              <input
                type="text"
                value={middleName}
                onChange={(e) => setMiddleName(e.target.value)}
                className="w-full px-4 py-2 bg-slate-50 border border-slate-200 rounded-lg focus:outline-none focus:ring-2 focus:ring-slate-900"
              />
            </div>

            <div>
              <label className="block text-sm font-medium text-slate-700 mb-2">Дата рождения</label>
              <input
                type="date"
                value={dateOfBirth}
                onChange={(e) => setDateOfBirth(e.target.value)}
                className="w-full px-4 py-2 bg-slate-50 border border-slate-200 rounded-lg focus:outline-none focus:ring-2 focus:ring-slate-900"
              />
            </div>
          </div>

          <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
            <div>
              <label className="block text-sm font-medium text-slate-700 mb-2">Email</label>
              <input
                type="email"
                value={email}
                onChange={(e) => setEmail(e.target.value)}
                className="w-full px-4 py-2 bg-slate-50 border border-slate-200 rounded-lg focus:outline-none focus:ring-2 focus:ring-slate-900"
                disabled={!!user}
                required
              />
            </div>

            <div>
              <label className="block text-sm font-medium text-slate-700 mb-2">Телефон</label>
              <input
                type="tel"
                value={phoneNumber}
                onChange={(e) => setPhoneNumber(e.target.value)}
                className="w-full px-4 py-2 bg-slate-50 border border-slate-200 rounded-lg focus:outline-none focus:ring-2 focus:ring-slate-900"
                placeholder="+7 (999) 123-45-67"
              />
            </div>
          </div>

          {!user && (
            <div>
              <label className="block text-sm font-medium text-slate-700 mb-2">Пароль</label>
              <input
                type="password"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                className="w-full px-4 py-2 bg-slate-50 border border-slate-200 rounded-lg focus:outline-none focus:ring-2 focus:ring-slate-900"
                required
                minLength={6}
              />
              <p className="text-xs text-slate-500 mt-1">Минимум 6 символов</p>
            </div>
          )}

          <div>
            <label className="block text-sm font-medium text-slate-700 mb-2">Фото (URL)</label>
            <input
              type="url"
              value={photoUrl}
              onChange={(e) => setPhotoUrl(e.target.value)}
              className="w-full px-4 py-2 bg-slate-50 border border-slate-200 rounded-lg focus:outline-none focus:ring-2 focus:ring-slate-900"
              placeholder="https://example.com/photo.jpg"
            />
          </div>

          <div>
            <h3 className="text-sm font-semibold text-slate-900 mb-3">Социальные сети</h3>
            <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
              <div>
                <label className="block text-sm font-medium text-slate-700 mb-2">WhatsApp</label>
                <input
                  type="text"
                  value={whatsApp}
                  onChange={(e) => setWhatsApp(e.target.value)}
                  className="w-full px-4 py-2 bg-slate-50 border border-slate-200 rounded-lg focus:outline-none focus:ring-2 focus:ring-slate-900"
                  placeholder="+7 (999) 123-45-67"
                />
              </div>

              <div>
                <label className="block text-sm font-medium text-slate-700 mb-2">Telegram</label>
                <input
                  type="text"
                  value={telegram}
                  onChange={(e) => setTelegram(e.target.value)}
                  className="w-full px-4 py-2 bg-slate-50 border border-slate-200 rounded-lg focus:outline-none focus:ring-2 focus:ring-slate-900"
                  placeholder="@username"
                />
              </div>

              <div>
                <label className="block text-sm font-medium text-slate-700 mb-2">Instagram</label>
                <input
                  type="text"
                  value={instagram}
                  onChange={(e) => setInstagram(e.target.value)}
                  className="w-full px-4 py-2 bg-slate-50 border border-slate-200 rounded-lg focus:outline-none focus:ring-2 focus:ring-slate-900"
                  placeholder="@username"
                />
              </div>

              <div>
                <label className="block text-sm font-medium text-slate-700 mb-2">Messenger</label>
                <input
                  type="text"
                  value={messenger}
                  onChange={(e) => setMessenger(e.target.value)}
                  className="w-full px-4 py-2 bg-slate-50 border border-slate-200 rounded-lg focus:outline-none focus:ring-2 focus:ring-slate-900"
                  placeholder="Имя или ID"
                />
              </div>

              <div>
                <label className="block text-sm font-medium text-slate-700 mb-2">Viber</label>
                <input
                  type="text"
                  value={viber}
                  onChange={(e) => setViber(e.target.value)}
                  className="w-full px-4 py-2 bg-slate-50 border border-slate-200 rounded-lg focus:outline-none focus:ring-2 focus:ring-slate-900"
                  placeholder="+7 (999) 123-45-67"
                />
              </div>
            </div>
          </div>

          <div className="border-t border-slate-200 pt-4">
            <div className="flex items-center gap-3 mb-4">
              <input
                type="checkbox"
                id="isEmployee"
                checked={isEmployee}
                onChange={(e) => setIsEmployee(e.target.checked)}
                className="w-4 h-4 text-slate-900 border-slate-300 rounded focus:ring-slate-900"
              />
              <label htmlFor="isEmployee" className="text-sm font-medium text-slate-700">
                Является сотрудником
              </label>
            </div>

            {isEmployee && (
              <div className="grid grid-cols-1 md:grid-cols-2 gap-4 ml-7">
                <div>
                  <label className="block text-sm font-medium text-slate-700 mb-2">
                    Дата начала работы
                  </label>
                  <input
                    type="date"
                    value={employmentStartDate}
                    onChange={(e) => setEmploymentStartDate(e.target.value)}
                    className="w-full px-4 py-2 bg-slate-50 border border-slate-200 rounded-lg focus:outline-none focus:ring-2 focus:ring-slate-900"
                  />
                </div>

                <div>
                  <label className="block text-sm font-medium text-slate-700 mb-2">
                    Дата увольнения
                  </label>
                  <input
                    type="date"
                    value={employmentEndDate}
                    onChange={(e) => setEmploymentEndDate(e.target.value)}
                    className="w-full px-4 py-2 bg-slate-50 border border-slate-200 rounded-lg focus:outline-none focus:ring-2 focus:ring-slate-900"
                  />
                </div>
              </div>
            )}
          </div>

          <div>
            <label className="block text-sm font-medium text-slate-700 mb-2">Роль</label>
            <select
              value={roleId}
              onChange={(e) => setRoleId(Number(e.target.value))}
              className="w-full px-4 py-2 bg-slate-50 border border-slate-200 rounded-lg focus:outline-none focus:ring-2 focus:ring-slate-900"
              required>
              {roles.map((role) => (
                <option key={role.id} value={role.id}>
                  {role.name.charAt(0).toUpperCase() + role.name.slice(1)} — {role.description}
                </option>
              ))}
            </select>
          </div>

          <div className="flex items-center gap-3">
            <input
              type="checkbox"
              id="isActive"
              checked={isActive}
              onChange={(e) => setIsActive(e.target.checked)}
              className="w-4 h-4 text-slate-900 border-slate-300 rounded focus:ring-slate-900"
            />
            <label htmlFor="isActive" className="text-sm font-medium text-slate-700">
              Активный пользователь
            </label>
          </div>

          <div className="flex gap-3 pt-4">
            <button
              type="button"
              onClick={onClose}
              className="flex-1 px-4 py-2 border border-slate-300 text-slate-700 rounded-lg hover:bg-slate-50 transition-colors">
              Отмена
            </button>
            <button
              type="submit"
              disabled={loading}
              className="flex-1 px-4 py-2 bg-slate-900 text-white rounded-lg hover:bg-slate-800 transition-colors disabled:opacity-50">
              {loading ? 'Сохраняем…' : user ? 'Обновить' : 'Создать'}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
};
