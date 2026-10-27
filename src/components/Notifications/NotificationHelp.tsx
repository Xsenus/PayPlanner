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
      <details className="mt-3 border-t border-blue-100 pt-2">
        <summary className="cursor-pointer font-semibold focus-visible:rounded focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2">
          MAX, типы уведомлений и тихие часы
        </summary>
        <div className="mt-2 space-y-3">
          <section aria-label="Личные настройки уведомлений">
            <h3 className="font-semibold">Где настроить</h3>
            <p>
              В меню пользователя откройте «Ваш профиль» → «Профиль» → «Уведомления в MAX».
              {' '}<a href="/?ppTab=profile" className="font-semibold underline underline-offset-2">Открыть свой профиль</a>.
              {' '}В разделе «Уведомления» нажмите «Типы», чтобы выбрать события, каналы и режим «Не беспокоить».
              {' '}<a href="/notifications" className="font-semibold underline underline-offset-2">Открыть уведомления</a>.
            </p>
          </section>
          <section aria-label="Подключение личного MAX">
            <h3 className="font-semibold">Подключение MAX</h3>
            <p>
              Нажмите «Подключить MAX», затем «Открыть бота MAX» и «Начать» в своём аккаунте MAX.
              Вернитесь в профиль и нажмите «Обновить статус»: ожидается состояние «Подключён».
              Ссылка действует 15 минут и предназначена только вам; после её истечения выберите «Создать новую ссылку».
              «Отменить привязку» отменяет ожидание, «Отключить MAX» прекращает ваши уведомления в MAX.
              Если канал недоступен, его настраивает администратор. Открытая переписка с ботом сама по себе не подтверждает подключение к CRM.
            </p>
          </section>
          <section aria-label="Типы и каналы уведомлений">
            <h3 className="font-semibold">Событие и способ доставки</h3>
            <p>
              В «Типах» сначала включите нужное событие, затем выберите доступные для него каналы и нажмите «Сохранить».
              Отключённое событие не создаёт новых уведомлений; выключенный канал исключает только этот способ доставки.
              Ранее полученные уведомления этого типа не удаляются, но скрыты в центре, пока тип выключен.
              Личные настройки имеют приоритет над правилами роли. Включение канала не подключает бота или устройство автоматически.
              Для Push нажмите «Включить на этом устройстве» и разрешите уведомления в браузере на каждом нужном устройстве.
            </p>
          </section>
          <section aria-label="Режим тишины уведомлений">
            <h3 className="font-semibold">Тихие часы</h3>
            <p>
              В «Уведомлениях» → «Типы» режим «Не беспокоить» задаёт рабочее время, выходные и отпуск;
              события остаются в центре уведомлений, а всплывающие сообщения, Push, Email, Telegram, SMS и MAX ограничиваются выбранным расписанием.
              При необходимости отдельно включите «Срочные уведомления показывать сразу» и сохраните настройки.
              Для MAX администратор задаёт отдельные тихие часы, часовой пояс и дневной лимит: сообщение может ожидать следующего разрешённого времени.
            </p>
          </section>
          <section aria-label="Сообщения и согласования">
            <h3 className="font-semibold">Сообщение и согласование</h3>
            <p>
              Уведомление о сообщении ведёт к переписке, уведомление о согласовании — к требующему решения действию в CRM.
              Прочтение уведомления не согласовывает задачу и не отправляет ответ в чат.
              Откройте доступную вам переписку для ответа или задачу для согласования; сообщение MAX само по себе не заменяет это действие.
            </p>
          </section>
        </div>
      </details>
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
      <details className="mt-3 border-t border-slate-200 pt-2 text-xs leading-5">
        <summary className="cursor-pointer font-semibold focus-visible:rounded focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2">
          MAX и правила для ролей
        </summary>
        <div className="mt-2 space-y-2">
          <p>
            Общие параметры доступны администратору или сотруднику с разрешением на настройки уведомлений:
            «Настройки» → «Интеграции и доступ» → «Уведомления».
            {' '}<a href="/?ppTab=settings&settingsSection=notifications" className="font-semibold underline underline-offset-2">Открыть настройки уведомлений</a>.
            Для изменения требуется право редактирования.
          </p>
          <p>
            В блоке «MAX messenger» проверьте готовность канала, включение нужного сценария, тихие часы, часовой пояс,
            дневной лимит и отсутствие аварийной остановки. Каждый сотрудник подключает свой MAX в «Вашем профиле»;
            общая настройка бота не создаёт личную привязку.
          </p>
          <p>
            «Ролевые правила уведомлений» задают исходные типы и каналы для выбранной роли; личные настройки сотрудника имеют приоритет.
            Уведомления, отправка документов и подтверждения — отдельные сценарии MAX: включение одного не включает остальные.
            Статус готовности и личная привязка не подтверждают получение конкретного сообщения: результат смотрите в «Последних доставках MAX».
          </p>
        </div>
      </details>
    </div>
  );
}
