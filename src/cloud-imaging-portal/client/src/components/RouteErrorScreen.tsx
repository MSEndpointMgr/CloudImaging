import { RefreshCw } from 'lucide-react';
import { Button } from './ui/button.tsx';
import { BrandMark } from './BrandMark.tsx';

/**
 * Full-screen recoverable error state rendered by `RouteErrorBoundary`. Kept in its own file
 * (and as a function component) so it can read branding from context: an error boundary has to
 * be a class, and classes cannot use hooks.
 */
export function RouteErrorScreen(): React.ReactElement {
  return (
    <div className="flex h-screen w-full flex-col items-center justify-center gap-4 bg-background text-foreground">
      <BrandMark />
      <div className="flex max-w-sm flex-col items-center gap-1 text-center">
        <p className="text-lg font-semibold">Something went wrong</p>
        <p className="text-sm text-muted-foreground">
          This page failed to load. This usually clears up after a reload.
        </p>
      </div>
      <Button onClick={() => window.location.reload()}>
        <RefreshCw /> Reload
      </Button>
    </div>
  );
}
