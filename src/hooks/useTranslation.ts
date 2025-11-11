import { useCallback } from 'react';
import { ru, TranslationKey } from '../locales/ru';

export function useTranslation() {
  const t = useCallback((key: TranslationKey): string => {
    return ru[key] || key;
  }, []);

  const formatCurrency = useCallback((amount: number): string => {
    return new Intl.NumberFormat('ru-RU', {
      style: 'currency',
      currency: 'RUB',
      minimumFractionDigits: 0,
      maximumFractionDigits: 0,
    }).format(amount);
  }, []);

  const formatDate = useCallback((dateStr: string): string => {
    return new Date(dateStr).toLocaleDateString('ru-RU', {
      year: 'numeric',
      month: 'short',
      day: 'numeric',
    });
  }, []);

  const formatMonth = useCallback((date: Date): string => {
    return date.toLocaleDateString('ru-RU', {
      month: 'long',
      year: 'numeric',
    });
  }, []);

  return {
    t,
    formatCurrency,
    formatDate,
    formatMonth,
  };
}