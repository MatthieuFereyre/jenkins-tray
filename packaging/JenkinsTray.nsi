; Installer for Jenkins Tray, compiled by makensis — which runs on Linux, like everything else in
; this build. It replaces an MSI written by wixl: every capability that package needed ended up as
; a table rewritten after the fact, because wixl expresses almost nothing, and both bugs it shipped
; came from Windows Installer rules — a versioned file refusing to be replaced by one declared
; unversioned, and a DLL held by the running process. Neither rule exists here: a file is copied.
;
; Compiled with: makensis -DVERSION=1.1.42 -DSOURCE_DIR=... -DICON=... -DOUT_FILE=... JenkinsTray.nsi
; Source paths use forward slashes (they are read on Linux), target paths use backslashes.

Unicode true

!include "MUI2.nsh"
!include "WinVer.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"

!ifndef VERSION
  !error "VERSION is required"
!endif
!ifndef SOURCE_DIR
  !error "SOURCE_DIR is required"
!endif
!ifndef OUT_FILE
  !error "OUT_FILE is required"
!endif
!ifndef ICON
  !error "ICON is required"
!endif

!define APP_NAME "Jenkins Tray"
!define APP_EXE "JenkinsTray.exe"
!define PUBLISHER "Matthieu Fereyre"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\JenkinsTray"
!define RUN_KEY "Software\Microsoft\Windows\CurrentVersion\Run"

Name "${APP_NAME}"
OutFile "${OUT_FILE}"
InstallDir "$LOCALAPPDATA\Programs\${APP_NAME}"
; A personal desktop utility: no service, nothing outside the user profile, no elevation.
RequestExecutionLevel user
SetCompressor /SOLID lzma
ShowInstDetails show
ShowUninstDetails show

VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "${APP_NAME}"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "FileVersion" "${VERSION}.0"
VIAddVersionKey "CompanyName" "${PUBLISHER}"
VIAddVersionKey "FileDescription" "${APP_NAME} Setup"
VIAddVersionKey "LegalCopyright" "${PUBLISHER}"

!define MUI_ICON "${ICON}"
!define MUI_UNICON "${ICON}"
!define MUI_ABORTWARNING
; No page before the progress: double-clicking installs, which is what the MSI did.
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_UNPAGE_INSTFILES

; English first: MUI falls back to the language declared first when the system matches none of
; them, and the project is published in English.
!insertmacro MUI_LANGUAGE "English"
!insertmacro MUI_LANGUAGE "French"

; The installer has no interface of its own beyond a progress list, so these five lines are all
; of its text. $LANGUAGE is resolved from the system, the same way the application defaults to
; the system language before anyone opens its settings.
LangString MsgNeedsWin10 ${LANG_ENGLISH} "${APP_NAME} requires Windows 10 or a more recent release."
LangString MsgNeedsWin10 ${LANG_FRENCH}  "${APP_NAME} nécessite Windows 10 ou une version plus récente."
LangString MsgClosing    ${LANG_ENGLISH} "Closing ${APP_NAME}..."
LangString MsgClosing    ${LANG_FRENCH}  "Fermeture de ${APP_NAME}..."
LangString MsgRemoving   ${LANG_ENGLISH} "Removing the previous version..."
LangString MsgRemoving   ${LANG_FRENCH}  "Retrait de la version précédente..."
LangString MsgStarting   ${LANG_ENGLISH} "Starting ${APP_NAME}..."
LangString MsgStarting   ${LANG_FRENCH}  "Démarrage de ${APP_NAME}..."

;-------------------------------------------------------------------------------------------------
; Nothing here removes the MSI product the previous packaging left behind: that path is uninstalled
; by hand, once, and an msiexec call nested inside this installer only ever failed and retried.
Function .onInit
  ; VersionNT stops at 6.3 for anything from Windows 8.1 onwards unless the installer is manifested
  ; for later releases; NSIS is, so AtLeastWin10 answers truthfully.
  ${IfNot} ${AtLeastWin10}
    MessageBox MB_ICONSTOP "$(MsgNeedsWin10)"
    Abort
  ${EndIf}
  SetShellVarContext current
FunctionEnd

Section "Install"
  ; Windows will not let anyone overwrite a file the running application has mapped. Closing it is
  ; safe: settings are written on every change, never on the way out.
  DetailPrint "$(MsgClosing)"
  nsExec::Exec 'taskkill /IM ${APP_EXE} /F'
  Pop $0
  Sleep 500

  ; Emptying the folder first is what guarantees no file of the previous version survives — the
  ; whole point of this installer. Guarded on the executable so a wrong INSTDIR removes nothing.
  ${If} ${FileExists} "$INSTDIR\${APP_EXE}"
    DetailPrint "$(MsgRemoving)"
    RMDir /r "$INSTDIR"
  ${EndIf}

  SetOutPath "$INSTDIR"
  File /r "${SOURCE_DIR}/*"

  ; The application rewrites this shortcut on first run to attach its AppUserModelID: Windows only
  ; shows toasts from a desktop app whose identity is backed by a Start-menu shortcut.
  CreateShortcut "$SMPROGRAMS\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0

  WriteUninstaller "$INSTDIR\Uninstall.exe"

  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "${APP_NAME}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\${APP_EXE},0"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "${PUBLISHER}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "EstimatedSize" $0

  ; Closed above, so it comes back — otherwise nothing would be watching the builds any more, and
  ; nothing would say so. On a first install it simply starts the application that was just put in.
  DetailPrint "$(MsgStarting)"
  Exec '"$INSTDIR\${APP_EXE}"'
  SetAutoClose true
SectionEnd

;-------------------------------------------------------------------------------------------------
Function un.onInit
  SetShellVarContext current
FunctionEnd

Section "Uninstall"
  ; Same lock, same answer: an open application would keep its own folder alive.
  nsExec::Exec 'taskkill /IM ${APP_EXE} /F'
  Pop $0
  Sleep 500

  Delete "$SMPROGRAMS\${APP_NAME}.lnk"
  RMDir /r "$INSTDIR"
  DeleteRegKey HKCU "${UNINSTALL_KEY}"
  ; Written by the application when "start with Windows" is on; left behind it would point at an
  ; executable that no longer exists. The MSI packaging never removed it.
  DeleteRegValue HKCU "${RUN_KEY}" "JenkinsTray"
SectionEnd
