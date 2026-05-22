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
