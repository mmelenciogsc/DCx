# ADR 0104: Independently compare flattened-PDF pixels with a golden scan

- Status: Accepted for one generated golden fixture; representative complex-document and human visual acceptance remain blocked
- Date: 2026-10-19

## Context

The tagged flattened-PDF gate already proved atomic replacement, structure markers, extracted text, reviewed coordinates and source-raster aspect. Those checks could still accept a raster whose pixels were materially changed while retaining the expected dimensions and placement. P12 therefore still lacked an independent pixel-level comparison for its generated scanned-document fixture.

This decision concerns validation only. It must not add a production PDF decoder, retain user documents, weaken export admission or turn one synthetic fixture into a general visual-fidelity claim.

## Decision

- Extend the test-only PdfPig inspector to decode embedded PDF images into a fresh caller-selected directory, compute SHA-256, and report deterministic page/image filenames. Bound the input to 4,000 images, 10,000 samples per dimension, 24 MiB per decoded PNG and 96 MiB aggregate decoded PNG data.
- Publish the decoded-image directory only after inspection completes. Cleanup is fail-closed and flat: it removes only a non-reparse temporary directory containing no subdirectories and only exact generated PNG names. It never recursively deletes a published directory.
- Build an independent reference from the generated pre-PDF scan with the already required ImageMagick executable, normalized to the decoded raster's exact dimensions, sRGB color space and eight-bit depth.
- Require normalized RMSE no greater than `0.03` and the two-percent-fuzz absolute-error pixel ratio no greater than `0.03`.
- Generate a deterministic altered reference by placing a large black rectangle over the center. Require that negative control to diverge by at least `0.30` normalized RMSE and `0.10` changed-pixel ratio, proving the comparison is capable of rejecting a materially wrong raster.
- Retain only ignored validation artifacts. The production exporter and its privacy behavior are unchanged; PdfPig remains test-only and outside the desktop package.

## Validation

Implementation commit `7998482` passed the standalone native P07 gate and the complete nine-scenario P12 aggregate from the exact clean source. Aggregate P07 evidence is `artifacts/test-results/p07-d1c732be63154c8abc23ac1bfad42052/`: positive RMSE `0.0233979`, changed-pixel ratio `0.0186401`, negative-control RMSE `0.358607`, negative-control changed-pixel ratio `0.180714`, and decoded-raster SHA-256 `0d6024b30f9852986f08c4030508cdc12d743def979e39a8fbf234811833bf5e`. Aggregate P12 evidence is `artifacts/test-results/p12-38abeba3f6c34151b2f43fbbc9bac8b2/`; all nine scenarios passed with zero production actions. The unchanged full Windows suite passed 652/652 after one documented non-reproducing Whisper resource-result failure; the focused Whisper case and immediate full-suite rerun passed.

## Consequences and residual risk

- P12 now has independent pixel-level evidence for one generated, single-page, mostly monochrome golden fixture plus a sensitivity control.
- This does not cover representative complex, color, photographic, rotated, transparency-heavy or multipage scans; establish cross-version thresholds; or prove every embedded image/filter/color-profile path.
- The source reference and decoded PDF raster still depend on software render/decoder stacks. This is stronger than dimension/aspect inspection but is not an approved PDF conformance laboratory or human visual review.
- Fillable AcroForm support, PDF/UA validation, Adobe Reader interoperability and human JAWS/NVDA/Narrator acceptance remain blocked.
