import { render, screen, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { AdminWebPushGuide, UserPushHelp } from './NotificationHelp';

describe('NotificationHelp', () => {
  it('explains push behavior for a regular user', () => {
    render(<UserPushHelp />);

    expect(screen.getByLabelText('Подсказка по Push-уведомлениям')).toBeTruthy();
    expect(screen.getByText('Как работают Push-уведомления')).toBeTruthy();
    expect(screen.getByText(/Включайте Push на каждом устройстве/)).toBeTruthy();
    expect(screen.getByText(/браузер запретил уведомления/)).toBeTruthy();
  });

  it('shows an administrator checklist for web push diagnostics', () => {
    render(<AdminWebPushGuide />);

    expect(screen.getByLabelText('Инструкция администратора по Web Push')).toBeTruthy();
    expect(screen.getByText('Короткая проверка для администратора')).toBeTruthy();
    expect(screen.getByText(/public key, private key и subject/)).toBeTruthy();
    expect(screen.getByText(/WebSocket-настройки nginx для SignalR/)).toBeTruthy();
  });

  it('keeps the detailed employee guide collapsed and links only to personal CRM pages', () => {
    const { container } = render(<UserPushHelp />);
    const details = container.querySelector('details');

    expect(details?.open).toBe(false);
    expect(details?.querySelector('summary')?.textContent).toContain('MAX, типы уведомлений и тихие часы');
    const links = screen.getAllByRole('link', { hidden: true });
    expect(links.map((link) => link.getAttribute('href'))).toEqual(['/?ppTab=profile', '/notifications']);
    expect(container.querySelector('button, form, input')).toBeNull();
  });

  it('explains the existing personal MAX connection and recovery without promising delivery', () => {
    render(<UserPushHelp />);
    const help = within(screen.getByLabelText('Подключение личного MAX'));

    expect(help.getByText(/«Подключить MAX», затем «Открыть бота MAX» и «Начать»/)).toBeTruthy();
    expect(help.getByText(/«Обновить статус»: ожидается состояние «Подключён»/)).toBeTruthy();
    expect(help.getByText(/15 минут и предназначена только вам/)).toBeTruthy();
    expect(help.getByText(/«Создать новую ссылку»/)).toBeTruthy();
    expect(help.getByText(/«Отключить MAX» прекращает ваши уведомления/)).toBeTruthy();
    expect(help.getByText(/Открытая переписка с ботом сама по себе не подтверждает подключение/)).toBeTruthy();
  });

  it('distinguishes disabling an event from disabling a channel and registering a device', () => {
    render(<UserPushHelp />);
    const help = within(screen.getByLabelText('Типы и каналы уведомлений'));

    expect(help.getByText(/Отключённое событие не создаёт новых уведомлений/)).toBeTruthy();
    expect(help.getByText(/выключенный канал исключает только этот способ доставки/)).toBeTruthy();
    expect(help.getByText(/Личные настройки имеют приоритет над правилами роли/)).toBeTruthy();
    expect(help.getByText(/не подключает бота или устройство автоматически/)).toBeTruthy();
    expect(help.getByText(/«Включить на этом устройстве»/)).toBeTruthy();
  });

  it('separates personal quiet time from the organisation MAX schedule and limit', () => {
    render(<UserPushHelp />);
    const help = within(screen.getByLabelText('Режим тишины уведомлений'));

    expect(help.getByText(/события остаются в центре уведомлений/)).toBeTruthy();
    expect(help.getByText(/Push, Email, Telegram, SMS и MAX/)).toBeTruthy();
    expect(help.getByText(/«Срочные уведомления показывать сразу»/)).toBeTruthy();
    expect(help.getByText(/Для MAX администратор задаёт отдельные тихие часы, часовой пояс и дневной лимит/)).toBeTruthy();
    expect(help.getByText(/может ожидать следующего разрешённого времени/)).toBeTruthy();
  });

  it('explains that reading a notification does not approve a task or send a chat reply', () => {
    render(<UserPushHelp />);
    const help = within(screen.getByLabelText('Сообщения и согласования'));

    expect(help.getByText(/Прочтение уведомления не согласовывает задачу и не отправляет ответ в чат/)).toBeTruthy();
    expect(help.getByText(/Откройте доступную вам переписку/)).toBeTruthy();
    expect(help.getByText(/сообщение MAX само по себе не заменяет это действие/)).toBeTruthy();
  });

  it('limits common configuration instructions to authorised staff and preserves personal linking', () => {
    const { container } = render(<AdminWebPushGuide />);

    expect(container.querySelector('details')?.open).toBe(false);
    expect(screen.getByRole('link', { name: 'Открыть настройки уведомлений', hidden: true }).getAttribute('href'))
      .toBe('/?ppTab=settings&settingsSection=notifications');
    expect(screen.getByText(/сотруднику с разрешением на настройки уведомлений/)).toBeTruthy();
    expect(screen.getByText(/Для изменения требуется право редактирования/)).toBeTruthy();
    expect(screen.getByText(/общая настройка бота не создаёт личную привязку/)).toBeTruthy();
    expect(screen.getByText(/включение одного не включает остальные/)).toBeTruthy();
    expect(screen.getByText(/не подтверждают получение конкретного сообщения/)).toBeTruthy();
  });
});
