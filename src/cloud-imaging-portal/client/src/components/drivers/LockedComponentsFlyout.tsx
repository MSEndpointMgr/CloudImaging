import React, { useCallback, useId, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { Lock, X } from 'lucide-react';
import { Button } from '../ui/button.tsx';
import { useAnchoredPosition, useDismissOnOutsidePress } from '../ui/anchored-position.ts';
import type { DriverComponentRow } from '../../lib/drivers.ts';

const PANEL_WIDTH = 440;
const PANEL_HEIGHT = 420;

/** The components the vendor locks to Skip, kept out of the table and listed from its footer. */
export function LockedComponentsFlyout({ components, initiallyOpen = false }: { components: DriverComponentRow[]; initiallyOpen?: boolean }): React.ReactElement {
  const [open, setOpen] = useState(initiallyOpen);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const panelRef = useRef<HTMLDivElement>(null);
  const titleId = useId();
  const position = useAnchoredPosition(triggerRef, open, { preferredHeight: PANEL_HEIGHT, minWidth: PANEL_WIDTH });
  const close = useCallback(() => setOpen(false), []);
  useDismissOnOutsidePress([triggerRef, panelRef], open, close);

  const dismiss = () => {
    setOpen(false);
    triggerRef.current?.focus();
  };

  return (
    <>
      <button
        ref={triggerRef}
        type="button"
        aria-expanded={open}
        aria-haspopup="dialog"
        onClick={() => setOpen(o => !o)}
        className="inline-flex items-center gap-1.5 rounded-sm underline-offset-2 hover:text-foreground hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
      >
        <Lock className="h-3 w-3" aria-hidden="true" />
        {`${components.length} locked to Skip`}
      </button>
      {open && position && createPortal(
        <div
          ref={panelRef}
          role="dialog"
          aria-labelledby={titleId}
          onKeyDown={event => { if (event.key === 'Escape') { event.stopPropagation(); dismiss(); } }}
          style={{ position: 'fixed', left: position.left, width: position.width, maxHeight: position.maxHeight, ...(position.top !== undefined ? { top: position.top } : { bottom: position.bottom }) }}
          className="z-50 flex flex-col overflow-hidden rounded-lg border border-border bg-popover text-popover-foreground shadow-xl ring-1 ring-black/5 animate-popover-in"
        >
          <div className="flex items-start gap-3 border-b border-border px-4 py-3">
            <div className="min-w-0 flex-1">
              <p id={titleId} className="text-sm font-medium">Locked to Skip</p>
              <p className="mt-0.5 text-sm text-muted-foreground">The vendor&apos;s data only allows Skip for these components, so they are never installed and have no action to choose.</p>
            </div>
            <Button variant="ghost" size="icon" aria-label="Close" onClick={dismiss} autoFocus><X /></Button>
          </div>
          <ul className="min-h-0 flex-1 divide-y divide-border overflow-y-auto">
            {components.map(component => (
              <li key={component.lineageKey} id={`component-${component.lineageKey}`} className="flex items-start gap-3 px-4 py-2.5">
                <div className="min-w-0 flex-1">
                  <p className="truncate text-sm font-medium" title={component.name}>{component.name}</p>
                  <div className="flex min-w-0 items-center gap-2 text-xs text-muted-foreground">
                    <span className="shrink-0 font-mono">{component.componentId}</span>
                    {component.category && <span className="truncate">{component.category}</span>}
                  </div>
                </div>
                <span className="shrink-0 text-sm tabular-nums text-muted-foreground">{component.version}</span>
              </li>
            ))}
          </ul>
        </div>,
        document.body,
      )}
    </>
  );
}
