/** One unsaved setting: what it is, and the tab or area it lives on when the page has several. */
export interface UnsavedChange {
  label: string;
  scope?: string;
}

/** "a", "a and b", "a, b and c". */
function joinAnd(items: string[]): string {
  if (items.length <= 1) return items.join('');
  return `${items.slice(0, -1).join(', ')} and ${items[items.length - 1]}`;
}

/** "1 unsaved change on Autopilot." or "3 unsaved changes." */
export function describeUnsavedChanges(changes: UnsavedChange[]): string {
  const count = changes.length;
  const scopes = [...new Set(changes.map((c) => c.scope).filter((s): s is string => Boolean(s)))];
  const noun = count === 1 ? 'unsaved change' : 'unsaved changes';
  return `${String(count)} ${noun}${scopes.length > 0 ? ` on ${joinAnd(scopes)}` : ''}.`;
}

/** Names what would be lost, for the leave dialog. Long lists are cut short rather than listed in full. */
export function describeLostChanges(changes: UnsavedChange[]): string {
  const labels = [...new Set(changes.map((c) => c.label))];
  if (labels.length === 1) return `${labels[0]} has not been saved.`;
  if (labels.length <= 3) return `${joinAnd(labels)} have not been saved.`;
  return `${labels.slice(0, 2).join(', ')} and ${String(labels.length - 2)} more have not been saved.`;
}

/**
 * The amber rail that marks a setting whose value differs from the saved one, so the save bar's
 * count can be traced to the rows that caused it.
 *
 * - `boxed`: a bordered row, card or table cell. The rail sits on its left edge, so nothing moves.
 * - `bare`: a row without a border. The rail sits in the card padding to its left.
 */
export function unsavedRail(changed: boolean, variant: 'boxed' | 'bare' = 'boxed'): string {
  if (!changed) return '';
  return variant === 'boxed'
    ? 'border-l-amber-500 shadow-[inset_2px_0_0_0_theme(colors.amber.500)]'
    : 'relative before:absolute before:-left-3 before:inset-y-0 before:w-0.5 before:rounded-full before:bg-amber-500';
}
