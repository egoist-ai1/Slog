# Egoist Voice — application map

## Russian Compact 2.4.2 quality profile

Warm WASAPI ring (2 s, timestamped) → take starts at press − 500 ms → complete session + adaptive release tail (min 250 ms, until 200 ms of silence, max 900 ms) → 16 kHz mono PCM → quiet-session acceptance without cropping → plain GigaAM v3 RNNT INT8 words → GigaAM v3 E2E RNNT INT8 on the same PCM → bounded punctuation/case projection → audio-confirmed known names → literal text rules → safe delivery. Both native engines use four CPU threads and greedy decoding; one user-facing profile. Disabling audio formatting retains the exact primary result. Capsule cadence remains 60 Hz (10 Hz reduced motion).

`RussianSpeechQualityService` serializes warm-up/decode, runs chunk planning and file reads off the WPF dispatcher and reports formatting availability. A formatter failure retains recognized words, announces delivery without formatting and allows retry on a later transcription after a 30-second backoff. `AudioTranscriptComposer` changes punctuation/initial case; `AudioConfirmedNameFormatter` replaces only catalogued unambiguous names with an exact Latin spelling confirmed by the secondary audio decoder and agreement of the whole chunk. Structured syntax and unknown names are protected. This is not arbitrary spelling/grammar rewriting.

Eight pinned model assets total 650090519 bytes. Source-only sherpa-onnx UTF-8 bindings fix Unicode model paths; official no-TTS native CAPI retains the stock frontend; ONNX Runtime stays unchanged. Build-RussianInstaller produces the native self-contained offline package; Update-CompactInstallation applies a verified transactional payload and preserves user Data. Full/translation flows below remain separate.

## Shipped Windows patch 2.4.1

`CaptureOperationQueue` owns FIFO native work outside the dispatcher and drains shutdown. `App` awaits background capture construction; `MainWindow` tracks init/start/release/cancel/model/translation work and exposes cached microphone state. `AudioCaptureService` resolves a fresh Windows default on Start and distinguishes transient unavailability from user pause; `MicrophoneDeviceCatalog` serializes topology observers. `SettingsWindow` refreshes inventory asynchronously, gates timers to visible pages and flushes volume before hiding. Capsule render/clock eligibility follows window visibility and reduced motion.

Bounded text repairs support catalogue-backed Russian-name/preposition joins and exact uppercase abbreviation repetition only with secondary audio confirmation and two unchanged anchors. Unknown words/endings are preserved. No general secondary lexical switching, gain/decoder parameter change or new model is selected. Final source Full1150/Compact1143+7 and cleanWindowsCI1150 pass; exact final UI24capsule/17settings/two startup modes and all545installedhashes match. Public release/tag66f79ad and installed2.4.1 are verified. [Resources](models/EVALUATION-2.4.1.md) distinguish background idle from cold CLI and unmeasured warm latency.

## Actors and flows

- Warm WASAPI pre-roll → user presses configured trigger → in-memory session +
  release tail → one 16 kHz mono conversion → local GigaAM/conditional Whisper
  ASR → target/utterance entity profile → deterministic canonicalization →
  normalization → safe clipboard/keystroke delivery.
- Esc cancels; rejected/quiet/short audio receives explicit state; sensitive password targets suppress copy/insert.
- Tray settings control triggers, sound and dictionaries; an explicit voice
  translation command routes text through the pinned current-user shared Engine
  client and fails closed before delivery on any translation error.

## Implementation map

- `Services/AudioCaptureService.cs` owns warm WASAPI, bounded pre-roll/session,
  release tail, level measurement, adaptive noise calibration and the explicit
  corpus-only WAV boundary. Current EV-2233 source adds an internal endpoint
  factory, native retirement outside callback locks with tracked cleanup and generation checks
  for stale callbacks, release tails and state notifications. Tests exercise the
  actual service with fake IWaveIn; no microphone is opened. This delta is shipped and installed in2.4.1. Root WPF files and the remaining `Services/`,
  `Core/` paths own UI, hooks, inference and delivery.
- `Core/BuiltInVocabulary.cs` owns canonical names and exact observed aliases;
  `Services/EntityProfilePolicy.cs` enables only justified technology/gaming
  ambiguities. `UserDictionary` preserves user-last precedence and regex limits.
- `Services/TranslatorClient.cs` owns the pinned named-pipe adapter, shared Host
  discovery/start without ownership, target-tier mapping and typed failures.
- `installer/EgoistVoice.iss` and `scripts/build-installer.ps1` consume the
  exact Full Offline Engine bundle, install owner-last and emit a receipt-bound
  one-file setup. `installer/EgoistVoiceBootstrap.cs` owns bounded extraction,
  embedded hash verification, inner setup exit propagation and cleanup;
  `New-/Test-EgoistVoiceSingleFile.ps1` own package creation and independent
  validation. Voice uninstall owns only `egoist-voice.owner.json`; shared Host,
  model and other product owners remain outside its scope.
- `installer/EgoistVoiceWebBootstrap*.cs` owns public web/offline delivery;
  `Export-/Build-/Test-EgoistVoiceWebInstaller.ps1` own exact payload extraction,
  PE/receipt/checksum generation and the local download/resume/hash regression
  fixture. Release assets remain outside Git and are uploaded from
  `artifacts/release/github-<tag>/`.
- `tests/Egoist.Voice.Tests/` owns source contracts; `tests/corpus/` describes private benchmark workflow.
- `scripts/` owns installer, release smoke, visual capture and operational helpers.
- `docs/HANDOFF-2.1.1.md` is the current predecessor to `STATUS.md`; historical detail remains linked, not copied.

## States to preserve

- idle/listening/processing/success/error/cancelled, model cold/ready/unloaded, translator verified/unavailable, delivery inserted/suppressed.
- Single-instance mutex, hook watchdog, GPU/CPU fallback and installer upgrade/uninstall lifecycle are durable reliability contracts.

## Resource maintenance 2.4.2

Published/installed source8ac retains the same profile/native weights. Short complete-array memory reuse avoids copy; partial/offsets remain bounded copies. Result marshals once; capture queue keeps completion-only barrier; disabled cue preload omitted. Full/CI1153,Compact1146+7,native16paritypairs and installed545hashes pass. ActualidleCPUavg0.0108%,resident1.135GB; no permanentRAM/personal acoustic/universal speed gain. [Evaluation](models/EVALUATION-2.4.2.md).
