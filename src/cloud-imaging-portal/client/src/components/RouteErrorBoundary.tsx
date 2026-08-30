import { Component, type ErrorInfo, type ReactNode } from 'react';
import { HardDriveDownload, RefreshCw } from 'lucide-react';
import { Button } from './ui/button.tsx';

interface RouteErrorBoundaryProps {
  children: ReactNode;
}

interface RouteErrorBoundaryState {
  error: Error | null;
}

/**
 * Catches errors thrown while rendering a route (including a route chunk that failed to load
 * even after `lazyWithReload`'s single automatic retry) so the app shows a recoverable message
 * instead of an indefinitely stuck Suspense fallback or a silently blank page.
 */
export class RouteErrorBoundary extends Component<RouteErrorBoundaryProps, RouteErrorBoundaryState> {
  state: RouteErrorBoundaryState = { error: null };

  static getDerivedStateFromError(error: Error): RouteErrorBoundaryState {
    return { error };
  }

  componentDidCatch(error: Error, info: ErrorInfo): void {
    console.error('Route failed to render', error, info.componentStack);
  }

  render(): ReactNode {
    if (!this.state.error) {
      return this.props.children;
    }

    return (
      <div className="flex h-screen w-full flex-col items-center justify-center gap-4 bg-background text-foreground">
        <span className="flex h-20 w-20 items-center justify-center rounded-2xl bg-primary text-primary-foreground shadow-lg">
          <HardDriveDownload size={40} />
        </span>
        <div className="flex max-w-sm flex-col items-center gap-1 text-center">
          <p className="text-lg font-semibold">Something went wrong</p>
          <p className="text-sm text-muted-foreground">
            This page failed to load. This usually clears up after a reload.
          </p>
        </div>
        <Button onClick={() => window.location.reload()}>
          <RefreshCw size={14} /> Reload
        </Button>
      </div>
    );
  }
}
