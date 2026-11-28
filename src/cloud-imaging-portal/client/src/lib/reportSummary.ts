/**
 * Bar segment and legend dot colours for the report summaries. They match the dots of the badges in
 * the report tables, in both themes, so a value reads the same colour in the summary and the rows.
 */
export const SUMMARY_COLORS = {
  success: 'bg-emerald-600 dark:bg-emerald-400',
  negative: 'bg-red-600 dark:bg-red-400',
  warning: 'bg-amber-600 dark:bg-amber-400',
  muted: 'bg-muted-foreground',
  info: 'bg-blue-600 dark:bg-blue-400',
  violet: 'bg-violet-600 dark:bg-violet-400',
  cyan: 'bg-cyan-600 dark:bg-cyan-400',
} as const;

/** Whole-percent share of a total, or null when the total is 0. */
export function share(count: number, total: number): number | null {
  return total > 0 ? Math.round((count * 100) / total) : null;
}

/** "1 location", "3 locations". */
export function plural(count: number, one: string, many: string): string {
  return `${count} ${count === 1 ? one : many}`;
}

/** Keeps "14 new" on one line when a summary line wraps between its comma-separated parts. */
export function keepTogether(text: string): string {
  return text.replace(/ /g, '\u00a0');
}
