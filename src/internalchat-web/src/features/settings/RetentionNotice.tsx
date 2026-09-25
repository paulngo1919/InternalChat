import { useQuery } from '@tanstack/react-query'
import type { ApiClient } from '../../lib/api/client'

interface RetentionNoticeProps {
  readonly api: ApiClient
}

/**
 * T208: Displays the active retention period to every employee (FR-053).
 */
export function RetentionNotice({ api }: RetentionNoticeProps) {
  const { data, isLoading, isError } = useQuery({
    queryKey: ['settings', 'retentionPolicy'],
    queryFn: api.getRetentionPolicy,
  })

  if (isLoading) {
    return (
      <section aria-labelledby="retention-heading">
        <h2 id="retention-heading">Data Retention</h2>
        <p>Loading retention policy…</p>
      </section>
    )
  }

  if (isError || !data) {
    return (
      <section aria-labelledby="retention-heading">
        <h2 id="retention-heading">Data Retention</h2>
        <p>Could not load retention policy.</p>
      </section>
    )
  }

  const dateOptions: Intl.DateTimeFormatOptions = { dateStyle: 'long' }
  const appliesFrom = new Date(data.appliesFrom).toLocaleDateString(undefined, dateOptions)
  const nextSweepAt = new Date(data.nextSweepAt).toLocaleDateString(undefined, dateOptions)

  return (
    <section aria-labelledby="retention-heading">
      <h2 id="retention-heading">Data Retention</h2>
      <p>
        Messages and files are retained for <strong>{data.retentionMonths} months</strong>. 
        Content sent before {appliesFrom} is permanently deleted. The next scheduled sweep will run on {nextSweepAt}.
      </p>
    </section>
  )
}
