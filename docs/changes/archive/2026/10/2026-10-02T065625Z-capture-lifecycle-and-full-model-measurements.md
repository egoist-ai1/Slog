# Capture lifecycle correction and full-model measurements

UTC 2026-10-02T06:56:25.497813+00:00. Active EV-2232 continues; no new final build/release/install.

The source correction eliminates an independently reproduced WASAPI Join/callback lock inversion. Detach happens under state lock; native retirement is outside both locks, callback-safe, tracked and generation-bound. DSP/pre-roll320ms/tail350ms/source PCM sections are unchanged. Internal endpoint injection permits actual-service tests without a microphone. Root Full1057passed,20 new lifecycle cases; affected Full/Compact102 each. Agent frozen72-case reproducer rejects a labelled Join-under-lock mutation. Frozen27 files/source hashes and rootTRX were independently reconciled; no real-driver claim.

Full model experiments reject promotion of tested configurations:600M FP32 with exact official frontend dev78/public29 vs68/26,208 raw outputs match its initial frontend; full Whisper large-v3 FP16 dev157/public67 and three raw negative hallucinations. Fixed saved-model consensus improves development68→52 but regresses old80 26→33 and losesне. Root416 FP32 and720 consensus C# score rows agree; corpus/control/source pins retained. GigaChat tiny CUDA/NF4 backend works, full weights downloaded; guarded full load/pilot is pending without a quality claim.

Source owners released and hashes9535abd783d529ee3c557c4f0524dd268a851f18b0430d21a0d779ac4e4cc47b (service),56d1bb050028e58833885b212b8aeaad1512df0c96562dc0f409a6c064c3da65 (tests) match accepted fix. Public proof/companion board in artifacts/quality-next/capture-audit/lifecycle-fix-v1, capture-lifecycle-root-verification.json; model reports under corresponding ignored quality-next folders. Review board choices remain neutral and do not gate already authorized fixes. ConsumerV1 stays immutable; V2 is being prepared.

Files: Services/AudioCaptureService.cs; tests/Egoist.Voice.Tests/AudioCaptureServiceTests.cs; STATUS.md; docs/ARCHITECTURE.md; docs/APP_MAP.md; docs/tickets/EV-2232-quality-50.md; this immutable note and generated history index/archive links. No persisted schema, ASR default, UI, DNS, private Data, installed payload or shared system change.

Next: source/docs local commit and bounded GigaChat measured pilot; keep independent control untouched until a defensible candidate lock. Full50% word/spelling/punctuation and later cold-start/resource/stability/UI gates remain unmet.
