/**
 * T168 — the search highlight is the one string this app renders as HTML.
 *
 * `ts_headline` returns message text with `<mark>` markers inserted around matches, and the text
 * between those markers is whatever someone typed into a chat box. Rendering it unescaped would be
 * stored XSS delivered through search results — to the person who went looking for the message.
 *
 * These tests exist because the failure is invisible: a highlight containing a script tag renders
 * as an empty span, which looks like a formatting quirk rather than an executed payload.
 */

import { describe, expect, it } from 'vitest'

import { renderHighlight } from '../src/features/search/highlight'

describe('renderHighlight', () => {
  it('keeps the match markers', () => {
    expect(renderHighlight('a <mark>runbook</mark> for release')).toBe(
      'a <mark>runbook</mark> for release',
    )
  })

  it('escapes a script tag in the message text', () => {
    const hostile = '<script>alert(1)</script> <mark>runbook</mark>'

    const rendered = renderHighlight(hostile)

    expect(rendered).toContain('&lt;script&gt;')
    expect(rendered).not.toContain('<script>')

    // The legitimate marker still survives alongside the neutralised tag.
    expect(rendered).toContain('<mark>runbook</mark>')
  })

  it('escapes an attribute-injection attempt', () => {
    const hostile = '<img src=x onerror="alert(1)">'

    const rendered = renderHighlight(hostile)

    expect(rendered).not.toContain('<img')
    expect(rendered).not.toContain('onerror="')
  })

  it('does not resurrect a marker from already-escaped text', () => {
    // Someone writing about markup, whose text reached us already escaped. Because & is escaped
    // first, "&lt;" becomes "&amp;lt;" and no longer matches the unescape pattern — so it stays
    // inert text rather than becoming a tag. The ordering makes this safe rather than luck.
    const rendered = renderHighlight('use &lt;mark&gt; to highlight')

    expect(rendered).toBe('use &amp;lt;mark&amp;gt; to highlight')
    expect(rendered).not.toContain('<mark>')
  })

  it('cannot distinguish a user-typed <mark> from a real match marker', () => {
    // The genuine limitation of substituting on strings, recorded rather than hidden: ts_headline
    // does not escape the body it wraps, so someone who literally types <mark> produces a highlight
    // this function cannot tell from a real one.
    //
    // Accepted because the blast radius is a rendering quirk, not a vulnerability: <mark> carries
    // no attributes, no URL, and no behaviour, so the worst outcome is a word shown as highlighted
    // that was not a match. Every tag that could *do* something is still escaped.
    const rendered = renderHighlight('I typed <mark>this</mark> myself')

    expect(rendered).toContain('<mark>')
    expect(rendered).not.toContain('<script')
  })

  it('escapes ampersands before anything else, so escaping is not double-applied', () => {
    // & must be replaced first or "&lt;" in the source becomes "&amp;lt;" — mangled text rather
    // than a security problem, but the kind of bug that only shows up on one message in a thousand.
    expect(renderHighlight('Tom & Jerry')).toBe('Tom &amp; Jerry')
  })

  it('leaves ordinary text untouched', () => {
    expect(renderHighlight('kế hoạch quý tư')).toBe('kế hoạch quý tư')
  })

  it('handles an empty highlight', () => {
    expect(renderHighlight('')).toBe('')
  })
})
