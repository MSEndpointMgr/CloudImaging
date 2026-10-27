import { ArrowUp, ArrowDown, HardDrive } from 'lucide-react';
import { Button } from './ui/button.tsx';
import { Input } from './ui/input.tsx';
import { Label } from './ui/label.tsx';
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from './ui/card.tsx';
import { UndoChangeButton } from './ui/unsaved-change.tsx';
import { cn } from '../lib/utils.ts';
import { unsavedRail } from '../lib/unsavedChanges.ts';
import { PARTITION_LABELS, positionOf, sortedByOrder, undoPartition, type PartitionType, type PartitioningScheme } from '../lib/partitioningScheme.ts';

const PARTITION_DESCRIPTIONS: Record<PartitionType, string> = {
  EfiSystem: 'Holds the UEFI boot loader (FAT32). Microsoft requires at least 200 MB, or 300 MB on 4K-native drives. 500 MB is the common enterprise default and leaves headroom for OEM firmware capsule updates and multiple boot loaders.',
  Msr: 'Reserved by Windows for internal use. No drive letter or filesystem. Microsoft minimum: 16 MB (fixed).',
  Windows: 'The main OS partition. Always fills whatever space remains on the disk.',
  Recovery: 'Holds the WinRE recovery image. Microsoft recommends 990 MB (winre.wim is typically 500-700 MB).',
};

/**
 * Recommended size for each partition (`null` for Windows, which always fills whatever space
 * remains).
 *
 * ESP is set to 500 MB rather than the 100 MB that Windows Setup creates interactively. Microsoft's
 * current OEM guidance puts the floor at 200 MB (300 MB on 4K-native drives), and their own
 * CreatePartitions-UEFI.txt sample uses `create partition efi size=200`. 500 MB is what MDT and
 * Configuration Manager task sequences have long defaulted to, and what most enterprises and OEMs
 * ship, because the ESP also has to absorb OEM firmware capsule updates and any additional boot
 * loaders over the life of the device. The extra few hundred megabytes cost nothing on a modern
 * disk, whereas an undersized ESP is painful to grow after the fact.
 *
 * MSR and Recovery follow Microsoft Learn, "UEFI/GPT-based hard drive partitions" (16 MB, and a
 * 990 MB recommended minimum for custom diskpart-created recovery partitions).
 */
const PARTITION_RECOMMENDED_MB: Record<PartitionType, number | null> = {
  EfiSystem: 500,
  Msr: 16,
  Windows: null,
  Recovery: 990,
};

/**
 * Admin-configurable disk partitioning scheme: sizes and order for the four fixed,
 * well-known UEFI-bootable partition types. Applies globally to future imaging sessions only.
 */
export function PartitioningSchemePanel({ scheme, savedScheme, loading, onChange }: {
  scheme: PartitioningScheme;
  savedScheme: PartitioningScheme;
  loading: boolean;
  onChange: (next: PartitioningScheme) => void;
}): React.ReactElement {
  const partitions = sortedByOrder(scheme);

  const updateSize = (partitionType: PartitionType, sizeMb: number) => {
    onChange({
      partitions: scheme.partitions.map(p => p.partitionType === partitionType ? { ...p, sizeMb } : p),
    });
  };

  const move = (index: number, direction: -1 | 1) => {
    const target = index + direction;
    if (target < 0 || target >= partitions.length) return;
    const reordered = [...partitions];
    const tmp = reordered[index];
    reordered[index] = reordered[target]!;
    reordered[target] = tmp;
    onChange({ partitions: reordered.map((p, i) => ({ ...p, order: i })) });
  };

  const undo = (type: PartitionType) => onChange(undoPartition(scheme, savedScheme, type));

  if (loading) return <p className="text-muted-foreground">Loading…</p>;

  return (
    <div className="space-y-6">
      <Card>
        <CardHeader>
          <div className="flex items-center gap-2">
            <HardDrive className="h-5 w-5 text-primary" aria-hidden="true" />
            <CardTitle>Disk partitioning scheme</CardTitle>
          </div>
          <CardDescription>
            Controls the size and order of partitions created on the target disk. Applies to
            future sessions only; sessions already in progress keep their original scheme.
          </CardDescription>
          <p className="text-xs text-muted-foreground">
            This is Microsoft's default UEFI layout. Recovery must stay last so a future WinRE
            update can grow it (by shrinking Windows). Extending Windows later requires removing
            Recovery first (<code className="text-xs">reagentc /disable</code>, then resize).
          </p>
        </CardHeader>
        <CardContent className="space-y-4">
          {partitions.map((p, index) => {
            const before = savedScheme.partitions.find((s) => s.partitionType === p.partitionType);
            const changed = !!before && (before.sizeMb !== p.sizeMb || positionOf(savedScheme, p.partitionType) !== index);
            return (
            <div key={p.partitionType} className={cn('flex items-center gap-4 rounded-md border border-border p-3', unsavedRail(changed))}>
              <div className="flex flex-col gap-1">
                <Button
                  type="button"
                  variant="ghost"
                  size="icon"
                  disabled={index === 0}
                  onClick={() => move(index, -1)}
                  aria-label={`Move ${PARTITION_LABELS[p.partitionType]} up`}
                >
                 <ArrowUp />
                </Button>
                <Button
                  type="button"
                  variant="ghost"
                  size="icon"
                  disabled={index === partitions.length - 1}
                  onClick={() => move(index, 1)}
                  aria-label={`Move ${PARTITION_LABELS[p.partitionType]} down`}
                >
                 <ArrowDown />
                </Button>
              </div>
              <div className="flex-1 space-y-1">
                <Label>{PARTITION_LABELS[p.partitionType]}</Label>
                <p className="text-sm text-muted-foreground">{PARTITION_DESCRIPTIONS[p.partitionType]}</p>
              </div>
              <div className="w-48 space-y-1">
                {p.partitionType === 'Windows' ? (
                  <p className="text-sm text-muted-foreground">Fills remaining space</p>
                ) : (
                  <>
                    <div className="flex items-center gap-2">
                      <Input
                        type="number"
                        min={1}
                        max={p.partitionType === 'EfiSystem' || p.partitionType === 'Msr' ? 2048 : 51200}
                        value={p.sizeMb}
                        onChange={e => updateSize(p.partitionType, Number(e.target.value))}
                        className="w-24"
                      />
                      <span className="text-xs text-muted-foreground">MB</span>
                    </div>
                    {PARTITION_RECOMMENDED_MB[p.partitionType] !== null && (
                      <p className="text-xs text-muted-foreground">
                        Recommended: {PARTITION_RECOMMENDED_MB[p.partitionType]} MB.{' '}
                        {p.sizeMb !== PARTITION_RECOMMENDED_MB[p.partitionType] && (
                          <Button
                            variant="link"
                            type="button"
                            className="underline underline-offset-2 hover:text-foreground"
                            onClick={() => updateSize(p.partitionType, PARTITION_RECOMMENDED_MB[p.partitionType]!)}
                          >
                            Use recommended
                          </Button>
                        )}
                      </p>
                    )}
                  </>
                )}
              </div>
              <div className="w-8 shrink-0">
                {changed && <UndoChangeButton label={PARTITION_LABELS[p.partitionType]} onUndo={() => undo(p.partitionType)} />}
              </div>
            </div>
            );
          })}
        </CardContent>
      </Card>
    </div>
  );
}
