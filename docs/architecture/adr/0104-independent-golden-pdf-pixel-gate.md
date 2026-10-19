# ADR 0104: Independently compare flattened-PDF pixels with a golden scan

- Status: Accepted for a deterministic three-page mixed-layout fixture; representative real-world documents and human visual acceptance remain blocked
- Date: 2026-10-19

## Context

The tagged flattened-PDF gate already proved atomic replacement, structure markers, extracted text, reviewed coordinates and source-raster aspect. Those checks could still accept a raster whose pixels were materially changed while retaining the expected dimensions and placement. P12 therefore still lacked an independent pixel-level comparison for its generated scanned-document fixture.

This decision concerns validation only. It must not add a production PDF decoder, retain user documents, weaken export admission or turn one synthetic fixture into a general visual-fidelity claim.

## Decision

- Extend the test-only PdfPig inspector to decode embedded PDF images into a fresh caller-selected directory, compute SHA-256, and report deterministic page/image filenames. Bound the input to 4,000 images, 10,000 samples per dimension, 24 MiB per decoded PNG and 96 MiB aggregate decoded PNG data.
- Publish the decoded-image directory only after inspection completes. Cleanup is fail-closed and flat: it removes only a non-reparse temporary directory containing no subdirectories and only exact generated PNG names. It never recursively deletes a published directory.
- Build an independent reference from the generated pre-PDF scan with the already required ImageMagick executable, normalized to the decoded raster's exact dimensions, sRGB color space and eight-bit depth.
- Preserve the original monochrome page's normalized RMSE and two-percent-fuzz changed-pixel limits at `0.03`. Bound the added color/gradient pages at `0.05` for both metrics to accommodate color-rendering variance; the retained measured maxima must still be reported rather than represented by the looser admission limit.
- Generate a deterministic altered reference by inverting every RGB channel. Require every page's negative control to diverge by at least `0.30` normalized RMSE and `0.95` changed-pixel ratio, proving the comparison can reject materially wrong monochrome, gradient and mixed-color rasters without depending on a chosen patch color.
- Retain only ignored validation artifacts. The production exporter and its privacy behavior are unchanged; PdfPig remains test-only and outside the desktop package.

## Validation

Initial implementation commit `7998482` established the bounded decoder and single-page gate. Commit `5d17960` extends it to monochrome-landscape, color-gradient-portrait and mixed-color-landscape pages and passes the standalone native P07 gate, 652/652 full CI, and the complete nine-scenario P12 aggregate from exact clean source. Aggregate P07 evidence is `artifacts/test-results/p07-96eb169f99f14aefa31469baaa1fabb8/`: maximum positive RMSE `0.0233979`, maximum changed-pixel ratio `0.020161`, per-page negative-control RMSE `0.363235` through `0.862883`, and negative-control changed-pixel ratios `0.999996` through `1.0`. Aggregate P12 evidence is `artifacts/test-results/p12-60d795e72b9043cbb04f32b982e95274/`; all nine scenarios passed with zero production actions.

## Consequences and residual risk

- P12 now has independent per-page pixel-level evidence for a generated three-page fixture spanning monochrome/color, portrait/landscape, gradient, geometric and text content, with one sensitivity control per page.
- This does not constitute a representative real-world document corpus and does not cover photographic scans, rotation/skew, transparency, unusual color profiles, damaged PDFs or every embedded image/filter path. Cross-WebView2/PDF-decoder version thresholds are not established.
- The source reference and decoded PDF raster still depend on software render/decoder stacks. This is stronger than dimension/aspect inspection but is not an approved PDF conformance laboratory or human visual review.
- Fillable AcroForm support, PDF/UA validation, Adobe Reader interoperability and human JAWS/NVDA/Narrator acceptance remain blocked.
