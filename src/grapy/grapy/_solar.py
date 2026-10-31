"""The sun's position for the sun shadow tools — ADR-238.

The position is NOAA's Solar Calculator method, the low-accuracy solar coordinates of Meeus, *Astronomical
Algorithms* (2nd ed., 1998), chapter 25, with the equation of time of chapter 28 in its series form (Smart 1956), as
NOAA's Global Monitoring Laboratory publishes it (gml.noaa.gov/grad/solcalc/calcdetails.html). No atmospheric
refraction is applied: the shadow tools treat light rays as straight and parallel, as their pages say.

Time zones are named as the tools' pages name them — Windows' zone names with underscores (``Pacific_Standard_Time``)
and ``UTC``, ``UTC-11``, ``UTC+12`` — and each is read as its standard offset from UTC (ADR-238 §5.2, INFERRED). When a
tool is told the times are adjusted for daylight saving, every time it reads is taken as one hour ahead of standard.
"""
from __future__ import annotations

import datetime as _dt
import math
import re

# Standard (winter) offsets in hours of Windows' time zone names (CLDR's windowsZones mapping, offsets of the
# standard time of each zone).
ZONES = {
    "Dateline_Standard_Time": -12, "UTC-11": -11, "Aleutian_Standard_Time": -10, "Hawaiian_Standard_Time": -10,
    "Marquesas_Standard_Time": -9.5, "Alaskan_Standard_Time": -9, "UTC-09": -9, "UTC-9": -9,
    "Pacific_Standard_Time_(Mexico)": -8, "Pacific_Standard_Time_Mexico": -8, "UTC-08": -8, "UTC-8": -8,
    "Pacific_Standard_Time": -8, "US_Mountain_Standard_Time": -7, "Mountain_Standard_Time_(Mexico)": -7,
    "Mountain_Standard_Time_Mexico": -7, "Mountain_Standard_Time": -7, "Yukon_Standard_Time": -7,
    "Central_America_Standard_Time": -6, "Central_Standard_Time": -6, "Easter_Island_Standard_Time": -6,
    "Central_Standard_Time_(Mexico)": -6, "Central_Standard_Time_Mexico": -6, "Canada_Central_Standard_Time": -6,
    "SA_Pacific_Standard_Time": -5, "Eastern_Standard_Time_(Mexico)": -5, "Eastern_Standard_Time_Mexico": -5,
    "Eastern_Standard_Time": -5, "Haiti_Standard_Time": -5, "Cuba_Standard_Time": -5, "US_Eastern_Standard_Time": -5,
    "Turks_And_Caicos_Standard_Time": -5, "Paraguay_Standard_Time": -4, "Atlantic_Standard_Time": -4,
    "Venezuela_Standard_Time": -4, "Central_Brazilian_Standard_Time": -4, "SA_Western_Standard_Time": -4,
    "Pacific_SA_Standard_Time": -4, "Newfoundland_Standard_Time": -3.5, "Tocantins_Standard_Time": -3,
    "E._South_America_Standard_Time": -3, "E_South_America_Standard_Time": -3, "SA_Eastern_Standard_Time": -3,
    "Argentina_Standard_Time": -3, "Greenland_Standard_Time": -3, "Montevideo_Standard_Time": -3,
    "Magallanes_Standard_Time": -3, "Saint_Pierre_Standard_Time": -3, "Bahia_Standard_Time": -3, "UTC-02": -2,
    "UTC-2": -2, "Mid-Atlantic_Standard_Time": -2, "Azores_Standard_Time": -1, "Cape_Verde_Standard_Time": -1,
    "UTC": 0, "Coordinated_Universal_Time": 0, "GMT_Standard_Time": 0, "Greenwich_Standard_Time": 0,
    "Sao_Tome_Standard_Time": 0, "Morocco_Standard_Time": 0, "W._Europe_Standard_Time": 1,
    "W_Europe_Standard_Time": 1, "Central_Europe_Standard_Time": 1, "Romance_Standard_Time": 1,
    "Central_European_Standard_Time": 1, "W._Central_Africa_Standard_Time": 1, "W_Central_Africa_Standard_Time": 1,
    "Jordan_Standard_Time": 3, "GTB_Standard_Time": 2, "Middle_East_Standard_Time": 2, "Egypt_Standard_Time": 2,
    "E._Europe_Standard_Time": 2, "E_Europe_Standard_Time": 2, "Syria_Standard_Time": 3,
    "West_Bank_Standard_Time": 2, "South_Africa_Standard_Time": 2, "FLE_Standard_Time": 2, "Israel_Standard_Time": 2,
    "South_Sudan_Standard_Time": 2, "Kaliningrad_Standard_Time": 2, "Sudan_Standard_Time": 2,
    "Libya_Standard_Time": 2, "Namibia_Standard_Time": 2, "Arabic_Standard_Time": 3, "Turkey_Standard_Time": 3,
    "Arab_Standard_Time": 3, "Belarus_Standard_Time": 3, "Russian_Standard_Time": 3, "E._Africa_Standard_Time": 3,
    "E_Africa_Standard_Time": 3, "Volgograd_Standard_Time": 3, "Iran_Standard_Time": 3.5,
    "Arabian_Standard_Time": 4, "Astrakhan_Standard_Time": 4, "Azerbaijan_Standard_Time": 4,
    "Russia_Time_Zone_3": 4, "Mauritius_Standard_Time": 4, "Saratov_Standard_Time": 4, "Georgian_Standard_Time": 4,
    "Caucasus_Standard_Time": 4, "Afghanistan_Standard_Time": 4.5, "West_Asia_Standard_Time": 5,
    "Ekaterinburg_Standard_Time": 5, "Pakistan_Standard_Time": 5, "Qyzylorda_Standard_Time": 5,
    "India_Standard_Time": 5.5, "Sri_Lanka_Standard_Time": 5.5, "Nepal_Standard_Time": 5.75,
    "Central_Asia_Standard_Time": 6, "Bangladesh_Standard_Time": 6, "Omsk_Standard_Time": 6,
    "Myanmar_Standard_Time": 6.5, "SE_Asia_Standard_Time": 7, "Altai_Standard_Time": 7,
    "W._Mongolia_Standard_Time": 7, "W_Mongolia_Standard_Time": 7, "North_Asia_Standard_Time": 7,
    "N._Central_Asia_Standard_Time": 7, "N_Central_Asia_Standard_Time": 7, "Tomsk_Standard_Time": 7,
    "China_Standard_Time": 8, "North_Asia_East_Standard_Time": 8, "Singapore_Standard_Time": 8,
    "W._Australia_Standard_Time": 8, "W_Australia_Standard_Time": 8, "Taipei_Standard_Time": 8,
    "Ulaanbaatar_Standard_Time": 8, "Aus_Central_W._Standard_Time": 8.75, "Aus_Central_W_Standard_Time": 8.75,
    "Transbaikal_Standard_Time": 9, "Tokyo_Standard_Time": 9, "North_Korea_Standard_Time": 9,
    "Korea_Standard_Time": 9, "Yakutsk_Standard_Time": 9, "Cen._Australia_Standard_Time": 9.5,
    "Cen_Australia_Standard_Time": 9.5, "AUS_Central_Standard_Time": 9.5, "E._Australia_Standard_Time": 10,
    "E_Australia_Standard_Time": 10, "AUS_Eastern_Standard_Time": 10, "West_Pacific_Standard_Time": 10,
    "Tasmania_Standard_Time": 10, "Vladivostok_Standard_Time": 10, "Lord_Howe_Standard_Time": 10.5,
    "Bougainville_Standard_Time": 11, "Russia_Time_Zone_10": 11, "Magadan_Standard_Time": 11,
    "Norfolk_Standard_Time": 11, "Sakhalin_Standard_Time": 11, "Central_Pacific_Standard_Time": 11,
    "Russia_Time_Zone_11": 12, "New_Zealand_Standard_Time": 12, "UTC+12": 12, "Fiji_Standard_Time": 12,
    "Kamchatka_Standard_Time": 12, "Chatham_Islands_Standard_Time": 12.75, "UTC+13": 13, "Tonga_Standard_Time": 13,
    "Samoa_Standard_Time": 13, "Line_Islands_Standard_Time": 14,
}


def offset(time_zone, default=0.0):
    """Hours east of UTC of a time zone name; ``UTC±h[:mm]`` is read as written."""
    if time_zone in (None, "", "#"):
        return float(default)
    name = str(time_zone).strip().replace(" ", "_")
    for key, hours in ZONES.items():
        if key.lower() == name.lower():
            return float(hours)
    found = re.fullmatch(r"(?i)UTC([+-])(\d{1,2})(?::?(\d{2}))?", name)
    if found:
        hours = int(found.group(2)) + int(found.group(3) or 0) / 60
        return hours if found.group(1) == "+" else -hours
    raise ValueError(f"'{time_zone}' is not a time zone name grapy reads: write a Windows zone name such as "
                     "Pacific_Standard_Time, or UTC±h.")


def to_utc(local, zone_hours, dst=False):
    """A naive local date and time as UTC."""
    return local - _dt.timedelta(hours=zone_hours + (1 if dst else 0))


def julian_day(utc):
    """The Julian day of a naive UTC datetime (Meeus ch. 7)."""
    y, m = utc.year, utc.month
    d = utc.day + (utc.hour + (utc.minute + (utc.second + utc.microsecond / 1e6) / 60) / 60) / 24
    if m <= 2:
        y, m = y - 1, m + 12
    a = y // 100
    b = 2 - a + a // 4
    return math.floor(365.25 * (y + 4716)) + math.floor(30.6001 * (m + 1)) + d + b - 1524.5


def coordinates(jd):
    """(apparent longitude, declination, equation of time in minutes) at a Julian day, degrees."""
    t = (jd - 2451545.0) / 36525.0
    l0 = (280.46646 + t * (36000.76983 + t * 0.0003032)) % 360
    m = 357.52911 + t * (35999.05029 - 0.0001537 * t)
    e = 0.016708634 - t * (0.000042037 + 0.0000001267 * t)
    mr = math.radians(m)
    c = (math.sin(mr) * (1.914602 - t * (0.004817 + 0.000014 * t)) + math.sin(2 * mr) * (0.019993 - 0.000101 * t)
         + math.sin(3 * mr) * 0.000289)
    omega = 125.04 - 1934.136 * t
    apparent = l0 + c - 0.00569 - 0.00478 * math.sin(math.radians(omega))
    eps0 = 23 + (26 + (21.448 - t * (46.815 + t * (0.00059 - t * 0.001813))) / 60) / 60
    eps = eps0 + 0.00256 * math.cos(math.radians(omega))
    declination = math.degrees(math.asin(math.sin(math.radians(eps)) * math.sin(math.radians(apparent))))
    y = math.tan(math.radians(eps) / 2) ** 2
    l0r = math.radians(l0)
    eot = 4 * math.degrees(y * math.sin(2 * l0r) - 2 * e * math.sin(mr) + 4 * e * y * math.sin(mr) * math.cos(2 * l0r)
                           - 0.5 * y * y * math.sin(4 * l0r) - 1.25 * e * e * math.sin(2 * mr))
    return apparent % 360, declination, eot


def position(utc, longitude, latitude):
    """(azimuth clockwise from true north, elevation above the horizon) of the sun, degrees, at a naive UTC datetime
    and a place (longitude east positive)."""
    _, declination, eot = coordinates(julian_day(utc))
    minutes = utc.hour * 60 + utc.minute + (utc.second + utc.microsecond / 1e6) / 60
    true_solar = (minutes + eot + 4 * longitude) % 1440
    hour_angle = true_solar / 4 - 180
    lat, dec, ha = math.radians(latitude), math.radians(declination), math.radians(hour_angle)
    cos_zenith = math.sin(lat) * math.sin(dec) + math.cos(lat) * math.cos(dec) * math.cos(ha)
    elevation = 90 - math.degrees(math.acos(max(-1.0, min(1.0, cos_zenith))))
    azimuth = (math.degrees(math.atan2(math.sin(ha), math.cos(ha) * math.sin(lat) - math.tan(dec) * math.cos(lat)))
               + 180) % 360
    return azimuth, elevation


def towards(azimuth, elevation):
    """The unit vector from the ground towards the sun (x east, y north, z up)."""
    a, e = math.radians(azimuth), math.radians(elevation)
    return (math.sin(a) * math.cos(e), math.cos(a) * math.cos(e), math.sin(e))


def place(x, y, srid):
    """(longitude, latitude) of a point in a coordinate system."""
    if not srid:
        raise ValueError("the sun's position needs the data's coordinate system, and the input has none.")
    from pyproj import Transformer
    lon, lat = Transformer.from_crs(int(srid), 4326, always_xy=True).transform(x, y)
    return lon, lat


def parse_time(value, tool):
    """A date and time from a datetime or the text forms a script passes ("12/25/2011 10:00 AM", ISO 8601)."""
    if isinstance(value, _dt.datetime):
        return value.replace(tzinfo=None)
    if isinstance(value, _dt.date):
        return _dt.datetime(value.year, value.month, value.day)
    text = str(value).strip()
    for form in ("%m/%d/%Y %I:%M:%S %p", "%m/%d/%Y %I:%M %p", "%m/%d/%Y %H:%M:%S", "%m/%d/%Y %H:%M", "%m/%d/%Y",
                 "%Y-%m-%dT%H:%M:%S", "%Y-%m-%d %H:%M:%S", "%Y-%m-%dT%H:%M", "%Y-%m-%d %H:%M", "%Y-%m-%d"):
        try:
            return _dt.datetime.strptime(text, form)
        except ValueError:
            continue
    for form in ("%I:%M:%S %p", "%I:%M %p", "%H:%M:%S", "%H:%M"):
        try:
            return _dt.datetime.strptime(text, form).time()
        except ValueError:
            continue
    raise ValueError(f"arcpy.ddd.{tool}: '{value}' is not a date and time grapy reads (write 12/25/2011 10:00 AM).")
