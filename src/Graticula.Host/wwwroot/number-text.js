/*
 * A typed number, read by one rule on every Studio screen — ADR-312 §5.6, ADR-313 INFERRED (o).
 *
 * - One comma or one point is the decimal separator: "90,5" and "90.5" are both ninety and a half.
 * - Nothing groups thousands, so "1500" is how fifteen hundred is typed.
 * - One separator before exactly three digits — "1,500" or "1.500" — could be either: a Turkish, German or Spanish
 *   reader means fifteen hundred by "1.500", an English one by "1,500". Both are refused with a sentence rather than
 *   read one way (the forms reviewer's fourth render, 2026-11-23; "1,500" alone was refused before, "1.500" read as 1.5).
 * - The separator is never read from the browser's locale: the owner writes commas, and one rule on every screen
 *   matters more than guessing a reader's habit.
 *
 * Callers: webmap.js `wmParseNumber`, form-info.js `decimalText` / `ambiguousNumber`, contingent.js `cvParseNumber` /
 * `cvAmbiguousNumber` / `cvAmbiguousSaid`. A classic script in the browser (globals start with `nt`), CommonJS in Node,
 * where tests/js/number-text.test.js loads it.
 */

/**
 * Whether typed text could mean a thousands-grouped whole number as well as a decimal: "1,500", "1.500". A number
 * whose whole part is 0 or starts with 0 — "0.125", "0,500", "012.500" — groups no thousands, so it is never ambiguous
 * (fourth render, M1: "0.125" was refused and offered "0125").
 */
function ntAmbiguous(raw) {
  return /^[-+]?[1-9]\d{0,2}[.,]\d{3}$/.test(String(raw === null || raw === undefined ? "" : raw).trim());
}

const NT_ONES = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve",
  "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"];
const NT_TENS = ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];

/** A whole number below a thousand in words. */
function ntBelowThousand(n) {
  if (n < 20) return NT_ONES[n];
  if (n < 100) return NT_TENS[Math.floor(n / 10)] + (n % 10 ? `-${NT_ONES[n % 10]}` : "");
  return `${NT_ONES[Math.floor(n / 100)]} hundred${n % 100 ? ` ${ntBelowThousand(n % 100)}` : ""}`;
}

/**
 * A whole number below a million in words, as somebody says it: 1500 is "fifteen hundred", 2000 "two thousand",
 * 12500 "twelve thousand five hundred". Only what an ambiguous number can be (1,000 to 999,999) needs saying.
 */
function ntWords(n) {
  const v = Math.abs(Math.trunc(n));
  const sign = n < 0 ? "minus " : "";
  if (v >= 1000 && v < 10000 && v % 100 === 0 && v % 1000 !== 0) return `${sign}${ntBelowThousand(v / 100)} hundred`;
  if (v < 1000) return sign + ntBelowThousand(v);
  const rest = v % 1000;
  return `${sign}${ntBelowThousand(Math.floor(v / 1000))} thousand${rest ? ` ${ntBelowThousand(rest)}` : ""}`;
}

/**
 * The decimal reading of an ambiguous text, written so the rule accepts it: trailing zeros dropped ("1.500" → "1.5"),
 * or, when all three decimals are needed, a fourth zero added ("2.125" → "2.1250"), in the separator the reader used.
 */
function ntDecimalForm(raw) {
  const t = String(raw).trim();
  const separator = t.includes(",") ? "," : ".";
  const [whole, fraction] = t.split(/[.,]/);
  const trimmed = fraction.replace(/0+$/, "");
  if (!trimmed) return whole;
  return trimmed.length === 3 ? `${whole}${separator}${trimmed}0` : `${whole}${separator}${trimmed}`;
}

/**
 * The sentence an ambiguous number is refused with. Every form it tells the reader to type is one the rule accepts
 * (fourth render, M1: "Type 2125 or 2.125" offered the very text it had refused). A field of whole numbers offers only
 * the reading it can hold: "1,500: type 1500 if you mean fifteen hundred."; any other offers both: "2.125 could mean
 * 2125 or 2.125. Type 2125, or 2.1250 for the decimal."
 */
function ntAmbiguousSaid(raw, whole = false) {
  const t = String(raw).trim();
  const thousands = t.replace(/[.,]/, "");
  if (whole) return `${t}: type ${thousands} if you mean ${ntWords(Number(thousands))}.`;
  // The decimal reading is said with the reader's own separator, as the suggestion after it is (fifth render P3).
  const value = String(Number(t.replace(",", "."))).replace(".", t.includes(",") ? "," : ".");
  return `${t} could mean ${thousands} or ${value}. Type ${thousands}, or ${ntDecimalForm(t)} for the decimal.`;
}

/**
 * Reads typed text by the rule.
 *
 * @param {*} raw what was typed
 * @param {boolean} [whole] whether the field holds whole numbers, which changes only the ambiguity sentence
 * @returns {{value: number}|{empty: true}|{ambiguous: string}|{wrong: true}} a number; nothing typed; text that could
 *   be either reading, with the sentence to say; or text that is not a number
 */
function ntParseNumber(raw, whole = false) {
  const t = String(raw === null || raw === undefined ? "" : raw).trim();
  if (t === "") return { empty: true };
  if (ntAmbiguous(t)) return { ambiguous: ntAmbiguousSaid(t, whole) };
  if (!/^[-+]?(\d+([.,]\d*)?|[.,]\d+)([eE][-+]?\d+)?$/.test(t)) return { wrong: true };
  const value = Number(t.replace(",", "."));
  return Number.isFinite(value) ? { value } : { wrong: true };
}

if (typeof module !== "undefined" && module.exports) {
  module.exports = { ntParseNumber, ntAmbiguous, ntAmbiguousSaid, ntDecimalForm, ntWords };
}
