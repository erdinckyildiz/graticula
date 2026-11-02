#!/usr/bin/env python3
"""Prints the `dotnet test --filter` that runs one part of the console suite.

Why this is a tool
------------------
The console suite drives a real browser one test at a time against one server, and
parallel runs inside it are off on purpose (`AssemblyInfo.cs`): the tests share the
server's state. It took about sixteen minutes of a CI run of twenty-five to thirty, and
the owner asked on 2026-11-02 for the run to be shorter. So CI runs it as several jobs,
each with its own server, and this decides which test classes each one runs.

**By class, never by test.** A class's tests may rely on the order they run in and on what
the class set up; splitting one across two servers would break exactly that.

**Balanced by the number of tests a class has** (each `[Fact]`, and each `[InlineData]`
row of a `[Theory]`), greedily, heaviest first. A count is a proxy for time and not time;
it is the proxy that needs no earlier run to read.

**Every class goes to exactly one part, and the tool says so.** A class that no part runs
is coverage the build claims and does not have (D-63), so `--check` fails the build when
the parts do not add up to the whole.

Usage:  python tools/console-shard.py <part> <parts>     prints the filter for part 1..parts
        python tools/console-shard.py --check <parts>     checks every class is in one part
"""

import io
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SUITE = os.path.join(ROOT, "tests", "Graticula.Console.Tests")
NAMESPACE = "Graticula.Console.Tests"


def classes():
    """Test classes and their test counts, read from the suite's source."""
    weights = {}

    for name in sorted(os.listdir(SUITE)):
        if not name.endswith(".cs"):
            continue

        text = io.open(os.path.join(SUITE, name), encoding="utf-8").read()
        found = list(re.finditer(r"^\s*(?:public|internal)\s+(?:sealed\s+|static\s+|partial\s+)*class\s+(\w+)", text, re.M))

        for index, match in enumerate(found):
            end = found[index + 1].start() if index + 1 < len(found) else len(text)
            body = text[match.end():end]
            tests = len(re.findall(r"\[Fact\b", body)) + len(re.findall(r"\[InlineData\b", body))

            if tests:
                weights[match.group(1)] = weights.get(match.group(1), 0) + tests

    return weights


def parts(count):
    """The classes of each part, heaviest first into the lightest part so far."""
    loads = [[0, []] for _ in range(count)]

    for name, weight in sorted(classes().items(), key=lambda item: (-item[1], item[0])):
        lightest = min(loads, key=lambda load: load[0])
        lightest[0] += weight
        lightest[1].append(name)

    return [sorted(names) for _, names in loads]


def main():
    if len(sys.argv) == 3 and sys.argv[1] == "--check":
        count = int(sys.argv[2])
        every = classes()
        split = parts(count)
        placed = [name for part in split for name in part]

        if len(every) < 50:
            print(f"only {len(every)} console test classes were found, so this is reading nothing.")
            return 1

        if sorted(placed) != sorted(every) or len(placed) != len(set(placed)):
            print("the parts do not add up to the suite: a class is in no part, or in two.")
            return 1

        for index, part in enumerate(split, 1):
            print(f"part {index}: {len(part)} classes, {sum(every[name] for name in part)} tests")

        return 0

    if len(sys.argv) != 3:
        print(__doc__.strip().splitlines()[-2].strip())
        return 2

    part, count = int(sys.argv[1]), int(sys.argv[2])

    if not 1 <= part <= count:
        print(f"part {part} is not one of 1..{count}")
        return 2

    # The trailing dot bounds the class: `…Tests.Foo.` matches Foo's tests and not FooBar's.
    print("|".join(f"FullyQualifiedName~{NAMESPACE}.{name}." for name in parts(count)[part - 1]))
    return 0


if __name__ == "__main__":
    sys.exit(main())
