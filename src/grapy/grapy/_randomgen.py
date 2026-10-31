"""arcpy's random number generators — CreateRandomValueGenerator, the RandomNumberGenerator class and the Random
Number Generator environment (ADR-247 §5.2).

The pages (doc.esri.com, read 2026-10-31: ``arcpy/functions/createrandomvaluegenerator.html``,
``arcpy/classes/randomnumbergenerator.html``, ``tool-reference/environment-settings/random-number-generator.html``)
name three generators — ``ACM599`` (*"ACM collected algorithm 599"*, the default), ``MERSENNE_TWISTER`` (*"Mersenne
Twister mt19937"*) and ``STANDARD_C`` (*"Standard C Rand"*) — and a seed, an integer, 0 by default. The environment
takes ``"<seed> <generator>"``, a seed alone, or a RandomNumberGenerator; the object's string form is the same
``"<seed> <generator>"`` (``exportToString``, ``loadFromString``).

What a tool draws with (``_numpy_seed``, which ``management._seed`` answers):

- ``MERSENNE_TWISTER``: NumPy's MT19937, its state set by the published ``init_genrand`` seeding of Matsumoto and
  Nishimura (1998) — NumPy's ``RandomState(seed)``, whose first output for seed 5489 is 3499211612 and whose 10,000th
  is 4123659995, the value the C++ standard gives for ``std::mt19937`` ([rand.predef]). Whether ArcGIS seeds its
  mt19937 the same way is not published, and the draws a tool makes from the raw stream are NumPy's, so the answers
  are an EQUIVALENT of Pro's, not Pro's.
- ``ACM599`` and ``STANDARD_C``: NumPy's PCG64 from the seed, as grapy's tools drew before this module. INFERRED:
  Algorithm 599's uniform generator and the C library's ``rand`` are not built — C leaves ``rand`` to each
  implementation (ISO/IEC 9899:2018 §7.22.2.1 gives one only as an example), so no stream is *the* STANDARD_C stream.

Each tool starts its draws from the seed, so running a tool twice with the same seed gives the same output, as the
environment page says. INFERRED: the page's global stream, from which each later tool in a model draws the next value,
is not kept — a tool's answer does not depend on what ran before it.
"""
from __future__ import annotations

from ._runtime import equivalent

GENERATORS = ("ACM599", "MERSENNE_TWISTER", "STANDARD_C")


def _parse(text):
    """(seed, generator) of ``"<seed> <generator>"`` or ``"<seed>"``; an unknown generator is an error."""
    parts = str(text).split()
    if not parts:
        return 0, "ACM599"
    try:
        seed = int(float(parts[0]))
    except ValueError:
        raise ValueError(f"'{text}' is not a random generator: it is a seed, an integer, then optionally one of "
                         f"{', '.join(GENERATORS)}.") from None
    generator = parts[1].upper() if len(parts) > 1 else "ACM599"
    if generator not in GENERATORS or len(parts) > 2:
        raise ValueError(f"'{text}' is not a random generator: the generator is one of {', '.join(GENERATORS)}.")
    return seed, generator


class RandomNumberGenerator:
    """The type and seed used to create random numbers. Its page documents two methods, ``exportToString`` and
    ``loadFromString``; INFERRED: the constructor takes the seed and the generator, defaulting to 0 and ACM599, the
    environment's defaults, since the page documents no constructor."""

    def __init__(self, seed=0, distribution="ACM599"):
        self._seed, self._generator = _parse(f"{int(seed)} {distribution or 'ACM599'}")

    def exportToString(self):
        """The object as its string, ``"<seed> <generator>"`` — what the environment takes."""
        return f"{self._seed} {self._generator}"

    def loadFromString(self, string):
        """Sets the object from a space-delimited seed and generator, such as ``"0 ACM599"``."""
        self._seed, self._generator = _parse(string)

    def __str__(self):
        return self.exportToString()

    def __repr__(self):
        return f"<RandomNumberGenerator {self.exportToString()}>"

    def __eq__(self, other):
        return isinstance(other, RandomNumberGenerator) and self.exportToString() == other.exportToString()

    def __hash__(self):
        return hash(self.exportToString())


@equivalent("MERSENNE_TWISTER as NumPy's MT19937 seeded by init_genrand; ACM599 and STANDARD_C as NumPy's PCG64 "
            "from the seed", "Matsumoto and Nishimura 1998; O'Neill 2014")
def CreateRandomValueGenerator(seed, distribution="ACM599"):
    """A RandomNumberGenerator of the seed and generator, which ``arcpy.env.randomGenerator`` takes."""
    return RandomNumberGenerator(seed, distribution)


def _setting(value):
    """(seed, generator) of what ``env.randomGenerator`` holds: nothing (0, ACM599), a number, a string or an object."""
    if value is None:
        return 0, "ACM599"
    if isinstance(value, RandomNumberGenerator):
        return value._seed, value._generator
    if isinstance(value, (int, float)):
        return int(value), "ACM599"
    if str(value).strip() in ("", "#"):
        return 0, "ACM599"
    return _parse(value)


def _numpy_seed(value):
    """What ``numpy.random.default_rng`` is seeded with for this setting: the seed itself (PCG64), or for
    MERSENNE_TWISTER an MT19937 bit generator in the state ``init_genrand(seed mod 2**32)`` gives. INFERRED: a seed
    outside 0 … 2**32 − 1 is taken modulo 2**32, the width of mt19937's seed."""
    import numpy
    seed, generator = _setting(value)
    if generator != "MERSENNE_TWISTER":
        return seed
    bits = numpy.random.MT19937()
    bits.state = numpy.random.RandomState(seed % 2 ** 32).get_state(legacy=False)
    return bits
