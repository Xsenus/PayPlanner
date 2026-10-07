import { createRoot } from 'react-dom/client';
import { DialogContext, type DialogContextValue } from '../../contexts/dialogContextCore';
import { ToastContext, type ToastContextValue } from '../../contexts/toastContextCore';
import '../../index.css';
import { StorageCapacityRecoverySettings } from './StorageCapacityRecoverySettings';

const dialog: DialogContextValue = {
  confirm: async () => true,
  alert: async () => undefined,
  prompt: async () => null,
};
const toast: ToastContextValue = {
  showToast: () => 'visual',
  dismissToast: () => undefined,
  success: () => 'visual',
  error: () => 'visual',
  info: () => 'visual',
};

createRoot(document.getElementById('root')!).render(
  <DialogContext.Provider value={dialog}>
    <ToastContext.Provider value={toast}>
      <main className="mx-auto max-w-5xl p-3 lg:p-4">
        <h1 className="mb-3 text-xl font-semibold">Настройки → Хранилище файлов</h1>
        <StorageCapacityRecoverySettings releasedMarkers={new URLSearchParams(location.search).get('markers') === 'true'} />
      </main>
    </ToastContext.Provider>
  </DialogContext.Provider>,
);
