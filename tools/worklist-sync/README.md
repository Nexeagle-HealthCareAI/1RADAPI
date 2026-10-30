# 1Rad → Orthanc worklist sync

Keeps Orthanc's modality-worklist folder in step with 1Rad bookings, so a CT/MR/X-ray
console that does a worklist query (DICOM C-FIND) lists today's 1Rad patients.

1. In 1Rad: **Settings → Integrations → New key**, tick *Modality worklist*, copy the key.
2. In Orthanc (`orthanc.json`):
   ```json
   "Worklists": { "Enable": true, "Database": "/var/lib/orthanc/worklists" }
   ```
   and point the modality's worklist (MWL) AE at Orthanc.
3. On the Orthanc machine:
   ```bash
   pip install pydicom requests
   export ONERAD_API=https://1rad-api.nexeagle.com
   export ONERAD_KEY=1rk_...
   export WORKLIST_DIR=/var/lib/orthanc/worklists
   python3 worklist_sync.py          # keeps running, refreshes every 60 s
   ```
   For cron instead: `* * * * * … python3 worklist_sync.py --once`.

Options (environment): `MODALITY=CT` (one modality only), `STATION_AE=CT01` (only that
console sees the items), `DAYS=2`, `INTERVAL=30`, `LOG_LEVEL=DEBUG`.

How the images find their way back: every item carries a Study Instance UID made from the
1Rad service id. When the modality sends the study (through Orthanc/the bridge or DICOMweb
STOW), 1Rad recognises that UID and attaches the images to that exact booking.

Files the tool creates start with `1rad_`; it never touches other files in the folder. If
1Rad can't be reached it keeps the current files and retries.

Example systemd unit (`/etc/systemd/system/1rad-worklist.service`):
```ini
[Unit]
Description=1Rad worklist sync
After=network-online.target

[Service]
Environment=ONERAD_API=https://1rad-api.nexeagle.com
Environment=ONERAD_KEY=1rk_...
Environment=WORKLIST_DIR=/var/lib/orthanc/worklists
ExecStart=/usr/bin/python3 /opt/1rad/worklist_sync.py
Restart=always

[Install]
WantedBy=multi-user.target
```
