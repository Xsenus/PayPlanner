import { fireEvent, render, screen } from '@testing-library/react';
import { expect, it, vi } from 'vitest';
import { YealinkImportPreviewModal } from './YealinkImportPreviewModal';
import type { YealinkContactImportDecisionAction, YealinkContactImportResultDto } from '../../services/api';

it('import preview controls: pending confirmation freezes choices and retry restores the same selection', () => {
  const preview: YealinkContactImportResultDto = { dryRun: true, scanned: 1, withPhones: 1, created: 1, updated: 0, skippedExisting: 0, skippedNoPhone: 0, skippedDuplicateInFile: 0, skippedByUser: 0, skippedInvalid: 0, details: [{ rowId: 1, name: 'Контрольный контакт', phones: [], action: 'WouldCreateExternal', matchedPhones: [] }] };
  const props = { preview, fileName: 'safe.xml', decisions: { 1: 'createClient' as YealinkContactImportDecisionAction }, importing: true, onChangeDecision: vi.fn(), onConfirm: vi.fn(), onClose: vi.fn() };
  const view = render(<YealinkImportPreviewModal {...props} />);
  const select = screen.getByRole('combobox') as HTMLSelectElement;
  expect(select.matches(':disabled')).toBe(true);
  expect(select.value).toBe('createClient');
  fireEvent.click(screen.getByRole('button', { name: /Импортир/ })); expect(props.onConfirm).not.toHaveBeenCalled();
  expect(screen.getByRole('button', { name: /Импортир/ }).matches(':disabled')).toBe(true);
  view.rerender(<YealinkImportPreviewModal {...props} importing={false} />);
  expect(select.value).toBe('createClient'); expect(select.matches(':disabled')).toBe(false);
  fireEvent.click(select);
  fireEvent.click(screen.getByRole('option', { name: 'Создать сторонний' }));
  expect(props.onChangeDecision).toHaveBeenCalledWith(1, 'createExternal');
});
