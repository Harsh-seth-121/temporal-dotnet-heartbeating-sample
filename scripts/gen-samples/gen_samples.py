#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.11,<3.14"
# dependencies = []
# ///
"""Generate large sample files of indexed random-word rows.

    N
    0000000001 [amber pebble willow amber lantern brook copper]

Line 1 is the row count. Every row is a 10-digit zero-padded index, a space, then seven
words from words-1024.txt in brackets. Words may repeat WITHIN a row; no two rows are
identical and no two share a seven-word tuple, a guarantee rather than a probability. S
"""

import argparse
import contextlib
import hashlib
import io
import os
import re
import shutil
import sys
import tempfile
import textwrap
import time
from enum import Enum, IntEnum, auto
from pathlib import Path
from typing import NamedTuple, Optional

# --- format ----------------------------------------------------------------
# Row bytes: 10 index digits + ' ' + '[' + 7 words + 6 inner spaces + ']' + '\n'.
# Everything except the words themselves is fixed, hence ROW_OVERHEAD.
IDX_WIDTH = 10          # holds 9,999,999,999 rows, well past the billion asked for
WORDS_PER_ROW = 7
ROW_OVERHEAD = IDX_WIDTH + 1 + 1 + (WORDS_PER_ROW - 1) + 1 + 1   # == 20
ROW_FMT = b"%010d [%s %s %s %s %s %s %s]\n"
ROW_RE = re.compile(rb"^[0-9]{10} \[[a-z]+(?: [a-z]+){6}\]$")

# --- the bijection ---------------------------------------------------------
# WORD_COUNT must be a power of two and BITS_PER_WORD its log2. 1024**7 == 2**70, so a
# bijection on 70 bits IS a bijection on the space of seven-word tuples.
WORD_COUNT = 1024
BITS_PER_WORD = 10
WORD_MASK = WORD_COUNT - 1
HALF_BITS = (BITS_PER_WORD * WORDS_PER_ROW) // 2   # 35
HALF_MASK = (1 << HALF_BITS) - 1
ROUNDS = 4

M64 = 0xFFFFFFFFFFFFFFFF
GOLDEN = 0x9E3779B97F4A7C15
MIX_A = 0xBF58476D1CE4E5B9
MIX_B = 0x94D049BB133111EB

# --- io --------------------------------------------------------------------
FLUSH_BYTES = 1 << 20    # buffer rows to ~1 MiB, never one write() per row
COPY_CHUNK = 1 << 22     # 4 MiB, used for the header-prepend copy

DEFAULT_SIZES = "100MB,200MB,350MB,500MB"
DEFAULT_SEED = 1234567890123456789
BENCH_ROWS = 1000000

# Known answer for the documented stream: the seven word INDICES of row 1 of the 200 MB
# file recorded in MANIFEST.txt, i.e. word_indices(tuple_bits(1, round_keys(file_seed(
# DEFAULT_SEED, KNOWN_TARGET)))). Indices, not words, so a custom --words list cannot fail
# it; what it pins is the arithmetic. Change this constant and every sha256 in MANIFEST.txt
# and the 1.15 GB on disk describe something else, so regenerate rather than re-record.
KNOWN_TARGET = 200_000_000
KNOWN_ROW1_INDICES = (159, 181, 495, 262, 518, 393, 413)
# Enough rows to cross the 10/100/1000 odometer boundaries inside generate_body, which
# is where an inlined hot loop is most likely to disagree with the reference.
REFERENCE_ROWS = 2000

SCRIPT_DIR = Path(__file__).absolute().parent


# Exit codes. Kept in sync with README.md and with gen-samples.sh.
class Exit(IntEnum):
    def __new__(cls, code, label):
        member = int.__new__(cls, code)
        member._value_ = code
        member.label = label
        return member

    OK = 0,
    USAGE = 2,
    NO_INTERPRETER = 3,
    BAD_WORDS = 4, "bad word list"
    EXISTS = 5, "output exists without --force"
    VERIFY = 6, "verify failed"
    SELFTEST = 7, "selftest failed"
    IO = 8, "cannot write output"


def info(msg):
    print(msg, flush=True)


def warn(msg):
    print(f"WARNING: {msg}", file=sys.stderr, flush=True)


def die(code, msg):
    print(f"gen-samples: {msg}", file=sys.stderr, flush=True)
    raise SystemExit(code)


def mix(z):
    """SplitMix64 finalizer. Bijective on 64 bits, which is the only property used."""
    z = (z + GOLDEN) & M64
    z = ((z ^ (z >> 30)) * MIX_A) & M64
    z = ((z ^ (z >> 27)) * MIX_B) & M64
    return z ^ (z >> 31)


def round_keys(seed):
    return tuple(mix(seed + r * GOLDEN) for r in range(ROUNDS))


def tuple_bits(i, keys):
    """Map a row index to 70 bits, injectively.

    A balanced Feistel network is a bijection for ANY round function, so distinct i
    can never collide. That is what makes tuple uniqueness a guarantee with zero
    memory: no dedupe set, nothing that grows with the 8.6M rows of a 500 MB file.

    Reference implementation. The generator inlines this for speed, and check_reference
    below is what holds the two together: --selftest runs both over the same rows and
    fails on the first disagreement. It has to, because nothing else notices. --verify
    is handed a path and no seed, so all it can check is that the tuples are DISTINCT,
    and any injective map satisfies that -- including a broken one that rewrote the
    whole corpus.
    """
    left = (i >> HALF_BITS) & HALF_MASK
    right = i & HALF_MASK
    for k in keys:
        left, right = right, left ^ (mix(right ^ k) & HALF_MASK)
    return (left << HALF_BITS) | right


def word_indices(bits):
    return [(bits >> (BITS_PER_WORD * s)) & WORD_MASK for s in range(WORDS_PER_ROW)]


def load_words(path):
    """Read and hard-validate the word list.

    Every failure here is fatal rather than a warning. A quietly 1023-entry list would
    not raise anything; it would just silently void the uniqueness guarantee, because
    1023**7 is not a power of two and the bit-slicing above would stop being a
    bijection onto the tuple space.
    """
    if not os.path.isfile(path):
        die(Exit.BAD_WORDS, f"word list not found: {path}")
    with open(path, "r") as fh:
        words = [line.strip() for line in fh]
    words = [w for w in words if w]
    if len(words) != WORD_COUNT:
        die(Exit.BAD_WORDS,
            f"word list must hold exactly {WORD_COUNT} entries, "
            f"found {len(words)} in {path}")
    if len(set(words)) != WORD_COUNT:
        die(Exit.BAD_WORDS, f"word list has duplicate entries: {path}")
    bad = [w for w in words if not re.match(r"^[a-z]{3,9}$", w)]
    if bad:
        die(Exit.BAD_WORDS,
            f"word list entries must match ^[a-z]{{3,9}}$, "
            f"offenders: {', '.join(bad[:5])}")
    return words


def parse_size(text):
    """'100MB' -> 100000000, '100MiB' -> 104857600, '512' -> 512.

    MB is decimal because that is what Finder and `ls -lh` report on macOS. The MiB
    spelling is there for anyone who wants the binary reading and says so.
    """
    m = re.match(r"^\s*(\d+)\s*([KMGkmg]i?[Bb]?|)\s*$", text)
    if not m:
        die(Exit.USAGE, f"cannot parse size {text!r} (try 100MB, 100MiB, or a byte count)")
    n = int(m.group(1))
    unit = m.group(2).lower()
    if unit in ("", "b"):
        return n
    binary = "i" in unit
    power = {"k": 1, "m": 2, "g": 3}[unit[0]]
    return n * ((1024 if binary else 1000) ** power)


def size_label(text):
    return re.sub(r"\s+", "", text).lower()


def output_name(text):
    return f"sample-{size_label(text)}.txt"


def file_seed(base, target):
    """Per-file seed derived from the TARGET SIZE, not from position in --sizes.

    `--sizes 200MB` on its own must reproduce byte-for-byte what the default four-file run
    produced for 200MB. Keying on list position would make the same file differ depending
    on what it was generated alongside, quietly invalidating MANIFEST.txt.
    """
    return mix(base + target)


def generate_body(handle, target, seed, words, max_rows=None):
    """Write body rows to handle. Returns (rows, body_bytes).

    Stops before the first row that would push header+body past target, or at
    max_rows if given. max_rows exists for --bench, which wants a fixed row count
    rather than a fixed byte count and has no business guessing the average row
    width to convert between them. Header length
    is monotonic in the row count, so testing it inside the loop is correct and needs
    no second solve; digits is tracked as an odometer rather than via str(i) because
    this runs eight million times.

    This is the hot loop, so mix() and tuple_bits() are inlined and every constant is
    bound to a local. Keep it in step with the reference versions above.
    """
    encoded = [w.encode("ascii") for w in words]
    lengths = [len(w) for w in encoded]
    keys = round_keys(seed)

    m64 = M64
    half_mask = HALF_MASK
    half_bits = HALF_BITS
    word_mask = WORD_MASK
    golden = GOLDEN
    mix_a = MIX_A
    mix_b = MIX_B
    overhead = ROW_OVERHEAD
    fmt = ROW_FMT
    flush_at = FLUSH_BYTES
    write = handle.write

    buf = []
    buf_len = 0
    body = 0
    rows = 0
    digits = 1
    next_decade = 10

    while True:
        i = rows + 1
        if max_rows is not None and i > max_rows:
            break
        if i >= next_decade:
            digits += 1
            next_decade *= 10

        left = (i >> half_bits) & half_mask
        right = i & half_mask
        for k in keys:
            z = ((right ^ k) + golden) & m64
            z = ((z ^ (z >> 30)) * mix_a) & m64
            z = ((z ^ (z >> 27)) * mix_b) & m64
            z ^= z >> 31
            left, right = right, left ^ (z & half_mask)
        bits = (left << half_bits) | right

        a = bits & word_mask
        b = (bits >> 10) & word_mask
        c = (bits >> 20) & word_mask
        d = (bits >> 30) & word_mask
        e = (bits >> 40) & word_mask
        f = (bits >> 50) & word_mask
        g = (bits >> 60) & word_mask

        row_len = (overhead + lengths[a] + lengths[b] + lengths[c] + lengths[d]
                   + lengths[e] + lengths[f] + lengths[g])

        if digits + 1 + body + row_len > target:
            break

        buf.append(fmt % (i, encoded[a], encoded[b], encoded[c], encoded[d],
                          encoded[e], encoded[f], encoded[g]))
        buf_len += row_len
        body += row_len
        rows = i

        if buf_len >= flush_at:
            write(b"".join(buf))
            buf = []
            buf_len = 0

    if buf:
        write(b"".join(buf))
    return rows, body


def check_reference(words):
    """Fail if the emitted stream has drifted from the documented one.

    Two halves, because they catch different edits. The known answer pins the reference
    itself against the stream MANIFEST.txt was built from; the row-by-row comparison
    pins generate_body's inlined copy against the reference. Neither alone is enough: a
    maintainer who "tidies" the hot loop breaks only the second, and one who tidies both
    the same way breaks only the first.

    Without this the reference above is unreachable code and the comment telling you to
    keep the two in agreement is unenforced. --selftest would still pass, because it
    compares one build to itself, and --verify would still pass, because distinct tuples
    are all it looks for -- while every corpus on disk quietly became a different file.
    """
    seed = file_seed(DEFAULT_SEED, KNOWN_TARGET)
    keys = round_keys(seed)
    known = tuple(word_indices(tuple_bits(1, keys)))
    if known != KNOWN_ROW1_INDICES:
        die(Exit.SELFTEST,
            "the bijection no longer produces the stream MANIFEST.txt records: row 1 "
            f"of the {KNOWN_TARGET} target is {known}, expected {KNOWN_ROW1_INDICES}")

    sink = io.BytesIO()
    # 1 << 62 disables the byte budget so max_rows is what ends the loop, same as bench().
    produced, _body = generate_body(sink, 1 << 62, seed, words, max_rows=REFERENCE_ROWS)
    lines = sink.getvalue().splitlines(True)
    if produced != REFERENCE_ROWS or len(lines) != REFERENCE_ROWS:
        die(Exit.SELFTEST,
            f"asked generate_body for {REFERENCE_ROWS} rows, got {len(lines)}")

    encoded = [w.encode("ascii") for w in words]
    for i in range(1, REFERENCE_ROWS + 1):
        idx = word_indices(tuple_bits(i, keys))
        expected = ROW_FMT % ((i,) + tuple(encoded[j] for j in idx))
        if lines[i - 1] != expected:
            got = lines[i - 1].rstrip(b"\n")
            want = expected.rstrip(b"\n")
            die(Exit.SELFTEST,
                f"generate_body disagrees with the reference bijection at row {i}\n"
                f"  generator {got!r}\n  reference {want!r}")
    return REFERENCE_ROWS


@contextlib.contextmanager
def published_atomically(final_path, mode):
    """Open a sibling '.<name>.part', hand it over, and os.replace it into place.

    Nothing ever appears under the final name half-written. os.replace is atomic within a
    filesystem and the staging file sits in final_path's own directory, so it is the same
    filesystem by construction. If the body raises, the staging file goes and final_path is
    left alone; the exception keeps propagating, so callers wanting an exit code rather than
    a traceback catch it OUTSIDE the with. Plain open, not mkstemp, because mkstemp hands
    back 0600 instead of the umask-respecting mode every other file here gets. The staging
    path uses final_path AS SPELLED, not an abspath, since os.replace only needs siblings.
    """
    final = Path(final_path)
    staged = final.parent / f".{final.name}.part"
    published = False
    try:
        with open(staged, mode) as handle:
            yield handle
        os.replace(staged, final)
        published = True
    finally:
        if not published and staged.exists():
            staged.unlink()


def finalize(tmp_path, final_path, rows):
    """Prepend the header and hash the result in the same pass.

    The row count is only known once the body exists and the header is variable width, so
    there is no reserving space up front. One copy beats a second generation pass and reads
    every byte anyway, so the sha256 for MANIFEST.txt is free. It lands in a second temp
    published with os.replace, never streamed into final_path, and costs about a third of a
    second on the 500 MB file. A partial write under the FINAL name is the worst wreck
    available: valid header, thousands of valid rows, nothing saying the run did not finish,
    and the next run refusing with "pass --force". With --force it is worse, because
    open(final_path, "wb") truncates a good corpus before one new byte is known writable.
    """
    digest = hashlib.sha256()
    header = f"{rows}\n".encode("ascii")
    total = len(header)
    with published_atomically(final_path, "wb") as out:
        out.write(header)
        digest.update(header)
        with open(tmp_path, "rb") as src:
            while True:
                chunk = src.read(COPY_CHUNK)
                if not chunk:
                    break
                out.write(chunk)
                digest.update(chunk)
                total += len(chunk)
    return total, digest.hexdigest()


class ManifestRow(NamedTuple):
    name: str
    target: int
    bytes: int
    rows: int
    sha256: str


def generate_one(out_dir, size_text, base_seed, words, force):
    target = parse_size(size_text)
    name = output_name(size_text)
    final_path = os.path.join(out_dir, name)
    tmp_path = os.path.join(out_dir, "." + name + ".tmp")

    if os.path.exists(final_path) and not force:
        die(Exit.EXISTS, f"{final_path} already exists. Pass --force to overwrite it.")

    seed = file_seed(base_seed, target)
    started = time.monotonic()
    try:
        try:
            with open(tmp_path, "wb", buffering=0) as tmp:
                rows, _body = generate_body(tmp, target, seed, words)
            if rows == 0:
                die(Exit.USAGE,
                    f"target {size_text} is too small to hold even one row")
            size, digest = finalize(tmp_path, final_path, rows)
        except OSError as exc:
            # A full or read-only volume is the ordinary way to get here, and without this
            # the user gets a raw traceback out of the middle of finalize(), which reads as
            # a tool bug rather than as "your disk is full". SystemExit from die() is not an
            # OSError, so the rows==0 path above still passes through.
            die(Exit.IO, f"cannot write {final_path}: {exc}")
    finally:
        if os.path.exists(tmp_path):
            os.remove(tmp_path)
    elapsed = time.monotonic() - started

    info(f"  {name:<20} {rows:10d} rows  {size:12d} B  "
         f"(target {target}, {target - size} under)  {elapsed:5.1f}s")
    return ManifestRow(name, target, size, rows, digest)


def read_manifest(path):
    entries = {}
    if not Path(path).is_file():
        return entries
    with open(path, "r") as fh:
        for line in fh:
            line = line.strip()
            if not line or line.startswith("#"):
                continue
            parts = line.split()
            if len(parts) != 5:
                continue
            row = ManifestRow(parts[0], int(parts[1]), int(parts[2]),
                              int(parts[3]), parts[4])
            entries[row.name] = row
    return entries


class HeaderState(Enum):
    ABSENT = auto()
    # The state read_manifest_header reports when the file HAS a "# base seed" line
    # that will not parse. Deliberately distinct from "no header line at all", which is
    # the documented opt-out for a hand-written manifest.
    MALFORMED = auto()
    PARSED = auto()


class ManifestHeader(NamedTuple):
    state: HeaderState
    seed: Optional[int] = None
    out: Optional[str] = None


def read_manifest_header(path):
    r"""Recover the (base seed, out dir) a manifest was written for.

    HeaderState.ABSENT covers a missing file or one with no such comment: that is how a
    hand-written or pre-header manifest opts out of the identity check instead of tripping
    it. A line that starts like a header and will not parse is MALFORMED, which
    manifest_conflict refuses, since this tool only writes headers this pattern reads back.
    The seed pattern is -?\d+ rather than \d+ because argparse accepts `--seed=-5` and
    write_manifest formats it with %d. A header written one command ago and unreadable now
    reports ABSENT, manifest_conflict reads ABSENT as consent, and the guard is off for that
    manifest permanently, for every later seed and every later --out.
    """
    if not Path(path).is_file():
        return ManifestHeader(HeaderState.ABSENT)
    malformed = False
    with open(path, "r") as fh:
        for line in fh:
            m = re.match(r"^#\s*base seed\s+(-?\d+)\s+out\s+(\S.*?)\s*$", line)
            if m:
                return ManifestHeader(HeaderState.PARSED, int(m.group(1)), m.group(2))
            if re.match(r"^#\s*base seed\b", line):
                malformed = True
    if malformed:
        return ManifestHeader(HeaderState.MALFORMED)
    return ManifestHeader(HeaderState.ABSENT)


def parent_dir(path):
    """The absolute directory holding `path`, with symlinks left unresolved."""
    return Path(os.path.abspath(path)).parent


def resolve_recorded_out(manifest_path, recorded):
    """Absolute directory for a manifest header's `out` value, or None if undecidable.

    write_manifest records the path relative to the manifest's own directory, so the first
    candidate below is exact from any working directory. Older manifests recorded the raw
    --out string, "sample_files" rather than today's "../../sample_files". Resolving that
    against the CURRENT cwd, as this used to, is how `cd /tmp && gen-samples.sh` merged a run
    that wrote /tmp/sample_files into the manifest describing the repository's, while
    `--out <repo>/sample_files` was refused. A relative value is therefore tried against the
    manifest's directory and then each parent, deepest first, first hit wins: a pre-fix
    scripts/gen-samples/MANIFEST.txt resolves "sample_files" to <repo>/sample_files, and
    deepest-first stays deterministic when two ancestors both hold one. None means
    undecidable, and manifest_conflict refuses rather than guessing.
    """
    recorded_path = Path(recorded)
    if recorded_path.is_absolute():
        return recorded_path.resolve()
    base = parent_dir(manifest_path)
    for directory in (base, *base.parents):
        candidate = directory / recorded_path
        if candidate.is_dir():
            return candidate.resolve()
    return None


def manifest_conflict(path, out_dir, base_seed):
    """Message if writing this run into `path` would relabel rows it did not produce.

    Entries are keyed on the bare file name, not the directory the file went to and not the
    seed that made it. --manifest defaults to the COMMITTED MANIFEST.txt beside this script,
    so without this check a scratch run with a different --out or --seed overwrites the row
    describing the real corpus in sample_files, and rebuilds the header lines besides,
    leaving "base seed 7   out /tmp/scratch" above rows produced by another seed into
    another directory. The README's own recipe, shasum a corpus and grep its name in
    MANIFEST.txt, then disagrees for a perfectly intact file and sends the reader hunting a
    corruption bug that does not exist. Refusing is the same contract as an output file that
    already exists.
    """
    entries = read_manifest(path)
    if not entries:
        return None
    header = read_manifest_header(path)
    if header.state is HeaderState.MALFORMED:
        return (f"{path} holds {len(entries)} entr(y/ies) under a '# base seed' line "
                "this tool cannot read back, so there is no way to tell whether they "
                "describe this run. Repair the header, or pass --no-manifest or "
                "--manifest PATH.")
    if header.state is HeaderState.ABSENT:
        return None
    # Identity is the resolved DIRECTORY on both sides, never the spelling. "sample_files"
    # from the repository root, an absolute path, and "../../sample_files" from
    # scripts/gen-samples all name one corpus; "sample_files" typed in /tmp names another.
    # realpath alone cannot tell those apart, which is what resolve_recorded_out is for.
    recorded_dir = resolve_recorded_out(path, header.out)
    if recorded_dir is None:
        return (f"{path} records out {header.out}, a relative path, and no directory "
                f"of that name exists at or above {parent_dir(path)}, "
                f"so there is no telling which corpus its {len(entries)} entr(y/ies) "
                "describe. Pass --no-manifest, or --manifest PATH to keep a separate "
                "record.")
    out_resolved = Path(out_dir).resolve()
    if header.seed == base_seed and recorded_dir == out_resolved:
        return None
    # Resolved directories in the message, not the raw spellings: "out sample_files, but
    # this run is out sample_files" would be the least helpful refusal imaginable.
    return (f"{path} records base seed {header.seed} out {recorded_dir}, but this run is "
            f"base seed {base_seed} out {out_resolved}. "
            "Entries there are keyed on file name alone, so writing this run into it "
            f"would relabel {len(entries)} entr(y/ies) it did not produce. Pass "
            "--no-manifest, or --manifest PATH to keep a separate record.")


def manifest_unwritable(path):
    """Message if the manifest cannot be used, or None.

    main() calls this before generating, write_manifest again at the end. The default set is
    46 seconds of work, and a chmod 444 MANIFEST.txt, a --manifest pointing into a directory
    that does not exist, or a checkout on a read-only mount used to surface only after all
    of it, as a traceback and an undocumented exit 1 rather than the exit 8 README promises.
    Unreadable counts as unwritable: merging by name reads the entries, the identity check
    reads the header. The DIRECTORY has to be writable even when the file is, because the
    manifest publishes through a sibling temp and os.replace like finalize(); a read-only
    manifest is refused anyway, since chmod 444 means "do not overwrite this". os.access is
    advisory, so write_manifest also catches OSError. Early warning, not enforcement.
    """
    directory = parent_dir(path)
    if not directory.is_dir():
        return f"cannot write {path}: {directory} is not a directory"
    if not os.access(directory, os.W_OK | os.X_OK):
        return f"cannot write {path}: {directory} is not writable"
    manifest = Path(path)
    if manifest.exists():
        if not manifest.is_file():
            return f"cannot write {path}: not a regular file"
        if not os.access(manifest, os.R_OK):
            return f"cannot write {path}: not readable"
        if not os.access(manifest, os.W_OK):
            return f"cannot write {path}: not writable"
    return None


def record_out(manifest_path, out_dir):
    """The `out` value to write into the header, relative to the MANIFEST'S directory.

    Absolute looks safer and is worse. MANIFEST.txt is COMMITTED, so an absolute path bakes
    one machine's home directory into git and gets a teammate's clone refused by
    manifest_conflict on their very first run. Verified: a clone under /tmp exited 2 against
    a header reading /Users/<someone>/repos/... Relative to the manifest survives a clone or
    a move and is the anchor resolve_recorded_out already uses; it is NOT cwd-relative, the
    thing that made the original guard answer differently depending on where it ran. realpath
    both sides so /var versus /private/var on macOS cannot make one directory look like two.
    Absolute is the fallback when no relative path exists at all, a different Windows drive,
    which resolve_recorded_out still handles.
    """
    target = Path(out_dir).resolve()
    base = parent_dir(manifest_path).resolve()
    try:
        return os.path.relpath(target, base)
    except ValueError:
        return str(target)


def check_manifest_ok(path, out_dir, base_seed):
    """Refuse the run if the manifest cannot be written or describes another corpus.

    Writability first: manifest_conflict reads the file, so on a manifest this run cannot
    open at all, the conflict check is what raises, as a traceback rather than the Exit.IO
    this is here to produce.
    """
    unwritable = manifest_unwritable(path)
    if unwritable:
        die(Exit.IO, unwritable)
    conflict = manifest_conflict(path, out_dir, base_seed)
    if conflict:
        die(Exit.USAGE, conflict)


def write_manifest(path, results, out_dir, base_seed):
    """Merge by name rather than truncate.

    A partial run (--sizes 100MB) must not erase the provenance of the three files still on
    disk from the previous full run. Merging is only sound when the file on disk describes
    the same corpus this run is producing, hence the guard; main() runs the identical checks
    up front so a mistake costs a second rather than a 46-second generate, but these are the
    ones that hold for a caller who skipped the pre-flight. Published through a staging file
    and os.replace like finalize(), with a stronger case: this file is the only record of
    four gitignored corpora totalling 1.15 GB, including the sha256s the README tells you to
    grep, and open(path, "w") truncates it before one new byte is known writable. A full
    volume turned a 492-byte manifest into a 0-byte one, traceback on top, after the
    generation itself had succeeded. A rename cannot lose the old record.
    """
    check_manifest_ok(path, out_dir, base_seed)
    entries = read_manifest(path)
    for row in results:
        entries[row.name] = row
    lines = [
        "# gen-samples manifest. Regenerate with: ./gen-samples.sh",
        "# Corpora are gitignored; this file is the record of what was produced.",
        # Resolved, never the raw --out string: read back from another working directory
        # that string names a different corpus or none, and this line's one job is to say
        # which corpus the rows below it describe. realpath rather than abspath, so a
        # symlinked path cannot compare unequal to itself on the next run.
        f"# base seed {base_seed}   out {record_out(path, out_dir)}",
        f"# {'name':<18} {'target':<12} {'bytes':<12} {'rows':<10} sha256",
    ]
    for name in sorted(entries):
        e = entries[name]
        lines.append(f"{e.name:<20} {e.target:<12d} {e.bytes:<12d} "
                     f"{e.rows:<10d} {e.sha256}")
    # The except sits outside the with on purpose: published_atomically's cleanup runs
    # first, so the staging file is gone by the time this turns the failure into Exit.IO.
    try:
        with published_atomically(path, "w") as fh:
            fh.write("\n".join(lines) + "\n")
    except OSError as exc:
        die(Exit.IO, f"cannot write {path}: {exc}")
    info(f"  manifest {path} ({len(entries)} entries)")


def verify(path, words):
    """Stream a generated file and check every claim it makes about itself.

    The tuple set is the memory-hungry part, roughly 250 MB on a 100 MB file. That is
    fine on the small file and the point of checking the small one: the Feistel is a
    bijection, so a clean result on any one file is evidence for all of them.
    """
    if not os.path.isfile(path):
        die(Exit.VERIFY, f"no such file: {path}")
    vocabulary = set(w.encode("ascii") for w in words)
    seen = set()
    problems = []
    started = time.monotonic()

    with open(path, "rb") as fh:
        header = fh.readline()
        try:
            declared = int(header.strip())
        except ValueError:
            die(Exit.VERIFY, f"line 1 is not a row count: {header[:40]!r}")

        rows = 0
        truncated = False
        for line in fh:
            rows += 1
            row = line.rstrip(b"\n")
            if not ROW_RE.match(row):
                problems.append(f"row {rows} malformed: {row[:60]!r}")
                if len(problems) > 5:
                    truncated = True
                    break
                continue
            idx_bytes, rest = row.split(b" ", 1)
            if int(idx_bytes) != rows:
                problems.append(f"row {rows} has index {idx_bytes.decode()}")
            tuple_bytes = rest[1:-1]
            for w in tuple_bytes.split(b" "):
                if w not in vocabulary:
                    problems.append(f"row {rows} has off-list word {w!r}")
                    break
            if tuple_bytes in seen:
                problems.append(f"row {rows} repeats an earlier word tuple")
            else:
                seen.add(tuple_bytes)

    # Only meaningful if the scan reached EOF. Bailing out after six problems leaves `rows`
    # wherever it got to, and comparing THAT against the header invents a "file holds 6
    # rows" line on top of the real failures and sends the reader after the wrong bug.
    if truncated:
        # insert, not append: only the first six are printed, and "there is more"
        # is the one line that must survive that cap.
        problems.insert(0, "stopped after the first few problems; there may be more, and the row count was not checked")
    elif declared != rows:
        problems.append(f"header says {declared} rows, file holds {rows}")

    elapsed = time.monotonic() - started
    if problems:
        for p in problems[:6]:
            sys.stderr.write("  FAIL  " + p + "\n")
        die(Exit.VERIFY, f"{path} failed verification")
    info(f"  OK  {path}: {rows} rows, {len(seen)} distinct tuples, "
         f"header agrees  ({elapsed:.1f}s)")
    return rows


def bench(rows, words):
    """Throughput only. Writes nothing, so the number is generation, not disk."""
    class Sink(object):
        def write(self, chunk):
            return len(chunk)

    info(f"interpreter  {sys.executable}")
    info(f"version      {sys.version.split()[0]}")
    started = time.monotonic()
    # 1 << 62 disables the byte budget, so the number reported is the number asked for.
    produced, body = generate_body(Sink(), 1 << 62, 0, words, max_rows=rows)
    elapsed = time.monotonic() - started
    rate = produced / elapsed
    avg = body / float(produced)
    info(f"rows         {produced} in {elapsed:.2f}s  ({rate / 1000:.0f}k rows/s)")
    info(f"row width    {avg:.1f} bytes average")
    total = sum(parse_size(text) for text in DEFAULT_SIZES.split(","))
    # Measured average rather than a hardcoded constant: the word list decides it.
    projected = (total / avg) / rate
    info(f"projected    {projected:.0f}s for the default set ({DEFAULT_SIZES})")


def selftest(words, words_path):
    """Prove the tool works with nothing but a temp directory.

    Exists because "standalone" is a claim, and a claim should be checkable in one
    command on a machine that has none of this repository.
    """
    tmp_dir = tempfile.mkdtemp(prefix="gen-samples-selftest-")
    try:
        info(f"selftest in {tmp_dir}")
        # First, because everything after it only proves this build agrees with itself: the
        # determinism comparisons run the same code twice, and verify() gets no seed.
        info(f"  reference agrees with the generator for {check_reference(words)} rows")
        results = [generate_one(tmp_dir, "2MB", DEFAULT_SEED, words, False)]
        path = os.path.join(tmp_dir, results[0].name)
        rows = verify(path, words)

        again = generate_one(tmp_dir, "2MB", DEFAULT_SEED, words, True)
        if again.sha256 != results[0].sha256:
            die(Exit.SELFTEST, "not deterministic: two runs produced different hashes")
        if again.rows != rows:
            die(Exit.SELFTEST, "not deterministic: row counts differ")

        manifest = os.path.join(tmp_dir, "MANIFEST.txt")
        write_manifest(manifest, results, tmp_dir, DEFAULT_SEED)
        if len(read_manifest(manifest)) != 1:
            die(Exit.SELFTEST, "manifest round-trip failed")

        info(f"selftest PASSED  (words {words_path}, {len(words)} entries)")
    finally:
        shutil.rmtree(tmp_dir, ignore_errors=True)


def check_interpreter():
    if sys.version_info < (3, 11):
        die(Exit.NO_INTERPRETER,
            f"needs python 3.11 or newer, this is {sys.version.split()[0]}")
    if sys.version_info[:2] >= (3, 14):
        warn(f"python {sys.version.split()[0]} is above the tuned range "
             "[3.11, 3.14); expect roughly 30% slower generation. Install uv to have "
             "a tuned CPython fetched for you.")


def build_parser():
    p = argparse.ArgumentParser(
        prog="gen-samples.sh",
        description="Generate large sample files of indexed random-word rows.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=textwrap.fill(
            "exit codes: " + ", ".join(f"{e.value} {e.label}" for e in Exit),
            width=72, subsequent_indent=" " * 12))
    p.add_argument("--out", default="sample_files",
                   help="output directory, relative to the working directory "
                        "(default: %(default)s)")
    p.add_argument("--sizes", default=DEFAULT_SIZES,
                   help="comma-separated targets, MB decimal or MiB binary "
                        "(default: %(default)s)")
    p.add_argument("--seed", type=int, default=DEFAULT_SEED,
                   help="base seed; per-file seeds derive from it and the target size")
    p.add_argument("--words", default=SCRIPT_DIR / "words-1024.txt",
                   help="word list (default: words-1024.txt beside this script)")
    p.add_argument("--manifest", default=SCRIPT_DIR / "MANIFEST.txt",
                   help="manifest path (default: MANIFEST.txt beside this script)")
    p.add_argument("--no-manifest", action="store_true", help="skip the manifest")
    p.add_argument("--force", action="store_true",
                   help="overwrite existing output files")
    p.add_argument("--verify", metavar="PATH", help="verify an existing file and exit")
    p.add_argument("--selftest", action="store_true",
                   help="generate and verify ~2MB in a temp directory, then exit")
    p.add_argument("--bench", nargs="?", type=int, const=BENCH_ROWS, metavar="ROWS",
                   help="measure throughput, write nothing, and exit "
                        f"(default: {BENCH_ROWS} rows)")
    return p


def main(argv):
    check_interpreter()
    args = build_parser().parse_args(argv)
    words = load_words(args.words)

    if args.bench is not None:
        bench(args.bench, words)
        return 0
    if args.selftest:
        selftest(words, args.words)
        return 0
    # `is not None`, not truthiness, exactly as --bench above. --verify "" is a wrapper
    # whose "$CORPUS" came out empty, and reading that as "no --verify given" does not
    # skip a check, it falls through to the generate path below with every default in
    # place: 1.15 GB into ./sample_files, the committed manifest rewritten, exit 0 for a
    # verification that never ran.
    if args.verify is not None:
        verify(args.verify, words)
        return 0

    out_dir = args.out
    # Both of these are caller mistakes rather than IO failures, and both used to reach
    # os.makedirs and come out as a traceback with the undocumented exit 1.
    if not out_dir:
        die(Exit.USAGE, "--out is empty")
    if os.path.exists(out_dir) and not os.path.isdir(out_dir):
        die(Exit.USAGE, f"--out {out_dir} exists and is not a directory")
    if not os.path.isdir(out_dir):
        try:
            os.makedirs(out_dir)
        except OSError as exc:
            # The first write a run makes, outside generate_one's OSError wrapper. README
            # advertises --out as "created if missing" and documents exit 8 for a full or
            # read-only volume; a traceback out of main() reads as a tool bug.
            die(Exit.IO, f"cannot create {out_dir}: {exc}")

    sizes = [s for s in (t.strip() for t in args.sizes.split(",")) if s]
    if not sizes:
        die(Exit.USAGE, "--sizes is empty")

    if not args.no_manifest:
        # Up front, not at the end: write_manifest enforces both too, but finding out after
        # 46 seconds of generation that the record cannot be written is a waste.
        check_manifest_ok(args.manifest, out_dir, args.seed)

    info(f"generating {len(sizes)} file(s) into {os.path.abspath(out_dir)}")
    started = time.monotonic()
    results = [generate_one(out_dir, s, args.seed, words, args.force) for s in sizes]
    if not args.no_manifest:
        write_manifest(args.manifest, results, out_dir, args.seed)
    info(f"done in {time.monotonic() - started:.1f}s")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
