# Egoist Voice — roadmap

## Current release — 2026-10-02

EV-2234 / [2.4.2](https://github.com/egoist-ai1/EgoistVoice/releases/tag/v2.4.2) is published/installed, source8ac. Current GigaAM/CPU4/arena retained; short PCM copy, repeated Result marshal, generic queue result retention and disabled-cue preload removed. Full/CI1153,Compact1146+7,native16parity pairs/all545installedhashes/bothready/defaultmic/pause+13choices pass. [Acceptance](tickets/EV-2234-current-model-resource-minimization.md), [evaluation](models/EVALUATION-2.4.2.md).

Actual20snapshots/45.23s:CPUavg0.0108%,resident1.135GB/private1.147GB,GPU0%observed/ded15.92MB. Shortmanaged783536→42052B; permanentRAM and universal speed gains notestablished. Arena-off/2threads rejected for latency; private acousticgain5–10%, realdrivers/endurance and cleanVM remain unverified. Previous2.4.1/source66f79ad inherited Windows/text/UI work and cleanup remain immutable. New owned firstbuild cleanup552files/1.732GB logicalbytes; no physicaldisk claim.

## Previous quality release — 2026-10-02

EV-2231 /2.4.0 remains immutable historical evidence. Its public80 punctuation130/rawchar241/lexical26 result is retained in2.4.1 saved-output replay, not a new full80 final acoustic run. [Original evaluation](models/EVALUATION-2.4.0.md). Current-model gain/blank and general secondary switching were measured and rejected.

## Previous publication — 2026-10-02

EV-2230 / [2.3.0](https://github.com/egoist-ai1/EgoistVoice/releases/tag/v2.3.0) was published and installed before2.4: single Russian plain RNNT profile, quiet acceptance fix, bounded native animation and transactional upgrade. CI920, independent package hashes and current installed readiness pass. Personal difficult-speech corpus and clean Windows lifecycle remain separate follow-up checks; historical Full EV-2210 is not declared complete.

Программный контекст обоих продуктов и порядок фаз до финальных установщиков —
в [`PROGRAM-PLAN.md`](../../egoist-translator/docs/program/PROGRAM-PLAN.md);
указатель на межпроектные документы — [`docs/program/README.md`](./program/README.md).

## Previous publication — 2026-09-09

`v2.2.1` promotes the Russian RC3 to the final publication channel at the owner's
explicit request. It includes the scarlet capsule, literal mode, streaming audio
and React installer. Current evidence and remaining Windows lifecycle limits live
in [STATUS](../STATUS.md) and [release notes](releases/2.2.1.md). This publication
does not close the historical Full Offline/EV-2210 verification program below.

## Historical program baseline

- `EV-2200`: зафиксировать текущие ~68 несохранённых файлов в ветке
  `v2.2-wip`. Выполняется **первым**: сейчас неделя работы существует только в
  рабочем каталоге. Коммит в ветку не является релизом.
- `EV-2201`: the 350-clip offline corpus harness is verified; private recording
  remains required before accuracy/release claims. **Записать корпус может
  только пользователь** — это единственный релизный блокер, который не
  решается кодом.
- `EV-2202`–`EV-2204`: deterministic capture, optional contextual-bias candidate
  and profile-aware entity repair are implemented; private voice/endurance and
  paired quality gates remain release requirements.
- `EV-2205`: typed strict translation-command parsing passes the locked 40/80
  fixture. User-observed product-name, inline punctuation and dotted-identifier
  regressions are fixed; protected-span normalization and pause-confirmed
  paragraph formatting remain the wider active slice.
- `EV-2206`: protected shared-host client is implemented and independently
  accepted locally; the old port/HTTP/sidecar path is gone.
- Voice `2.2.0` Full Offline field package is ready: both ASR models, GPU/CPU
  runtimes and the exact shared Engine/Q8 pack are inside one integrity-checked
  outer EXE. Internal Inno slices never leave build staging. Exact VM execution
  is not-run.
- Public `v2.2.0-preview.1` delivery exposes those same internal files as
  GitHub-compatible assets behind a hash-pinned web/offline bootstrapper. This
  is a field-test channel, not `EV-2210 SHIP`; stable remains `2.1.0`.
- Preserve the current dirty 2.1.1 candidate as the tested baseline; do not publish or silently fold it into a release.

## Next

- Finish EV-2205 with idempotent code/URL/placeholder preservation, strict
  formatting commands and paragraphs only on confirmed pause + sentence
  boundaries.
- User field test of the unsigned Full Offline Voice/Translator candidates.
- `EV-2214` guest harness -> `EV-2213`/Translator `T020` exact coexistence
  matrix; then `EV-2207` -> `EV-2208` for recovery UX/resource arbitration.

## Амендмент 002 — облик и независимость

Утверждён `2026-08-02`; спецификация
[`002`](./specs/002-voice-2.2-brand-ui-and-independence.md).

- `EV-2211` — конвейер фирменной иконки. Идёт параллельно сразу после
  `EV-2200`, как только пользователь положит исходный растр 1024×1024 в
  `assets/brand/`.
- `EV-2212` — честные состояния перевода в интерфейсе; отдельно проверяется,
  что диктовка работает при любом состоянии движка, включая его отсутствие.
- `EV-2214` — оснастка проверки установщика в Windows Sandbox и Hyper-V VM.
- `EV-2213` — независимость и жизненный цикл; выполняется **совместно** с
  Translator `T020`. Доказывает главный сценарий: удаление Translator не
  прерывает голосовой перевод в Voice ни сразу, ни после перезагрузки.

`EV-2210` не выдаёт `SHIP`, пока `EV-2211`–`EV-2214` не закрыты.

## Release gate

- Select ASR and MT models only by paired corpus evidence.
- Build the Full Offline installer and verify concurrent Egoist Voice + Egoist Translate ownership, upgrade and uninstall on clean Windows 10/11.
- `EV-2210`: run corpus, performance, stress and independent ship review; only then declare 2.2.0 final.

## Later / explicitly out of this milestone

- Cloud transcription/translation, semantic LLM rewriting and a full visual redesign.
- Public publishing or signed distribution without separate external authorization and signing access.

Completed detail belongs in `docs/changes/` or user-facing release notes, not
in this roadmap.
