import { Component, type ErrorInfo, type ReactNode } from 'react';

type RouteErrorBoundaryProps = {
  children: ReactNode;
  resetKey: string;
  onGoHome: () => void;
};

type RouteErrorBoundaryState = {
  error: Error | null;
};

function isChunkLoadError(error: unknown): boolean {
  const message = error instanceof Error ? error.message : String(error ?? '');

  return /ChunkLoadError|Loading chunk|Failed to fetch dynamically imported module|Importing a module script failed|error loading dynamically imported module/i.test(message);
}

export class RouteErrorBoundary extends Component<RouteErrorBoundaryProps, RouteErrorBoundaryState> {
  state: RouteErrorBoundaryState = {
    error: null,
  };

  static getDerivedStateFromError(error: Error): RouteErrorBoundaryState {
    return { error };
  }

  componentDidCatch(error: Error, errorInfo: ErrorInfo) {
    console.error('Route render failed', error, errorInfo);
  }

  componentDidUpdate(previousProps: RouteErrorBoundaryProps) {
    if (previousProps.resetKey !== this.props.resetKey && this.state.error) {
      this.setState({ error: null });
    }
  }

  private reset = () => {
    this.setState({ error: null });
  };

  private reload = () => {
    window.location.reload();
  };

  private goHome = () => {
    this.reset();
    this.props.onGoHome();
  };

  render() {
    if (!this.state.error) {
      return this.props.children;
    }

    const chunkLoadFailed = isChunkLoadError(this.state.error);

    return (
      <div className="p-8">
        <div className="mx-auto max-w-4xl rounded-3xl border border-amber-200 bg-amber-50 p-8 text-center shadow-sm">
          <h2 className="text-xl font-bold text-amber-950">
            {chunkLoadFailed ? 'Раздел обновился, нужно перезагрузить страницу' : 'Раздел не удалось открыть'}
          </h2>
          <p className="mx-auto mt-3 max-w-2xl text-sm leading-6 text-amber-800">
            {chunkLoadFailed
              ? 'Похоже, браузер попытался открыть старый файл интерфейса после обновления системы. Сначала сохраните несохранённые данные, затем обновите страницу вручную.'
              : 'Мы не будем оставлять вас на белом экране. Попробуйте открыть раздел ещё раз или вернитесь в календарь.'}
          </p>
          <div className="mt-6 flex flex-wrap justify-center gap-3">
            <button
              type="button"
              onClick={this.reload}
              className="rounded-xl bg-slate-950 px-4 py-2 text-sm font-semibold text-white transition hover:bg-slate-800"
            >
              Обновить страницу
            </button>
            <button
              type="button"
              onClick={this.reset}
              className="rounded-xl border border-amber-300 bg-white px-4 py-2 text-sm font-semibold text-amber-900 transition hover:bg-amber-100"
            >
              Попробовать ещё раз
            </button>
            <button
              type="button"
              onClick={this.goHome}
              className="rounded-xl border border-slate-200 bg-white px-4 py-2 text-sm font-semibold text-slate-700 transition hover:bg-slate-50"
            >
              В календарь
            </button>
          </div>
        </div>
      </div>
    );
  }
}
