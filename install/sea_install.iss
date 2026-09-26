; ============================================================
;  SEA 大航海时代2026 · Windows 安装包脚本 (Inno Setup 6)
;  用 ISCC 编译:  ISCC.exe sea_install.iss
;  产物:         大航海时代2026_Setup_v1.0.4.exe (放本目录)
;  注: 本文件含中文, 必须存为 UTF-8 with BOM (Inno 按 ANSI 读)。
; ============================================================

[Setup]
AppId={{8F1D4E5A-6B2C-4E7D-9A10-2F3C4D5E6F70}
AppName=大航海时代2026
AppVersion=1.0.4
AppPublisher=SEA (rulioo)
AppPublisherURL=https://github.com/rulioo/sea
AppSupportURL=https://github.com/rulioo/sea
AppComments=KOEI 大航海时代4 玩法复刻 · 原创内容

; 免 UAC 的每用户安装 (Programs 在 %LocalAppData%\Programs)
PrivilegesRequired=lowest
DefaultDirName={userpf}\大航海时代2026
DefaultGroupName=大航海时代2026
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\SEA.exe
UninstallDisplayName=大航海时代2026

; 仅 64 位 Windows
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

Compression=lzma2
SolidCompression=yes
WizardStyle=modern
OutputDir=.
OutputBaseFilename=大航海时代2026_Setup_v1.0.4

; 注: 向导默认英文 UI (本 Inno 未附简中 .isl); 产品/游戏文案均为中文。
[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务:"; Flags: unchecked

[Files]
; 整个独立版目录 (SEA.exe + SEA_Data/ + MonoBleedingEdge/ + D3D12/ 等)
Source: "E:\cc\sea\Build\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\大航海时代2026"; Filename: "{app}\SEA.exe"
Name: "{group}\卸载 大航海时代2026"; Filename: "{uninstallexe}"
Name: "{autodesktop}\大航海时代2026"; Filename: "{app}\SEA.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\SEA.exe"; Description: "立即运行 大航海时代2026"; Flags: nowait postinstall skipifsilent
