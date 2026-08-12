import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
} from 'react';
import { Loader2, CheckCircle2, XCircle, Info, X } from 'lucide-react';
import { cn } from '../lib/utils';

/** Visual/semantic state of a notification (mirrors the Azure/Intune portal toasts). */
export type ToastStatus = 'loading' | 'success' | 'error' | 'info';

export interface ToastOptions {
  /** Short bold headline, e.g. "Saving branding settings". */
  title: string;
  /** Optional secondary line with more detail. */
  description?: string;
  /** Notification state; `loading` shows a spinner and does not auto-dismiss. */
  status?: ToastStatus;
  /** Optional determinate progress (0–100) rendered as a thin bar. */
  progress?: number;
  /** Auto-dismiss delay in ms. Ignored for `loading`. Defaults per status. */
  duration?: number;
}

interface Toast extends Required<Pick<ToastOptions, 'title' | 'status'>> {
  id: string;
  description?: string;
  progress?: number;
  duration?: number;
  leaving?: boolean;
}

interface ToastContextValue {
  /** Shows a notification and returns its id (use with `update`/`dismiss`). */
  notify: (options: ToastOptions) => string;
  /** Patches an existing notification (e.g. loading → success). */
  update: (id: string, patch: Partial<ToastOptions>) => void;
  /** Dismisses a notification (plays the exit animation first). */
  dismiss: (id: string) => void;
}

const ToastContext = createContext<ToastContextValue | null>(null);

/** Default auto-dismiss durations (ms) by status. `loading` never auto-dismisses. */
const DEFAULT_DURATION: Record<ToastStatus, number | null> = {
  loading: null,
  success: 4000,
  error: 7000,
  info: 5000,
};

const EXIT_ANIMATION_MS = 180;

let counter = 0;
function nextId(): string {
  counter += 1;
  return `toast-${counter}-${Date.now()}`;
}

/**
 * Global notification provider (Azure/Intune-style status messages).
 * Mount once near the app root; consume via {@link useToast}.
 */
export function ToastProvider({ children }: { children: React.ReactNode }): React.ReactElement {
  const [toasts, setToasts] = useState<Toast[]>([]);
  const timers = useRef(new Map<string, ReturnType<typeof setTimeout>>());

  const clearTimer = useCallback((id: string) => {
    const timer = timers.current.get(id);
    if (timer) {
      clearTimeout(timer);
      timers.current.delete(id);
    }
  }, []);

  const remove = useCallback((id: string) => {
    setToasts((prev) => prev.filter((t) => t.id !== id));
  }, []);

  const dismiss = useCallback(
    (id: string) => {
      clearTimer(id);
      // Play the exit animation, then remove from the DOM.
      setToasts((prev) => prev.map((t) => (t.id === id ? { ...t, leaving: true } : t)));
      const timer = setTimeout(() => remove(id), EXIT_ANIMATION_MS);
      timers.current.set(`${id}-exit`, timer);
    },
    [clearTimer, remove],
  );

  const scheduleAutoDismiss = useCallback(
    (id: string, status: ToastStatus, duration?: number) => {
      clearTimer(id);
      const ms = duration ?? DEFAULT_DURATION[status];
      if (ms == null) return; // loading (or explicitly persistent)
      const timer = setTimeout(() => dismiss(id), ms);
      timers.current.set(id, timer);
    },
    [clearTimer, dismiss],
  );

  const notify = useCallback(
    (options: ToastOptions): string => {
      const id = nextId();
      const status = options.status ?? 'info';
      const toast: Toast = {
        id,
        title: options.title,
        description: options.description,
        status,
        progress: options.progress,
        duration: options.duration,
      };
      setToasts((prev) => [...prev, toast]);
      scheduleAutoDismiss(id, status, options.duration);
      return id;
    },
    [scheduleAutoDismiss],
  );

  const update = useCallback(
    (id: string, patch: Partial<ToastOptions>) => {
      setToasts((prev) =>
        prev.map((t) => {
          if (t.id !== id) return t;
          const nextStatus = patch.status ?? t.status;
          return {
            ...t,
            ...patch,
            status: nextStatus,
            leaving: false,
          };
        }),
      );
      // Re-arm the auto-dismiss based on the (possibly new) status.
      setToasts((prev) => {
        const target = prev.find((t) => t.id === id);
        if (target) scheduleAutoDismiss(id, target.status, patch.duration ?? target.duration);
        return prev;
      });
    },
    [scheduleAutoDismiss],
  );

  useEffect(() => {
    const map = timers.current;
    return () => {
      map.forEach((timer) => clearTimeout(timer));
      map.clear();
    };
  }, []);

  const value = useMemo<ToastContextValue>(() => ({ notify, update, dismiss }), [notify, update, dismiss]);

  return (
    <ToastContext.Provider value={value}>
      {children}
      <Toaster toasts={toasts} onDismiss={dismiss} />
    </ToastContext.Provider>
  );
}

/** Access the global notification API. Must be used within {@link ToastProvider}. */
export function useToast(): ToastContextValue {
  const ctx = useContext(ToastContext);
  if (!ctx) throw new Error('useToast must be used within a ToastProvider');
  return ctx;
}

const STATUS_ICON: Record<ToastStatus, React.ReactElement> = {
  loading: <Loader2 className="h-5 w-5 animate-spin text-primary" aria-hidden="true" />,
  success: <CheckCircle2 className="h-5 w-5 animate-pop text-emerald-500" aria-hidden="true" />,
  error: <XCircle className="h-5 w-5 animate-pop text-destructive" aria-hidden="true" />,
  info: <Info className="h-5 w-5 text-primary" aria-hidden="true" />,
};

const STATUS_ACCENT: Record<ToastStatus, string> = {
  loading: 'before:bg-primary',
  success: 'before:bg-emerald-500',
  error: 'before:bg-destructive',
  info: 'before:bg-primary',
};

/** Fixed bottom-right notification stack. */
function Toaster({ toasts, onDismiss }: { toasts: Toast[]; onDismiss: (id: string) => void }): React.ReactElement {
  return (
    <div
      className="pointer-events-none fixed bottom-4 right-4 z-[60] flex w-full max-w-sm flex-col gap-2"
      role="region"
      aria-label="Notifications"
    >
      {toasts.map((toast) => (
        <ToastCard key={toast.id} toast={toast} onDismiss={onDismiss} />
      ))}
    </div>
  );
}

function ToastCard({ toast, onDismiss }: { toast: Toast; onDismiss: (id: string) => void }): React.ReactElement {
  const showProgress = typeof toast.progress === 'number';
  const progress = Math.max(0, Math.min(100, toast.progress ?? 0));

  return (
    <div
      role="status"
      aria-live={toast.status === 'error' ? 'assertive' : 'polite'}
      className={cn(
        'pointer-events-auto relative overflow-hidden rounded-lg border border-border bg-card text-card-foreground shadow-lg',
        // Coloured accent bar on the leading edge (Azure/Intune style).
        'before:absolute before:inset-y-0 before:left-0 before:w-1 before:content-[""]',
        STATUS_ACCENT[toast.status],
        toast.leaving ? 'animate-toast-out' : 'animate-toast-in',
      )}
    >
      <div className="flex items-start gap-3 py-3 pl-4 pr-3">
        <span className="mt-0.5 shrink-0">{STATUS_ICON[toast.status]}</span>
        <div className="min-w-0 flex-1">
          <p className="text-sm font-medium leading-snug">{toast.title}</p>
          {toast.description && (
            <p className="mt-0.5 text-sm text-muted-foreground">{toast.description}</p>
          )}
        </div>
        <button
          type="button"
          onClick={() => onDismiss(toast.id)}
          className="shrink-0 rounded-md p-1 text-muted-foreground transition-colors hover:bg-accent hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          aria-label="Dismiss notification"
        >
          <X className="h-4 w-4" aria-hidden="true" />
        </button>
      </div>
      {showProgress && (
        <div className="h-1 w-full bg-muted">
          <div
            className="h-full bg-primary transition-[width] duration-300 ease-out"
            style={{ width: `${progress}%` }}
          />
        </div>
      )}
    </div>
  );
}
