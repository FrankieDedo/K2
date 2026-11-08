# K2 — Building from source

This page explains how to build K2 and its installer yourself. If you only want
to *use* K2, grab the ready-made installer from the
[Releases](https://github.com/FrankieDedo/K2/releases) page instead — see
[Installation](Installation.md).

The short version: install two free tools, download the source, double-click
one batch file.

---

## 1. What you need

| | |
|---|---|
| OS | Windows 10 or 11, 64-bit |
| [.NET SDK](https://dotnet.microsoft.com/download) | Version **8 or newer** (the *SDK*, not just the runtime). The x64 SDK is enough: it builds the 32-bit parts too. |
| [Inno Setup 6](https://jrsoftware.org/isinfo.php) | Only needed for the installer. Keep the default options; all the installer languages K2 uses ship with it. |
| Internet | Needed on the first build: the .NET SDK downloads K2's NuGet packages. |
| Disk | About 2 GB free for the build output. |

You do **not** need Visual Studio, Base Camp, or any Mountain device to build.

[Git](https://git-scm.com/download/win) is optional — you can download the
source as a ZIP instead.

## 2. Get the source

Either clone the repository:

```
git clone https://github.com/FrankieDedo/K2.git C:\src\K2
```

or use **Code → Download ZIP** on GitHub and extract it.

> **Keep the folder path short** (for example `C:\src\K2`). Some bundled asset
> files have long names, and Windows fails with *"Filename too long"* when the
> full path goes past 260 characters. Avoid deeply nested folders such as
> `Documents\Projects\...`.

## 3. Build the installer

Double-click **`Installer\build-installer-ask.bat`**.

It asks for a version number (for example `1.4.3`); press Enter to leave it
blank for a test build. The script then:

1. publishes K2 (32-bit) and the two DisplayPad helpers (64-bit) as
   self-contained builds, so the result needs no .NET install on the target PC;
2. compiles the Inno Setup installer;
3. zips the same files as a portable package.

When it finishes, the results are in **`Installer\output\`**:

| File | What it is |
|---|---|
| `K2-Setup-<version>.exe` | The installer |
| `K2-<version>.zip` | Portable package, same files, no install |

To skip the question, run `Installer\build-installer.bat 1.4.3` from a command
prompt.

The first build takes a few minutes; most of that is downloading packages.

## 4. Build without the installer

To just compile and run K2 while working on the code:

```
dotnet build K2.sln -c Debug -p:Platform=x86
```

The app lands in `K2.App\bin\x86\Debug\net8.0-windows10.0.19041.0\K2.App.exe`.

`build-check.bat` in the repository root does a clean rebuild of every project
and writes the full output to `build-check.log` — handy for a quick
pass/fail check. Close K2 before running it.

## 5. What is not in the repository

**Mountain's native DLLs.** `MacroPadSDK.dll`, `SDKDLL.dll` and
`Everest360_USB.dll` are internal Base Camp components and cannot be
redistributed, so they are not in the source and are never put in the
installer. K2 builds without them. At run time it finds them in an existing
Base Camp install, or in a folder you point it to — see
[Installation §4.2](Installation.md#42-base-camp-dll-folder). The DisplayPad
and Makalu work without them.

## 6. Troubleshooting

| Problem | Fix |
|---|---|
| `ERROR: the .NET SDK was not found` | Install the .NET SDK (section 1), then open a **new** window and run the script again. |
| `ERROR: Inno Setup 6 compiler (ISCC.exe) not found` | Install Inno Setup 6. If you installed it in a custom folder, edit the `ISCC` path at the top of `Installer\build-installer.bat`. |
| `Filename too long` when cloning or extracting | Move the source to a shorter path such as `C:\src\K2`. |
| Errors about files being in use or that cannot be copied | K2 is still running. Close it (check the tray icon) and build again. |
| `WARNING: found non-redistributable ...dll` | A Mountain DLL ended up in the build output. Remove it before sharing the package. |
