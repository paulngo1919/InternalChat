/**
 * The sanitizer for server-generated search highlights.
 *
 * Its own module, deliberately. The project's ESLint configuration blocks
 * `dangerouslySetInnerHTML` except with "a reviewed sanitizer allow-list (constitution: Security
 * Requirements / Application Controls)" — this file *is* that sanitizer, and keeping it separate
 * from the component means the thing being reviewed is thirty lines rather than a UI file.
 *
 * The allow-list is exactly one element: `<mark>`. It carries no attributes, no URL, and no
 * behaviour, so the worst a forged one can do is highlight a word.
 */

/**
 * Escapes a highlight, then restores only its `<mark>` markers.
 *
 * `ts_headline` returns message text with `<mark>`/`</mark>` inserted around matches, and it does
 * **not** escape the text it wraps. That text is whatever someone typed into a chat box, so
 * rendering the string as-is would be stored XSS delivered through search results — to the person
 * who went looking for the message.
 *
 * **The order matters and is the whole safety argument.** Everything is escaped first, including
 * the markers, and only the two exact marker spellings are unescaped afterwards. Doing it the other
 * way round — strip markers, escape, re-insert — requires the marker positions to survive the
 * escape, and they do not.
 *
 * `&` is replaced before `<`, or an already-escaped `&lt;` would become `&amp;lt;` on one pass and
 * be mangled. That ordering also means pre-escaped text cannot be resurrected into a tag.
 *
 * **Known limitation, accepted rather than hidden:** a message in which someone literally types
 * `<mark>` is indistinguishable from a real match marker, so their word renders as highlighted.
 * That is a rendering quirk, not a vulnerability — every element that could *do* something is still
 * escaped.
 */
export function renderHighlight(highlight: string): string {
  const escaped = highlight
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#39;')

  return escaped.replaceAll('&lt;mark&gt;', '<mark>').replaceAll('&lt;/mark&gt;', '</mark>')
}
