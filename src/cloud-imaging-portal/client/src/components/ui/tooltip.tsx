import * as React from 'react';
import { createPortal } from 'react-dom';
import { cn } from '../../lib/utils.ts';

type Side = 'top' | 'bottom';

interface TooltipProps {
  /** Tooltip text. Kept short - this is a label, not documentation. */
  content: React.ReactNode;
  children: React.ReactNode;
  side?: Side;
  /** Applied to the inline wrapper that owns the hover/focus handlers. */
  className?: string;
}

/** Gap in px between the trigger and the tooltip. */
const OFFSET = 6;
/** Keeps the bubble off the very edge of the viewport when a trigger sits in a corner. */
const VIEWPORT_MARGIN = 8;
/**
 * Long enough that sweeping the pointer across a row of icon buttons does not flash a tooltip per
 * button, short enough that deliberately resting on one feels immediate.
 */
const OPEN_DELAY_MS = 200;

/**
 * Hover- *and* focus-triggered tooltip.
 *
 * Replaces the native `title` attribute on icon-only controls. `title` has three problems that
 * matter here: it never appears on keyboard focus, so a keyboard user has no way to discover what
 * an unlabelled icon does; its ~1s delay and OS-drawn styling are outside the theme; and screen
 * reader support for it is inconsistent, so it cannot be relied on as the accessible name. This
 * exposes the text through `aria-describedby` instead - the trigger still needs its own
 * `aria-label`.
 *
 * The bubble is portalled to `document.body` and positioned with fixed coordinates rather than
 * absolutely inside a wrapper, because the triggers live in table cells whose scroll containers
 * would otherwise clip it.
 */
export function Tooltip({ content, children, side = 'top', className }: TooltipProps): React.ReactElement {
  const [open, setOpen] = React.useState(false);
  const [coords, setCoords] = React.useState<{ top: number; left: number } | null>(null);
  const wrapperRef = React.useRef<HTMLSpanElement>(null);
  const bubbleRef = React.useRef<HTMLDivElement>(null);
  const openTimer = React.useRef<ReturnType<typeof setTimeout> | null>(null);
  const describedById = React.useId();

  const cancelPendingOpen = React.useCallback(() => {
    if (openTimer.current) {
      clearTimeout(openTimer.current);
      openTimer.current = null;
    }
  }, []);

  const hide = React.useCallback(() => {
    cancelPendingOpen();
    setOpen(false);
  }, [cancelPendingOpen]);

  // Keyboard focus shows the tooltip with no delay - the delay exists to suppress accidental
  // pointer sweeps, and there is no such thing as accidentally tabbing to a control.
  const show = React.useCallback((immediate: boolean) => {
    cancelPendingOpen();
    if (immediate) {
      setOpen(true);
      return;
    }
    openTimer.current = setTimeout(() => setOpen(true), OPEN_DELAY_MS);
  }, [cancelPendingOpen]);

  React.useEffect(() => cancelPendingOpen, [cancelPendingOpen]);

  // Measured after paint, once the bubble has a real width, so it can be centred and clamped.
  React.useLayoutEffect(() => {
    if (!open) {
      setCoords(null);
      return;
    }

    const trigger = wrapperRef.current?.getBoundingClientRect();
    const bubble = bubbleRef.current?.getBoundingClientRect();
    if (!trigger || !bubble) return;

    // `Math.max` is applied last so that a viewport narrower than the bubble still clamps to the
    // left margin instead of going negative (the upper bound would otherwise win).
    const centred = trigger.left + trigger.width / 2 - bubble.width / 2;
    const left = Math.max(
      VIEWPORT_MARGIN,
      Math.min(centred, window.innerWidth - bubble.width - VIEWPORT_MARGIN),
    );

    // Flip to the other side rather than render off-screen when there is no room.
    const fitsTop = trigger.top - bubble.height - OFFSET >= VIEWPORT_MARGIN;
    const fitsBottom = trigger.bottom + bubble.height + OFFSET <= window.innerHeight - VIEWPORT_MARGIN;
    const placeOnTop = side === 'top' ? fitsTop || !fitsBottom : !fitsBottom && fitsTop;

    setCoords({
      top: placeOnTop ? trigger.top - bubble.height - OFFSET : trigger.bottom + OFFSET,
      left,
    });
  }, [open, side, content]);

  // The bubble is `fixed`, so its coordinates go stale the moment anything scrolls. Hiding is the
  // right response rather than repositioning: the pointer has left the trigger by then anyway, and
  // a keyboard user scrolling away from the focused control does not need its label following them.
  React.useEffect(() => {
    if (!open) return;
    window.addEventListener('scroll', hide, true); // capture: also catches nested scroll containers
    window.addEventListener('resize', hide);
    return () => {
      window.removeEventListener('scroll', hide, true);
      window.removeEventListener('resize', hide);
    };
  }, [open, hide]);

  // Escape dismisses, matching every other transient surface in the portal.
  React.useEffect(() => {
    if (!open) return;
    const onKeyDown = (event: KeyboardEvent): void => {
      if (event.key === 'Escape') hide();
    };
    document.addEventListener('keydown', onKeyDown);
    return () => document.removeEventListener('keydown', onKeyDown);
  }, [open, hide]);

  // `aria-describedby` has to land on the interactive child, not on this wrapper: assistive tech
  // reads the description of the *focused* element, and a plain `<span>` is never focused. The
  // wrapper still owns the pointer handlers and the measurement rect.
  const describedChild = React.isValidElement(children)
    ? React.cloneElement(children as React.ReactElement<{ 'aria-describedby'?: string }>, {
        'aria-describedby': open ? describedById : undefined,
      })
    : children;

  return (
    <>
      <span
        ref={wrapperRef}
        className={cn('inline-flex', className)}
        // Touch fires pointerenter on tap and never fires pointerleave, which would leave the
        // bubble stranded until the next tap elsewhere.
        onPointerEnter={event => { if (event.pointerType === 'mouse') show(false); }}
        onPointerLeave={hide}
        onFocusCapture={() => show(true)}
        onBlurCapture={hide}
      >
        {describedChild}
      </span>

      {open && createPortal(
        <div
          ref={bubbleRef}
          id={describedById}
          role="tooltip"
          className={cn(
            'pointer-events-none fixed z-50 max-w-xs rounded-md border border-border bg-popover px-2 py-1',
            'text-xs text-popover-foreground shadow-md',
            // Hidden until measured, otherwise the first frame renders at 0,0 and visibly jumps.
            coords ? 'opacity-100' : 'opacity-0',
          )}
          style={{ top: coords?.top ?? 0, left: coords?.left ?? 0 }}
        >
          {content}
        </div>,
        document.body,
      )}
    </>
  );
}
