import * as React from 'react';

/** Gap between the anchor and the panel, and the minimum breathing room at the viewport edge. */
export const VIEWPORT_MARGIN = 8;

export interface AnchoredPosition {
  left: number;
  /** The anchor's width, or the requested minimum when wider: a panel never ends short of its trigger. */
  minWidth: number;
  /** The viewport less its margins: as far as a panel may grow for content that needs the room. */
  maxWidth: number;
  maxHeight: number;
  /** Exactly one of `top`/`bottom` is set, depending on the side the panel flipped to. */
  top?: number;
  bottom?: number;
}

interface AnchoredPositionOptions {
  /** Height the panel would like. Drives the decision to flip above the anchor. */
  preferredHeight: number;
  /** Width the panel needs whatever its anchor's (a date range's two date fields); lists leave it at 0. */
  minWidth?: number;
  /**
   * The rendered panel. Panels are as wide as their anchor and only grow for content that needs
   * it (`width: max-content`), so the panel is measured to keep a grown one inside the viewport.
   */
  panelRef?: React.RefObject<HTMLElement | null>;
  /** Hold the width the panel opened with, so filtering its rows does not shrink it while typing. */
  keepWidth?: boolean;
}

function samePosition(a: AnchoredPosition, b: AnchoredPosition): boolean {
  return a.left === b.left && a.minWidth === b.minWidth && a.maxWidth === b.maxWidth && a.maxHeight === b.maxHeight
    && a.top === b.top && a.bottom === b.bottom;
}

/**
 * Positions a portalled panel against an anchor element, in viewport coordinates.
 *
 * Portalling is not cosmetic: several call sites sit inside `overflow-hidden` card headers, which
 * would clip an in-flow absolutely positioned panel. Position is recomputed on scroll and resize,
 * and the panel flips above the anchor when there is genuinely more room there.
 */
export function useAnchoredPosition(
  anchorRef: React.RefObject<HTMLElement | null>,
  open: boolean,
  { preferredHeight, minWidth = 0, panelRef, keepWidth = false }: AnchoredPositionOptions,
): AnchoredPosition | null {
  const [position, setPosition] = React.useState<AnchoredPosition | null>(null);
  const openedWidth = React.useRef(0);

  const update = React.useCallback(() => {
    const anchor = anchorRef.current;
    if (!anchor) return;

    const rect = anchor.getBoundingClientRect();
    const spaceBelow = window.innerHeight - rect.bottom - VIEWPORT_MARGIN * 2;
    const spaceAbove = rect.top - VIEWPORT_MARGIN * 2;
    // Below unless it genuinely does not fit and there is more room above, so the panel does not
    // jump sides for an anchor that is merely close to the fold.
    const placeBelow = spaceBelow >= preferredHeight || spaceBelow >= spaceAbove;

    const measured = panelRef?.current?.offsetWidth ?? 0;
    if (keepWidth && measured > openedWidth.current) openedWidth.current = measured;
    const maxWidth = Math.max(0, window.innerWidth - VIEWPORT_MARGIN * 2);
    const floor = Math.min(maxWidth, Math.max(rect.width, minWidth, keepWidth ? openedWidth.current : 0));
    const width = Math.min(maxWidth, Math.max(floor, measured));
    const left = Math.max(VIEWPORT_MARGIN, Math.min(rect.left, window.innerWidth - width - VIEWPORT_MARGIN));

    const next: AnchoredPosition = {
      left,
      minWidth: floor,
      maxWidth,
      maxHeight: Math.max(120, Math.min(preferredHeight, placeBelow ? spaceBelow : spaceAbove)),
      ...(placeBelow
        ? { top: rect.bottom + VIEWPORT_MARGIN }
        : { bottom: window.innerHeight - rect.top + VIEWPORT_MARGIN }),
    };
    setPosition(current => (current && samePosition(current, next) ? current : next));
  }, [anchorRef, preferredHeight, minWidth, panelRef, keepWidth]);

  React.useLayoutEffect(() => {
    if (!open) {
      openedWidth.current = 0;
      setPosition(null);
      return;
    }
    update();

    // Capture phase so the panel also tracks scrolling of any nested scroll container the anchor
    // sits in, not just the window.
    const onReflow = (): void => update();
    window.addEventListener('scroll', onReflow, true);
    window.addEventListener('resize', onReflow);
    return () => {
      window.removeEventListener('scroll', onReflow, true);
      window.removeEventListener('resize', onReflow);
    };
  }, [open, update]);

  // A panel sized by its content can only be measured once it is on screen, so place it again then.
  const shown = position !== null;
  React.useLayoutEffect(() => {
    if (open && shown) update();
  }, [open, shown, update]);

  return position;
}

/** Dismisses an open panel on a pointer press outside every supplied element. */
export function useDismissOnOutsidePress(
  refs: readonly React.RefObject<HTMLElement | null>[],
  open: boolean,
  onDismiss: () => void,
): void {
  React.useEffect(() => {
    if (!open) return;
    const onPointerDown = (event: MouseEvent): void => {
      const target = event.target as Node;
      if (refs.some(ref => ref.current?.contains(target))) return;
      onDismiss();
    };
    document.addEventListener('mousedown', onPointerDown);
    return () => document.removeEventListener('mousedown', onPointerDown);
    // Spreading refs keeps the effect stable: ref objects are identity-stable across renders.
  }, [open, onDismiss, ...refs]); // eslint-disable-line react-hooks/exhaustive-deps
}
