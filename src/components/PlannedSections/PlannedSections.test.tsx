import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { PayrollSection, WorkspaceSection } from './PlannedSections';

describe('PlannedSections', () => {
  it('shows the planned workspace structure without presenting unfinished actions as available', () => {
    render(<WorkspaceSection />);

    expect(screen.getByRole('heading', { name: 'Рабочий стол' })).toBeTruthy();
    expect(screen.getByText('Сводка дня')).toBeTruthy();
    expect(screen.getByText('Быстрые действия')).toBeTruthy();
    expect(screen.queryByRole('button')).toBeNull();
  });

  it('shows payroll scope and clearly marks calculations as unavailable', () => {
    render(<PayrollSection />);

    expect(screen.getByRole('heading', { name: 'Зарплата' })).toBeTruthy();
    expect(screen.getByText('Внештатные сотрудники')).toBeTruthy();
    expect(screen.getByText('Зарплатные ведомости')).toBeTruthy();
    expect(screen.getByText(/Расчёты пока не выполняются/i)).toBeTruthy();
  });
});
