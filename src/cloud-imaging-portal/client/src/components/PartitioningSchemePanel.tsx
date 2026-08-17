import { useState, useEffect } from 'react';
import { ArrowUp, ArrowDown, HardDrive } from 'lucide-react';
import { apiFetch } from '../lib/apiClient.ts';
import { Button, type ButtonStatus } from './ui/button.tsx';
import { Input } from './ui/input.tsx';
import { Label } from './ui/label.tsx';
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from './ui/card.tsx';
import { useToast } from '../context/toastContext.tsx';

type PartitionType = 'EfiSystem' | 'Msr' | 'Windows' | 'Recovery';

interface PartitionDefinition {
  partitionType: PartitionType;
  sizeMb: number;
  order: number;
}

interface PartitioningScheme {
  partitions: PartitionDefinition[];
}

const PARTITION_LABELS: Record<PartitionType, string> = {
  EfiSystem: 'EFI System Partition (ESP)',
  Msr: 'Microsoft Reserved (MSR)',
  Windows: 'Windows',
  Recovery: 'Recovery (WinRE)',
};

const PARTITION_DESCRIPTIONS: Record<PartitionType, string> = {
  EfiSystem: 'Holds the UEFI boot loader (FAT32). Microsoft minimum: 100 MB (300 MB on 4K-sector drives).',
  Msr: 'Reserved by Windows for internal use. No drive letter or filesystem. Microsoft minimum: 16 MB (fixed).',
  Windows: 'The main OS partition. Always fills whatever space remains on the disk.',
  Recovery: 'Holds the WinRE recovery image. Microsoft recommends 990 MB (winre.wim is typically 500-700 MB).',
};

/**
 * Microsoft's documented recommended size for each partition (`null` for Windows, which always
 * fills whatever space remains). Source: Microsoft Learn, "UEFI/GPT-based hard drive partitions".
 */
const PARTITION_RECOMMENDED_MB: Record<PartitionType, number | null> = {
  EfiSystem: 100,
  Msr: 16,
  Windows: null,
  Recovery: 990,
};

const DEFAULT_SCHEME: PartitioningScheme = {
  partitions: [
    { partitionType: 'EfiSystem', sizeMb: 100, order: 0 },
    { partitionType: 'Msr', sizeMb: 16, order: 1 },
    { partitionType: 'Windows', sizeMb: 0, order: 2 },
    { partitionType: 'Recovery', sizeMb: 990, order: 3 },
  ],
};

function sortedByOrder(scheme: PartitioningScheme): PartitionDefinition[] {
  return [...scheme.partitions].sort((a, b) => a.order - b.order);
}

function schemeEquals(a: PartitioningScheme, b: PartitioningScheme): boolean {
  const pa = sortedByOrder(a);
  const pb = sortedByOrder(b);
  if (pa.length !== pb.length) return false;
  return pa.every((p, i) => p.partitionType === pb[i]?.partitionType && p.sizeMb === pb[i]?.sizeMb);
}

/**
 * Admin-configurable disk partitioning scheme: sizes and order for the four fixed,
 * well-known UEFI-bootable partition types. Applies globally to future imaging sessions only.
 */
export function PartitioningSchemePanel(): React.ReactElement {
  const { notify, update } = useToast();
  const [scheme, setScheme] = useState<PartitioningScheme>(DEFAULT_SCHEME);
  const [savedScheme, setSavedScheme] = useState<PartitioningScheme>(DEFAULT_SCHEME);
  const [loading, setLoading] = useState(true);
  const [saveStatus, setSaveStatus] = useState<ButtonStatus>('idle');
  const isDirty = !schemeEquals(scheme, savedScheme);

  useEffect(() => {
    void (async () => {
      setLoading(true);
      try {
        const res = await apiFetch('/api/partitioning-scheme', { credentials: 'include' });
        if (res.ok) {
          const data = await res.json() as PartitioningScheme;
          setScheme(data);
          setSavedScheme(data);
        }
      } catch { /* use defaults */ }
      finally { setLoading(false); }
    })();
  }, []);

  const partitions = sortedByOrder(scheme);

  const updateSize = (partitionType: PartitionType, sizeMb: number) => {
    setScheme(s => ({
      partitions: s.partitions.map(p => p.partitionType === partitionType ? { ...p, sizeMb } : p),
    }));
  };

  const move = (index: number, direction: -1 | 1) => {
    const target = index + direction;
    if (target < 0 || target >= partitions.length) return;
    const reordered = [...partitions];
    const tmp = reordered[index];
    reordered[index] = reordered[target]!;
    reordered[target] = tmp;
    setScheme({ partitions: reordered.map((p, i) => ({ ...p, order: i })) });
  };

  const save = async () => {
    setSaveStatus('loading');
    const toastId = notify({ status: 'loading', title: 'Saving partitioning scheme…' });
    try {
      const res = await apiFetch('/api/partitioning-scheme', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        credentials: 'include',
        body: JSON.stringify(scheme),
      });
      if (res.ok || res.status === 204) {
        setSavedScheme(scheme);
        setSaveStatus('success');
        update(toastId, {
          status: 'success',
          title: 'Partitioning scheme saved',
          description: 'New device sessions will use this layout.',
        });
      } else {
        let description = 'Failed to save partitioning scheme.';
        try { description = await res.text() || description; } catch { /* keep default */ }
        setSaveStatus('error');
        update(toastId, { status: 'error', title: 'Could not save partitioning scheme', description });
      }
    } catch {
      setSaveStatus('error');
      update(toastId, {
        status: 'error',
        title: 'Could not save partitioning scheme',
        description: 'A network error occurred. Please try again.',
      });
    } finally {
      setTimeout(() => setSaveStatus('idle'), 1600);
    }
  };

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
            Recovery first (<code className="text-[11px]">reagentc /disable</code>, then resize).
          </p>
        </CardHeader>
        <CardContent className="space-y-4">
          {partitions.map((p, index) => (
            <div key={p.partitionType} className="flex items-center gap-4 rounded-md border border-border p-3">
              <div className="flex flex-col gap-1">
                <Button
                  type="button"
                  variant="ghost"
                  size="icon"
                  disabled={index === 0}
                  onClick={() => move(index, -1)}
                  aria-label={`Move ${PARTITION_LABELS[p.partitionType]} up`}
                >
                  <ArrowUp className="h-4 w-4" />
                </Button>
                <Button
                  type="button"
                  variant="ghost"
                  size="icon"
                  disabled={index === partitions.length - 1}
                  onClick={() => move(index, 1)}
                  aria-label={`Move ${PARTITION_LABELS[p.partitionType]} down`}
                >
                  <ArrowDown className="h-4 w-4" />
                </Button>
              </div>
              <div className="flex-1 space-y-1">
                <Label>{PARTITION_LABELS[p.partitionType]}</Label>
                <p className="text-xs text-muted-foreground">{PARTITION_DESCRIPTIONS[p.partitionType]}</p>
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
                          <button
                            type="button"
                            className="underline underline-offset-2 hover:text-foreground"
                            onClick={() => updateSize(p.partitionType, PARTITION_RECOMMENDED_MB[p.partitionType]!)}
                          >
                            Use recommended
                          </button>
                        )}
                      </p>
                    )}
                  </>
                )}
              </div>
            </div>
          ))}
        </CardContent>
      </Card>

      <div className="flex items-center gap-3">
        <Button
          onClick={save}
          status={saveStatus}
          variant={isDirty || saveStatus !== 'idle' ? 'default' : 'secondary'}
          disabled={saveStatus === 'loading' || (!isDirty && saveStatus === 'idle')}
        >
          Save Partitioning Scheme
        </Button>
        {isDirty && saveStatus === 'idle' && (
          <p className="text-xs text-muted-foreground">You have unsaved changes.</p>
        )}
      </div>
    </div>
  );
}
