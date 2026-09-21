#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#ifndef PublishDir
  #error "PublishDir is not defined."
#endif

#ifndef OutputDir
  #define OutputDir SourcePath
#endif

[Setup]
; Single closing brace: {{ escapes to a literal {, and the single } at the end closes it, giving {GUID}. Writing }} adds a literal },
; turning the uninstall registry key into {GUID}}_is1. AppId cannot change once the first installer ships, so keep it in this canonical form.
AppId={{FC70583D-FF8A-4E92-B58B-F7F08BDB71E1}
AppName=nfa.pub Loader
AppVersion={#AppVersion}
AppPublisher=nfa.pub
DefaultDirName={localappdata}\nfa.pub Loader
DefaultGroupName=nfa.pub Loader
DisableDirPage=no
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=nfa-pub-loader-{#AppVersion}-win-x64-setup
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; The app needs at least Windows 10 1809 (net10.0-windows / Windows App SDK); without this it would install on older systems where it cannot run.
MinVersion=10.0.17763
PrivilegesRequired=lowest
WizardStyle=modern
ChangesAssociations=no
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\NfaLoader.exe
SetupIconFile={#PublishDir}\Assets\AppIcon.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\nfa.pub Loader"; Filename: "{app}\NfaLoader.exe"
Name: "{autodesktop}\nfa.pub Loader"; Filename: "{app}\NfaLoader.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\NfaLoader.exe"; Description: "{cm:LaunchProgram,nfa.pub Loader}"; Flags: nowait postinstall skipifsilent
