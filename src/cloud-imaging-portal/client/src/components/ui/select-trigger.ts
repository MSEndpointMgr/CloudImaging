import { cva } from 'class-variance-authority';
import { cn } from '../../lib/utils.ts';

/**
 * The closed state of every list picker (`Select`, `MultiSelect`).
 *
 * Geometry deliberately mirrors `Button` rather than inventing a second scale: 32px, the portal's
 * one control height. Pickers are almost always laid out beside a button (the OS image picker sits
 * next to "Start Imaging") and a 4px height difference between two adjacent controls reads as a
 * rendering bug, which is what shipped while this defaulted to 36px.
 */
export const selectTriggerVariants = cva(
  cn(
    'flex w-full items-center justify-between gap-2 rounded-md border border-input bg-background text-sm shadow-sm',
    'transition-colors hover:bg-accent/40',
    'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background',
    'disabled:cursor-not-allowed disabled:opacity-50 disabled:hover:bg-background',
  ),
  {
    variants: {
      size: {
        sm: 'h-8 pl-3 pr-2',
      },
    },
    defaultVariants: { size: 'sm' },
  },
);
