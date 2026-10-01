# Portfolio overview video

`portfolio-overview.mp4` is a 40-second, captioned introduction for a GitHub project page,
LinkedIn post, or portfolio case study. It is deliberately a **preview**, not a replacement for
the 3–5 minute interactive tour in [the storyboard](../storyboard.md).

Every UI frame comes from a locally running frontend. The only claims in its title cards are
traceable to the full test run and k6 run recorded in the README and
[`docs/benchmarks/baseline.md`](../../benchmarks/baseline.md). It does not fabricate an
authenticated assistant conversation or a time-machine result.

## Render it again

1. Start the frontend from the showcase branch with the local vault runtime.
2. Capture `1920×1080` browser frames for `/engineering` and `/history` into `docs/demo/assets/`.
3. Run `scripts/render-showcase-video.sh` from the repository root.

The output has no soundtrack so it can be embedded silently. Use the narration below for a
spoken version; captions already carry the essential message.

## Narration

> DS is a distributed people-operations platform built as a systems portfolio. Eight services
> cooperate through explicit boundaries and documented decisions. The browser holds a session;
> the Next.js BFF keeps OAuth tokens on the server. The assistant can read and propose, but a
> person approves and the server bounds the action. The project has 632 passing tests across
> sixteen projects and a clean 481-check k6 run. Search is measured honestly, and organization
> history is folded from the event log. Run the complete local tour with `scripts/demo.sh up`.

For the complete sequence, including editor/viewer behavior, the model evaluation, and chaos
testing, record [the storyboard](../storyboard.md) against a prepared local runtime.
