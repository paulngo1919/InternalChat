import { expect, test } from 'vitest'
import { OfflineQueue } from '../src/lib/messages/offlineQueue'
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
  const { container } = renderWithProviders(<Composer conversationId="c1" queue={new OfflineQueue(() => Promise.resolve({ status: 'sent' }))} connected onEnqueued={() => undefined} onStartTyping={() => undefined} onStopTyping={() => undefined} />)
  const results = await axe(container)
  expect(results).toHaveNoViolations()
})

test('MessageList should have no accessibility violations', async () => {
  const { container } = renderWithProviders(<MessageList currentEmployeeId="e1" messages={[]} pending={[]} onLoadOlder={() => undefined} hasOlder={false} />)
  const results = await axe(container)
  expect(results).toHaveNoViolations()
})

test('SearchPanel should have no accessibility violations', async () => {
  const { container } = renderWithProviders(<SearchPanel api={fakeMessagingClient()} onJumpTo={() => undefined} />)
  const results = await axe(container)
  expect(results).toHaveNoViolations()
})

test('MeetingPanel should have no accessibility violations', async () => {
  const { container } = renderWithProviders(<MeetingPanel conversationId="c1" client={fakeMessagingClient()} currentEmployeeId="e1" />)
  const results = await axe(container)
  expect(results).toHaveNoViolations()
})
