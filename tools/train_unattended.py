"""
Unattended PPO training supervisor for the Unity PPO racing trainer.

Wraps mlagents-learn in a restart-on-crash loop:
  * activates the venv from `tools/python/.venv/`
  * launches the headless trainer at `Build/AiDriverTrainer/`
  * on non-zero exit, sleeps 30s and relaunches with --resume
  * on wall-clock --max-hours expiry, kills the trainer and copies the
    freshest ONNX into `Assets/Resources/AiDriver/Policies/`
  * on clean exit (max_steps reached), copies the final ONNX into
    `Assets/Resources/AiDriver/Policies/`
Press Ctrl+C to stop the loop cleanly.

PERSISTENT FILES — DO NOT WIPE:
  * `tools/circuit_records/records.json` — permanent per-circuit fastest
    lap history. Read by C# RewardShaper at episode-reset; written by
    both the trainer (when a new flying lap beats the stored best) and
    the dashboard's aggregator merge-min. Survives every restart and
    every `results/` wipe. This script never touches it.

Usage:
    # canonical cold-start recipe (no arguments needed):
    python tools/train_unattended.py

    # power-user overrides:
    python tools/train_unattended.py --num-envs 8 --max-hours 12
    python tools/train_unattended.py --run-id my-experiment-1
    python tools/train_unattended.py --init-from v1-20260520-1430

    # macOS / Linux: same command, build path auto-detected per OS.
"""

from __future__ import annotations

import argparse
import datetime as dt
import os
import shutil
import signal
import socket
import subprocess
import sys
import time
from pathlib import Path

try:
    import psutil
except ImportError:
    psutil = None  # type: ignore[assignment]

REPO_ROOT = Path(__file__).resolve().parents[1]
VENV_DIR = REPO_ROOT / "tools" / "python" / ".venv"
DEFAULT_CONFIG = "Assets/_Bootstrap/Configs/MlAgents/racing_driver.yaml"
DEFAULT_RESULTS_ROOT = "results"
DEFAULT_ONNX_DEST = "Assets/Resources/AiDriver/Policies"
DEFAULT_ONNX_NAME = "RacingDriver.onnx"

_proc: subprocess.Popen | None = None
_timed_out = False


def venv_python(venv_dir: Path) -> Path:
    if sys.platform == "win32":
        return venv_dir / "Scripts" / "python.exe"
    return venv_dir / "bin" / "python"


def default_build_path() -> str:
    if sys.platform == "win32":
        return "Build/AiDriverTrainer/AiDriverTrainer.exe"
    if sys.platform == "darwin":
        return "Build/AiDriverTrainer/AiDriverTrainer.app/Contents/MacOS/AiDriverTrainer"
    return "Build/AiDriverTrainer/AiDriverTrainer.x86_64"


def kill_orphans(env_proc_name: str, reason: str) -> None:
    """Kill any leftover headless trainer or mlagents-learn python processes."""
    if psutil is None:
        return
    killed: list[str] = []
    for p in psutil.process_iter(attrs=["pid", "name", "cmdline"]):
        try:
            info = p.info
            name = (info.get("name") or "").lower()
            cmdline = " ".join(info.get("cmdline") or []).lower()
        except (psutil.NoSuchProcess, psutil.AccessDenied):
            continue
        is_unity_env = env_proc_name.lower() in name
        is_mlagents = "python" in name and "mlagents" in cmdline and ("learn" in cmdline or "trainers" in cmdline)
        if is_unity_env or is_mlagents:
            try:
                p.terminate()
                killed.append(f"pid={info['pid']} name={info['name']}")
            except (psutil.NoSuchProcess, psutil.AccessDenied):
                pass
    if killed:
        time.sleep(0.5)
        for p in psutil.process_iter():
            try:
                if p.is_running() and (env_proc_name.lower() in (p.name() or "").lower()):
                    p.kill()
            except (psutil.NoSuchProcess, psutil.AccessDenied):
                pass
        print(f"[train] killed {len(killed)} orphan process(es) — {reason}", flush=True)


def port_busy(port: int) -> bool:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        try:
            s.bind(("127.0.0.1", port))
            return False
        except OSError:
            return True


def wait_port_free(port: int, max_wait_sec: int = 60) -> bool:
    deadline = time.time() + max_wait_sec
    while time.time() < deadline:
        if not port_busy(port):
            return True
        print(f"[train] port {port} busy — waiting 5s", flush=True)
        time.sleep(5)
    print(f"[train] port {port} still busy after {max_wait_sec}s — proceeding anyway", flush=True)
    return False


def find_checkpoint(run_dir: Path) -> Path | None:
    if not run_dir.exists():
        return None
    for sub in sorted(run_dir.iterdir(), key=lambda p: p.stat().st_mtime, reverse=True):
        if sub.is_dir() and (sub / "checkpoint.pt").exists():
            return sub / "checkpoint.pt"
    return None


def find_behavior_dir(run_dir: Path) -> Path | None:
    if not run_dir.exists():
        return None
    with_checkpoint = [
        sub for sub in run_dir.iterdir()
        if sub.is_dir() and (sub / "checkpoint.pt").exists()
    ]
    if with_checkpoint:
        return max(with_checkpoint, key=lambda p: p.stat().st_mtime)
    any_dir = [sub for sub in run_dir.iterdir() if sub.is_dir()]
    if any_dir:
        return max(any_dir, key=lambda p: p.stat().st_mtime)
    return None


def copy_final_onnx(run_dir: Path, dest_dir: Path, timed_out: bool) -> None:
    behavior_dir = find_behavior_dir(run_dir)
    if behavior_dir is None:
        print(
            f"[train] no ONNX found in {run_dir} — training never reached a checkpoint. "
            "Check the run directory + console logs.",
            flush=True,
        )
        return
    final_onnx = run_dir / f"{behavior_dir.name}.onnx"
    checkpoint_onnxs = sorted(behavior_dir.glob("*-*.onnx"), key=lambda p: p.stat().st_mtime, reverse=True)
    latest_checkpoint = checkpoint_onnxs[0] if checkpoint_onnxs else None

    if timed_out and latest_checkpoint is not None:
        src = latest_checkpoint
        kind = "time-out checkpoint"
    elif final_onnx.exists():
        src = final_onnx
        kind = "final"
    elif latest_checkpoint is not None:
        src = latest_checkpoint
        kind = "final"
    else:
        print(f"[train] no ONNX found in {run_dir} — training never reached a checkpoint.", flush=True)
        return

    dest_dir.mkdir(parents=True, exist_ok=True)
    dest = dest_dir / DEFAULT_ONNX_NAME
    shutil.copy2(src, dest)
    print(f"[train] copied {kind} ONNX: {src} -> {dest}", flush=True)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--run-id", default=None, help="Unique id for this run. Defaults to v1-{timestamp}.")
    parser.add_argument("--num-envs", type=int, default=4, help="Parallel Unity processes.")
    parser.add_argument("--max-hours", type=float, default=0.0, help="Wall-clock budget. 0 = unbounded.")
    parser.add_argument("--init-from", default=None, help="Run-id to warm-start from.")
    parser.add_argument("--config-path", default=DEFAULT_CONFIG)
    parser.add_argument("--build-path", default=None, help="Headless build path. Auto-detected per OS if omitted.")
    parser.add_argument("--results-root", default=DEFAULT_RESULTS_ROOT)
    parser.add_argument("--onnx-dest", default=DEFAULT_ONNX_DEST)
    parser.add_argument("--base-port", type=int, default=5104)
    parser.add_argument(
        "--timeout-wait",
        type=int,
        default=300,
        help="mlagents-learn --timeout-wait. Covers Burst AOT cold-start.",
    )
    parser.add_argument(
        "--race-scoped",
        default="0",
        help="Sets RACING_RACE_SCOPED for the headless envs. '0' = per-car terminals (canonical).",
    )
    return parser.parse_args()


def install_signal_handler():
    def _on_sigint(signum, frame):  # noqa: ANN001
        print("\n[train] supervisor received SIGINT — cleaning up", flush=True)
        global _proc
        if _proc and _proc.poll() is None:
            try:
                _proc.terminate()
                _proc.wait(timeout=5)
            except subprocess.TimeoutExpired:
                _proc.kill()
        raise KeyboardInterrupt
    signal.signal(signal.SIGINT, _on_sigint)
    if hasattr(signal, "SIGTERM"):
        signal.signal(signal.SIGTERM, _on_sigint)


def main() -> int:
    args = parse_args()
    install_signal_handler()

    run_id = args.run_id or f"v1-{dt.datetime.now():%Y%m%d-%H%M}"
    build_path = args.build_path or default_build_path()

    venv_py = venv_python(VENV_DIR)
    if not venv_py.exists():
        print(f"ERROR: venv missing at {VENV_DIR}. Run `python tools/setup.py` first.", flush=True)
        return 2
    if not Path(args.config_path).exists():
        print(f"ERROR: config not found: {args.config_path}", flush=True)
        return 1
    if not Path(build_path).exists():
        print(f"ERROR: headless build not found: {build_path}", flush=True)
        print("Build it via the Unity Editor menu: Build > AI Driver Trainer (Headless)", flush=True)
        return 1
    if psutil is None:
        print(
            "WARNING: psutil not installed — orphan cleanup disabled. "
            "Run `python tools/setup.py` to fix.",
            flush=True,
        )

    env_proc_name = Path(build_path).stem  # AiDriverTrainer

    env = os.environ.copy()
    if args.race_scoped != "":
        env["RACING_RACE_SCOPED"] = args.race_scoped
        print(f"[train] RACING_RACE_SCOPED={args.race_scoped}", flush=True)
    env["PPO_RACING_RUN_ID"] = run_id

    # Sweep any orphan envs left over from a previous session.
    kill_orphans(env_proc_name, "pre-launch cleanup")

    run_dir = Path(args.results_root) / run_id
    start_time = time.time()
    deadline = start_time + args.max_hours * 3600 if args.max_hours > 0 else float("inf")
    attempt = 0
    global _proc, _timed_out

    try:
        while True:
            if time.time() >= deadline:
                _timed_out = True
                break

            attempt += 1
            remaining = (
                str(dt.timedelta(seconds=int(deadline - time.time())))
                if args.max_hours > 0 else "no limit"
            )
            print("", flush=True)
            print("=" * 60, flush=True)
            print(
                f"[train] attempt {attempt}   run={run_id}   envs={args.num_envs}   remaining={remaining}",
                flush=True,
            )
            print("=" * 60, flush=True)

            cmd = [
                str(venv_py), "-m", "mlagents.trainers.learn",
                args.config_path,
                f"--env={build_path}",
                f"--run-id={run_id}",
                f"--num-envs={args.num_envs}",
                f"--base-port={args.base_port}",
                f"--timeout-wait={args.timeout_wait}",
                "--no-graphics",
            ]

            checkpoint = find_checkpoint(run_dir)
            if checkpoint is not None:
                print(f"[train] resuming from {checkpoint}", flush=True)
                cmd.append("--resume")
            elif run_dir.exists():
                print(f"[train] {run_dir} exists with no checkpoint.pt — clearing leftover state", flush=True)
                shutil.rmtree(run_dir, ignore_errors=True)
                if args.init_from:
                    cmd.append(f"--initialize-from={args.init_from}")
            elif args.init_from:
                cmd.append(f"--initialize-from={args.init_from}")

            wait_port_free(args.base_port, max_wait_sec=60)

            _proc = subprocess.Popen(cmd, env=env)

            while _proc.poll() is None:
                if time.time() >= deadline:
                    print(
                        f"[train] killing mlagents-learn (pid={_proc.pid}) — "
                        f"wall-clock budget hit ({args.max_hours} h)",
                        flush=True,
                    )
                    try:
                        _proc.terminate()
                        _proc.wait(timeout=5)
                    except subprocess.TimeoutExpired:
                        _proc.kill()
                    _timed_out = True
                    break
                time.sleep(5)
            if _timed_out:
                kill_orphans(env_proc_name, "wall-clock timeout")
                break

            code = _proc.returncode or 0
            kill_orphans(env_proc_name, "post-trainer cleanup")

            if code == 0:
                print("[train] mlagents-learn exited cleanly (max_steps hit). Stopping supervisor.", flush=True)
                break
            if time.time() + 30 >= deadline:
                print(f"[train] trainer exited {code} — wall-clock budget too low to relaunch, stopping", flush=True)
                _timed_out = True
                break
            print(f"[train] trainer exited {code} — sleeping 30s then resuming", flush=True)
            time.sleep(30)
    except KeyboardInterrupt:
        print("[train] supervisor interrupted by user", flush=True)
    finally:
        kill_orphans(env_proc_name, "supervisor done")
        copy_final_onnx(run_dir, Path(args.onnx_dest), _timed_out)
        elapsed = str(dt.timedelta(seconds=int(time.time() - start_time)))
        print(f"[train] supervisor exiting. elapsed={elapsed} timed_out={_timed_out}", flush=True)

    return 0


if __name__ == "__main__":
    sys.exit(main())
