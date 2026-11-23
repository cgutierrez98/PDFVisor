; Inno Setup 6 script for PDFVisor (Windows x64)
#ifndef MyAppVersion
  #define MyAppVersion "0.8.0"
#endif

[Setup]
AppId={{7E8C9F2A-4D51-4C6B-9E3A-1D7F8B4A2C90}
AppName=PDFVisor
AppVersion={#MyAppVersion}
AppPublisher=PDFVisor
DefaultDirName={autopf}\PDFVisor
DefaultGroupName=PDFVisor
AllowNoIcons=yes
OutputDir=..\dist\installer
OutputBaseFilename=PDFVisor-Setup-{#MyAppVersion}-win-x64
SetupIconFile=..\src\desktop\PDFVisor.App\Assets\PDFVisor.ico
UninstallDisplayIcon={app}\PDFVisor.exe
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
ChangesAssociations=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "associatepdf"; Description: "Asociar archivos .pdf con PDFVisor"; GroupDescription: "Integración con Windows"

[Files]
Source: "..\dist\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\PDFVisor"; Filename: "{app}\PDFVisor.exe"
Name: "{group}\Desinstalar PDFVisor"; Filename: "{uninstallexe}"
Name: "{autodesktop}\PDFVisor"; Filename: "{app}\PDFVisor.exe"; Tasks: desktopicon

[Registry]
; Register PDFVisor as a handler for .pdf files (Open With + optional default)
Root: HKA; Subkey: "Software\Classes\.pdf\OpenWithProgids"; ValueType: string; ValueName: "PDFVisor.Document"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associatepdf
Root: HKA; Subkey: "Software\Classes\PDFVisor.Document"; ValueType: string; ValueName: ""; ValueData: "Documento PDF (PDFVisor)"; Flags: uninsdeletekey; Tasks: associatepdf
Root: HKA; Subkey: "Software\Classes\PDFVisor.Document\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\PDFVisor.exe,0"; Tasks: associatepdf
Root: HKA; Subkey: "Software\Classes\PDFVisor.Document\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\PDFVisor.exe"" ""%1"""; Tasks: associatepdf

[Run]
Filename: "{app}\PDFVisor.exe"; Description: "{cm:LaunchProgram,PDFVisor}"; Flags: nowait postinstall skipifsilent
