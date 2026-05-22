"""
Circuit tier-list browser. Serves two pages:

* http://localhost:8765/            — drag-and-drop tier list over the
  numbered curriculum stages (circuits/stage_<N>/*.json). Persists ordering
  to circuits/playlist.json for the trainer to consume.
* http://localhost:8765/authored    — read-only viewer for the authored-only-
  closure batch library (circuits/stage_authored_closure/*.json). Generated
  from the Unity menu "Build > Authored Closure Circuit Library (100)" via
  AuthoredOnlyClosureLoopScenario's pipeline. No tier-listing; this page is
  just a catalog so you can eyeball every output.

Usage:
    python tools/circuit_tierlist/server.py
    open http://localhost:8765 in a browser
"""
import http.server
import json
import os
import socketserver
import sys
import threading
import time
import urllib.parse
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent
CIRCUITS_DIR = ROOT / "circuits"
PLAYLIST_FILE = CIRCUITS_DIR / "playlist.json"
AUTHORED_CLOSURE_DIR = CIRCUITS_DIR / "stage_authored_closure"
TELEMETRY_DIR = ROOT / "results" / "_telemetry"
# Permanent per-circuit fastest-lap store. Survives every run, supervisor
# restart, and results/ wipe — explicitly OUTSIDE TELEMETRY_DIR. Written by
# both this aggregator AND the C# RewardShaper; merge-min semantics keep
# the two writers convergent.
CIRCUIT_RECORDS_DIR = ROOT / "tools" / "circuit_records"
CIRCUIT_RECORDS_FILE = CIRCUIT_RECORDS_DIR / "records.json"
_CIRCUIT_RECORDS_LOCK = threading.Lock()
# Per-race JSON dumps written by RaceTelemetryService on the C# side.
# Reservoir-sampled at 1-per-1000-episodes per env; capped at 50 newest
# files by the C# sink (Python is read-only on this directory).
RACES_DIR = TELEMETRY_DIR / "races"
PORT = 8765
# Hard retention cap — drop all telemetry older than this both on disk and in
# memory. Long unattended runs were OOM-crashing the dashboard because
# env_*.jsonl files grew unbounded and merged events accumulated in RAM.
# 20 min: matches the dashboard window dropdown ceiling (15m) plus slack so
# nothing the UI can request has been pruned out from under it.
TELEMETRY_RETENTION_SECONDS = 1200
# Don't rewrite jsonl files more than once per this many seconds.
TELEMETRY_PRUNE_MIN_INTERVAL = 60
# Hard ceiling on merged in-memory events — last line of defense if pruning
# can't keep up (e.g. burst from many envs within the retention window).
TELEMETRY_MAX_MERGED = 200_000

# Race-pin protocol — when the dashboard is viewing a race, write the id to
# results/_telemetry/races/.pinned so the C# DiskJsonRaceSink prune path
# leaves the file alone (DiskJsonRaceSink reads this same file). Entry
# expires after RACE_PIN_TTL_SECONDS so a tab left open forever doesn't
# leak protection indefinitely; the JS heartbeat re-touches the pin every
# few minutes to keep an actively-viewed race alive.
RACE_PIN_TTL_SECONDS = 900
PINNED_FILE = TELEMETRY_DIR / "races" / ".pinned"
_RACE_PINS = {}  # race_id -> expiry epoch
_RACE_PINS_LOCK = threading.Lock()

# ---------- Per-version manifest round-trip ----------
# Phase 2b: dashboard now edits the per-version JSON manifests under
# Assets/_Bootstrap/Configs/Versions/<id>.json instead of the legacy
# settings.json at the repo root. Each manifest is self-contained
# (physics + tire + drafting + rewards + stages + observation layout
# selection + ml_agents / runtime keys). Snapshots (anything other than
# version_id == "latest") are immutable by default — POSTs return 409
# unless ?force=1 is set, because mutating a frozen snapshot silently
# un-snapshots it relative to its committed ONNX.
MANIFESTS_DIR = ROOT / "Assets" / "_Bootstrap" / "Configs" / "Versions"
_SETTINGS_LOCK = threading.Lock()

# Sections + fields whose values are baked into the ONNX checkpoint or
# the loader's identity contract. POST mutations to any field under these
# sections are rejected. observation owns the float layout the ONNX was
# trained against; mlAgents pins behavior_name + yaml path that the
# Python trainer matches verbatim; runtime owns the prefab + ONNX
# resource paths. Mirrors observation._frozen in the schema and the C#
# ObservationSettings._frozen note.
_MANIFEST_FROZEN_SECTIONS = {"observation", "mlAgents", "runtime"}

# Top-level keys you must NEVER touch via the dashboard regardless of
# section gating — mutating these silently re-keys the manifest.
_MANIFEST_FROZEN_TOP_LEVEL = {"schemaVersion", "versionId"}


def _safe_version_id(version):
    """Sanitize a query-string version id so the filesystem path can't
    escape MANIFESTS_DIR. Allows ascii letters, digits, underscore,
    dash, and dot — same surface as a typical filename stem."""
    if not version or not isinstance(version, str):
        return None
    if len(version) > 64:
        return None
    allowed = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-."
    if any(c not in allowed for c in version):
        return None
    if version in (".", ".."):
        return None
    return version


def _manifest_path(version):
    """Return MANIFESTS_DIR/<version>.json after sanitization. None if
    the id is malformed."""
    safe = _safe_version_id(version)
    if not safe:
        return None
    return MANIFESTS_DIR / f"{safe}.json"


def _load_manifest(version):
    """Read a single manifest by version id. Returns dict or None."""
    path = _manifest_path(version)
    if not path or not path.exists():
        return None
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except Exception:
        return None


def _list_manifests():
    """List every manifest under MANIFESTS_DIR. Returns list of
    {id, displayName} pairs sorted with latest first, then alphabetical.
    Tolerates malformed JSON — bad files are skipped silently."""
    out = []
    if not MANIFESTS_DIR.exists():
        return out
    for jf in sorted(MANIFESTS_DIR.glob("*.json")):
        try:
            rec = json.loads(jf.read_text(encoding="utf-8"))
        except Exception:
            continue
        vid = rec.get("versionId") or jf.stem
        out.append({
            "id": vid,
            "displayName": rec.get("displayName", vid),
            "frozen": vid != "latest",
        })
    out.sort(key=lambda r: (r["id"] != "latest", r["id"]))
    return out


def _has_frozen_diff(new_data, old_data):
    """Return field path of the first frozen-section mutation, or None.
    Also rejects mutations to top-level identity keys (schemaVersion,
    versionId)."""
    for key in _MANIFEST_FROZEN_TOP_LEVEL:
        if new_data.get(key) != old_data.get(key):
            return key
    for section in _MANIFEST_FROZEN_SECTIONS:
        new_sec = new_data.get(section, {})
        old_sec = old_data.get(section, {})
        if not isinstance(new_sec, dict) or not isinstance(old_sec, dict):
            continue
        for k, v in new_sec.items():
            if k.startswith("_"):
                continue
            if old_sec.get(k) != v:
                return f"{section}.{k}"
    return None


def _save_manifest(version, data, force=False):
    """Atomic write with one-deep backup. Rejects frozen-section
    mutations and (unless force) rejects writes to any version other
    than 'latest'."""
    if not isinstance(data, dict):
        return False, "manifest payload must be a JSON object", 400
    path = _manifest_path(version)
    if not path:
        return False, "invalid version id", 400
    is_snapshot = version != "latest"
    if is_snapshot and not force:
        return (False,
                "snapshots are immutable; edit latest.json and snapshot a new "
                "id. Pass ?force=1 if you really need to overwrite this file.",
                409)
    with _SETTINGS_LOCK:
        current = _load_manifest(version) or {}
        # Enforce versionId identity — if the payload claims a different id,
        # treat it as a frozen-key mutation. _has_frozen_diff catches it,
        # but explicit short-circuit avoids ambiguous error messages.
        if "versionId" in data and data["versionId"] != version and current:
            return False, f"payload versionId '{data['versionId']}' doesn't match URL '{version}'", 400
        bad = _has_frozen_diff(data, current)
        if bad:
            return False, f"{bad} is frozen (baked into ONNX or identity); mutation rejected", 400
        try:
            MANIFESTS_DIR.mkdir(parents=True, exist_ok=True)
            tmp = path.with_suffix(".json.tmp")
            tmp.write_text(json.dumps(data, indent=2), encoding="utf-8")
            bak = path.with_suffix(".json.bak")
            if path.exists():
                # One rolling backup, replaced atomically per save.
                path.replace(bak)
            tmp.replace(path)
            return True, None, 200
        except Exception as e:
            return False, f"write failed: {e}", 500


def _reset_manifest(version):
    """Restore <version>.json from <version>.json.bak if present."""
    path = _manifest_path(version)
    if not path:
        return False, "invalid version id"
    bak = path.with_suffix(".json.bak")
    with _SETTINGS_LOCK:
        if not bak.exists():
            return False, "no backup to restore (no prior save for this version)"
        try:
            bak.replace(path)
            return True, None
        except Exception as e:
            return False, f"restore failed: {e}"


def _snapshot_manifest(new_version):
    """Copy latest.json → <new_version>.json. Fails if the target
    already exists (snapshots are immutable; pick a new id) or if
    latest.json doesn't exist yet."""
    safe = _safe_version_id(new_version)
    if not safe:
        return False, "invalid snapshot id (letters/digits/_-. only, max 64 chars)"
    if safe == "latest":
        return False, "'latest' is reserved for the canonical; pick a different id"
    src = MANIFESTS_DIR / "latest.json"
    dst = MANIFESTS_DIR / f"{safe}.json"
    if not src.exists():
        return False, "latest.json missing; nothing to snapshot"
    if dst.exists():
        return False, f"{safe}.json already exists — snapshots are immutable"
    with _SETTINGS_LOCK:
        try:
            data = json.loads(src.read_text(encoding="utf-8"))
            if not isinstance(data, dict):
                return False, "latest.json is not a JSON object"
            data["versionId"] = safe
            data["displayName"] = data.get("displayName", safe)
            dst.write_text(json.dumps(data, indent=2), encoding="utf-8")
            return True, None
        except Exception as e:
            return False, f"snapshot failed: {e}"


SETTINGS_HTML = """<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>Trainer Settings</title>
<style>
  :root { color-scheme: dark; }
  body { font-family: ui-monospace, Menlo, Consolas, monospace; background: #0c0d10; color: #d8dde2; margin: 0; padding: 2rem 1.5rem 5rem; max-width: 980px; margin-inline: auto; }
  header { display: flex; align-items: baseline; gap: 1rem; margin-bottom: 1.25rem; flex-wrap: wrap; }
  h1 { font-size: 1.25rem; font-weight: 600; margin: 0; }
  nav a { color: #7aa6da; text-decoration: none; margin-right: 1rem; font-size: 0.85rem; }
  nav a:hover { text-decoration: underline; }
  .picker { display: flex; align-items: center; gap: 0.5rem; margin-left: auto; }
  .picker label { font-size: 0.78rem; color: #b3bcc6; letter-spacing: 0.05em; text-transform: uppercase; }
  .picker select { background: #14171b; border: 1px solid #2c3138; color: #e6ebf0; padding: 0.3rem 0.5rem; border-radius: 3px; font-family: inherit; font-size: 0.85rem; }
  .picker .snapshot-btn { background: #2c3138; color: #e6ebf0; border: 1px solid #3a4049; border-radius: 3px; padding: 0.3rem 0.7rem; font-size: 0.78rem; cursor: pointer; }
  .note { background: #1a1d22; border-left: 3px solid #5a8ed1; padding: 0.6rem 0.9rem; margin: 0.5rem 0 1.25rem; font-size: 0.85rem; color: #b3bcc6; }
  .frozen-note { border-left-color: #d18555; color: #d6b39a; }
  .snapshot-banner { background: #2a2316; border-left: 3px solid #d18555; padding: 0.65rem 0.9rem; margin: 0.5rem 0 1.25rem; font-size: 0.85rem; color: #d6b39a; }
  details { background: #14171b; border: 1px solid #23272d; border-radius: 4px; margin-bottom: 0.6rem; }
  summary { cursor: pointer; padding: 0.65rem 0.9rem; font-weight: 600; user-select: none; }
  summary:hover { background: #181b20; }
  .section-body { padding: 0.25rem 0.9rem 0.9rem; display: grid; grid-template-columns: 1fr 1fr; gap: 0.4rem 1.2rem; }
  .field { display: grid; grid-template-columns: minmax(0,1fr) 7.5rem; gap: 0.6rem; align-items: center; padding: 0.15rem 0; }
  .field label { font-size: 0.82rem; color: #b3bcc6; overflow-wrap: anywhere; }
  .field input { background: #0c0d10; border: 1px solid #2c3138; border-radius: 3px; color: #e6ebf0; padding: 0.3rem 0.45rem; font-family: inherit; font-size: 0.82rem; text-align: right; }
  .field input:focus { outline: none; border-color: #5a8ed1; }
  .field input:disabled { color: #6b7480; background: #16191d; }
  .frozen-badge { display: inline-block; margin-left: 0.4rem; font-size: 0.7rem; padding: 0.05rem 0.4rem; background: #3a2818; color: #d6b39a; border-radius: 3px; vertical-align: 1px; }
  .actions { position: fixed; bottom: 0; left: 0; right: 0; padding: 0.75rem 1.5rem; background: #0c0d10; border-top: 1px solid #23272d; display: flex; justify-content: flex-end; gap: 0.6rem; }
  button { background: #2c3138; color: #e6ebf0; border: 1px solid #3a4049; border-radius: 3px; padding: 0.45rem 1rem; font-family: inherit; cursor: pointer; }
  button.primary { background: #2f5a8f; border-color: #3a6ba3; }
  button:hover { filter: brightness(1.15); }
  button:disabled { opacity: 0.5; cursor: not-allowed; }
  #toast { position: fixed; bottom: 4.5rem; right: 1.5rem; background: #1a1d22; border: 1px solid #2c3138; padding: 0.5rem 0.9rem; border-radius: 3px; font-size: 0.82rem; opacity: 0; transition: opacity 0.3s; pointer-events: none; }
  #toast.show { opacity: 1; }
  #toast.error { border-color: #c95d3c; color: #f0a48d; }
</style>
</head>
<body>
<header>
  <h1>Trainer Settings</h1>
  <nav>
    <a href="/training">training</a>
    <a href="/races">races</a>
    <a href="/authored">authored circuits</a>
  </nav>
  <div class="picker">
    <label for="version-select">version</label>
    <select id="version-select"></select>
    <button type="button" class="snapshot-btn" id="snapshot-btn" title="Copy latest.json to a new snapshot id">snapshot latest</button>
  </div>
</header>
<div id="banner" class="note">
  Edits write to <code>Assets/_Bootstrap/Configs/Versions/&lt;id&gt;.json</code>. <b>Changes apply on next trainer restart</b> — the running supervisor still uses its already-loaded values until you Ctrl+C and relaunch. A one-deep backup at <code>&lt;id&gt;.json.bak</code> is created on every Save.
</div>
<div id="snapshot-banner" class="snapshot-banner" hidden>
  Viewing a frozen snapshot. Edits are blocked unless you accept the warning to overwrite — snapshots are paired with a specific ONNX, and mutating them silently desyncs the pair. Switch to <code>latest</code> to edit.
</div>
<form id="settings-form"></form>
<div class="actions">
  <button type="button" id="reset-btn">Reset to backup</button>
  <button type="button" id="save-btn" class="primary">Save</button>
</div>
<div id="toast"></div>
<script>
const FROZEN_SECTIONS = new Set(["observation", "mlAgents", "runtime"]);
const FROZEN_TOP_LEVEL = new Set(["schemaVersion", "versionId"]);
let activeVersion = "latest";
let activeIsSnapshot = false;

function toast(msg, isErr) {
  const t = document.getElementById("toast");
  t.textContent = msg;
  t.classList.toggle("error", !!isErr);
  t.classList.add("show");
  setTimeout(() => t.classList.remove("show"), 3500);
}

async function loadVersions() {
  const res = await fetch("/api/versions");
  const body = await res.json();
  const sel = document.getElementById("version-select");
  sel.innerHTML = "";
  for (const v of (body.versions || [])) {
    const opt = document.createElement("option");
    opt.value = v.id;
    opt.textContent = v.displayName + (v.frozen ? " (frozen)" : "");
    sel.appendChild(opt);
  }
  // URL ?version= takes precedence so deep links land on the right form.
  const qp = new URLSearchParams(location.search);
  const requested = qp.get("version");
  if (requested && [...sel.options].some(o => o.value === requested)) {
    sel.value = requested;
  } else if ([...sel.options].some(o => o.value === "latest")) {
    sel.value = "latest";
  }
  activeVersion = sel.value || "latest";
  activeIsSnapshot = activeVersion !== "latest";
}

async function loadManifest() {
  const res = await fetch(`/api/settings?version=${encodeURIComponent(activeVersion)}`);
  if (!res.ok) {
    toast(`load failed (${res.status})`, true);
    return;
  }
  const data = await res.json();
  document.getElementById("snapshot-banner").hidden = !activeIsSnapshot;
  document.getElementById("save-btn").disabled = false;  // server enforces the gate
  renderForm(data);
}

function renderForm(data) {
  const form = document.getElementById("settings-form");
  form.innerHTML = "";
  for (const [section, body] of Object.entries(data)) {
    if (section.startsWith("_")) continue;
    if (FROZEN_TOP_LEVEL.has(section)) {
      // Top-level identity (schemaVersion, versionId) — show as read-only metadata, not a section.
      const det = document.createElement("details");
      det.open = false;
      const sum = document.createElement("summary");
      sum.textContent = section;
      const b = document.createElement("span");
      b.className = "frozen-badge";
      b.textContent = "identity — never edit";
      sum.appendChild(b);
      det.appendChild(sum);
      const body2 = document.createElement("div");
      body2.className = "section-body";
      body2.appendChild(renderField(`${section}`, body, true, "identity field — write protected"));
      det.appendChild(body2);
      form.appendChild(det);
      continue;
    }
    if (typeof body !== "object" || body === null || Array.isArray(body)) {
      // Top-level scalar (e.g. displayName) — render as a single field.
      const wrap = document.createElement("div");
      wrap.style.marginBottom = "0.6rem";
      wrap.appendChild(renderField(section, body, false));
      form.appendChild(wrap);
      continue;
    }
    form.appendChild(renderSection(section, body, FROZEN_SECTIONS.has(section)));
  }
}

function renderSection(name, obj, frozen) {
  const det = document.createElement("details");
  det.open = true;
  const sum = document.createElement("summary");
  sum.textContent = name;
  if (frozen) {
    const b = document.createElement("span");
    b.className = "frozen-badge";
    b.textContent = "frozen — baked into ONNX";
    sum.appendChild(b);
  }
  det.appendChild(sum);
  const body = document.createElement("div");
  body.className = "section-body";
  for (const [key, value] of Object.entries(obj)) {
    if (key.startsWith("_")) {
      if (typeof value === "string") {
        const n = document.createElement("div");
        n.className = "note" + (frozen ? " frozen-note" : "");
        n.style.gridColumn = "1 / -1";
        n.textContent = value;
        body.appendChild(n);
      }
      continue;
    }
    if (typeof value === "object" && value !== null && !Array.isArray(value)) {
      const sub = renderSection(`${name}.${key}`, value, frozen);
      sub.style.gridColumn = "1 / -1";
      body.appendChild(sub);
      continue;
    }
    if (Array.isArray(value)) {
      body.appendChild(renderField(`${name}.${key}`, value.join(", "), true, "comma-separated"));
      continue;
    }
    body.appendChild(renderField(`${name}.${key}`, value, frozen));
  }
  det.appendChild(body);
  return det;
}

function renderField(path, value, disabled, hint) {
  const wrap = document.createElement("div");
  wrap.className = "field";
  const label = document.createElement("label");
  label.textContent = path.includes(".") ? path.split(".").slice(1).join(".") : path;
  if (hint) label.title = hint;
  const input = document.createElement("input");
  input.dataset.path = path;
  input.value = value;
  input.disabled = !!disabled;
  if (typeof value === "number") input.type = "number";
  if (typeof value === "boolean") { input.type = "checkbox"; input.checked = value; }
  wrap.appendChild(label);
  wrap.appendChild(input);
  return wrap;
}

function collectForm() {
  const data = {};
  document.querySelectorAll("[data-path]").forEach(inp => {
    if (inp.disabled) return;
    const path = inp.dataset.path.split(".");
    let cur = data;
    for (let i = 0; i < path.length - 1; i++) {
      cur[path[i]] = cur[path[i]] || {};
      cur = cur[path[i]];
    }
    let v = inp.value;
    if (inp.type === "checkbox") v = inp.checked;
    else if (inp.type === "number") {
      const n = parseFloat(v);
      if (!Number.isNaN(n)) v = n;
    }
    cur[path[path.length - 1]] = v;
  });
  return data;
}

async function fetchRaw() {
  const res = await fetch(`/api/settings?version=${encodeURIComponent(activeVersion)}`);
  return await res.json();
}

function mergeOverFrozen(base, edits) {
  const out = JSON.parse(JSON.stringify(base));
  function walk(target, edit) {
    for (const [k, v] of Object.entries(edit)) {
      if (typeof v === "object" && v !== null && !Array.isArray(v) && typeof target[k] === "object" && target[k] !== null) {
        walk(target[k], v);
      } else {
        target[k] = v;
      }
    }
  }
  walk(out, edits);
  // Restore frozen sections + top-level identity from base.
  for (const sec of FROZEN_SECTIONS) {
    if (sec in base) out[sec] = base[sec];
  }
  for (const key of FROZEN_TOP_LEVEL) {
    if (key in base) out[key] = base[key];
  }
  return out;
}

document.getElementById("version-select").addEventListener("change", (e) => {
  activeVersion = e.target.value;
  activeIsSnapshot = activeVersion !== "latest";
  const url = new URL(location);
  url.searchParams.set("version", activeVersion);
  history.replaceState({}, "", url.toString());
  loadManifest();
});

document.getElementById("save-btn").addEventListener("click", async () => {
  const base = await fetchRaw();
  const edits = collectForm();
  const merged = mergeOverFrozen(base, edits);
  let url = `/api/settings?version=${encodeURIComponent(activeVersion)}`;
  if (activeIsSnapshot) {
    if (!confirm(`Overwrite frozen snapshot "${activeVersion}.json"? This silently desyncs the snapshot from its committed ONNX. Continue?`)) return;
    url += "&force=1";
  }
  const res = await fetch(url, { method: "POST", headers: {"Content-Type":"application/json"}, body: JSON.stringify(merged) });
  const body = await res.json();
  if (body.ok) {
    toast(`${activeVersion}.json saved`);
    loadManifest();
  } else {
    toast(`save failed: ${body.error || "unknown"}`, true);
  }
});

document.getElementById("reset-btn").addEventListener("click", async () => {
  if (!confirm(`Restore ${activeVersion}.json from its .bak?`)) return;
  const res = await fetch(`/api/settings/reset?version=${encodeURIComponent(activeVersion)}`, { method: "POST" });
  const body = await res.json();
  if (body.ok) {
    toast("restored from backup");
    loadManifest();
  } else {
    toast(`reset failed: ${body.error || "unknown"}`, true);
  }
});

document.getElementById("snapshot-btn").addEventListener("click", async () => {
  const id = prompt("New snapshot id (letters/digits/_-., e.g. 'v1', 'v2-cold'):");
  if (!id) return;
  const res = await fetch(`/api/versions/snapshot?id=${encodeURIComponent(id)}`, { method: "POST" });
  const body = await res.json();
  if (body.ok) {
    toast(`snapshot ${id} created`);
    await loadVersions();
    document.getElementById("version-select").value = id;
    activeVersion = id;
    activeIsSnapshot = true;
    loadManifest();
  } else {
    toast(`snapshot failed: ${body.error || "unknown"}`, true);
  }
});

(async () => {
  await loadVersions();
  await loadManifest();
})();
</script>
</body>
</html>
"""


def _touch_race_pin(race_id):
    """Refresh (or add) a pin for race_id and rewrite the sidecar file.
    No-op for falsy / obviously-wrong ids so an attacker can't make us
    write a giant .pinned file by spamming /api/races/<garbage>."""
    if not race_id or not isinstance(race_id, str) or len(race_id) < 8 or len(race_id) > 64:
        return
    now = time.time()
    with _RACE_PINS_LOCK:
        _RACE_PINS[race_id] = now + RACE_PIN_TTL_SECONDS
        stale = [k for k, v in _RACE_PINS.items() if v < now]
        for k in stale:
            _RACE_PINS.pop(k, None)
        live_ids = list(_RACE_PINS.keys())
    try:
        PINNED_FILE.parent.mkdir(parents=True, exist_ok=True)
        tmp = PINNED_FILE.with_suffix(PINNED_FILE.suffix + ".tmp")
        tmp.write_text("\n".join(live_ids) + ("\n" if live_ids else ""), encoding="utf-8")
        os.replace(tmp, PINNED_FILE)
    except Exception:
        # Pinning is best-effort; on failure the prune-floor still gives
        # the dashboard ~10 min of breathing room.
        pass


SECTOR_K = 9   # micro-sectors per loop, must match LoopSectorization.MicroCount in C#
SECTOR_MACRO = 3   # macro sectors


_MISSING_LAPSTART_WARNED = set()


def _warn_missing_lapstart(circuit_id):
    """One-shot stderr warning per circuit so the user notices stale files
    without spamming the log. The dashboard suppresses the START label for
    these — re-running CircuitBarrierExportScenario in Unity bakes the
    canonical LapStartAnchorIndex into every JSON."""
    if not circuit_id or circuit_id in _MISSING_LAPSTART_WARNED:
        return
    _MISSING_LAPSTART_WARNED.add(circuit_id)
    print(f"[circuit_tierlist] WARN circuit '{circuit_id}' missing LapStartAnchorIndex. "
          f"Re-run Scenario Browser → Circuit Barrier Re-Export to bake it.",
          file=sys.stderr, flush=True)


def _find_longest_straight_midpoint(anchors, cum, total, threshold=0.15):
    """DEPRECATED. Kept only for reference — the C# side now bakes
    LapStartAnchorIndex into every circuit JSON via
    CircuitBarrierExportScenario, so this Python heuristic is no longer
    used. Removed-callers commit: see _compute_sector_meta below."""
    n = len(anchors)
    if n < 4:
        return 0
    # Per-anchor curvature estimate.
    import math
    curv = [0.0] * n
    for i in range(n):
        a_prev = anchors[(i - 1 + n) % n]
        a = anchors[i]
        a_next = anchors[(i + 1) % n]
        v1x, v1y = a[0] - a_prev[0], a[1] - a_prev[1]
        v2x, v2y = a_next[0] - a[0], a_next[1] - a[1]
        l1 = math.hypot(v1x, v1y)
        l2 = math.hypot(v2x, v2y)
        if l1 < 1e-6 or l2 < 1e-6:
            continue
        cross = v1x * v2y - v1y * v2x
        dot = v1x * v2x + v1y * v2y
        ang = math.atan2(cross, dot)   # signed turn angle at i
        ds = 0.5 * (l1 + l2)
        curv[i] = abs(ang) / max(ds, 1e-6)

    # Longest contiguous run with curvature below threshold. Walk 2n to
    # capture runs that cross the seam at i=0.
    best_start, _best_end, best_len = 0, 0, 0.0
    run_start = -1
    run_len = 0.0
    for k in range(2 * n):
        i = k % n
        if curv[i] < threshold:
            if run_start < 0:
                run_start = i
                run_len = 0.0
            nxt = (i + 1) % n
            seg = (total - cum[i]) if nxt == 0 else (cum[nxt] - cum[i])
            run_len += seg
            if run_len > best_len:
                best_len = run_len
                best_start = run_start
            if k >= n and i == best_start:
                break
        else:
            run_start = -1
            run_len = 0.0
    if best_len <= 0:
        # Fallback: longest single segment.
        max_seg, max_idx = -1.0, 0
        for i in range(n):
            nxt = (i + 1) % n
            seg = (total - cum[i]) if nxt == 0 else (cum[nxt] - cum[i])
            if seg > max_seg:
                max_seg = seg
                max_idx = i
        best_start, _best_end, best_len = max_idx, (max_idx + 1) % n, max_seg

    # Midpoint arc along the run, then nearest anchor.
    start_arc = cum[best_start]
    mid_arc = (start_arc + best_len * 0.5) % total
    # Find largest i with cum[i] <= mid_arc.
    best = 0
    for i in range(n):
        if cum[i] <= mid_arc:
            best = i
        else:
            break
    return best


def _compute_sector_meta(anchors, lap_start_override=None, sector_anchors_override=None,
                         circuit_id=None):
    """Given a list of [x, y] anchors in lap-traversal order, derive
    (lap_start_idx, [sector boundary anchor indices, K of them], total_arc).

    C# is the SINGLE source of truth for lap-start and sector boundaries.
    The overrides (LapStartAnchorIndex, MicroBoundaryAnchor) come straight
    from ClosedLoopService.BuildClosedLoop — anchors are post-CCW-flip and
    the indices reference *this* sequence. If either override is missing
    we log a one-shot warning and return (-1, [], total) so the renderer
    knows to suppress the START label rather than guess wrong."""
    if not anchors or len(anchors) < 2:
        return 0, [], 0.0

    # Per-segment arc lengths (closing wrap included).
    arcs = []
    n = len(anchors)
    for i in range(n):
        a = anchors[i]
        b = anchors[(i + 1) % n]
        dx = b[0] - a[0]
        dy = b[1] - a[1]
        arcs.append((dx * dx + dy * dy) ** 0.5)
    cum = [0.0]
    for d in arcs[:-1]:
        cum.append(cum[-1] + d)
    total = cum[-1] + arcs[-1]
    if total <= 0:
        return 0, [], 0.0

    if lap_start_override is None or not (0 <= lap_start_override < n):
        _warn_missing_lapstart(circuit_id)
        # Sentinel: lap_start_idx = -1 tells the renderer to skip the START
        # label. Empty sector list ditto for sector posts. No heuristic
        # fallback — guessing here is what caused the "wrong start line on
        # some circuits" bug; the only correct value is the one Unity baked.
        return -1, [], total

    lap_start_idx = lap_start_override

    if sector_anchors_override and len(sector_anchors_override) > 0:
        clamped = [max(0, min(int(i), n - 1)) for i in sector_anchors_override]
        return lap_start_idx, clamped, total

    # Sector override missing but lap-start present → also a stale JSON,
    # since CircuitBarrierExportScenario writes them together. Warn and
    # return no sector posts; the canonical lap-start still anchors the
    # START gate.
    _warn_missing_lapstart(circuit_id)
    return lap_start_idx, [], total


def load_circuits():
    """Walk circuits/stage_*/*.json and return one record per file."""
    out = []
    if not CIRCUITS_DIR.exists():
        return out
    for stage_dir in sorted(CIRCUITS_DIR.glob("stage_*")):
        try:
            stage_id = int(stage_dir.name.split("_")[1])
        except (IndexError, ValueError):
            continue
        for jf in sorted(stage_dir.glob("*.json")):
            try:
                with open(jf, "r", encoding="utf-8") as f:
                    rec = json.load(f)
            except Exception:
                continue
            placements = rec.get("Placements") or []
            anchors_raw = rec.get("Anchors") or []
            walls = rec.get("Walls") or []      # [[ax,ay,bx,by], ...] world XZ
            kerbs = rec.get("Kerbs") or []      # [[p0x,p0y,...,p3x,p3y], ...] world XZ
            # Normalise anchor shape to [x,y] pairs (legacy stage_<N> JSON
            # already uses pairs; authored uses {X,Z} dicts).
            anchors = []
            for a in anchors_raw:
                if isinstance(a, (list, tuple)) and len(a) >= 2:
                    anchors.append([a[0], a[1]])
                elif isinstance(a, dict):
                    ax = a.get("X", a.get("x"))
                    az = a.get("Z", a.get("z", a.get("Y", a.get("y"))))
                    if ax is not None and az is not None:
                        anchors.append([ax, az])
            if not placements and not anchors:
                continue
            # Apply the same anchor → current-world rescale used by
            # _record_from_json (anchors live at legacy CellSize=2; walls
            # and live telemetry at current CellSize=3).
            anchor_scale = _infer_anchor_to_world_scale(placements, walls, anchors)
            if anchor_scale != 1.0:
                anchors = [[a[0] * anchor_scale, a[1] * anchor_scale] for a in anchors]
            xs, ys = [], []
            if anchors:
                xs.extend(a[0] for a in anchors)
                ys.extend(a[1] for a in anchors)
            else:
                xs.extend(p["X"] for p in placements)
                ys.extend(p["Y"] for p in placements)
            for w in walls:
                xs.extend((w[0], w[2]))
                ys.extend((w[1], w[3]))
            for q in kerbs:
                xs.extend((q[0], q[2], q[4], q[6]))
                ys.extend((q[1], q[3], q[5], q[7]))
            circuit_id_for_warn = rec.get("Id") or jf.stem
            lap_start_idx, sector_idx, total_arc = _compute_sector_meta(
                anchors,
                lap_start_override=rec.get("LapStartAnchorIndex"),
                sector_anchors_override=rec.get("MicroBoundaryAnchor"),
                circuit_id=circuit_id_for_warn)
            out.append({
                "id": rec.get("Id") or jf.stem,
                "stageId": rec.get("StageId", stage_id),
                "stageName": rec.get("StageName", f"stage_{stage_id}"),
                "totalLength": rec.get("TotalLength", total_arc),
                "pieceCount": len(placements),
                "anchorCount": rec.get("AnchorCount", len(anchors)),
                "minX": min(xs), "maxX": max(xs),
                "minY": min(ys), "maxY": max(ys),
                "anchors": anchors,
                "cells": [[p["X"], p["Y"]] for p in placements],
                "walls": walls,
                "kerbs": kerbs,
                "lapStartIdx": lap_start_idx,
                "sectorIdx": sector_idx,
                "sectorMacro": SECTOR_MACRO,
            })
    return out


# TrackPieceConstants.CellSize (current world cell size) and the legacy
# scale anchors were authored at. Wall extrusion biases world_per_cell
# upward (~3.17 for CellSize=3) so we can't infer this purely from wall
# spans — hardcode the pair the C# side actually uses. Bump CURRENT if
# TrackPieceConstants.CellSize ever moves again.
CURRENT_CELL_SIZE = 3.0
LEGACY_ANCHOR_CELL_SIZE = 2.0


def _infer_anchor_to_world_scale(placements, walls, anchors):
    """Anchors live in legacy CellSize=2 world units; walls and live
    telemetry live in current CellSize=3 world units. Without a correction
    the anchor-traced circuit lands at 2/3 the size of the wall/telemetry
    overlay → mismatched canvases. Detect whether anchors actually need
    rescaling by checking their per-cell spacing (a_span / p_span) against
    LEGACY_ANCHOR_CELL_SIZE. Returns CURRENT/LEGACY (=1.5) when legacy is
    detected, 1.0 otherwise.

    Inferring from wall span produces a slightly inflated ratio because
    walls extrude outward of the road edge, so we use the integer
    placement grid as the unbiased reference."""
    if not placements or not anchors:
        return 1.0
    try:
        px = [p.get("X", 0) for p in placements]
        pz = [p.get("Y", 0) for p in placements]
        p_span = max(max(px) - min(px), max(pz) - min(pz))
        if p_span <= 0:
            return 1.0
        ax = [a[0] for a in anchors]
        az = [a[1] for a in anchors]
        a_span = max(max(ax) - min(ax), max(az) - min(az))
        if a_span <= 0:
            return 1.0
        anchor_per_cell = a_span / p_span
        # Tolerant match against known scales. ±15% covers per-port offsets
        # and the wall-extrusion bias on the current scale.
        if abs(anchor_per_cell - LEGACY_ANCHOR_CELL_SIZE) / LEGACY_ANCHOR_CELL_SIZE <= 0.15:
            return CURRENT_CELL_SIZE / LEGACY_ANCHOR_CELL_SIZE
        if abs(anchor_per_cell - CURRENT_CELL_SIZE) / CURRENT_CELL_SIZE <= 0.15:
            return 1.0
        return 1.0
    except (TypeError, KeyError, IndexError):
        return 1.0


def _expand_bbox_with_points(bbox, *point_streams, pad_frac=0.05):
    """Returns a new bbox dict that unions the input bbox with every (x, z)
    point yielded by the supplied iterables, then pads by `pad_frac` of the
    larger axis. Used by every race-viz page that overlays telemetry on a
    circuit — without it, wall hits / end positions outside the original
    circuit footprint clip off the canvas or render in mis-aligned pixels.
    Returns None if no input data exists at all."""
    minX = bbox["minX"] if bbox else None
    maxX = bbox["maxX"] if bbox else None
    minY = bbox["minY"] if bbox else None
    maxY = bbox["maxY"] if bbox else None
    for stream in point_streams:
        for pt in stream:
            try:
                x = float(pt[0])
                z = float(pt[1])
            except (TypeError, ValueError, IndexError):
                continue
            minX = x if minX is None else min(minX, x)
            maxX = x if maxX is None else max(maxX, x)
            minY = z if minY is None else min(minY, z)
            maxY = z if maxY is None else max(maxY, z)
    if minX is None:
        return None
    w = max(1e-6, maxX - minX)
    h = max(1e-6, maxY - minY)
    pad = max(w, h) * pad_frac
    return {
        "minX": minX - pad, "maxX": maxX + pad,
        "minY": minY - pad, "maxY": maxY + pad,
    }


def _record_from_json(jf, default_stage_id, default_stage_name):
    """Shared record builder used by both the tier-list and the authored
    viewer. Returns None if the file is unreadable or has no placements."""
    try:
        with open(jf, "r", encoding="utf-8") as f:
            rec = json.load(f)
    except Exception:
        return None
    placements = rec.get("Placements") or []
    anchors_raw = rec.get("Anchors") or []
    walls = rec.get("Walls") or []
    kerbs = rec.get("Kerbs") or []
    # Anchors come in two shapes:
    #   * legacy stage_<N> circuits — list of [x, y] pairs
    #   * authored-closure circuits — list of {X, Z} dicts (Unity JsonUtility)
    # Normalise to [x, y] pairs so the SVG renderer has one path.
    anchors = []
    for a in anchors_raw:
        if isinstance(a, dict):
            x = a.get("X", a.get("x"))
            y = a.get("Z", a.get("z", a.get("Y", a.get("y"))))
            if x is not None and y is not None:
                anchors.append([x, y])
        elif isinstance(a, (list, tuple)) and len(a) >= 2:
            anchors.append([a[0], a[1]])
    if not placements and not anchors:
        return None
    # Rescale anchors to current-world coords so they line up with walls,
    # kerbs, and live telemetry. Without this the heatmap on /training shows
    # a small anchor-traced circuit floating away from the wall/end-point
    # cluster (CellSize 2 → 3 migration left anchors behind).
    anchor_scale = _infer_anchor_to_world_scale(placements, walls, anchors)
    if anchor_scale != 1.0:
        anchors = [[a[0] * anchor_scale, a[1] * anchor_scale] for a in anchors]
    xs, ys = [], []
    if anchors:
        xs.extend(a[0] for a in anchors)
        ys.extend(a[1] for a in anchors)
    else:
        xs.extend(p["X"] for p in placements)
        ys.extend(p["Y"] for p in placements)
    for w in walls:
        xs.extend((w[0], w[2]))
        ys.extend((w[1], w[3]))
    for q in kerbs:
        xs.extend((q[0], q[2], q[4], q[6]))
        ys.extend((q[1], q[3], q[5], q[7]))

    # Closure-piece overlays — each segment is one closure shape's per-piece
    # spine in world XZ. Authored-only-closure circuits emit these so the
    # viewer can paint generator-bridged arcs in a contrasting colour.
    closure_segments = []
    for seg in (rec.get("ClosureSegments") or []):
        seg_anchors_raw = seg.get("Anchors") if isinstance(seg, dict) else None
        if not seg_anchors_raw:
            continue
        pts = []
        for a in seg_anchors_raw:
            if isinstance(a, dict):
                x = a.get("X", a.get("x"))
                y = a.get("Z", a.get("z", a.get("Y", a.get("y"))))
                if x is not None and y is not None:
                    pts.append([x, y])
            elif isinstance(a, (list, tuple)) and len(a) >= 2:
                pts.append([a[0], a[1]])
        if len(pts) >= 2:
            # Closure segments share the anchor coord system (saved-once,
            # legacy CellSize). Apply the same correction.
            if anchor_scale != 1.0:
                pts = [[p[0] * anchor_scale, p[1] * anchor_scale] for p in pts]
            closure_segments.append(pts)
            for px, py in pts:
                xs.append(px)
                ys.append(py)
    circuit_id_for_warn = rec.get("Id") or jf.stem
    lap_start_idx, sector_idx, total_arc = _compute_sector_meta(
        anchors,
        lap_start_override=rec.get("LapStartAnchorIndex"),
        sector_anchors_override=rec.get("MicroBoundaryAnchor"),
        circuit_id=circuit_id_for_warn)
    return {
        "id": rec.get("Id") or jf.stem,
        "stageId": rec.get("StageId", default_stage_id),
        "stageName": rec.get("StageName", default_stage_name),
        "totalLength": rec.get("TotalLength", total_arc),
        "pieceCount": len(placements),
        "anchorCount": rec.get("AnchorCount", len(anchors)),
        # Authored-closure extras (default 0 for legacy stage_<N> records).
        "seed": rec.get("Seed", 0),
        "authoredCardCount": rec.get("AuthoredCardCount", 0),
        "closureCardCount": rec.get("ClosureCardCount", 0),
        "minX": min(xs), "maxX": max(xs),
        "minY": min(ys), "maxY": max(ys),
        "anchors": anchors,
        "cells": [[p["X"], p["Y"]] for p in placements],
        "walls": walls,
        "kerbs": kerbs,
        "closureSegments": closure_segments,
        "lapStartIdx": lap_start_idx,
        "sectorIdx": sector_idx,
        "sectorMacro": SECTOR_MACRO,
    }


def load_authored_closure_circuits():
    """Walk circuits/stage_authored_closure/*.json and return one record
    per file in seed order. Used by the /authored viewer page only — these
    circuits are deliberately excluded from the numbered-stage tier list."""
    out = []
    if not AUTHORED_CLOSURE_DIR.exists():
        return out
    for jf in sorted(AUTHORED_CLOSURE_DIR.glob("*.json"), key=lambda p: p.stat().st_mtime):
        rec = _record_from_json(jf, default_stage_id=-1,
                                default_stage_name="authored_closure")
        if rec is not None:
            out.append(rec)
    return out


def load_playlist():
    if not PLAYLIST_FILE.exists():
        return {}
    try:
        with open(PLAYLIST_FILE, "r", encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return {}


def save_playlist(data):
    CIRCUITS_DIR.mkdir(parents=True, exist_ok=True)
    with open(PLAYLIST_FILE, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=2)


# ============================================================================
# Chalk-paper design system — shared primitives.
#
# Lifted from the `Race Analytics.html` Claude Design prototype so every page
# served by this dashboard reads on the same cream paper background with the
# Inter Tight + JetBrains Mono pairing. Each page composes its body with the
# `chrome_header()` helper and a page-local `<style>` block for IA-specific
# rules; the global palette, typography, and component primitives all live in
# BASE_STYLES.
# ============================================================================

BASE_HEAD = r"""<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link href="https://fonts.googleapis.com/css2?family=Inter+Tight:wght@500;600;700;800;900&family=JetBrains+Mono:wght@500;600;700&display=swap" rel="stylesheet">"""


BASE_STYLES = r"""
  :root {
    --paper:        #efece4;
    --paper-2:      #e6e2d6;
    --paper-3:      #ddd7c7;
    --card:         #f6f3eb;
    --ink:          #1f1c17;
    --ink-2:        #4a4540;
    --ink-3:        #837c72;
    --ink-4:        #b3aca0;
    --hair:         #d4cdba;
    --hair-2:       #c8c0aa;
    --signal:       #c84a2c;
    --signal-soft:  #e8a594;
    --good:         #5b7a4e;
    --warn:         #b88840;
    --bad:          #b04428;
    --slate:        #4a6c8a;
    --shadow:       0 1px 0 #fff7 inset, 0 1px 2px #00000010, 0 6px 18px -10px #2a241b30;
    --shadow-lg:    0 1px 0 #fff8 inset, 0 8px 32px -16px #2a241b40;
    --r-sm: 6px;  --r: 10px;  --r-lg: 14px;
    --ease: cubic-bezier(.2,.7,.2,1);
  }
  /* hue palette for cars / per-driver series — muted, paper-friendly */
  .c0  { --car: oklch(0.62 0.13 25);  }
  .c1  { --car: oklch(0.62 0.11 60);  }
  .c2  { --car: oklch(0.66 0.10 110); }
  .c3  { --car: oklch(0.58 0.10 160); }
  .c4  { --car: oklch(0.58 0.10 210); }
  .c5  { --car: oklch(0.52 0.12 260); }
  .c6  { --car: oklch(0.55 0.13 310); }
  .c7  { --car: oklch(0.60 0.13 350); }

  *, *::before, *::after { box-sizing: border-box; }
  html, body { margin: 0; padding: 0; }
  body {
    background: var(--paper);
    color: var(--ink);
    font-family: 'Inter Tight', system-ui, sans-serif;
    font-size: 15px;
    font-weight: 600;
    line-height: 1.35;
    letter-spacing: -0.01em;
    -webkit-font-smoothing: antialiased;
    background-image:
      radial-gradient(circle at 12% 18%, #00000004 0, transparent 60%),
      radial-gradient(circle at 84% 76%, #00000005 0, transparent 50%);
  }
  a { color: var(--ink); text-decoration: none; }

  /* ---------- TOP NAV / HEADER ---------- */
  header.top {
    display: flex; align-items: center; gap: 24px;
    padding: 14px 22px;
    border-bottom: 1px solid var(--hair);
    background: var(--paper);
    position: sticky; top: 0; z-index: 30;
    backdrop-filter: blur(6px);
  }
  .brand {
    font-family: 'Inter Tight', sans-serif;
    font-size: 26px; font-weight: 900; line-height: 1;
    letter-spacing: -0.03em; text-transform: uppercase;
    color: var(--ink);
  }
  .brand small {
    display: block;
    font-family: 'JetBrains Mono', monospace;
    font-size: 11px; font-weight: 600; letter-spacing: 0.14em; text-transform: uppercase;
    color: var(--ink-3); margin-top: 5px;
  }
  .nav-tabs { display: flex; gap: 4px; }
  .nav-tabs a {
    font-family: 'JetBrains Mono', monospace; font-size: 12px; font-weight: 700;
    letter-spacing: 0.10em; text-transform: uppercase;
    color: var(--ink-3);
    padding: 8px 12px; border-radius: var(--r-sm);
    transition: all 0.18s var(--ease);
  }
  .nav-tabs a:hover { background: var(--paper-2); color: var(--ink); }
  .nav-tabs a.active {
    background: var(--ink); color: var(--paper);
  }

  .meta { display: flex; gap: 24px; font-family: 'JetBrains Mono', monospace; font-size: 13px; font-weight: 600; }
  .meta dl { margin: 0; display: flex; flex-direction: column; gap: 3px; }
  .meta dt { color: var(--ink-3); font-size: 11px; font-weight: 700; letter-spacing: 0.12em; text-transform: uppercase; }
  .meta dd { margin: 0; color: var(--ink); font-weight: 700; }
  .meta dd b { font-weight: 800; }
  .pill {
    display: inline-flex; align-items: center; gap: 6px;
    padding: 3px 10px; border-radius: 999px;
    background: var(--paper-2); border: 1px solid var(--hair);
    font-family: 'JetBrains Mono', monospace; font-size: 12px; font-weight: 700;
    color: var(--ink-2);
  }
  .pill::before {
    content: ''; width: 6px; height: 6px; border-radius: 50%;
    background: var(--good); box-shadow: 0 0 0 3px #5b7a4e22;
  }
  .pill.bad::before  { background: var(--signal); box-shadow: 0 0 0 3px #c84a2c22; }
  .pill.warn::before { background: var(--warn);   box-shadow: 0 0 0 3px #b8884022; }
  .pill.neutral::before { background: var(--ink-3); box-shadow: 0 0 0 3px #837c7222; }

  .actions { display: flex; gap: 8px; align-items: center; }
  .btn {
    font: inherit; font-size: 13px; font-weight: 700;
    padding: 9px 14px; border-radius: var(--r-sm);
    background: var(--card); border: 1px solid var(--hair);
    color: var(--ink-2); cursor: pointer;
    transition: all 0.18s var(--ease);
    letter-spacing: -0.005em;
    text-decoration: none; display: inline-flex; align-items: center; gap: 6px;
  }
  .btn:hover { background: var(--paper-2); border-color: var(--hair-2); color: var(--ink); }
  .btn.primary { background: var(--ink); color: var(--paper); border-color: var(--ink); }
  .btn.primary:hover { background: #000; }
  .btn .kbd { font-family: 'JetBrains Mono', monospace; opacity: 0.55; margin-left: 7px; font-size: 11px; font-weight: 700; }
  .btn-link {
    font-family: 'JetBrains Mono', monospace; font-size: 12px; font-weight: 700;
    color: var(--ink-3); padding: 6px 10px;
  }
  .btn-link:hover { color: var(--ink); }

  select, input[type="text"], input[type="search"] {
    font: inherit; font-size: 13px; font-weight: 600;
    padding: 8px 12px; border-radius: var(--r-sm);
    border: 1px solid var(--hair); background: var(--card);
    color: var(--ink);
  }
  select { font-family: 'JetBrains Mono', monospace; font-size: 12px; font-weight: 700; color: var(--ink-2); padding: 8px 10px; }
  input[type="text"]:focus, input[type="search"]:focus, input:focus, select:focus {
    outline: 2px solid var(--signal-soft); outline-offset: -1px;
  }

  /* ---------- CARDS ---------- */
  .card {
    background: var(--card);
    border: 1px solid var(--hair);
    border-radius: var(--r-lg);
    box-shadow: var(--shadow);
    overflow: hidden;
  }
  .card-head {
    display: flex; align-items: baseline; justify-content: space-between;
    padding: 12px 16px;
    border-bottom: 1px solid var(--hair);
    gap: 12px;
  }
  .card-head h2 {
    margin: 0;
    font-size: 13px; font-weight: 800;
    letter-spacing: 0.12em; text-transform: uppercase; color: var(--ink);
    font-family: 'Inter Tight', sans-serif;
  }
  .card-head .sub {
    font-family: 'JetBrains Mono', monospace; font-size: 12px; font-weight: 600;
    color: var(--ink-3);
  }
  .card-body { padding: 12px 16px; }

  /* ---------- KPI RIBBON ---------- */
  .kpis {
    display: grid; grid-template-columns: repeat(6, 1fr); gap: 0;
    border-bottom: 1px solid var(--hair);
    background: var(--card);
  }
  .kpis.in-card { border-bottom: none; border-top: 1px solid var(--hair); }
  .kpi {
    padding: 14px 16px;
    border-right: 1px solid var(--hair);
  }
  .kpi:last-child { border-right: none; }
  .kpi-label {
    font-family: 'Inter Tight', sans-serif; font-size: 11px; font-weight: 800;
    letter-spacing: 0.12em; text-transform: uppercase; color: var(--ink-3);
  }
  .kpi-value {
    font-family: 'Inter Tight', sans-serif; font-weight: 900;
    font-size: 44px; line-height: 1; color: var(--ink);
    margin-top: 6px;
    letter-spacing: -0.035em;
  }
  .kpi-sub {
    font-family: 'JetBrains Mono', monospace; font-size: 12px; font-weight: 600;
    color: var(--ink-3); margin-top: 6px;
  }

  /* ---------- SCRUBBER ---------- */
  .scrub-strip {
    padding: 14px 22px 8px;
    border-bottom: 1px solid var(--hair);
    background: linear-gradient(180deg, var(--paper) 0%, var(--paper-2) 100%);
    position: sticky; top: 64px; z-index: 28;
    backdrop-filter: blur(6px);
  }
  .scrub-row { display: flex; align-items: center; gap: 14px; }
  .play-btn {
    width: 36px; height: 36px; border-radius: 50%;
    background: var(--ink); color: var(--paper);
    border: none; cursor: pointer; display: grid; place-items: center;
    box-shadow: var(--shadow);
    transition: transform 0.18s var(--ease);
  }
  .play-btn:hover { transform: scale(1.06); }
  .play-btn svg { width: 12px; height: 12px; }
  .time-readout {
    font-family: 'JetBrains Mono', monospace; font-size: 14px; font-weight: 700;
    color: var(--ink-2); min-width: 110px;
  }
  .time-readout b { color: var(--ink); font-weight: 800; font-size: 16px; }
  .scrub-track {
    flex: 1; height: 56px; position: relative;
    cursor: crosshair;
  }
  .scrub-track svg { display: block; width: 100%; height: 100%; }
  .speed-pick { display: flex; gap: 2px; }
  .speed-pick button {
    font: inherit; font-family: 'JetBrains Mono', monospace; font-size: 12px; font-weight: 700;
    padding: 6px 10px; border: 1px solid var(--hair); background: var(--card);
    color: var(--ink-3); cursor: pointer; border-radius: var(--r-sm);
  }
  .speed-pick button.on { background: var(--ink); color: var(--paper); border-color: var(--ink); }

  /* ---------- PILL-TOGGLE ---------- */
  .pill-toggle {
    font-family: 'JetBrains Mono', monospace; font-size: 12px; font-weight: 700;
    padding: 6px 12px; border-radius: 999px;
    background: var(--paper-2); border: 1px solid var(--hair);
    color: var(--ink-2); cursor: pointer; user-select: none;
    transition: all 0.16s var(--ease);
    display: inline-flex; align-items: center; gap: 5px;
  }
  .pill-toggle.on { background: var(--ink); color: var(--paper); border-color: var(--ink); }
  .pill-toggle .dot {
    display: inline-block; width: 6px; height: 6px; border-radius: 50%;
  }
  .pill-toggle .dot.overtake { background: #6f8e6a; }
  .pill-toggle .dot.car_hit  { background: var(--signal); }
  .pill-toggle .dot.wall_hit { background: var(--warn); }

  /* ---------- STATUS BADGE (reasons) ---------- */
  .status {
    font-family: 'Inter Tight', sans-serif; font-size: 11px; font-weight: 800;
    text-transform: uppercase; letter-spacing: 0.08em;
    padding: 3px 8px; border-radius: 4px;
    background: var(--paper-3); color: var(--ink-2);
    white-space: nowrap;
  }
  .status[data-r="Failure_Wreck"],
  .status[data-r="wall_max"],
  .status[data-r="Failure_Collision"]   { background: #e7c1b5; color: #6e2410; }
  .status[data-r="Failure_OffTrack"],
  .status[data-r="off_track"],
  .status[data-r="step_max"],
  .status[data-r="timeout"]             { background: #ead7b0; color: #644413; }
  .status[data-r="Finished"],
  .status[data-r="Success"],
  .status[data-r="finish"],
  .status[data-r="lap_done"]            { background: #c8d8bf; color: #2c4520; }
  .status[data-r="agent_request"]       { background: var(--paper-3); color: var(--ink-3); }

  /* ---------- TIER-LIST BADGE ---------- */
  .tier-label {
    width: 42px; height: 42px; border-radius: 8px;
    display: grid; place-items: center;
    font-family: 'Inter Tight', sans-serif;
    font-size: 22px; font-weight: 900; line-height: 1;
    letter-spacing: -0.04em;
    color: var(--paper);
    box-shadow: var(--shadow);
  }
  .tier-label--S { background: var(--signal); }
  .tier-label--A { background: var(--warn); }
  .tier-label--B { background: #d4a04a; color: var(--ink); }
  .tier-label--C { background: var(--good); }
  .tier-label--D { background: var(--slate); }
  .tier-label--F { background: var(--ink-3); }

  /* ---------- UTILITY ---------- */
  .row-flex { display: flex; gap: 10px; align-items: center; }
  .spacer { flex: 1; }
  .muted { color: var(--ink-3); }
  .mono { font-family: 'JetBrains Mono', monospace; }
  hr.hair { border: 0; border-top: 1px solid var(--hair); margin: 0; }

  /* ---------- LOADING / STATUS BADGES (shared across all pages) ---------- */
  .relative { position: relative; }
  .spinner {
    display: inline-block; width: 12px; height: 12px;
    border: 2px solid var(--hair); border-top-color: var(--ink);
    border-radius: 50%; animation: spin 0.8s linear infinite;
    vertical-align: middle; margin-right: 6px;
  }
  @keyframes spin { to { transform: rotate(360deg); } }
  .loading-badge {
    position: absolute; top: 12px; right: 14px;
    background: var(--paper); border: 1px solid var(--hair);
    border-radius: 99px; padding: 4px 10px;
    font-family: 'JetBrains Mono', monospace; font-size: 11px; font-weight: 700;
    color: var(--ink-3);
    z-index: 2; display: none;
  }
  .loading-badge.on { display: inline-flex; align-items: center; }
  .loading-badge.err { background: #fbeae5; border-color: #c84a2c; color: #8b3220; }
  .loading-badge.ok  { background: #eaf1e3; border-color: #5b7a4e; color: #3e5535; }
  .loading-badge .err-dot {
    display: inline-block; width: 12px; height: 12px; line-height: 11px;
    text-align: center; background: #c84a2c; color: #fff; border-radius: 50%;
    font-size: 10px; font-weight: 900; margin-right: 6px;
  }
  .loading-badge .ok-dot {
    display: inline-block; width: 8px; height: 8px;
    background: #5b7a4e; border-radius: 50%; margin-right: 6px;
  }
  .loading-badge.fade { opacity: 0; transition: opacity 0.6s ease 0.4s; }
"""


def _nav_tabs(active):
    items = (
        ("tier", "/", "tier list"),
        ("authored", "/authored", "authored"),
        ("training", "/training", "training"),
        ("races", "/races", "races"),
    )
    out = ['<nav class="nav-tabs">']
    for key, href, label in items:
        cls = ' class="active"' if key == active else ""
        out.append(f'<a{cls} href="{href}">{label}</a>')
    out.append("</nav>")
    return "".join(out)


def chrome_header(active, subtitle, meta_html="", actions_html=""):
    """Sticky chalk-paper header shared across every dashboard page. `active`
    is the nav-tabs key in {'tier','authored','training','races'}; meta_html
    is an optional inline strip that sits between tabs and actions (race
    detail uses it for the race meta dl). actions_html drops into the right-
    side .actions slot."""
    return (
        '<header class="top">'
        f'<div class="brand">racing<small>{subtitle}</small></div>'
        + _nav_tabs(active)
        + meta_html
        + '<div class="spacer"></div>'
        + f'<div class="actions">{actions_html}</div>'
        + "</header>"
    )


# Shared SVG renderer — single source of truth for both pages so the line
# style is identical between the tier-list and the authored-closure viewer.
# Dimensions/pad are passed in by the caller (cards on the two pages have
# different physical sizes, but the geometry pipeline is one function).
RENDER_JS = r"""
function svgFor(c, W, H, PAD) {
  W = W || 118; H = H || 90; PAD = (PAD == null) ? 0.05 : PAD;
  const w = (c.maxX - c.minX) || 1;
  const h = (c.maxY - c.minY) || 1;
  const scale = Math.min(W * (1 - 2*PAD) / w, H * (1 - 2*PAD) / h);
  const ox = (W - scale * w) / 2;
  const oy = (H - scale * h) / 2;
  const project = (x, y) => {
    const sx = ox + (x - c.minX) * scale;
    const sy = H - oy - (y - c.minY) * scale; // flip Y so +Y is up
    return [sx.toFixed(2), sy.toFixed(2)];
  };

  let body = '';
  // Kerbs first (under everything) — translucent red quads to suggest the rumble strip.
  if (c.kerbs && c.kerbs.length) {
    for (const q of c.kerbs) {
      const [a0, a1] = project(q[0], q[1]);
      const [b0, b1] = project(q[2], q[3]);
      const [d0, d1] = project(q[4], q[5]);
      const [e0, e1] = project(q[6], q[7]);
      body += `<polygon points="${a0},${a1} ${b0},${b1} ${d0},${d1} ${e0},${e1}" fill="#c84a2c" fill-opacity="0.30"/>`;
    }
  }
  if (c.anchors && c.anchors.length > 1) {
    let d = '';
    for (let i = 0; i < c.anchors.length; i++) {
      const [sx, sy] = project(c.anchors[i][0], c.anchors[i][1]);
      d += (i === 0 ? `M${sx},${sy}` : ` L${sx},${sy}`);
    }
    d += ' Z';
    body += `<path d="${d}" fill="none" stroke="#1f1c17" stroke-width="1.6" stroke-linejoin="round" stroke-linecap="round"/>`;

    // Sector boundaries + start/finish gate + direction arrow. Each boundary
    // is drawn as a short perpendicular tick crossing the centerline at the
    // sector-boundary anchor; sector 0 (the start/finish line) is doubled in
    // length and weight, then a tiny chevron arrow indicates lap-traversal
    // direction (anchors are stored in forward order; arrow points from
    // lapStart anchor toward the next one).
    const lapStartIdx = (c.lapStartIdx == null) ? 0 : c.lapStartIdx;
    const sectorIdx = c.sectorIdx || [];
    const macroCount = c.sectorMacro || 3;
    const microCount = sectorIdx.length;
    const sectorColors = ['#b88840', '#5b7a4e', '#4a6c8a']; // 3 macros: ochre/moss/slate (chalk palette)

    if (sectorIdx.length > 0) {
      // F1-style 3-sector display. Macro gates (S1/S2/S3 boundaries) are
      // emphasised; the 6 micro boundaries between them stay as faint
      // unlabeled tick marks so cheat-prevention gating is still visible.
      const microsPerMacro = microCount > 0 ? Math.max(1, Math.floor(microCount / macroCount)) : 1;
      for (let s = 0; s < sectorIdx.length; s++) {
        const ai = sectorIdx[s];
        const a = c.anchors[ai];
        const ap = c.anchors[(ai - 1 + c.anchors.length) % c.anchors.length];
        const an = c.anchors[(ai + 1) % c.anchors.length];
        const [px, py] = project(a[0], a[1]);
        const [npx, npy] = project(an[0], an[1]);
        const [ppx, ppy] = project(ap[0], ap[1]);
        let tx = parseFloat(npx) - parseFloat(ppx);
        let ty = parseFloat(npy) - parseFloat(ppy);
        const tlen = Math.hypot(tx, ty) || 1;
        tx /= tlen; ty /= tlen;
        const nx = -ty, ny = tx;
        const isStart = s === 0;
        const isMacro = (s % microsPerMacro) === 0;
        const macroNum = Math.floor(s / microsPerMacro) + 1;
        const halfLen = isStart ? 8.0 : (isMacro ? 5.5 : 2.5);
        const x1 = parseFloat(px) + nx * halfLen;
        const y1 = parseFloat(py) + ny * halfLen;
        const x2 = parseFloat(px) - nx * halfLen;
        const y2 = parseFloat(py) - ny * halfLen;
        const color = isStart ? '#1f1c17'
                      : (isMacro ? sectorColors[(macroNum - 1) % sectorColors.length]
                                 : 'rgba(131,124,114,0.55)');
        const sw = isStart ? 2.6 : (isMacro ? 1.6 : 0.8);
        body += `<line x1="${x1.toFixed(2)}" y1="${y1.toFixed(2)}" x2="${x2.toFixed(2)}" y2="${y2.toFixed(2)}" stroke="${color}" stroke-width="${sw}" stroke-linecap="round"/>`;
      }
      // Start-gate dot (white) + direction arrow (a small triangle pointing
      // along tangent at the lapStart anchor).
      const sa = c.anchors[lapStartIdx];
      const sb = c.anchors[(lapStartIdx + 1) % c.anchors.length];
      const [sax, say] = project(sa[0], sa[1]);
      const [sbx, sby] = project(sb[0], sb[1]);
      let dx = parseFloat(sbx) - parseFloat(sax);
      let dy = parseFloat(sby) - parseFloat(say);
      const dlen = Math.hypot(dx, dy) || 1;
      dx /= dlen; dy /= dlen;
      // Triangle tip along tangent, base perpendicular, length 7px.
      const arrowLen = 7.0;
      const arrowHalf = 3.2;
      const tipX = parseFloat(sax) + dx * arrowLen;
      const tipY = parseFloat(say) + dy * arrowLen;
      const baseLX = parseFloat(sax) + (-dy) * arrowHalf;
      const baseLY = parseFloat(say) + (dx) * arrowHalf;
      const baseRX = parseFloat(sax) - (-dy) * arrowHalf;
      const baseRY = parseFloat(say) - (dx) * arrowHalf;
      body += `<polygon points="${tipX.toFixed(2)},${tipY.toFixed(2)} ${baseLX.toFixed(2)},${baseLY.toFixed(2)} ${baseRX.toFixed(2)},${baseRY.toFixed(2)}" fill="#1f1c17" stroke="#efece4" stroke-width="0.4"/>`;
      body += `<circle cx="${sax}" cy="${say}" r="2.2" fill="#1f1c17" stroke="#efece4" stroke-width="0.4"/>`;
    } else {
      // Fallback when no sectorization (e.g. degenerate loop) — keep the
      // legacy blue start-dot so old behaviour is recognisable.
      const [s0x, s0y] = project(c.anchors[0][0], c.anchors[0][1]);
      body += `<circle cx="${s0x}" cy="${s0y}" r="2.2" fill="#1f1c17"/>`;
    }
  } else if (c.cells && c.cells.length) {
    const cell = Math.min(W / (w + 2), H / (h + 2));
    const ox2 = (W - cell * (w + 2)) / 2;
    const oy2 = (H - cell * (h + 2)) / 2;
    for (const [x, y] of c.cells) {
      const sx = ox2 + (x - c.minX + 1) * cell;
      const sy = H - oy2 - (y - c.minY + 1) * cell - cell;
      body += `<rect x="${sx.toFixed(1)}" y="${sy.toFixed(1)}" width="${cell.toFixed(1)}" height="${cell.toFixed(1)}" fill="#c84a2c"/>`;
    }
  }
  if (c.walls && c.walls.length) {
    let wd = '';
    for (const w of c.walls) {
      const [ax, ay] = project(w[0], w[1]);
      const [bx, by] = project(w[2], w[3]);
      wd += `M${ax},${ay} L${bx},${by} `;
    }
    body += `<path d="${wd}" stroke="#4a4540" stroke-width="0.8" fill="none" stroke-linecap="round"/>`;
  }
  // Closure-piece overlay — render after the body anchors polyline so each
  // generator-bridged arc sits on top in a contrasting cyan. One <path> per
  // closure shape's per-piece spine; pieces don't share endpoints so each
  // segment stands on its own.
  if (c.closureSegments && c.closureSegments.length) {
    let cd = '';
    for (const seg of c.closureSegments) {
      for (let i = 0; i < seg.length; i++) {
        const [sx, sy] = project(seg[i][0], seg[i][1]);
        cd += (i === 0 ? `M${sx},${sy}` : ` L${sx},${sy}`);
      }
      cd += ' ';
    }
    body += `<path d="${cd}" fill="none" stroke="#b88840" stroke-width="2.2" stroke-linejoin="round" stroke-linecap="round"/>`;
  }
  return `<svg viewBox="0 0 ${W} ${H}">${body}</svg>`;
}
"""


_AUTHORED_STYLES = r"""
  main { padding: 18px 22px 60px; }
  .toolbar {
    display: flex; align-items: center; gap: 10px;
    margin-bottom: 14px;
  }
  .toolbar .stat { font-family: 'JetBrains Mono', monospace; font-size: 12px; font-weight: 700; color: var(--ink-3); }
  .auth-grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(200px, 1fr));
    gap: 14px;
  }
  .auth-grid .card {
    padding: 10px; display: flex; flex-direction: column; gap: 8px;
  }
  .auth-grid .card svg {
    display: block; width: 100%; height: 138px;
    background: var(--paper); border-radius: var(--r-sm);
    border: 1px solid var(--hair);
  }
  .auth-grid .id {
    font-family: 'JetBrains Mono', monospace;
    font-size: 11px; font-weight: 700; color: var(--ink-2);
    word-break: break-all; letter-spacing: -0.005em;
  }
  .auth-grid .card-meta {
    font-family: 'JetBrains Mono', monospace;
    font-size: 11px; font-weight: 600; color: var(--ink-3); line-height: 1.5;
  }
  .auth-grid .card-meta b { color: var(--ink); font-weight: 800; }
  .pills { display: flex; flex-wrap: wrap; gap: 4px; }
  .pill-mini {
    background: var(--paper-2); border: 1px solid var(--hair);
    padding: 2px 8px; border-radius: 10px;
    font-family: 'JetBrains Mono', monospace; font-size: 10px; font-weight: 700;
    color: var(--ink-3); letter-spacing: 0.04em;
  }
  .empty {
    padding: 60px 0; text-align: center;
    color: var(--ink-3); font-family: 'JetBrains Mono', monospace; font-size: 13px;
  }
  .empty code {
    background: var(--paper-2); padding: 2px 6px; border-radius: 3px;
    color: var(--ink); border: 1px solid var(--hair);
  }
  footer.chalk-foot {
    position: fixed; bottom: 0; left: 0; right: 0;
    background: var(--paper); border-top: 1px solid var(--hair);
    padding: 8px 22px;
    font-family: 'JetBrains Mono', monospace; font-size: 11px; font-weight: 700;
    color: var(--ink-3); letter-spacing: 0.04em;
  }
  footer.chalk-foot .sw {
    display: inline-block; width: 10px; height: 10px; border-radius: 2px;
    vertical-align: -1px; margin-right: 3px;
  }
"""

_AUTHORED_BODY = r"""
<main>
  <div class="toolbar">
    <input type="search" id="filter" placeholder="filter by id / seed…">
    <button class="btn" id="reload">reload</button>
    <span class="spacer"></span>
    <span class="stat" id="stat">loading…</span>
  </div>
  <div class="auth-grid" id="grid"></div>
  <div class="empty" id="empty" style="display:none">
    no circuits in <code>circuits/stage_authored_closure/</code>.<br>
    run the unity menu <b>build → authored closure circuit library (100)</b> to populate.
  </div>
</main>
<footer class="chalk-foot">
  source: <code style="background:var(--paper-2);padding:1px 5px;border-radius:3px;border:1px solid var(--hair);color:var(--ink-2)">circuits/stage_authored_closure/</code>
  &nbsp;·&nbsp;<span class="sw" style="background:#1f1c17"></span>authored body
  &nbsp;·&nbsp;<span class="sw" style="background:#b88840"></span>closure transition (generator-bridged, no walls)
  &nbsp;·&nbsp;<span class="sw" style="background:#c84a2c;opacity:0.6"></span>kerb
</footer>
<script src="/static/render.js"></script>
<script>
function cardEl(c) {
  const el = document.createElement('div');
  el.className = 'card';
  el.dataset.id = c.id;
  el.dataset.seed = c.seed;
  const auth = c.authoredCardCount || 0;
  const closure = c.closureCardCount || 0;
  el.innerHTML = svgFor(c, 180, 138, 0.06) +
    `<div class="id">${c.id}</div>` +
    `<div class="card-meta"><b>${c.totalLength.toFixed(1)}m</b> · ${c.pieceCount} pieces · stage ${c.stageId}</div>` +
    `<div class="pills">` +
      (c.seed ? `<span class="pill-mini">seed ${c.seed}</span>` : '') +
      (auth ? `<span class="pill-mini">authored ×${auth}</span>` : '') +
      (closure ? `<span class="pill-mini">closure ×${closure}</span>` : '') +
    `</div>`;
  return el;
}

function applyFilter(circuits) {
  const q = document.getElementById('filter').value.trim().toLowerCase();
  const grid = document.getElementById('grid');
  grid.innerHTML = '';
  let shown = 0;
  for (const c of circuits) {
    if (q) {
      const hay = `${c.id} ${c.seed}`.toLowerCase();
      if (!hay.includes(q)) continue;
    }
    grid.appendChild(cardEl(c));
    shown++;
  }
  document.getElementById('empty').style.display = (circuits.length === 0) ? 'block' : 'none';
  document.getElementById('stat').textContent =
    `${shown} of ${circuits.length} circuits`;
}

let _circuits = [];
async function load() {
  const r = await fetch('/api/authored');
  const data = await r.json();
  _circuits = data.circuits || [];
  applyFilter(_circuits);
}
document.getElementById('filter').addEventListener('input', () => applyFilter(_circuits));
document.getElementById('reload').onclick = load;
load();
</script>
"""

AUTHORED_HTML = (
    '<!doctype html><html lang="en"><head><meta charset="utf-8">'
    '<title>RACING · authored closure circuits</title>'
    '<meta name="viewport" content="width=device-width, initial-scale=1">'
    + BASE_HEAD
    + '<style>' + BASE_STYLES + _AUTHORED_STYLES + '</style>'
    + '</head><body>'
    + chrome_header('authored', 'authored closure library')
    + _AUTHORED_BODY
    + '</body></html>'
)


_TIER_STYLES = r"""
  main { padding: 18px 22px 70px; }
  .tier-row, .pool-row {
    background: var(--card);
    border: 1px solid var(--hair);
    border-radius: var(--r-lg);
    padding: 10px;
    margin-bottom: 10px;
    min-height: 110px;
    display: flex; align-items: flex-start; gap: 10px;
    box-shadow: var(--shadow);
  }
  .tier-row { padding-left: 64px; position: relative; flex-wrap: wrap; }
  .tier-row .tier-label { position: absolute; left: 10px; top: 50%; transform: translateY(-50%); }
  .pool-row { flex-wrap: wrap; padding-top: 30px; position: relative; }
  .pool-row::before {
    content: "unsorted · drag to a tier →";
    position: absolute; left: 18px; top: 10px;
    color: var(--ink-3); font-size: 11px; font-weight: 700;
    font-family: 'JetBrains Mono', monospace;
    letter-spacing: 0.08em; text-transform: uppercase;
    pointer-events: none;
  }
  .tier-row.over, .pool-row.over { outline: 2px dashed var(--signal); outline-offset: -4px; }

  .tcard {
    width: 138px;
    background: var(--paper-2);
    border: 1px solid var(--hair);
    border-radius: var(--r-sm);
    padding: 6px;
    cursor: grab;
    user-select: none;
    flex-shrink: 0;
    transition: transform 0.12s var(--ease);
  }
  .tcard:hover { transform: translateY(-1px); border-color: var(--hair-2); }
  .tcard.drag { opacity: 0.4; }
  .tcard svg {
    display: block; width: 124px; height: 94px;
    background: var(--paper); border-radius: 3px;
    border: 1px solid var(--hair);
  }
  .tcard .tmeta {
    font-family: 'JetBrains Mono', monospace;
    font-size: 10px; font-weight: 700;
    color: var(--ink-3); margin-top: 5px; line-height: 1.3;
    letter-spacing: -0.005em;
  }
  .tcard .tmeta .id { color: var(--ink-2); }
  .tcard .tmeta .len { color: var(--ink); font-weight: 800; }

  .autodot {
    display: inline-block; width: 8px; height: 8px; border-radius: 50%;
    background: var(--good); opacity: 0; transition: opacity 0.3s;
    box-shadow: 0 0 0 3px #5b7a4e22;
    margin: 0 8px;
  }

  footer.chalk-foot {
    position: fixed; bottom: 0; left: 0; right: 0;
    background: var(--paper); border-top: 1px solid var(--hair);
    padding: 10px 22px;
    font-family: 'JetBrains Mono', monospace; font-size: 11px; font-weight: 700;
    color: var(--ink-3); letter-spacing: 0.04em;
  }

  .toast {
    position: fixed; bottom: 56px; right: 22px;
    background: var(--ink); color: var(--paper);
    padding: 10px 14px; border-radius: var(--r-sm);
    font-family: 'JetBrains Mono', monospace; font-size: 12px; font-weight: 700;
    opacity: 0; transition: opacity 0.2s;
    pointer-events: none;
    box-shadow: 0 8px 24px -8px #00000050;
  }
  .toast.show { opacity: 1; }
"""

_TIER_BODY = r"""
<main>
  <div class="pool-row" id="pool" data-tier="UNSORTED"></div>
  <div class="tier-row" data-tier="S"><div class="tier-label tier-label--S">S</div></div>
  <div class="tier-row" data-tier="A"><div class="tier-label tier-label--A">A</div></div>
  <div class="tier-row" data-tier="B"><div class="tier-label tier-label--B">B</div></div>
  <div class="tier-row" data-tier="C"><div class="tier-label tier-label--C">C</div></div>
  <div class="tier-row" data-tier="D"><div class="tier-label tier-label--D">D</div></div>
  <div class="tier-row" data-tier="F"><div class="tier-label tier-label--F">F</div></div>
</main>
<footer class="chalk-foot">S = easiest · F = hardest. Trainer reads tier order S→A→B→C→D→F, advancing per-circuit when reward threshold is met.</footer>
<div class="toast" id="toast"></div>
<script src="/static/render.js"></script>
<script>
const TIERS = ['S','A','B','C','D','F'];

function cardEl(c) {
  const el = document.createElement('div');
  el.className = 'tcard';
  el.draggable = true;
  el.dataset.id = c.id;
  el.dataset.stage = c.stageId;
  el.innerHTML = svgFor(c, 124, 94, 0.05) +
    `<div class="tmeta"><span class="id">${c.id}</span> · stage ${c.stageId}<br>` +
    `<span class="len">${c.totalLength.toFixed(1)}m · ${c.pieceCount}p</span></div>`;
  el.addEventListener('dragstart', e => {
    e.dataTransfer.setData('text/plain', c.id);
    el.classList.add('drag');
  });
  el.addEventListener('dragend', () => el.classList.remove('drag'));
  return el;
}

function setupDrop(zone) {
  zone.addEventListener('dragover', e => { e.preventDefault(); zone.classList.add('over'); });
  zone.addEventListener('dragleave', () => zone.classList.remove('over'));
  zone.addEventListener('drop', e => {
    e.preventDefault();
    zone.classList.remove('over');
    const id = e.dataTransfer.getData('text/plain');
    const card = document.querySelector(`.tcard[data-id="${id}"]`);
    if (card) {
      let target = null;
      for (const sib of zone.querySelectorAll('.tcard')) {
        const r = sib.getBoundingClientRect();
        if (e.clientX < r.left + r.width/2 && e.clientY < r.bottom) { target = sib; break; }
      }
      if (target) zone.insertBefore(card, target);
      else zone.appendChild(card);
      autosave();
    }
  });
}

let autosaveTimer = null;
function autosave() {
  if (autosaveTimer) clearTimeout(autosaveTimer);
  autosaveTimer = setTimeout(async () => {
    try {
      const pl = currentPlaylist();
      await fetch('/api/playlist', {method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify(pl)});
      const dot = document.getElementById('autodot');
      if (dot) { dot.style.opacity = 1; setTimeout(() => dot.style.opacity = 0, 600); }
    } catch (e) { /* silent */ }
  }, 250);
}

function currentPlaylist() {
  const out = {};
  for (const t of TIERS) {
    out[t] = [...document.querySelectorAll(`.tier-row[data-tier="${t}"] .tcard`)].map(c => c.dataset.id);
  }
  out.UNSORTED = [...document.querySelectorAll('.pool-row .tcard')].map(c => c.dataset.id);
  return out;
}

function applyPlaylist(circuits, playlist) {
  const byId = Object.fromEntries(circuits.map(c => [c.id, c]));
  const placedIds = new Set();
  for (const t of TIERS) {
    const zone = document.querySelector(`.tier-row[data-tier="${t}"]`);
    for (const id of (playlist[t] || [])) {
      if (byId[id]) { zone.appendChild(cardEl(byId[id])); placedIds.add(id); }
    }
  }
  const pool = document.getElementById('pool');
  for (const c of circuits) { if (!placedIds.has(c.id)) pool.appendChild(cardEl(c)); }
}

async function load() {
  const r = await fetch('/api/state');
  const data = await r.json();
  document.querySelectorAll('.tcard').forEach(c => c.remove());
  applyPlaylist(data.circuits, data.playlist || {});
  document.getElementById('stat').textContent =
    `${data.circuits.length} circuits · ${new Set(data.circuits.map(c=>c.stageId)).size} stages`;
}

async function save() {
  const pl = currentPlaylist();
  const r = await fetch('/api/playlist', {method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify(pl)});
  const j = await r.json();
  const t = document.getElementById('toast');
  t.textContent = j.ok ? `saved ${j.totalAssigned} ranked circuits → playlist.json` : `save failed: ${j.error}`;
  t.classList.add('show');
  setTimeout(() => t.classList.remove('show'), 2200);
}

setupDrop(document.getElementById('pool'));
document.querySelectorAll('.tier-row').forEach(setupDrop);
document.getElementById('reload').onclick = load;
document.getElementById('save').onclick = save;
load();
</script>
"""

_TIER_ACTIONS = (
    '<span class="mono" id="stat" style="font-size:12px;color:var(--ink-3);font-weight:700">loading…</span>'
    '<span class="autodot" id="autodot" title="autosaved"></span>'
    '<button class="btn" id="reload">reload</button>'
    '<button class="btn primary" id="save">save playlist</button>'
)

HTML = (
    '<!doctype html><html lang="en"><head><meta charset="utf-8">'
    '<title>RACING · circuit tier-list</title>'
    '<meta name="viewport" content="width=device-width, initial-scale=1">'
    + BASE_HEAD
    + '<style>' + BASE_STYLES + _TIER_STYLES + '</style>'
    + '</head><body>'
    + chrome_header('tier', 'circuit tier-list', actions_html=_TIER_ACTIONS)
    + _TIER_BODY
    + '</body></html>'
)


# ----------------------------------------------------------------------------
# Training analytics — read append-only JSONL from results/_telemetry/.
# Each Unity training process writes its own env_<pid>_<ts>.jsonl. Events:
#   {"event":"session_start","pid":...,"ts":...}
#   {"event":"circuit_change","ts":...,"circuit":...,"pieces":...,"length":...}
#   {"event":"episode_end","ts":...,"car":...,"circuit":...,"reason":...,
#    "x":...,"z":...,"lap_frac":...,"laps":...,"steps":...,"elapsed":...,
#    "reward":...,"wall_hits":...}
# ----------------------------------------------------------------------------


def _telemetry_files():
    if not TELEMETRY_DIR.exists():
        return []
    return sorted(TELEMETRY_DIR.glob("env_*.jsonl"))


# Append-only telemetry cache. Each file is parsed once; on subsequent calls
# we stat (mtime, size) — if size grew we only read the new tail; if unchanged
# we reuse the parsed events. The dashboard polls every 5s so this turns
# O(N_files * N_lines * fan-out) into O(new_lines_since_last_poll).
_TELEMETRY_CACHE = {
    "files": {},        # path_str -> {"mtime", "size", "events": [...]}
    "all_events": None, # flat merged list (rebuilt only when something changed)
    "by_circuit": None, # {circuit_id: [events]} for fast per-circuit endpoints
}


_LAST_PRUNE = {"ts": 0.0}


def _cutoff_iso(seconds):
    from datetime import datetime, timedelta, timezone
    return (datetime.now(timezone.utc) - timedelta(seconds=seconds)).isoformat()


def _prune_jsonl_inplace(path, cutoff_iso):
    """Drop lines older than cutoff_iso from a single env_*.jsonl. Files are
    append-only and roughly time-ordered, so we find the first line with
    ts >= cutoff and keep everything from there on (preserves trailing no-ts
    samples like step_sample/wall_hit that belong to in-progress episodes).
    Returns True if the file was rewritten."""
    try:
        with open(path, "rb") as f:
            data = f.read()
    except Exception:
        return False
    if not data:
        return False
    lines = data.split(b"\n")
    keep_from = None
    for i, ln in enumerate(lines):
        s = ln.strip()
        if not s:
            continue
        try:
            rec = json.loads(s)
        except Exception:
            continue
        ts = rec.get("ts")
        if ts and ts >= cutoff_iso:
            keep_from = i
            break
    if keep_from == 0:
        return False
    if keep_from is None:
        # Entire file is older than retention — truncate.
        try:
            with open(path, "wb") as f:
                f.write(b"")
            return True
        except Exception:
            return False
    new_data = b"\n".join(lines[keep_from:])
    tmp = path.with_suffix(path.suffix + ".tmp")
    try:
        with open(tmp, "wb") as f:
            f.write(new_data)
        os.replace(tmp, path)
        return True
    except Exception:
        try:
            tmp.unlink(missing_ok=True)
        except Exception:
            pass
        return False


def _maybe_prune_telemetry(retention_seconds=TELEMETRY_RETENTION_SECONDS,
                           min_interval=TELEMETRY_PRUNE_MIN_INTERVAL,
                           force=False):
    """Rewrite all env_*.jsonl to keep only the last `retention_seconds` worth
    of events. Throttled to once per `min_interval` seconds unless forced."""
    import time
    now = time.time()
    if not force and now - _LAST_PRUNE["ts"] < min_interval:
        return False
    _LAST_PRUNE["ts"] = now
    cutoff = _cutoff_iso(retention_seconds)
    pruned_any = False
    for jf in _telemetry_files():
        if _prune_jsonl_inplace(jf, cutoff):
            pruned_any = True
    return pruned_any


def clear_telemetry(keep_seconds=None):
    """Manual wipe — either truncate every env_*.jsonl (keep_seconds is None
    or 0) or run an immediate prune at the given window. Always clears the
    in-memory cache so the next request rebuilds from disk."""
    if keep_seconds and keep_seconds > 0:
        _maybe_prune_telemetry(retention_seconds=keep_seconds,
                               min_interval=0, force=True)
    else:
        for jf in _telemetry_files():
            try:
                with open(jf, "wb") as f:
                    f.write(b"")
            except Exception:
                pass
    _TELEMETRY_CACHE["files"].clear()
    _TELEMETRY_CACHE["all_events"] = None
    _TELEMETRY_CACHE["by_circuit"] = None


def load_telemetry_events(limit_per_file=50000):
    """Merge all env_*.jsonl into a single list. Cached + incremental:
    unchanged files reuse parsed events, grown files only parse the tail.
    `limit_per_file` keeps memory bounded across long unattended runs."""
    _maybe_prune_telemetry()
    cache = _TELEMETRY_CACHE
    seen_keys = set()
    changed = False
    for jf in _telemetry_files():
        key = str(jf)
        seen_keys.add(key)
        try:
            st = jf.stat()
        except (OSError, FileNotFoundError):
            continue
        entry = cache["files"].get(key)
        if entry and entry["mtime"] == st.st_mtime and entry["size"] == st.st_size:
            continue  # unchanged — reuse parsed events
        env_id = jf.stem
        events = entry["events"] if (entry and st.st_size >= entry["size"]) else []
        start_byte = entry["size"] if (entry and st.st_size >= entry["size"]) else 0
        try:
            with open(jf, "rb") as f:
                if start_byte:
                    f.seek(start_byte)
                tail_bytes = f.read()
        except Exception:
            continue
        try:
            tail_text = tail_bytes.decode("utf-8", errors="replace")
        except Exception:
            tail_text = ""
        for ln in tail_text.splitlines():
            ln = ln.strip()
            if not ln:
                continue
            try:
                rec = json.loads(ln)
            except Exception:
                continue
            rec["env"] = env_id
            events.append(rec)
        if len(events) > limit_per_file:
            events = events[-limit_per_file:]
        cache["files"][key] = {
            "mtime": st.st_mtime,
            "size": st.st_size,
            "events": events,
        }
        changed = True
    # Drop entries for files that disappeared (manual cleanup, etc.)
    for k in list(cache["files"].keys()):
        if k not in seen_keys:
            cache["files"].pop(k, None)
            changed = True
    if changed or cache["all_events"] is None:
        merged = []
        for k in sorted(cache["files"].keys()):
            merged.extend(cache["files"][k]["events"])
        # Defense in depth: hard cap on merged-event count even if pruning
        # hasn't caught up yet. Tail-keep so the dashboard always shows the
        # most recent slice.
        if len(merged) > TELEMETRY_MAX_MERGED:
            merged = merged[-TELEMETRY_MAX_MERGED:]
        cache["all_events"] = merged
        by_circuit = {}
        for e in merged:
            cid = e.get("circuit")
            if cid:
                by_circuit.setdefault(cid, []).append(e)
        cache["by_circuit"] = by_circuit
    return cache["all_events"]


def telemetry_events_for_circuit(circuit):
    """Pre-bucketed slice for per-circuit endpoints (lap log, heatmap).
    Avoids scanning the full N-file event stream on every click."""
    load_telemetry_events()
    return _TELEMETRY_CACHE["by_circuit"].get(circuit, [])


def telemetry_file_mtimes():
    """env_id -> last-mtime (epoch seconds), for live-env staleness gating."""
    out = {}
    for jf in _telemetry_files():
        try:
            out[jf.stem] = jf.stat().st_mtime
        except OSError:
            pass
    return out


def filter_events_by_window(events, window_seconds):
    """Keep only events that occurred within the last N seconds.
    step_sample / wall_hit have no `ts` — they're attributed to the NEXT
    episode_end on the same (env, car) channel and dropped if that
    episode_end falls outside the window. Trailing samples without a
    terminating episode_end are kept (they belong to an in-progress run)."""
    if not window_seconds or window_seconds <= 0:
        return events
    from datetime import datetime, timedelta, timezone
    cutoff_iso = (datetime.now(timezone.utc)
                  - timedelta(seconds=window_seconds)).isoformat()
    out = []
    pending = {}  # (env, car) -> [events]
    for e in events:
        ev = e.get("event")
        ts = e.get("ts")
        if ev in ("episode_end", "circuit_change", "session_start"):
            key = (e.get("env", ""), e.get("car", 0))
            if ts and ts >= cutoff_iso:
                out.extend(pending.pop(key, []))
                out.append(e)
            else:
                pending.pop(key, None)
        else:
            key = (e.get("env", ""), e.get("car", 0))
            pending.setdefault(key, []).append(e)
    # Trailing in-progress events (no terminating episode_end yet) are
    # almost always part of the current live episode — keep them.
    for buf in pending.values():
        out.extend(buf)
    return out


def _load_circuit_records():
    """Read tools/circuit_records/records.json (or return empty bootstrap)."""
    try:
        if not CIRCUIT_RECORDS_FILE.exists():
            return {"version": 1, "circuits": {}}
        with CIRCUIT_RECORDS_FILE.open("r", encoding="utf-8") as f:
            data = json.load(f)
        if not isinstance(data, dict):
            return {"version": 1, "circuits": {}}
        data.setdefault("version", 1)
        data.setdefault("circuits", {})
        if not isinstance(data["circuits"], dict):
            data["circuits"] = {}
        return data
    except (json.JSONDecodeError, OSError):
        return {"version": 1, "circuits": {}}


def _persist_circuit_records(agg, run_id="tierlist"):
    """Merge-min write per-circuit best-lap into CIRCUIT_RECORDS_FILE.

    Only flying-lap best laps from the aggregator feed this store
    (best_lap_steps is set ONLY on lap >= 2 in _aggregate_events). One
    atomic write per call via tmp+replace; concurrent C# writes are safe
    because each side reads-then-writes the whole map under min-semantics.
    """
    if not agg or not agg.get("circuits"):
        return
    try:
        with _CIRCUIT_RECORDS_LOCK:
            data = _load_circuit_records()
            circuits = data["circuits"]
            now_iso = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
            dirty = False
            for row in agg["circuits"]:
                cid = row.get("circuit") or ""
                if not cid or cid == "<unknown>":
                    continue
                best_sec = row.get("bestLapSeconds")
                if best_sec is None or best_sec <= 0:
                    continue
                prev = circuits.get(cid, {})
                prev_best = prev.get("best_lap_seconds")
                if prev_best is not None and prev_best <= best_sec:
                    continue
                circuits[cid] = {
                    "best_lap_seconds": float(best_sec),
                    "run_id": run_id,
                    "timestamp_utc": now_iso,
                    "writer": "python",
                    "set_count": int((prev.get("set_count") or 0)) + 1,
                }
                dirty = True
            if not dirty:
                return
            CIRCUIT_RECORDS_DIR.mkdir(parents=True, exist_ok=True)
            tmp = CIRCUIT_RECORDS_FILE.with_suffix(".json.tmp")
            with tmp.open("w", encoding="utf-8") as f:
                json.dump(data, f, indent=2, sort_keys=True)
            os.replace(tmp, CIRCUIT_RECORDS_FILE)
    except OSError:
        # Best-effort: never let persistence failure kill the dashboard.
        pass


def aggregate_training_stats(events):
    """Roll up per-circuit + global counters from raw events."""
    by_circuit = {}
    by_reason = {}
    total = 0
    laps_completed = 0
    rewards_sum = 0.0
    rewards_n = 0
    steps_sum = 0
    elapsed_sum = 0.0
    wall_hits_sum = 0
    lap_frac_sum = 0.0
    # Dedupe lap_complete by (env, car, lap, ts). A duplicate event reaching
    # the loop a second time would otherwise inflate lapCount and corrupt
    # mean lap stats. Keying on ts (not just lap_num) keeps legitimate
    # next-episode lap=1 events alive even if their episode_end is delayed.
    seen_lap_completes = {}
    for e in events:
        ev = e.get("event")
        if ev == "lap_complete":
            # v18b multi-lap per-lap record. Used only for circuit-level
            # best/mean-lap stats; episode totals come from episode_end.
            circuit = e.get("circuit", "") or "<unknown>"
            c = by_circuit.setdefault(circuit, {
                "episodes": 0, "laps": 0, "lap_frac_sum": 0.0,
                "reward_sum": 0.0, "wall_hits_sum": 0, "by_reason": {},
                "best_lap_steps": None, "lap_steps_sum": 0, "lap_steps_n": 0,
                "best_lap_reward": None,
            })
            try:
                steps = int(e.get("steps", 0) or 0)
                lap_num = int(e.get("lap", 0) or 0)
            except (TypeError, ValueError):
                continue
            dedup_key = (e.get("env", ""), e.get("car", 0))
            dedup_token = (lap_num, e.get("ts", ""))
            if seen_lap_completes.get(dedup_key) == dedup_token:
                continue
            seen_lap_completes[dedup_key] = dedup_token
            kind = e.get("kind", "") or "flying"
            is_flying = bool(e.get("flying", kind == "flying"))
            # Best-lap leaderboard tracks flying laps only — cold laps are
            # physics-bound (acceleration phase), not a skill signal.
            if is_flying and steps > 0:
                rew = float(e.get("reward", 0) or 0)
                c["lap_steps_sum"] += steps
                c["lap_steps_n"] += 1
                if c["best_lap_steps"] is None or steps < c["best_lap_steps"]:
                    c["best_lap_steps"] = steps
                    c["best_lap_reward"] = rew
            continue
        if ev != "episode_end":
            continue
        total += 1
        circuit = e.get("circuit", "") or "<unknown>"
        reason = e.get("reason", "") or "<unknown>"
        c = by_circuit.setdefault(circuit, {
            "episodes": 0, "laps": 0, "lap_frac_sum": 0.0,
            "reward_sum": 0.0, "wall_hits_sum": 0, "by_reason": {},
            "best_lap_steps": None, "lap_steps_sum": 0, "lap_steps_n": 0,
            "best_lap_reward": None,
        })
        c["episodes"] += 1
        c["laps"] += int(e.get("laps", 0) or 0)
        c["lap_frac_sum"] += float(e.get("lap_frac", 0) or 0)
        c["reward_sum"] += float(e.get("reward", 0) or 0)
        c["wall_hits_sum"] += int(e.get("wall_hits", 0) or 0)
        c["by_reason"][reason] = c["by_reason"].get(reason, 0) + 1
        # v18b: best/mean lap stats come ONLY from lap_complete events.
        # Legacy episode_end fallback removed — episode `steps` in multi-
        # lap mode is the episode total, not a lap time.
        by_reason[reason] = by_reason.get(reason, 0) + 1
        laps_completed += int(e.get("laps", 0) or 0)
        rewards_sum += float(e.get("reward", 0) or 0)
        rewards_n += 1
        steps_sum += int(e.get("steps", 0) or 0)
        elapsed_sum += float(e.get("elapsed", 0) or 0)
        wall_hits_sum += int(e.get("wall_hits", 0) or 0)
        lap_frac_sum += float(e.get("lap_frac", 0) or 0)
        # Lap numbering resets to 1 at each new episode for the same (env,
        # car), so reset the per-(env, car) dedupe watermark on episode_end.
        seen_lap_completes.pop((e.get("env", ""), e.get("car", 0)), None)
    circuits_out = []
    # Decision sim ticks per second — telemetry "elapsed" is computed in C# as
    # steps/50 (sim runs at 50Hz). Use that to convert step counts to seconds
    # for human-readable lap times.
    SIM_HZ = 50.0
    for cid, c in by_circuit.items():
        n = max(1, c["episodes"])
        ln = max(1, c["lap_steps_n"])
        best_steps = c["best_lap_steps"]
        mean_steps = (c["lap_steps_sum"] / ln) if c["lap_steps_n"] > 0 else None
        circuits_out.append({
            "circuit": cid,
            "episodes": c["episodes"],
            "laps": c["laps"],
            "meanLapFrac": c["lap_frac_sum"] / n,
            "meanReward": c["reward_sum"] / n,
            "meanWallHits": c["wall_hits_sum"] / n,
            "byReason": c["by_reason"],
            "bestLapSteps": best_steps,
            "bestLapSeconds": (best_steps / SIM_HZ) if best_steps else None,
            "bestLapReward": c["best_lap_reward"],
            "meanLapSteps": mean_steps,
            "meanLapSeconds": (mean_steps / SIM_HZ) if mean_steps else None,
            "lapCount": c["lap_steps_n"],
        })
    circuits_out.sort(key=lambda r: r["episodes"], reverse=True)
    n = max(1, total)
    agg = {
        "totalEpisodes": total,
        "lapsCompleted": laps_completed,
        "meanReward": rewards_sum / max(1, rewards_n),
        "meanLapFrac": lap_frac_sum / n,
        "meanWallHits": wall_hits_sum / n,
        "meanSteps": steps_sum / n,
        "meanElapsed": elapsed_sum / n,
        "byReason": by_reason,
        "circuits": circuits_out,
    }
    # Permanent per-circuit fastest-lap persistence. Best-effort
    # merge-min write to tools/circuit_records/records.json. No-op when the
    # aggregator has no new bests to record. Failure here never poisons the
    # dashboard response.
    _persist_circuit_records(agg)
    return agg


def latest_circuit_per_env(events, max_age_seconds=120):
    """For each env_pid, return the latest circuit_change so the dashboard
    can show what each headless trainer is currently driving on. Stale envs
    (telemetry file untouched for > max_age_seconds) are filtered out — past
    runs leave behind env_*.jsonl files that would otherwise pollute the
    'live envs' panel forever."""
    import time
    out = {}
    for e in events:
        if e.get("event") != "circuit_change":
            continue
        env = e.get("env", "")
        prev = out.get(env)
        if prev is None or e.get("ts", "") >= prev.get("ts", ""):
            out[env] = e
    mtimes = telemetry_file_mtimes()
    now = time.time()
    return [
        e for env, e in out.items()
        if (now - mtimes.get(env, 0.0)) <= max_age_seconds
    ]


def laps_for_circuit(events, circuit, limit=200):
    """Return all completed-lap (Success) episodes for one circuit, ordered by
    timestamp ascending so the dashboard can render a chronological log + a
    "best so far" cumulative-min curve. `limit` caps the response size.

    Each row also carries 9-element `splits` (cumulative seconds at each
    sector boundary, S1..S8 entry + lap-end at index 8) and `durations` (time
    spent in each sector S0..S8).

    Walks ALL events (not the pre-filtered per-circuit slice) because the
    EpisodeRunner.PublishEnd order-bug (fixed in C# but lingering in old
    telemetry) attributes episode_end records to the NEXT circuit. We
    re-attribute the episode_end to whatever circuit the buffered sectors
    were tagged with — that's the circuit the lap actually ran on. Bucket
    by (env, car) since carIdHash collides across multi-env training."""
    rows = []
    # (env, car) -> {lap: [(sector, t_lap, circuit), ...]}.
    sector_buf = {}
    # Tracks the highest lap_complete number emitted per (env, car) so the
    # legacy episode_end branch below doesn't double-count its final lap.
    last_emitted_lap = {}
    # (env, car) -> (lap_num, ts) of last emitted lap_complete, used to
    # discard exact duplicate events (the second pass would otherwise emit
    # a sector-blank ghost row because cell[lap] was popped on the first).
    dedupe_seen = {}
    for e in events:
        ev = e.get("event")
        car = e.get("car", 0)
        env = e.get("env", "")
        key = (env, car)
        if ev == "micro_sector":
            try:
                lap = int(e.get("lap", 0) or 0)
                sec = int(e.get("sector", 0) or 0)
                t = float(e.get("t", 0) or 0)
            except (TypeError, ValueError):
                continue
            cell = sector_buf.setdefault(key, {})
            cell.setdefault(lap, []).append((sec, t, e.get("circuit", "")))
            continue
        if ev == "lap_complete":
            # v18b multi-lap live telemetry. One row per lap-cross.
            if (e.get("circuit", "") or "") != circuit:
                continue
            try:
                lap_num = int(e.get("lap", 0) or 0)
                steps = int(e.get("steps", 0) or 0)
                seconds = float(e.get("seconds", 0) or 0)
            except (TypeError, ValueError):
                continue
            # Dedupe: same (env, car, lap_num, ts) reaching the loop twice
            # would otherwise emit a sector-blank ghost row, because cell[lap]
            # was popped on the first pass. Key on ts so legitimate new
            # episodes (which restart lap_num=1) aren't suppressed if their
            # upstream episode_end happens to be late. Persisted in `_dedupe`,
            # not `last_emitted_lap`, because the latter still drives the
            # episode_end watermark logic below.
            ts = e.get("ts", "")
            dedup_token = (lap_num, ts)
            if dedupe_seen.get(key) == dedup_token:
                continue
            dedupe_seen[key] = dedup_token
            kind = e.get("kind", "") or ("flying" if lap_num >= 2 else "cold")
            is_flying = bool(e.get("flying", kind == "flying"))
            cell = sector_buf.get(key, {})
            emissions = sorted(cell.get(lap_num, []), key=lambda r: r[0])
            splits = [None] * 9
            for s, t, _c in emissions:
                if 1 <= s <= 8:
                    splits[s - 1] = t
            splits[8] = seconds
            max_split = max((v for v in splits[:8] if v is not None), default=0.0)
            valid_splits = max_split <= seconds + 0.2
            if not valid_splits:
                splits = [None] * 9
            durations = [None] * 9
            if valid_splits:
                prev = 0.0
                for i in range(9):
                    cur = splits[i]
                    if cur is None or prev is None:
                        durations[i] = None
                        prev = None
                    else:
                        durations[i] = cur - prev
                        prev = cur
            rows.append({
                "ts": e.get("ts", ""),
                "steps": steps,
                "seconds": seconds,
                "reward": float(e.get("reward", 0) or 0),
                "wall_hits": int(e.get("wall_hits", 0) or 0),
                "health": float(e.get("health", 1) if e.get("health") is not None else 1.0),
                "lap_frac": 1.0,
                "lap": lap_num,
                "kind": kind,
                "flying": is_flying,
                "splits": splits,
                "durations": durations,
            })
            last_emitted_lap[key] = max(last_emitted_lap.get(key, 0), lap_num)
            # Drop only this lap's sector buffer; subsequent laps land in
            # cell[lap_num + 1] and stay valid for the next lap_complete.
            cell.pop(lap_num, None)
            continue
        if ev != "episode_end":
            continue
        reason = e.get("reason", "") or ""
        if not reason.lower().startswith("success"):
            sector_buf.pop(key, None)
            last_emitted_lap.pop(key, None)
            dedupe_seen.pop(key, None)
            continue
        laps_count = int(e.get("laps", 0) or 0)
        if laps_count <= 0:
            sector_buf.pop(key, None)
            last_emitted_lap.pop(key, None)
            dedupe_seen.pop(key, None)
            continue
        # Skip if v18b lap_complete already covered the final lap.
        if last_emitted_lap.get(key, 0) >= laps_count:
            sector_buf.pop(key, None)
            last_emitted_lap.pop(key, None)
            dedupe_seen.pop(key, None)
            continue
        # v18b: legacy episode_end fallback removed. Episode `steps` is
        # the multi-lap episode total (~3500 = 70s at max-steps), NOT a
        # lap time, so it would corrupt the leaderboard if rendered as
        # a row. Skip outright.
        sector_buf.pop(key, None)
        last_emitted_lap.pop(key, None)
        dedupe_seen.pop(key, None)
        continue
    rows.sort(key=lambda r: r["ts"])
    # v18b: keep flying laps only. Cold laps (lap 1 from spawn pose) are
    # physics-bound (acceleration from rest) so they pollute the leaderboard
    # AND the speed-improvement curve. Per-circuit lap log shows ONLY laps
    # that started in motion from a previous lap-cross.
    rows = [r for r in rows if r.get("flying", False)]
    if len(rows) > limit:
        rows = rows[-limit:]
    # Best-so-far running minimum for the speed-improvement curve.
    # v18b: only flying laps (lap >= 2 of an episode) count toward the
    # "best lap time over training" curve. Cold laps (first lap from
    # spawn, accelerating from rest) are inherently slower by physics
    # not skill — including them dilutes the signal. Cold rows still
    # appear in the table; their bestSoFar carries the prior flying best
    # forward unchanged.
    best = None
    for r in rows:
        if r.get("flying", False) and (best is None or r["steps"] < best):
            best = r["steps"]
        r["bestSoFarSteps"] = best
        r["bestSoFarSeconds"] = (best / 50.0) if best is not None else None
    # Top-10 by steps ascending for the leaderboard panel.
    top = sorted(rows, key=lambda r: r["steps"])[:10]
    # Best per-sector duration across all laps (purple-sector / theoretical-best).
    best_durations = [None] * 9
    for r in rows:
        for i, d in enumerate(r.get("durations") or []):
            if d is None or d <= 0:
                continue
            if best_durations[i] is None or d < best_durations[i]:
                best_durations[i] = d
    return {
        "circuit": circuit,
        "rows": rows,
        "top": top,
        "count": len(rows),
        "bestDurations": best_durations,
        "sectorMacro": SECTOR_MACRO,
        "sectorK": SECTOR_K,
    }


def heatmap_for_circuit(events, circuit, bins=48):
    """Failure-focused heatmap for one circuit:
      * density: 2D grid of FAILURE positions (crashes / timeouts / off-track)
        plus per-tick wall_hit positions — i.e. where the car lost control.
      * wall_clusters: wall-hit positions clustered by 2D bin (size-by-count)
      * end_points: episode_end positions coloured by reason (full list)
    Plus the circuit's bbox + overlay (anchors/walls/kerbs from JSON)."""
    end_points = []
    walls_raw = []
    failures_x = []
    failures_z = []
    for e in events:
        if e.get("circuit") != circuit:
            continue
        ev = e.get("event")
        if ev == "episode_end":
            x = float(e.get("x", 0) or 0)
            z = float(e.get("z", 0) or 0)
            reason = e.get("reason", "") or ""
            end_points.append({
                "x": x, "z": z, "reason": reason,
                "lap_frac": float(e.get("lap_frac", 0) or 0),
            })
            # Failure modes contribute to the density grid; Success drops
            # don't pollute the heatmap with finish-line clutter.
            if reason and not reason.lower().startswith("success"):
                failures_x.append(x)
                failures_z.append(z)
        elif ev == "wall_hit":
            wx = float(e.get("x", 0) or 0)
            wz = float(e.get("z", 0) or 0)
            walls_raw.append((wx, wz))
            # Each per-tick wall scrape is also a "loss-of-control" sample.
            failures_x.append(wx)
            failures_z.append(wz)
    bbox = None
    overlay = None
    if CIRCUITS_DIR.exists():
        for jf in CIRCUITS_DIR.glob("**/" + circuit + ".json"):
            rec = _record_from_json(jf, default_stage_id=-1,
                                    default_stage_name="?")
            if rec:
                bbox = {
                    "minX": rec["minX"], "maxX": rec["maxX"],
                    "minY": rec["minY"], "maxY": rec["maxY"],
                }
                overlay = rec
                break
    # Union the circuit bbox with every telemetry point we have (wall hits,
    # end positions, failure samples). Without this, anything that lands
    # outside the original circuit footprint clips off the canvas or sits
    # in mis-aligned pixel space. Pad by 5% of the larger axis so dots
    # aren't flush with the edge.
    bbox = _expand_bbox_with_points(
        bbox,
        ((p["x"], p["z"]) for p in end_points if p.get("x") is not None and p.get("z") is not None),
        zip(failures_x, failures_z),
        walls_raw,
    )
    # Density grid — failure positions only (crashes / timeouts / wall scrapes).
    density = None
    density_max = 0
    density_total = 0
    if bbox and failures_x:
        w = (bbox["maxX"] - bbox["minX"]) or 1
        h = (bbox["maxY"] - bbox["minY"]) or 1
        grid = [0] * (bins * bins)
        for x, z in zip(failures_x, failures_z):
            bx = int((x - bbox["minX"]) / w * bins)
            bz = int((z - bbox["minY"]) / h * bins)
            if 0 <= bx < bins and 0 <= bz < bins:
                grid[bz * bins + bx] += 1
        density_total = sum(grid)
        density_max = max(grid) if grid else 0
        density = grid
    # Cluster wall hits into bins; emit cluster centroids + counts.
    wall_clusters = []
    if walls_raw:
        if bbox:
            cb = max(16, bins // 2)  # coarser bins for circle plotting
            w = (bbox["maxX"] - bbox["minX"]) or 1
            h = (bbox["maxY"] - bbox["minY"]) or 1
            buckets = {}
            for x, z in walls_raw:
                bx = int((x - bbox["minX"]) / w * cb)
                bz = int((z - bbox["minY"]) / h * cb)
                key = (bx, bz)
                rec = buckets.setdefault(key, {"sum_x": 0.0, "sum_z": 0.0, "n": 0})
                rec["sum_x"] += x
                rec["sum_z"] += z
                rec["n"] += 1
            for k, v in buckets.items():
                wall_clusters.append({
                    "x": v["sum_x"] / v["n"],
                    "z": v["sum_z"] / v["n"],
                    "count": v["n"],
                })
            wall_clusters.sort(key=lambda r: r["count"], reverse=True)
        else:
            wall_clusters = [{"x": x, "z": z, "count": 1} for x, z in walls_raw]
    failure_ends = sum(1 for p in end_points
                       if p["reason"] and not p["reason"].lower().startswith("success"))
    return {
        "circuit": circuit,
        "bins": bins,
        "bbox": bbox,
        "overlay": overlay,
        "endPoints": end_points,
        "wallClusters": wall_clusters,
        "wallHitTotal": len(walls_raw),
        "density": density,
        "densityMax": density_max,
        "densityTotal": density_total,
        "failureEndCount": failure_ends,
        "endTotal": len(end_points),
    }


_TRAINING_STYLES = r"""
  main { padding: 0 0 60px; max-width: 1480px; margin: 0 auto; }
  .training-section {
    padding: 16px 22px;
    border-bottom: 1px solid var(--hair);
  }
  .training-section:last-child { border-bottom: none; }
  .section-head {
    display: flex; align-items: baseline; justify-content: space-between;
    margin-bottom: 12px;
  }
  .section-head h2 {
    margin: 0;
    font-size: 13px; font-weight: 800;
    letter-spacing: 0.12em; text-transform: uppercase; color: var(--ink);
    font-family: 'Inter Tight', sans-serif;
  }
  .section-head .sub {
    font-family: 'JetBrains Mono', monospace; font-size: 12px; font-weight: 600;
    color: var(--ink-3);
  }

  .envs-row { display: flex; flex-wrap: wrap; gap: 10px; }
  .env-tile {
    background: var(--card); border: 1px solid var(--hair);
    border-radius: var(--r-sm); padding: 10px 14px;
    min-width: 180px;
    box-shadow: var(--shadow);
  }
  .env-tile .env-id {
    font-family: 'JetBrains Mono', monospace; font-size: 11px; font-weight: 700;
    color: var(--ink-3); letter-spacing: 0.08em; text-transform: uppercase;
  }
  .env-tile .env-circuit {
    font-family: 'JetBrains Mono', monospace; font-size: 14px; font-weight: 800;
    color: var(--ink); margin-top: 4px; letter-spacing: -0.005em;
  }
  .env-tile .env-meta {
    font-family: 'JetBrains Mono', monospace; font-size: 11px; font-weight: 600;
    color: var(--ink-3); margin-top: 3px;
  }

  .grid-two { display: grid; grid-template-columns: 1fr 1fr; gap: 14px; }
  @media (max-width: 1000px) { .grid-two { grid-template-columns: 1fr; } }

  .reason-row { margin-bottom: 8px; }
  .reason-row .reason-head {
    display: flex; gap: 8px; align-items: center;
    font-family: 'JetBrains Mono', monospace; font-size: 12px; font-weight: 700;
  }
  .reason-row .reason-bar-track {
    background: var(--paper-2); border: 1px solid var(--hair);
    height: 6px; border-radius: 99px; margin-top: 5px; overflow: hidden;
  }
  .reason-row .reason-bar-fill { height: 100%; transition: width 0.3s var(--ease); }

  .data-table {
    width: 100%; border-collapse: collapse;
    font-family: 'JetBrains Mono', monospace; font-size: 12px;
  }
  .data-table thead th {
    color: var(--ink-3); font-family: 'Inter Tight', sans-serif;
    font-weight: 800; font-size: 10px; letter-spacing: 0.10em; text-transform: uppercase;
    padding: 6px 10px;
    border-bottom: 1px solid var(--hair);
    text-align: left;
    background: var(--paper-2);
    position: sticky; top: 0;
  }
  .data-table tbody td {
    padding: 6px 10px; border-bottom: 1px solid var(--hair);
    color: var(--ink); font-weight: 700;
  }
  .data-table tbody tr {
    cursor: pointer; transition: background 0.14s var(--ease);
  }
  .data-table tbody tr:hover { background: var(--paper-2); }
  .data-table tbody tr.sel { background: var(--paper-3); }

  .scroll { max-height: 280px; overflow: auto; border: 1px solid var(--hair); border-radius: var(--r-sm); background: var(--card); }

  canvas { display: block; max-width: 100%; background: var(--card); border-radius: var(--r-sm); border: 1px solid var(--hair); }

  .legend-inline {
    display: inline-flex; flex-wrap: wrap; gap: 10px;
    font-family: 'JetBrains Mono', monospace; font-size: 11px; font-weight: 700;
    color: var(--ink-3); letter-spacing: 0.04em;
  }
  .legend-inline .sw {
    display: inline-block; width: 10px; height: 10px; border-radius: 2px;
    vertical-align: -1px; margin-right: 4px;
  }

  .pulse {
    display: inline-block; width: 8px; height: 8px; border-radius: 50%;
    background: var(--good); margin-right: 6px; animation: pulse 1.6s infinite;
    box-shadow: 0 0 0 3px #5b7a4e22;
    vertical-align: 1px;
  }
  @keyframes pulse { 0% {opacity:1} 50% {opacity:.4} 100% {opacity:1} }

  .spinner {
    display: inline-block; width: 12px; height: 12px;
    border: 2px solid var(--hair); border-top-color: var(--ink);
    border-radius: 50%; animation: spin 0.8s linear infinite;
    vertical-align: middle; margin-right: 6px;
  }
  @keyframes spin { to { transform: rotate(360deg); } }

  .relative { position: relative; }
  .loading-badge {
    position: absolute; top: 12px; right: 14px;
    background: var(--paper); border: 1px solid var(--hair);
    border-radius: 99px; padding: 4px 10px;
    font-family: 'JetBrains Mono', monospace; font-size: 11px; font-weight: 700;
    color: var(--ink-3);
    z-index: 2; display: none;
  }
  .loading-badge.on { display: inline-flex; align-items: center; }
  .loading-badge.err { background: #fbeae5; border-color: #c84a2c; color: #8b3220; }
  .loading-badge.ok  { background: #eaf1e3; border-color: #5b7a4e; color: #3e5535; }
  .loading-badge .err-dot {
    display: inline-block; width: 12px; height: 12px; line-height: 11px;
    text-align: center; background: #c84a2c; color: #fff; border-radius: 50%;
    font-size: 10px; font-weight: 900; margin-right: 6px;
  }
  .loading-badge .ok-dot {
    display: inline-block; width: 8px; height: 8px;
    background: #5b7a4e; border-radius: 50%; margin-right: 6px;
  }
  /* Briefly flash 'ok' after a successful fetch, then fade. */
  .loading-badge.fade { opacity: 0; transition: opacity 0.6s ease 0.4s; }

  .reason-pill {
    display: inline-block;
    font-family: 'JetBrains Mono', monospace; font-size: 11px; font-weight: 700;
    padding: 2px 8px; border-radius: 99px;
    letter-spacing: 0.06em;
  }

  .train-actions {
    display: flex; align-items: center; gap: 8px;
    font-family: 'JetBrains Mono', monospace; font-size: 12px; font-weight: 700;
    color: var(--ink-3);
  }
  .train-actions label { display: inline-flex; align-items: center; gap: 6px; }
  .train-actions select { font-size: 12px; }
  .train-actions .cd { color: var(--ink); }
"""

_TRAINING_META = (
    '<div class="meta"><dl><dt>status</dt><dd><span class="pill"><span class="pulse"></span>live</span></dd></dl></div>'
)

_TRAINING_ACTIONS = (
    '<div class="train-actions">'
    '<label>window <select id="win-sel">'
    '<option value="60">1m</option>'
    '<option value="300">5m</option>'
    '<option value="600">10m</option>'
    '<option value="900" selected>15m</option>'
    '</select></label>'
    '<span>auto <span class="cd" id="cd">5s</span></span>'
    '</div>'
    '<button class="btn" id="clear-btn" title="Wipe telemetry on disk + memory. Keeps last 20 minutes by default.">clear</button>'
    '<button class="btn" id="wipe-btn" title="Wipe ALL telemetry (no retention). Use if dashboard is unresponsive.">wipe all</button>'
    '<span class="mono muted" id="clear-status" style="font-size:11px"></span>'
)

_TRAINING_BODY = r"""
<!-- KPI ribbon -->
<section class="kpis relative" id="kpis-bar">
  <span class="loading-badge" id="kpis-loading"><span class="spinner"></span>loading stats…</span>
  <div class="kpi"><div class="kpi-label">total episodes</div><div class="kpi-value" id="kpi-total">0</div><div class="kpi-sub" id="kpi-total-sub">across all envs</div></div>
  <div class="kpi"><div class="kpi-label">laps completed</div><div class="kpi-value" id="kpi-laps">0</div><div class="kpi-sub" id="kpi-laps-sub">finished circuits</div></div>
  <div class="kpi"><div class="kpi-label">mean lap frac</div><div class="kpi-value" id="kpi-lapfrac">0%</div><div class="kpi-sub" id="kpi-lapfrac-sub">how far drivers get</div></div>
  <div class="kpi"><div class="kpi-label">mean reward</div><div class="kpi-value" id="kpi-reward">0</div><div class="kpi-sub" id="kpi-reward-sub">per episode</div></div>
  <div class="kpi"><div class="kpi-label">mean wall hits</div><div class="kpi-value" id="kpi-walls">0</div><div class="kpi-sub" id="kpi-walls-sub">per episode</div></div>
  <div class="kpi"><div class="kpi-label">mean steps</div><div class="kpi-value" id="kpi-steps">0</div><div class="kpi-sub" id="kpi-steps-sub">per episode</div></div>
</section>

<main>

<section class="training-section relative">
  <span class="loading-badge" id="envs-loading"><span class="spinner"></span>loading envs…</span>
  <div class="section-head">
    <h2>live envs · current circuit per worker</h2>
    <span class="sub" id="envs-sub"></span>
  </div>
  <div id="envs" class="envs-row"></div>
</section>

<section class="training-section">
  <div class="grid-two">
    <div class="card relative">
      <span class="loading-badge" id="reasons-loading"><span class="spinner"></span>loading reasons…</span>
      <div class="card-head">
        <h2>end-of-episode reason breakdown</h2>
        <span class="sub" id="reasons-sub"></span>
      </div>
      <div class="card-body"><div id="reasons"></div></div>
    </div>
    <div class="card relative">
      <span class="loading-badge" id="circuits-loading"><span class="spinner"></span>loading circuits…</span>
      <div class="card-head">
        <h2>per-circuit roll-up</h2>
        <span class="sub" id="circuit-rows-sub">click a row for lap log + heatmap</span>
      </div>
      <div class="card-body">
        <div class="scroll">
          <table class="data-table">
            <thead><tr><th>circuit</th><th>eps</th><th>laps</th><th>mean&nbsp;%</th><th>mean&nbsp;reward</th><th>wall&nbsp;hits</th><th>best&nbsp;(win)</th><th>all-time</th><th>best&nbsp;reward</th><th>mean&nbsp;lap</th></tr></thead>
            <tbody id="circuit-rows"></tbody>
          </table>
        </div>
      </div>
    </div>
  </div>
</section>

<section class="training-section relative" id="laplog-panel" style="display:none">
  <span class="loading-badge" id="laplog-loading"><span class="spinner"></span>loading lap log…</span>
  <div class="section-head">
    <h2>lap log · <span id="laplog-circuit" class="muted" style="font-family:'JetBrains Mono',monospace;font-size:13px;letter-spacing:-0.005em;text-transform:none;font-weight:700">(click a circuit)</span></h2>
    <span class="sub" id="laplog-summary"></span>
  </div>
  <div class="grid-two">
    <div class="card">
      <div class="card-head">
        <h2>top 10 <span id="laplog-top-mode">fastest</span> laps</h2>
        <label class="sub">sort <select id="laplog-sort">
          <option value="fastest" selected>fastest</option>
          <option value="latest">latest</option>
        </select></label>
      </div>
      <div class="card-body">
        <div class="scroll" style="max-height:280px">
          <table class="data-table">
            <thead><tr><th>#</th><th>lap&nbsp;time</th><th>s1</th><th>s2</th><th>s3</th><th>reward</th><th>health</th><th>when</th></tr></thead>
            <tbody id="laplog-top"></tbody>
          </table>
        </div>
      </div>
    </div>
    <div class="card">
      <div class="card-head">
        <h2>best lap time over training</h2>
        <span class="sub" id="laplog-chart-stats"></span>
      </div>
      <div class="card-body">
        <canvas id="laplog-chart" width="600" height="240"></canvas>
      </div>
    </div>
  </div>
</section>

<section class="training-section relative">
  <span class="loading-badge" id="hm-loading"><span class="spinner"></span>loading heatmap…</span>
  <div class="section-head">
    <h2>crash heatmap · episode-end positions</h2>
    <span class="sub" id="hm-count"></span>
  </div>
  <div class="row-flex" style="margin-bottom:10px;flex-wrap:wrap">
    <label class="sub" style="font-family:'JetBrains Mono',monospace;font-size:12px;color:var(--ink-3)">circuit <select id="hm-sel"></select></label>
    <span class="legend-inline">
      <span><span class="sw" style="background:#5b7a4e"></span>finish</span>
      <span><span class="sw" style="background:#b88840"></span>timeout</span>
      <span><span class="sw" style="background:#c84a2c"></span>wall_max</span>
      <span><span class="sw" style="background:#837c72"></span>other</span>
      <span><span class="sw" style="background:rgba(200,74,44,0.5);border:1.5px solid var(--ink)"></span>wall hit cluster</span>
    </span>
    <span class="spacer"></span>
    <label class="sub" style="font-family:'JetBrains Mono',monospace;font-size:12px;color:var(--ink-3)"><input type="checkbox" id="show-density" checked> density</label>
    <label class="sub" style="font-family:'JetBrains Mono',monospace;font-size:12px;color:var(--ink-3)"><input type="checkbox" id="show-walls" checked> walls</label>
    <label class="sub" style="font-family:'JetBrains Mono',monospace;font-size:12px;color:var(--ink-3)"><input type="checkbox" id="show-ends" checked> ends</label>
  </div>
  <canvas id="hm" width="1100" height="540"></canvas>
  <div id="hm-stats" class="mono muted" style="font-size:12px;margin-top:8px"></div>
</section>

</main>

<script>
const REASON_COLORS = {
  finish:        "#5b7a4e",
  lap_done:      "#5b7a4e",
  timeout:       "#b88840",
  step_max:      "#b88840",
  wall_max:      "#c84a2c",
  off_track:     "#b88840",
  agent_request: "#837c72",
};
function reasonColor(r) { return REASON_COLORS[r] || "#837c72"; }

let LAST_STATS = null;
let LAST_CIRCUITS = [];
let SELECTED_CIRCUIT = null;
function selectedWindow() {
  const v = document.getElementById('win-sel');
  return v ? (v.value || '0') : '0';
}
function withWindow(url) {
  const w = selectedWindow();
  return url + (url.includes('?') ? '&' : '?') + 'window=' + encodeURIComponent(w);
}

async function refresh() {
  // Three independent panel groups visible to the user — stats fans out to
  // KPIs + reasons + circuit-rows, envs fills the worker row. Each gets a
  // separate badge so the user can tell at a glance which pieces are still
  // loading vs. which already errored.
  setStatus('kpis-loading', 'loading');
  setStatus('reasons-loading', 'loading');
  setStatus('circuits-loading', 'loading');
  setStatus('envs-loading', 'loading');

  // Fetch the all-time records FIRST (cheap — single small JSON file). Falls
  // back to empty map on error so stats still render. renderStats reads from
  // window.LAST_RECORDS at row-render time.
  const recordsP = fetch('/api/circuit_records')
    .then(r => r.ok ? r.json() : { circuits: {} })
    .then(j => { window.LAST_RECORDS = (j && j.circuits) ? j.circuits : {}; })
    .catch(() => { window.LAST_RECORDS = {}; });

  const statsP = recordsP.then(() => fetch(withWindow('/api/training/stats')))
    .then(r => { if (!r.ok) throw new Error('HTTP ' + r.status); return r.json(); })
    .then(stats => {
      renderStats(stats);
      setStatus('kpis-loading', 'ok');
      setStatus('reasons-loading', 'ok');
      setStatus('circuits-loading', 'ok');
    })
    .catch(e => {
      console.error('stats', e);
      const msg = (e && e.message) ? e.message : 'fetch failed';
      setStatus('kpis-loading', 'error', msg);
      setStatus('reasons-loading', 'error', msg);
      setStatus('circuits-loading', 'error', msg);
    });

  const envsP = fetch('/api/training/envs')
    .then(r => { if (!r.ok) throw new Error('HTTP ' + r.status); return r.json(); })
    .then(envs => {
      renderEnvs(envs.envs || []);
      setStatus('envs-loading', 'ok');
    })
    .catch(e => {
      console.error('envs', e);
      setStatus('envs-loading', 'error', (e && e.message) || 'fetch failed');
    });

  try {
    await Promise.all([statsP, envsP]);
  } catch (e) { console.error(e); }
}

async function clearTelemetry(keepSeconds) {
  const status = document.getElementById('clear-status');
  const label = keepSeconds ? `keep last ${keepSeconds}s` : 'wipe all';
  if (!confirm(`Clear telemetry (${label})? Files on disk will be rewritten.`)) return;
  status.textContent = 'clearing…';
  try {
    const r = await fetch('/api/training/clear', {
      method: 'POST',
      headers: {'Content-Type':'application/json'},
      body: JSON.stringify({keep_seconds: keepSeconds || 0}),
    });
    const j = await r.json();
    status.textContent = j.ok ? 'cleared.' : ('error: ' + (j.error || 'unknown'));
    setTimeout(() => { status.textContent = ''; }, 4000);
    refresh();
  } catch (e) {
    status.textContent = 'error: ' + e;
  }
}

document.addEventListener('DOMContentLoaded', () => {
  const c = document.getElementById('clear-btn');
  const w = document.getElementById('wipe-btn');
  if (c) c.addEventListener('click', () => clearTelemetry(1200));
  if (w) w.addEventListener('click', () => clearTelemetry(0));
});

function renderEnvs(envs) {
  const root = document.getElementById('envs');
  const sub = document.getElementById('envs-sub');
  if (sub) sub.textContent = envs.length + ' worker' + (envs.length === 1 ? '' : 's');
  if (!envs.length) { root.innerHTML = '<span class="mono muted" style="font-size:12px">no telemetry yet — waiting for trainer to emit events…</span>'; return; }
  root.innerHTML = envs.map(e =>
    '<div class="env-tile">'
    + '<div class="env-id">' + e.env + '</div>'
    + '<div class="env-circuit">' + (e.circuit || '<em style="font-style:normal;color:var(--ink-3);font-weight:600">none</em>') + '</div>'
    + '<div class="env-meta">' + (e.pieces || 0) + ' pieces · len=' + (Number(e.length||0).toFixed(0)) + '</div>'
    + '</div>').join('');
}

function renderStats(s) {
  LAST_STATS = s;
  document.getElementById('kpi-total').textContent = s.totalEpisodes;
  document.getElementById('kpi-laps').textContent = s.lapsCompleted;
  document.getElementById('kpi-lapfrac').textContent = (100*s.meanLapFrac).toFixed(1) + '%';
  document.getElementById('kpi-reward').textContent = (s.meanReward||0).toFixed(2);
  document.getElementById('kpi-walls').textContent = (s.meanWallHits||0).toFixed(2);
  document.getElementById('kpi-steps').textContent = (s.meanSteps||0).toFixed(0);
  // Reasons.
  const total = Math.max(1, s.totalEpisodes);
  const reasons = Object.entries(s.byReason||{}).sort((a,b)=>b[1]-a[1]);
  document.getElementById('reasons-sub').textContent = reasons.length + ' reason' + (reasons.length === 1 ? '' : 's');
  document.getElementById('reasons').innerHTML = reasons.map(([k,v])=> {
    const pct = (100*v/total).toFixed(1);
    return '<div class="reason-row"><div class="reason-head">'
      +   '<span class="reason-pill" style="background:'+reasonColor(k)+'22;color:'+reasonColor(k)+'">'+k+'</span>'
      +   '<span class="muted">'+v+' / '+total+' · '+pct+'%</span>'
      + '</div>'
      + '<div class="reason-bar-track">'
      +   '<div class="reason-bar-fill" style="width:'+pct+'%;background:'+reasonColor(k)+'"></div>'
      + '</div></div>';
  }).join('') || '<span class="mono muted" style="font-size:12px">no episodes recorded</span>';
  // Per-circuit table.
  LAST_CIRCUITS = s.circuits || [];
  const sel = document.getElementById('hm-sel');
  const prev = sel.value;
  sel.innerHTML = LAST_CIRCUITS.map(c => '<option value="'+c.circuit+'">'+c.circuit+' ('+c.episodes+')</option>').join('');
  if (LAST_CIRCUITS.length) {
    SELECTED_CIRCUIT = (prev && LAST_CIRCUITS.find(c=>c.circuit===prev)) ? prev : LAST_CIRCUITS[0].circuit;
    sel.value = SELECTED_CIRCUIT;
    refreshHeatmap();
  }
  const records = window.LAST_RECORDS || {};
  document.getElementById('circuit-rows').innerHTML = LAST_CIRCUITS.map(c => {
    const best = c.bestLapSeconds != null ? c.bestLapSeconds.toFixed(1) + 's' : '—';
    const bestR = c.bestLapReward != null ? c.bestLapReward.toFixed(0) : '—';
    const mean = c.meanLapSeconds != null ? c.meanLapSeconds.toFixed(1) + 's' : '—';
    const lapCount = c.lapCount || 0;
    const meanCell = lapCount > 0 ? mean + ' <span class="muted">('+lapCount+')</span>' : '—';
    // All-time best lap from tools/circuit_records/records.json. If the
    // windowed best matches/beats the historical record we just paint the
    // value green to flag "this window is at the floor"; otherwise show
    // the historical floor and the gap (Δ) in muted text so the user can
    // see how much further the agents have to go.
    const rec = records[c.circuit];
    let allTimeCell = '<span class="muted">—</span>';
    if (rec && rec.best_lap_seconds != null) {
      const recSec = rec.best_lap_seconds;
      const recStr = recSec.toFixed(2) + 's';
      if (c.bestLapSeconds != null && c.bestLapSeconds <= recSec + 1e-3) {
        allTimeCell = '<span style="color:#3ec48a;font-weight:600">' + recStr + '</span>';
      } else if (c.bestLapSeconds != null) {
        const gap = (c.bestLapSeconds - recSec).toFixed(2);
        allTimeCell = recStr + ' <span class="muted">(+' + gap + 's)</span>';
      } else {
        allTimeCell = recStr;
      }
    }
    return '<tr data-c="'+c.circuit+'"><td>'+c.circuit+'</td>'
      + '<td>'+c.episodes+'</td>'
      + '<td>'+c.laps+'</td>'
      + '<td>'+(100*c.meanLapFrac).toFixed(0)+'%</td>'
      + '<td>'+c.meanReward.toFixed(2)+'</td>'
      + '<td>'+c.meanWallHits.toFixed(1)+'</td>'
      + '<td>'+best+'</td>'
      + '<td>'+allTimeCell+'</td>'
      + '<td>'+bestR+'</td>'
      + '<td>'+meanCell+'</td></tr>';
  }).join('');
  for (const tr of document.querySelectorAll('#circuit-rows tr')) {
    tr.addEventListener('click', () => {
      SELECTED_CIRCUIT = tr.dataset.c;
      sel.value = SELECTED_CIRCUIT;
      // Highlight selection immediately so the click feels responsive
      // even while the heatmap/lap fetches are in flight.
      for (const r of document.querySelectorAll('#circuit-rows tr')) r.classList.remove('sel');
      tr.classList.add('sel');
      // Pop open the lap-log panel right away with a loading placeholder
      // so the user sees motion within ~16ms instead of waiting for fetch.
      const panel = document.getElementById('laplog-panel');
      panel.style.display = 'block';
      document.getElementById('laplog-circuit').textContent = SELECTED_CIRCUIT + '  ·  loading…';
      document.getElementById('laplog-top').innerHTML = '<tr><td colspan="5" class="muted"><span class="spinner"></span>loading laps for ' + SELECTED_CIRCUIT + '…</td></tr>';
      document.getElementById('laplog-summary').textContent = '';
      drawLapChart({rows: [], _loading: true});
      refreshHeatmap();
    });
  }
}

document.getElementById('hm-sel').addEventListener('change', e => {
  SELECTED_CIRCUIT = e.target.value;
  refreshHeatmap();
});

// Per-panel status indicator. State is one of:
//   'loading' — spinner + "loading …" label, badge visible
//   'ok'      — green dot, briefly visible then fades out
//   'error'   — red badge with the error message, stays until next fetch
// 'ok' is the only state that auto-clears; the others persist until the
// caller changes them.
const _statusFadeTimers = {};
function setStatus(id, state, msg) {
  const el = document.getElementById(id);
  if (!el) return;
  // Cancel any pending auto-fade so a fresh fetch's spinner can't be
  // killed by the previous fetch's fade timer.
  if (_statusFadeTimers[id]) { clearTimeout(_statusFadeTimers[id]); delete _statusFadeTimers[id]; }
  el.classList.remove('on', 'err', 'ok', 'fade');
  if (state === 'loading') {
    el.classList.add('on');
    el.innerHTML = '<span class="spinner"></span>' + (msg || 'loading…');
  } else if (state === 'error') {
    el.classList.add('on', 'err');
    el.innerHTML = '<span class="err-dot">!</span>' + (msg || 'error');
  } else if (state === 'ok') {
    el.classList.add('on', 'ok');
    el.innerHTML = '<span class="ok-dot"></span>' + (msg || 'live');
    // Fade out after ~1s so the panel doesn't stay green forever between
    // 5s refresh ticks.
    _statusFadeTimers[id] = setTimeout(() => {
      el.classList.add('fade');
      delete _statusFadeTimers[id];
    }, 1000);
  }
}
// Backwards-compat shim for code that still calls setLoading(id, bool).
function setLoading(id, on) { setStatus(id, on ? 'loading' : 'ok'); }

async function refreshHeatmap() {
  if (!SELECTED_CIRCUIT) return;
  setStatus('hm-loading', 'loading', 'loading heatmap…');
  try {
    const r = await fetch(withWindow('/api/training/heatmap/' + encodeURIComponent(SELECTED_CIRCUIT)));
    if (!r.ok) throw new Error('HTTP ' + r.status);
    const j = await r.json();
    drawHeatmap(j);
    setStatus('hm-loading', 'ok');
  } catch (e) {
    console.error('heatmap', e);
    setStatus('hm-loading', 'error', (e && e.message) || 'fetch failed');
  }
  refreshLapLog();
}

async function refreshLapLog() {
  if (!SELECTED_CIRCUIT) return;
  setStatus('laplog-loading', 'loading', 'loading lap log…');
  try {
    const r = await fetch(withWindow('/api/training/laps/' + encodeURIComponent(SELECTED_CIRCUIT)));
    if (!r.ok) throw new Error('HTTP ' + r.status);
    const j = await r.json();
    LAST_LAPLOG = j;
    renderLapLog(j);
    setStatus('laplog-loading', 'ok');
  } catch (e) {
    console.error('laplog', e);
    setStatus('laplog-loading', 'error', (e && e.message) || 'fetch failed');
  }
}

let LAST_LAPLOG = null;
let LAPLOG_SORT = 'fastest';
document.getElementById('laplog-sort').addEventListener('change', e => {
  LAPLOG_SORT = e.target.value;
  document.getElementById('laplog-top-mode').textContent = LAPLOG_SORT;
  if (LAST_LAPLOG) renderLapLog(LAST_LAPLOG);
});

function renderLapLog(d) {
  const panel = document.getElementById('laplog-panel');
  panel.style.display = 'block';
  document.getElementById('laplog-circuit').textContent = d.circuit + '  ·  ' + (d.count || 0) + ' completed laps';
  if (!d.rows || !d.rows.length) {
    document.getElementById('laplog-top').innerHTML = '<tr><td colspan="8" class="muted">no completed laps yet</td></tr>';
    document.getElementById('laplog-summary').textContent = '';
    drawLapChart({rows: []});
    return;
  }
  // F1-style 3-sector splits: fold the 9 micro durations into 3 macro
  // sectors (S1/S2/S3) by summing each consecutive triple. Per-cell colour:
  // macro tint normally, magenta if the lap holds the all-time best for that
  // macro across this circuit's history (purple-sector convention).
  const bestMicroDurs = d.bestDurations || [];
  const sectorMacroColors = ['#b88840', '#5b7a4e', '#4a6c8a'];
  const microK = (d.sectorK || 9);
  const macroK = (d.sectorMacro || 3);
  const microsPerMacro = Math.max(1, Math.floor(microK / macroK));

  function macrosFromMicros(micros) {
    if (!micros || !micros.length) return null;
    const out = [];
    for (let m = 0; m < macroK; m++) {
      let sum = 0;
      let any = false;
      for (let k = 0; k < microsPerMacro; k++) {
        const v = micros[m * microsPerMacro + k];
        if (v == null || v <= 0) continue;
        sum += v;
        any = true;
      }
      out.push(any ? sum : null);
    }
    return out;
  }
  // Best macro split across all laps (purple-sector reference).
  const bestMacroDurs = (function() {
    const out = [null, null, null];
    for (const r of (d.rows || [])) {
      const macros = macrosFromMicros(r.durations);
      if (!macros) continue;
      for (let m = 0; m < macroK; m++) {
        const v = macros[m];
        if (v != null && v > 0 && (out[m] == null || v < out[m])) out[m] = v;
      }
    }
    return out;
  })();

  function renderMacroCell(durations, macroIdx) {
    const macros = macrosFromMicros(durations);
    if (!macros || macros[macroIdx] == null) return '<span class="muted">—</span>';
    const v = macros[macroIdx];
    const best = bestMacroDurs[macroIdx];
    const isPurple = best != null && Math.abs(v - best) < 1e-3;
    const color = isPurple ? '#8a5a9a' : sectorMacroColors[macroIdx % sectorMacroColors.length];
    const weight = isPurple ? 'bold' : 'normal';
    return '<span style="color:' + color + ';font-weight:' + weight + '">' + v.toFixed(2) + 's</span>';
  }

  // Top-10 view honours the leaderboard sort selector — fastest (server-
  // computed `d.top`) or latest (last 10 chronological rows, newest first).
  const sortedTop = LAPLOG_SORT === 'latest'
    ? (d.rows || []).slice(-10).reverse()
    : (d.top || []);
  document.getElementById('laplog-top').innerHTML = sortedTop.map((r, i) => {
    const ts = r.ts ? r.ts.replace('T', ' ').slice(0, 19) : '—';
    return '<tr><td>' + (i+1) + '</td>'
      + '<td><b>' + r.seconds.toFixed(2) + 's</b> <span class="muted">(' + r.steps + ' steps)</span></td>'
      + '<td>' + renderMacroCell(r.durations, 0) + '</td>'
      + '<td>' + renderMacroCell(r.durations, 1) + '</td>'
      + '<td>' + renderMacroCell(r.durations, 2) + '</td>'
      + '<td>' + r.reward.toFixed(0) + '</td>'
      + '<td>' + (function(){
          const h = (r.health == null ? 1 : r.health);
          const pct = Math.round(h * 100);
          const col = h >= 0.999 ? '#5b7a4e' : h >= 0.6 ? '#b88840' : '#c84a2c';
          const wt = h >= 0.999 ? 'normal' : 'bold';
          return '<span style="color:' + col + ';font-weight:' + wt + '">' + pct + '%</span>';
        })() + '</td>'
      + '<td class="muted" style="font-size:11px">' + ts + '</td></tr>';
  }).join('');
  // Improvement summary: first vs latest best-so-far.
  const first = d.rows[0];
  const last = d.rows[d.rows.length-1];
  const dropAbs = first.bestSoFarSeconds - last.bestSoFarSeconds;
  const dropPct = (100 * dropAbs / Math.max(1e-3, first.bestSoFarSeconds)).toFixed(1);
  document.getElementById('laplog-summary').textContent =
    'first lap ' + first.seconds.toFixed(2) + 's  →  best ' + last.bestSoFarSeconds.toFixed(2) + 's  ('
    + (dropAbs >= 0 ? '−' : '+') + Math.abs(dropAbs).toFixed(2) + 's, ' + dropPct + '%)';
  drawLapChart(d);
}

function drawLapChart(d) {
  const cv = document.getElementById('laplog-chart');
  const ctx = cv.getContext('2d');
  ctx.fillStyle = '#f6f3eb'; ctx.fillRect(0,0,cv.width,cv.height);
  const rows = d.rows || [];
  if (!rows.length) {
    ctx.fillStyle = '#837c72'; ctx.font = '13px "JetBrains Mono", monospace';
    ctx.fillText(d._loading ? 'loading lap history…' : 'no laps for this circuit yet', 16, 28);
    document.getElementById('laplog-chart-stats').textContent = '';
    return;
  }
  const pad = 30;
  const W = cv.width, H = cv.height;
  const sec = rows.map(r => r.seconds);
  const xN = rows.length;
  const meanSec = sec.reduce((a,b)=>a+b, 0) / xN;
  let bestIdx = 0;
  for (let i = 1; i < xN; i++) if (sec[i] < sec[bestIdx]) bestIdx = i;
  const bestSec = sec[bestIdx];
  const yMax = Math.max(...sec) * 1.05;
  const yMin = bestSec * 0.95;
  const xAt = i => pad + (W - 2*pad) * (xN > 1 ? i / (xN-1) : 0.5);
  const yAt = v => H - pad - (H - 2*pad) * (v - yMin) / Math.max(1e-3, yMax - yMin);
  // Grid (chalk hair).
  ctx.strokeStyle = '#d4cdba'; ctx.lineWidth = 0.6;
  ctx.setLineDash([2, 3]);
  for (let i = 0; i <= 4; i++) {
    const y = pad + i * (H - 2*pad) / 4;
    ctx.beginPath(); ctx.moveTo(pad, y); ctx.lineTo(W - pad, y); ctx.stroke();
  }
  ctx.setLineDash([]);
  ctx.fillStyle = '#837c72'; ctx.font = '10px "JetBrains Mono", monospace';
  for (let i = 0; i <= 4; i++) {
    const v = yMax - i * (yMax - yMin) / 4;
    ctx.fillText(v.toFixed(1) + 's', 2, pad + i * (H - 2*pad) / 4 + 4);
  }
  // Mean line — slate.
  const yMean = yAt(meanSec);
  ctx.strokeStyle = '#4a6c8a'; ctx.lineWidth = 1.4;
  ctx.setLineDash([6, 4]);
  ctx.beginPath();
  ctx.moveTo(pad, yMean); ctx.lineTo(W - pad, yMean);
  ctx.stroke();
  ctx.setLineDash([]);
  ctx.fillStyle = '#4a6c8a'; ctx.font = 'bold 11px "JetBrains Mono", monospace';
  ctx.fillText('mean ' + meanSec.toFixed(2) + 's', W - pad - 100, yMean - 4);
  // Per-lap points (ink-3) except fastest (signal).
  for (let i = 0; i < xN; i++) {
    if (i === bestIdx) continue;
    ctx.fillStyle = 'rgba(131,124,114,0.55)';
    ctx.beginPath(); ctx.arc(xAt(i), yAt(sec[i]), 2.2, 0, 2*Math.PI); ctx.fill();
  }
  // Fastest lap — signal, halo.
  const bx = xAt(bestIdx), by = yAt(bestSec);
  ctx.fillStyle = 'rgba(200,74,44,0.20)';
  ctx.beginPath(); ctx.arc(bx, by, 8, 0, 2*Math.PI); ctx.fill();
  ctx.fillStyle = '#c84a2c';
  ctx.beginPath(); ctx.arc(bx, by, 4, 0, 2*Math.PI); ctx.fill();
  ctx.fillStyle = '#c84a2c'; ctx.font = 'bold 11px "JetBrains Mono", monospace';
  const lblX = Math.min(W - pad - 6, bx + 8);
  ctx.fillText(bestSec.toFixed(2) + 's', lblX, by - 6);
  document.getElementById('laplog-chart-stats').textContent =
    xN + ' laps · grey = each lap · slate dashed = mean (' + meanSec.toFixed(2) + 's) · signal = fastest (' + bestSec.toFixed(2) + 's)';
}

// Chalk-friendly density ramp: slate (cool, low) -> ochre (mid) -> signal (hot).
function densityColor(t, alpha) {
  t = Math.max(0, Math.min(1, t));
  let r, g, b;
  if (t < 0.5) {
    // slate (#4a6c8a) -> ochre (#b88840)
    const k = t / 0.5;
    r = Math.round(74  + (184 - 74)  * k);
    g = Math.round(108 + (136 - 108) * k);
    b = Math.round(138 + (64  - 138) * k);
  } else {
    // ochre -> signal (#c84a2c)
    const k = (t - 0.5) / 0.5;
    r = Math.round(184 + (200 - 184) * k);
    g = Math.round(136 + (74  - 136) * k);
    b = Math.round(64  + (44  - 64)  * k);
  }
  return 'rgba(' + r + ',' + g + ',' + b + ',' + alpha.toFixed(2) + ')';
}

function drawHeatmap(d) {
  const cv = document.getElementById('hm');
  const ctx = cv.getContext('2d');
  ctx.fillStyle = '#f6f3eb'; ctx.fillRect(0,0,cv.width,cv.height);
  const showD = document.getElementById('show-density').checked;
  const showW = document.getElementById('show-walls').checked;
  const showE = document.getElementById('show-ends').checked;
  document.getElementById('hm-count').textContent =
    (d.endTotal||0) + ' ends (' + (d.failureEndCount||0) + ' failures) · '
    + (d.wallHitTotal||0) + ' wall hits';
  if (!d.bbox) {
    ctx.fillStyle = '#837c72'; ctx.font = '14px "JetBrains Mono", monospace';
    ctx.fillText('no bbox available for this circuit (no JSON found)', 16, 24);
    return;
  }
  const pad = 28;
  const w = d.bbox.maxX - d.bbox.minX || 1;
  const h = d.bbox.maxY - d.bbox.minY || 1;
  const sc = Math.min((cv.width - 2*pad)/w, (cv.height - 2*pad)/h);
  const ox = (cv.width - sc*w) / 2;
  const oy = (cv.height - sc*h) / 2;
  const proj = (x, y) => [ox + (x - d.bbox.minX) * sc, cv.height - oy - (y - d.bbox.minY) * sc];

  // ---- Density layer (cyan -> red plasma ramp) -----------------------------
  const bins = d.bins || 48;
  if (showD && d.density && d.densityMax > 0) {
    const cellW = (sc * w) / bins;
    const cellH = (sc * h) / bins;
    for (let yy=0; yy<bins; yy++) for (let xx=0; xx<bins; xx++) {
      const c = d.density[yy*bins + xx];
      if (c <= 0) continue;
      // log-normalise so common areas don't blot out the rare hot spots
      const t = Math.log(1 + c) / Math.log(1 + d.densityMax);
      ctx.fillStyle = densityColor(t, 0.18 + 0.55 * t);
      const sx = ox + xx*cellW;
      const sy = cv.height - oy - (yy+1)*cellH;
      ctx.fillRect(sx, sy, cellW+0.6, cellH+0.6);
    }
  }

  // ---- Track overlay (kerbs/anchors/walls) ---------------------------------
  if (d.overlay) {
    if (d.overlay.kerbs) {
      ctx.fillStyle = 'rgba(200,74,44,0.22)';
      for (const q of d.overlay.kerbs) {
        ctx.beginPath();
        const [a0,a1] = proj(q[0],q[1]);
        const [b0,b1] = proj(q[2],q[3]);
        const [cx,cy] = proj(q[4],q[5]);
        const [e0,e1] = proj(q[6],q[7]);
        ctx.moveTo(a0,a1); ctx.lineTo(b0,b1); ctx.lineTo(cx,cy); ctx.lineTo(e0,e1); ctx.closePath(); ctx.fill();
      }
    }
    if (d.overlay.anchors && d.overlay.anchors.length>1) {
      ctx.strokeStyle = 'rgba(31,28,23,0.85)'; ctx.lineWidth = 1.6; ctx.beginPath();
      for (let i=0;i<d.overlay.anchors.length;i++) {
        const [sx,sy] = proj(d.overlay.anchors[i][0], d.overlay.anchors[i][1]);
        if (i===0) ctx.moveTo(sx,sy); else ctx.lineTo(sx,sy);
      }
      ctx.closePath(); ctx.stroke();

      // F1-style 3-sector display (S1/S2/S3). The K=9 micro sectors stay in
      // the underlying logic for cheat-prevention; here only the 3 macro
      // boundaries get labels + heavy ticks. Micro boundaries between them
      // are drawn as small unlabeled ticks so you can still see all 9 gates
      // without text clutter.
      const anchors = d.overlay.anchors;
      const sectorIdx = d.overlay.sectorIdx || [];
      const lapStartIdx = (d.overlay.lapStartIdx == null) ? 0 : d.overlay.lapStartIdx;
      const macroCount = d.overlay.sectorMacro || 3;
      const microCount = sectorIdx.length;
      const microsPerMacro = microCount > 0 ? Math.max(1, Math.floor(microCount / macroCount)) : 1;
      const sectorColors = ['#b88840', '#5b7a4e', '#4a6c8a'];
      for (let s = 0; s < sectorIdx.length; s++) {
        const ai = sectorIdx[s];
        const a = anchors[ai];
        const ap = anchors[(ai - 1 + anchors.length) % anchors.length];
        const an = anchors[(ai + 1) % anchors.length];
        const [px, py] = proj(a[0], a[1]);
        const [npx, npy] = proj(an[0], an[1]);
        const [ppx, ppy] = proj(ap[0], ap[1]);
        let tx = npx - ppx, ty = npy - ppy;
        const tlen = Math.hypot(tx, ty) || 1;
        tx /= tlen; ty /= tlen;
        const nx = -ty, ny = tx;
        const isStart = s === 0;
        const isMacro = (s % microsPerMacro) === 0;
        // S1 spans micros [0..microsPerMacro-1], S2 next, S3 next.
        const macroNum = Math.floor(s / microsPerMacro) + 1; // F1 convention 1..3
        const halfLen = isStart ? 18.0 : (isMacro ? 12.0 : 5.0);
        ctx.beginPath();
        ctx.moveTo(px + nx * halfLen, py + ny * halfLen);
        ctx.lineTo(px - nx * halfLen, py - ny * halfLen);
        ctx.strokeStyle = isStart ? '#1f1c17'
                          : (isMacro ? sectorColors[(macroNum - 1) % sectorColors.length]
                                     : 'rgba(131,124,114,0.55)');
        ctx.lineWidth = isStart ? 4.0 : (isMacro ? 2.8 : 1.2);
        ctx.lineCap = 'round';
        ctx.stroke();
        // Only label macro boundaries: "S1", "S2", "S3" (F1 convention).
        if (isMacro) {
          ctx.font = isStart ? 'bold 13px "JetBrains Mono", monospace' : 'bold 12px "JetBrains Mono", monospace';
          ctx.fillStyle = isStart ? '#1f1c17' : sectorColors[(macroNum - 1) % sectorColors.length];
          // Label says "Start / S1" at the lap-start gate, just "S2" / "S3" elsewhere.
          const label = isStart ? 'START · S1' : ('S' + macroNum);
          ctx.fillText(label, px + nx * (halfLen + 5), py + ny * (halfLen + 5));
        }
      }
      // Direction arrow at lap-start anchor.
      if (anchors.length > 1) {
        const sa = anchors[lapStartIdx];
        const sb = anchors[(lapStartIdx + 1) % anchors.length];
        const [sax, say] = proj(sa[0], sa[1]);
        const [sbx, sby] = proj(sb[0], sb[1]);
        let dx = sbx - sax, dy = sby - say;
        const dlen = Math.hypot(dx, dy) || 1;
        dx /= dlen; dy /= dlen;
        const arrowLen = 22.0, arrowHalf = 9.0;
        const tipX = sax + dx * arrowLen;
        const tipY = say + dy * arrowLen;
        ctx.beginPath();
        ctx.moveTo(tipX, tipY);
        ctx.lineTo(sax + (-dy) * arrowHalf, say + (dx) * arrowHalf);
        ctx.lineTo(sax - (-dy) * arrowHalf, say - (dx) * arrowHalf);
        ctx.closePath();
        ctx.fillStyle = '#1f1c17';
        ctx.fill();
        ctx.lineWidth = 1.2;
        ctx.strokeStyle = '#efece4';
        ctx.stroke();
        // Start dot at the gate position.
        ctx.beginPath(); ctx.arc(sax, say, 4.2, 0, 2*Math.PI);
        ctx.fillStyle = '#1f1c17';
        ctx.fill();
        ctx.lineWidth = 1.0;
        ctx.strokeStyle = '#efece4';
        ctx.stroke();
      }
    }
    if (d.overlay.walls) {
      ctx.strokeStyle = 'rgba(74,69,64,0.65)'; ctx.lineWidth = 1.0;
      for (const wseg of d.overlay.walls) {
        const [ax,ay] = proj(wseg[0],wseg[1]);
        const [bx,by] = proj(wseg[2],wseg[3]);
        ctx.beginPath(); ctx.moveTo(ax,ay); ctx.lineTo(bx,by); ctx.stroke();
      }
    }
  }

  // ---- Wall-hit clusters (size = count) ------------------------------------
  if (showW && d.wallClusters && d.wallClusters.length) {
    const maxN = d.wallClusters[0].count || 1;
    ctx.font = 'bold 11px "JetBrains Mono", monospace';
    for (const c of d.wallClusters) {
      const [sx, sy] = proj(c.x, c.z);
      const r = 4 + 14 * Math.sqrt(c.count / maxN);
      ctx.beginPath(); ctx.arc(sx, sy, r, 0, 2*Math.PI);
      ctx.fillStyle = 'rgba(200,74,44,0.50)';
      ctx.fill();
      ctx.lineWidth = 1.4;
      ctx.strokeStyle = '#1f1c17';
      ctx.stroke();
      if (c.count >= 3) {
        ctx.fillStyle = '#1f1c17';
        ctx.fillText(c.count, sx + r + 2, sy + 3);
      }
    }
  }

  // ---- Episode-end markers -------------------------------------------------
  if (showE && d.endPoints) {
    for (const p of d.endPoints) {
      const [sx, sy] = proj(p.x, p.z);
      ctx.fillStyle = reasonColor(p.reason);
      ctx.beginPath(); ctx.arc(sx, sy, 2.6, 0, 2*Math.PI); ctx.fill();
      ctx.lineWidth = 0.5;
      ctx.strokeStyle = 'rgba(31,28,23,0.45)';
      ctx.stroke();
    }
  }

  // ---- Stats line ---------------------------------------------------------
  const stats = [];
  if (d.densityTotal) stats.push('failure samples: ' + d.densityTotal.toLocaleString());
  if (d.densityMax) {
    const pctMax = (100 * d.densityMax / Math.max(1, d.densityTotal)).toFixed(2);
    stats.push('hottest bin: ' + d.densityMax + ' (' + pctMax + '% of failures)');
  }
  if (d.wallClusters && d.wallClusters.length) {
    const top = d.wallClusters[0];
    stats.push('top wall hotspot: ' + top.count + ' hits @ (' + top.x.toFixed(1) + ',' + top.z.toFixed(1) + ')');
  }
  document.getElementById('hm-stats').textContent = stats.join('  ·  ');
}

document.addEventListener('change', (e) => {
  if (['show-density','show-walls','show-ends'].includes(e.target.id)) refreshHeatmap();
  if (e.target.id === 'win-sel') refresh();
});

let countdown = 5;
setInterval(() => {
  countdown -= 1;
  if (countdown <= 0) { refresh(); countdown = 5; }
  document.getElementById('cd').textContent = countdown + 's';
}, 1000);
refresh();
</script>
"""

TRAINING_HTML = (
    '<!doctype html><html lang="en"><head><meta charset="utf-8">'
    '<title>RACING · training dashboard</title>'
    '<meta name="viewport" content="width=device-width, initial-scale=1">'
    + BASE_HEAD
    + '<style>' + BASE_STYLES + _TRAINING_STYLES + '</style>'
    + '</head><body>'
    + chrome_header('training', 'training dashboard',
                    meta_html=_TRAINING_META, actions_html=_TRAINING_ACTIONS)
    + _TRAINING_BODY
    + '</body></html>'
)


# ---------------------------------------------------------------------------
# Race history (C# RaceTelemetryService -> results/_telemetry/races/*.json).
# Reservoir-sampled: 1 race kept per 1000 episodes per env. The C# sink caps
# the directory at 50 newest files; this server is read-only.
# ---------------------------------------------------------------------------

def _race_files_newest_first():
    if not RACES_DIR.exists():
        return []
    files = []
    for p in RACES_DIR.glob("race_*.json"):
        try:
            files.append((p, p.stat().st_mtime))
        except OSError:
            continue
    files.sort(key=lambda t: t[1], reverse=True)
    return [t[0] for t in files]


def _load_race(path):
    try:
        with open(path, "r", encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return None


def load_race_summaries(max_n=50):
    out = []
    for p in _race_files_newest_first()[:max_n]:
        rec = _load_race(p)
        if not rec:
            continue
        circuit = rec.get("circuit") or {}
        drivers = rec.get("drivers") or []
        out.append({
            "race_id": rec.get("race_id", ""),
            "captured_at_utc": rec.get("captured_at_utc", ""),
            "env_pid": rec.get("env_pid", 0),
            "episode_index": rec.get("episode_index", 0),
            "stage_id": rec.get("stage_id", 0),
            "circuit_id": circuit.get("id", ""),
            "duration_s": rec.get("duration_s", 0.0),
            "driver_count": len(drivers),
            "reasons": [(d.get("end_state") or {}).get("reason", "") for d in drivers],
            # Race-scoped fields. Stay 0/empty for legacy races so the
            # dashboard's existing per-row rendering is unchanged.
            "end_reason": rec.get("end_reason", ""),
            "lap_target": rec.get("lap_target", 0),
            "finishers_count": rec.get("finishers_count", 0),
            "eliminated_count": rec.get("eliminated_count", 0),
        })
    return out


def load_race_by_id(race_id):
    for p in _race_files_newest_first():
        rec = _load_race(p)
        if rec and rec.get("race_id") == race_id:
            return rec
    return None


def _load_circuit_overlay(circuit_id):
    """Locate the circuit JSON by id, return the same record shape as
    _record_from_json (with anchor-scale correction applied) so the race
    detail page can draw the track in correct-world coords. Returns None
    if not found."""
    if not circuit_id or not CIRCUITS_DIR.exists():
        return None
    for jf in CIRCUITS_DIR.glob("**/" + circuit_id + ".json"):
        rec = _record_from_json(jf, default_stage_id=-1, default_stage_name="?")
        if rec is not None:
            return rec
    return None


def _sample_pos_iter(race):
    """Yields (x, z) world coords for every sample across every driver."""
    for d in (race.get("drivers") or []):
        for s in (d.get("samples") or []):
            pos = s.get("pos") or {}
            if isinstance(pos, dict):
                x = pos.get("x")
                z = pos.get("z")
            elif isinstance(pos, (list, tuple)) and len(pos) >= 3:
                x, z = pos[0], pos[2]
            else:
                continue
            if x is not None and z is not None:
                yield (x, z)


def enriched_race(race_id):
    """Race record + circuit overlay + effective bbox. Used by
    /api/races/<id> so the replay canvas on /races/<id> can draw the
    circuit and the driver dots in a single, consistent coord system.

    The bbox is built from the **circuit geometry alone** (anchors, walls,
    kerbs — already in current-world coords after the legacy rescale).
    Driver samples that wander off-track are deliberately excluded so they
    cannot stretch the canvas and shrink the visible track. Cars that drive
    far outside the circuit footprint will project to the canvas edge or
    just past it — acceptable trade-off versus a squished track every time
    a single agent yeets off-circuit at high speed."""
    race = load_race_by_id(race_id)
    if race is None:
        return None
    circuit = race.get("circuit") or {}
    overlay = _load_circuit_overlay(circuit.get("id") or "")
    bbox = None
    if overlay:
        bbox = {
            "minX": overlay["minX"], "maxX": overlay["maxX"],
            "minY": overlay["minY"], "maxY": overlay["maxY"],
        }
    # Just pad — no sample-position union.
    effective = _expand_bbox_with_points(bbox)
    race["overlay"] = overlay
    race["effective_bbox"] = effective
    return race


_RACES_STYLES = r"""
  main { padding: 0 0 60px; }
  .races-table {
    width: 100%; border-collapse: collapse;
    font-family: 'JetBrains Mono', monospace; font-size: 13px;
  }
  .races-table thead th {
    background: var(--paper-2);
    color: var(--ink-3);
    font-family: 'Inter Tight', sans-serif;
    font-size: 11px; font-weight: 800;
    letter-spacing: 0.10em; text-transform: uppercase;
    text-align: left;
    padding: 10px 16px;
    border-bottom: 1px solid var(--hair);
    position: sticky; top: 64px; z-index: 5;
  }
  .races-table tbody td {
    padding: 10px 16px;
    border-bottom: 1px solid var(--hair);
    color: var(--ink); font-weight: 700;
    vertical-align: middle;
  }
  .races-table tbody tr {
    cursor: pointer; transition: background 0.14s var(--ease);
  }
  .races-table tbody tr:hover { background: var(--paper-2); }
  .races-table tbody tr:last-child td { border-bottom: none; }
  .races-table .col-circuit { color: var(--ink-2); font-weight: 700; letter-spacing: -0.005em; }
  .races-table .col-ts { color: var(--ink-3); font-weight: 600; }
  .races-table .col-num { font-weight: 800; }
  .races-table .reasons-cell {
    display: flex; flex-wrap: wrap; gap: 4px;
    max-width: 380px;
  }
  .reason-chip {
    font-family: 'Inter Tight', sans-serif; font-size: 10px; font-weight: 800;
    text-transform: uppercase; letter-spacing: 0.06em;
    padding: 2px 6px; border-radius: 3px;
    background: var(--paper-3); color: var(--ink-2);
  }
  .reason-chip[data-r="Failure_Wreck"],
  .reason-chip[data-r="wall_max"],
  .reason-chip[data-r="Failure_Collision"]  { background: #e7c1b5; color: #6e2410; }
  .reason-chip[data-r="Failure_OffTrack"],
  .reason-chip[data-r="off_track"],
  .reason-chip[data-r="step_max"],
  .reason-chip[data-r="timeout"]            { background: #ead7b0; color: #644413; }
  .reason-chip[data-r="Finished"],
  .reason-chip[data-r="Success"],
  .reason-chip[data-r="finish"],
  .reason-chip[data-r="lap_done"]           { background: #c8d8bf; color: #2c4520; }

  .empty {
    padding: 60px 24px; text-align: center;
    color: var(--ink-3);
    font-family: 'JetBrains Mono', monospace; font-size: 13px;
  }
  .empty code {
    background: var(--paper-2); padding: 2px 6px; border-radius: 3px;
    border: 1px solid var(--hair); color: var(--ink);
  }
  .blurb {
    padding: 10px 22px; font-family: 'JetBrains Mono', monospace;
    font-size: 11px; font-weight: 600; color: var(--ink-3);
    background: var(--paper);
    border-bottom: 1px solid var(--hair);
    letter-spacing: 0.02em;
  }
"""

_RACES_BODY = r"""
<!-- KPI ribbon -->
<section class="kpis relative" id="kpis">
  <span class="loading-badge" id="races-kpis-loading"><span class="spinner"></span>loading…</span>
</section>
<div class="blurb">
  reservoir-sampled · each env keeps 1 random race per 1000 episodes · cap 50 newest globally · click row for per-driver detail.
</div>
<main class="relative">
  <span class="loading-badge" id="races-tbl-loading"><span class="spinner"></span>loading races…</span>
  <div id="tbl"></div>
</main>
<script>
function fmt(s) { return (s && s.length > 19) ? s.substring(0, 19).replace('T', ' ') : (s || '—'); }
function relative(iso) {
  if (!iso) return '—';
  const t = Date.parse(iso); if (isNaN(t)) return iso;
  const dt = (Date.now() - t) / 1000;
  if (dt < 60) return Math.floor(dt) + 's ago';
  if (dt < 3600) return Math.floor(dt/60) + 'm ago';
  if (dt < 86400) return Math.floor(dt/3600) + 'h ago';
  return Math.floor(dt/86400) + 'd ago';
}

function renderKPIs(races) {
  const finished = races.filter(r => (r.reasons||[]).some(x => x === 'Finished' || x === 'Success' || x === 'finish' || x === 'lap_done')).length;
  const eliminated = races.filter(r => (r.reasons||[]).length && (r.reasons||[]).every(x => x.indexOf('Failure') === 0)).length;
  const avgDur = races.length
    ? races.reduce((s,r) => s + (r.duration_s||0), 0) / races.length
    : 0;
  const latest = races.length ? relative(races[0].captured_at_utc) : '—';
  const uniqueCircuits = new Set(races.map(r => r.circuit_id).filter(Boolean)).size;
  const totalDrivers = races.reduce((s,r) => s + (r.driver_count||0), 0);
  const items = [
    { label: 'races',       value: races.length,      sub: '50 newest, reservoir sampled' },
    { label: 'finished',    value: finished,          sub: 'any driver completed lap' },
    { label: 'eliminated',  value: eliminated,        sub: 'all drivers DNF' },
    { label: 'avg duration',value: avgDur.toFixed(1)+'s', sub: 'per race window' },
    { label: 'circuits',    value: uniqueCircuits,    sub: 'distinct ids in window' },
    { label: 'latest',      value: latest,            sub: totalDrivers + ' drivers total' },
  ];
  document.getElementById('kpis').innerHTML = items.map(k => `
    <div class="kpi">
      <div class="kpi-label">${k.label}</div>
      <div class="kpi-value">${k.value}</div>
      <div class="kpi-sub">${k.sub}</div>
    </div>`).join('');
}

function setStatus(id, state, msg) {
  const el = document.getElementById(id);
  if (!el) return;
  el.classList.remove('on', 'err', 'ok', 'fade');
  if (state === 'loading') {
    el.classList.add('on');
    el.innerHTML = '<span class="spinner"></span>' + (msg || 'loading…');
  } else if (state === 'error') {
    el.classList.add('on', 'err');
    el.innerHTML = '<span class="err-dot">!</span>' + (msg || 'error');
  } else if (state === 'ok') {
    el.classList.add('on', 'ok');
    el.innerHTML = '<span class="ok-dot"></span>' + (msg || 'live');
    setTimeout(() => el.classList.add('fade'), 600);
  }
}

async function load() {
  setStatus('races-kpis-loading', 'loading');
  setStatus('races-tbl-loading', 'loading', 'loading races…');
  let r;
  try {
    r = await fetch('/api/races');
    if (!r.ok) throw new Error('HTTP ' + r.status);
  } catch (e) {
    setStatus('races-kpis-loading', 'error', e.message || 'fetch failed');
    setStatus('races-tbl-loading', 'error', e.message || 'fetch failed');
    document.getElementById('tbl').innerHTML = '<div class="empty">failed to load: ' + (e.message || e) + '</div>';
    return;
  }
  const d = await r.json();
  const races = d.races || [];
  renderKPIs(races);
  setStatus('races-kpis-loading', 'ok');
  const el = document.getElementById('tbl');
  if (!races.length) {
    el.innerHTML = '<div class="empty">no races yet · trainer writes one race per ~1000 episodes per env into <code>results/_telemetry/races/</code>.</div>';
    setStatus('races-tbl-loading', 'ok', 'empty');
    return;
  }
  let html = '<table class="races-table"><thead><tr>'
    + '<th>captured</th><th>episode</th><th>stage</th><th>circuit</th>'
    + '<th>drivers</th><th>outcome</th><th>duration</th><th>reasons</th><th>env pid</th>'
    + '</tr></thead><tbody>';
  for (const r of races) {
    const reasons = (r.reasons || [])
      .map(x => `<span class="reason-chip" data-r="${x || ''}">${(x || '?').replace('Failure_','').toLowerCase()}</span>`)
      .join('');
    // Race-scoped outcome cell: shows "Nf / Me · target X" when the race
    // recorded a lap_target. Legacy races (no end_reason) render an em-dash.
    let outcomeCell = '—';
    if (r.end_reason && r.end_reason !== '' && r.end_reason !== 'in_progress') {
      const fin = r.finishers_count || 0;
      const elim = r.eliminated_count || 0;
      const tag = r.end_reason.replace('AllDriversResolved','full')
                              .replace('MaxStepsCap','cap')
                              .replace('Aborted','abort');
      outcomeCell = `<b>${fin}f</b> / <b>${elim}e</b><br><span class="reason-chip" data-r="race-${tag}">${tag} · ${r.lap_target || '?'}L</span>`;
    }
    html += `<tr onclick="location.href='/races/${encodeURIComponent(r.race_id)}'">`
      + `<td class="col-ts">${fmt(r.captured_at_utc || '')}</td>`
      + `<td class="col-num">${r.episode_index}</td>`
      + `<td class="col-num">${r.stage_id}</td>`
      + `<td class="col-circuit">${r.circuit_id || '?'}</td>`
      + `<td class="col-num">${r.driver_count}</td>`
      + `<td class="col-outcome">${outcomeCell}</td>`
      + `<td class="col-num">${(r.duration_s || 0).toFixed(1)}s</td>`
      + `<td><div class="reasons-cell">${reasons}</div></td>`
      + `<td class="col-num">${r.env_pid}</td>`
      + '</tr>';
  }
  html += '</tbody></table>';
  el.innerHTML = html;
  setStatus('races-tbl-loading', 'ok', races.length + ' races');
}
load();
</script>
"""

RACES_LIST_HTML = (
    '<!doctype html><html lang="en"><head><meta charset="utf-8">'
    '<title>RACING · race history</title>'
    '<meta name="viewport" content="width=device-width, initial-scale=1">'
    + BASE_HEAD
    + '<style>' + BASE_STYLES + _RACES_STYLES + '</style>'
    + '</head><body>'
    + chrome_header('races', 'race history')
    + _RACES_BODY
    + '</body></html>'
)


_RACE_DETAIL_STYLES = r"""
  /* main grid ----- */
  main.race-grid {
    display: grid;
    grid-template-columns: minmax(0, 1fr) 420px;
    gap: 18px;
    padding: 18px 22px;
  }
  @media (max-width: 1100px) { main.race-grid { grid-template-columns: 1fr; } }

  /* ---------- TRACK MAP ---------- */
  .map-card { display: flex; flex-direction: column; }
  .map-wrap {
    aspect-ratio: 4 / 3;
    position: relative;
    background:
      radial-gradient(ellipse at 50% 100%, #00000008 0%, transparent 60%),
      var(--card);
  }
  .map-wrap svg { width: 100%; height: 100%; display: block; }
  .map-overlay {
    position: absolute; inset: 14px;
    pointer-events: none;
    display: flex; flex-direction: column; justify-content: space-between;
  }
  .map-overlay .corner {
    font-family: 'JetBrains Mono', monospace; font-size: 12px; font-weight: 700;
    color: var(--ink-3); letter-spacing: 0.04em;
    display: flex; gap: 14px;
  }
  .map-overlay .corner.bl { align-self: flex-start; }
  .map-overlay .corner.br { align-self: flex-end; }
  .map-legend {
    display: flex; gap: 14px; align-items: center;
    padding: 12px 18px; border-top: 1px solid var(--hair);
    font-size: 12px; font-weight: 700; color: var(--ink-2);
    font-family: 'JetBrains Mono', monospace;
  }
  .map-legend .swatch { display: inline-flex; align-items: center; gap: 6px; }
  .map-legend .swatch::before {
    content: ''; width: 10px; height: 2px; background: var(--ink-3); display: inline-block;
  }
  .map-legend .swatch.active::before { background: var(--signal); height: 2px; }
  .map-legend .swatch.dead::before { background: var(--ink-4); border-top: 1px dashed var(--ink-3); height: 0; border-radius: 0; }

  .car-tip {
    position: absolute; pointer-events: none;
    background: var(--ink); color: var(--paper);
    padding: 10px 12px; border-radius: var(--r-sm);
    font-family: 'JetBrains Mono', monospace; font-size: 12px; font-weight: 600;
    transform: translate(-50%, -120%);
    opacity: 0; transition: opacity 0.14s;
    white-space: nowrap;
    box-shadow: 0 8px 24px -8px #00000050;
    z-index: 20;
  }
  .car-tip.on { opacity: 1; }
  .car-tip .tip-name { color: var(--car); font-weight: 800; font-size: 14px; }
  .car-tip dl { margin: 6px 0 0; display: grid; grid-template-columns: auto 1fr; gap: 2px 12px; font-size: 11px; }
  .car-tip dt { color: #ffffff88; }
  .car-tip dd { margin: 0; color: var(--paper); }

  /* ---------- LEADERBOARD ---------- */
  .lb { max-height: 720px; overflow: auto; }
  .lb-tools {
    display: flex; gap: 8px; padding: 10px 14px;
    border-bottom: 1px solid var(--hair); background: var(--paper-2);
  }
  .lb-tools input { flex: 1; }
  .lb-row {
    display: grid;
    grid-template-columns: 26px 16px 1fr 76px 22px;
    align-items: center;
    gap: 10px;
    padding: 9px 14px;
    border-bottom: 1px solid var(--hair);
    cursor: pointer;
    transition: background 0.16s var(--ease);
    position: relative;
  }
  .lb-row:hover, .lb-row.focus { background: var(--paper-2); }
  .lb-row.focus::before {
    content: ''; position: absolute; left: 0; top: 10%; bottom: 10%;
    width: 3px; background: var(--signal); border-radius: 0 2px 2px 0;
  }
  .lb-pos {
    font-family: 'JetBrains Mono', monospace; font-size: 13px;
    font-weight: 800; color: var(--ink); text-align: right;
  }
  .lb-tag {
    width: 14px; height: 14px; border-radius: 3px;
    background: var(--car); border: 1px solid #00000018;
  }
  .lb-meta { display: flex; flex-direction: column; gap: 3px; min-width: 0; }
  .lb-name {
    font-family: 'JetBrains Mono', monospace; font-size: 13px; font-weight: 700;
    color: var(--ink); display: flex; gap: 8px; align-items: center;
  }
  .lb-name .ovt { color: var(--ink-3); font-size: 10px; }
  .lb-mini { display: flex; gap: 6px; align-items: center; }
  .lb-mini svg { display: block; }
  .lb-status { justify-self: end; }
  .lb-chev {
    color: var(--ink-3); transition: transform 0.18s var(--ease);
    font-family: 'JetBrains Mono', monospace; font-size: 14px; font-weight: 800;
    text-align: center;
  }
  .lb-row.open .lb-chev { transform: rotate(90deg); }
  .lb-detail {
    grid-column: 1 / -1;
    display: none;
    grid-template-columns: 1fr 1fr;
    gap: 8px 18px;
    padding: 10px 4px 4px;
    font-family: 'JetBrains Mono', monospace; font-size: 12px; font-weight: 600;
  }
  .lb-row.open .lb-detail { display: grid; }
  .lb-detail .kv {
    display: flex; justify-content: space-between; gap: 8px;
    padding: 2px 0;
    border-bottom: 1px dashed var(--hair);
  }
  .lb-detail .kv span:first-child { color: var(--ink-3); }
  .lb-detail .kv b { font-weight: 800; color: var(--ink); }
  .lb-detail .full { grid-column: 1 / -1; }
  .pgrid {
    display: grid; grid-template-columns: repeat(8, 1fr); gap: 2px;
    margin-top: 4px;
  }
  .pgrid div {
    aspect-ratio: 1;
    background: var(--ink);
    border-radius: 2px;
  }

  /* ---------- SMALL MULTIPLES ---------- */
  .charts-card { grid-column: 1 / -1; }
  .charts-grid {
    display: grid;
    grid-template-columns: repeat(4, 1fr);
    gap: 0;
  }
  @media (max-width: 1100px) { .charts-grid { grid-template-columns: repeat(2, 1fr); } }
  .chart-tile {
    padding: 12px 14px 10px;
    border-right: 1px solid var(--hair);
    border-bottom: 1px solid var(--hair);
    background: var(--card);
    position: relative;
    cursor: crosshair;
  }
  .chart-tile:nth-child(4n) { border-right: none; }
  @media (max-width: 1100px) {
    .chart-tile:nth-child(4n) { border-right: 1px solid var(--hair); }
    .chart-tile:nth-child(2n) { border-right: none; }
  }
  .chart-head {
    display: flex; justify-content: space-between; align-items: baseline;
    margin-bottom: 6px;
  }
  .chart-title {
    font-family: 'Inter Tight', sans-serif; font-size: 12px;
    letter-spacing: 0.10em; text-transform: uppercase;
    color: var(--ink); font-weight: 800;
  }
  .chart-unit {
    font-family: 'JetBrains Mono', monospace; font-size: 11px; font-weight: 700;
    color: var(--ink-3);
  }
  .chart-stat {
    font-family: 'Inter Tight', sans-serif; font-weight: 800;
    font-size: 36px; line-height: 1; color: var(--ink);
    margin: 4px 0 6px;
    letter-spacing: -0.025em;
  }
  .chart-stat .delta {
    font-family: 'JetBrains Mono', monospace;
    font-size: 11px; font-weight: 700;
    color: var(--ink-3); margin-left: 8px;
  }
  .chart-svg { width: 100%; height: 70px; display: block; }
  .chart-svg .axis line { stroke: var(--hair-2); stroke-dasharray: 2 3; stroke-width: 0.5; }
  .chart-svg .cursor { stroke: var(--ink); stroke-width: 1; opacity: 0.5; }
  .chart-svg path.series, .chart-svg polyline.series {
    fill: none; stroke-width: 1.2; opacity: 0.62; transition: opacity 0.15s;
  }
  .chart-svg polyline.series.focus { opacity: 1; stroke-width: 1.8; }
  .chart-svg polyline.series.dim   { opacity: 0.10; }

  /* ---------- EVENTS LANE ---------- */
  .events-card { grid-column: 1 / -1; }
  .events-tools { display: flex; gap: 6px; align-items: center; }
  .events-tracks { display: flex; flex-direction: column; }
  .events-track {
    display: grid;
    grid-template-columns: 168px 1fr 96px;
    align-items: center;
    gap: 18px;
    padding: 14px 18px;
    border-bottom: 1px solid var(--hair);
    background: var(--card);
    transition: opacity 0.16s var(--ease);
  }
  .events-track:last-child { border-bottom: none; }
  .events-track.dim { opacity: 0.22; }
  .events-track-head { display: flex; flex-direction: column; gap: 6px; }
  .events-track-title {
    font-family: 'Inter Tight', sans-serif; font-size: 12px;
    letter-spacing: 0.10em; text-transform: uppercase;
    color: var(--ink); font-weight: 800;
    display: inline-flex; align-items: center; gap: 8px;
  }
  .events-track-title .dot {
    width: 8px; height: 8px; border-radius: 50%;
    box-shadow: 0 0 0 3px #00000010;
  }
  .events-track-rate {
    font-family: 'JetBrains Mono', monospace; font-size: 11px; font-weight: 700;
    color: var(--ink-3);
  }
  .events-track-svg { width: 100%; height: 58px; display: block; cursor: crosshair; }
  .events-track-stat {
    font-family: 'Inter Tight', sans-serif; font-weight: 900;
    font-size: 30px; line-height: 1; color: var(--ink);
    letter-spacing: -0.03em; text-align: right;
  }
  .events-list {
    max-height: 260px; overflow: auto;
    font-family: 'JetBrains Mono', monospace; font-size: 13px; font-weight: 600;
  }
  .ev-row {
    display: grid;
    grid-template-columns: 64px 100px 1fr 60px;
    gap: 14px; padding: 9px 18px;
    border-bottom: 1px dashed var(--hair);
    align-items: center;
  }
  .ev-row:hover { background: var(--paper-2); }
  .ev-t { color: var(--ink-3); }
  .ev-type {
    font-family: 'Inter Tight', sans-serif;
    font-size: 11px; font-weight: 800; letter-spacing: 0.08em; text-transform: uppercase;
    padding: 3px 8px; border-radius: 4px;
    background: var(--paper-3); color: var(--ink-2);
    text-align: center;
  }
  .ev-type.overtake { background: #d2dec9; color: #2c4520; }
  .ev-type.car_hit  { background: #e7c1b5; color: #6e2410; }
  .ev-type.wall_hit { background: #ead7b0; color: #644413; }
  .ev-detail { color: var(--ink-2); }
  .ev-detail b { color: var(--ink); }
  .ev-mini { justify-self: end; color: var(--ink-3); font-size: 11px; font-weight: 700; }
"""

_RACE_DETAIL_BODY = r"""
<!-- KPI ribbon -->
<section class="kpis relative" id="kpis">
  <span class="loading-badge" id="race-loading"><span class="spinner"></span>loading race…</span>
</section>

<!-- Scrubber -->
<section class="scrub-strip">
  <div class="scrub-row">
    <button class="play-btn" id="playBtn" aria-label="play">
      <svg viewBox="0 0 12 12"><polygon points="2,1 11,6 2,11" fill="currentColor"/></svg>
    </button>
    <div class="time-readout"><b id="tNow">0.00</b> / <span id="tDur">0.00</span> s</div>
    <div class="scrub-track" id="scrub">
      <svg viewBox="0 0 1000 56" preserveAspectRatio="none" id="scrubSvg"></svg>
    </div>
    <div class="speed-pick" id="speedPick">
      <button data-s="0.5">0.5×</button>
      <button data-s="1" class="on">1×</button>
      <button data-s="2">2×</button>
      <button data-s="4">4×</button>
    </div>
  </div>
</section>

<main class="race-grid">
  <section class="card map-card">
    <div class="card-head">
      <h2>circuit · top-down</h2>
      <div class="sub" id="mapSub">x,z space · drag dots to inspect</div>
    </div>
    <div class="map-wrap" id="mapWrap">
      <svg viewBox="0 0 800 600" id="mapSvg"></svg>
      <div class="map-overlay">
        <div class="corner bl"><span>start/finish</span><span id="cornerSF">—</span></div>
        <div class="corner br"><span id="cornerCircuit">—</span><span id="cornerLen">length n/a</span></div>
      </div>
      <div class="car-tip" id="carTip"></div>
    </div>
    <div class="map-legend">
      <span class="swatch">trace</span>
      <span class="swatch active">current</span>
      <span class="swatch dead">eliminated</span>
      <span class="spacer"></span>
      <span id="legendCount">—</span>
    </div>
  </section>

  <section class="card">
    <div class="card-head">
      <h2>drivers · final classification</h2>
      <div class="sub" id="lbCount">—</div>
    </div>
    <div class="lb-tools">
      <input type="text" id="lbSearch" placeholder="filter by car_id, status, personality…">
      <select id="lbSort">
        <option value="pos">sort · position</option>
        <option value="reward">sort · reward</option>
        <option value="hits">sort · car hits</option>
        <option value="ovt">sort · overtakes</option>
      </select>
    </div>
    <div class="lb" id="lb"></div>
  </section>

  <section class="card charts-card">
    <div class="card-head">
      <h2>small multiples · per car over t</h2>
      <div class="sub">hover tile · cursor syncs · click driver to isolate</div>
    </div>
    <div class="charts-grid" id="chartsGrid"></div>
  </section>

  <section class="card events-card">
    <div class="card-head">
      <h2>events · <span id="evTotal">0</span> total</h2>
      <div class="events-tools" id="evToolBar">
        <span class="pill-toggle on" data-e="overtake"><span class="dot overtake"></span>overtake · <b id="cOvt">0</b></span>
        <span class="pill-toggle on" data-e="car_hit"><span class="dot car_hit"></span>car_hit · <b id="cHit">0</b></span>
        <span class="pill-toggle on" data-e="wall_hit"><span class="dot wall_hit"></span>wall_hit · <b id="cWall">0</b></span>
      </div>
    </div>
    <div class="events-tracks" id="evTracks">
      <div class="events-track" data-e="overtake">
        <div class="events-track-head">
          <div class="events-track-title"><span class="dot" style="background:#6f8e6a"></span>overtake</div>
          <div class="events-track-rate"><span id="rOvt">0.0</span>/s avg</div>
        </div>
        <svg class="events-track-svg" viewBox="0 0 1000 58" preserveAspectRatio="none" data-lane="overtake"></svg>
        <div class="events-track-stat" id="sOvt">0</div>
      </div>
      <div class="events-track" data-e="car_hit">
        <div class="events-track-head">
          <div class="events-track-title"><span class="dot" style="background:#c84a2c"></span>car_hit</div>
          <div class="events-track-rate"><span id="rHit">0.0</span>/s avg</div>
        </div>
        <svg class="events-track-svg" viewBox="0 0 1000 58" preserveAspectRatio="none" data-lane="car_hit"></svg>
        <div class="events-track-stat" id="sHit">0</div>
      </div>
      <div class="events-track" data-e="wall_hit">
        <div class="events-track-head">
          <div class="events-track-title"><span class="dot" style="background:#b88840"></span>wall_hit</div>
          <div class="events-track-rate"><span id="rWall">0.0</span>/s avg</div>
        </div>
        <svg class="events-track-svg" viewBox="0 0 1000 58" preserveAspectRatio="none" data-lane="wall_hit"></svg>
        <div class="events-track-stat" id="sWall">0</div>
      </div>
    </div>
    <div class="events-list" id="evList"></div>
  </section>
</main>

<script>
const PERSONALITY_NAMES = ['TirePres','FuelEco','Pass','Defend','Risk','PeakPace','Rsv','Rsv'];
const HUE_WHEEL = [25,60,110,160,210,260,310,350];
const EVENT_COLORS = { overtake: '#6f8e6a', car_hit: '#c84a2c', wall_hit: '#b88840' };

const $ = s => document.querySelector(s);
const fmt = (n, d=2) => Number(n).toFixed(d);
const racePathId = () => decodeURIComponent(location.pathname.split('/races/')[1] || '');
function carHue(i) { return `oklch(0.62 0.13 ${HUE_WHEEL[i%8]})`; }
function carClass(i) { return 'c' + (i % 8); }

let RACE = null;
let DRIVERS = [];          // normalised list, indexed by display order
let EVENTS = [];           // flat events array
let OVERLAY = null;
let BBOX = null;
let DURATION = 0;
let HZ = 5;

const state = {
  t: 0,
  playing: false,
  speed: 1,
  focusCar: null,
  enabledEvents: new Set(['overtake','car_hit','wall_hit']),
  lbSort: 'pos',
  lbSearch: '',
  scaleSx: null,
  scaleSy: null,
};

// Per-frame caches built once on init / scrub-driven re-render. tick()
// reads from these instead of rebuilding strings + DOM each RAF frame.
const cache = {
  carNodes: [],        // [{ g, idx, driver }] — stable <g> elements inside #carsG
  chartTiles: [],      // [{ key, c, cursorEl, statEl, sx, sy, special }]
  eventCursors: [],    // [<line> elements, one per lane]
  carStateAtT: null,   // Map<carId, sample> — invalidated each tick
};

function clearCarStateCache() { cache.carStateAtT = null; }
function carStateMap(t) {
  if (cache.carStateAtT) return cache.carStateAtT;
  const m = new Map();
  DRIVERS.forEach(d => { m.set(d.car_id, carStateAt(d, t)); });
  cache.carStateAtT = m;
  return m;
}

/* sample interpolation (matches design: linear between samples) */
function carStateAt(driver, t) {
  const s = driver.samples;
  if (!s.length) return null;
  if (t <= s[0].t) return s[0];
  if (t >= s[s.length-1].t) {
    const last = s[s.length-1];
    const ended = (driver.end_state || {}).reason !== 'Finished' && (driver.end_state || {}).reason !== 'Success'
                  && (driver.end_state || {}).reason !== 'finish' && (driver.end_state || {}).reason !== 'lap_done';
    if (last.t < DURATION - 0.05 && ended) return { ...last, dead: true };
    return last;
  }
  // binary search
  let lo = 0, hi = s.length - 1;
  while (lo < hi) {
    const mid = (lo + hi + 1) >> 1;
    if (s[mid].t <= t) lo = mid; else hi = mid - 1;
  }
  const a = s[lo], b = s[Math.min(lo+1, s.length-1)];
  if (a === b) return a;
  const u = (t - a.t) / Math.max(1e-6, (b.t - a.t));
  const pa = a.pos || {}, pb = b.pos || {};
  return {
    t,
    pos: { x: (pa.x||0) + u*((pb.x||0)-(pa.x||0)),
           y: 0,
           z: (pa.z||0) + u*((pb.z||0)-(pa.z||0)) },
    speed:  (a.speed||0)  + u*((b.speed||0)  - (a.speed||0)),
    fuel_l: (a.fuel_l||0) + u*((b.fuel_l||0) - (a.fuel_l||0)),
    tire_l: (a.tire_l||0) + u*((b.tire_l||0) - (a.tire_l||0)),
    tire_r: (a.tire_r||0) + u*((b.tire_r||0) - (a.tire_r||0)),
    draft:  (a.draft||0)  + u*((b.draft||0)  - (a.draft||0)),
    heading: a.heading || 0,
    lap:    a.lap || 0,
    sector: a.sector || 0,
  };
}

/* ---------- HEADER META ---------- */
function renderHeaderMeta() {
  const m = document.getElementById('hdrMeta');
  if (!m) return;
  const captured = (RACE.captured_at_utc || '').replace('T', ' ').slice(0, 19);
  m.innerHTML = `
    <dl><dt>race</dt><dd><b>${RACE.race_id || '?'}</b></dd></dl>
    <dl><dt>circuit</dt><dd>${(RACE.circuit||{}).id || '?'}</dd></dl>
    <dl><dt>episode</dt><dd>${RACE.episode_index || 0} · stage ${RACE.stage_id || 0}</dd></dl>
    <dl><dt>duration</dt><dd>${fmt(RACE.duration_s,2)}s · ${HZ} Hz</dd></dl>
    <dl><dt>captured</dt><dd>${captured || '—'}</dd></dl>
    <dl><dt>status</dt><dd><span class="pill">env pid ${RACE.env_pid || 0}</span></dd></dl>`;
}

/* ---------- KPI RIBBON ---------- */
function renderKPIs() {
  const finished = DRIVERS.filter(d => {
    const r = (d.end_state||{}).reason; return r === 'Finished' || r === 'Success' || r === 'finish' || r === 'lap_done';
  }).length;
  const failures = DRIVERS.filter(d => ((d.end_state||{}).reason || '').toLowerCase().indexOf('fail') === 0).length;
  const wrecks = DRIVERS.filter(d => (d.end_state||{}).reason === 'Failure_Wreck' || (d.end_state||{}).reason === 'wall_max').length;
  const offtrack = DRIVERS.filter(d => (d.end_state||{}).reason === 'Failure_OffTrack' || (d.end_state||{}).reason === 'off_track').length;
  const eliminated = DRIVERS.length - finished;
  const rewards = DRIVERS.map(d => (d.end_state||{}).cumulative_reward || 0);
  const avgReward = rewards.length ? rewards.reduce((s,v)=>s+v,0) / rewards.length : 0;
  const std = rewards.length ? Math.sqrt(rewards.reduce((s,v)=>s+(v-avgReward)*(v-avgReward),0) / rewards.length) : 0;
  const totalOvt = EVENTS.filter(e => e.type === 'overtake').length;
  const hits = EVENTS.filter(e => e.type === 'car_hit');
  const peakImpact = hits.length ? Math.max(...hits.map(e => e.impact_speed || 0)) : 0;
  const totalHits = hits.length;
  const steps = Math.floor(DURATION * HZ);
  const items = [
    { label: 'drivers',    value: DRIVERS.length, sub: `${finished} finished · ${eliminated} eliminated` },
    { label: 'duration',   value: fmt(DURATION,2)+'s', sub: `${HZ} Hz · ${steps} steps` },
    { label: 'overtakes',  value: totalOvt, sub: `${(totalOvt/Math.max(DURATION,0.001)).toFixed(1)}/s avg` },
    { label: 'car hits',   value: totalHits, sub: `peak ${fmt(peakImpact,2)} m/s` },
    { label: 'avg reward', value: fmt(avgReward,1), sub: `σ ${fmt(std,1)}` },
    { label: 'incidents',  value: wrecks + offtrack, sub: `${wrecks} wreck · ${offtrack} off-track` },
  ];
  $('#kpis').innerHTML = items.map(k => `
    <div class="kpi">
      <div class="kpi-label">${k.label}</div>
      <div class="kpi-value">${k.value}</div>
      <div class="kpi-sub">${k.sub}</div>
    </div>`).join('');
}

/* ---------- SCRUBBER + EVENT DENSITY ---------- */
function renderScrub() {
  const svg = $('#scrubSvg');
  const W = 1000, H = 56;
  const bins = 80;
  const binW = W / bins;
  const densityOvt = new Array(bins).fill(0);
  const densityHit = new Array(bins).fill(0);
  const densityWall = new Array(bins).fill(0);
  EVENTS.forEach(e => {
    const b = Math.min(bins-1, Math.max(0, Math.floor((e.t / Math.max(DURATION, 0.001)) * bins)));
    if (e.type === 'overtake') densityOvt[b]++;
    else if (e.type === 'car_hit') densityHit[b]++;
    else if (e.type === 'wall_hit') densityWall[b]++;
  });
  const maxD = Math.max(1, ...densityOvt, ...densityHit, ...densityWall);
  const bar = (arr, color, yOff, scale) => arr.map((v,i) => {
    const h = (v/maxD)*16*scale;
    return `<rect x="${i*binW+0.5}" y="${yOff-h}" width="${binW-1}" height="${h}" fill="${color}" opacity="0.65"/>`;
  }).join('');
  svg.innerHTML = `
    <rect x="0" y="46" width="${W}" height="2" fill="#d4cdba"/>
    ${bar(densityOvt,  '#6f8e6a', 44, 1)}
    ${bar(densityHit,  '#c84a2c', 28, 0.9)}
    ${bar(densityWall, '#b88840', 12, 0.7)}
    <g id="scrubHead">
      <line x1="0" y1="0" x2="0" y2="56" stroke="#1f1c17" stroke-width="2"/>
      <circle cx="0" cy="48" r="6" fill="#1f1c17"/>
      <circle cx="0" cy="48" r="3" fill="#efece4"/>
    </g>`;
  updateScrubHead();
  $('#tDur').textContent = fmt(DURATION, 2);
}
function updateScrubHead() {
  const x = (state.t / Math.max(DURATION,0.001)) * 1000;
  const head = document.querySelector('#scrubSvg #scrubHead');
  if (head) head.setAttribute('transform', `translate(${x},0)`);
  $('#tNow').textContent = fmt(state.t, 2);
}
let dragging = false;
function bindScrub() {
  $('#scrub').addEventListener('mousedown', e => { dragging = true; onScrub(e); });
  window.addEventListener('mousemove', e => dragging && onScrub(e));
  window.addEventListener('mouseup', () => dragging = false);
}
function onScrub(e) {
  const r = $('#scrub').getBoundingClientRect();
  const u = Math.max(0, Math.min(1, (e.clientX - r.left) / r.width));
  state.t = u * DURATION;
  state.playing = false; setPlayIcon();
  tick();
}
function setPlayIcon() {
  $('#playBtn').innerHTML = state.playing
    ? '<svg viewBox="0 0 12 12"><rect x="2" y="2" width="3" height="8" fill="currentColor"/><rect x="7" y="2" width="3" height="8" fill="currentColor"/></svg>'
    : '<svg viewBox="0 0 12 12"><polygon points="2,1 11,6 2,11" fill="currentColor"/></svg>';
}
function bindPlay() {
  $('#playBtn').addEventListener('click', () => {
    state.playing = !state.playing;
    setPlayIcon();
    if (state.playing) lastTickRT = performance.now();
  });
  document.querySelectorAll('#speedPick button').forEach(b => b.addEventListener('click', () => {
    state.speed = parseFloat(b.dataset.s);
    document.querySelectorAll('#speedPick button').forEach(x => x.classList.toggle('on', x === b));
  }));
  window.addEventListener('keydown', e => {
    if (e.code === 'Space' && e.target.tagName !== 'INPUT') { e.preventDefault(); $('#playBtn').click(); }
  });
}
let lastTickRT = performance.now();
function loop(now) {
  if (state.playing) {
    const dt = (now - lastTickRT) / 1000;
    state.t += dt * state.speed;
    if (state.t >= DURATION) { state.t = DURATION; state.playing = false; setPlayIcon(); }
    tick();
  }
  lastTickRT = now;
  requestAnimationFrame(loop);
}

/* ---------- TRACK MAP ---------- */
function renderMap() {
  const svg = $('#mapSvg');
  if (!BBOX) {
    svg.innerHTML = '<text x="20" y="30" font-family="JetBrains Mono" font-size="12" fill="#837c72">no bbox — circuit overlay unavailable</text>';
    return;
  }
  const pad = 30;
  const w = (BBOX.maxX - BBOX.minX) || 1;
  const h = (BBOX.maxY - BBOX.minY) || 1;
  const sc = Math.min((800 - 2*pad) / w, (600 - 2*pad) / h);
  const ox = (800 - sc*w) / 2;
  const oy = (600 - sc*h) / 2;
  const sx = v => ox + (v - BBOX.minX) * sc;
  const sz = v => 600 - oy - (v - BBOX.minY) * sc;
  state.scaleSx = sx; state.scaleSy = sz;

  // grid lines
  const grid = [];
  for (let g = 0; g <= 10; g++) {
    grid.push(`<line x1="${(g/10)*800}" y1="0" x2="${(g/10)*800}" y2="600" stroke="#d4cdba" stroke-width="0.5" stroke-dasharray="2 4" opacity="0.6"/>`);
    grid.push(`<line x1="0" y1="${(g/10)*600}" x2="800" y2="${(g/10)*600}" stroke="#d4cdba" stroke-width="0.5" stroke-dasharray="2 4" opacity="0.6"/>`);
  }
  // overlay (real circuit geometry)
  let overlay = '';
  if (OVERLAY) {
    if (OVERLAY.kerbs) {
      for (const q of OVERLAY.kerbs) {
        const pts = [[q[0],q[1]],[q[2],q[3]],[q[4],q[5]],[q[6],q[7]]]
          .map(p => `${sx(p[0]).toFixed(1)},${sz(p[1]).toFixed(1)}`).join(' ');
        overlay += `<polygon points="${pts}" fill="#c84a2c" fill-opacity="0.28"/>`;
      }
    }
    if (OVERLAY.walls) {
      for (const w of OVERLAY.walls) {
        overlay += `<line x1="${sx(w[0]).toFixed(1)}" y1="${sz(w[1]).toFixed(1)}" x2="${sx(w[2]).toFixed(1)}" y2="${sz(w[3]).toFixed(1)}" stroke="#4a4540" stroke-width="0.9" stroke-linecap="round"/>`;
      }
    }
    if (OVERLAY.anchors && OVERLAY.anchors.length > 1) {
      const a = OVERLAY.anchors;
      let d = '';
      for (let i = 0; i < a.length; i++) {
        const [x,y] = [sx(a[i][0]).toFixed(1), sz(a[i][1]).toFixed(1)];
        d += (i === 0 ? `M${x},${y}` : ` L${x},${y}`);
      }
      d += ' Z';
      overlay += `<path d="${d}" fill="none" stroke="#1f1c17" stroke-width="1.6" stroke-linejoin="round"/>`;
      // start gate marker at lapStartIdx. -1 means the JSON predates the
      // canonical LapStartAnchorIndex field — skip the START label entirely
      // so it never lies. Re-run Scenario Browser → Circuit Barrier Re-Export
      // to bake the field into every stage_*/ JSON.
      const li = (OVERLAY.lapStartIdx != null) ? OVERLAY.lapStartIdx : -1;
      if (li >= 0 && li < a.length) {
        const sa = a[li];
        const sb = a[(li+1) % a.length];
        const sax = sx(sa[0]), say = sz(sa[1]);
        const sbx = sx(sb[0]), sby = sz(sb[1]);
        let dx = sbx - sax, dy = sby - say;
        const len = Math.hypot(dx, dy) || 1; dx /= len; dy /= len;
        const tipX = sax + dx*14, tipY = say + dy*14;
        const blX = sax + (-dy)*5, blY = say + (dx)*5;
        const brX = sax - (-dy)*5, brY = say - (dx)*5;
        overlay += `<polygon points="${tipX.toFixed(1)},${tipY.toFixed(1)} ${blX.toFixed(1)},${blY.toFixed(1)} ${brX.toFixed(1)},${brY.toFixed(1)}" fill="#1f1c17"/>`;
        overlay += `<circle cx="${sax.toFixed(1)}" cy="${say.toFixed(1)}" r="3.2" fill="#1f1c17"/>`;
        overlay += `<text x="${sax.toFixed(1)}" y="${(say-12).toFixed(1)}" text-anchor="middle" font-family="JetBrains Mono" font-size="11" font-weight="700" fill="#1f1c17" letter-spacing="0.08em">START</text>`;
        $('#cornerSF').textContent = `x=${sa[0].toFixed(0)} z=${sa[1].toFixed(0)}`;
      } else {
        $('#cornerSF').textContent = 'missing — rebake';
      }
    }
  }
  // driver traces
  let traces = '';
  DRIVERS.forEach((d, i) => {
    if (!d.samples || !d.samples.length) return;
    const pts = d.samples.filter(s => s.pos).map(s => `${sx(s.pos.x).toFixed(1)},${sz(s.pos.z).toFixed(1)}`).join(' ');
    if (!pts) return;
    traces += `<polyline points="${pts}" fill="none" stroke="${carHue(i)}" stroke-width="1.2" opacity="0.30" data-carid="${d.car_id}"/>`;
  });
  svg.innerHTML = grid.join('') + overlay + traces + '<g id="carsG"></g>';
  $('#cornerCircuit').textContent = ((RACE.circuit||{}).id) || '—';
  buildCarDots();
}

// Build one stable <g class="carDot"> per driver and bind listeners once.
// updateCars then only mutates transform/visibility — no innerHTML churn.
function buildCarDots() {
  const g = document.querySelector('#mapSvg #carsG');
  if (!g) return;
  // Use a namespaced helper for SVG elements (createElement won't work).
  const NS = 'http://www.w3.org/2000/svg';
  g.innerHTML = '';
  cache.carNodes = DRIVERS.map((d, i) => {
    const node = document.createElementNS(NS, 'g');
    node.setAttribute('class', 'carDot');
    node.setAttribute('data-carid', String(d.car_id));
    node.setAttribute('data-idx', String(i));
    node.style.cursor = 'pointer';
    const col = carHue(i);
    const ring = document.createElementNS(NS, 'circle');
    ring.setAttribute('r', '10');
    ring.setAttribute('fill', 'none');
    ring.setAttribute('stroke', '#1f1c17');
    ring.setAttribute('stroke-width', '1');
    ring.setAttribute('visibility', 'hidden');
    const body = document.createElementNS(NS, 'circle');
    body.setAttribute('r', '5');
    body.setAttribute('fill', col);
    body.setAttribute('stroke', '#1f1c17');
    body.setAttribute('stroke-width', '0.8');
    const label = document.createElementNS(NS, 'text');
    label.setAttribute('x', '0');
    label.setAttribute('y', '-13');
    label.setAttribute('text-anchor', 'middle');
    label.setAttribute('font-family', 'JetBrains Mono');
    label.setAttribute('font-size', '11');
    label.setAttribute('font-weight', '800');
    label.setAttribute('fill', '#1f1c17');
    label.setAttribute('visibility', 'hidden');
    label.textContent = 'car_' + d.car_id;
    node.appendChild(ring);
    node.appendChild(body);
    node.appendChild(label);
    g.appendChild(node);

    node.addEventListener('mouseenter', e => showTip(e, d.car_id, i));
    node.addEventListener('mouseleave', () => $('#carTip').classList.remove('on'));
    node.addEventListener('click', () => {
      state.focusCar = state.focusCar === d.car_id ? null : d.car_id;
      renderLB();
      renderCharts();  // rebuild to refresh polyline focus/dim classes
      applyCarFocus();
    });
    return { g: node, ring, body, label, idx: i, driver: d };
  });
  applyCarFocus();
}

// Toggle focus rendering on existing dot nodes — avoids rebuilding.
function applyCarFocus() {
  cache.carNodes.forEach(n => {
    const focus = state.focusCar === n.driver.car_id;
    n.ring.setAttribute('visibility', focus ? 'visible' : 'hidden');
    n.label.setAttribute('visibility', focus ? 'visible' : 'hidden');
    n.body.setAttribute('r', focus ? '7' : '5');
  });
}

function updateCars() {
  if (!state.scaleSx || !cache.carNodes.length) return;
  const states = carStateMap(state.t);
  let activeCount = 0;
  for (const n of cache.carNodes) {
    const st = states.get(n.driver.car_id);
    if (!st || st.dead || !st.pos) {
      n.g.setAttribute('visibility', 'hidden');
      continue;
    }
    n.g.setAttribute('visibility', 'visible');
    activeCount++;
    const x = state.scaleSx(st.pos.x);
    const z = state.scaleSy(st.pos.z);
    n.g.setAttribute('transform', `translate(${x.toFixed(1)},${z.toFixed(1)})`);
  }
  $('#legendCount').textContent = `${DRIVERS.length} drivers · ${activeCount} active at t=${fmt(state.t, 2)}`;
}

function showTip(e, carId, idx) {
  const d = DRIVERS.find(x => x.car_id === carId);
  if (!d) return;
  const st = carStateAt(d, state.t);
  if (!st) return;
  const tip = $('#carTip');
  const wrap = $('#mapWrap').getBoundingClientRect();
  const dot = e.currentTarget.getBoundingClientRect();
  tip.style.left = (dot.left + dot.width/2 - wrap.left) + 'px';
  tip.style.top  = (dot.top - wrap.top) + 'px';
  tip.style.setProperty('--car', carHue(idx));
  const reason = ((d.end_state||{}).reason || '').replace('Failure_','');
  tip.innerHTML = `
    <div class="tip-name">car_${carId}</div>
    <dl>
      <dt>speed</dt><dd>${fmt(st.speed||0, 2)} m/s</dd>
      <dt>fuel</dt><dd>${fmt(st.fuel_l||0, 2)} L</dd>
      <dt>tire L/R</dt><dd>${fmt(st.tire_l||0, 2)} / ${fmt(st.tire_r||0, 2)}</dd>
      <dt>draft</dt><dd>${fmt(st.draft||0, 2)}</dd>
      <dt>lap · sec</dt><dd>L${st.lap||0} · S${(st.sector||0)+1}</dd>
      <dt>final</dt><dd>P${(d.end_state||{}).final_position || '-'} · ${reason || '—'}</dd>
    </dl>`;
  tip.classList.add('on');
}

/* ---------- LEADERBOARD ---------- */
function spark(values, width=58, height=14, max=null, fill=false) {
  if (!values.length) return '';
  const mx = max != null ? max : Math.max(...values, 0.001);
  const mn = Math.min(...values, 0);
  const range = (mx - mn) || 1;
  const pts = values.map((v, i) => {
    const x = (i/Math.max(values.length-1,1)) * width;
    const y = height - ((v - mn) / range) * height;
    return `${x.toFixed(1)},${y.toFixed(1)}`;
  });
  return `<svg width="${width}" height="${height}" viewBox="0 0 ${width} ${height}">
    ${fill ? `<polygon points="${pts.join(' ')} ${width},${height} 0,${height}" fill="var(--car)" opacity="0.18"/>` : ''}
    <polyline points="${pts.join(' ')}" fill="none" stroke="var(--car)" stroke-width="1.2"/>
  </svg>`;
}

function renderLB() {
  const lb = $('#lb');
  // Race-scoped finish_position is the authoritative podium order for
  // drivers who completed lap_target this race. Fall back to live final
  // position for eliminated drivers + legacy races.
  const podiumOf = d => {
    const es = d.end_state || {};
    return es.finish_position && es.finish_position > 0
      ? es.finish_position
      : (es.final_position || 999);
  };
  const sorters = {
    pos:    (a,b) => podiumOf(a) - podiumOf(b),
    reward: (a,b) => ((b.end_state||{}).cumulative_reward || 0) - ((a.end_state||{}).cumulative_reward || 0),
    hits:   (a,b) => ((b.end_state||{}).car_hits || 0) - ((a.end_state||{}).car_hits || 0),
    ovt:    (a,b) => ((b.end_state||{}).overtakes_made || 0) - ((a.end_state||{}).overtakes_made || 0),
  };
  let list = DRIVERS.slice();
  list.sort(sorters[state.lbSort]);
  if (state.lbSearch) {
    const q = state.lbSearch.toLowerCase();
    list = list.filter(d =>
      (d.display_name || '').toLowerCase().includes(q) ||
      String(d.car_id) === q ||
      ((d.end_state||{}).reason || '').toLowerCase().includes(q)
    );
  }
  $('#lbCount').textContent = `${list.length} of ${DRIVERS.length} drivers`;
  lb.innerHTML = list.map((d) => {
    const idx = DRIVERS.indexOf(d);
    const es = d.end_state || {};
    const speeds = (d.samples || []).map(s => s.speed || 0);
    const tires  = (d.samples || []).map(s => Math.max(s.tire_l || 0, s.tire_r || 0));
    const fuels  = (d.samples || []).map(s => s.fuel_l || 0);
    const reason = es.reason || '';
    const focus = state.focusCar === d.car_id;
    const personality = (d.personality || []).map((p, j) =>
      `<div style="background:rgba(31,28,23,${(0.08 + p*0.85).toFixed(2)})" title="${PERSONALITY_NAMES[j]||'dim'+j}: ${p.toFixed(3)}"></div>`
    ).join('');
    const podium = es.finish_position && es.finish_position > 0
      ? `P${es.finish_position}<sup>F</sup>`
      : (es.final_position ? `P${es.final_position}` : 'DNF');
    return `<div class="lb-row ${focus ? 'focus' : ''} ${carClass(idx)}" data-carid="${d.car_id}">
        <div class="lb-pos">${podium}</div>
        <div class="lb-tag" style="background:${carHue(idx)}"></div>
        <div class="lb-meta">
          <div class="lb-name">
            <span>${d.display_name || ('car_' + d.car_id)}</span>
            <span class="ovt">+${es.overtakes_made||0}/-${es.overtakes_lost||0}</span>
          </div>
          <div class="lb-mini">
            ${spark(speeds, 56, 14, 30)}
            ${spark(fuels,  44, 14, 100, true)}
            ${spark(tires,  44, 14, 1, true)}
          </div>
        </div>
        <div class="lb-status"><span class="status" data-r="${reason}">${(reason || '?').replace('Failure_','').toLowerCase() || '?'}</span></div>
        <div class="lb-chev">›</div>
        <div class="lb-detail">
          <div class="kv"><span>reward</span><b>${fmt(es.cumulative_reward||0,2)}</b></div>
          <div class="kv"><span>laps</span><b>${es.laps_completed||0}</b></div>
          <div class="kv"><span>fuel</span><b>${fmt(es.fuel_l_start||0,1)} → ${fmt(es.fuel_l_final||0,1)} L</b></div>
          <div class="kv"><span>tire L/R</span><b>${fmt(es.tire_l_final||0,2)} / ${fmt(es.tire_r_final||0,2)}${es.punctured_l||es.punctured_r ? ' · puncture' : ''}</b></div>
          <div class="kv"><span>wall · car</span><b>${es.wall_hits||0} · ${es.car_hits||0}</b></div>
          <div class="kv"><span>overtakes ±</span><b>+${es.overtakes_made||0} / −${es.overtakes_lost||0}</b></div>
          <div class="full">
            <span style="color:var(--ink-3)">personality vector · 8 dims</span>
            <div class="pgrid">${personality}</div>
          </div>
        </div>
      </div>`;
  }).join('');
  lb.querySelectorAll('.lb-row').forEach(row => {
    row.addEventListener('click', (e) => {
      if (e.target.tagName === 'INPUT' || e.target.tagName === 'SELECT') return;
      const carId = parseInt(row.dataset.carid);
      // Toggle expanded view + focus.
      const wasOpen = row.classList.contains('open');
      row.classList.toggle('open', !wasOpen);
      state.focusCar = (state.focusCar === carId && wasOpen) ? null : carId;
      // Update focus class across rows.
      lb.querySelectorAll('.lb-row').forEach(r => {
        const id = parseInt(r.dataset.carid);
        r.classList.toggle('focus', id === state.focusCar);
      });
      renderCharts();
      applyCarFocus();
      updateCars();
    });
  });
}

function bindLB() {
  $('#lbSearch').addEventListener('input', e => { state.lbSearch = e.target.value; renderLB(); });
  $('#lbSort').addEventListener('change', e => { state.lbSort = e.target.value; renderLB(); });
}

/* ---------- SMALL MULTIPLES ---------- */
const CHARTS = [
  { key:'speed',  title:'speed',             unit:'m/s',  get:s=>s.speed||0, max:30 },
  { key:'fuel',   title:'fuel',              unit:'L',    get:s=>s.fuel_l||0, autoY:true, padPct:0.1 },
  { key:'tireL',  title:'tire wear · left',  unit:'[0,1]', get:s=>s.tire_l||0, max:1 },
  { key:'tireR',  title:'tire wear · right', unit:'[0,1]', get:s=>s.tire_r||0, max:1 },
  { key:'draft',  title:'draft strength',    unit:'[0,1]', get:s=>s.draft||0, max:1 },
  { key:'reward', title:'cumulative reward', unit:'',     special:'reward' },
  { key:'pos',    title:'final position',    unit:'rank', special:'pos' },
  { key:'inc',    title:'incident pressure', unit:'count', special:'inc' },
];

function chartCurrentValue(c) {
  if (c.special === 'inc') {
    const W = 0.4;
    return EVENTS.filter(e => Math.abs(e.t - state.t) < W).length;
  }
  if (c.special === 'pos') {
    if (state.focusCar != null) {
      const d = DRIVERS.find(x => x.car_id === state.focusCar);
      return d ? ((d.end_state||{}).final_position || 0) : 0;
    }
    const ps = DRIVERS.map(d => (d.end_state||{}).final_position || 0).filter(x => x > 0);
    return ps.length ? (ps.reduce((s,v)=>s+v,0) / ps.length) : 0;
  }
  if (c.special === 'reward') {
    let n = 0, sum = 0;
    DRIVERS.forEach(d => {
      const endT = d.samples && d.samples.length ? d.samples[d.samples.length-1].t : DURATION;
      const ratio = Math.max(0, Math.min(1, state.t / Math.max(endT, 1e-3)));
      const r = ((d.end_state||{}).cumulative_reward || 0) * ratio;
      sum += r; n++;
    });
    return n ? sum/n : 0;
  }
  const states = carStateMap(state.t);
  let n = 0, sum = 0;
  DRIVERS.forEach(d => {
    const st = states.get(d.car_id);
    if (st && !st.dead) { sum += c.get(st); n++; }
  });
  return n ? sum/n : 0;
}

function chartSeries(c, sx, sy, W, H) {
  // Returns array of {color, points, carId, focusClass}
  const out = [];
  DRIVERS.forEach((d, i) => {
    if (!d.samples || !d.samples.length) return;
    let pts;
    if (c.special === 'reward') {
      const endT = d.samples[d.samples.length-1].t;
      const endR = (d.end_state||{}).cumulative_reward || 0;
      pts = d.samples.map(s => `${sx(s.t).toFixed(1)},${sy(endR * Math.min(1, s.t/Math.max(endT,1e-3))).toFixed(1)}`).join(' ');
    } else if (c.special === 'pos') {
      const fp = (d.end_state||{}).final_position || 0;
      const xL = sx(0).toFixed(1), xR = sx(DURATION).toFixed(1);
      const y = sy(fp).toFixed(1);
      pts = `${xL},${y} ${xR},${y}`;
    } else {
      pts = d.samples.map(s => `${sx(s.t).toFixed(1)},${sy(c.get(s)).toFixed(1)}`).join(' ');
    }
    const cls = state.focusCar == null ? '' : (state.focusCar === d.car_id ? 'focus' : 'dim');
    out.push({ color: carHue(i), pts, cls, carId: d.car_id });
  });
  return out;
}

// renderCharts builds the static parts (polylines, axes) once and caches a
// reference to each tile's cursor <line> + stat <div>. updateChartsCursor
// then runs per-frame and only touches those refs — no innerHTML rebuilds,
// no per-frame polyline string concat over every sample.
function renderCharts() {
  const grid = $('#chartsGrid');
  const W = 280, H = 70;
  cache.chartTiles = [];

  grid.innerHTML = CHARTS.map((c) => {
    let body = '';
    if (c.special === 'inc') {
      const bins = 40; const arr = new Array(bins).fill(0);
      EVENTS.forEach(e => {
        const b = Math.min(bins-1, Math.max(0, Math.floor((e.t/Math.max(DURATION,1e-3))*bins)));
        arr[b]++;
      });
      const mx = Math.max(1, ...arr);
      body = arr.map((v,i) => {
        const x = (i/bins)*W;
        const h = (v/mx)*H;
        return `<rect x="${x}" y="${H-h}" width="${W/bins-1}" height="${h}" fill="#1f1c17" opacity="0.55"/>`;
      }).join('');
    } else {
      let dmin = 0, dmax;
      if (c.max != null) dmax = c.max;
      else if (c.special === 'pos') { dmin = 1; dmax = Math.max(2, DRIVERS.length); }
      else if (c.special === 'reward') {
        const allR = DRIVERS.map(d => (d.end_state||{}).cumulative_reward || 0);
        dmax = Math.max(1, ...allR) * 1.05; dmin = Math.min(0, ...allR);
      } else if (c.autoY) {
        // Single-pass min/max — Math.min(...allV) spread blows the stack on
        // long races (e.g. 20k samples × 24 drivers).
        let lo = Infinity, hi = -Infinity, has = false;
        DRIVERS.forEach(d => (d.samples||[]).forEach(s => {
          const v = c.get(s);
          if (v < lo) lo = v;
          if (v > hi) hi = v;
          has = true;
        }));
        if (has) {
          const pad = (hi - lo) * (c.padPct || 0.05);
          dmin = lo - pad; dmax = hi + pad;
          if (dmin === dmax) dmax = dmin + 1;
        } else { dmax = 1; }
      } else { dmax = 1; }
      const sx = t => (t/Math.max(DURATION,1e-3)) * W;
      const sy = v => H - ((v - dmin) / Math.max(1e-6, (dmax - dmin))) * H;
      body += `<g class="axis"><line x1="0" y1="${sy(dmin)}" x2="${W}" y2="${sy(dmin)}"/><line x1="0" y1="${sy(dmax)}" x2="${W}" y2="${sy(dmax)}"/></g>`;
      const series = chartSeries(c, sx, sy, W, H);
      series.forEach(s => {
        body += `<polyline class="series ${s.cls}" points="${s.pts}" stroke="${s.color}"/>`;
      });
    }
    const cursorX = (state.t / Math.max(DURATION,1e-3)) * W;
    body += `<line class="cursor" x1="${cursorX}" y1="0" x2="${cursorX}" y2="${H}"/>`;
    return `<div class="chart-tile" data-key="${c.key}">
        <div class="chart-head">
          <span class="chart-title">${c.title}</span>
          <span class="chart-unit">${c.unit}</span>
        </div>
        <div class="chart-stat"></div>
        <svg class="chart-svg" viewBox="0 0 ${W} ${H}" preserveAspectRatio="none">${body}</svg>
      </div>`;
  }).join('');

  // Cache cursor + stat refs per tile so updateChartsCursor is O(charts).
  grid.querySelectorAll('.chart-tile').forEach((tile, i) => {
    const c = CHARTS[i];
    cache.chartTiles.push({
      key: c.key,
      c,
      cursorEl: tile.querySelector('line.cursor'),
      statEl: tile.querySelector('.chart-stat'),
      W,
    });
    tile.addEventListener('mousemove', e => {
      const r = tile.querySelector('.chart-svg').getBoundingClientRect();
      const u = Math.max(0, Math.min(1, (e.clientX - r.left) / r.width));
      state.t = u * DURATION;
      state.playing = false; setPlayIcon();
      tick();
    });
  });
  updateChartsCursor();
}

function updateChartsCursor() {
  const focusLabel = state.focusCar != null ? 'focus' : 'avg';
  const tLabel = fmt(state.t, 1);
  for (const t of cache.chartTiles) {
    const cursorX = (state.t / Math.max(DURATION,1e-3)) * t.W;
    if (t.cursorEl) {
      t.cursorEl.setAttribute('x1', cursorX);
      t.cursorEl.setAttribute('x2', cursorX);
    }
    if (t.statEl) {
      const cur = chartCurrentValue(t.c);
      const curStr = t.c.special === 'inc' ? String(cur) : fmt(cur, 1);
      t.statEl.innerHTML = `${curStr}<span class="delta">${focusLabel} @ t=${tLabel}</span>`;
    }
  }
}

/* ---------- EVENTS LANE ---------- */
// Same static/dynamic split as charts: render lanes + marks once, cache
// cursor <line> refs. updateEventsCursor moves cursors + rebuilds the small
// "around-cursor" event list (the only cursor-dependent UI).
function renderEvents() {
  const W = 1000, H = 58, yMid = 29;
  const cursorX = (state.t/Math.max(DURATION,1e-3))*W;
  const totals = { overtake: 0, car_hit: 0, wall_hit: 0 };
  EVENTS.forEach(e => { if (totals[e.type] != null) totals[e.type]++; });
  const statIds = { overtake: 'sOvt', car_hit: 'sHit', wall_hit: 'sWall' };
  const rateIds = { overtake: 'rOvt', car_hit: 'rHit', wall_hit: 'rWall' };
  cache.eventCursors = [];
  ['overtake','car_hit','wall_hit'].forEach(kind => {
    const row = document.querySelector(`.events-track[data-e="${kind}"]`);
    if (!row) return;
    const enabled = state.enabledEvents.has(kind);
    row.classList.toggle('dim', !enabled);
    const lane = row.querySelector('svg.events-track-svg');
    const col = EVENT_COLORS[kind] || '#837c72';
    const baseline = `<line x1="0" y1="${yMid}" x2="${W}" y2="${yMid}" stroke="#d4cdba" stroke-width="0.5" stroke-dasharray="2 3"/>`;
    const marks = EVENTS.filter(e => e.type === kind).map(e => {
      const x = (e.t / Math.max(DURATION,1e-3)) * W;
      const r = kind === 'overtake' ? 2 : 2 + (e.impact_speed || 0);
      return `<circle cx="${x.toFixed(1)}" cy="${yMid}" r="${Math.min(r,5).toFixed(1)}" fill="${col}" opacity="0.78"/>`;
    }).join('');
    const cursor = `<line class="evCursor" x1="${cursorX.toFixed(1)}" y1="0" x2="${cursorX.toFixed(1)}" y2="${H}" stroke="#1f1c17" stroke-width="1" opacity="0.55"/>`;
    lane.innerHTML = baseline + marks + cursor;
    cache.eventCursors.push(lane.querySelector('line.evCursor'));
    $('#'+statIds[kind]).textContent = totals[kind];
    $('#'+rateIds[kind]).textContent = (totals[kind] / Math.max(DURATION, 0.001)).toFixed(1);
  });
  updateEventsCursor();
}

function updateEventsCursor() {
  const W = 1000;
  const x = (state.t / Math.max(DURATION, 1e-3)) * W;
  for (const c of cache.eventCursors) {
    if (!c) continue;
    c.setAttribute('x1', x.toFixed(1));
    c.setAttribute('x2', x.toFixed(1));
  }
  // events list within ±1.5s of cursor — cheap (filter on EVENTS array).
  const filtered = EVENTS.filter(e => state.enabledEvents.has(e.type));
  const win = 1.5;
  const around = filtered.filter(e => Math.abs(e.t - state.t) < win).sort((a,b)=>a.t-b.t).slice(0, 40);
  $('#evList').innerHTML = around.length === 0
    ? `<div style="padding:14px 16px; color:var(--ink-3); font-family:'JetBrains Mono', monospace; font-size:11px;">no events within ±${win}s of cursor — scrub to find incidents.</div>`
    : around.map(e => {
      let detail;
      if (e.type === 'overtake') {
        detail = `<b>car_${e.passer}</b> passed <b>car_${e.passed}</b>` + (e.new_position ? ` → new_position=<b>${e.new_position}</b>` : '');
      } else if (e.type === 'car_hit') {
        detail = `<b>car_${e.a}</b> ↯ <b>car_${e.b}</b> · impact ${fmt(e.impact_speed||0, 2)} m/s`;
      } else {
        detail = `<b>car_${e.car_id}</b> ↯ wall · impact ${fmt(e.impact_speed||0, 2)} m/s`;
      }
      return `<div class="ev-row">
        <div class="ev-t">${fmt(e.t,3)}s</div>
        <div class="ev-type ${e.type}">${e.type}</div>
        <div class="ev-detail">${detail}</div>
        <div class="ev-mini">Δ ${fmt(e.t - state.t, 2)}</div>
      </div>`;
    }).join('');
}

function bindEvents() {
  document.querySelectorAll('#evToolBar .pill-toggle').forEach(p => {
    p.addEventListener('click', () => {
      const k = p.dataset.e;
      if (state.enabledEvents.has(k)) state.enabledEvents.delete(k);
      else state.enabledEvents.add(k);
      p.classList.toggle('on');
      renderEvents();
    });
  });
  // hover-scrub on event lanes (mirrors chart-tile behaviour)
  document.querySelectorAll('.events-track-svg').forEach(lane => {
    lane.addEventListener('mousemove', e => {
      const r = lane.getBoundingClientRect();
      const u = Math.max(0, Math.min(1, (e.clientX - r.left) / r.width));
      state.t = u * DURATION;
      state.playing = false; setPlayIcon();
      tick();
    });
  });
}

/* ---------- TICK (re-render time-dependent layers) ----------
 * Hot path. Must stay O(drivers + charts + lanes), NOT O(samples).
 * Each tick: invalidate the per-time car-state memo, update scrub head,
 * move car transforms, slide cursor lines, refresh "around cursor" list.
 */
function tick() {
  clearCarStateCache();
  updateScrubHead();
  updateCars();
  updateChartsCursor();
  updateEventsCursor();
}

/* ---------- EXPORT / NAV ---------- */
function exportJson() {
  const a = document.createElement('a');
  a.href = '/api/races/' + encodeURIComponent(racePathId());
  a.download = (RACE.race_id || 'race') + '.json';
  document.body.appendChild(a); a.click(); document.body.removeChild(a);
}

/* ---------- INIT ---------- */
function setStatus(id, state, msg) {
  const el = document.getElementById(id);
  if (!el) return;
  el.classList.remove('on', 'err', 'ok', 'fade');
  if (state === 'loading') {
    el.classList.add('on');
    el.innerHTML = '<span class="spinner"></span>' + (msg || 'loading…');
  } else if (state === 'error') {
    el.classList.add('on', 'err');
    el.innerHTML = '<span class="err-dot">!</span>' + (msg || 'error');
  } else if (state === 'ok') {
    el.classList.add('on', 'ok');
    el.innerHTML = '<span class="ok-dot"></span>' + (msg || 'loaded');
    setTimeout(() => el.classList.add('fade'), 1200);
  }
}

// Keep the C# pruner from deleting this race while we view it. The HTML
// route + first API fetch already pin server-side; this heartbeat
// extends the pin every ~5 min so a long-open tab stays valid.
function startPinHeartbeat(id) {
  setInterval(() => {
    fetch('/api/races/' + encodeURIComponent(id) + '/pin').catch(() => {});
  }, 5 * 60 * 1000);
}

async function load() {
  const id = racePathId();
  setStatus('race-loading', 'loading', 'loading race…');
  let r;
  try {
    r = await fetch('/api/races/' + encodeURIComponent(id));
  } catch (e) {
    setStatus('race-loading', 'error', (e && e.message) || 'network error');
    return;
  }
  if (!r.ok) {
    setStatus('race-loading', 'error', r.status === 404 ? 'not found' : ('HTTP ' + r.status));
    document.querySelector('main.race-grid').innerHTML =
      '<div class="card" style="grid-column:1/-1;padding:40px;text-align:center;color:var(--ink-3);font-family:JetBrains Mono,monospace">race not found · ' + id + '</div>';
    return;
  }
  startPinHeartbeat(id);
  RACE = await r.json();
  DRIVERS = (RACE.drivers || []).slice();
  EVENTS = (RACE.events || []).slice().sort((a,b)=>a.t-b.t);
  OVERLAY = RACE.overlay || null;
  BBOX = RACE.effective_bbox || (OVERLAY && {
    minX: OVERLAY.minX, maxX: OVERLAY.maxX,
    minY: OVERLAY.minY, maxY: OVERLAY.maxY,
  });
  DURATION = Math.max(0.1, RACE.duration_s || 0);
  HZ = RACE.sample_hz || 5;

  // Event count pills.
  const cOvt = EVENTS.filter(e => e.type === 'overtake').length;
  const cHit = EVENTS.filter(e => e.type === 'car_hit').length;
  const cWall = EVENTS.filter(e => e.type === 'wall_hit').length;
  $('#cOvt').textContent = cOvt;
  $('#cHit').textContent = cHit;
  $('#cWall').textContent = cWall;
  $('#evTotal').textContent = EVENTS.length;

  // length corner (from OVERLAY totalLength if present)
  if (OVERLAY && OVERLAY.totalLength) {
    $('#cornerLen').textContent = `length ${OVERLAY.totalLength.toFixed(1)}m`;
  }

  // Export button + nav.
  const btnExport = document.getElementById('btnExport');
  if (btnExport) btnExport.addEventListener('click', exportJson);

  renderHeaderMeta();
  renderKPIs();
  renderScrub();
  renderMap();
  renderLB();
  renderCharts();
  renderEvents();
  updateCars();
  bindScrub();
  bindPlay();
  bindLB();
  bindEvents();
  requestAnimationFrame(loop);
  setStatus('race-loading', 'ok', (RACE.race_id || '').slice(0, 8) + ' · ' + (DRIVERS.length || 0) + ' drivers');
}
load();
</script>
"""

_RACE_DETAIL_META = '<div class="meta" id="hdrMeta"></div>'

_RACE_DETAIL_ACTIONS = (
    '<a class="btn" href="/races">← all races</a>'
    '<button class="btn" id="btnExport">export json <span class="kbd">json</span></button>'
    '<button class="btn primary" id="playBtnHdr" onclick="document.getElementById(\'playBtn\').click()">replay race <span class="kbd">space</span></button>'
)

RACE_DETAIL_HTML = (
    '<!doctype html><html lang="en"><head><meta charset="utf-8">'
    '<title>RACING · race telemetry</title>'
    '<meta name="viewport" content="width=device-width, initial-scale=1">'
    + BASE_HEAD
    + '<style>' + BASE_STYLES + _RACE_DETAIL_STYLES + '</style>'
    + '</head><body>'
    + chrome_header('races', 'race telemetry',
                    meta_html=_RACE_DETAIL_META, actions_html=_RACE_DETAIL_ACTIONS)
    + _RACE_DETAIL_BODY
    + '</body></html>'
)


class Handler(http.server.BaseHTTPRequestHandler):
    def _send(self, status, body, ctype="application/json"):
        self.send_response(status)
        self.send_header("Content-Type", ctype + "; charset=utf-8")
        if isinstance(body, str):
            body = body.encode("utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        path = urllib.parse.urlparse(self.path).path
        if path in ("/", "/index.html"):
            # Tier-list root page stripped in the open-source build. Redirect
            # to /training (the live trainer dashboard).
            self.send_response(302)
            self.send_header("Location", "/training")
            self.end_headers()
            return
        if path == "/settings":
            self._send(200, SETTINGS_HTML, "text/html")
            return
        if path == "/api/versions":
            self._send(200, json.dumps({"versions": _list_manifests()}))
            return
        if path == "/api/settings":
            qs = urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query)
            version = (qs.get("version") or ["latest"])[0]
            data = _load_manifest(version)
            if data is None:
                self._send(404, json.dumps({"error": f"manifest not found: {version}"}))
                return
            self._send(200, json.dumps(data))
            return
        if path == "/authored":
            self._send(200, AUTHORED_HTML, "text/html")
            return
        if path == "/static/render.js":
            self._send(200, RENDER_JS, "application/javascript")
            return
        if path == "/api/state":
            payload = {"circuits": load_circuits(), "playlist": load_playlist()}
            self._send(200, json.dumps(payload))
            return
        if path == "/api/authored":
            payload = {"circuits": load_authored_closure_circuits()}
            self._send(200, json.dumps(payload))
            return
        if path == "/training":
            self._send(200, TRAINING_HTML, "text/html")
            return
        if path == "/api/training/stats":
            qs = urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query)
            window = int((qs.get("window") or ["0"])[0])
            events = filter_events_by_window(load_telemetry_events(), window)
            self._send(200, json.dumps(aggregate_training_stats(events)))
            return
        if path == "/api/training/envs":
            events = load_telemetry_events()
            self._send(200, json.dumps({"envs": latest_circuit_per_env(events)}))
            return
        if path == "/api/circuit_records":
            # Permanent per-circuit fastest-lap history. Read-only — writes
            # go through aggregate_training_stats(_persist_circuit_records).
            self._send(200, json.dumps(_load_circuit_records()))
            return
        if path == "/api/training/events":
            qs = urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query)
            limit = int((qs.get("limit") or ["500"])[0])
            window = int((qs.get("window") or ["0"])[0])
            events = filter_events_by_window(load_telemetry_events(), window)
            self._send(200, json.dumps({"events": events[-limit:]}))
            return
        if path.startswith("/api/training/heatmap/"):
            circuit = urllib.parse.unquote(path[len("/api/training/heatmap/"):])
            qs = urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query)
            window = int((qs.get("window") or ["0"])[0])
            events = filter_events_by_window(
                telemetry_events_for_circuit(circuit), window)
            self._send(200, json.dumps(heatmap_for_circuit(events, circuit)))
            return
        if path.startswith("/api/training/laps/"):
            circuit = urllib.parse.unquote(path[len("/api/training/laps/"):])
            qs = urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query)
            window = int((qs.get("window") or ["0"])[0])
            limit = int((qs.get("limit") or ["200"])[0])
            # Pass ALL events, not the per-circuit slice: lap re-attribution
            # needs to see micro_sector events whose circuit field was correct
            # while the matching episode_end was poisoned by the regen race.
            events = filter_events_by_window(load_telemetry_events(), window)
            self._send(200, json.dumps(laps_for_circuit(events, circuit, limit)))
            return
        # ----- Race history -----
        if path == "/races":
            self._send(200, RACES_LIST_HTML, "text/html")
            return
        if path == "/api/races":
            self._send(200, json.dumps({"races": load_race_summaries()}))
            return
        if path.startswith("/api/races/"):
            tail = urllib.parse.unquote(path[len("/api/races/"):])
            # Heartbeat endpoint — refreshes the pin without re-serving the
            # (potentially MB-sized) race JSON. JS in /races/<id> hits this
            # every few minutes to keep the C# pruner away.
            if tail.endswith("/pin"):
                rid = tail[: -len("/pin")]
                _touch_race_pin(rid)
                self._send(200, json.dumps({"ok": True, "race_id": rid}))
                return
            rid = tail
            race = enriched_race(rid)
            if race is None:
                self._send(404, json.dumps({"error": "race not found", "race_id": rid}))
            else:
                # Touch the pin so opening the race detail (which fetches
                # this endpoint) immediately protects the underlying file
                # from prune even if the C# trainer is racing it.
                _touch_race_pin(rid)
                self._send(200, json.dumps(race))
            return
        if path.startswith("/races/"):
            # Also pin on the HTML route so a freshly-opened tab is
            # protected even before its first API fetch lands.
            try:
                rid = urllib.parse.unquote(path[len("/races/"):])
                _touch_race_pin(rid)
            except Exception:
                pass
            self._send(200, RACE_DETAIL_HTML, "text/html")
            return
        self._send(404, json.dumps({"error": "not found"}))

    def do_POST(self):
        path = urllib.parse.urlparse(self.path).path
        if path == "/api/training/clear":
            length = int(self.headers.get("Content-Length", "0"))
            keep_seconds = None
            if length:
                try:
                    body = json.loads(self.rfile.read(length).decode("utf-8"))
                    if isinstance(body, dict):
                        keep_seconds = int(body.get("keep_seconds") or 0) or None
                except Exception:
                    keep_seconds = None
            try:
                clear_telemetry(keep_seconds=keep_seconds)
                self._send(200, json.dumps({
                    "ok": True,
                    "kept_seconds": keep_seconds or 0,
                }))
            except Exception as e:
                self._send(500, json.dumps({"ok": False, "error": str(e)}))
            return
        if path == "/api/settings":
            qs = urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query)
            version = (qs.get("version") or ["latest"])[0]
            force = (qs.get("force") or ["0"])[0] in ("1", "true", "yes")
            length = int(self.headers.get("Content-Length", "0"))
            try:
                data = json.loads(self.rfile.read(length).decode("utf-8"))
            except Exception as e:
                self._send(400, json.dumps({"ok": False, "error": f"invalid JSON: {e}"}))
                return
            ok, err, status = _save_manifest(version, data, force=force)
            if ok:
                self._send(200, json.dumps({"ok": True, "version": version}))
            else:
                self._send(status, json.dumps({"ok": False, "error": err}))
            return
        if path == "/api/settings/reset":
            qs = urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query)
            version = (qs.get("version") or ["latest"])[0]
            ok, err = _reset_manifest(version)
            if ok:
                self._send(200, json.dumps({"ok": True, "version": version}))
            else:
                self._send(400, json.dumps({"ok": False, "error": err}))
            return
        if path == "/api/versions/snapshot":
            qs = urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query)
            new_id = (qs.get("id") or [""])[0]
            ok, err = _snapshot_manifest(new_id)
            if ok:
                self._send(200, json.dumps({"ok": True, "version": new_id}))
            else:
                self._send(400, json.dumps({"ok": False, "error": err}))
            return
        self._send(404, json.dumps({"error": "not found"}))

    def log_message(self, fmt, *args):
        pass  # quiet


class _ThreadedTCPServer(socketserver.ThreadingMixIn, socketserver.TCPServer):
    daemon_threads = True
    allow_reuse_address = True


def main():
    socketserver.TCPServer.allow_reuse_address = True
    host = os.environ.get("TIERLIST_HOST", "127.0.0.1")
    with _ThreadedTCPServer((host, PORT), Handler) as httpd:
        print(f"Circuit tier-list at http://{host}:{PORT}")
        print(f"Authored closure at http://localhost:{PORT}/authored")
        print(f"Training dashboard at http://localhost:{PORT}/training")
        print(f"Race history at      http://localhost:{PORT}/races")
        print(f"Reading from:        {CIRCUITS_DIR}")
        print(f"Authored closure dir: {AUTHORED_CLOSURE_DIR}")
        print(f"Telemetry dir:       {TELEMETRY_DIR}")
        print(f"Saves to:            {PLAYLIST_FILE}")
        print("Ctrl+C to stop.")
        try:
            httpd.serve_forever()
        except KeyboardInterrupt:
            pass


if __name__ == "__main__":
    main()
