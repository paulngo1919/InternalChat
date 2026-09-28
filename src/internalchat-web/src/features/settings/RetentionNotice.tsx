import { useQuery } from '@tanstack/react-query';
import { Database } from 'lucide-react';
import { Spinner } from '../../components/loading/Skeletons';
import type { ApiClient } from '../../lib/api/client';

interface RetentionNoticeProps {
  readonly api: ApiClient;
}

export function RetentionNotice({ api }: RetentionNoticeProps) {
  const { data, isLoading, isError } = useQuery({
    queryKey: ['settings', 'retentionPolicy'],
    queryFn: api.getRetentionPolicy,
  });

  if (isLoading) {
    return (
      <section aria-labelledby="retention-heading" className="settings-section">
        <h2 id="retention-heading" className="sidebar-section-title">
          <Database size={14} style={{ marginRight: '6px' }} />
          Data Retention
        </h2>
        <Spinner label="Loading retention policy…" />
      </section>
    );
  }

  if (isError || !data) {
    return (
      <section aria-labelledby="retention-heading" className="settings-section">
        <h2 id="retention-heading" className="sidebar-section-title">
          <Database size={14} style={{ marginRight: '6px' }} />
          Data Retention
        </h2>
        <p className="error-text">Could not load retention policy.</p>
      </section>
    );
  }

  const dateOptions: Intl.DateTimeFormatOptions = { dateStyle: 'long' };
  const appliesFrom = new Date(data.appliesFrom).toLocaleDateString(undefined, dateOptions);
  const nextSweepAt = new Date(data.nextSweepAt).toLocaleDateString(undefined, dateOptions);

  return (
    <section aria-labelledby="retention-heading" className="settings-section">
      <h2 id="retention-heading" className="sidebar-section-title">
        <Database size={14} style={{ marginRight: '6px' }} />
        Data Retention
      </h2>
      <p className="retention-notice-text">
        Messages and files are retained for <strong>{data.retentionMonths} months</strong>. Content
        sent before {appliesFrom} is permanently deleted. Next scheduled sweep runs on {nextSweepAt}.
      </p>
    </section>
  );
}
