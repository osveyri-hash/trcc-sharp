Unicode true

!include "MUI2.nsh"
!include "LogicLib.nsh"

!define APPNAME    "TrccSharp"
!define VERSION    "0.2.1"
!define PUBLISHER  "TrccSharp"
!define APPEXE     "TrccSharp.exe"
!define APPICON    "TrccSharp.ico"
!define APPKEY     "Software\TrccSharp"
!define UNINSTKEY  "Software\Microsoft\Windows\CurrentVersion\Uninstall\TrccSharp"
!define RUNKEY     "Software\Microsoft\Windows\CurrentVersion\Run"
!define GITHUB     "https://github.com/osveyri-hash/trcc-sharp"

Name "${APPNAME} ${VERSION}"
OutFile "..\dist-installer\TrccSharp-${VERSION}-setup.exe"
InstallDir "$LOCALAPPDATA\Programs\${APPNAME}"
InstallDirRegKey HKCU "${APPKEY}" "InstallDir"
RequestExecutionLevel user
SetCompressor /SOLID lzma
SetCompressorDictSize 64
BrandingText "${APPNAME} ${VERSION}"

Icon "..\src\TrccSharp.App\assets\TrccSharp.ico"
UninstallIcon "..\src\TrccSharp.App\assets\TrccSharp.ico"

VIProductVersion "0.2.1.0"
VIAddVersionKey "ProductName"     "${APPNAME}"
VIAddVersionKey "FileDescription" "${APPNAME} installer"
VIAddVersionKey "FileVersion"     "${VERSION}"
VIAddVersionKey "ProductVersion"  "${VERSION}"
VIAddVersionKey "CompanyName"     "${PUBLISHER}"
VIAddVersionKey "LegalCopyright"  "${PUBLISHER}"

!define MUI_ABORTWARNING
!define MUI_ICON "..\src\TrccSharp.App\assets\TrccSharp.ico"
!define MUI_UNICON "..\src\TrccSharp.App\assets\TrccSharp.ico"
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APPEXE}"
!define MUI_FINISHPAGE_RUN_TEXT "Запустить ${APPNAME}"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "Russian"
!insertmacro MUI_LANGUAGE "English"

Var AutoStart

Section "TrccSharp" SEC_MAIN
    SectionIn RO

    ; Обновление поверх запущенной копии: закрываем её.
    nsExec::ExecToLog 'taskkill /IM ${APPEXE} /F'
    Sleep 500

    SetOutPath "$INSTDIR"
    File "..\dist\${APPEXE}"
    File "..\src\TrccSharp.App\assets\${APPICON}"
    File "..\README.md"

    WriteUninstaller "$INSTDIR\Uninstall.exe"

    CreateDirectory "$SMPROGRAMS\${APPNAME}"
    CreateShortCut "$SMPROGRAMS\${APPNAME}\${APPNAME}.lnk" "$INSTDIR\${APPEXE}" "" "$INSTDIR\${APPICON}" 0
    CreateShortCut "$SMPROGRAMS\${APPNAME}\Удалить ${APPNAME}.lnk" "$INSTDIR\Uninstall.exe" "" "$INSTDIR\Uninstall.exe" 0

    WriteRegStr HKCU "${APPKEY}" "InstallDir" "$INSTDIR"

    WriteRegStr   HKCU "${UNINSTKEY}" "DisplayName"     "${APPNAME}"
    WriteRegStr   HKCU "${UNINSTKEY}" "DisplayVersion"  "${VERSION}"
    WriteRegStr   HKCU "${UNINSTKEY}" "Publisher"       "${PUBLISHER}"
    WriteRegStr   HKCU "${UNINSTKEY}" "DisplayIcon"     "$INSTDIR\${APPEXE},0"
    WriteRegStr   HKCU "${UNINSTKEY}" "InstallLocation" "$INSTDIR"
    WriteRegStr   HKCU "${UNINSTKEY}" "URLInfoAbout"    "${GITHUB}"
    WriteRegStr   HKCU "${UNINSTKEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
    WriteRegStr   HKCU "${UNINSTKEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
    WriteRegDWORD HKCU "${UNINSTKEY}" "NoModify" 1
    WriteRegDWORD HKCU "${UNINSTKEY}" "NoRepair" 1
    WriteRegDWORD HKCU "${UNINSTKEY}" "EstimatedSize" 155000
SectionEnd

Section "Ярлык на рабочем столе" SEC_DESKTOP
    CreateShortCut "$DESKTOP\${APPNAME}.lnk" "$INSTDIR\${APPEXE}" "" "$INSTDIR\${APPICON}" 0
SectionEnd

Section "Автозапуск при входе в Windows" SEC_AUTOSTART
    StrCpy $AutoStart "on"
SectionEnd

; Выполняется всегда, даже если галочка автозапуска снята.
Section "-Autostart"
    ${If} $AutoStart == "on"
        nsExec::ExecToLog '"$INSTDIR\${APPEXE}" --set-autostart on'
    ${Else}
        nsExec::ExecToLog '"$INSTDIR\${APPEXE}" --set-autostart off'
    ${EndIf}
    Sleep 300
SectionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
    !insertmacro MUI_DESCRIPTION_TEXT ${SEC_MAIN} "Само приложение и ярлык в меню «Пуск»."
    !insertmacro MUI_DESCRIPTION_TEXT ${SEC_DESKTOP} "Положить ярлык TrccSharp на рабочий стол."
    !insertmacro MUI_DESCRIPTION_TEXT ${SEC_AUTOSTART} "Запускать TrccSharp при входе в Windows (скрыто в трее)."
!insertmacro MUI_FUNCTION_DESCRIPTION_END

; При обновлении не включаем автозапуск насильно, если пользователь его выключил.
Function .onInit
    ReadRegStr $0 HKCU "${APPKEY}" "InstallDir"
    StrCmp $0 "" done

    ReadRegStr $1 HKCU "${RUNKEY}" "${APPNAME}"
    StrCmp $1 "" 0 done

    SectionSetFlags ${SEC_AUTOSTART} 0
done:
FunctionEnd

Section "Uninstall"
    nsExec::ExecToLog 'taskkill /IM ${APPEXE} /F'
    Sleep 500

    Delete "$DESKTOP\${APPNAME}.lnk"
    Delete "$SMPROGRAMS\${APPNAME}\${APPNAME}.lnk"
    Delete "$SMPROGRAMS\${APPNAME}\Удалить ${APPNAME}.lnk"
    RMDir  "$SMPROGRAMS\${APPNAME}"

    DeleteRegValue HKCU "${RUNKEY}" "${APPNAME}"
    DeleteRegKey  HKCU "${UNINSTKEY}"
    DeleteRegKey  HKCU "${APPKEY}"

    Delete "$INSTDIR\${APPEXE}"
    Delete "$INSTDIR\${APPICON}"
    Delete "$INSTDIR\README.md"
    Delete "$INSTDIR\Uninstall.exe"
    RMDir  "$INSTDIR"
SectionEnd
