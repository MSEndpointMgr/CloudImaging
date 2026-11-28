import { useEffect, useMemo, useState } from 'react';
import { FileDown, HardDrive } from 'lucide-react';
import { apiFetchWithRetry } from '../lib/apiClient.ts';
import { formatDateTime } from '../lib/utils.ts';
import { toCsv, downloadBlob } from '../lib/csv.ts';
import { formatImageVersion, formatOsImageInventoryName } from '../lib/imageInventoryFormatting.ts';
import { BOOT_IMAGE_STAGE_LABELS, bootImageStage } from '../lib/bootImageStage.ts';
import { SUMMARY_COLORS, plural } from '../lib/reportSummary.ts';
import { sortRows, useSort } from '../lib/tableSort.ts';
import { Button } from '../components/ui/button.tsx';
import { ReportSummary } from '../components/ReportSummary.tsx';
import { SortableHead } from '../components/ui/sortable-head.tsx';
import { TableSkeletonRows } from '../components/ui/skeleton.tsx';
import { Badge, type BadgeProps } from '../components/ui/badge.tsx';
import { EmptyState } from '../components/ui/empty-state.tsx';
import { Table, TableHeader, TableBody, TableRow, TableCell } from '../components/ui/table.tsx';

interface OsImage {
  imageId: string;
  name: string;
  version: string;
  sizeBytes: number;
  uploadedAt: string;
  isInUse: boolean;
}

interface BootImage {
  bootImageId: string;
  version: string;
  createdAt: string;
  sizeBytes: number;
  architecture?: 'x64' | 'arm64';
  isLatestPublished: boolean;
  isProduction?: boolean;
  isActive: boolean;
}

interface RecoveryImage {
  recoveryImageId: string;
  version: string;
  sizeBytes: number;
  uploadedAt: string;
  isLatestPublished: boolean;
  isActive: boolean;
}

type Catalog = 'OS Image' | 'Boot Image' | 'Recovery Image';

// Catalogs are not outcomes, so they take colours of their own rather than the status colours.
const CATALOGS: readonly { catalog: Catalog; label: string; singular: string; colorClassName: string }[] = [
  { catalog: 'OS Image', label: 'OS images', singular: 'OS image', colorClassName: SUMMARY_COLORS.info },
  { catalog: 'Boot Image', label: 'Boot images', singular: 'Boot image', colorClassName: SUMMARY_COLORS.violet },
  { catalog: 'Recovery Image', label: 'Recovery images', singular: 'Recovery image', colorClassName: SUMMARY_COLORS.cyan },
];

interface InventoryRow {
  catalog: Catalog;
  id: string;
  name: string;
  sizeBytes: number;
  date: string;
  statusLabel: string;
  statusVariant: BadgeProps['variant'];
  statusDot: boolean;
}

function fmtSize(bytes: number): string {
  return `${(bytes / 1_073_741_824).toFixed(2)} GB`;
}

function ageDays(iso: string): number {
  return Math.max(0, Math.floor((Date.now() - new Date(iso).getTime()) / 86_400_000));
}

/**
 * Image Inventory report (Reports feature): combined view of the OS image, boot image, and
 * recovery image catalogs: age, size, and usage status. Reuses the existing live catalog
 * endpoints as-is (catalogs aren't purged, so no history store is needed for this report).
 */
export default function ReportImageInventoryPage(): React.ReactElement {
  const [rows, setRows] = useState<InventoryRow[] | null>(null);
  // Rows arrive grouped by catalog, which is an artefact of fetching three endpoints rather than
  // an order anyone asked for. Newest first matches the image catalog pages.
  const [sort, toggleSort] = useSort<'catalog' | 'name' | 'size' | 'date' | 'status'>({ key: 'date', dir: 'desc' });

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const [osRes, bootRes, recRes] = await Promise.all([
          apiFetchWithRetry('/api/images', { credentials: 'include' }),
          apiFetchWithRetry('/api/boot-images', { credentials: 'include' }),
          apiFetchWithRetry('/api/recovery-images', { credentials: 'include' }),
        ]);
        const osImages = osRes.ok ? await osRes.json() as OsImage[] : [];
        const bootImages = bootRes.ok ? await bootRes.json() as BootImage[] : [];
        const recoveryImages = recRes.ok ? await recRes.json() as RecoveryImage[] : [];
        if (cancelled) return;

        // Status label/variant/dot mirrors the exact convention used on the OS/Boot/Recovery
        // Image catalog pages, so this report's pills look identical to the ones an operator
        // already knows from those pages instead of introducing a one-off wording/style.
        const combined: InventoryRow[] = [
          ...osImages.map((img): InventoryRow => ({
            catalog: 'OS Image', id: img.imageId, name: formatOsImageInventoryName(img.name, img.version),
            sizeBytes: img.sizeBytes, date: img.uploadedAt,
            statusLabel: img.isInUse ? 'In Use' : 'Available',
            statusVariant: img.isInUse ? 'info' : 'success', statusDot: true,
          })),
          ...bootImages.map((img): InventoryRow => ({
            // "Latest" is per architecture, so the architecture disambiguates two Latest rows.
            catalog: 'Boot Image', id: img.bootImageId, name: `${formatImageVersion(img.version)} (${img.architecture === 'arm64' ? 'ARM64' : 'x64'})`,
            sizeBytes: img.sizeBytes, date: img.createdAt,
            statusLabel: BOOT_IMAGE_STAGE_LABELS[bootImageStage(img)],
            statusVariant: img.isLatestPublished ? 'info' : bootImageStage(img) === 'preProduction' ? 'warning' : 'muted',
            statusDot: true,
          })),
          ...recoveryImages.map((img): InventoryRow => ({
            catalog: 'Recovery Image', id: img.recoveryImageId, name: formatImageVersion(img.version),
            sizeBytes: img.sizeBytes, date: img.uploadedAt,
            statusLabel: img.isLatestPublished ? 'Latest' : img.isActive ? 'Active' : 'Inactive',
            statusVariant: img.isLatestPublished ? 'info' : 'muted', statusDot: img.isLatestPublished || img.isActive,
          })),
        ];
        setRows(combined);
      } catch {
        if (!cancelled) setRows([]);
      }
    })();
    return () => { cancelled = true; };
  }, []);

  const totals = useMemo(() => {
    const list = rows ?? [];
    return {
      size: list.reduce((sum, r) => sum + r.sizeBytes, 0),
      largest: list.reduce((max, r) => Math.max(max, r.sizeBytes), 0),
      oldest: list.reduce<InventoryRow | null>((oldest, r) => (!oldest || Date.parse(r.date) < Date.parse(oldest.date) ? r : oldest), null),
      catalogs: CATALOGS.map(c => {
        const inCatalog = list.filter(r => r.catalog === c.catalog);
        return { ...c, count: inCatalog.length, size: inCatalog.reduce((sum, r) => sum + r.sizeBytes, 0) };
      }),
    };
  }, [rows]);

  const sorted = useMemo(() => sortRows(rows ?? [], sort, {
    catalog: (r: InventoryRow) => r.catalog,
    name:    (r: InventoryRow) => r.name,
    size:    (r: InventoryRow) => r.sizeBytes,
    date:    (r: InventoryRow) => new Date(r.date).getTime(),
    status:  (r: InventoryRow) => r.statusLabel,
  }), [rows, sort]);

  const handleExport = () => {
    if (sorted.length === 0) return;
    const csv = toCsv(sorted, [
      { header: 'Catalog', accessor: r => r.catalog },
      { header: 'Name', accessor: r => r.name },
      { header: 'Size (bytes)', accessor: r => r.sizeBytes },
      { header: 'Date', accessor: r => r.date },
      { header: 'Age (days)', accessor: r => ageDays(r.date) },
      { header: 'Status', accessor: r => r.statusLabel },
    ]);
    downloadBlob('image-inventory.csv', csv);
  };

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-end">
        <Button variant="outline" onClick={handleExport} disabled={!rows || rows.length === 0}>
          <FileDown /> Export CSV
        </Button>
      </div>

      <ReportSummary
        loading={rows === null}
        total={rows?.length ?? 0}
        noun={['image', 'images']}
        parts={totals.catalogs.map(c => ({
          key: c.catalog,
          label: c.label,
          count: c.count,
          colorClassName: c.colorClassName,
          detail: c.count > 0 ? fmtSize(c.size) : undefined,
        }))}
        emptyText="No OS, boot or recovery images have been uploaded."
        figures={[
          {
            label: 'Total size',
            value: totals.oldest ? fmtSize(totals.size) : null,
            detail: totals.oldest ? `Largest image ${fmtSize(totals.largest)}` : 'No images uploaded',
          },
          {
            label: 'Oldest image',
            value: totals.oldest ? plural(ageDays(totals.oldest.date), 'day', 'days') : null,
            detail: totals.oldest
              ? `${CATALOGS.find(c => c.catalog === totals.oldest?.catalog)?.singular ?? totals.oldest.catalog}, uploaded ${new Date(totals.oldest.date).toLocaleDateString(undefined, { dateStyle: 'medium' })}`
              : 'No images uploaded',
          },
        ]}
      />

      <div className="rounded-md border border-border overflow-hidden">
      <Table>
        <TableHeader>
          <TableRow className="hover:bg-transparent">
            <SortableHead label="Catalog" sortKey="catalog" sort={sort} onSort={toggleSort} />
            <SortableHead label="Name" sortKey="name" sort={sort} onSort={toggleSort} />
            <SortableHead label="Size" sortKey="size" sort={sort} onSort={toggleSort} />
            <SortableHead label="Date" sortKey="date" sort={sort} onSort={toggleSort} />
            <SortableHead label="Age" sortKey="date" sort={sort} onSort={toggleSort} />
            <SortableHead label="Status" sortKey="status" sort={sort} onSort={toggleSort} />
          </TableRow>
        </TableHeader>
        <TableBody>
          {rows === null && <TableSkeletonRows columns={6} rows={4} />}
          {rows?.length === 0 && (
            <TableRow>
              <TableCell colSpan={6}>
                <EmptyState icon={HardDrive} title="No images in any catalog yet."
                  description="OS images, boot images, and recovery images will appear here once uploaded." />
              </TableCell>
            </TableRow>
          )}
          {sorted.map(r => (
            <TableRow key={`${r.catalog}-${r.id}`}>
              <TableCell>{r.catalog}</TableCell>
              <TableCell>{r.name}</TableCell>
              <TableCell>{fmtSize(r.sizeBytes)}</TableCell>
              <TableCell>{formatDateTime(r.date)}</TableCell>
              <TableCell>{ageDays(r.date)}d</TableCell>
              <TableCell>
                <Badge variant={r.statusVariant} dot={r.statusDot}>{r.statusLabel}</Badge>
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
      </div>
    </div>
  );
}
