import { expect, test } from 'vitest'
import { axe, toHaveNoViolations } from 'jest-axe'
import { renderWithProviders, fakeMessagingClient } from './helpers'
import { ChatShell } from '../src/features/messages/ChatShell'
import { Composer } from '../src/features/messages/Composer'
import { MessageList } from '../src/features/messages/MessageList'
import { SearchPanel } from '../src/features/search/SearchPanel'
import { MeetingPanel } from '../src/features/meetings/MeetingPanel'

expect.extend(toHaveNoViolations)

vi.mock('../src/lib/realtime/chatConnection', () => ({
  ChatEvents: {},
  ChatConnection: class {
    constructor() {}
    start() { return Promise.resolve() }
    stop() { return Promise.resolve() }
    startTyping() { return Promise.resolve() }
    stopTyping() { return Promise.resolve() }
  },
}))

test('ChatShell should have no accessibility violations', async () => {
  const { container } = renderWithProviders(<ChatShell authorized={async () => new Response()} getAccessToken={async () => 'token'} currentEmployeeId="e1" />)
  const results = await axe(container)
  expect(results).toHaveNoViolations()
})

test('Composer should have no accessibility violations', async () => {
  const { container } = renderWithProviders(<Composer conversationId="c1" client={fakeMessagingClient()} onStartTyping={() => {}} onStopTyping={() => {}} />)
  const results = await axe(container)
  expect(results).toHaveNoViolations()
})

test('MessageList should have no accessibility violations', async () => {
  const { container } = renderWithProviders(<MessageList conversationId="c1" client={fakeMessagingClient()} currentEmployeeId="e1" connected={true} messages={[]} pending={[]} onLoadOlder={() => {}} hasOlder={false} />)
  const results = await axe(container)
  expect(results).toHaveNoViolations()
})

test('SearchPanel should have no accessibility violations', async () => {
  const { container } = renderWithProviders(<SearchPanel api={fakeMessagingClient()} onJumpTo={() => {}} />)
  const results = await axe(container)
  expect(results).toHaveNoViolations()
})

test('MeetingPanel should have no accessibility violations', async () => {
  const { container } = renderWithProviders(<MeetingPanel meetingId="m1" client={fakeMessagingClient()} onLeave={() => {}} />)
  const results = await axe(container)
  expect(results).toHaveNoViolations()
})
