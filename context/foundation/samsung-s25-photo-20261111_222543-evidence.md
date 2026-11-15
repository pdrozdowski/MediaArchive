# Samsung S25 test photo evidence

Target: `20261111_222543.jpg` in `DCIM/Camera` on the connected Samsung Galaxy S25.

## Read method and copy

A C# probe compiled inline with PowerShell used Windows `Shell.Application` COM automation to enumerate `This PC > S25 > Internal storage > DCIM > Camera`, find this exact filename, query the Windows Shell property columns, and copy the file into the local probe folder. This read was non-destructive.

- Shell device name: `Urządzenie S25 użytkownika Pawel`
- Shell type: JPG File
- MTP/Shell reported size: 2.41 MB (2,528,963 bytes)
- Modified shown by Shell: 2026-11-11 22:25 (local display)
- Local evidence copy: `.tmp/s25-probe/20261111_222543.jpg`
- Local copy SHA-256: `D7143FCED513DB4862F8666CC2A9AA23D04E7EB51899BF249CFE1CDA59314FAA`

The local copy is retained as a temporary safety copy while the phone-side delete attempt is unresolved. The hash proves the identity of this local copy for subsequent checks; no pre-copy hash was available from MTP for byte-for-byte comparison.

## Decoded image metadata

- Format: JPEG
- Pixel dimensions: 4000 × 3000
- Camera make/model: Samsung Galaxy S25
- Capture timestamp (`DateTimeOriginal`, `DateTimeDigitized`): `2026:11:11 22:25:43`
- Orientation tag: 6 (display rotated 90° clockwise by EXIF-aware viewers)
- Exposure time: 0.019983 seconds (approximately 1/50 s)
- F-number: f/1.8
- ISO speed: 800
- Focal length: 5.4 mm; 35 mm equivalent: 59 mm
- Flash tag: 0 (flash did not fire)
- Metering mode tag: 2
- Camera software string: `S931BXXSDDZJ1`
- Image unique ID tag: `F50XLPE00VM`
- No GPS EXIF tag was present in the copied JPEG.

The JPEG also contains EXIF maker-note data, an ICC color profile, and an embedded thumbnail. The maker-note and ICC payloads were present but not decoded in this probe. Windows Shell exposed name, type, size, modified time, support status, and title; the camera details above came from the local JPEG's EXIF tags after the copy.

## Removal attempt status

The first C# probe located only the exact target filename in the S25 camera folder and invoked its Windows Shell `delete` verb. The user heard a system sound but did not see a popup in Alt+Tab or on the taskbar. A separate read-only enumeration confirmed the target was still listed. The stalled PowerShell probe process was stopped.

A second C# attempt used Windows `IFileOperation` with silent/no-confirmation flags, the Shift+Delete-style path suggested by the user. The Shell API rejected conversion of the MTP parsing path to an `IShellItem` with HRESULT `0x80070057` (`E_INVALIDARG`), before queuing a delete. A subsequent read-only enumeration again found the target. Removal did not complete. No other phone item was targeted.

A third attempt launched a separate visible Windows PowerShell process and invoked the same exact-item Shell `delete` verb from that window. The computer-control connection could not inspect the window or confirm whether the invocation returned. A separate read-only enumeration still found the target in `DCIM/Camera`; deletion remains unconfirmed and the phone copy is currently present.


## Microsoft WPD sample and latest retry report

Microsoft's `Windows-classic-samples` `PortableDeviceCOM/cpp/ContentTransfer.cpp` includes `DeleteContentFromDevice`. It takes one object identifier, creates an `IPortableDevicePropVariantCollection`, adds that identifier, and calls `IPortableDeviceContent::Delete` with `PORTABLE_DEVICE_DELETE_NO_RECURSION`. The sample distinguishes successful `S_OK`, per-object failure `S_FALSE`, and failed HRESULTs. Microsoft documents `IPortableDeviceContent` as the interface for enumerating and deleting device content, and WPD as supporting MTP devices.

This is the concrete official reference for a direct device-side delete probe. First enumerate and match the exact filename to its WPD object ID; then pass only that one ID. Do not use a normal filesystem path for the MTP item. Microsoft documentation reviewed here does not specify Explorer's exact internal delete implementation; using Windows' WPD/Shell integration is consistent with Explorer's ability to browse and manage this MTP device.

On the latest Shell retry, the user again heard a sound, saw no popup, and pressing Y, Enter, or Escape had no visible effect. A sound is not evidence of deletion. The previous read-only check found the photo still present; the latest user report does not itself establish a new device-state check.


## Direct C# WPD deletion result

A C# console probe using the Windows SDK's WPD COM interfaces (`IPortableDeviceManager`, `IPortableDevice`, `IPortableDeviceContent`, `IPortableDeviceProperties`, `IPortableDeviceKeyCollection`, `IPortableDevicePropVariantCollection`, and `IEnumPortableDeviceObjectIDs`) connected to the detected device `Urz?dzenie S25 u?ytkownika Pawel`. It recursively enumerated WPD object IDs and queried `WPD_OBJECT_NAME` / `WPD_OBJECT_ORIGINAL_FILE_NAME` only after checking each object's supported properties. It required exactly one filename match and a path ending in `DCIM/Camera/20261111_222543.jpg` before proceeding.

- Matched object ID: `o5A9`
- Matched path: `Pami?? wewn?trzna/DCIM/Camera/20261111_222543.jpg`
- Delete options: `PORTABLE_DEVICE_DELETE_NO_RECURSION` (`0`)
- API result: `IPortableDeviceContent::Delete` returned `S_OK` (`0x00000000`).
- Verification: a separate read-only WPD enumeration completed afterward and reported the exact target not found in `DCIM/Camera`.
- Scope: one explicitly matched device object only; no other object IDs were submitted.
- Cloud status: this probe did not inspect Google Photos.

The first C# WPD enumeration attempt requested object properties without checking support and received `0x80070032` (`ERROR_NOT_SUPPORTED`); its safety guard made no delete call. The exception dialog shown during that failed run was a managed probe error, not a phone confirmation prompt. The probe was corrected to call `GetSupportedProperties` and request only supported name properties. The corrected run found and deleted the exact target, then the separate read-only WPD check confirmed it was absent.

The inline C# prototype and project remain under `.tmp/wpd-delete-probe/` for this session. They are exploratory probe files, not production-ready application code.
