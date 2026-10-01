// extension/lib/hlc.js — Hybrid Logical Clock (HLC)
//
// WARNING: This file must stay line-by-line equivalent with
// BookmarkSync.Domain/Hlc.cs. If the encoding format, branch order, or
// overflow handling differ in even one place, merges on the "m exactly equal"
// branch become nondeterministic — the same bookmark pair alternately wins
// across rounds, and both sides diverge without anyone noticing.
//
// Consistency is guaranteed by test/hlc_vectors.json: the C# side exports
// input sequences with expected outputs, and the JS side replays them one by
// one (see hlc.test.js).
//
// Paper: Kulkarni et al., "Logical Physical Clocks and Consistent Snapshots
//       in Globally Distributed Databases", 2014

const PHYSICAL_DIGITS = 13;
const COUNTER_DIGITS = 5;
const COUNTER_MAX = 99_999;

/** Minimum timestamp, used as the "no event seen yet" initial value. */
export const HLC_ZERO = '0000000000000-00000';

const FORMAT_RE = /^\d{13}-\d{5}$/;

/**
 * Encode (physical milliseconds, logical counter) as a fixed-width string.
 * Fixed width guarantees "lexicographic order == timestamp total order",
 * so compare() uses plain string comparison without parsing.
 */
export function encode(physicalMs, counter) {
  return String(physicalMs).padStart(PHYSICAL_DIGITS, '0') + '-' + String(counter).padStart(COUNTER_DIGITS, '0');
}

/** Parse an HLC string. Returns null on failure (instead of throwing) — bad input must not crash sync. */
export function decode(hlcString) {
  if (typeof hlcString !== 'string' || hlcString.length !== PHYSICAL_DIGITS + 1 + COUNTER_DIGITS) return null;
  if (hlcString[PHYSICAL_DIGITS] !== '-') return null;
  const physicalPart = hlcString.slice(0, PHYSICAL_DIGITS);
  const counterPart = hlcString.slice(PHYSICAL_DIGITS + 1);
  if (!/^\d+$/.test(physicalPart) || !/^\d{5}$/.test(counterPart)) return null;
  return { l: Number(physicalPart), c: Number(counterPart) };
}

export function isValidHLC(hlcString) {
  return typeof hlcString === 'string' && FORMAT_RE.test(hlcString);
}

/**
 * Compare two HLCs: -1 / 0 / 1.
 *
 * Malformed timestamps sort **below** every valid value, matching the C#
 * behavior — the polluted side always loses and cannot poison authority.
 */
export function compare(leftHlc, rightHlc) {
  const leftDecoded = decode(leftHlc);
  const rightDecoded = decode(rightHlc);
  if (leftDecoded === null && rightDecoded === null) return leftHlc < rightHlc ? -1 : leftHlc > rightHlc ? 1 : 0;
  if (leftDecoded === null) return -1;
  if (rightDecoded === null) return 1;
  if (leftDecoded.l !== rightDecoded.l) return leftDecoded.l < rightDecoded.l ? -1 : 1;
  if (leftDecoded.c !== rightDecoded.c) return leftDecoded.c < rightDecoded.c ? -1 : 1;
  return 0;
}

export function maxHLC(...hlcList) {
  let best = HLC_ZERO;
  for (const candidate of hlcList) {
    if (compare(candidate, best) > 0) best = candidate;
  }
  return best;
}

/** Hybrid logical clock instance. */
export class HLC {
  #physicalMs = 0;
  #logicalCounter = 0;
  #now;

  /**
   * @param {() => number} nowMs Returns current wall-clock milliseconds. Inject a fixed clock in tests.
   */
  constructor(nowMs = () => Date.now()) {
    this.#now = nowMs;
  }

  #normalize() {
    if (this.#logicalCounter > COUNTER_MAX) {
      this.#physicalMs += 1;
      this.#logicalCounter = 0;
    }
  }

  /** Produce a timestamp for a local event. */
  now() {
    const wallNowMs = this.#now();
    if (wallNowMs > this.#physicalMs) {
      this.#physicalMs = wallNowMs;
      this.#logicalCounter = 0;
    } else {
      this.#logicalCounter += 1;
    }
    this.#normalize();
    return encode(this.#physicalMs, this.#logicalCounter);
  }

  /**
   * Advance the local clock after receiving a remote timestamp.
   *
   * Branch order is critical (mirrors the C# side one-to-one):
   *   1. maxObserved == local && maxObserved == physicalNow → counter = max(counter, remoteCounter) + 1
   *   2. maxObserved == local                               → counter = counter + 1
   *   3. maxObserved == remotePhysical                      → counter = remoteCounter + 1
   *   4. otherwise (brand-new millisecond)                 → counter = 0
   *
   * Rule 3 must come before "physical clock is ahead". Counter-example: A emits
   * an event at millisecond 1000 with counter 5, while the receiver's physical
   * clock is also at 1000ms but its own counter is 0. Taking rule 4 here yields
   * (1000, 0), and a later (1000, 1) would sort **below** the received
   * (1000, 5) — causality is broken and the symptom is "bookmarks randomly lost".
   */
  update(remote) {
    const physicalNow = this.#now();
    const remoteDecoded = decode(remote);

    if (remoteDecoded === null) {
      // Remote is malformed: degrade to a pure local tick
      if (physicalNow > this.#physicalMs) {
        this.#physicalMs = physicalNow;
        this.#logicalCounter = 0;
      } else {
        this.#logicalCounter += 1;
      }
      this.#normalize();
      return encode(this.#physicalMs, this.#logicalCounter);
    }

    let maxObserved = this.#physicalMs;
    if (physicalNow > maxObserved) maxObserved = physicalNow;
    if (remoteDecoded.l > maxObserved) maxObserved = remoteDecoded.l;

    if (maxObserved === this.#physicalMs && maxObserved === physicalNow) {
      if (remoteDecoded.c > this.#logicalCounter) this.#logicalCounter = remoteDecoded.c;
      this.#logicalCounter += 1;
    } else if (maxObserved === this.#physicalMs) {
      this.#logicalCounter += 1;
    } else if (maxObserved === remoteDecoded.l) {
      this.#logicalCounter = remoteDecoded.c + 1;
    } else {
      this.#logicalCounter = 0;
    }
    this.#physicalMs = maxObserved;

    this.#normalize();
    return encode(this.#physicalMs, this.#logicalCounter);
  }

  /** Return the current timestamp without advancing the counter. */
  current() {
    return encode(this.#physicalMs, this.#logicalCounter);
  }

  /** Absorb many remote timestamps, advancing the local clock past their maximum. */
  observeMany(hlcList) {
    for (const candidate of hlcList) {
      const decoded = decode(candidate);
      if (decoded === null) continue;
      if (decoded.l > this.#physicalMs || (decoded.l === this.#physicalMs && decoded.c > this.#logicalCounter)) {
        this.#physicalMs = decoded.l;
        this.#logicalCounter = decoded.c;
      }
    }
    return this.current();
  }
}

/**
 * Encoding layout, kept in sync with the Hlc layout constant on the C# side.
 * 13-digit physical milliseconds + '-' + 5-digit logical counter, e.g. 1790000000000-00042
 */
export const HLC_LAYOUT = '13-digit physical ms + "-" + 5-digit logical counter, e.g. 1790000000000-00042';
