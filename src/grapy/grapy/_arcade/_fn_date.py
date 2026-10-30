"""Arcade's date functions (ADR-235 §5.3), from the Date functions page: months are 0-based in Date, DateOnly and Month,
1-based in ISOMonth; DateAdd and DateDiff count in milliseconds unless given units.

INFERRED (ADR-235 §5.8): a rule run by grapy executes in UTC, so Now, Today and a Date built without a time zone are
UTC, and ToLocal answers UTC too. Named IANA zones are read from the system's time zone database; where it has none,
naming one is an error, while UTC and fixed offsets (``+07:00``) always work. Out-of-range components roll over, as
JavaScript's dates do (month 12 is January of the next year). DateAdd adds whole months and years, rounded; DateDiff
in months or years counts whole months and then the fraction of the next month elapsed, and a year is twelve months.
"""
from __future__ import annotations

import calendar
import datetime
import math
import re

from ._errors import ArcadeError
from ._fn_core import arity, first_is
from ._library import builtin
from ._values import UTC, Date, DateOnly, Time, is_number, to_number, to_text, type_of

_UNITS = {"millisecond": 1.0, "milliseconds": 1.0, "second": 1000.0, "seconds": 1000.0, "minute": 60000.0,
          "minutes": 60000.0, "hour": 3600000.0, "hours": 3600000.0, "day": 86400000.0, "days": 86400000.0,
          "week": 604800000.0, "weeks": 604800000.0}
_CALENDAR = {"month": 1, "months": 1, "year": 12, "years": 12}
_OFFSET = re.compile(r"^([+-])(\d{1,2})(?::?(\d{2}))?$")


def zone(name):
    """A tzinfo for an Arcade time zone: UTC, system or local (both UTC here), a fixed offset, or an IANA name."""
    text = to_text(name).strip()
    lower = text.lower()
    if lower in ("utc", "system", "local", "z", "gmt", "unknown", ""):
        return UTC
    match = _OFFSET.match(text)
    if match:
        sign, hours, minutes = match.group(1), int(match.group(2)), int(match.group(3) or 0)
        delta = datetime.timedelta(hours=hours, minutes=minutes)
        return datetime.timezone(-delta if sign == "-" else delta)
    try:
        import zoneinfo
        return zoneinfo.ZoneInfo(text)
    except Exception:
        raise ArcadeError(f"The time zone '{text}' is not known on this system; UTC and offsets such as +07:00 "
                          "are.") from None


def _zone_name(tz):
    key = getattr(tz, "key", None)
    if key:
        return key
    offset = tz.utcoffset(None)
    if offset == datetime.timedelta(0):
        return "UTC"
    minutes = int(offset.total_seconds() // 60)
    return f"{'+' if minutes >= 0 else '-'}{abs(minutes) // 60:02d}:{abs(minutes) % 60:02d}"


def build(year, month, day=1, hour=0, minute=0, second=0, millisecond=0, tz=UTC):
    """A datetime from components that may be out of range, rolled over as JavaScript does."""
    year, month = int(year) + int(month) // 12, int(month) % 12
    base = datetime.datetime(year, month + 1, 1, tzinfo=tz)
    base += datetime.timedelta(days=int(day) - 1, hours=int(hour), minutes=int(minute), seconds=int(second),
                               milliseconds=int(millisecond))
    return base


def parse_iso(text):
    """A Date from ISO 8601 text, or None. Text with no offset is in the execution zone (UTC)."""
    text = text.strip()
    if not text:
        return None
    candidate = text[:-1] + "+00:00" if text.endswith(("Z", "z")) else text
    try:
        moment = datetime.datetime.fromisoformat(candidate)
    except ValueError:
        return None
    if moment.tzinfo is None:
        moment = moment.replace(tzinfo=UTC)
    return Date(moment.astimezone(UTC))


def as_date(value):
    """Implicit Date casting: a Date, a DateOnly at midnight, ISO text; anything else null."""
    if isinstance(value, Date):
        return value
    if isinstance(value, DateOnly):
        return Date(datetime.datetime.combine(value.value, datetime.time(), UTC))
    if isinstance(value, str):
        return parse_iso(value)
    if isinstance(value, datetime.datetime):
        return Date(value)
    return None


def _moment(name, value):
    """The datetime (or date, for a DateOnly) a date function reads."""
    if isinstance(value, DateOnly):
        return value.value
    date = as_date(value)
    if date is None:
        if value is None:
            return None
        raise ArcadeError(f"{name} takes a Date; a {type_of(value)} was given.")
    return date.value


@builtin("Date")
def _date(_, args):
    if not args:
        return Date(datetime.datetime.now(UTC))
    first = args[0]
    if len(args) == 1:
        if first is None:
            return Date(datetime.datetime.fromtimestamp(0, UTC))
        if isinstance(first, Date):
            return Date(first.value)
        if isinstance(first, DateOnly):
            return as_date(first)
        if isinstance(first, str):
            return parse_iso(first)
        if is_number(first):
            value = to_number(first)
            if math.isnan(value) or math.isinf(value):
                return None
            return Date(datetime.datetime.fromtimestamp(0, UTC) + datetime.timedelta(milliseconds=value))
        raise ArcadeError(f"Date takes a number, text or a date; a {type_of(first)} was given.")
    if isinstance(first, DateOnly):
        arity("Date", args, 1, 3)
        tz = zone(args[2]) if len(args) > 2 else UTC
        ms = args[1].ms if isinstance(args[1], Time) else 0
        return Date(datetime.datetime.combine(first.value, datetime.time(), tz) + datetime.timedelta(milliseconds=ms))
    arity("Date", args, 3, 8)
    parts = [to_number(a) for a in args[:7]]
    if any(math.isnan(p) for p in parts):
        return None
    tz = zone(args[7]) if len(args) > 7 else UTC
    try:
        return Date(build(*parts, tz=tz))
    except (OverflowError, ValueError):
        return None


@builtin("Now")
def _now(_, args):
    arity("Now", args, 0)
    return Date(datetime.datetime.now(UTC))


@builtin("Timestamp")
def _timestamp(_, args):
    arity("Timestamp", args, 0)
    return Date(datetime.datetime.now(UTC))


@builtin("Today")
def _today(_, args):
    arity("Today", args, 0)
    now = datetime.datetime.now(UTC)
    return Date(now.replace(hour=0, minute=0, second=0, microsecond=0))


@builtin("ToLocal")
def _to_local(_, args):
    arity("ToLocal", args, 1)
    date = as_date(args[0])
    return None if date is None else Date(date.value.astimezone(UTC))


@builtin("ToUTC")
def _to_utc(_, args):
    arity("ToUTC", args, 1)
    date = as_date(args[0])
    return None if date is None else Date(date.value.astimezone(UTC))


@builtin("ChangeTimeZone")
def _change_time_zone(_, args):
    arity("ChangeTimeZone", args, 2)
    date = as_date(args[0])
    return None if date is None else Date(date.value.astimezone(zone(args[1])))


@builtin("TimeZone")
def _time_zone(_, args):
    arity("TimeZone", args, 1)
    date = as_date(args[0])
    return None if date is None else _zone_name(date.value.tzinfo)


@builtin("TimeZoneOffset")
def _time_zone_offset(_, args):
    arity("TimeZoneOffset", args, 1)
    date = as_date(args[0])
    return None if date is None else date.value.utcoffset().total_seconds() * 1000.0


def _part(name, read, times=False):
    @builtin(name)
    def run(_, args):
        arity(name, args, 1)
        if times and isinstance(args[0], Time):
            return float(read(datetime.datetime(1970, 1, 1) + datetime.timedelta(milliseconds=args[0].ms)))
        moment = _moment(name, args[0])
        return None if moment is None else float(read(moment))
    return run


_part("Year", lambda m: m.year)
_part("Month", lambda m: m.month - 1)
_part("ISOMonth", lambda m: m.month)
_part("Day", lambda m: m.day)
_part("Hour", lambda m: m.hour, times=True)
_part("Minute", lambda m: m.minute, times=True)
_part("Second", lambda m: m.second, times=True)
_part("Millisecond", lambda m: m.microsecond // 1000, times=True)
_part("Weekday", lambda m: (m.weekday() + 1) % 7)
_part("ISOWeekday", lambda m: m.isoweekday())
_part("ISOWeek", lambda m: m.isocalendar()[1])
_part("ISOYear", lambda m: m.isocalendar()[0])


@builtin("Week")
def _week(_, args):
    """The number of the given first days of the week on or before the date in its year: 0 before the first one."""
    arity("Week", args, 1, 2)
    moment = _moment("Week", args[0])
    if moment is None:
        return None
    start = int(to_number(args[1])) if len(args) > 1 else 0
    if not 0 <= start <= 6:
        raise ArcadeError("Week's start day is 0 (Sunday) to 6 (Saturday).")
    day = moment.timetuple().tm_yday - 1
    first_weekday = (datetime.date(moment.year, 1, 1).weekday() + 1) % 7
    to_first = (start - first_weekday) % 7
    return 0.0 if day < to_first else float((day - to_first) // 7 + 1)


def _add_months(moment, months):
    total = moment.month - 1 + months
    year, month = moment.year + total // 12, total % 12 + 1
    day = min(moment.day, calendar.monthrange(year, month)[1])
    return moment.replace(year=year, month=month, day=day)


def _units(name, value):
    unit = to_text(value or "milliseconds").strip().lower()
    if unit not in _UNITS and unit not in _CALENDAR:
        raise ArcadeError(f"{name}: '{unit}' is not a unit: milliseconds, seconds, minutes, hours, days, months or "
                          "years.")
    return unit


@builtin("DateAdd")
def _date_add(_, args):
    arity("DateAdd", args, 2, 3)
    unit = _units("DateAdd", args[2] if len(args) > 2 else None)
    amount = to_number(args[1])
    target = args[0]
    if isinstance(target, Time):
        if unit not in ("milliseconds", "millisecond", "seconds", "second", "minutes", "minute", "hours", "hour"):
            raise ArcadeError("DateAdd on a Time takes milliseconds, seconds, minutes or hours.")
        return Time((target.ms + int(amount * _UNITS[unit])) % 86400000)
    if isinstance(target, DateOnly):
        if unit in _CALENDAR:
            moment = datetime.datetime.combine(target.value, datetime.time())
            return DateOnly(_add_months(moment, int(round(amount)) * _CALENDAR[unit]).date())
        days = int(amount * _UNITS[unit] / 86400000.0)  # less than a day adds nothing, as documented
        return DateOnly(target.value + datetime.timedelta(days=days))
    date = as_date(target)
    if date is None:
        return None
    if unit in _CALENDAR:
        return Date(_add_months(date.value, int(round(amount)) * _CALENDAR[unit]))
    return Date(date.value + datetime.timedelta(milliseconds=amount * _UNITS[unit]))


def _month_difference(a, b):
    """``a - b`` in months: whole months, then the fraction of the next one."""
    whole = (a.year - b.year) * 12 + (a.month - b.month)
    anchor = _add_months(b, whole)
    if anchor > a:
        whole -= 1
        anchor = _add_months(b, whole)
    following = _add_months(b, whole + 1)
    span = (following - anchor).total_seconds()
    return whole + ((a - anchor).total_seconds() / span if span else 0.0)


@builtin("DateDiff")
def _date_diff(_, args):
    arity("DateDiff", args, 2, 4)
    unit = _units("DateDiff", args[2] if len(args) > 2 else None)
    a, b = args[0], args[1]
    if isinstance(a, Time) and isinstance(b, Time):
        if unit in _CALENDAR:
            raise ArcadeError("DateDiff between Times takes milliseconds, seconds, minutes or hours.")
        return (a.ms - b.ms) / _UNITS[unit]
    if isinstance(a, DateOnly) and isinstance(b, DateOnly):
        x = datetime.datetime.combine(a.value, datetime.time(), UTC)
        y = datetime.datetime.combine(b.value, datetime.time(), UTC)
    else:
        first, second = as_date(a), as_date(b)
        if first is None or second is None:
            return None
        x, y = first.value, second.value
    if unit in _CALENDAR:
        return _month_difference(x, y) / _CALENDAR[unit]
    return (x - y).total_seconds() * 1000.0 / _UNITS[unit]


@builtin("DateOnly")
def _date_only(_, args):
    if not args:
        return DateOnly(datetime.datetime.now(UTC).date())
    first = args[0]
    if len(args) == 1:
        if isinstance(first, DateOnly):
            return DateOnly(first.value)
        if isinstance(first, Date):
            return DateOnly(first.value.date())
        if is_number(first):
            return DateOnly((datetime.datetime.fromtimestamp(0, UTC)
                             + datetime.timedelta(milliseconds=to_number(first))).date())
        if isinstance(first, str):
            text = first.strip()
            try:
                return DateOnly(datetime.date.fromisoformat(text[:10]))
            except ValueError:
                return None
        if first is None:
            return None
        raise ArcadeError(f"DateOnly takes a number, text or a date; a {type_of(first)} was given.")
    if len(args) == 2:
        raise ArcadeError("DateOnly with a format pattern is not built in grapy yet: only ISO 8601 text is read "
                          "(ADR-235 §5.3).")
    arity("DateOnly", args, 3)
    year, month, day = (to_number(a) for a in args)
    try:
        return DateOnly(datetime.date(int(year), int(month) + 1, int(day)))
    except (ValueError, OverflowError):
        return None  # an out-of-range month or day answers null, as documented


_CLOCK = re.compile(r"^\s*(\d{1,2})(?::(\d{2}))?(?::(\d{2})(?:\.(\d{1,3}))?)?\s*([AaPp][Mm])?\s*$")


@builtin("Time")
def _time(_, args):
    if not args:
        now = datetime.datetime.now(UTC)
        return Time(((now.hour * 60 + now.minute) * 60 + now.second) * 1000 + now.microsecond // 1000)
    first = args[0]
    if len(args) == 1:
        if isinstance(first, Time):
            return Time(first.ms)
        if isinstance(first, Date):
            moment = first.value
            return Time(((moment.hour * 60 + moment.minute) * 60 + moment.second) * 1000 + moment.microsecond // 1000)
        if is_number(first):
            value = to_number(first)
            return Time(value) if 0 <= value <= 86400000 else None
        if isinstance(first, str):
            match = _CLOCK.match(first)
            if not match:
                return None
            hour, minute = int(match.group(1)), int(match.group(2) or 0)
            second, ms = int(match.group(3) or 0), int((match.group(4) or "0").ljust(3, "0"))
            half = (match.group(5) or "").upper()
            if half:
                if not 1 <= hour <= 12:
                    return None
                hour = hour % 12 + (12 if half == "PM" else 0)
            if hour > 23 or minute > 59 or second > 59:
                return None
            return Time(((hour * 60 + minute) * 60 + second) * 1000 + ms)
        if first is None:
            return None
        raise ArcadeError(f"Time takes a number, text or a date; a {type_of(first)} was given.")
    if len(args) == 2 and isinstance(first, str):
        raise ArcadeError("Time with a format pattern is not built in grapy yet: only hh:mm[:ss] text, with AM or "
                          "PM, is read (ADR-235 §5.3).")
    arity("Time", args, 2, 4)
    hour, minute = int(to_number(args[0])), int(to_number(args[1]))
    second = int(to_number(args[2])) if len(args) > 2 else 0
    ms = int(to_number(args[3])) if len(args) > 3 else 0
    if not (0 <= hour <= 23 and 0 <= minute <= 59 and 0 <= second <= 59 and 0 <= ms <= 999):
        return None
    return Time(((hour * 60 + minute) * 60 + second) * 1000 + ms)


_ = first_is
