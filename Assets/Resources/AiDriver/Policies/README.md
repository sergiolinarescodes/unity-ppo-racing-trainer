# Trained policy checkpoints

Trained `.onnx` files land here after running `python tools/train_unattended.py`.
The supervisor copies the final checkpoint automatically on Ctrl+C.

Ships with a reference policy: `RacingDriver-baseline.onnx` — a
multi-million-step cold-start checkpoint trained against the current
60-float observation layout + canonical physics. Use it to verify the
inference path works before you commit to a full ~12-hour training run
yourself.

To run the shipped (or your own) ONNX in the Editor: open
`Assets/_Bootstrap/TrainerTest.unity` and press Play. The bootstrap loads
the newest-mtime `RacingDriver-*.onnx` in this folder. If none exist, the
agent runs a random policy.

Your trained checkpoints overwrite / sit alongside the shipped reference —
the loader picks the freshest by mtime.

## Freezing a snapshot ONNX

When promoting a freshly trained policy into a versioned snapshot
(`v1`, `v2`, …), run:

```
python tools/snapshot_policy_onnx.py --version v2
```

This copies the newest non-baseline `RacingDriver-*.onnx` to
`RacingDriver-v2.onnx` (matching the path pinned in
`Assets/_Bootstrap/Configs/Versions/v2.json`) and prunes orphaned
step-numbered checkpoints so this folder stays at:

- `RacingDriver.onnx` — rolling latest from the supervisor
- `RacingDriver-baseline.onnx` — shipped reference
- `RacingDriver-v<N>.onnx` — one per frozen snapshot

See `docs/snapshot-version.md` for the full snapshot recipe.
