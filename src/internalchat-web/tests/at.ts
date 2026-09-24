/**
 * Indexes an array, failing loudly rather than handing back `undefined`.
 *
 * `tsconfig.app.json` turns on `noUncheckedIndexedAccess` deliberately, and tests are compiled
 * under the same program. That is the right setting — it is what stops `items[5].id` from being a
 * runtime crash the compiler waved through — but it makes every `getAllBy…()[0]` in a test a type
 * error, and the two usual escapes are both worse than this helper:
 *
 * - A non-null assertion (`items[0]!`) is banned by `strictTypeChecked`, and rightly: it silences
 *   the compiler without checking anything, so the test still crashes with `Cannot read properties
 *   of undefined` several lines later.
 * - Relaxing the flag for tests would mean the suite is typechecked under weaker rules than the
 *   code it covers.
 *
 * This checks. A test that asks for the third row when only two rendered fails on that sentence,
 * naming what it wanted and what was there — which is the failure message you want at 2am, rather
 * than a property access on `undefined` in a stack frame ten lines further on.
 */
export function at<T>(items: ArrayLike<T> | readonly T[], index = 0): T {
  const item = items[index]

  if (item === undefined) {
    throw new Error(
      `Expected an element at index ${String(index)}, but there were only ${String(items.length)}.`,
    )
  }

  return item
}

/**
 * A promise that never settles, typed as whatever the caller needs.
 *
 * For asserting the in-flight state of something: a query that has not answered, a save still
 * running. `new Promise(() => undefined)` infers `Promise<unknown>`, which does not satisfy the
 * client method it is standing in for, and widening the stub's type to accept it would mean the
 * stub no longer has to match the real client at all.
 */
export function pending<T>(): Promise<T> {
  return new Promise<T>(() => undefined)
}
