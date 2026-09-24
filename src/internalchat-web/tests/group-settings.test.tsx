import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { CreateGroupForm, GroupMembers } from '../src/features/conversations/GroupSettings'
import type { EmployeeSummary } from '../src/lib/api/client'
import type { MemberResponse } from '../src/lib/api/messages'
import { aConversation, fakeMessagingClient, renderWithProviders } from './helpers'
import { at, pending } from './at'

/**
 * Group creation and member management (T119 — US3 scenarios 1 and 3, FR-008).
 *
 * <b>The admin check here is a convenience, not a control, and the tests are written that way.</b>
 * The server refuses a non-admin through `.RequireConversationMembership(MembershipRole.Admin)`
 * whatever this component renders. What these assertions protect is the other direction: that the
 * UI does not offer a button which would only ever come back refused, and — more importantly — that
 * it does not offer "remove" against the one person it must never be offered against, which is the
 * admin themselves. A group whose last admin removed themselves has nobody who can add anyone.
 */

const anEmployee = (id: string, displayName: string): EmployeeSummary => ({
  id,
  displayName,
  email: `${id}@example.test`,
  avatarUrl: null,
  status: 'active',
})

const aMember = (id: string, displayName: string, role: 'member' | 'admin' = 'member') => ({
  employee: anEmployee(id, displayName),
  role,
  joinedAt: '2026-01-01T00:00:00Z',
})

afterEach(cleanup)

describe('CreateGroupForm', () => {
  function renderForm(overrides: Partial<Parameters<typeof CreateGroupForm>[0]> = {}) {
    const client = overrides.client ?? fakeMessagingClient()
    const onCreated = vi.fn()

    renderWithProviders(
      <CreateGroupForm client={client} onCreated={onCreated} {...overrides} />,
    )

    return { client, onCreated }
  }

  it('refuses to submit without a name and at least one member', () => {
    renderForm()

    const submit = screen.getByRole('button', { name: 'Create group' })

    expect(submit).toBeDisabled()

    fireEvent.change(screen.getByLabelText('Group name'), { target: { value: 'Design' } })

    // A group with a name and nobody in it is a conversation with one participant, which the
    // server's validator refuses anyway.
    expect(submit).toBeDisabled()
  })

  it('searches only once the query is worth searching for', async () => {
    const { client } = renderForm()

    fireEvent.change(screen.getByLabelText('Find colleagues to add'), { target: { value: 'a' } })
    fireEvent.click(screen.getByRole('button', { name: 'Search' }))

    // One character matches most of a directory. The threshold keeps a company-wide scan off the
    // server for a query nobody meant to run yet.
    await waitFor(() => {
      expect(client.searchEmployees).not.toHaveBeenCalled()
    })

    fireEvent.change(screen.getByLabelText('Find colleagues to add'), { target: { value: ' an ' } })
    fireEvent.click(screen.getByRole('button', { name: 'Search' }))

    await waitFor(() => {
      expect(client.searchEmployees).toHaveBeenCalledWith('an')
    })
  })

  it('selects and deselects candidates, and counts them', async () => {
    const client = fakeMessagingClient({
      searchEmployees: vi.fn(() => Promise.resolve([anEmployee('e2', 'An Nguyen')])),
    })

    renderForm({ client })

    fireEvent.change(screen.getByLabelText('Find colleagues to add'), { target: { value: 'an' } })
    fireEvent.click(screen.getByRole('button', { name: 'Search' }))

    const add = await screen.findByRole('button', { name: 'Add' })

    fireEvent.click(add)

    expect(screen.getByTestId('selected-member-count')).toHaveTextContent('1 member(s) selected')

    // The same button toggles back, so a mis-click is one click to undo rather than a reload.
    fireEvent.click(screen.getByRole('button', { name: 'Remove' }))

    expect(screen.queryByTestId('selected-member-count')).not.toBeInTheDocument()
  })

  it('creates the group and hands the id back to the caller', async () => {
    const client = fakeMessagingClient({
      searchEmployees: vi.fn(() => Promise.resolve([anEmployee('e2', 'An Nguyen')])),
      createGroupConversation: vi.fn(() => Promise.resolve(aConversation({ id: 'new-c' }))),
    })

    const { onCreated } = renderForm({ client })

    fireEvent.change(screen.getByLabelText('Group name'), { target: { value: '  Design  ' } })
    fireEvent.change(screen.getByLabelText('Find colleagues to add'), { target: { value: 'an' } })
    fireEvent.click(screen.getByRole('button', { name: 'Search' }))

    fireEvent.click(await screen.findByRole('button', { name: 'Add' }))
    fireEvent.click(screen.getByRole('button', { name: 'Create group' }))

    await waitFor(() => {
      // Trimmed. A group named "  Design  " sorts and searches differently from "Design" for no
      // reason the person who typed it would understand.
      expect(client.createGroupConversation).toHaveBeenCalledWith('Design', ['e2'])
    })

    await waitFor(() => {
      expect(onCreated).toHaveBeenCalledWith('new-c')
    })
  })

  it('clears the form after a successful create', async () => {
    const client = fakeMessagingClient({
      searchEmployees: vi.fn(() => Promise.resolve([anEmployee('e2', 'An Nguyen')])),
    })

    renderForm({ client })

    fireEvent.change(screen.getByLabelText('Group name'), { target: { value: 'Design' } })
    fireEvent.change(screen.getByLabelText('Find colleagues to add'), { target: { value: 'an' } })
    fireEvent.click(screen.getByRole('button', { name: 'Search' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Add' }))
    fireEvent.click(screen.getByRole('button', { name: 'Create group' }))

    // Otherwise a second click creates a second identical group — and the form still looks ready
    // to submit, which invites exactly that.
    await waitFor(() => {
      expect(screen.getByLabelText('Group name')).toHaveValue('')
    })

    expect(screen.queryByTestId('selected-member-count')).not.toBeInTheDocument()
  })

  it('reports a refused create without clearing what was typed', async () => {
    const client = fakeMessagingClient({
      searchEmployees: vi.fn(() => Promise.resolve([anEmployee('e2', 'An Nguyen')])),
      createGroupConversation: vi.fn(() => Promise.reject(new Error('The API returned 422.'))),
    })

    renderForm({ client })

    fireEvent.change(screen.getByLabelText('Group name'), { target: { value: 'Design' } })
    fireEvent.change(screen.getByLabelText('Find colleagues to add'), { target: { value: 'an' } })
    fireEvent.click(screen.getByRole('button', { name: 'Search' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Add' }))
    fireEvent.click(screen.getByRole('button', { name: 'Create group' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('The API returned 422.')

    // Still there to retry. Clearing on failure loses the member selection the person just built.
    expect(screen.getByLabelText('Group name')).toHaveValue('Design')
  })
})

describe('GroupMembers', () => {
  function renderMembers(
    members: ReturnType<typeof aMember>[],
    overrides: Partial<Parameters<typeof GroupMembers>[0]> = {},
  ) {
    const client =
      overrides.client ??
      fakeMessagingClient({ listMembers: vi.fn(() => Promise.resolve(members)) })

    renderWithProviders(
      <GroupMembers
        conversationId="c1"
        client={client}
        currentEmployeeId="e1"
        {...overrides}
      />,
    )

    return { client }
  }

  it('reports loading and failure distinctly', async () => {
    renderMembers([], {
      client: fakeMessagingClient({ listMembers: vi.fn(() => pending<MemberResponse[]>()) }),
    })

    expect(screen.getByText(/loading members/i)).toBeInTheDocument()

    cleanup()

    renderMembers([], {
      client: fakeMessagingClient({ listMembers: vi.fn(() => Promise.reject(new Error('x'))) }),
    })

    expect(await screen.findByRole('alert')).toHaveTextContent(/could not load/i)
  })

  it('lists active members with their roles', async () => {
    renderMembers([aMember('e1', 'An Nguyen', 'admin'), aMember('e2', 'Bình Tran')])

    const rows = await screen.findAllByTestId('group-member')

    expect(at(rows, 0)).toHaveTextContent('An Nguyen')
    expect(at(rows, 0)).toHaveTextContent('(admin)')
    expect(at(rows, 1)).toHaveTextContent('(member)')
  })

  it('offers no management controls to a non-admin', async () => {
    renderMembers([aMember('e1', 'An Nguyen'), aMember('e2', 'Bình Tran', 'admin')])

    await screen.findAllByTestId('group-member')

    // The server refuses them regardless. Not rendering them just avoids offering a button whose
    // only outcome is a 403.
    expect(screen.queryByRole('button', { name: 'Remove' })).not.toBeInTheDocument()
    expect(screen.queryByRole('textbox')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Search' })).not.toBeInTheDocument()
  })

  it('never offers an admin the button to remove themselves', async () => {
    renderMembers([aMember('e1', 'An Nguyen', 'admin'), aMember('e2', 'Bình Tran')])

    await screen.findAllByTestId('group-member')

    // A group whose last admin removed themselves has nobody left who can add anyone — an
    // unrecoverable state reachable by one click.
    const removes = screen.getAllByRole('button', { name: 'Remove' })

    expect(removes).toHaveLength(1)
    expect(at(removes, 0).closest('li')).toHaveTextContent('Bình Tran')
  })

  it('removes a member and refreshes the list', async () => {
    const listMembers = vi
      .fn()
      .mockResolvedValueOnce([aMember('e1', 'An Nguyen', 'admin'), aMember('e2', 'Bình Tran')])
      .mockResolvedValue([aMember('e1', 'An Nguyen', 'admin')])

    const client = fakeMessagingClient({ listMembers })

    renderMembers([], { client })

    await screen.findAllByTestId('group-member')

    fireEvent.click(screen.getByRole('button', { name: 'Remove' }))

    await waitFor(() => {
      expect(client.removeMember).toHaveBeenCalledWith('c1', 'e2')
    })

    // Refetched rather than spliced locally. US3 scenario 3 takes effect immediately for new
    // messages, and the authoritative list is the server's.
    await waitFor(() => {
      expect(screen.getAllByTestId('group-member')).toHaveLength(1)
    })
  })

  it('adds a member and refreshes the list', async () => {
    const client = fakeMessagingClient({
      listMembers: vi.fn(() => Promise.resolve([aMember('e1', 'An Nguyen', 'admin')])),
      searchEmployees: vi.fn(() => Promise.resolve([anEmployee('e2', 'Bình Tran')])),
    })

    renderMembers([], { client })

    await screen.findAllByTestId('group-member')

    fireEvent.change(screen.getByRole('textbox'), { target: { value: 'binh' } })
    fireEvent.click(screen.getByRole('button', { name: 'Search' }))

    fireEvent.click(await screen.findByRole('button', { name: 'Add' }))

    await waitFor(() => {
      expect(client.addMember).toHaveBeenCalledWith('c1', 'e2')
    })
  })

  it('does not offer to add someone who is already a member', async () => {
    const client = fakeMessagingClient({
      listMembers: vi.fn(() =>
        Promise.resolve([aMember('e1', 'An Nguyen', 'admin'), aMember('e2', 'Bình Tran')]),
      ),
      searchEmployees: vi.fn(() =>
        Promise.resolve([anEmployee('e2', 'Bình Tran'), anEmployee('e3', 'Chi Le')]),
      ),
    })

    renderMembers([], { client })

    await screen.findAllByTestId('group-member')

    fireEvent.change(screen.getByRole('textbox'), { target: { value: 'tran' } })
    fireEvent.click(screen.getByRole('button', { name: 'Search' }))

    const adds = await screen.findAllByRole('button', { name: 'Add' })

    // The server treats a repeat add as already-active and refuses it. Offering the button anyway
    // makes a no-op look like a failure.
    expect(adds).toHaveLength(1)
    expect(at(adds, 0).closest('li')).toHaveTextContent('Chi Le')
  })

  it('does not search for a query too short to mean anything', async () => {
    const client = fakeMessagingClient({
      listMembers: vi.fn(() => Promise.resolve([aMember('e1', 'An Nguyen', 'admin')])),
    })

    renderMembers([], { client })

    await screen.findAllByTestId('group-member')

    fireEvent.change(screen.getByRole('textbox'), { target: { value: 'b' } })
    fireEvent.click(screen.getByRole('button', { name: 'Search' }))

    await waitFor(() => {
      expect(client.searchEmployees).not.toHaveBeenCalled()
    })
  })
})
