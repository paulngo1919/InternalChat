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
      onSubmit={(event) => {
        event.preventDefault()
        if (canSubmit) {
          create.mutate()
        }
      }}
    >
      <label htmlFor="group-name">Group name</label>
      <input
        id="group-name"
        value={name}
        onChange={(event) => {
          setName(event.target.value)
        }}
      />

      <label htmlFor="group-member-search">Find colleagues to add</label>
      <input
        id="group-member-search"
        value={query}
        onChange={(event) => {
          setQuery(event.target.value)
        }}
      />
      <button
        type="button"
        onClick={() => {
          void search()
        }}
      >
        Search
      </button>

      <ul aria-label="Search results">
        {candidates.map((candidate) => (
          <li key={candidate.id}>
            <span>{candidate.displayName}</span>
            <button
              type="button"
              onClick={() => {
                toggle(candidate)
              }}
            >
              {selected.has(candidate.id) ? 'Remove' : 'Add'}
            </button>
          </li>
        ))}
      </ul>

      {selected.size > 0 && (
        <p data-testid="selected-member-count">{selected.size} member(s) selected</p>
      )}

      {create.isError && (
        <p role="alert">
          {create.error instanceof Error ? create.error.message : 'Could not create the group.'}
        </p>
      )}

      <button type="submit" disabled={!canSubmit}>
        Create group
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
    <section aria-label="Group members">
      <ul>
        {members.data.map((member) => (
          <li key={member.employee.id} data-testid="group-member">
            <span>{member.employee.displayName}</span>
            <span> ({member.role})</span>

            {isAdmin && member.employee.id !== currentEmployeeId && (
              <button
                type="button"
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

      {isAdmin && (
        <div aria-label="Add a member">
          <label htmlFor={`add-member-search-${conversationId}`}>Add a member</label>
          <input
            id={`add-member-search-${conversationId}`}
            value={query}
            onChange={(event) => {
              setQuery(event.target.value)
            }}
          />
          <button
            type="button"
            onClick={() => {
              void search()
            }}
          >
            Search
          </button>

          <ul aria-label="Search results">
            {candidates
              .filter((candidate) => !memberIds.has(candidate.id))
              .map((candidate) => (
                <li key={candidate.id}>
                  <span>{candidate.displayName}</span>
                  <button
                    type="button"
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
        </div>
      )}
    </section>
  )
}
