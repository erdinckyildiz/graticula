"""Reading text by a pattern (ADR-235 §5.3): Number(text, pattern), DateOnly(text, format) and Time(text, format).

Number's pattern is the Text pattern read backwards: ``0`` a required digit, ``#`` an optional one, ``,`` a group
separator, ``.`` the decimal point, ``%`` a percentage, ``;`` a negative pattern after the positive one, anything else
literal text that must be there. As the page's examples show, the text must have at least as many integer digits as
the pattern's ``0`` and between the ``0`` and the ``0``-and-``#`` count of fraction digits (``Number('10.4',
'00.00##')`` is NaN), and group separators may stand anywhere in the integer digits.

DateOnly's format takes D, DD, DDD, d, ddd, dddd, M, MM, MMM, MMMM, Y and YY; Time's h, hh, H, HH, m, mm, s, ss, A
(and SSS). Anything else is literal.

INFERRED (ADR-235 §5.8): a single positive pattern also reads a leading minus sign; a two-digit year YY is 2000–2068
for 00–68 and 1969–1999 for 69–99; month and day names are English; in a Time format M and MM read minutes, as the
page's ``Time("12 05 04", "HH MM ss")`` does.
"""
from __future__ import annotations

import datetime
import math
import re

_MONTHS = ("january", "february", "march", "april", "may", "june", "july", "august", "september", "october",
           "november", "december")
_DAYS = ("monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday")


def number(text, pattern):
    sections = pattern.split(";")
    for index, section in enumerate(sections[:2]):
        value = _one_number(text, section)
        if value is not None:
            return -value if index == 1 else value
    if len(sections) == 1 and text.strip().startswith("-"):
        value = _one_number(text.strip()[1:], sections[0])
        if value is not None:
            return -value
    return math.nan


def _one_number(text, pattern):
    placeholders = [i for i, c in enumerate(pattern) if c in "0#,."]
    if not placeholders:
        return None
    first, last = placeholders[0], placeholders[-1]
    prefix, core, suffix = pattern[:first], pattern[first:last + 1], pattern[last + 1:]
    integer, dot, fraction = core.partition(".")
    least_integer = integer.count("0")
    least_fraction, most_fraction = fraction.count("0"), sum(1 for c in fraction if c in "0#")
    expression = re.escape(prefix) + r"(?P<int>[0-9,]*)"
    if dot:
        fraction_digits = f"[0-9]{{{least_fraction},{most_fraction}}}"
        expression += (rf"(?:\.(?P<frac>{fraction_digits}))" + ("?" if least_fraction == 0 else ""))
    expression += re.escape(suffix)
    found = re.fullmatch(expression, text.strip())
    if not found:
        return None
    digits = found.group("int").replace(",", "")
    if len(digits) < least_integer or (not digits and not found.groupdict().get("frac")):
        return None
    import decimal
    value = decimal.Decimal((digits or "0") + "." + (found.groupdict().get("frac") or "0"))
    if "%" in prefix + suffix:
        value /= 100  # in decimal, so '99.99%' is 0.9999 and not 0.99989999…
    return float(value)


def _year(text):
    value = int(text)
    if len(text) <= 2:
        return value + (2000 if value <= 68 else 1900)
    return value


_DATE_TOKENS = re.compile(r"dddd|ddd|DDD|DD|D|d|MMMM|MMM|MM|M|YYYY|YY|Y")
_TIME_TOKENS = re.compile(r"hh|h|HH|H|mm|m|MM|M|ss|s|SSS|A|a")


def _expression(format_text, tokens, groups):
    out, at = [], 0
    for match in tokens.finditer(format_text):
        out.append(re.escape(format_text[at:match.start()]).replace(r"\ ", r"\s+"))
        token = match.group(0)
        out.append(f"(?P<t{len(groups)}>{_PIECES[token]})")
        groups.append(token)
        at = match.end()
    out.append(re.escape(format_text[at:]).replace(r"\ ", r"\s+"))
    return "".join(out)


_PIECES = {"D": r"\d{1,2}", "DD": r"\d{2}", "DDD": r"\d{1,3}", "d": r"[1-7]", "ddd": r"[A-Za-z]{3}",
           "dddd": r"[A-Za-z]+", "MMMM": r"[A-Za-z]+", "MMM": r"[A-Za-z]{3}", "YYYY": r"\d{4}", "YY": r"\d{2}",
           "Y": r"\d{1,4}", "h": r"\d{1,2}", "hh": r"\d{2}", "H": r"\d{1,2}", "HH": r"\d{2}", "m": r"\d{1,2}",
           "mm": r"\d{2}", "M": r"\d{1,2}", "MM": r"\d{2}", "s": r"\d{1,2}", "ss": r"\d{2}", "SSS": r"\d{3}",
           "A": r"[AaPp][Mm]", "a": r"[AaPp][Mm]"}


def date_only(text, format_text):
    """A date read by a DateOnly format, or None."""
    groups = []
    found = re.fullmatch(_expression(format_text, _DATE_TOKENS, groups), text.strip())
    if not found:
        return None
    year, month, day, day_of_year = None, None, None, None
    for index, token in enumerate(groups):
        value = found.group(f"t{index}")
        if token in ("D", "DD"):
            day = int(value)
        elif token == "DDD":
            day_of_year = int(value)
        elif token in ("M", "MM"):
            month = int(value)
        elif token in ("MMM", "MMMM"):
            names = [m[:3] if token == "MMM" else m for m in _MONTHS]
            if value.lower() not in names:
                return None
            month = names.index(value.lower()) + 1
        elif token in ("Y", "YY", "YYYY"):
            year = _year(value)
        elif token in ("ddd", "dddd"):
            names = [d[:3] if token == "ddd" else d for d in _DAYS]
            if value.lower() not in names:
                return None
    if year is None:
        return None
    try:
        if day_of_year is not None and month is None:
            return datetime.date(year, 1, 1) + datetime.timedelta(days=day_of_year - 1)
        return datetime.date(year, month or 1, day or 1)
    except ValueError:
        return None


def time(text, format_text):
    """Milliseconds since midnight read by a Time format, or None."""
    groups = []
    found = re.fullmatch(_expression(format_text, _TIME_TOKENS, groups), text.strip())
    if not found:
        return None
    hour, minute, second, ms, half = 0, 0, 0, 0, None
    for index, token in enumerate(groups):
        value = found.group(f"t{index}")
        if token in ("h", "hh", "H", "HH"):
            hour = int(value)
        elif token in ("m", "mm", "M", "MM"):
            minute = int(value)
        elif token in ("s", "ss"):
            second = int(value)
        elif token == "SSS":
            ms = int(value)
        else:
            half = value.upper()
    if half is not None:
        if not 1 <= hour <= 12:
            return None
        hour = hour % 12 + (12 if half == "PM" else 0)
    if hour > 23 or minute > 59 or second > 59:
        return None
    return ((hour * 60 + minute) * 60 + second) * 1000 + ms
