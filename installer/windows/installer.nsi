; -----------------------------------------------------------------------------
; ClaudePanel Bridge — Windows installer (NSIS)
;
; Per-user install (no admin required). Registers in Apps & Features so the
; user can uninstall via Settings, drops the binary in %LOCALAPPDATA%, and
; auto-registers the scheduled task that runs the bridge at login.
;
; Parameterize at build time with /D flags:
;   makensis /DVERSION=0.2.2 /DBINARY=path\to\ClaudeStatusBridge.exe installer.nsi
; -----------------------------------------------------------------------------

!ifndef VERSION
  !define VERSION "0.0.0-dev"
!endif
!ifndef BINARY
  !define BINARY "ClaudeStatusBridge.exe"
!endif

!define APPNAME      "ClaudePanel Bridge"
!define APPID        "ClaudePanelBridge"
!define EXENAME      "ClaudeStatusBridge.exe"
!define COMPANY      "sep"
!define UNINST_KEY   "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPID}"

Name        "${APPNAME}"
OutFile     "ClaudePanelBridge-${VERSION}-Setup.exe"
InstallDir  "$LOCALAPPDATA\${APPID}"
InstallDirRegKey HKCU "Software\${APPID}" "InstallDir"

RequestExecutionLevel user
SetCompressor /SOLID lzma
ShowInstDetails show
ShowUninstDetails show

; ---------------------------------------------------------------------------
; Pages
; ---------------------------------------------------------------------------
Page directory
Page instfiles
UninstPage uninstConfirm
UninstPage instfiles

; ---------------------------------------------------------------------------
; Install
; ---------------------------------------------------------------------------
Section "Install"
  ; Make sure no instance is running before we copy the new EXE in.
  ; nsExec::ExecToLog runs the child with no visible console window and
  ; pipes output into the installer details pane — no flash. Wrap in
  ; cmd /c with redirection so taskkill's "process not found" stderr on
  ; first install (when nothing's running yet) doesn't show up as a
  ; scary red ERROR line in the details pane.
  nsExec::ExecToLog 'cmd /c taskkill /F /IM "${EXENAME}" >nul 2>&1'
  Pop $0
  Sleep 500

  SetOutPath "$INSTDIR"
  File "/oname=${EXENAME}" "${BINARY}"

  ; Start Menu shortcuts
  CreateDirectory "$SMPROGRAMS\${APPNAME}"
  CreateShortCut  "$SMPROGRAMS\${APPNAME}\${APPNAME}.lnk" "$INSTDIR\${EXENAME}"
  CreateShortCut  "$SMPROGRAMS\${APPNAME}\Uninstall ${APPNAME}.lnk" "$INSTDIR\Uninstall.exe"

  ; Apps & Features registration (per-user, HKCU)
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayName"     "${APPNAME}"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayVersion"  "${VERSION}"
  WriteRegStr HKCU "${UNINST_KEY}" "Publisher"       "${COMPANY}"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayIcon"     "$INSTDIR\${EXENAME}"
  WriteRegStr HKCU "${UNINST_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKCU "${UNINST_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegStr HKCU "${UNINST_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoRepair" 1

  ; Remember our install directory for upgrades
  WriteRegStr HKCU "Software\${APPID}" "InstallDir" "$INSTDIR"

  ; Write the uninstaller
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  ; Register the autostart entry (HKCU Run key) and launch the tray.
  ; `bridge install` writes the Run value AND spawns the tray itself,
  ; so this single call covers both registration and "start now".
  ; Failure is non-fatal — the user can flip the tray menu's "Run on
  ; login" toggle later to retry.
  nsExec::ExecToLog '"$INSTDIR\${EXENAME}" install'
  Pop $0
SectionEnd

; ---------------------------------------------------------------------------
; Uninstall
; ---------------------------------------------------------------------------
Section "Uninstall"
  ; Deregister scheduled task and stop any running instance.
  ; Same cmd /c redirection trick as the install section: silences
  ; taskkill's harmless "process not found" stderr if the user already
  ; quit the tray app from the menu before uninstalling.
  nsExec::ExecToLog '"$INSTDIR\${EXENAME}" uninstall'
  Pop $0
  nsExec::ExecToLog 'cmd /c taskkill /F /IM "${EXENAME}" >nul 2>&1'
  Pop $0
  Sleep 500

  Delete "$INSTDIR\${EXENAME}"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir  "$INSTDIR"

  Delete "$SMPROGRAMS\${APPNAME}\${APPNAME}.lnk"
  Delete "$SMPROGRAMS\${APPNAME}\Uninstall ${APPNAME}.lnk"
  RMDir  "$SMPROGRAMS\${APPNAME}"

  DeleteRegKey HKCU "${UNINST_KEY}"
  DeleteRegKey HKCU "Software\${APPID}"
SectionEnd
