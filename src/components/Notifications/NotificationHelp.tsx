import { AlertCircle, CheckCircle2, Info, Settings } from 'lucide-react';

const USER_PUSH_HELP_ITEMS = [
  'Включайте Push на каждом устройстве, где нужно получать срочные уведомления при закрытой вкладке CRM.',
  'Отключить устройство можно здесь же: это не выключит центр уведомлений и всплывающие сообщения в открытой CRM.',
  'Push может не прийти, если браузер запретил уведомления, устройство без интернета или сервер Web Push еще не настроен администратором.',
];

const ADMIN_WEB_PUSH_GUIDE_ITEMS = [
  'Проверьте, что Web Push включен и все три серверных параметра заполнены: public key, private key и subject.',
  'Убедитесь, что CRM открывается по HTTPS, а service worker доступен на том же домене, где сотрудники включают Push.',
  'Смотрите количество активных устройств и последнюю ошибку доставки: если устройств нет, сотрудникам нужно включить Push в профиле или центре уведомлений.',
  'Если моментальные уведомления в открытой CRM нестабильны, отдельно проверьте WebSocket-настройки nginx для SignalR.',
];

export function UserPushHelp() {
  return (
    <div
      aria-label="Подсказка по Push-уведомлениям"
      className="mt-3 rounded-lg border border-blue-100 bg-blue-50/70 p-3 text-xs leading-5 text-blue-900"
    >
      <div className="flex items-center gap-2 font-semibold">
        <Info className="h-4 w-4 text-blue-600" />
        Как работают Push-уведомления
      </div>
      <ul className="mt-2 space-y-1">
        {USER_PUSH_HELP_ITEMS.map((item) => (
          <li key={item} className="flex gap-2">
            <CheckCircle2 className="mt-0.5 h-3.5 w-3.5 shrink-0 text-blue-600" />
            <span>{item}</span>
          </li>
        ))}
      </ul>
    </div>
  );
}

export function AdminWebPushGuide() {
  return (
    <div
      aria-label="Инструкция администратора по Web Push"
      className="mt-3 rounded-xl border border-slate-200 bg-slate-50 p-3 text-sm text-slate-700"
    >
      <div className="flex flex-wrap items-center gap-2 font-semibold text-slate-900">
        <Settings className="h-4 w-4 text-slate-600" />
        Короткая проверка для администратора
      </div>
      <div className="mt-2 grid gap-2 md:grid-cols-2">
        {ADMIN_WEB_PUSH_GUIDE_ITEMS.map((item, index) => (
          <div key={item} className="flex gap-2 rounded-lg bg-white p-2 text-xs leading-5 text-slate-600">
            {index === ADMIN_WEB_PUSH_GUIDE_ITEMS.length - 1 ? (
              <AlertCircle className="mt-0.5 h-3.5 w-3.5 shrink-0 text-amber-600" />
            ) : (
              <CheckCircle2 className="mt-0.5 h-3.5 w-3.5 shrink-0 text-emerald-600" />
            )}
            <span>{item}</span>
          </div>
        ))}
      </div>
    </div>
  );
}
