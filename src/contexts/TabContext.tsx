/* eslint-disable react-refresh/only-export-components */
import { createContext, useContext, type ReactNode } from 'react';
import type { Tab } from '../types/tabs';

export interface TabContextValue {
  activeTab: Tab;
  setActiveTab: (tab: Tab) => void;
}

const TabContext = createContext<TabContextValue | null>(null);

interface TabProviderProps {
  value: TabContextValue;
  children: ReactNode;
}

export function TabProvider({ value, children }: TabProviderProps) {
  return <TabContext.Provider value={value}>{children}</TabContext.Provider>;
}

export function useTabNavigation() {
  const context = useContext(TabContext);
  if (!context) {
    return {
      activeTab: 'calendar' as Tab,
      setActiveTab: () => {
        /* no-op */
      },
    } satisfies TabContextValue;
  }
  return context;
}
