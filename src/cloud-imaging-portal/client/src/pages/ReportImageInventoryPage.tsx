import { useEffect, useMemo, useState } from 'react';
import { FileDown, HardDrive } from 'lucide-react';
import { apiFetchWithRetry } from '../lib/apiClient.ts';
import { formatDateTime } from '../lib/utils.ts';
import { toCsv, downloadBlob } from '../lib/csv.ts';
import { Button } from '../components/ui/button.tsx';
import { Skeleton } from '../components/ui/skeleton.tsx';
import { Badge } from '../components/ui/badge.tsx';
import { EmptyState } from '../components/ui/empty-state.tsx';
import { Table, TableHeader, TableBody, TableRow, TableHead, TableCell } from '../components/ui/table.tsx';

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
  isLatestPublished: boolean;
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

interface InventoryRow {
  catalog: Catalog;
  id: string;
  name: string;
  sizeBytes: number;
  date: string;
  status: string;
}

function fmtSize(bytes: number): string {
  return `${(bytes / 1_073_741_824).toFixed(2)} GB`;
}

function ageDays(iso: string): number {
  return Math.max(0, Math.floor((Date.now() - new Date(iso).getTime()) / 86_400_000));
}

/**
 * Image Inventory report (Reports feature): combined view of the OS image, boot image, and
 * recovery image catalogs — age, size, and usage status. Reuses the existing live catalog
 * endpoints as-is (catalogs aren't purged, so no history store is needed for this report).
 */
export default function ReportImageInventoryPage(): React.ReactElement {
  const [rows, setRows] = useState<InventoryRow[] | null>(null);

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

        const combined: InventoryRow[] = [
          ...osImages.map((img): InventoryRow => ({
            catalog: 'OS Image', id: img.imageId, name: `${img.name} v${img.version}`,
            sizeBytes: img.sizeBytes, date: img.uploadedAt, status: img.isInUse ? 'In use' : 'Unused',
          })),
          ...bootImages.map((img): InventoryRow => ({
            catalog: 'Boot Image', id: img.bootImageId, name: `v${img.version}`,
            sizeBytes: img.sizeBytes, date: img.createdAt,
            status: img.isLatestPublished ? 'Latest published' : img.isActive ? 'Active' : 'Inactive',
          })),
          ...recoveryImages.map((img): InventoryRow => ({
            catalog: 'Recovery Image', id: img.recoveryImageId, name: `v${img.version}`,
            sizeBytes: img.sizeBytes, date: img.uploadedAt,
            status: img.isLatestPublished ? 'Latest published' : img.isActive ? 'Active' : 'Inactive',
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
    if (!rows) return null;
    return {
      count: rows.length,
      totalSize: rows.reduce((sum, r) => sum + r.sizeBytes, 0),
    };
  }, [rows]);

  const handleExport = () => {
    if (!rows || rows.length === 0) return;
    const csv = toCsv(rows, [
      { header: 'Catalog', accessor: r => r.catalog },
      { header: 'Name', accessor: r => r.name },
      { header: 'Size (bytes)', accessor: r => r.sizeBytes },
      { header: 'Date', accessor: r => r.date },
      { header: 'Age (days)', accessor: r => ageDays(r.date) },
      { header: 'Status', accessor: r => r.status },
    ]);
    downloadBlob('image-inventory.csv', csv);
  };

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between gap-3">
        <p className="text-sm text-muted-foreground">
          {totals ? `${totals.count} images, ${fmtSize(totals.totalSize)} total` : <Skeleton className="h-4 w-48" />}
        </p>
        <Button variant="secondary" onClick={handleExport} disabled={!rows || rows.length === 0}>
          <FileDown size={14} /> Export CSV
        </Button>
      </div>

      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>Catalog</TableHead>
            <TableHead>Name</TableHead>
            <TableHead>Size</TableHead>
            <TableHead>Date</TableHead>
            <TableHead>Age</TableHead>
            <TableHead>Status</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {rows === null && Array.from({ length: 4 }).map((_, i) => (
            <TableRow key={i}>
              <TableCell colSpan={6}><Skeleton className="h-5 w-full" /></TableCell>
            </TableRow>
          ))}
          {rows?.length === 0 && (
            <TableRow>
              <TableCell colSpan={6}>
                <EmptyState icon={HardDrive} title="No images in any catalog yet."
                  description="OS images, boot images, and recovery images will appear here once uploaded." />
              </TableCell>
            </TableRow>
          )}
          {rows?.map(r => (
            <TableRow key={`${r.catalog}-${r.id}`}>
              <TableCell>{r.catalog}</TableCell>
              <TableCell>{r.name}</TableCell>
              <TableCell>{fmtSize(r.sizeBytes)}</TableCell>
              <TableCell>{formatDateTime(r.date)}</TableCell>
              <TableCell>{ageDays(r.date)}d</TableCell>
              <TableCell>
                <Badge variant={r.status === 'In use' || r.status === 'Latest published' || r.status === 'Active' ? 'success' : 'muted'}>
                  {r.status}
                </Badge>
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </div>
  );
}
