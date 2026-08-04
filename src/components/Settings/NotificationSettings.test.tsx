import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { NotificationSenderDto } from '../../services/api';
import { SenderTable } from './NotificationSettings';

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
        purposes={[{ key: 'system', label: 'Системные', description: 'Системные уведомления' }]}
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
