# The Python job worker — ADR-037's second kind.
#
# GDAL comes from the base image and from nowhere else. The serving container has none (A-016,
# ADR-009 §2.2), and this is the only artefact that carries it — which is why the rule is checkable
# at build time rather than by inspection.
#
# `ubuntu-small` rather than `ubuntu-full`: the full image carries the proprietary and
# patent-encumbered drivers, and DEPENDENCY-LICENSES.md's own warning is that a GDAL build is a bill
# of materials rather than one licence. A smaller driver set is a smaller bill. ADR-037 condition 2
# owes an enumeration of what is in it before this ships.
FROM ghcr.io/osgeo/gdal:ubuntu-small-3.10.3

# <b>Pinned to a release, and the debt came due immediately rather than eventually.</b> The first
# version of this file said `ubuntu-small-latest` with a note that pinning was owed. `latest` is
# Ubuntu 25.10 with **Python 3.14**, for which `pyogrio` and `pyarrow` have no wheels yet — so pip
# fell back to building from source and the image has no compiler. The build failed with
# `No such file or directory: 'x86_64-linux-gnu-gcc'`.
#
# **The lesson is not that the pin was tidier.** A curated wheel set exists so that two builds of the
# same image are the same image (ADR-016 §7), and a floating base is the one input that makes that
# impossible. Here it also put the base ahead of the wheel ecosystem, which is a failure mode nobody
# would predict from *"use the latest patch"*.
#
# 3.10.3 is Ubuntu 24.04 with Python 3.12, which the wheels cover. A digest pin is stronger still and
# is owed when this leaves the development machine — a tag can move.

RUN apt-get update \
 && apt-get install -y --no-install-recommends python3-pip \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /worker

COPY requirements.txt .

# `--no-cache-dir` keeps the layer to what is installed; `--break-system-packages` is the base
# image's Debian policy, and this container has one job and no other Python to protect.
RUN pip3 install --no-cache-dir --break-system-packages -r requirements.txt

COPY worker.py .

# <b>Runs as nobody.</b> It reads an archive a stranger uploaded, which is the one process in this
# product whose input is a file chosen by somebody else — security.md's upload rules are about
# exactly this, and root is not a defence anybody would choose on purpose.
USER nobody

# Unbuffered, because the contract is line-at-a-time over a pipe and Python's default buffering
# makes a responsive worker look hung.
ENV PYTHONUNBUFFERED=1

ENTRYPOINT ["python3", "worker.py"]
