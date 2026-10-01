// extension/lib/hlc.test.js — HLC cross-side consistency check.
// This is the most important test file in the project.
//
// Why: merges pick winners by HLC timestamp, and HLC `update` has four
// branches. A wrong order never throws and never crashes — it only produces a
// value **smaller** than an already-seen timestamp when the physical clock
// exactly catches up to the remote millisecond. Both sides then alternately
// win, bookmarks are randomly lost, and the user notices nothing.
//
// That kind of bug cannot be caught by "reading the code", because each of
// the four branches looks reasonable on its own. A fixed input sequence with
// expected outputs, replayed on both sides, is required.
//
// Vector source: BookmarkSync.Domain/Hlc.cs, exported by
//   HlcVectorExportTests in the Domain tests.
// Regenerate: cd bmsync && dotnet test --filter HlcVectorExport
//
// Run: node --test lib/hlc.test.js

import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

import { HLC, encode, decode, compare, isValidHLC, HLC_ZERO } from './hlc.js';

const here = dirname(fileURLToPath(import.meta.url));
const VECTORS = join(here, '..', '..', 'test', 'hlc_vectors.json');

// ── Encoding and comparison (local properties, no C# side needed) ──────

test('encode outputs fixed-width 19 chars', () => {
  const cases = [
    [0, 0, '0000000000000-00000'],
    [1000, 0, '0000000001000-00000'],
    [1000, 42, '0000000001000-00042'],
    [1790000000000, 99999, '1790000000000-99999'],
    [9223372036854, 1, '9223372036854-00001'],
  ];
  for (const [physicalMs, counter, want] of cases) {
    assert.equal(encode(physicalMs, counter), want, `encode(${physicalMs}, ${counter})`);
  }
});

test('encode/decode round-trips', () => {
  for (let i = 0; i < 500; i++) {
    const physicalMs = Math.floor(Math.random() * 9_000_000_000_000);
    const counter = Math.floor(Math.random() * 100_000);
    const encoded = encode(physicalMs, counter);
    assert.equal(isValidHLC(encoded), true);
    const decoded = decode(encoded);
    assert.equal(decoded.l, physicalMs);
    assert.equal(decoded.c, counter);
  }
});

test('decode rejects malformed input', () => {
  for (const hlcString of [
    '', '123', '0000000001000-0000', '0000000001000000000',
    '0000000001000_00000', '0000000001000-0000x', 'abc-00000',
    '0000000001000-999999', '-0000000001000-00000',
  ]) {
    assert.equal(decode(hlcString), null, `should reject ${JSON.stringify(hlcString)}`);
    assert.equal(isValidHLC(hlcString), false);
  }
});

test('compare: same millisecond compares by counter, cross-millisecond physical wins', () => {
  assert.equal(compare(encode(1000, 5), encode(1000, 6)), -1);
  assert.equal(compare(encode(1000, 6), encode(1000, 5)), 1);
  assert.equal(compare(encode(1000, 5), encode(1000, 5)), 0);
  assert.equal(compare(encode(1001, 0), encode(1000, 99999)), 1);
});

test('compare: malformed values sort below every valid value (polluted side always loses)', () => {
  assert.equal(compare('garbage', encode(1000, 0)), -1);
  assert.equal(compare(encode(1000, 0), 'garbage'), 1);
  assert.equal(compare('a', 'b'), 'a'.localeCompare('b') === 0 ? 0 : -1);
});

test('lexicographic order == time order (the reason the encoding exists)', () => {
  const samples = [];
  for (let i = 0; i < 800; i++) {
    // Cluster many samples in the same millisecond to force the
    // "same physical, counter-only" path
    samples.push(encode(1_000_000_000_000 + Math.floor(Math.random() * 5),
                   Math.floor(Math.random() * 100_000)));
  }
  const sorted = [...samples].sort();
  for (let i = 1; i < sorted.length; i++) {
    assert.ok(compare(sorted[i - 1], sorted[i]) <= 0,
      `${sorted[i - 1]} should be <= ${sorted[i]}`);
  }
});

// ── Local behavior ────────────────────────────────────────────────────

test('now() is strictly monotonic', () => {
  const clock = new HLC(() => 1_000_000_000_000);
  let prev = clock.now();
  for (let i = 0; i < 500; i++) {
    const current = clock.now();
    assert.ok(compare(current, prev) > 0, `step ${i} did not advance: ${prev} -> ${current}`);
    prev = current;
  }
});

test('counter resets when the physical clock advances', () => {
  let wallMs = 1000;
  const clock = new HLC(() => wallMs);
  assert.equal(clock.now(), '0000000001000-00000');
  assert.equal(clock.now(), '0000000001000-00001');
  wallMs = 2000;
  assert.equal(clock.now(), '0000000002000-00000');
});

test('causality: A events strictly precede B events observed after them', () => {
  const clockA = new HLC(() => 1_000_000_000_000);
  const clockB = new HLC(() => 1_000_000_000_000);
  let last = clockA.now();
  for (let i = 0; i < 5; i++) last = clockA.now();
  for (let i = 0; i < 5; i++) {
    clockB.update(last);
    const got = clockB.now();
    assert.ok(compare(got, last) > 0, `B follow-up event ${got} is not greater than ${last}`);
  }
});

// Regression test for the branch that was once written wrong.
//
// A's event millisecond leads while B's local clock lags — maxObserved equals
// the remote millisecond. The wrong code ("reset counter when the physical
// clock leads") gave B (maxObserved, 0), so a later now() produced
// (maxObserved, 1) below the already-received (maxObserved, remoteCounter) —
// causality broken.
test('remote millisecond ahead must continue after the remote counter', () => {
  const clockA = new HLC(() => 1500);
  const clockB = new HLC(() => 1000);

  // A emits 6 events within 1500ms -> the last is (1500, 00005)
  let remote;
  for (let i = 0; i < 6; i++) remote = clockA.now();
  assert.equal(remote, '0000000001500-00005');

  clockB.update(remote);
  const got = clockB.now();

  assert.ok(compare(got, remote) > 0,
    `causality broken: ${got} is not greater than remote ${remote}`);
  // update itself consumed remoteCounter+1, so now() gets remoteCounter+2.
  assert.equal(got, '0000000001500-00007');
});

test('same-millisecond remote and local must also continue after the remote counter', () => {
  // Other counter-example: maxObserved equals both local and remote, first branch
  const clockA = new HLC(() => 1000);
  const clockB = new HLC(() => 1000);
  let remote;
  for (let i = 0; i < 6; i++) remote = clockA.now();
  assert.equal(remote, '0000000001000-00005');

  clockB.update(remote);
  const got = clockB.now();
  assert.ok(compare(got, remote) > 0, `causality broken: ${got} is not greater than ${remote}`);
});

test('logical time keeps advancing after the clock moves backwards', () => {
  let wallMs = 2_000_000_000_000;
  const clock = new HLC(() => wallMs);
  const first = clock.now();

  wallMs = 2_000_000_000_000 - 3_600_000; // user set the clock back one hour
  let prev = first;
  for (let i = 0; i < 100; i++) {
    const current = clock.now();
    assert.ok(compare(current, prev) > 0, `step ${i} after rollback did not advance`);
    prev = current;
  }
  assert.ok(compare(clock.current(), first) >= 0);
});

test('counter overflow carries into the next millisecond, length unchanged', () => {
  // Push internal state to the boundary via observeMany instead of 100000 calls.
  const clock = new HLC(() => 1000);
  clock.observeMany([encode(1000, 99_999)]);
  const first = clock.now();
  const second = clock.now();
  assert.ok(compare(second, first) > 0);
  assert.equal(first.length, 19);
  assert.equal(second.length, 19);
});

test('update with malformed remote does not crash and still yields a valid stamp', () => {
  const clock = new HLC(() => 1000);
  const first = clock.now();
  const got = clock.update('not an HLC at all');
  assert.equal(isValidHLC(got), true);
  assert.ok(compare(got, first) > 0);
});

test('observeMany catches up to the maximum of all remote stamps', () => {
  const clock = new HLC(() => 500);
  const remote = encode(9000, 12);
  const got = clock.observeMany([encode(100, 0), remote, 'garbage']);
  assert.ok(compare(got, remote) >= 0);
  assert.ok(compare(clock.now(), remote) > 0);
});

test('HLC_ZERO is the minimum', () => {
  assert.equal(HLC_ZERO, '0000000000000-00000');
  assert.equal(compare(HLC_ZERO, encode(0, 1)), -1);
});

// ── Cross-side vector verification ────────────────────────────────────

test('C# and JS HLC implementations agree step by step (test/hlc_vectors.json)', () => {
  const raw = JSON.parse(readFileSync(VECTORS, 'utf-8'));
  const vectors = raw.vectors;

  assert.ok(Array.isArray(vectors) && vectors.length >= 15,
    `only ${vectors?.length} vectors, cross-side coverage is too thin — regenerate with ` +
    'cd bmsync && dotnet test --filter HlcVectorExport');

  // Each step sets the physical clock to the vector's `at`, then runs that step.
  //
  // WARNING: it must be `() => wallMs`, not `() => 0`: HLC now() takes the max
  // of the physical clock and the known logical time. Feeding 0 takes the
  // "logical time leads" branch while the vectors record the "physical clock"
  // branch — the symptom is a mismatch on the very first entry that looks
  // like a cross-side deviation but is really this arrow function written wrong.
  let wallMs = 0;
  const clock = new HLC(() => wallMs);

  for (const [index, vector] of vectors.entries()) {
    wallMs = vector.at;
    const got = vector.op === 'update' ? clock.update(vector.remote) : clock.now();
    assert.equal(got, vector.out,
      `step ${index + 1} (op=${vector.op} remote=${vector.remote} at=${vector.at}) diverged:\n` +
      `  C#: ${vector.out}\n  JS: ${got}\n` +
      `  The two implementations disagree; merges on "exactly equal stamps" go nondeterministic.`);
  }
});

test('vector sequence itself is strictly monotonic', () => {
  const { vectors } = JSON.parse(readFileSync(VECTORS, 'utf-8'));
  for (let i = 1; i < vectors.length; i++) {
    assert.ok(compare(vectors[i - 1].out, vectors[i].out) < 0,
      `vector ${i} (${vectors[i].out}) is not strictly greater than previous (${vectors[i - 1].out})`);
  }
});

test('vectors cover the once-broken "remote leads" branch', () => {
  const { vectors } = JSON.parse(readFileSync(VECTORS, 'utf-8'));
  // Must include an update where the remote millisecond leads with a nonzero counter
  const remoteLeads = vectors.filter((vector) => {
    if (vector.op !== 'update' || !isValidHLC(vector.remote)) return false;
    const remoteDecoded = decode(vector.remote);
    return remoteDecoded.l > 1000_000 && remoteDecoded.c > 0;
  });
  assert.ok(remoteLeads.length > 0,
    'vectors contain no "remote millisecond ahead with nonzero counter" sample — ' +
    'the once-broken branch is uncovered');
});
