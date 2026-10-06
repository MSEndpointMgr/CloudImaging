import * as React from 'react';
import { cva, type VariantProps } from 'class-variance-authority';
import { Loader2, Check, X } from 'lucide-react';
import { cn } from '../../lib/utils';

const buttonVariants = cva(
  'inline-flex items-center justify-center gap-2 whitespace-nowrap rounded-md text-sm font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background disabled:pointer-events-none disabled:opacity-50 [&_svg]:pointer-events-none [&_svg]:size-4 [&_svg]:shrink-0',
  {
    variants: {
      variant: {
        default: 'bg-primary text-primary-foreground shadow-sm hover:bg-primary/90',
        destructive: 'bg-destructive text-destructive-foreground shadow-sm hover:bg-destructive/90',
        outline: 'border border-input bg-background shadow-sm hover:bg-accent hover:text-accent-foreground',
        secondary: 'bg-secondary text-secondary-foreground shadow-sm hover:bg-secondary/80',
        ghost: 'hover:bg-accent hover:text-accent-foreground',
        link: 'text-primary underline-offset-4 hover:underline',
      },
      size: {
        // `sm` is the portal's only standard size (see defaultVariants). 32px with a 14px label.
        // There is deliberately no 36px step: a second near-identical height is impossible to pick
        // between and produced buttons 4px taller than their neighbours wherever it was used.
        sm: 'h-8 rounded-md px-3',
        lg: 'h-10 rounded-md px-6',
        icon: 'h-8 w-8',
      },
    },
    // A text link has no box: it sits inline in a sentence, so the size's height and padding are
    // wrong for it. compoundVariants emit last, so twMerge in `cn` lets these win.
    compoundVariants: [{ variant: 'link', className: 'h-auto p-0' }],
    defaultVariants: { variant: 'default', size: 'sm' },
  },
);

/**
 * Transient interaction feedback rendered inside the button:
 * - `loading`  → a spinner replaces the button's own icon (operation in progress).
 * - `success`  → a green check "pops" in (operation completed).
 * - `error`    → a red cross "pops" in (operation failed).
 * The label stays visible throughout, so the button never becomes an unlabelled box mid-action.
 * Pages typically drive this through a short-lived state machine
 * (idle → loading → success | error → idle).
 */
export type ButtonStatus = 'idle' | 'loading' | 'success' | 'error';

export interface ButtonProps
  extends React.ButtonHTMLAttributes<HTMLButtonElement>,
    VariantProps<typeof buttonVariants> {
  /** Convenience flag equivalent to `status="loading"`. */
  loading?: boolean;
  /** Drives the in-button spinner / success / error micro-animation. */
  status?: ButtonStatus;
}

const Button = React.forwardRef<HTMLButtonElement, ButtonProps>(
  ({ className, variant, size, loading, status = 'idle', disabled, children, ...props }, ref) => {
    const state: ButtonStatus = loading ? 'loading' : status;
    const isBusy = state === 'loading';

    const statusIcon =
      state === 'loading' ? <Loader2 className="animate-spin" aria-hidden="true" />
      : state === 'success' ? <Check className="animate-pop text-emerald-500" aria-hidden="true" />
      : state === 'error' ? <X className="animate-pop text-destructive" aria-hidden="true" />
      : null;

    return (
      <button
        ref={ref}
        className={cn(buttonVariants({ variant, size }), className)}
        disabled={disabled ?? isBusy}
        aria-busy={isBusy || undefined}
        {...props}
      >
        {statusIcon}
        {/* `contents` keeps the flex gap; hiding the leading icon lets the status icon take its
            place rather than sitting next to it. */}
        <span className={cn('contents', statusIcon && '[&>svg]:hidden')}>{children}</span>
      </button>
    );
  },
);
Button.displayName = 'Button';

export { Button, buttonVariants };
