#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.11,<3.14"
# dependencies = []
# ///
"""Generate large sample files of indexed random-word rows.

    N
    0000000001 [amber pebble willow amber lantern brook copper]
    0000000002 [drizzle anchor bamboo ridge anchor meadow flint]
    ...

Line 1 is the row count. Every row is a 10-digit zero-padded index, a space, then
seven words from words-1024.txt inside brackets. Words may repeat WITHIN a row; no
two rows are ever identical, and no two rows share the same seven-word tuple. See
_tuple_bits below for why that is a guarantee rather than a probability.

THREE THINGS THAT LOOK LIKE MISTAKES AND ARE NOT
------------------------------------------------
1. `dependencies = []` above is not an oversight. This is stdlib-only. What uv buys
   here is INTERPRETER SELECTION, not packages: on a box that only has 3.14 it fetches
   a CPython inside the pinned range instead, worth about 25% of total runtime. uv
   takes the NEWEST version satisfying the pin, so in practice that is 3.13, not 3.11.
   No uv.lock until there is a real dependency to lock.

2. `<3.14` is a PERFORMANCE bound, not a compatibility one. Measured on an Apple
   silicon Mac, 1M rows: 3.11 2.39s, 3.13 2.51s, 3.12 2.60s, 3.14 3.15s. Both a
   uv-managed 3.14 and a Homebrew 3.14.7 agree, so it is the version regressing and
   not one bad build. Do not "fix" this by widening the range.

3. The syntax here stays 3.9-compatible even though the pin says 3.11. gen-samples.sh
   falls back to whatever `python3` is on PATH when uv is absent, and on macOS that is
   /usr/bin/python3 3.9.6. No match statements, no `int | None`, no tomllib.

Run --bench to re-derive the numbers above on your own machine.
"""

import argparse
import hashlib
import os
import re
import shutil
import sys
import tempfile
import time

# --- format ----------------------------------------------------------------
# Row bytes: 10 index digits + ' ' + '[' + 7 words + 6 inner spaces + ']' + '\n'.
# Everything except the words themselves is fixed, hence ROW_OVERHEAD.
IDX_WIDTH = 10          # holds 9,999,999,999 rows, well past the billion asked for
WORDS_PER_ROW = 7
ROW_OVERHEAD = IDX_WIDTH + 1 + 1 + (WORDS_PER_ROW - 1) + 1 + 1   # == 20
ROW_FMT = b"%010d [%s %s %s %s %s %s %s]\n"
ROW_RE = re.compile(rb"^[0-9]{10} \[[a-z]+(?: [a-z]+){6}\]$")

# --- the bijection ---------------------------------------------------------
# WORD_COUNT must be a power of two and BITS_PER_WORD its log2. 1024**7 == 2**70,
# so a bijection on 70 bits IS a bijection on the space of seven-word tuples. A
# non-power-of-two list would need rejection sampling and would break the guarantee
# silently, which is why the loader treats a wrong-length word list as fatal.
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
# DEFAULT_SEED, KNOWN_TARGET)))). Indices rather than words on purpose, so a custom
# --words list does not make this fail; what it pins is the arithmetic, not the
# vocabulary. It nails down mix, round_keys, file_seed, tuple_bits and word_indices at
# once. If you change this constant you have changed every sha256 in MANIFEST.txt and
# the 1.15 GB on disk is now something else, so regenerate rather than re-record.
KNOWN_TARGET = 200000000
KNOWN_ROW1_INDICES = (159, 181, 495, 262, 518, 393, 413)
# Enough rows to cross the 10/100/1000 odometer boundaries inside generate_body, which
# is where an inlined hot loop is most likely to disagree with the reference. Costs
# about 5 ms, so --selftest pays nothing for it.
REFERENCE_ROWS = 2000

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))

# Exit codes. Kept in sync with README.md and with gen-samples.sh.
EXIT_USAGE = 2
EXIT_NO_INTERPRETER = 3
EXIT_BAD_WORDS = 4
EXIT_EXISTS = 5
EXIT_VERIFY = 6
EXIT_SELFTEST = 7
EXIT_IO = 8


def info(msg):
    sys.stdout.write(msg + "\n")
    sys.stdout.flush()


def warn(msg):
    sys.stderr.write("WARNING: " + msg + "\n")


def die(code, msg):
    sys.stderr.write("gen-samples: " + msg + "\n")
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
        die(EXIT_BAD_WORDS, "word list not found: %s" % path)
    with open(path, "r") as fh:
        words = [line.strip() for line in fh]
    words = [w for w in words if w]
    if len(words) != WORD_COUNT:
        die(EXIT_BAD_WORDS,
            "word list must hold exactly %d entries, found %d in %s"
            % (WORD_COUNT, len(words), path))
    if len(set(words)) != WORD_COUNT:
        die(EXIT_BAD_WORDS, "word list has duplicate entries: %s" % path)
    bad = [w for w in words if not re.match(r"^[a-z]{3,9}$", w)]
    if bad:
        die(EXIT_BAD_WORDS,
            "word list entries must match ^[a-z]{3,9}$, offenders: %s"
            % ", ".join(bad[:5]))
    return words


def parse_size(text):
    """'100MB' -> 100000000, '100MiB' -> 104857600, '512' -> 512.

    MB is decimal because that is what Finder and `ls -lh` report on macOS. The MiB
    spelling is there for anyone who wants the binary reading and says so.
    """
    m = re.match(r"^\s*(\d+)\s*([KMGkmg]i?[Bb]?|)\s*$", text)
    if not m:
        die(EXIT_USAGE, "cannot parse size %r (try 100MB, 100MiB, or a byte count)" % text)
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
    return "sample-%s.txt" % size_label(text)


def file_seed(base, target):
    """Per-file seed derived from the TARGET SIZE, not from position in --sizes.

    Deliberate: `--sizes 200MB` on its own must reproduce byte-for-byte what the
    default four-file run produced for 200MB. Keying on list position would make the
    same file come out differently depending on what it was generated alongside,
    which would quietly invalidate MANIFEST.txt.
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
        die(EXIT_SELFTEST,
            "the bijection no longer produces the stream MANIFEST.txt records: row 1 "
            "of the %d target is %s, expected %s" % (KNOWN_TARGET, known, KNOWN_ROW1_INDICES))

    class Collector(object):
        def __init__(self):
            self.chunks = []

        def write(self, chunk):
            self.chunks.append(chunk)
            return len(chunk)

    sink = Collector()
    # 1 << 62 disables the byte budget so max_rows is what ends the loop, same as bench().
    produced, _body = generate_body(sink, 1 << 62, seed, words, max_rows=REFERENCE_ROWS)
    lines = b"".join(sink.chunks).splitlines(True)
    if produced != REFERENCE_ROWS or len(lines) != REFERENCE_ROWS:
        die(EXIT_SELFTEST, "asked generate_body for %d rows, got %d"
            % (REFERENCE_ROWS, len(lines)))

    encoded = [w.encode("ascii") for w in words]
    for i in range(1, REFERENCE_ROWS + 1):
        idx = word_indices(tuple_bits(i, keys))
        expected = ROW_FMT % ((i,) + tuple(encoded[j] for j in idx))
        if lines[i - 1] != expected:
            die(EXIT_SELFTEST,
                "generate_body disagrees with the reference bijection at row %d\n"
                "  generator %r\n  reference %r"
                % (i, lines[i - 1].rstrip(b"\n"), expected.rstrip(b"\n")))
    return REFERENCE_ROWS


def finalize(tmp_path, final_path, rows):
    """Prepend the header and hash the result in the same pass.

    The row count is only known once the body exists, and the header is variable
    width, so there is no way to reserve space for it up front. One copy is still
    cheaper than a second generation pass, and since the copy has to read every byte
    anyway the sha256 for MANIFEST.txt is free.

    The copy lands in a second temp and is published with os.replace, never streamed
    into final_path. That copy takes about a third of a second on the 500 MB file and
    fails deterministically on a full disk, and a partial write under the FINAL name is
    the worst possible wreck: valid header, thousands of valid rows, nothing on disk
    saying the run did not finish, and the next run refusing to overwrite it with "pass
    --force". With --force it is worse still, because open(final_path, "wb") truncates
    a good corpus before one new byte is known to be writable. os.replace is atomic
    within a filesystem, and the staging file sits in final_path's own directory, so it
    is the same filesystem by construction.
    """
    digest = hashlib.sha256()
    header = ("%d\n" % rows).encode("ascii")
    total = len(header)
    staged = os.path.join(os.path.dirname(final_path) or ".",
                          "." + os.path.basename(final_path) + ".part")
    published = False
    try:
        # Plain open, not mkstemp: mkstemp would hand the corpus 0600 instead of the
        # umask-respecting mode every other file here gets.
        with open(staged, "wb") as out:
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
        os.replace(staged, final_path)
        published = True
    finally:
        if not published and os.path.exists(staged):
            os.remove(staged)
    return total, digest.hexdigest()


def generate_one(out_dir, size_text, base_seed, words, force):
    target = parse_size(size_text)
    name = output_name(size_text)
    final_path = os.path.join(out_dir, name)
    tmp_path = os.path.join(out_dir, "." + name + ".tmp")

    if os.path.exists(final_path) and not force:
        die(EXIT_EXISTS,
            "%s already exists. Pass --force to overwrite it." % final_path)

    seed = file_seed(base_seed, target)
    started = time.monotonic()
    try:
        try:
            with open(tmp_path, "wb", buffering=0) as tmp:
                rows, _body = generate_body(tmp, target, seed, words)
            if rows == 0:
                die(EXIT_USAGE,
                    "target %s is too small to hold even one row" % size_text)
            size, digest = finalize(tmp_path, final_path, rows)
        except OSError as exc:
            # A full or read-only volume is the ordinary way to get here, and without
            # this the user gets a raw traceback out of the middle of finalize(), which
            # reads as a tool bug rather than as "your disk is full". SystemExit from
            # die() is not an OSError, so the rows==0 path above still passes through.
            # finalize() stages and os.replace()s, so nothing partial survives under
            # the final name and there is no corpse for the next run to trip over.
            die(EXIT_IO, "cannot write %s: %s" % (final_path, exc))
    finally:
        if os.path.exists(tmp_path):
            os.remove(tmp_path)
    elapsed = time.monotonic() - started

    info("  %-20s %10d rows  %12d B  (target %d, %d under)  %5.1fs"
         % (name, rows, size, target, target - size, elapsed))
    return {"name": name, "target": target, "bytes": size, "rows": rows,
            "seed": seed, "sha256": digest}


def read_manifest(path):
    entries = {}
    if not os.path.isfile(path):
        return entries
    with open(path, "r") as fh:
        for line in fh:
            line = line.strip()
            if not line or line.startswith("#"):
                continue
            parts = line.split()
            if len(parts) != 5:
                continue
            entries[parts[0]] = {"name": parts[0], "target": int(parts[1]),
                                 "bytes": int(parts[2]), "rows": int(parts[3]),
                                 "sha256": parts[4]}
    return entries


# Second element read_manifest_header returns when the file HAS a "# base seed" line
# that will not parse. Deliberately distinct from "no header line at all", which is the
# documented opt-out for a hand-written manifest.
MALFORMED_HEADER = object()


def read_manifest_header(path):
    r"""Recover the (base seed, out dir) a manifest was written for.

    Returns (None, None) for a missing file or one with no such comment, which is how a
    hand-written or pre-header manifest opts out of the identity check below rather
    than tripping it. A line that starts like a header and then will not parse returns
    (None, MALFORMED_HEADER) instead, and manifest_conflict refuses on that: this tool
    only ever writes headers this pattern can read back, so a mangled one is a file we
    cannot reason about, not somebody opting out.

    The seed pattern is -?\d+ rather than \d+ because --seed is a plain int and argparse
    accepts `--seed=-5` quite happily, write_manifest then formats it with %d, and a
    header this tool wrote one command ago but cannot read back does not merely lose the
    seed: read_manifest_header returns (None, None), manifest_conflict reads that as
    consent, and the guard is off for that manifest permanently, for every later seed and
    every later --out.
    """
    if not os.path.isfile(path):
        return None, None
    malformed = False
    with open(path, "r") as fh:
        for line in fh:
            m = re.match(r"^#\s*base seed\s+(-?\d+)\s+out\s+(\S.*?)\s*$", line)
            if m:
                return int(m.group(1)), m.group(2)
            if re.match(r"^#\s*base seed\b", line):
                malformed = True
    if malformed:
        return None, MALFORMED_HEADER
    return None, None


def resolve_recorded_out(manifest_path, recorded):
    """Absolute directory for a manifest header's `out` value, or None if undecidable.

    write_manifest records a fully resolved path, so the ordinary case is one realpath
    and it is exact from any working directory.

    Manifests written before that recorded the raw --out string, and the committed
    MANIFEST.txt is one of them: it holds the bare word "sample_files". A relative string
    does not name a directory by itself, and nothing in the file says which directory it
    was typed in, so resolving it against the CURRENT cwd -- which is what this used to
    do -- answers a different question every time the caller cds. That is how
    `cd /tmp && gen-samples.sh` came to merge a run that wrote /tmp/sample_files into the
    manifest describing the repository's, while `--out <repo>/sample_files`, the only
    unambiguous way to name the real corpus from elsewhere, was refused.

    The manifest's own location is the only anchor left, so a relative value is tried
    against the manifest's directory and then each parent, deepest first, taking the
    first candidate that is a directory. scripts/gen-samples/MANIFEST.txt therefore
    resolves "sample_files" to <repo>/sample_files, the corpus it actually describes.
    Deepest first rather than collecting every match and demanding exactly one, so the
    answer stays deterministic when two ancestors both hold a sample_files.

    None means undecidable and manifest_conflict refuses rather than guessing. Reaching
    it takes a pre-fix manifest whose directory is gone or never was beneath it -- this
    directory copied elsewhere with its MANIFEST.txt in tow, say -- and such a manifest
    describes files this run did not produce and cannot even find, which is exactly the
    case the guard exists for. One successful run rewrites the header as an absolute
    path and none of this applies again.
    """
    if os.path.isabs(recorded):
        return os.path.realpath(recorded)
    base = os.path.dirname(os.path.abspath(manifest_path))
    while True:
        candidate = os.path.join(base, recorded)
        if os.path.isdir(candidate):
            return os.path.realpath(candidate)
        parent = os.path.dirname(base)
        if parent == base:
            return None
        base = parent


def manifest_conflict(path, out_dir, base_seed):
    """Message if writing this run into `path` would relabel rows it did not produce.

    Entries are keyed on the bare file name, which encodes the size label and nothing
    else: not the directory the file went to, not the seed that made it. --manifest
    defaults to the COMMITTED MANIFEST.txt beside this script, so without this check a
    scratch run with a different --out or --seed overwrites the row describing the real
    corpus in sample_files. And it is not just the one row: the header lines are rebuilt
    from the current run, so "base seed 7   out /tmp/scratch" ends up sitting above rows
    produced by a different seed into a different directory and every line in the file
    is then false or misleading. The README's own recipe -- shasum a corpus, grep its
    name in MANIFEST.txt -- then disagrees for a perfectly intact file and sends the
    reader hunting a corruption bug that does not exist.

    Refusing is the same contract as an existing output file: this tool does not
    silently overwrite the record of something it did not produce.
    """
    entries = read_manifest(path)
    if not entries:
        return None
    seed, recorded_out = read_manifest_header(path)
    if recorded_out is MALFORMED_HEADER:
        return ("%s holds %d entr(y/ies) under a '# base seed' line this tool cannot "
                "read back, so there is no way to tell whether they describe this run. "
                "Repair the header, or pass --no-manifest or --manifest PATH."
                % (path, len(entries)))
    if seed is None:
        return None
    # Identity is the resolved DIRECTORY on both sides, never the spelling. "sample_files"
    # from the repository root, an absolute path, and "../../sample_files" from
    # scripts/gen-samples all name one corpus and all have to be accepted; "sample_files"
    # typed in /tmp names a different one and has to be refused. realpath alone cannot
    # tell those apart, because it resolves a relative recorded value against whatever
    # cwd this run happens to have rather than the one the manifest was written from --
    # see resolve_recorded_out, which is what turns the recorded side into a directory.
    recorded_dir = resolve_recorded_out(path, recorded_out)
    if recorded_dir is None:
        return ("%s records out %s, a relative path, and no directory of that name "
                "exists at or above %s, so there is no telling which corpus its %d "
                "entr(y/ies) describe. Pass --no-manifest, or --manifest PATH to keep a "
                "separate record."
                % (path, recorded_out, os.path.dirname(os.path.abspath(path)),
                   len(entries)))
    if seed == base_seed and recorded_dir == os.path.realpath(out_dir):
        return None
    # Resolved directories in the message, not the raw spellings: both sides can read
    # "sample_files" and mean different directories, and "out sample_files, but this run
    # is out sample_files" would be the least helpful refusal imaginable.
    return ("%s records base seed %d out %s, but this run is base seed %d out %s. "
            "Entries there are keyed on file name alone, so writing this run into it "
            "would relabel %d entr(y/ies) it did not produce. Pass --no-manifest, or "
            "--manifest PATH to keep a separate record."
            % (path, seed, recorded_dir, base_seed, os.path.realpath(out_dir),
               len(entries)))


def manifest_unwritable(path):
    """Message if the manifest cannot be used, or None.

    main() calls this before generating and write_manifest calls it again at the end.
    The default set is 46 seconds of work, and a chmod 444 MANIFEST.txt, a --manifest
    pointing into a directory that does not exist, or a checkout on a read-only mount
    used to be discovered only after all of it, as a traceback and an undocumented exit
    1 rather than the exit 8 README promises for "cannot write".

    "Unwritable" also covers unreadable, because merging by name means reading the
    current entries and checking identity means reading the header: a chmod 000
    MANIFEST.txt cannot be merged into either, and open(path, "r") raising out of
    read_manifest is the same raw traceback under a different errno.

    os.access is advisory -- it can say yes and the write can still fail on an ACL, on a
    full volume, or because the file changed underneath us -- which is why write_manifest
    also catches OSError. This is the early warning, not the enforcement.

    The DIRECTORY has to be writable even when the file itself is, because the manifest
    is published the way finalize() publishes a corpus: a sibling temp, then os.replace.
    And an existing read-only manifest is refused even though os.replace could happily
    rename over it, since the whole meaning of chmod 444 is "do not overwrite this" and
    the publish mechanism being able to ignore that is not a reason to.
    """
    directory = os.path.dirname(os.path.abspath(path))
    if not os.path.isdir(directory):
        return "cannot write %s: %s is not a directory" % (path, directory)
    if not os.access(directory, os.W_OK | os.X_OK):
        return "cannot write %s: %s is not writable" % (path, directory)
    if os.path.exists(path):
        if not os.path.isfile(path):
            return "cannot write %s: not a regular file" % path
        if not os.access(path, os.R_OK):
            return "cannot write %s: not readable" % path
        if not os.access(path, os.W_OK):
            return "cannot write %s: not writable" % path
    return None


def record_out(manifest_path, out_dir):
    """The `out` value to write into the header, relative to the MANIFEST'S directory.

    Absolute looks safer here and is worse. MANIFEST.txt is COMMITTED, so an absolute
    path bakes one machine's home directory into the repository: a teammate's clone at
    any other path is refused by manifest_conflict on their very first run, with a
    message naming a directory that does not exist on their disk, and the committed file
    leaks a local path into git besides. Verified: a clone under /tmp exited 2 against a
    header reading /Users/<someone>/repos/...

    Relative-to-the-manifest survives a clone, survives the whole directory being moved,
    and is the exact anchor resolve_recorded_out already uses, so the round trip is
    lossless. It is NOT cwd-relative, which is the thing that made the original guard
    answer a different question depending on where it was invoked from.

    realpath both sides before diffing them so symlinks, and /var versus /private/var on
    macOS, cannot make two spellings of one directory look like two directories. Falls
    back to the absolute path when no relative path exists at all (a different Windows
    drive), which resolve_recorded_out still handles.
    """
    target = os.path.realpath(out_dir)
    base = os.path.realpath(os.path.dirname(os.path.abspath(manifest_path)))
    try:
        return os.path.relpath(target, base)
    except ValueError:
        return target


def write_manifest(path, results, out_dir, base_seed):
    """Merge by name rather than truncate.

    A partial run (--sizes 100MB) must not erase the provenance of the three files
    still sitting on disk from the previous full run.

    Merging is only sound when the file on disk describes the same corpus this run is
    producing, hence the guard. main() runs the identical checks up front so a mistake
    costs a second rather than a 46-second generate; these are the ones that actually
    hold, for any caller that skipped the pre-flight.

    Published through a staging file and os.replace for the same reason finalize() is,
    and the argument is if anything stronger here. This file is the only record of four
    gitignored corpora totalling 1.15 GB, including the sha256s the README tells you to
    grep, and open(path, "w") truncates it before one new byte is known to be writable:
    a full volume turned a 492-byte manifest into a 0-byte one and put a traceback on
    top, after the generation itself had succeeded. A rename cannot lose the old record.
    """
    # Writability first, and not for tidiness: manifest_conflict reads the file, so on a
    # manifest this run cannot open at all the conflict check is what would raise, and it
    # would raise as a traceback rather than as the EXIT_IO this is here to produce.
    unwritable = manifest_unwritable(path)
    if unwritable:
        die(EXIT_IO, unwritable)
    conflict = manifest_conflict(path, out_dir, base_seed)
    if conflict:
        die(EXIT_USAGE, conflict)
    entries = read_manifest(path)
    for r in results:
        entries[r["name"]] = r
    lines = [
        "# gen-samples manifest. Regenerate with: ./gen-samples.sh",
        "# Corpora are gitignored; this file is the record of what was produced.",
        # Resolved, never the raw --out string. The string is only what this caller
        # happened to type; read back from another working directory it names a
        # different directory or none at all, and the one job of this line is to say
        # which corpus the rows below it describe. realpath rather than abspath so a
        # symlinked path cannot compare unequal to itself on the next run.
        "# base seed %d   out %s" % (base_seed, record_out(path, out_dir)),
        "# %-18s %-12s %-12s %-10s %s" % ("name", "target", "bytes", "rows", "sha256"),
    ]
    for name in sorted(entries):
        e = entries[name]
        lines.append("%-20s %-12d %-12d %-10d %s"
                     % (e["name"], e["target"], e["bytes"], e["rows"], e["sha256"]))
    staged = os.path.join(os.path.dirname(os.path.abspath(path)),
                          "." + os.path.basename(path) + ".part")
    published = False
    try:
        # Plain open rather than mkstemp, same as finalize(): mkstemp would hand the
        # manifest 0600 instead of the umask-respecting mode it has today.
        with open(staged, "w") as fh:
            fh.write("\n".join(lines) + "\n")
        os.replace(staged, path)
        published = True
    except OSError as exc:
        die(EXIT_IO, "cannot write %s: %s" % (path, exc))
    finally:
        if not published and os.path.exists(staged):
            os.remove(staged)
    info("  manifest %s (%d entries)" % (path, len(entries)))


def verify(path, words):
    """Stream a generated file and check every claim it makes about itself.

    The tuple set is the memory-hungry part, roughly 250 MB on a 100 MB file. That is
    fine on the small file and the point of checking the small one: the Feistel is a
    bijection, so a clean result on any one file is evidence for all of them.
    """
    if not os.path.isfile(path):
        die(EXIT_VERIFY, "no such file: %s" % path)
    vocabulary = set(w.encode("ascii") for w in words)
    seen = set()
    problems = []
    started = time.monotonic()

    with open(path, "rb") as fh:
        header = fh.readline()
        try:
            declared = int(header.strip())
        except ValueError:
            die(EXIT_VERIFY, "line 1 is not a row count: %r" % header[:40])

        rows = 0
        truncated = False
        for line in fh:
            rows += 1
            row = line.rstrip(b"\n")
            if not ROW_RE.match(row):
                problems.append("row %d malformed: %r" % (rows, row[:60]))
                if len(problems) > 5:
                    truncated = True
                    break
                continue
            idx_bytes, rest = row.split(b" ", 1)
            if int(idx_bytes) != rows:
                problems.append("row %d has index %s" % (rows, idx_bytes.decode()))
            tuple_bytes = rest[1:-1]
            for w in tuple_bytes.split(b" "):
                if w not in vocabulary:
                    problems.append("row %d has off-list word %r" % (rows, w))
                    break
            if tuple_bytes in seen:
                problems.append("row %d repeats an earlier word tuple" % rows)
            else:
                seen.add(tuple_bytes)

    # Only meaningful if the scan reached EOF. Bailing out after six problems leaves
    # `rows` at however far it got, and comparing THAT against the header invents a
    # "file holds 6 rows" line on top of the real failures, which sends whoever is
    # reading the diagnostics after the wrong bug.
    if truncated:
        # insert, not append: only the first six are printed, and "there is more"
        # is the one line that must survive that cap.
        problems.insert(0, "stopped after the first few problems; there may be more, and the row count was not checked")
    elif declared != rows:
        problems.append("header says %d rows, file holds %d" % (declared, rows))

    elapsed = time.monotonic() - started
    if problems:
        for p in problems[:6]:
            sys.stderr.write("  FAIL  " + p + "\n")
        die(EXIT_VERIFY, "%s failed verification" % path)
    info("  OK  %s: %d rows, %d distinct tuples, header agrees  (%.1fs)"
         % (path, rows, len(seen), elapsed))
    return rows


def bench(rows, words):
    """Throughput only. Writes nothing, so the number is generation, not disk."""
    class Sink(object):
        def write(self, chunk):
            return len(chunk)

    info("interpreter  %s" % sys.executable)
    info("version      %s" % sys.version.split()[0])
    started = time.monotonic()
    # Byte budget effectively disabled; max_rows is what ends the loop, so the number
    # reported is the number asked for.
    produced, body = generate_body(Sink(), 1 << 62, 0, words, max_rows=rows)
    elapsed = time.monotonic() - started
    rate = produced / elapsed
    avg = body / float(produced)
    info("rows         %d in %.2fs  (%.0fk rows/s)" % (produced, elapsed, rate / 1000))
    info("row width    %.1f bytes average" % avg)
    total = 0
    for text in DEFAULT_SIZES.split(","):
        total += parse_size(text)
    # Measured average rather than a hardcoded constant: the word list decides it.
    projected = (total / avg) / rate
    info("projected    %.0fs for the default set (%s)" % (projected, DEFAULT_SIZES))


def selftest(words, words_path):
    """Prove the tool works with nothing but a temp directory.

    Exists because "standalone" is a claim, and a claim should be checkable in one
    command on a machine that has none of this repository.
    """
    tmp_dir = tempfile.mkdtemp(prefix="gen-samples-selftest-")
    try:
        info("selftest in %s" % tmp_dir)
        # First, because everything after it only proves this build agrees with itself.
        # The two determinism comparisons below run the same code twice, and verify()
        # is not given a seed so it cannot know which stream it should be looking at.
        info("  reference agrees with the generator for %d rows" % check_reference(words))
        results = [generate_one(tmp_dir, "2MB", DEFAULT_SEED, words, False)]
        path = os.path.join(tmp_dir, results[0]["name"])
        rows = verify(path, words)

        again = generate_one(tmp_dir, "2MB", DEFAULT_SEED, words, True)
        if again["sha256"] != results[0]["sha256"]:
            die(EXIT_SELFTEST, "not deterministic: two runs produced different hashes")
        if again["rows"] != rows:
            die(EXIT_SELFTEST, "not deterministic: row counts differ")

        manifest = os.path.join(tmp_dir, "MANIFEST.txt")
        write_manifest(manifest, results, tmp_dir, DEFAULT_SEED)
        if len(read_manifest(manifest)) != 1:
            die(EXIT_SELFTEST, "manifest round-trip failed")

        info("selftest PASSED  (words %s, %d entries)" % (words_path, len(words)))
    finally:
        shutil.rmtree(tmp_dir, ignore_errors=True)


def check_interpreter():
    if sys.version_info < (3, 9):
        die(EXIT_NO_INTERPRETER,
            "needs python 3.9 or newer, this is %s" % sys.version.split()[0])
    if not ((3, 11) <= sys.version_info[:2] < (3, 14)):
        warn("python %s is outside the tuned range [3.11, 3.14); expect roughly 30%% "
             "slower generation. Install uv to have a tuned CPython fetched for you."
             % sys.version.split()[0])


def build_parser():
    p = argparse.ArgumentParser(
        prog="gen-samples.sh",
        description="Generate large sample files of indexed random-word rows.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="exit codes: 0 ok, 2 usage, 3 no interpreter, 4 bad word list,\n"
               "            5 output exists without --force, 6 verify failed,\n"
               "            7 selftest failed, 8 cannot write output")
    p.add_argument("--out", default="sample_files",
                   help="output directory, relative to the working directory "
                        "(default: %(default)s)")
    p.add_argument("--sizes", default=DEFAULT_SIZES,
                   help="comma-separated targets, MB decimal or MiB binary "
                        "(default: %(default)s)")
    p.add_argument("--seed", type=int, default=DEFAULT_SEED,
                   help="base seed; per-file seeds derive from it and the target size")
    p.add_argument("--words", default=os.path.join(SCRIPT_DIR, "words-1024.txt"),
                   help="word list (default: words-1024.txt beside this script)")
    p.add_argument("--manifest", default=os.path.join(SCRIPT_DIR, "MANIFEST.txt"),
                   help="manifest path (default: MANIFEST.txt beside this script)")
    p.add_argument("--no-manifest", action="store_true", help="skip the manifest")
    p.add_argument("--force", action="store_true",
                   help="overwrite existing output files")
    p.add_argument("--verify", metavar="PATH", help="verify an existing file and exit")
    p.add_argument("--selftest", action="store_true",
                   help="generate and verify ~2MB in a temp directory, then exit")
    p.add_argument("--bench", nargs="?", type=int, const=BENCH_ROWS, metavar="ROWS",
                   help="measure throughput, write nothing, and exit "
                        "(default: %d rows)" % BENCH_ROWS)
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
        die(EXIT_USAGE, "--out is empty")
    if os.path.exists(out_dir) and not os.path.isdir(out_dir):
        die(EXIT_USAGE, "--out %s exists and is not a directory" % out_dir)
    if not os.path.isdir(out_dir):
        try:
            os.makedirs(out_dir)
        except OSError as exc:
            # The first write a run makes, and outside generate_one's OSError wrapper.
            # README advertises --out as "created if missing" and documents exit 8 for a
            # full or read-only volume, which is precisely what fails here, before any
            # generation has started. A traceback out of main() reads as a tool bug.
            die(EXIT_IO, "cannot create %s: %s" % (out_dir, exc))

    sizes = [s for s in (t.strip() for t in args.sizes.split(",")) if s]
    if not sizes:
        die(EXIT_USAGE, "--sizes is empty")

    if not args.no_manifest:
        # Up front, not at the end: write_manifest enforces both of these too, but
        # finding out after 46 seconds of generation that the record cannot be written is
        # a waste. Writability belongs here for the same reason the conflict does; a
        # read-only manifest used to surface only once the whole run had succeeded.
        unwritable = manifest_unwritable(args.manifest)
        if unwritable:
            die(EXIT_IO, unwritable)
        conflict = manifest_conflict(args.manifest, out_dir, args.seed)
        if conflict:
            die(EXIT_USAGE, conflict)

    info("generating %d file(s) into %s" % (len(sizes), os.path.abspath(out_dir)))
    started = time.monotonic()
    results = [generate_one(out_dir, s, args.seed, words, args.force) for s in sizes]
    if not args.no_manifest:
        write_manifest(args.manifest, results, out_dir, args.seed)
    info("done in %.1fs" % (time.monotonic() - started))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
