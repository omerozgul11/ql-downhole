# SEG-2 reader

Code: `src/DownholeStudio.Formats/Seg2Reader.cs` (reader), `Seg2Writer.cs` (writer used for
synthetic data, tests and exports). Tests: `tests/DownholeStudio.Formats.Tests` with committed
golden files in `golden/`.

## Supported
* SEG-2 revision 1 (Pullan, 1990 — SEG Engineering and Groundwater Geophysics Committee).
* Byte order detected from the file descriptor block id: `55 3A` = little-endian, `3A 55` = big-endian.
* Sample formats 1 (int16), 2 (int32), 4 (IEEE float32), 5 (IEEE float64).
* String and line terminators of one or two bytes; free-format keyword strings in the file and trace
  descriptor blocks (each string is preceded by a 2-byte offset to the next; zero ends the list).
* Keywords used: `SAMPLE_INTERVAL` (required, seconds), `DELAY` (seconds; negative = pre-trigger
  samples), `CHANNEL_NUMBER`, `DESCALING_FACTOR`, `RECEIVER_LOCATION`, `SOURCE_LOCATION`, `RECEIVER`
  (used as the trace label), `ACQUISITION_DATE`, `ACQUISITION_TIME`, `NOTE`. All keywords are kept
  verbatim in the record header.

Samples are stored as recorded (no descaling is applied; the factor is kept). Dates: `DD/MMM/YYYY`,
`DD-Mon-YYYY`, `YYYY/MM/DD`, or `DD/MM/YYYY` when unambiguous; an ambiguous numeric date is left
blank rather than guessed. SEG-2 records no time zone; times are kept as written.

## Not supported (by design)
Format code 3 (20-bit packed) is rejected with an explanation. Its exact bit layout could not be
verified against the standard, and published readers disagree, so decoding it by assumption could
silently produce wrong amplitudes. Export such records as 32-bit or floating-point SEG-2.

## Failure handling
Every malformed input raises `SeismicFormatException` with a plain-language message and technical
details, for example: not a SEG-2 identifier, file shorter than the 32-byte header, invalid
terminator size, trace table too small, trace pointer outside the file, missing trace descriptor
(`0x4422`), unknown sample encoding, truncated sample data, missing or invalid `SAMPLE_INTERVAL`.
Inconsistent data-block sizes and unexpected revisions are reported as reader notes. The import
preview shows these per file; unreadable files are never imported.

## Import validation (before import)
`ImportInspector` reports: unreadable files, identical files (SHA-256), different sample rates,
different record lengths, fewer traces than the other records, missing channels, duplicate channel
numbers, reader notes, pre-trigger/trigger delays (aggregated), depths that cannot be suggested,
duplicate suggested depths, non-monotonic depth sequences and recording times that disagree with
the file order. Depth and shot suggestions come from keywords (`DEPTH x`) or file names
(`…12.5m…`, `P`, `SL`/`S+`/`LEFT`, `SR`/`S−`/`RIGHT`) and are applied only when the user ticks
"Create depths from the suggested depths".

## Adding formats
Implement `ISeismicDataReader` (`Name`, `SupportedExtensions`, `CanRead`, `ReadMetadataAsync`,
`ReadAsync`) and register it in `SeismicFormatRegistry.CreateDefault()`. Readers must not touch UI
code. Proprietary formats are added only when their specification is legally available.
