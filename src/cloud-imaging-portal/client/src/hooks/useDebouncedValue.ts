import * as React from 'react';

/**
 * Delays a rapidly changing value, so work keyed off it runs once the user pauses.
 *
 * Report search filters on every keystroke. That is cheap against a few hundred client-side rows
 * and will not be once a report filters server side, so the debounce belongs here rather than at
 * each call site.
 */
export function useDebouncedValue<T>(value: T, delayMs = 200): T {
  const [debounced, setDebounced] = React.useState(value);

  React.useEffect(() => {
    const timer = setTimeout(() => setDebounced(value), delayMs);
    return () => clearTimeout(timer);
  }, [value, delayMs]);

  return debounced;
}
