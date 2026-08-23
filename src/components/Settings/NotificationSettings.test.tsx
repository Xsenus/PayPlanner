import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { NotificationPurposeDto, NotificationSenderDto } from '../../services/api';
import {
  notificationPurposesForChannel,
  notificationDeliveryChannelLabel,
  DeliveryLogPanel,
  SenderTable,
  SMS_TEST_DELIVERY_GRID_CLASS,
} from './NotificationSettings';

const sender: NotificationSenderDto = {
  id: 17,
  channel: 'email',
  name: 'Основной SMTP-отправитель с длинным названием',
  isEnabled: true,
  ready: true,
  purposes: ['system'],
  sortOrder: 10,
  email: {
    smtpHost: 'smtp.example.test',
    smtpPort: 587,
    smtpUsername: 'notifications@example.test',
    fromEmail: 'notifications@example.test',
    fromName: 'ElitPravo CRM',
    enableSsl: true,
    smtpPasswordSet: true,
  },
  createdAt: '2026-08-04T00:00:00Z',
};

describe('SenderTable', () => {
  it('ограничивает широкую таблицу карточкой и оставляет горизонтальную прокрутку внутри неё', () => {
    render(
      <SenderTable
        channel="email"
        title="Email отправители"
        description="Отправители системных уведомлений"
        senders={[sender]}
        purposes={[{ key: 'system', label: 'Системные', description: 'Системные уведомления', channels: ['email'] }]}
        onCreate={vi.fn()}
        onOpen={vi.fn()}
        canEdit
      />,
    );

    expect(screen.getByTestId('notification-sender-table').className).toContain('min-w-0');
    expect(screen.getByTestId('notification-sender-table').className).toContain('max-w-full');
    expect(screen.getByTestId('notification-sender-table-scroll').className).toContain('max-w-full');
    expect(screen.getByTestId('notification-sender-table-scroll').className).toContain('overflow-x-auto');
    expect(screen.getByText('Основной SMTP-отправитель с длинным названием')).not.toBeNull();
  });
});

describe('адаптивная раскладка уведомлений', () => {
  it('не включает широкую форму тестовой SMS внутри узкой рабочей области настроек', () => {
    expect(SMS_TEST_DELIVERY_GRID_CLASS).toContain('min-w-0');
    expect(SMS_TEST_DELIVERY_GRID_CLASS).toContain('2xl:grid-cols-[minmax(0,0.8fr)_minmax(0,1.4fr)_auto]');
    expect(SMS_TEST_DELIVERY_GRID_CLASS).not.toContain('lg:grid-cols');
  });
});

describe('сценарии отправителей', () => {
  const purposes: NotificationPurposeDto[] = [
    { key: 'telegramAuth', label: 'Вход через Telegram', description: 'Авторизация', channels: ['telegram'] },
    { key: 'contractClient', label: 'Письма клиентам', description: 'Договоры', channels: ['email'] },
    { key: 'manualTest', label: 'Ручные сообщения', description: 'Тест', channels: ['email', 'telegram'] },
  ];

  it('показывает Email-отправителю только применимые сценарии', () => {
    expect(notificationPurposesForChannel(purposes, 'email').map((item) => item.key)).toEqual([
      'contractClient',
      'manualTest',
    ]);
  });

  it('не предлагает Telegram-боту сценарий договорных писем', () => {
    expect(notificationPurposesForChannel(purposes, 'telegram').map((item) => item.key)).toEqual([
      'telegramAuth',
      'manualTest',
    ]);
  });

  it('сохраняет совместимость со старым ответом API без списка каналов', () => {
    const legacyPurposes = purposes.map(({ channels, ...purpose }) => {
      void channels;
      return purpose;
    });

    expect(notificationPurposesForChannel(legacyPurposes, 'email').map((item) => item.key)).toEqual([
      'contractClient',
      'manualTest',
    ]);
    expect(notificationPurposesForChannel(legacyPurposes, 'telegram').map((item) => item.key)).toEqual([
      'telegramAuth',
      'manualTest',
    ]);
  });
});

describe('журнал доставки', () => {
  it('не подписывает неизвестный или Web Push канал как Email', () => {
    expect(notificationDeliveryChannelLabel('webPush')).toBe('Web Push');
    expect(notificationDeliveryChannelLabel('max')).toBe('MAX');
    expect(notificationDeliveryChannelLabel('custom-provider')).toBe('custom-provider');
  });

  it('позволяет перейти к следующей странице истории', () => {
    const onPageChange = vi.fn();

    render(
      <DeliveryLogPanel
        items={[]}
        total={45}
        status="failed"
        page={1}
        pageSize={20}
        loading={false}
        retryingId={null}
        canEdit
        onStatusChange={vi.fn()}
        onPageChange={onPageChange}
        onPageSizeChange={vi.fn()}
        onRefresh={vi.fn()}
        onRetry={vi.fn()}
      />,
    );

    expect(screen.getByText('1 / 3')).not.toBeNull();
    fireEvent.click(screen.getByTestId('pagination-next'));
    expect(onPageChange).toHaveBeenCalledWith(2);
  });

  it('не предлагает повторную отправку для Web Push, который сервер повторять не умеет', () => {
    const onRetry = vi.fn();

    render(
      <DeliveryLogPanel
        items={[{
          id: 7,
          eventKey: 'task:test',
          channel: 'webPush',
          senderId: null,
          senderName: 'Web Push',
          recipientUserId: 1,
          recipientAddress: 'Push-устройство • 12345678',
          subject: 'Проверка',
          status: 'failed',
          error: 'Push-подписка устарела.',
          providerKey: 'webpush',
          providerStatus: null,
          providerStatusCheckedAt: null,
          createdAt: '2026-08-16T00:00:00Z',
          sentAt: null,
        }]}
        total={1}
        status="failed"
        page={1}
        pageSize={20}
        loading={false}
        retryingId={null}
        canEdit
        onStatusChange={vi.fn()}
        onPageChange={vi.fn()}
        onPageSizeChange={vi.fn()}
        onRefresh={vi.fn()}
        onRetry={onRetry}
      />,
    );

    expect(screen.queryByRole('button', { name: 'Повторить' })).toBeNull();
    expect(onRetry).not.toHaveBeenCalled();
  });
});
