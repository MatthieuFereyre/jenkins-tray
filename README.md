# Jenkins Tray

A small Windows utility that watches Jenkins jobs from the notification area. When a monitored
build finishes, a system notification reports the result, the tests and the coverage — and clicking
it opens **the job** in your browser (not the build).

Inspired by [zionyx/jenkins-tray](https://github.com/zionyx/jenkins-tray), rewritten in C# / WPF
with a Fluent interface (Windows 11).

## Features

- **Several Jenkins instances** at once, gathered into a single view and a single tray icon.
- **Job explorer**: a tree of folders, subfolders and multibranch projects, with a name filter and
  checkboxes to pick what gets monitored.
- **Dashboard**: status, build number, duration, tests (passed / failed / skipped) and code
  coverage (lines and branches). Cards sort by server, name, status, last build, duration or
  coverage — the choice and its direction are remembered.
- **Start a build** from the job's card. The card breathes while the build runs, from the request
  through to the end. Needs the *Job/Build* permission on the configured account; everything else
  only needs read access.
- **Native Windows notifications** when a build ends, with the test and coverage detail. Clicking
  one opens the job's page.
- **A tray icon** coloured by the worst status among monitored jobs, pulsing during a build, with a
  menu listing the jobs and their latest result.
- **Themes**: light / dark / system, Mica effect, start with Windows, minimise to the tray.
- **English and French**, switchable at any time from the settings, defaulting to your Windows
  language.

## Requirements

- Windows 10 1809+ or Windows 11
- [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) (or the SDK to build)

## Install

Download `JenkinsTray-Setup-<version>.exe` from the
[latest release](../../releases/latest) and run it.

The installer is **per-user** and needs no elevation: it installs into
`%LOCALAPPDATA%\Programs\Jenkins Tray` with a Start-menu shortcut. It is not code-signed, so
Windows SmartScreen will warn on first run — *More info* → *Run anyway*.

Silent install:

```bash
JenkinsTray-Setup-1.1.42.exe /S
```

## Build and run

```bash
dotnet run --project src/JenkinsTray/JenkinsTray.csproj
```

Produce a single executable:

```bash
dotnet publish src/JenkinsTray/JenkinsTray.csproj -c Release -r win-x64 -p:PublishSingleFile=true --self-contained false
```

## Configuration

1. **Settings → Add a server**: name, instance URL, user and API token.
   Generate the token in Jenkins: *your profile → Configure → API tokens*.
   "Test the connection" validates the URL and the credentials before saving.
2. **Jobs**: pick the server, tick the jobs to watch. The selection is saved immediately.
3. **Dashboard**: the state of the monitored jobs, refreshed on the poll interval (30 s by default).

Settings live in `%APPDATA%\JenkinsTray\settings.json`. API tokens are encrypted with **DPAPI**:
they can only be decrypted by your Windows session on that machine.

### Backups

Every write shifts the three previous versions into `settings.backup1.json` through
`settings.backup3.json`, in the same folder. A write that changes nothing does not consume a
generation, and a mere display preference (the dashboard sort) is saved without consuming one
either — otherwise a few clicks would be enough to push the whole configuration out of the backups.
To roll back: close the application, rename the backup you want to `settings.json`, start again.

### Isolated profile

`--data-dir <path>` (or the `JENKINSTRAY_DATA_DIR` environment variable) moves *everything* the
application writes — configuration, backups, logs, icons — to another folder. Useful for testing,
for a demo, or to keep a second profile without ever touching the real configuration. Each data
folder gets its own instance: an isolated profile runs alongside the normal one instead of being
absorbed by the single-instance check.

```bash
JenkinsTray.exe --data-dir C:\temp\jenkins-tray-demo
```

## Code coverage

There is no single standard on the Jenkins side, so the tool queries the endpoints of the common
plugins and keeps the first that answers:

| Order | Plugin | Endpoint |
|---|---|---|
| 1 | Coverage (`io.jenkins.plugins.coverage.metrics`) | `<build>/coverage/api/json?tree=projectStatistics[*]` |
| 2 | Code Coverage API v1 | `<build>/coverage/result/api/json` |
| 3 | JaCoCo | `<build>/jacoco/api/json` |
| 4 | Cobertura | `<build>/cobertura/api/json` |

If none answers, the card and the notification simply show the tests. Test counts come from the
build's JUnit action, falling back to `<build>/testReport/api/json`.

These requests are only issued when an unknown build is detected, not on every poll.

## Notifications

Windows only shows notifications from a desktop application whose AppUserModelID is backed by a
Start-menu shortcut. So on first run the application creates
`%APPDATA%\Microsoft\Windows\Start Menu\Programs\Jenkins Tray.lnk`, carrying that identifier and
the activation CLSID. Without that shortcut, notifications are accepted by the system and then
silently dropped.

If you move the executable, the shortcut is recreated on the next start.

### Job naming

On a multibranch project a job is named after its branch: a lone `master` identifies nothing, since
every project has one. So everywhere a job is named — notification, dashboard, tray menu — it is
announced as **`project › branch`**. Folders above the project move to a second line (the
notification's attribution text, the card's subtitle).

## Languages

The interface ships in English and French. **Settings → Language** switches between them, and
applies immediately — no restart. Left on *System*, it takes French on a French Windows and English
everywhere else.

Both languages are embedded in the executable as
[`Strings_en.resx`](src/JenkinsTray/Resources/Strings_en.resx) and
[`Strings_fr.resx`](src/JenkinsTray/Resources/Strings_fr.resx), selected explicitly by
[`Loc`](src/JenkinsTray/Services/Loc.cs) rather than through .NET satellite assemblies — the
language is a setting, not a property of the machine. English is the fallback for any key a
translation is missing, so an untranslated string degrades to English instead of showing blank.

**Adding a language** means adding `Strings_<code>.resx` alongside the other two, a value in
`AppLanguage`, a `ResourceManager` in `Loc`, and one `ComboBoxItem` in the settings page. XAML
reaches every string through `{loc:Tr Some_Key}`, which binds rather than resolving once, which is
what lets a switch reach the interface with no restart.

## Installer

The installer is built **on Linux**, by **NSIS** (`makensis`). The application itself
cross-compiles for Windows from anywhere, Linux included: the targeting packs come from NuGet and
`EnableWindowsTargeting` fetches them. One command:

```bash
bash packaging/build-installer.sh 1.1.0 artifacts
```

It publishes for `win-x64`, runs `makensis` on `packaging/JenkinsTray.nsi`, and drops the installer
in `artifacts/`. It ends by reading back what it just wrote — the installer's version resource, and
the count of packaged files, read out of the `makensis` log since a solid LZMA block cannot be read
back — because no Linux agent can install it to make sure.

So the machine needs the **.NET 9 SDK** and **`makensis`** (the `nsis` package on Debian).

### What the installer does

A **per-user** install, without elevation, into `%LOCALAPPDATA%\Programs\Jenkins Tray`, with a
Start-menu shortcut. The application rewrites that shortcut on first run to attach its
AppUserModelID — without which Windows shows no notifications (see above).

It **closes Jenkins Tray before copying, and starts it again afterwards**. Windows lets nobody
overwrite a DLL a process has loaded, and the application saves its settings on every change and
never writes anything on the way out: stopping it loses nothing. One consequence worth knowing: on
a fresh install too, the application starts by itself at the end.

The install folder is **emptied before the copy**, which guarantees no file of the previous version
survives. The settings live elsewhere (`%APPDATA%\JenkinsTray`) and are never touched.

The uninstaller removes the files, the shortcut, the Add/Remove entry and the `Run` value the
application writes when "start with Windows" is on.

### Version number

The number is decided in the repository, in the two places that must stay aligned:

| File | Property |
|---|---|
| `src/JenkinsTray/JenkinsTray.csproj` | `Version` — the source of the number, the one the installer reads |
| `src/JenkinsTray/app.manifest` | `assemblyIdentity version` (four components) |

In CI, **the build number takes the third field**: the repository decides `1.1`, and build 42
produces `JenkinsTray-Setup-1.1.42.exe`.

### Releasing

Pushing a tag like `v1.1.0` runs
[`.github/workflows/release.yml`](.github/workflows/release.yml), which builds the installer on a
Linux runner and attaches it to a GitHub release. The workflow can also be started by hand from the
Actions tab, which builds the installer and uploads it as an artifact without publishing a release.

## Layout

```
src/JenkinsTray/
  Models/        persisted state and Jenkins API models
  Resources/     the interface strings, one .resx per language
  Services/      REST client, polling, notifications, icons, encrypted storage, localization
  ViewModels/    MVVM (CommunityToolkit.Mvvm)
  Views/         WPF windows and pages, tray icon host
packaging/       building the installer on Linux (NSIS)
```

Diagnostics: `%APPDATA%\JenkinsTray\jenkins-tray.log` and `error.log`.

## Licence

[MIT](LICENSE).
