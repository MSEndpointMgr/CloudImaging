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
        // `sm` is the standard portal button size (see defaultVariants). Every button
        // should use this size unless there is a specific reason not to. It deliberately
        // inherits the base `text-sm`: a 32px control with a 14px label matches the `sm`
        // variant of `Select` and keeps button labels readable next to body copy.
        default: 'h-9 px-4 py-2',
        sm: 'h-8 rounded-md px-3',
        lg: 'h-10 rounded-md px-6',
        icon: 'h-8 w-8',
      },
    },
    defaultVariants: { variant: 'default', size: 'sm' },
  },
);

/**
 * Transient interaction feedback rendered inside the button:
 * - `loading`  → the label is replaced by a spinner (operation in progress).
 * - `success`  → a green check "pops" in (operation completed).
 * - `error`    → a red cross "pops" in (operation failed).
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

    return (
      <button
        ref={ref}
        className={cn(buttonVariants({ variant, size }), className)}
        disabled={disabled ?? isBusy}
        aria-busy={isBusy || undefined}
        {...props}
      >
        {state === 'loading' ? (
          <Loader2 className="animate-spin" aria-hidden="true" />
        ) : state === 'success' ? (
          <Check className="animate-pop text-emerald-500" aria-hidden="true" />
        ) : state === 'error' ? (
          <X className="animate-pop text-destructive" aria-hidden="true" />
        ) : (
          children
        )}
      </button>
    );
  },
);
Button.displayName = 'Button';

export { Button, buttonVariants };
