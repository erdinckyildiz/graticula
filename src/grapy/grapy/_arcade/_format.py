"""Text(value, format): the number and date format specifiers of the Text page (ADR-235 §5.3).

Numbers: ``0`` a digit kept, ``#`` a digit dropped when it would be a leading or trailing zero, ``,`` grouping by
thousands, ``.`` the decimal point, ``%`` multiplying by 100, ``;`` separating the positive pattern from the negative
one; any other character is written as it is. INFERRED (ADR-235 §5.8), from the page's examples: a pattern without
grouping keeps only as many integer digits as it has placeholders (``Text(123, '00')`` is ``'23'``), and a half is
rounded away from zero.

Dates: the tokens the page lists, D … ZZZZZ. Anything else is written as it is.
"""
from __future__ import annotations

import datetime
import decimal
import math
import re

from ._values import Date, DateOnly, Time, is_number, to_text

_DATE_TOKENS = re.compile(r"\[[^\]]*\]|dddd|ddd|DDDD|DDD|DD|D|d|MMMM|MMM|MM|M|YYYY|YY|Y|hh|h|HH|H|mm|m|ss|s|SSS|A|a|"
                          r"ZZZZZ|ZZZZ|ZZZ|ZZ|Z")
_DAYS = ("Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday")
_MONTHS = ("January", "February", "March", "April", "May", "June", "July", "August", "September", "October",
           "November", "December")


def text(value, pattern=None):
    if pattern is None or pattern == "":
        return to_text(value)
    pattern = to_text(pattern)
    if isinstance(value, (Date, DateOnly, Time)):
        return date_text(value, pattern)
    if is_number(value) and not isinstance(value, bool):
        return number_text(float(value), pattern)
    return to_text(value)


def number_text(value, pattern):
    if math.isnan(value):
        return "NaN"
    if math.isinf(value):
        return "Infinity" if value > 0 else "-Infinity"
    sections = pattern.split(";")
    negative = value < 0
    if negative and len(sections) > 1:
        return _one(abs(value), sections[1])
    out = _one(abs(value), sections[0])
    return "-" + out if negative else out


def _one(value, pattern):
    placeholders = [i for i, c in enumerate(pattern) if c in "0#.,"]
    if not placeholders:
        return pattern
    first, last = placeholders[0], placeholders[-1]
    prefix, core, suffix = pattern[:first], pattern[first:last + 1], pattern[last + 1:]
    if "%" in prefix + suffix:
        value *= 100
    integer_part, _, fraction_part = core.partition(".")
    grouping = "," in integer_part
    integer_digits = integer_part.replace(",", "")
    minimum_fraction = fraction_part.count("0")
    maximum_fraction = sum(1 for c in fraction_part if c in "0#")
    exact = decimal.Decimal(repr(value)).quantize(decimal.Decimal(1).scaleb(-maximum_fraction),
                                                   rounding=decimal.ROUND_HALF_UP)
    whole, _, fraction = f"{exact:f}".partition(".")
    fraction = fraction.ljust(maximum_fraction, "0")[:maximum_fraction]
    while len(fraction) > minimum_fraction and fraction.endswith("0"):
        fraction = fraction[:-1]
    minimum_integer = integer_digits.count("0")
    if whole == "0" and minimum_integer == 0:
        whole = ""
    whole = whole.rjust(minimum_integer, "0")
    if not grouping and integer_digits and "#" not in integer_digits and len(whole) > len(integer_digits):
        whole = whole[-len(integer_digits):]
    if grouping and whole:
        whole = f"{int(whole):,}".rjust(len(whole), "0") if whole.strip("0") else whole
    body = whole + ("." + fraction if fraction else "")
    return prefix + body + suffix


def _zone(moment, token):
    offset = moment.utcoffset() or datetime.timedelta(0)
    minutes = int(offset.total_seconds() // 60)
    sign = "+" if minutes >= 0 else "-"
    hours, rest = divmod(abs(minutes), 60)
    if token == "Z":
        return f"{sign}{hours}" + (f":{rest:02d}" if rest else "")
    if token == "ZZ":
        return f"{sign}{hours:02d}:{rest:02d}"
    if token == "ZZZ":
        return f"{sign}{hours:02d}{rest:02d}"
    name = moment.tzname() or "UTC"
    return name


def date_text(value, pattern):
    if isinstance(value, Date):
        moment = value.value
    elif isinstance(value, DateOnly):
        moment = datetime.datetime.combine(value.value, datetime.time())
    else:
        moment = datetime.datetime(1970, 1, 1) + datetime.timedelta(milliseconds=value.ms)

    def one(match):
        token = match.group(0)
        if token.startswith("["):
            return token[1:-1]
        if token == "D":
            return str(moment.day)
        if token == "DD":
            return f"{moment.day:02d}"
        if token in ("DDD", "DDDD"):
            return str(moment.timetuple().tm_yday)
        if token == "d":
            return str(moment.isoweekday() % 7 + 1)  # 1–7, Sunday first, as the page lists it
        if token == "ddd":
            return _DAYS[moment.weekday()][:3]
        if token == "dddd":
            return _DAYS[moment.weekday()]
        if token == "M":
            return str(moment.month)
        if token == "MM":
            return f"{moment.month:02d}"
        if token == "MMM":
            return _MONTHS[moment.month - 1][:3]
        if token == "MMMM":
            return _MONTHS[moment.month - 1]
        if token in ("Y", "YYYY"):
            return str(moment.year)
        if token == "YY":
            return f"{moment.year % 100:02d}"
        hour12 = moment.hour % 12 or 12
        if token == "h":
            return str(hour12)
        if token == "hh":
            return f"{hour12:02d}"
        if token == "H":
            return str(moment.hour)
        if token == "HH":
            return f"{moment.hour:02d}"
        if token == "m":
            return str(moment.minute)
        if token == "mm":
            return f"{moment.minute:02d}"
        if token == "s":
            return str(moment.second)
        if token == "ss":
            return f"{moment.second:02d}"
        if token == "SSS":
            return f"{moment.microsecond // 1000:03d}"
        if token == "A":
            return "AM" if moment.hour < 12 else "PM"
        if token == "a":
            return "am" if moment.hour < 12 else "pm"
        if moment.tzinfo is None:
            return ""
        return _zone(moment, token)

    return _DATE_TOKENS.sub(one, pattern)
