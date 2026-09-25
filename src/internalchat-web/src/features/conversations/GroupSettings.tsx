/**
 * T119 — group creation and member management (US3 scenarios 1 and 3, FR-008).
 *
 * Two components, not one: creating a group and managing an existing one's members are different
 * moments in the same story, with different data available (a group being created has no member
 * list to query yet) and different audiences (anyone may create a group; only its admin may change
 * who is in it — enforced server-side by `.RequireConversationMembership(MembershipRole.Admin)`,
 * mirrored here only to avoid offering a control that would just come back refused).
 */

import { useCallback, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Search, Plus, X, Check } from 'lucide-react'

import type { EmployeeSummary } from '../../lib/api/client'
import type { MessagingClient } from '../../lib/api/messages'
import { conversationsQueryKey, membersQueryKey } from './queryKeys'

interface CreateGroupFormProps {
  readonly client: MessagingClient
  /** Called with the new conversation's id once creation succeeds, so the caller can open it. */
  readonly onCreated: (conversationId: string) => void
}

/** A form to start a new named group (US3 scenario 1). */
export function CreateGroupForm({ client, onCreated }: CreateGroupFormProps) {
  const queryClient = useQueryClient()

  const [name, setName] = useState('')
  const [query, setQuery] = useState('')
  const [candidates, setCandidates] = useState<readonly EmployeeSummary[]>([])
  const [selected, setSelected] = useState<ReadonlyMap<string, string>>(new Map())

  const search = useCallback(async () => {
    const trimmed = query.trim()
    setCandidates(trimmed.length >= 2 ? await client.searchEmployees(trimmed) : [])
  }, [client, query])

  const create = useMutation({
    mutationFn: () => client.createGroupConversation(name.trim(), [...selected.keys()]),
    onSuccess: (conversation) => {
      void queryClient.invalidateQueries({ queryKey: conversationsQueryKey })
      setName('')
      setSelected(new Map())
      onCreated(conversation.id)
    },
  })

  const toggle = useCallback((candidate: EmployeeSummary) => {
    setSelected((previous) => {
      const next = new Map(previous)
      if (next.has(candidate.id)) {
        next.delete(candidate.id)
      } else {
        next.set(candidate.id, candidate.displayName)
      }
      return next
    })
  }, [])

  const canSubmit = name.trim().length > 0 && selected.size > 0 && !create.isPending

  return (
    <form
      aria-label="Create a group"
      className="create-group-form"
      onSubmit={(event) => {
        event.preventDefault()
        if (canSubmit) {
          create.mutate()
        }
      }}
    >
      <div className="form-group">
        <label htmlFor="group-name">Group name</label>
        <input
          id="group-name"
          className="form-control"
          value={name}
          onChange={(event) => {
            setName(event.target.value)
          }}
        />
      </div>

      <div className="form-group">
        <label htmlFor="group-member-search">Find colleagues to add</label>
        <div className="search-input-group">
          <input
            id="group-member-search"
            className="form-control"
            value={query}
            onChange={(event) => {
              setQuery(event.target.value)
            }}
          />
          <button
            type="button"
            className="btn-secondary"
            onClick={() => {
              void search()
            }}
          >
            <Search size={16} /> Search
          </button>
        </div>
      </div>

      <ul aria-label="Search results" className="search-results-list">
        {candidates.map((candidate) => (
          <li key={candidate.id} className="search-result-item">
            <span>{candidate.displayName}</span>
            <button
              type="button"
              className={selected.has(candidate.id) ? "btn-danger-text btn-sm" : "btn-primary btn-sm"}
              onClick={() => {
                toggle(candidate)
              }}
            >
              {selected.has(candidate.id) ? <><X size={14}/> Remove</> : <><Plus size={14}/> Add</>}
            </button>
          </li>
        ))}
      </ul>

      {selected.size > 0 && (
        <p data-testid="selected-member-count" className="selected-count">{selected.size} member(s) selected</p>
      )}

      {create.isError && (
        <p role="alert" className="error-text">
          {create.error instanceof Error ? create.error.message : 'Could not create the group.'}
        </p>
      )}

      <button type="submit" className="btn-primary" disabled={!canSubmit} style={{ marginTop: '16px', width: '100%' }}>
        <Check size={16} /> Create group
      </button>
    </form>
  )
}

interface GroupMembersProps {
  readonly conversationId: string
  readonly client: MessagingClient
  readonly currentEmployeeId: string
}

/** Views and, for an admin, manages a group's active members (US3 scenario 3). */
export function GroupMembers({ conversationId, client, currentEmployeeId }: GroupMembersProps) {
  const queryClient = useQueryClient()
  const key = membersQueryKey(conversationId)

  const members = useQuery({ queryKey: key, queryFn: () => client.listMembers(conversationId) })

  const [query, setQuery] = useState('')
  const [candidates, setCandidates] = useState<readonly EmployeeSummary[]>([])

  const invalidate = useCallback(
    () => queryClient.invalidateQueries({ queryKey: key }),
    [queryClient, key],
  )

  const addMember = useMutation({
    mutationFn: (employeeId: string) => client.addMember(conversationId, employeeId),
    onSuccess: () => {
      void invalidate()
    },
  })

  const removeMember = useMutation({
    mutationFn: (employeeId: string) => client.removeMember(conversationId, employeeId),
    onSuccess: () => {
      void invalidate()
    },
  })

  const search = useCallback(async () => {
    const trimmed = query.trim()
    setCandidates(trimmed.length >= 2 ? await client.searchEmployees(trimmed) : [])
  }, [client, query])

  if (members.isPending) {
    return <p>Loading members…</p>
  }

  if (members.isError) {
    return <p role="alert">Could not load this group&apos;s members.</p>
  }

  // Server-enforced already — `.RequireConversationMembership(MembershipRole.Admin)` refuses
  // anyone else. Mirrored here only so the UI does not offer a control that would just come back
  // refused; the real check is the one that matters.
  const isAdmin = members.data.some(
    (member) => member.employee.id === currentEmployeeId && member.role === 'admin',
  )

  const memberIds = new Set(members.data.map((member) => member.employee.id))

  return (
    <section aria-label="Group members" className="group-members-panel">
      <div className="group-members-list-wrapper">
        <h3 className="group-members-title">Members</h3>
        <ul className="group-members-list">
          {members.data.map((member) => (
            <li key={member.employee.id} data-testid="group-member" className="group-member-item">
              <span className="group-member-name">{member.employee.displayName}</span>
              <span className="group-member-role"> ({member.role})</span>

              {isAdmin && member.employee.id !== currentEmployeeId && (
                <button
                  type="button"
                  className="btn-danger btn-sm"
                  onClick={() => {
                    removeMember.mutate(member.employee.id)
                  }}
                  disabled={removeMember.isPending}
                >
                  Remove
                </button>
              )}
            </li>
          ))}
        </ul>
      </div>

      {isAdmin && (
        <div aria-label="Add a member" className="add-member-section">
          <label htmlFor={`add-member-search-${conversationId}`} className="sr-only">Add a member</label>
          <div className="add-member-input-group">
            <input
              id={`add-member-search-${conversationId}`}
              className="add-member-input"
              placeholder="Add a member..."
              value={query}
              onChange={(event) => {
                setQuery(event.target.value)
              }}
            />
            <button
              type="button"
              className="btn-secondary btn-sm"
              onClick={() => {
                void search()
              }}
            >
              Search
            </button>
          </div>

          {candidates.length > 0 && (
            <ul aria-label="Search results" className="search-results-list">
              {candidates
                .filter((candidate) => !memberIds.has(candidate.id))
                .map((candidate) => (
                  <li key={candidate.id} className="search-result-item">
                    <span>{candidate.displayName}</span>
                    <button
                      type="button"
                      className="btn-primary btn-sm"
                      onClick={() => {
                        addMember.mutate(candidate.id)
                      }}
                      disabled={addMember.isPending}
                    >
                      Add
                    </button>
                  </li>
                ))}
            </ul>
          )}
        </div>
      )}
    </section>
  )
}
