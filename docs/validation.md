# Local validation

Validated on 2026-09-25 using Windows 11 Pro, build 26200.

## Automated tests

`dotnet test VirtualWebDav.sln -c Release --no-restore` passed: **19 tests, zero failures**. The first pass used SDK 10.0.401 targeting `net8.0`, and executed with the installed .NET / ASP.NET Core 8.0.31 runtime.

A separate local build and test run with **SDK 8.0.404** also passed all 19 tests. This SDK was installed in `%LOCALAPPDATA%\Microsoft\dotnet8` from Microsoft's official archive, verified against its published SHA-512 checksum. The current SDK download endpoints reset connections on this machine, so this validation used an accessible archived SDK. It does not replace the system SDK or change PATH.

GitHub Actions also passed on both **Windows and Linux**, using the workflow's .NET 8 SDK setup, and generated package/test artifacts: [successful implementation CI run](https://github.com/sbarisic/WebDAV/actions/runs/36122732062).

Coverage includes all implemented methods; Unicode/escaped names; empty files; recursive and shallow copies; case-only renames; overwrite conflicts; ETag races; custom properties and protected-property rollback; exclusive/collection locks, refresh, expiry and destination lock retention; host/origin rejection; malformed paths/XML; XML size limits; non-seekable downloads; range requests; and incomplete network uploads preserving prior content.

A 32 MiB synthetic upload/download test uses generated input and a counting sink, without retaining the content. It verifies exact transfer size, successful commit, stream disposal, and provider read/write calls no larger than 64 KiB. The in-memory sample intentionally retains its files and is not the bounded-storage benchmark.

`dotnet pack src/VirtualWebDav/VirtualWebDav.csproj -c Release --no-build -o artifacts` produced `VirtualWebDav.0.1.0.nupkg` successfully.

## Real Windows mapped drive

The Release sample ran at `http://localhost:8096/` and was rechecked at port 8097 after the smoke script gained automatic restoration of mapping preferences. This command completed successfully:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Test-WindowsDrive.ps1 -Port 8096
```

Result:

```text
PASS: map, enumerate, copy in/out, read, edit, copy, rename, move, temporary-file save, disconnect/reconnect, recursive delete.
```

The nonpersistent `W:` mapping and generated test folders were removed afterward. The original network-mapping persistence preference was restored. The sample process was stopped after validation.

WebClient was initially stopped and started automatically when Windows mapped the drive. No service startup configuration, authentication settings, or file-transfer limits were changed. This machine's WebClient file-transfer limit was 50,000,000 bytes; the smoke test uses small files.

PowerShell 7 directory enumeration exposed trailing null characters and recursive enumeration failed. Windows PowerShell 5.1 and native `dir` worked. This matches the independently reported [Windows WebDAV / .NET enumeration issue](https://github.com/dotnet/runtime/issues/46723). The smoke script requires Windows PowerShell to keep this client limitation visible rather than silently trimming names.

The acceptance test exercises filesystem operations through the real redirector; it does not claim interactive Explorer UI testing or Microsoft Office compatibility.
