# gen-samples

Generates large, deterministic sample corpora of indexed random-word rows. Everything it
needs is in this directory: `gen-samples.sh`, `gen_samples.py`, `words-1024.txt`. No
project libraries, no third-party packages, nothing to install first. Copy the directory
somewhere else and it still works.

## What it makes

Plain ASCII text. Line 1 is the row count `N`, bare decimal, nothing else on the line.
The next `N` lines are the rows.

```
3449054
0000000001 [cloth corn march energy mill horse invite]
0000000002 [tonight tender napkin attic chord citrus egg]
```

Each row is a 10-digit zero-padded index, one space, then seven words from
`words-1024.txt` inside square brackets, single-spaced. Indices run 1..N in order.

Words may repeat within a row. Row 10 of the file above is
`[inspire delight ticket cafe ticket moment wood]`, and that is legal and expected. What
never happens is a repeat at the row level. No two rows are identical, and no two rows
share the same seven-word tuple. That is a structural guarantee rather than a statistical
one. See [why the tuples are unique](#why-the-tuples-are-unique).

A row is 20 fixed bytes (10 index digits, a space, two brackets, six inner spaces, a
newline) plus the sum of its seven word lengths. Against the shipped word list that
averages about 58 bytes, so a 200 MB target lands at 3,449,054 rows. Row length varies,
which is why the generator measures as it goes instead of dividing.

## Quick start

```bash
./gen-samples.sh
```

Writes the default four files into `./sample_files`:

| File | Target |
|---|---|
| `sample-100mb.txt` | 100 MB |
| `sample-200mb.txt` | 200 MB |
| `sample-350mb.txt` | 350 MB |
| `sample-500mb.txt` | 500 MB |

About 1.15 GB in total, and 46 seconds measured end to end here on the uv default. `MB` is decimal, because
that is what Finder and `ls -lh` report on macOS. Write `MiB` if you want the binary
reading.

The wrapper only picks an interpreter. To skip it and run the script directly:

```bash
uv run gen_samples.py                 # same thing, no wrapper
uv run gen_samples.py --sizes 1GB     # one bigger file
```

To check the tool works before pointing it at a gigabyte:

```bash
./gen-samples.sh --selftest
```

That generates and verifies ~2 MB in a temp directory, regenerates it, confirms the two
runs hash identically, round-trips a manifest, and cleans up after itself. It touches
nothing outside the temp directory.

## Requirements

Either `uv`, or a `python3` of 3.9 or newer. Zero third-party packages. This is stdlib
only, and `dependencies = []` in the PEP 723 header is deliberate.

What `uv` buys here is interpreter selection, not packages. The script pins
`requires-python = ">=3.11,<3.14"`, so on a machine whose only Python is 3.14, uv fetches a
tuned one and uses that. It takes the NEWEST version satisfying the pin, so in practice that
is 3.13, not the 3.11 floor. When uv is absent, `gen-samples.sh` falls back to whatever `python3` is
on `PATH`, which on macOS is `/usr/bin/python3` 3.9.6. The fallback works, and the code
stays 3.9-syntax-compatible for exactly that reason: no `match`, no `int | None`, no
`tomllib`. It just costs about 30% of runtime on either 3.9 or 3.14, and the script prints
a warning saying so. Roughly a minute instead of 46 seconds for the default set.

```bash
GEN_NO_UV=1 ./gen-samples.sh    # force the python3 fallback even if uv is installed
```

## Flags

All flags belong to `gen_samples.py`. `gen-samples.sh` passes them straight through.

| Flag | Default | What it does |
|---|---|---|
| `--out DIR` | `sample_files` | Output directory, relative to the working directory. Created if missing. |
| `--sizes LIST` | `100MB,200MB,350MB,500MB` | Comma-separated targets. `MB`/`GB` decimal, `MiB`/`GiB` binary, or a bare byte count. |
| `--seed N` | `1234567890123456789` | Base seed. Per-file seeds derive from it and the target size. |
| `--words PATH` | `words-1024.txt` beside the script | Word list. Must hold exactly 1024 unique entries matching `^[a-z]{3,9}$`. |
| `--manifest PATH` | `MANIFEST.txt` beside the script | Where to record what was produced. |
| `--no-manifest` | off | Generate, but write no manifest. |
| `--force` | off | Overwrite existing output files instead of refusing. |
| `--verify PATH` | none | Verify one existing file and exit. Generates nothing. |
| `--selftest` | off | Generate and verify ~2 MB in a temp directory, then exit. |
| `--bench [ROWS]` | 1000000 rows when given bare | Measure throughput, write no file, exit. |
| `-h`, `--help` | none | Usage and the exit-code list. |

Exit codes:

| Code | Meaning |
|---|---|
| 0 | Success |
| 2 | Usage error: unparseable size, empty `--sizes`, or a target too small to hold one row |
| 3 | No usable interpreter |
| 4 | Bad word list: missing, wrong length, duplicated entries, or an entry outside `^[a-z]{3,9}$` |
| 5 | An output file already exists and `--force` was not passed |
| 6 | `--verify` failed |
| 7 | `--selftest` failed |
| 8 | cannot write the output file, e.g. a full or read-only volume |

Every word-list problem is fatal rather than a warning. A quietly 1023-entry list raises
nothing on its own. It would just void the uniqueness guarantee in silence.

## Performance

Measured on Apple silicon, macOS 25.6, 1,000,000 rows. That covers the full four-round
Feistel plus word lookups plus buffered output, written to a sink so the number is
generation and not disk. The last column is the script's own projection for the default
1.15 GB set.

| Interpreter | Version | 1M rows | Rate | Projected, all four |
|---|---|---|---|---|
| uv-managed CPython | 3.11 | 2.39 s | 418k rows/s | ~44 s |
| uv-managed CPython | 3.13 | 2.51 s | 398k rows/s | ~46 s |
| uv-managed CPython | 3.12 | 2.60 s | 385k rows/s | ~48 s |
| system `/usr/bin/python3` | 3.9.6 | 3.13 s | 319k rows/s | ~58 s |
| uv-managed CPython | 3.14 | 3.15 s | 317k rows/s | ~58 s |
| Homebrew `python3` | 3.14.7 | 3.21 s | 312k rows/s | ~59 s |
| `/usr/bin/perl` | 5.34.1 | 3.50 s | 286k rows/s | ~65 s |
| `bash` | 5.3 | ~390 s | 2.6k rows/s | ~2 h 40 m |

Those rows come from a standalone benchmark harness. The shipped generator inlines the
mixer and binds its constants to locals, so it runs faster than the table implies: 432k
rows/s on the uv default 3.13, and **46.4 s wall clock** for the real 1.15 GB set,
header-prepend copies and sha256 hashing included. Treat the table as a comparison
between interpreters rather than as a prediction of your run time, and use `--bench` for
the latter.

Three things worth knowing about that table.

**3.14 is the slowest CPython here.** It is 32% behind 3.11 and slightly behind the
five-year-old 3.9.6. A uv-managed 3.14 and a Homebrew 3.14.7 agree to within 2%, so it is
the version regressing rather than one bad build. This is why `requires-python` pins
`<3.14`. That upper bound is a performance decision, not a compatibility one. The code runs
fine on 3.14, it just runs slower. Do not "fix" it by widening the range.

**The 3.11-era speedups do not show up as a clean gradient.** This loop is dominated by
bytecode dispatch and small-int arithmetic rather than by anything the specializing
interpreter targets well, so the ordering runs 3.11, then 3.13, then 3.12, and 3.9.6 lands
mid-pack instead of last. Pick an interpreter by measuring, not by version number.

**Perl was the other serious candidate, and it lost on clarity rather than speed.** 3.50 s
is within 50% of the fastest Python. The problem is that plain perl silently promotes the
SplitMix64 multiply to a double and collapses the mixer to a constant:
`mix(0) == mix(1) == mix(2) == fffffffe00000000`, verified. Correct perl needs
`use integer` plus a masked logical-shift helper, roughly 20 extra lines whose absence
produces a wrong answer with no error and no warning. A generator whose whole promise is
uniqueness should not sit one forgotten pragma away from emitting the same row eight
million times.

The `bash` row is there to close the question. It is roughly 160x slower than the fastest
Python, and that measurement did not even include the Feistel. Only the formatting and the
word lookups.

uv's own startup overhead is 45-75 ms warm, which is irrelevant against a 44-second run.
The first run on a machine with no suitable interpreter pays a one-time CPython download.
Every run after that is warm.

To re-derive all of this on your own machine:

```bash
./gen-samples.sh --bench            # 1,000,000 rows
./gen-samples.sh --bench 250000     # quicker
```

## Why the tuples are unique

Seven words from a 1024-entry list is a space of `1024^7 == 2^70` tuples. Each row index is
mapped into that space by a 4-round balanced Feistel network over two 35-bit halves, keyed
by the file's seed, with SplitMix64 as the round function.

A Feistel network is a bijection for **any** round function. The round function does not
need to be invertible, well distributed, or good. That is the entire argument. Distinct
indices map to distinct 70-bit values, distinct 70-bit values slice into distinct
seven-word tuples, so a collision cannot happen. There is no birthday bound to reason about
and no retry loop.

Two consequences:

- **Zero memory.** No dedupe set, nothing that grows with the 8,622,570 rows of the 500 MB
  file. Generation is a fixed-size buffer and an odometer.
- **Checking one file is evidence for all of them.** `--verify` holds every tuple it has
  seen, which costs roughly 250 MB on the 100 MB file and would be unpleasant on the 500 MB
  one. Verify the small file. The bijection does not depend on the row count, so a clean
  result there says something about the large ones too.

Uniqueness holds **within a file**. Two files generated at different target sizes use
different seeds and are not disjoint from each other. Nothing in the design promises that,
and nothing should depend on it.

## Reproducibility

The seed and `words-1024.txt` fully determine the output. Same seed, same word list, same
target, same bytes.

The per-file seed derives from the target size, not from the file's position in `--sizes`.
That is deliberate:

```bash
./gen-samples.sh --sizes 200MB      # byte-for-byte identical to the 200MB file
                                    # produced by the default four-file run
```

Keying on list position would make the same file come out differently depending on what it
happened to be generated alongside, which would quietly invalidate the manifest.

`MANIFEST.txt` records one line per file, holding the name, target, actual bytes, row count
and sha256. It is merged by name rather than truncated, so a partial run does not erase the
record of the files still sitting on disk from the previous full run.

To confirm a file on disk is the file the manifest describes:

```bash
shasum -a 256 sample_files/sample-200mb.txt
grep sample-200mb.txt MANIFEST.txt        # sha256 is the last column
```

For a structural check instead of a hash comparison:

```bash
./gen-samples.sh --verify sample_files/sample-100mb.txt
```

That streams the file and checks every claim it makes about itself: row format, indices
ascending from 1 with no gaps, every word on the list, every tuple distinct, and a header
count matching the rows actually present.

## The word list

`words-1024.txt` holds 1024 hand-curated entries, lowercase `a-z` only, 3 to 9 letters,
chosen to be common and concrete. Output is meant to be readable and safe to paste into a
ticket.

It is deliberately not `/usr/share/dict/words`. macOS ships Webster's Second there: 235,976
entries, dense with archaic and offensive terms, variable in length, and not present on
every machine. None of that is what you want in a corpus someone will scroll through.

The count matters as much as the contents. 1024 is a power of two, and that is precisely
what makes the bit-slicing an exact bijection: 10 bits per word, 7 words, 70 bits, no
remainder and no rejection sampling. A list of any other length would break the uniqueness
guarantee in silence, so the loader treats a wrong-length list as fatal (exit 4) rather
than as something to warn about and continue past.
