#ifndef AppVersion
  #error Pass AppVersion from the published GlyphLatch executable.
#endif
#ifndef PublishDir
  #error Pass PublishDir with the published GlyphLatch executable.
#endif

[Setup]
AppId={{DC69D787-C004-4F3C-8328-5D241C5EDE47}
AppName=GlyphLatch
AppVersion={#AppVersion}
AppPublisher=GlyphLatch
AppPublisherURL=https://github.com/flecto2107/GlyphLatch
AppSupportURL=https://github.com/flecto2107/GlyphLatch/issues
AppUpdatesURL=https://github.com/flecto2107/GlyphLatch/releases
VersionInfoVersion={#AppVersion}
VersionInfoProductName=GlyphLatch
MinVersion=10.0.22000
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\GlyphLatch
DefaultGroupName=GlyphLatch
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\GlyphLatch.exe
SetupIconFile=..\src\GlyphLatch.Windows\Assets\glyphlatch.ico
OutputBaseFilename=GlyphLatch-Setup-{#AppVersion}-win-x64
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
AppMutex=Local\GlyphLatch.Windows
CloseApplications=no
RestartApplications=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Messages]
russian.SetupAppRunningError=GlyphLatch работает. Выберите «Закрыть GlyphLatch» в меню трея, затем нажмите «OK», чтобы продолжить установку.
russian.UninstallAppRunningError=GlyphLatch работает. Выберите «Закрыть GlyphLatch» в меню трея, затем нажмите «OK», чтобы продолжить удаление.

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; Flags: unchecked

[Files]
Source: "{#PublishDir}\GlyphLatch.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\src\GlyphLatch.Windows\Assets\Fonts\OFL-Medeo.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\src\GlyphLatch.Windows\Assets\Lucide\LICENSE-Lucide.txt"; DestDir: "{app}"; Flags: ignoreversion
#ifdef LocalLoadingGlyphs
Source: "{#PublishDir}\LoadingGlyphs.local.txt"; DestDir: "{app}"; Flags: ignoreversion
#endif

[Icons]
Name: "{group}\GlyphLatch"; Filename: "{app}\GlyphLatch.exe"
Name: "{userdesktop}\GlyphLatch"; Filename: "{app}\GlyphLatch.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\GlyphLatch.exe"; Description: "Открыть GlyphLatch"; Flags: nowait postinstall skipifsilent
