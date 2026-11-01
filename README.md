# UTube Bulk Uploader

A Windows app that renames raw news footage using folder names and shooting dates, groups it into batches of up to 15 for YouTube Studio, and (in a later update) uploads batches overnight.

## Install

1. Open **Releases** on the right of this page and download `UTubeBulkUploader-Setup.exe` from the latest one.
2. Run it. Windows may show **"Windows protected your PC"** because the installer isn't code-signed. Click **More info**, then **Run anyway**.
3. No admin rights are needed. The app appears in the Start menu, in Windows search, and can be removed from **Settings → Apps**.

Everything the app needs is included; nothing else has to be installed.

## Try it safely first

Renaming happens **in place**. The first time, copy a small footage folder somewhere and try the app on the copy.

## How it works

1. **Home**: add story folders (button, Ctrl+click for several, or drag them in from File Explorer).
2. **Preview**: check every clip's current name → new name. Edit story or sub-folder names (titles only; folders on disk keep their names), type over a folder's date, drag clips to reorder, untick clips to skip.
3. **Rename files**: renames in place, records every file in the log, and can be undone.
4. **Log**: search the log, open it in Excel, or undo a whole rename run.

### Names

- Clips directly in the story folder: `Collector Visit 1 (26.10.26).mp4`
- Clips in sub-folders: `Collector Visit - Phone 1 - 1 (26.10.26).mp4`, and deeper `Collector Visit - Phone 1 - Day 2 - 3 (26.10.26).mp4`
- Numbers start at 1 in each folder, in recording-time order (or the order you drag them into).
- Each folder's date is the earliest recording date of its clips (from the video's own information, otherwise the file's modified date).
- Titles are kept to 100 characters (YouTube's limit) by shortening the middle.
- Video types: MP4, MOV, MTS, M2TS, AVI, MXF. Sidecar files (XML, THM…) are left alone.

### Log

`UTube Bulk Uploader Log.csv` on your Desktop (changeable in Settings). It opens in Excel. Each clip is recognised by a fingerprint (size plus the first and last 1 MB), so the app never renames the same clip twice, even after renaming or on another drive letter.

If the log is open in Excel while the app writes, nothing is lost: the app keeps a copy and writes it once Excel closes it.

If the PC or app stops in the middle of renaming, the next launch offers to **finish** or **roll back**.

## Build status

Every push builds the app on a clean Windows machine, runs the tests, builds the installer, installs it, opens every screen, and publishes the installer as the latest release.

## Roadmap

1. Rename (Home, Preview, rename, undo, log) — **this version**
2. Batches and manual drag into YouTube Studio
3. Night mode on a test page
4. Night mode on real YouTube Studio
5. Second PC check
