# UTube Bulk Uploader

A Windows app that renames raw news footage using folder names and shooting dates, then uploads it to YouTube Studio one video at a time overnight (night mode).

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

1. Rename (Home, Preview, rename, undo, log) — done, tested on a real PC
2. Batches — removed (night mode uploads one by one)
3. Night mode on a test page — runs in the automated tests on every build (not shown in the app)
4. Night mode on real YouTube Studio — one by one with title, description, audience and Private — **this version, first real test pending**
5. Second PC check

## Night mode

Night mode opens YouTube Studio in its own Chrome window (a separate Chrome profile; your everyday Chrome is untouched)
and uploads the waiting clips **one by one**, the way a person would: Create → Upload videos → the file →

- **Title:** the file name, e.g. `May 2025 - Muth - 3 (30.10.26)` (punctuation kept)
- **Description:** `File Name: May 2025 - Muth - 3 (30.10.26), Upload Date: 01.11.26` (the day it's uploaded)
- **Audience:** No, it's not made for kids
- **Visibility:** Private → **Save**

Night mode stays on the details screen until Studio's progress line says **Upload complete**, then saves, marks the
clip **Uploaded** in the log (with its YouTube link) and starts the next video: one at a time, at least 45 seconds
each (Settings). An upload whose percentage doesn't move for **1:30** (Settings) is treated as stuck, and one still not uploaded after **12 minutes** is saved anyway, marked Failed (with a note to check
it on YouTube) and uploaded again next run; **3 such cut-offs in a row** stop the night. Studio is refreshed every
20 videos. The Night mode screen counts uploads this run and today.

Before the first video it checks that Studio opens signed in and in English, and keeps the PC awake.

It stops by itself and says why, with a screenshot and the page's text, when Studio shows a limit message, when Chrome
is signed out, or when a screen isn't what it expects. **Stop** first finishes the video in progress; **Stop now**
stops at once.

If Studio says an upload failed, the video is still saved, so a broken entry is left on YouTube: the log says so
(with the link) and the clip is uploaded again on the next run.

After Save, night mode closes Studio's "Video processing" (and similar) boxes so they can't block the next video.

The app recognises a clip by its content, not its name, so copies of one video count as one clip and renamed clips
stay "already done". To start over, use **Settings → Forget everything**: every clip counts as new again (the log
keeps the history, marked Forgotten).

### Log columns

Run ID, Date/time, Folder path, Old name, New name, Size (bytes), Recording date, Fingerprint, Status, Status time,
Note, **Full path** (where the file is now), **YouTube link**, **YouTube video ID**. Status is one of Renamed,
Uploading, Uploaded, Failed, Undone, Forgotten.

First time: Night mode → **Sign in** → sign in to the channel's Google account in the Chrome window that opens, then close that window. (It opens as ordinary Chrome because Google refuses sign-in in a browser a program controls; night mode reopens it itself.) Try a small batch first.

Night mode drives YouTube Studio's website, which YouTube's terms don't
officially allow for automated tools; use it knowing the channel carries that risk.
