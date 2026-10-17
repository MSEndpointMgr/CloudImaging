import * as React from 'react';

/** Gap between the anchor and the panel, and the minimum breathing room at the viewport edge. */
export const VIEWPORT_MARGIN = 8;

export interface AnchoredPosition {
  left: number;
  width: number;
  maxHeight: number;
  /** Exactly one of `top`/`bottom` is set, depending on the side the panel flipped to. */
  top?: number;
  bottom?: number;
}

interface AnchoredPositionOptions {
  /** Height the panel would like. Drives the decision to flip above the anchor. */
  preferredHeight: number;
  /** Panels wider than their anchor (a date range picker) set this; lists leave it at 0. */
  minWidth?: number;
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
  { preferredHeight, minWidth = 0 }: AnchoredPositionOptions,
): AnchoredPosition | null {
  const [position, setPosition] = React.useState<AnchoredPosition | null>(null);

  const update = React.useCallback(() => {
    const anchor = anchorRef.current;
    if (!anchor) return;

    const rect = anchor.getBoundingClientRect();
    const spaceBelow = window.innerHeight - rect.bottom - VIEWPORT_MARGIN * 2;
    const spaceAbove = rect.top - VIEWPORT_MARGIN * 2;
    // Below unless it genuinely does not fit and there is more room above, so the panel does not
    // jump sides for an anchor that is merely close to the fold.
    const placeBelow = spaceBelow >= preferredHeight || spaceBelow >= spaceAbove;

    const width = Math.max(rect.width, minWidth);
    const maxLeft = window.innerWidth - width - VIEWPORT_MARGIN;
    const left = Math.max(VIEWPORT_MARGIN, Math.min(rect.left, maxLeft));

    setPosition({
      left,
      width,
      maxHeight: Math.max(120, Math.min(preferredHeight, placeBelow ? spaceBelow : spaceAbove)),
      ...(placeBelow
        ? { top: rect.bottom + VIEWPORT_MARGIN }
        : { bottom: window.innerHeight - rect.top + VIEWPORT_MARGIN }),
    });
  }, [anchorRef, preferredHeight, minWidth]);

  React.useLayoutEffect(() => {
    if (!open) {
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
