# Samsung S25 MTP read-only probe

## What was tested

The phone was connected to Windows by USB-C, unlocked, and set to **File Transfer**. A small PowerShell probe used the built-in Windows `Shell.Application` COM automation object to browse the `This PC` namespace and enumerate the S25 storage. It listed the phone's folders and `DCIM/Camera` files without copying, moving, writing, or deleting anything.

This is a working **Windows Shell COM** MTP probe. It demonstrates application-level enumeration on this Windows PC with this Samsung Galaxy S25. It is not a direct call to Microsoft's lower-level WPD COM interfaces, and it does not yet demonstrate file transfer from a custom C# application.

## Reproduce the folder enumeration

Run in Windows PowerShell while the phone is connected, unlocked, and in File Transfer mode:

```powershell
$shell = New-Object -ComObject Shell.Application
$thisPc = $shell.Namespace(17) # Shell namespace ID for This PC
$thisPc.Items() |
    ForEach-Object {
        [pscustomobject]@{
            Name = $_.Name
            IsFolder = $_.IsFolder
            Path = $_.Path
        }
    } |
    Format-Table -AutoSize
```

To list the internal storage folders:

```powershell
$shell = New-Object -ComObject Shell.Application
$device = $shell.Namespace(17).Items() |
    Where-Object Name -Like '*S25*' |
    Select-Object -First 1
$device.GetFolder.Items() |
    ForEach-Object {
        [pscustomobject]@{ Name = $_.Name; IsFolder = $_.IsFolder }
    } |
    Format-Table -AutoSize
```

To list files in `DCIM/Camera`:

```powershell
$shell = New-Object -ComObject Shell.Application
$device = $shell.Namespace(17).Items() |
    Where-Object Name -Like '*S25*' |
    Select-Object -First 1
$storage = $device.GetFolder.Items() |
    Where-Object Name -Like '*wewnętrzna*' |
    Select-Object -First 1
$dcim = $storage.GetFolder.Items() |
    Where-Object Name -EQ 'DCIM' |
    Select-Object -First 1
$camera = $dcim.GetFolder.Items() |
    Where-Object Name -EQ 'Camera' |
    Select-Object -First 1
$folder = $camera.GetFolder
$folder.Items() |
    ForEach-Object {
        [pscustomobject]@{
            Name = $_.Name
            Type = $_.Type
            Modified = $folder.GetDetailsOf($_, 3)
        }
    } |
    Sort-Object Name -Descending |
    Select-Object -First 20 |
    Format-Table -AutoSize
```

The storage name filter above matches the Polish label observed on this PC (`Pamięć wewnętrzna`). For a reusable tool, inspect the device's child folders and select the storage folder by capability or by a configurable name rather than relying on this localized label.

## Observed result

- `This PC` enumerated **Urządzenie S25 użytkownika Pawel** and local disk `C:`.
- The S25 exposed **Pamięć wewnętrzna** (internal storage).
- Internal storage enumeration showed `DCIM`, `Pictures`, `Download`, and other folders.
- `DCIM/Camera` returned photo and video filenames and Windows Shell's **Modified** detail.
- The newest visible phone camera file during this probe was `20261110_143547.jpg`.
- The user-reported `20261111_214936.jpg` photo, previously deleted manually from the phone, was no longer listed locally.

## Direct WPD probe follow-up

A C# probe using Microsoft's WPD COM API has since enumerated this S25 and directly deleted the single user-authorized test photo `20261111_222543.jpg` from `DCIM/Camera`. It matched the photo to WPD object ID `o5A9`, called `IPortableDeviceContent::Delete` with `PORTABLE_DEVICE_DELETE_NO_RECURSION`, received `S_OK`, and verified with a later read-only WPD enumeration that the target was no longer listed. See [the photo evidence record](samsung-s25-photo-20261111_222543-evidence.md) for details.

The WPD probe queried `GetSupportedProperties` before requesting filename properties; a first exploratory query without that check returned `ERROR_NOT_SUPPORTED` and did not delete anything. The successful probe demonstrates device-side deletion only. It did not inspect or change the Google Photos cloud copy.

The earlier Shell COM probe remains read-only evidence for browsing. Shell names and localized folder labels vary, and object enumeration or property retrieval can fail for unsupported fields or device state changes; check for exactly one intended object and verify the result through a fresh enumeration.
