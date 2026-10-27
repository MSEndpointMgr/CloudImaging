import { useContext, useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { UNSAFE_DataRouterContext, useBlocker } from 'react-router-dom';
import { CircleAlert, CircleCheck, Save, TriangleAlert, Undo2 } from 'lucide-react';
import { Button, type ButtonStatus } from './ui/button.tsx';
import { useDialogFocus } from './drivers/useDialogFocus.ts';
import { cn } from '../lib/utils.ts';
import { describeLostChanges, describeUnsavedChanges, type UnsavedChange } from '../lib/unsavedChanges.ts';

/** Id of the slot the app shell renders directly below the header. */
export const SAVE_BAR_SLOT_ID = 'save-bar-slot';

/**
 * The unsaved-changes bar every editable page uses, and the guard that stops the page being left
 * with those changes. It renders into a slot under the header rather than in page flow, so it spans
 * the page and stays in view while a long form scrolls. Use one per page: the router allows a
 * single navigation blocker at a time.
 */
export function SaveBar({ changes, status = 'idle', blockedReason = null, savedMessage = 'Changes saved.', onDiscard, onSave }: {
  /** Everything that differs from the saved state. The bar shows while this is not empty. */
  changes: UnsavedChange[];
  status?: ButtonStatus;
  /** Why the changes cannot be saved yet. Shown in place of the summary, and holds the Save button. */
  blockedReason?: string | null;
  /** Shown while the success state settles. */
  savedMessage?: string;
  onDiscard: () => void;
  /** Saves everything; resolves true when it saved, so leaving can continue. */
  onSave: () => Promise<boolean> | boolean;
}): React.ReactElement | null {
  // The slot belongs to an ancestor, so it is only in the DOM from the first commit onwards.
  const [slot, setSlot] = useState<HTMLElement | null>(null);
  useEffect(() => { setSlot(document.getElementById(SAVE_BAR_SLOT_ID)); }, []);
  // useBlocker needs a data router; a page rendered on its own (tests) still gets the bar.
  const dataRouter = useContext(UNSAFE_DataRouterContext) !== null;
  const dirty = changes.length > 0;

  // Closing or reloading the tab is outside the router, so the browser's own prompt covers it.
  useEffect(() => {
    if (!dirty) return;
    const onBeforeUnload = (event: BeforeUnloadEvent) => { event.preventDefault(); };
    window.addEventListener('beforeunload', onBeforeUnload);
    return () => window.removeEventListener('beforeunload', onBeforeUnload);
  }, [dirty]);

  const busy = status === 'loading';
  const blocked = dirty && Boolean(blockedReason);
  const saved = status === 'success' && !dirty;
  const open = dirty || status !== 'idle';

  const bar = open ? (
    <div className={cn('border-b border-border bg-card shadow-sm', !saved && (blocked ? 'shadow-[inset_2px_0_0_0_hsl(var(--destructive))]' : 'shadow-[inset_2px_0_0_0_theme(colors.amber.500)]'))}>
      <div className="mx-auto flex w-full max-w-[1440px] items-center gap-3 px-6 py-3">
        <div className="flex min-w-0 flex-1 items-center gap-2" role="status">
          {saved
            ? <CircleCheck className="h-4 w-4 shrink-0 text-emerald-600 dark:text-emerald-400" aria-hidden="true" />
            : <CircleAlert className={cn('h-4 w-4 shrink-0', blocked ? 'text-destructive' : 'text-amber-500')} aria-hidden="true" />}
          <p className={cn('min-w-0 truncate text-sm', blocked ? 'text-destructive' : 'text-muted-foreground')}>
            {saved ? savedMessage : blocked ? blockedReason : describeUnsavedChanges(changes)}
          </p>
        </div>
        <Button variant="outline" onClick={onDiscard} disabled={busy || !dirty}><Undo2 />Discard</Button>
        <Button onClick={() => void onSave()} status={status} disabled={blocked || !dirty}><Save />Save</Button>
      </div>
    </div>
  ) : null;

  return (
    <>
      {/* Rendered in place when there is no shell around the page, so a page stays usable on its own. */}
      {bar && (slot ? createPortal(bar, slot) : bar)}
      {dataRouter && (
        <LeaveGuard dirty={dirty} changes={changes} blockedReason={blocked ? blockedReason : null} onDiscard={onDiscard} onSave={onSave} />
      )}
    </>
  );
}

/** Asks before leaving the page with unsaved changes, whether by link, the Back button or code. */
function LeaveGuard({ dirty, changes, blockedReason, onDiscard, onSave }: {
  dirty: boolean;
  changes: UnsavedChange[];
  blockedReason: string | null;
  onDiscard: () => void;
  onSave: () => Promise<boolean> | boolean;
}): React.ReactElement | null {
  // Same-page navigations (a search parameter) keep the draft, so they pass.
  const blocker = useBlocker(({ currentLocation, nextLocation }) => dirty && currentLocation.pathname !== nextLocation.pathname);
  const [saving, setSaving] = useState(false);

  if (blocker.state !== 'blocked') return null;

  const stay = () => { if (!saving) blocker.reset(); };
  const discard = () => {
    onDiscard();
    blocker.proceed();
  };
  const save = async () => {
    setSaving(true);
    try {
      if (await onSave()) blocker.proceed();
      else blocker.reset();
    } finally {
      setSaving(false);
    }
  };

  return <LeaveDialog changes={changes} blockedReason={blockedReason} saving={saving} onStay={stay} onDiscard={discard} onSave={() => void save()} />;
}

function LeaveDialog({ changes, blockedReason, saving, onStay, onDiscard, onSave }: {
  changes: UnsavedChange[];
  blockedReason: string | null;
  saving: boolean;
  onStay: () => void;
  onDiscard: () => void;
  onSave: () => void;
}): React.ReactElement {
  const panel = useRef<HTMLDivElement>(null);
  useDialogFocus(panel, onStay);
  return createPortal(
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 p-4" role="dialog" aria-modal="true" aria-labelledby="leave-unsaved-title">
      <div ref={panel} className="w-full max-w-md rounded-lg border border-border bg-background shadow-xl">
        <div className="flex items-start gap-3 p-5">
          <div className="flex h-10 w-10 shrink-0 items-center justify-center rounded-full bg-amber-500/15 text-amber-600 dark:text-amber-400">
            <TriangleAlert className="h-5 w-5" aria-hidden="true" />
          </div>
          <div className="min-w-0 space-y-2">
            <h2 id="leave-unsaved-title" className="text-base font-semibold">Leave without saving?</h2>
            <p className="break-words text-sm text-muted-foreground">{describeLostChanges(changes)} Save them, or discard them and leave.</p>
            {blockedReason && <p className="break-words text-sm text-destructive">{blockedReason}</p>}
          </div>
        </div>
        <div className="flex justify-end gap-2 border-t border-border px-5 py-3">
          <Button variant="secondary" onClick={onStay} disabled={saving}>Stay</Button>
          <Button variant="outline" onClick={onDiscard} disabled={saving}><Undo2 />Discard</Button>
          <Button onClick={onSave} loading={saving} disabled={Boolean(blockedReason)}><Save />Save</Button>
        </div>
      </div>
    </div>,
    document.body,
  );
}
