#!/usr/bin/env python3
"""
1Rad -> Orthanc modality worklist sync (gap analysis P3-6, phase 3).

Polls the 1Rad worklist feed (GET {api}/api/v1/worklist/mwl, DICOM JSON) and keeps
Orthanc's worklist folder in step with it: one .wl file per booked service that is not
yet scanned. The modality's C-FIND against Orthanc then lists today's 1Rad bookings, so
the technician picks the patient instead of typing name and ID. The images come back
with the Study Instance UID from the item, and 1Rad links them to that exact service.

Only files this tool wrote (prefix "1rad_") are ever removed, so hand-made worklist
files in the same folder are left alone.

Orthanc config (orthanc.json):
    "Worklists": { "Enable": true, "Database": "/var/lib/orthanc/worklists" }

Run:
    pip install pydicom requests
    export ONERAD_API=https://1rad-api.nexeagle.com
    export ONERAD_KEY=1rk_xxxxxxxxxx_xxxxxxxx      # Settings > Integrations, scope worklist.read
    export WORKLIST_DIR=/var/lib/orthanc/worklists
    python3 worklist_sync.py                       # loops; --once for a single pass (cron)

Optional: MODALITY=CT (only that modality), DAYS=1 (today + n-1 days), INTERVAL=60 (seconds),
          STATION_AE=CT01 (fill Scheduled Station AE Title so only that machine sees them).
"""
from __future__ import annotations

import argparse
import hashlib
import json
import logging
import os
import sys
import tempfile
import time
from datetime import datetime, timedelta, timezone

PREFIX = "1rad_"
IST = timezone(timedelta(hours=5, minutes=30))
log = logging.getLogger("worklist-sync")


def fetch_items(api: str, key: str, date: str, days: int, modality: str | None, timeout: int = 30) -> list[dict]:
    import requests  # imported here so --help works without it

    params = {"date": date, "days": str(days)}
    if modality:
        params["modality"] = modality
    r = requests.get(
        f"{api.rstrip('/')}/api/v1/worklist/mwl",
        params=params,
        headers={"Authorization": f"Bearer {key}", "Accept": "application/dicom+json"},
        timeout=timeout,
    )
    if r.status_code == 401:
        raise SystemExit("1Rad refused the key (401) — check ONERAD_KEY and that it has the worklist.read scope.")
    r.raise_for_status()
    data = r.json()
    if not isinstance(data, list):
        raise ValueError("Unexpected worklist response (not a JSON array).")
    return data


def file_name(item: dict) -> str:
    """Stable per service: the Study Instance UID the item carries."""
    uid = (item.get("0020000D", {}).get("Value") or [""])[0]
    if not uid:
        uid = hashlib.sha1(json.dumps(item, sort_keys=True).encode()).hexdigest()
    return f"{PREFIX}{hashlib.sha1(uid.encode()).hexdigest()[:24]}.wl"


def apply_station(item: dict, station_ae: str | None) -> dict:
    if not station_ae:
        return item
    seq = item.get("00400100", {}).get("Value") or []
    for sps in seq:
        sps["00400001"] = {"vr": "AE", "Value": [station_ae[:16]]}
    return item


def to_wl_bytes(item: dict) -> bytes:
    """A DICOM Part 10 worklist file from one DICOM JSON item."""
    from io import BytesIO

    from pydicom.dataset import Dataset, FileMetaDataset
    from pydicom.uid import ExplicitVRLittleEndian, PYDICOM_IMPLEMENTATION_UID

    ds = Dataset.from_json(json.dumps(item))
    meta = FileMetaDataset()
    meta.MediaStorageSOPClassUID = "1.2.840.10008.5.1.4.31"  # Modality Worklist Information Model - FIND
    meta.MediaStorageSOPInstanceUID = getattr(ds, "StudyInstanceUID", "") or "2.25.0"
    meta.TransferSyntaxUID = ExplicitVRLittleEndian
    meta.ImplementationClassUID = PYDICOM_IMPLEMENTATION_UID
    ds.file_meta = meta
    buf = BytesIO()
    # pydicom 3: enforce_file_format; pydicom 2: write_like_original=False
    try:
        ds.save_as(buf, enforce_file_format=True)
    except TypeError:
        ds.is_little_endian = True
        ds.is_implicit_VR = False
        ds.save_as(buf, write_like_original=False)
    return buf.getvalue()


def write_atomic(path: str, data: bytes) -> None:
    folder = os.path.dirname(path)
    fd, tmp = tempfile.mkstemp(dir=folder, prefix=".tmp_", suffix=".wl")
    try:
        with os.fdopen(fd, "wb") as f:
            f.write(data)
        os.replace(tmp, path)
    finally:
        if os.path.exists(tmp):
            os.remove(tmp)


def sync_once(api: str, key: str, folder: str, days: int, modality: str | None, station_ae: str | None) -> tuple[int, int, int]:
    """Returns (written, unchanged, removed)."""
    os.makedirs(folder, exist_ok=True)
    today = datetime.now(IST).strftime("%Y%m%d")
    items = fetch_items(api, key, today, days, modality)

    wanted: dict[str, bytes] = {}
    for item in items:
        try:
            wanted[file_name(item)] = to_wl_bytes(apply_station(item, station_ae))
        except Exception as ex:  # one bad item must not stop the rest
            log.warning("skipping an item that could not be converted: %s", ex)

    written = unchanged = removed = 0
    for name, data in wanted.items():
        path = os.path.join(folder, name)
        if os.path.exists(path):
            with open(path, "rb") as f:
                if f.read() == data:
                    unchanged += 1
                    continue
        write_atomic(path, data)
        written += 1

    for name in os.listdir(folder):
        if name.startswith(PREFIX) and name.endswith(".wl") and name not in wanted:
            os.remove(os.path.join(folder, name))  # scanned, cancelled or no longer today
            removed += 1
    return written, unchanged, removed


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--once", action="store_true", help="one pass, then exit (for cron)")
    args = ap.parse_args(argv)
    logging.basicConfig(level=os.environ.get("LOG_LEVEL", "INFO"), format="%(asctime)s %(levelname)s %(message)s")

    api = os.environ.get("ONERAD_API")
    key = os.environ.get("ONERAD_KEY")
    folder = os.environ.get("WORKLIST_DIR")
    if not (api and key and folder):
        print("Set ONERAD_API, ONERAD_KEY and WORKLIST_DIR.", file=sys.stderr)
        return 2
    days = max(1, min(7, int(os.environ.get("DAYS", "1"))))
    modality = os.environ.get("MODALITY") or None
    station_ae = os.environ.get("STATION_AE") or None
    interval = max(15, int(os.environ.get("INTERVAL", "60")))

    while True:
        try:
            w, u, r = sync_once(api, key, folder, days, modality, station_ae)
            log.info("worklist: %d written, %d unchanged, %d removed", w, u, r)
        except SystemExit:
            raise
        except Exception as ex:
            # Network down or API restarting: keep the current files, try again next round.
            log.error("sync failed (keeping current worklist): %s", ex)
            if args.once:
                return 1
        if args.once:
            return 0
        time.sleep(interval)


if __name__ == "__main__":
    sys.exit(main())
