import { useEffect, useRef, type RefObject } from 'react';

/**
 * Keyboard behaviour shared by the driver dialogs: focus starts on the first control inside the
 * dialog, Escape closes it, Tab stays inside it, and focus returns to where it was on close.
 */
export function useDialogFocus(ref: RefObject<HTMLElement | null>, onClose: () => void): void {
  // Read through a ref, so a new onClose each render does not move focus back to the start.
  const close = useRef(onClose);
  useEffect(() => { close.current = onClose; }, [onClose]);
  useEffect(() => {
    const previous = document.activeElement as HTMLElement | null;
    const focusable = (): HTMLElement[] =>
      Array.from(ref.current?.querySelectorAll<HTMLElement>('button:not([disabled]), input:not([disabled]), textarea, [tabindex="0"]') ?? []);
    focusable()[0]?.focus();
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        close.current();
        return;
      }
      if (event.key !== 'Tab') return;
      const items = focusable();
      if (items.length === 0) return;
      const first = items[0];
      const last = items[items.length - 1];
      if (event.shiftKey && document.activeElement === first) {
        event.preventDefault();
        last.focus();
      } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault();
        first.focus();
      }
    };
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('keydown', onKey);
      previous?.focus();
    };
  }, [ref]);
}
