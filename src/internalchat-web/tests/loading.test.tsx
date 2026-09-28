import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'

import { LoadingScreen } from '../src/components/loading/LoadingScreen'
import {
  ConversationListSkeleton,
  MessageListSkeleton,
  Spinner,
} from '../src/components/loading/Skeletons'

/**
 * Loading states. The animation itself is CSS and out of jsdom's reach; what is asserted is what
 * every visitor gets regardless of motion or sight: one polite live region saying what is happening,
 * and decoration that assistive technology skips.
 */

afterEach(cleanup)

describe('LoadingScreen', () => {
  it('announces what it is waiting for, once, politely', () => {
    render(<LoadingScreen message="Loading your profile…" />)

    const status = screen.getByRole('status')
    expect(status).toHaveTextContent('Loading your profile…')
    expect(status).toHaveAttribute('aria-live', 'polite')
    expect(screen.getAllByRole('status')).toHaveLength(1)
  })

  it('hides the brand mark and progress bar from assistive technology', () => {
    const { container } = render(<LoadingScreen message="Signing in…" />)

    for (const decoration of container.querySelectorAll('.loading-screen__mark, .loading-screen__bar')) {
      expect(decoration).toHaveAttribute('aria-hidden', 'true')
    }
  })
})

describe('skeletons', () => {
  it('stand in for the conversation list and say so', () => {
    const { container } = render(<ConversationListSkeleton />)

    expect(screen.getByRole('status')).toHaveTextContent(/loading conversations/i)
    expect(container.querySelectorAll('.skeleton-row').length).toBeGreaterThan(3)
  })

  it('stand in for the transcript and say so', () => {
    render(<MessageListSkeleton />)

    expect(screen.getByRole('status')).toHaveTextContent(/loading messages/i)
  })

  it('give a spinner an accessible label', () => {
    render(<Spinner label="Loading members…" />)

    expect(screen.getByRole('status')).toHaveTextContent('Loading members…')
  })
})
