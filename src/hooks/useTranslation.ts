import { useCallback } from 'react';
import { ru } from '../locales/ru';

export function useTranslation() {
  const t = useCallback((key: string, params?: Record<string, string | number>): string => {
    const template = ru[key as keyof typeof ru] ?? key;
    if (!params) return template;

    return Object.entries(params).reduce((acc, [paramKey, paramValue]) => {
      return acc.split(`{{${paramKey}}}`).join(String(paramValue));
    }, template);
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