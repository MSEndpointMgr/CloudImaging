import * as React from 'react';
import { formatDateTime, formatRelativeTime } from '../../lib/utils.ts';

// A relative label ("4 min ago") is wrong the moment it is painted and only gets more wrong the
// longer a page stays open — and several of these tables are left open as monitoring views. One
// shared ticker re-renders every mounted instance instead of each row owning a timer, so a
// 200-row table costs one interval rather than 200. The interval only exists while something is
// subscribed.
const listeners = new Set<() => void>();
let ticker: ReturnType<typeof setInterval> | null = null;
let tick = 0;

function subscribe(onStoreChange: () => void): () => void {
  listeners.add(onStoreChange);
  ticker ??= setInterval(() => {
    tick += 1;
    listeners.forEach((listener) => listener());
  }, 30_000);

  return () => {
    listeners.delete(onStoreChange);
    if (listeners.size === 0 && ticker) {
      clearInterval(ticker);
      ticker = null;
    }
  };
}

function getSnapshot(): number {
  return tick;
}

interface RelativeTimeProps {
  /** ISO-8601 timestamp. */
  value: string;
  className?: string;
}

/**
 * Renders how long ago something happened, with the exact timestamp available on hover and to
 * assistive technology. Use for "when did this happen" columns; keep the absolute form
 * (`formatDateTime`) in reports and exports, where the precise instant is the point.
 */
export function RelativeTime({ value, className }: RelativeTimeProps): React.ReactElement {
  React.useSyncExternalStore(subscribe, getSnapshot, getSnapshot);

  const absolute = formatDateTime(value);

  return (
    <time dateTime={value} title={absolute} className={className}>
      {formatRelativeTime(value)}
    </time>
  );
}
