import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { TablePagination } from './TablePagination';

describe('pagination page-size accessibility', () => {
  it.each([undefined, 'Задач на странице'])('names the visible page-size control and preserves its change handler (%s)', async (pageSizeLabel) => {
    const change = vi.fn(); const user = userEvent.setup();
    render(<TablePagination page={1} totalPages={3} totalItems={30} pageSize={10} pageSizeOptions={[10, 25, 50]}
      onPageChange={vi.fn()} onPageSizeChange={change} pageSizeLabel={pageSizeLabel} />);
    const label = pageSizeLabel ?? 'Строк на странице';
    await user.click(screen.getByRole('combobox', { name: new RegExp(`^${label}`) }));
    await user.click(screen.getByRole('option', { name: '25' }));
    expect(change).toHaveBeenCalledExactlyOnceWith(25);
    expect(screen.queryByRole('listbox')).toBeNull();
  });
});
