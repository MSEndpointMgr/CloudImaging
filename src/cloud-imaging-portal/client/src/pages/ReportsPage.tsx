import { Link } from 'react-router-dom';
import { PieChart, HardDrive, AlertOctagon, ArrowRight, BarChart3, MapPin } from 'lucide-react';
import { Card, CardContent } from '../components/ui/card.tsx';

interface ReportCard {
  to: string;
  title: string;
  description: string;
  icon: React.ReactNode;
}

const REPORT_CARDS: ReportCard[] = [
  {
    to: '/reports/device-outcomes',
    title: 'Device Outcomes',
    description: 'Breakdown of completed, failed, expired, and not-authorized imaging sessions over a date range.',
    icon: <PieChart size={18} />,
  },
  {
    to: '/reports/location-statistics',
    title: 'Location Statistics',
    description: 'Outcome counts, success rate, duration statistics, and device details for a selected location.',
    icon: <MapPin size={18} />,
  },
  {
    to: '/reports/image-inventory',
    title: 'Image Inventory',
    description: 'Age, size, and usage of every OS image, boot image, and recovery image in the catalog.',
    icon: <HardDrive size={18} />,
  },
  {
    to: '/reports/failures',
    title: 'Failure Detail',
    description: 'Drill into every failed, expired, or not-authorized session with its error detail and failed step.',
    icon: <AlertOctagon size={18} />,
  },
];

/**
 * Reports landing page: a card grid linking into each dedicated report (Reports feature).
 * Administrator or Reader only (see App.tsx RequireReportsAccess wrapper + Sidebar.tsx access flag).
 */
export default function ReportsPage(): React.ReactElement {
  return (
    <div className="space-y-6">
      <div className="flex items-center gap-2">
        <BarChart3 size={24} className="text-primary" />
        <h2 className="text-lg font-semibold">Reports</h2>
      </div>
      <p className="max-w-3xl text-sm text-muted-foreground">
        Detailed, exportable reports drawn from durably-retained session and catalog data.
        Select a report below to view it.
      </p>

      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
        {REPORT_CARDS.map(card => (
          <Link key={card.to} to={card.to} className="group">
            <Card className="h-full transition-colors hover:border-primary/50 hover:bg-accent/40">
              <CardContent className="flex h-full flex-col p-5">
                <div className="flex items-center gap-3">
                  <span className="flex h-9 w-9 items-center justify-center rounded-lg bg-primary/10 text-primary">
                    {card.icon}
                  </span>
                  <h3 className="text-sm font-semibold">{card.title}</h3>
                </div>
                <p className="mt-3 flex-1 text-sm text-muted-foreground">{card.description}</p>
                <span className="mt-4 inline-flex items-center gap-1 text-xs font-medium text-primary opacity-0 transition-opacity group-hover:opacity-100">
                  Open <ArrowRight size={16} />
                </span>
              </CardContent>
            </Card>
          </Link>
        ))}
      </div>
    </div>
  );
}
