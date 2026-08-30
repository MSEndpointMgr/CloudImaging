import { AlertTriangle } from 'lucide-react';
import { Button } from './ui/button.tsx';

/** Copy shown in an impact confirmation overlay before a high-impact action runs. */
export interface ConfirmImpactCopy {
  /** Short heading, e.g. "Rotate certificate?". */
  confirmTitle: string;
  /** Plain-language explanation of what will happen if the operator continues. */
  impact: string;
  /** Label for the confirm button, e.g. "Rotate". */
  confirmLabel: string;
  /** Styles the confirm button as destructive (red) when true. */
  destructive: boolean;
}

/**
 * Modal overlay that explains the impact of a high-impact/destructive action before it runs.
 * Used instead of the browser's native `confirm()` so the consequences can be described in detail.
 */
export function ConfirmImpactDialog({
  copy,
  busy,
  onCancel,
  onConfirm,
  titleId = 'confirm-impact-title',
}: {
  copy: ConfirmImpactCopy;
  busy: boolean;
  onCancel: () => void;
  onConfirm: () => void;
  /** Override the heading's element id if multiple dialogs could render in the same tree. */
  titleId?: string;
}): React.ReactElement {
  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 p-4"
      role="dialog"
      aria-modal="true"
      aria-labelledby={titleId}
    >
      <div className="w-full max-w-md rounded-lg border border-border bg-background shadow-xl">
        <div className="flex items-start gap-3 p-5">
          <div className="flex h-10 w-10 shrink-0 items-center justify-center rounded-full bg-destructive/10 text-destructive">
            <AlertTriangle className="h-5 w-5" aria-hidden="true" />
          </div>
          <div className="min-w-0 space-y-1.5">
            <h2 id={titleId} className="text-sm font-semibold break-words">
              {copy.confirmTitle}
            </h2>
            {/* Impact copy interpolates operator-supplied names, so an unbroken token long enough
                to outgrow the modal has to wrap rather than overflow it. */}
            <p className="text-xs leading-relaxed text-muted-foreground break-words">{copy.impact}</p>
          </div>
        </div>
        <div className="flex justify-end gap-2 border-t border-border px-5 py-3">
          <Button variant="outline" onClick={onCancel} disabled={busy}>
            Cancel
          </Button>
          <Button
            onClick={onConfirm}
            disabled={busy}
            loading={busy}
            className={
              copy.destructive
                ? 'bg-destructive text-destructive-foreground hover:bg-destructive/90'
                : undefined
            }
          >
            {copy.confirmLabel}
          </Button>
        </div>
      </div>
    </div>
  );
}
