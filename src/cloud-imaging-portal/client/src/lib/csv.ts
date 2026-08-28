/**
 * Minimal client-side CSV export utility (Reports feature). No existing CSV/export
 * utility exists elsewhere in the client — this is the first one, kept intentionally
 * small (escaping + a Blob download trigger) rather than pulling in a dependency.
 */

/** Escapes a single CSV field per RFC 4180 (quotes any value containing a comma, quote, or newline). */
function escapeCsvField(value: string | number | boolean | null | undefined): string {
  if (value === null || value === undefined) return '';
  const str = typeof value === 'string' ? value : String(value);
  if (/[",\n\r]/.test(str)) {
    return `"${str.replace(/"/g, '""')}"`;
  }
  return str;
}

export interface CsvColumn<T> {
  header: string;
  accessor: (row: T) => string | number | boolean | null | undefined;
}

/** Builds an RFC 4180 CSV string (with header row) from `rows` using the given column definitions. */
export function toCsv<T>(rows: T[], columns: CsvColumn<T>[]): string {
  const headerLine = columns.map(c => escapeCsvField(c.header)).join(',');
  const lines = rows.map(row => columns.map(c => escapeCsvField(c.accessor(row))).join(','));
  return [headerLine, ...lines].join('\r\n');
}

/** Triggers a browser download of `content` as a file named `filename`. */
export function downloadBlob(filename: string, content: string, mimeType = 'text/csv;charset=utf-8;'): void {
  const blob = new Blob([content], { type: mimeType });
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = filename;
  document.body.appendChild(link);
  link.click();
  document.body.removeChild(link);
  URL.revokeObjectURL(url);
}
