import { useCallback } from 'react';
import { apiService } from '../services/api';
import type { CreateUserActivityInput } from '../types/userActivity';

export function useActivityLogger(defaults?: Partial<CreateUserActivityInput>) {
  return useCallback(
    (payload: CreateUserActivityInput) => {
      const base: CreateUserActivityInput = {
        ...defaults,
        ...payload,
      };
      const finalPayload: CreateUserActivityInput = {
        ...base,
        status: base.status ?? 'Info',
      };

      if (!finalPayload.category || !finalPayload.action) {
        return;
      }

      void apiService.logUserActivity(finalPayload).catch((error) => {
        if (import.meta.env.DEV) {
          console.warn('Не удалось записать активность пользователя', error);
        }
      });
    },
    [defaults],
  );
}
