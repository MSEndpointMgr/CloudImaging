import { Component, type ErrorInfo, type ReactNode } from 'react';
import { RouteErrorScreen } from './RouteErrorScreen.tsx';

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

    return <RouteErrorScreen />;
  }
}
