# EV-2234 — current-model resource minimization

Status: engineering maintenance delivered; 2.4.2 published and installed, 2026-10-02. The owner requested further fast/stable current-model optimization with minimum practical resources. Existing build/install/publication authorization continues. Previous2.4.1 remains immutable accepted baseline; current2.4.2 keeps the same models.

Keep GigaAM v3 plain RNNT INT8 words plus E2E RNNT INT8 punctuation/case, CPU4, the eight pinned weights, PCM/chunk/tail/pre-roll and user preferences. Do not unload warm models or trim working sets to advertise lower RAM. Preserve DNS, private Data/history, the installer-on-workstation boundary and immutable previous releases.

## Implemented and verified

- Reuse an entire original short recording array instead of copying its PCM: a 20-second 16kHz float recording avoids one 1,280,000-byte payload allocation. Slice offsets/counts continue to copy exact bounds. No preprocessing or native decode setting changes.
- Read each native stream Result once, avoiding repeated token/timing marshaling.
- Queue retains a completion-only barrier while the caller owns its typed result/error/cancellation. This fixes generic result retention; ordinary memory dictation already enqueues Cancel in finally, so a persistent ordinary-idle audio leak is not established.
- Disabled sounds do not preload cue players/buffers during startup or settings refresh; explicit preview still creates its cue on demand.

Three deterministic regression cases failed on previous source. Updated Full1153 pass; Compact1146 pass with seven expected Full-only skips. Independent review accepted. Final exact-payload native16old/new output pairs and actualinstalledCLI pass; source-bound PCM unchanged/boundschecked.

## Rejected experiments

Headless paired recognizers on the published 2.4.1 native stack, same pinned public clips: CPU arena disabled saved roughly110MB resident after80 clips but p95 decoding worsened39% and77% in reversed orders. All raw and composed output hashes matched. Two versus four threads was substantially slower with negligible memory reduction. Keep default CPU4/arena. Similar encoder file sizes do not permit sharing weights: all548 verified tensor values differ.

Root verified immutable nine-file Windows and65-file native audit manifests. Experiments use guards, exact models/native pins and public PCM; no user audio or device recording. [Evidence](../../artifacts/quality-2.4.2/resources/experiments-v1/manifest.json).

## Final delivery

All required engineering gates complete: finalCI1153/tree match, payload/installed545hashes, unchanged8models, liveCAPI/ORT, source/tag8ac, published/latest402009744/eightassets verified, previous2.4.1unchanged. Actualinstalledbothenginesready/defaultmic/pause+13properties retained. Dynamic capsule/history metadata comparison was invalidated by normal app/user activity; private history/audio unread, updater does not replaceHistory. Transaction committed once; final readback passes.

Idle observation20snapshots/45.23s CPU16avg0.0108%,WS1.135GB/private1.147GB,GPU0%observed/ded15.92MB. ConstantRAM savings, personal acousticgain and universal speed gain notestablished. Shortmanaged allocation783536→42052B in bothcompletedorders. AdditionalABBA safely stopped by globalheadroom. CleanVM/real-driver endurance remain unverified. [Release](../releases/2.4.2.md), [evaluation](../models/EVALUATION-2.4.2.md), [neutral board](../../artifacts/quality-2.4.2/resources/review.html).

Root removed only owned unaccepted firstbuild/inner installer552files1732125260logicalB; currentweights/accepted evidence retained. Physicaldiskdelta unmeasured. No required install/release action remains.
