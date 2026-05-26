; DiskMonitor NSIS Installer Script
; Extracts to C:\DiskMonitor, creates desktop shortcut, provides built-in uninstaller.
; Run makensis from project root: makensis installer\DiskMonitor.nsi

Unicode true

!include "MUI2.nsh"
!include "LogicLib.nsh"

; Change working dir to project root so File paths are relative to it
!cd ".."

;--------------------------------
; General

Name "DiskMonitor"
OutFile "dist\DiskMonitor-Setup.exe"
InstallDir "C:\DiskMonitor"
RequestExecutionLevel admin
SetCompressor /SOLID lzma

;--------------------------------
; Version info

VIProductVersion "1.3.0.0"
VIAddVersionKey "ProductName"      "DiskMonitor"
VIAddVersionKey "ProductVersion"   "1.3.0"
VIAddVersionKey "FileDescription"  "DiskMonitor Setup"
VIAddVersionKey "LegalCopyright"   "GLWT License"

;--------------------------------
; Interface settings

!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN      "$INSTDIR\DiskMonitor.Frontend.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Launch DiskMonitor"

;--------------------------------
; Pages

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH

;--------------------------------
; Languages

!insertmacro MUI_LANGUAGE "SimpChinese"
!insertmacro MUI_LANGUAGE "English"

;--------------------------------
; Installer section

Section "MainSection" SEC01

  SetOutPath "$INSTDIR"
  SetOverwrite on

  ; Copy all frontend + service files (NSIS-edition build with auto-install dialog)
  File /r "publish\DiskMonitor-Nsis\*.*"

  ; Create desktop shortcut
  CreateShortcut "$DESKTOP\DiskMonitor.lnk" \
    "$INSTDIR\DiskMonitor.Frontend.exe" "" \
    "$INSTDIR\DiskMonitor.Frontend.exe" 0 \
    SW_SHOWNORMAL "" "DiskMonitor"

  ; Write uninstaller
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  ; Register in Add/Remove Programs
  WriteRegStr   HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\DiskMonitor" \
    "DisplayName"     "DiskMonitor"
  WriteRegStr   HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\DiskMonitor" \
    "DisplayVersion"  "1.3.0"
  WriteRegStr   HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\DiskMonitor" \
    "Publisher"       "DiskMonitor Project"
  WriteRegStr   HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\DiskMonitor" \
    "UninstallString" "$\"$INSTDIR\Uninstall.exe$\""
  WriteRegStr   HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\DiskMonitor" \
    "InstallLocation" "$INSTDIR"
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\DiskMonitor" \
    "NoModify" 1
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\DiskMonitor" \
    "NoRepair"  1

SectionEnd

;--------------------------------
; Uninstaller section

Section "Uninstall"

  ; Remove Add/Remove Programs entry
  DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\DiskMonitor"

  ; Remove desktop shortcut
  Delete "$DESKTOP\DiskMonitor.lnk"
  ClearErrors

  ; Remove installation directory
  ; Note: %ProgramData%\DiskMonitor\ (service data + database) is NOT touched
  RMDir /r "$INSTDIR"

SectionEnd
