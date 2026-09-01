# gen-samples

Generates large, deterministic sample corpora of indexed random-word rows. Everything it
needs is in this directory: `gen-samples.sh`, `gen_samples.py`, `words-1024.txt`. 

## What it makes

Plain ASCII text. Line 1 is the row count `N`. Next `N` lines are the rows.

```
3449054
0000000001 [cloth corn march energy mill horse invite]
0000000002 [tonight tender napkin attic chord citrus egg]
```

Each row is a 10-digit zero-padded index, one space, then seven words from `words-1024.txt` inside square brackets, single-spaced. Indices run 1..N in order.
Words may repeat within a row. No 2 rows are identical, and no two rows share the same seven-word tuple.


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

About 1.15 GB.

The wrapper only picks an interpreter. To skip it and run the script directly:

```bash
uv run gen_samples.py                 # same thing, no wrapper
uv run gen_samples.py --sizes 1GB     # one bigger file
```

To check the tool works before pointing it at a gigabyte:

```bash
./gen-samples.sh --selftest
```

Generates and verifies ~2 MB in a temp directory, regenerates it, confirms the two runs hash identically, round-trips a manifest, and cleans up after itself. 


## Requirements

Either `uv`, or a `python3` of 3.11 or newer. Anything older exits 3 with a message;
macOS's stock `/usr/bin/python3` 3.9.6 is below the floor and is rejected.


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

Every word-list problem is fatal. 


## Performance

Measured on Apple silicon, macOS 25.6, 1,000,000 rows. Covers full four-round Feistel plus word lookups plus buffered output. 

| Interpreter | Version | 1M rows | Rate | Projected, all four |
|---|---|---|---|---|
| uv-managed CPython | 3.11 | 2.39 s | 418k rows/s | ~44 s |
| uv-managed CPython | 3.13 | 2.51 s | 398k rows/s | ~46 s |
| uv-managed CPython | 3.12 | 2.60 s | 385k rows/s | ~48 s |
| uv-managed CPython | 3.14 | 3.15 s | 317k rows/s | ~58 s |
| Homebrew `python3` | 3.14.7 | 3.21 s | 312k rows/s | ~59 s |
| `/usr/bin/perl` | 5.34.1 | 3.50 s | 286k rows/s | ~65 s |
| `bash` | 5.3 | ~390 s | 2.6k rows/s | ~2 h 40 m |

macOS's stock `/usr/bin/python3` 3.9.6 measured 3.13 s here before the floor rose to
3.11. It now exits 3 instead of running.



To re-derive all of this on your own machine:

```bash
./gen-samples.sh --bench            # 1,000,000 rows
./gen-samples.sh --bench 250000     # quicker
```

## Reproducibility

The seed and `words-1024.txt` fully determine the output. 

The per-file seed derives from the target size, not from the file's position in `--sizes`:

```bash
./gen-samples.sh --sizes 200MB      # byte-for-byte identical to the 200MB file
                                    # produced by the default four-file run
```


## The word list

`words-1024.txt` holds 1024 hand-curated entries, lowercase `a-z` only, 3 to 9 letters,
chosen to be common and concrete. 

It is deliberately not `/usr/share/dict/words`. macOS ships Webster's Second there: 235,976
entries, dense with archaic and offensive terms, variable in length, and not present on
every machine. 