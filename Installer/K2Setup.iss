; K2Setup.iss - Inno Setup script for K2.
;
; Do NOT run ISCC on this file directly: it expects the self-contained
; publish output already staged under Installer\publish\K2.App by
; build-installer.bat (that script runs the dotnet publish steps first,
; then invokes ISCC on this file). Everything referenced here lives
; inside K2\, except the top-level LICENSE, which build-installer.bat
; leaves in place one level up (see project layout in _PROJECT_MAP.md).

#define MyAppName "K2"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#define MyAppPublisher "K2 Project (community, non-commercial)"
#define MyAppExeName "K2.App.exe"

[Setup]
AppId={{5C6A9E2A-6C6F-4C7B-9C64-1B7B6C7C7A21}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\K2
DefaultGroupName=K2
PrivilegesRequiredOverridesAllowed=dialog
DisableProgramGroupPage=yes
OutputDir=output
OutputBaseFilename=K2-Setup-{#MyAppVersion}
SetupIconFile=..\K2.App\Assets\K2_icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
LicenseFile=..\..\LICENSE
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

; Two or more [Languages] entries make Inno show its language-picker
; dialog at setup startup automatically (ShowLanguageDialog defaults to
; "auto" - one language would just skip it silently, so no explicit
; directive is needed here). Matches the languages K2 itself ships
; (K2.Core/Strings.*.xml), minus Chinese: Inno Setup has no official
; Simplified Chinese translation of its own built-in wizard text
; (Welcome/License/Ready/... pages), and a from-scratch one is out of
; scope here - a Chinese-speaking user can still pick Chinese as K2's
; own UI language after install, from the Settings tab.
[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "italian"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "polish"; MessagesFile: "compiler:Languages\Polish.isl"
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

; This file is saved as UTF-8 with a BOM (required so Inno Setup parses
; the Japanese/Korean text below correctly instead of falling back to
; the OS ANSI codepage - without a BOM those two would turn to mojibake,
; and non-ASCII Western European accents would be at risk too).
[CustomMessages]
english.BcPageCaption=Base Camp Components
english.BcPageDescription=K2 talks to some of your devices through three DLL files that are contained inside Base Camp installation folder. Tell K2 where to find them - you can change this later from K2's Settings tab.
english.BcRadioDetect=Use the Base Camp installation found on this PC:
english.BcNotFound=No Base Camp installation was detected automatically. Choose "Specify a folder manually" below and browse to the folder that contains the DLL files (MacroPadSDK.dll, SDKDLL.dll, Everest360_USB.dll) - for example, a Base Camp install on another drive, or a folder where you copied just the DLLs.
english.BcRadioManual=Specify a folder manually:
english.BcBrowse=Browse...
english.BcBrowsePrompt=Select the folder containing the Base Camp DLL files
english.BcNoDllConfirm=No Base Camp DLL (MacroPadSDK.dll, SDKDLL.dll, Everest360_USB.dll) was found in that folder. Continue anyway?
english.BehaviorPageCaption=Base Camp Startup Behavior
english.BehaviorPageSubCaption=Choose how K2 interacts with Base Camp automatically. Both can be changed later from K2's Settings tab.
english.BehaviorPageDescription=These affect every K2 startup and shutdown, not just this install.
english.CkAutoStop=Close Base Camp automatically when K2 starts
english.CkRestartOnClose=Restart Base Camp automatically when K2 closes

italian.BcPageCaption=Componenti di Base Camp
italian.BcPageDescription=K2 comunica con alcuni dei tuoi dispositivi tramite tre file DLL contenuti nella cartella di installazione di Base Camp. Indica a K2 dove trovarle - puoi cambiare questa scelta in seguito dalla scheda Impostazioni di K2.
italian.BcRadioDetect=Usa l'installazione di Base Camp trovata su questo PC:
italian.BcNotFound=Nessuna installazione di Base Camp è stata rilevata automaticamente. Seleziona "Specifica una cartella manualmente" qui sotto e sfoglia fino alla cartella che contiene i file DLL (MacroPadSDK.dll, SDKDLL.dll, Everest360_USB.dll) - ad esempio un'installazione di Base Camp su un altro disco, oppure una cartella in cui hai copiato solo le DLL.
italian.BcRadioManual=Specifica una cartella manualmente:
italian.BcBrowse=Sfoglia...
italian.BcBrowsePrompt=Seleziona la cartella contenente i file DLL di Base Camp
italian.BcNoDllConfirm=In quella cartella non è stata trovata nessuna DLL di Base Camp (MacroPadSDK.dll, SDKDLL.dll, Everest360_USB.dll). Continuare comunque?
italian.BehaviorPageCaption=Comportamento all'avvio di Base Camp
italian.BehaviorPageSubCaption=Scegli come K2 deve interagire automaticamente con Base Camp. Entrambe le opzioni si possono cambiare in seguito dalla scheda Impostazioni di K2.
italian.BehaviorPageDescription=Queste opzioni valgono per ogni avvio e chiusura di K2, non solo per questa installazione.
italian.CkAutoStop=Chiudi automaticamente Base Camp all'avvio di K2
italian.CkRestartOnClose=Riavvia automaticamente Base Camp alla chiusura di K2

german.BcPageCaption=Base-Camp-Komponenten
german.BcPageDescription=K2 kommuniziert mit einigen deiner Geräte über drei DLL-Dateien, die sich im Installationsordner von Base Camp befinden. Sag K2, wo es sie findet - du kannst das später in den K2-Einstellungen ändern.
german.BcRadioDetect=Die auf diesem PC gefundene Base-Camp-Installation verwenden:
german.BcNotFound=Es wurde keine Base-Camp-Installation automatisch erkannt. Wähle unten "Ordner manuell angeben" und navigiere zum Ordner mit den DLL-Dateien (MacroPadSDK.dll, SDKDLL.dll, Everest360_USB.dll) - zum Beispiel eine Base-Camp-Installation auf einem anderen Laufwerk oder ein Ordner, in den du nur die DLLs kopiert hast.
german.BcRadioManual=Ordner manuell angeben:
german.BcBrowse=Durchsuchen...
german.BcBrowsePrompt=Wähle den Ordner mit den Base-Camp-DLL-Dateien
german.BcNoDllConfirm=In diesem Ordner wurde keine Base-Camp-DLL (MacroPadSDK.dll, SDKDLL.dll, Everest360_USB.dll) gefunden. Trotzdem fortfahren?
german.BehaviorPageCaption=Base-Camp-Verhalten beim Start
german.BehaviorPageSubCaption=Wähle, wie K2 automatisch mit Base Camp interagiert. Beide Optionen lassen sich später in den K2-Einstellungen ändern.
german.BehaviorPageDescription=Diese Optionen gelten für jeden Start und jedes Beenden von K2, nicht nur für diese Installation.
german.CkAutoStop=Base Camp beim Start von K2 automatisch schließen
german.CkRestartOnClose=Base Camp beim Beenden von K2 automatisch neu starten

french.BcPageCaption=Composants Base Camp
french.BcPageDescription=K2 communique avec certains de vos périphériques via trois fichiers DLL contenus dans le dossier d'installation de Base Camp. Indiquez à K2 où les trouver - vous pourrez modifier ce choix plus tard dans l'onglet Paramètres de K2.
french.BcRadioDetect=Utiliser l'installation de Base Camp trouvée sur ce PC :
french.BcNotFound=Aucune installation de Base Camp n'a été détectée automatiquement. Choisissez "Indiquer un dossier manuellement" ci-dessous et parcourez jusqu'au dossier contenant les fichiers DLL (MacroPadSDK.dll, SDKDLL.dll, Everest360_USB.dll) - par exemple une installation de Base Camp sur un autre disque, ou un dossier où vous avez copié uniquement les DLL.
french.BcRadioManual=Indiquer un dossier manuellement :
french.BcBrowse=Parcourir...
french.BcBrowsePrompt=Sélectionnez le dossier contenant les fichiers DLL de Base Camp
french.BcNoDllConfirm=Aucune DLL de Base Camp (MacroPadSDK.dll, SDKDLL.dll, Everest360_USB.dll) n'a été trouvée dans ce dossier. Continuer quand même ?
french.BehaviorPageCaption=Comportement de Base Camp au démarrage
french.BehaviorPageSubCaption=Choisissez comment K2 interagit automatiquement avec Base Camp. Les deux options peuvent être modifiées plus tard dans l'onglet Paramètres de K2.
french.BehaviorPageDescription=Ces options s'appliquent à chaque démarrage et fermeture de K2, pas seulement à cette installation.
french.CkAutoStop=Fermer automatiquement Base Camp au démarrage de K2
french.CkRestartOnClose=Redémarrer automatiquement Base Camp à la fermeture de K2

spanish.BcPageCaption=Componentes de Base Camp
spanish.BcPageDescription=K2 se comunica con algunos de tus dispositivos mediante tres archivos DLL contenidos en la carpeta de instalación de Base Camp. Indica a K2 dónde encontrarlos - puedes cambiarlo más tarde en la pestaña Configuración de K2.
spanish.BcRadioDetect=Usar la instalación de Base Camp encontrada en este PC:
spanish.BcNotFound=No se detectó automáticamente ninguna instalación de Base Camp. Elige "Especificar una carpeta manualmente" abajo y busca la carpeta que contiene los archivos DLL (MacroPadSDK.dll, SDKDLL.dll, Everest360_USB.dll) - por ejemplo, una instalación de Base Camp en otra unidad, o una carpeta donde copiaste solo las DLL.
spanish.BcRadioManual=Especificar una carpeta manualmente:
spanish.BcBrowse=Examinar...
spanish.BcBrowsePrompt=Selecciona la carpeta que contiene los archivos DLL de Base Camp
spanish.BcNoDllConfirm=No se encontró ninguna DLL de Base Camp (MacroPadSDK.dll, SDKDLL.dll, Everest360_USB.dll) en esa carpeta. ¿Continuar de todas formas?
spanish.BehaviorPageCaption=Comportamiento de Base Camp al iniciar
spanish.BehaviorPageSubCaption=Elige cómo interactúa K2 automáticamente con Base Camp. Ambas opciones se pueden cambiar más tarde en la pestaña Configuración de K2.
spanish.BehaviorPageDescription=Estas opciones afectan a cada inicio y cierre de K2, no solo a esta instalación.
spanish.CkAutoStop=Cerrar Base Camp automáticamente al iniciar K2
spanish.CkRestartOnClose=Reiniciar Base Camp automáticamente al cerrar K2

polish.BcPageCaption=Składniki Base Camp
polish.BcPageDescription=K2 komunikuje się z niektórymi Twoimi urządzeniami za pomocą trzech plików DLL znajdujących się w folderze instalacyjnym Base Camp. Wskaż K2, gdzie je znaleźć - możesz to później zmienić w zakładce Ustawienia K2.
polish.BcRadioDetect=Użyj instalacji Base Camp znalezionej na tym komputerze:
polish.BcNotFound=Nie wykryto automatycznie żadnej instalacji Base Camp. Wybierz poniżej "Wskaż folder ręcznie" i przejdź do folderu zawierającego pliki DLL (MacroPadSDK.dll, SDKDLL.dll, Everest360_USB.dll) - na przykład instalację Base Camp na innym dysku lub folder, do którego skopiowano tylko pliki DLL.
polish.BcRadioManual=Wskaż folder ręcznie:
polish.BcBrowse=Przeglądaj...
polish.BcBrowsePrompt=Wybierz folder zawierający pliki DLL Base Camp
polish.BcNoDllConfirm=W tym folderze nie znaleziono żadnej biblioteki DLL Base Camp (MacroPadSDK.dll, SDKDLL.dll, Everest360_USB.dll). Kontynuować mimo to?
polish.BehaviorPageCaption=Zachowanie Base Camp przy uruchamianiu
polish.BehaviorPageSubCaption=Wybierz, jak K2 ma automatycznie współdziałać z Base Camp. Obie opcje można później zmienić w zakładce Ustawienia K2.
polish.BehaviorPageDescription=Te opcje dotyczą każdego uruchomienia i zamknięcia K2, nie tylko tej instalacji.
polish.CkAutoStop=Automatycznie zamykaj Base Camp przy uruchamianiu K2
polish.CkRestartOnClose=Automatycznie uruchamiaj ponownie Base Camp przy zamykaniu K2

brazilianportuguese.BcPageCaption=Componentes do Base Camp
brazilianportuguese.BcPageDescription=O K2 se comunica com alguns dos seus dispositivos por meio de três arquivos DLL contidos na pasta de instalação do Base Camp. Diga ao K2 onde encontrá-los - você pode alterar isso depois na aba Configurações do K2.
brazilianportuguese.BcRadioDetect=Usar a instalação do Base Camp encontrada neste PC:
brazilianportuguese.BcNotFound=Nenhuma instalação do Base Camp foi detectada automaticamente. Escolha "Especificar uma pasta manualmente" abaixo e navegue até a pasta que contém os arquivos DLL (MacroPadSDK.dll, SDKDLL.dll, Everest360_USB.dll) - por exemplo, uma instalação do Base Camp em outra unidade, ou uma pasta onde você copiou apenas as DLLs.
brazilianportuguese.BcRadioManual=Especificar uma pasta manualmente:
brazilianportuguese.BcBrowse=Procurar...
brazilianportuguese.BcBrowsePrompt=Selecione a pasta que contém os arquivos DLL do Base Camp
brazilianportuguese.BcNoDllConfirm=Nenhuma DLL do Base Camp (MacroPadSDK.dll, SDKDLL.dll, Everest360_USB.dll) foi encontrada nessa pasta. Continuar mesmo assim?
brazilianportuguese.BehaviorPageCaption=Comportamento do Base Camp na inicialização
brazilianportuguese.BehaviorPageSubCaption=Escolha como o K2 deve interagir automaticamente com o Base Camp. Ambas as opções podem ser alteradas depois na aba Configurações do K2.
brazilianportuguese.BehaviorPageDescription=Essas opções valem para cada inicialização e fechamento do K2, não apenas para esta instalação.
brazilianportuguese.CkAutoStop=Fechar o Base Camp automaticamente ao iniciar o K2
brazilianportuguese.CkRestartOnClose=Reiniciar o Base Camp automaticamente ao fechar o K2

japanese.BcPageCaption=Base Camp コンポーネント
japanese.BcPageDescription=K2は一部のデバイスと、Base Campのインストールフォルダー内にある3つのdllファイルを通じて通信します。K2にその場所を教えてください。この設定は後でK2の設定タブから変更できます。
japanese.BcRadioDetect=このPCで見つかったBase Campのインストールを使用する:
japanese.BcNotFound=Base Campのインストールは自動的に検出されませんでした。下の「フォルダーを手動で指定する」を選択し、DLLファイル(MacroPadSDK.dll、SDKDLL.dll、Everest360_USB.dll)が入っているフォルダーを参照してください。例えば、別のドライブにあるBase Campのインストール、またはDLLだけをコピーしたフォルダーです。
japanese.BcRadioManual=フォルダーを手動で指定する:
japanese.BcBrowse=参照...
japanese.BcBrowsePrompt=Base CampのDLLファイルが入っているフォルダーを選択してください
japanese.BcNoDllConfirm=そのフォルダーにはBase CampのDLL(MacroPadSDK.dll、SDKDLL.dll、Everest360_USB.dll)が見つかりませんでした。続行しますか?
japanese.BehaviorPageCaption=起動時のBase Campの動作
japanese.BehaviorPageSubCaption=K2がBase Campと自動的にどのように連携するかを選択してください。どちらも後でK2の設定タブから変更できます。
japanese.BehaviorPageDescription=これらの設定はK2の起動と終了のたびに適用され、このインストールだけに限りません。
japanese.CkAutoStop=K2の起動時にBase Campを自動的に終了する
japanese.CkRestartOnClose=K2の終了時にBase Campを自動的に再起動する

korean.BcPageCaption=Base Camp 구성 요소
korean.BcPageDescription=K2는 일부 장치와 Base Camp 설치 폴더 안에 있는 3개의 DLL 파일을 통해 통신합니다. K2에 해당 파일의 위치를 알려주세요. 이 설정은 나중에 K2의 설정 탭에서 변경할 수 있습니다.
korean.BcRadioDetect=이 PC에서 찾은 Base Camp 설치를 사용:
korean.BcNotFound=Base Camp 설치가 자동으로 감지되지 않았습니다. 아래에서 "폴더를 수동으로 지정"을 선택하고 DLL 파일(MacroPadSDK.dll, SDKDLL.dll, Everest360_USB.dll)이 있는 폴더를 찾아보세요. 예를 들어 다른 드라이브에 설치된 Base Camp나 DLL만 복사해 둔 폴더입니다.
korean.BcRadioManual=폴더를 수동으로 지정:
korean.BcBrowse=찾아보기...
korean.BcBrowsePrompt=Base Camp DLL 파일이 있는 폴더를 선택하세요
korean.BcNoDllConfirm=해당 폴더에서 Base Camp DLL(MacroPadSDK.dll, SDKDLL.dll, Everest360_USB.dll)을 찾을 수 없습니다. 계속하시겠습니까?
korean.BehaviorPageCaption=시작 시 Base Camp 동작
korean.BehaviorPageSubCaption=K2가 Base Camp와 자동으로 상호 작용하는 방식을 선택하세요. 두 옵션 모두 나중에 K2의 설정 탭에서 변경할 수 있습니다.
korean.BehaviorPageDescription=이 설정은 이번 설치뿐 아니라 K2를 시작하고 종료할 때마다 적용됩니다.
korean.CkAutoStop=K2 시작 시 Base Camp를 자동으로 종료
korean.CkRestartOnClose=K2 종료 시 Base Camp를 자동으로 다시 시작

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

; Staged by build-installer.bat: publish\K2.App\ is the full install tree,
; K2.App.exe + its Satellite\ helper + the standalone DisplayPad publish
; nested under DisplayPad\ - all installed unconditionally.
[Files]
Source: "publish\K2.App\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\K2"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall K2"; Filename: "{uninstallexe}"
Name: "{autodesktop}\K2"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; shellexec (not the default CreateProcess) is required here: K2.App.exe's
; manifest is requireAdministrator, and CreateProcess cannot elevate a child
; process on its own — it fails with "CreateProcess failed; code 740" even
; when the current user is an administrator. ShellExecute knows how to
; trigger the UAC elevation dance for a manifested-elevated target.
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,K2}"; Flags: nowait postinstall skipifsilent shellexec

[Code]
var
  BcPage: TWizardPage;
  RbBcDetected, RbBcManual: TNewRadioButton;
  LblBcPath: TNewStaticText;
  EdBcFolder: TNewEdit;
  BtnBcBrowse: TNewButton;
  DetectedBcDir: String;

  BehaviorPage: TInputOptionWizardPage;

{ True if any of the known Base Camp native DLLs (MacroPadSDK.dll,
  SDKDLL.dll, Everest360_USB.dll - same list as K2.App's
  NativeDependencyResolver.BaseCampNativeDlls) sits directly in Dir. }
function DllExistsInDir(const Dir: String): Boolean;
var
  Base: String;
begin
  Result := False;
  if Dir = '' then Exit;
  Base := AddBackslash(Dir);
  Result := FileExists(Base + 'MacroPadSDK.dll') or
            FileExists(Base + 'SDKDLL.dll') or
            FileExists(Base + 'Everest360_USB.dll');
end;

{ Collects InstallLocation values from uninstall registry keys whose
  DisplayName mentions "Base Camp" - same signal
  K2.App's NativeDependencyResolver.RegistryInstallLocations() uses at
  runtime, ported to Pascal Script for the wizard's default guess. }
procedure CollectFromUninstallKey(const RootKey: Integer; const UninstallKey: String; List: TStringList);
var
  Names: TArrayOfString;
  I: Integer;
  SubKey, DisplayName, InstallLoc: String;
begin
  if RegGetSubkeyNames(RootKey, UninstallKey, Names) then
  begin
    for I := 0 to GetArrayLength(Names) - 1 do
    begin
      SubKey := UninstallKey + '\' + Names[I];
      if RegQueryStringValue(RootKey, SubKey, 'DisplayName', DisplayName) then
        if Pos('Base Camp', DisplayName) > 0 then
          if RegQueryStringValue(RootKey, SubKey, 'InstallLocation', InstallLoc) then
            if InstallLoc <> '' then
              List.Add(InstallLoc);
    end;
  end;
end;

{ Best-effort auto-detection of an existing Base Camp install, checked
  against the same typical paths + registry uninstall keys as
  NativeDependencyResolver.BaseCampDirectories() in K2.App (not a
  byte-for-byte port - this only seeds the wizard's default choice; the
  app does its own, authoritative detection at runtime regardless of
  what is picked here). Returns '' if nothing was found. }
function DetectBaseCampDir(): String;
var
  Candidates, RegLocations: TStringList;
  I: Integer;
begin
  Result := '';
  Candidates := TStringList.Create;
  try
    Candidates.Add(ExpandConstant('{pf}\Mountain\Base Camp'));
    Candidates.Add(ExpandConstant('{pf}\Base Camp'));
    Candidates.Add(ExpandConstant('{pf32}\Mountain\Base Camp'));
    Candidates.Add(ExpandConstant('{pf32}\Base Camp'));
    Candidates.Add(ExpandConstant('{localappdata}\Programs\Base Camp'));
    Candidates.Add(ExpandConstant('{localappdata}\Programs\base-camp'));

    RegLocations := TStringList.Create;
    try
      CollectFromUninstallKey(HKLM, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', RegLocations);
      CollectFromUninstallKey(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall', RegLocations);
      CollectFromUninstallKey(HKCU, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', RegLocations);
      for I := 0 to RegLocations.Count - 1 do
        Candidates.Add(RegLocations[I]);
    finally
      RegLocations.Free;
    end;

    for I := 0 to Candidates.Count - 1 do
      if DllExistsInDir(Candidates[I]) then
      begin
        Result := Candidates[I];
        Exit;
      end;
  finally
    Candidates.Free;
  end;
end;

procedure UpdateBcControls;
begin
  EdBcFolder.Enabled := RbBcManual.Checked;
  BtnBcBrowse.Enabled := RbBcManual.Checked;
end;

procedure BcRadioClick(Sender: TObject);
begin
  UpdateBcControls;
end;

procedure BcBrowseClick(Sender: TObject);
var
  Dir: String;
begin
  Dir := EdBcFolder.Text;
  if BrowseForFolder(CustomMessage('BcBrowsePrompt'), Dir, False) then
    EdBcFolder.Text := Dir;
end;

{ Keeps "restart Base Camp on close" enabled/checkable only while
  "close Base Camp on startup" is on, per explicit request: the second
  behavior only makes sense paired with the first. }
procedure BehaviorCheckClick(Sender: TObject);
begin
  if not BehaviorPage.Values[0] then
  begin
    BehaviorPage.Values[1] := False;
    BehaviorPage.CheckListBox.ItemEnabled[1] := False;
  end
  else
    BehaviorPage.CheckListBox.ItemEnabled[1] := True;
end;

procedure InitializeWizard;
var
  RowH, Gap: Integer;
  Y: Integer;
begin
  RowH := ScaleY(17);
  Gap := ScaleY(8);

  DetectedBcDir := DetectBaseCampDir();

  BcPage := CreateCustomPage(wpSelectTasks, CustomMessage('BcPageCaption'), CustomMessage('BcPageDescription'));

  Y := 0;

  RbBcDetected := TNewRadioButton.Create(BcPage);
  RbBcDetected.Parent := BcPage.Surface;
  RbBcDetected.Left := 0;
  RbBcDetected.Top := Y;
  RbBcDetected.Width := BcPage.SurfaceWidth;
  RbBcDetected.Height := RowH;
  RbBcDetected.Caption := CustomMessage('BcRadioDetect');
  RbBcDetected.Enabled := DetectedBcDir <> '';
  RbBcDetected.Checked := DetectedBcDir <> '';
  RbBcDetected.OnClick := @BcRadioClick;
  Y := Y + RowH + ScaleY(2);

  LblBcPath := TNewStaticText.Create(BcPage);
  LblBcPath.Parent := BcPage.Surface;
  LblBcPath.Left := ScaleX(20);
  LblBcPath.Top := Y;
  LblBcPath.Width := BcPage.SurfaceWidth - ScaleX(20);
  LblBcPath.AutoSize := False;
  LblBcPath.WordWrap := True;
  if DetectedBcDir <> '' then
  begin
    LblBcPath.Caption := DetectedBcDir;
    LblBcPath.Height := ScaleY(17);
  end
  else
  begin
    LblBcPath.Caption := CustomMessage('BcNotFound');
    LblBcPath.Height := ScaleY(48);
  end;
  Y := Y + LblBcPath.Height + Gap;

  RbBcManual := TNewRadioButton.Create(BcPage);
  RbBcManual.Parent := BcPage.Surface;
  RbBcManual.Left := 0;
  RbBcManual.Top := Y;
  RbBcManual.Width := BcPage.SurfaceWidth;
  RbBcManual.Height := RowH;
  RbBcManual.Caption := CustomMessage('BcRadioManual');
  RbBcManual.Checked := DetectedBcDir = '';
  RbBcManual.OnClick := @BcRadioClick;
  Y := Y + RowH + Gap;

  BtnBcBrowse := TNewButton.Create(BcPage);
  BtnBcBrowse.Parent := BcPage.Surface;
  BtnBcBrowse.Width := ScaleX(80);
  BtnBcBrowse.Height := ScaleY(23);
  BtnBcBrowse.Left := BcPage.SurfaceWidth - BtnBcBrowse.Width;
  BtnBcBrowse.Top := Y;
  BtnBcBrowse.Caption := CustomMessage('BcBrowse');
  BtnBcBrowse.OnClick := @BcBrowseClick;

  EdBcFolder := TNewEdit.Create(BcPage);
  EdBcFolder.Parent := BcPage.Surface;
  EdBcFolder.Left := ScaleX(20);
  EdBcFolder.Top := Y + ScaleY(2);
  EdBcFolder.Width := BtnBcBrowse.Left - ScaleX(10) - EdBcFolder.Left;
  EdBcFolder.Text := '';

  UpdateBcControls;

  BehaviorPage := CreateInputOptionPage(BcPage.ID,
    CustomMessage('BehaviorPageCaption'), CustomMessage('BehaviorPageDescription'),
    CustomMessage('BehaviorPageSubCaption'), False, False);
  BehaviorPage.Add(CustomMessage('CkAutoStop'));
  BehaviorPage.Add(CustomMessage('CkRestartOnClose'));
  BehaviorPage.Values[0] := True;
  BehaviorPage.Values[1] := False;
  BehaviorPage.CheckListBox.ItemEnabled[1] := True;
  BehaviorPage.CheckListBox.OnClickCheck := @BehaviorCheckClick;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = BcPage.ID) and RbBcManual.Checked and (Trim(EdBcFolder.Text) <> '')
     and not DllExistsInDir(EdBcFolder.Text) then
  begin
    Result := MsgBox(CustomMessage('BcNoDllConfirm'), mbConfirmation, MB_YESNO) = IDYES;
  end;
end;

{ Seeds K2's app_settings.json with the choices made on the two pages
  above. Only runs on a fresh install: if the file already exists (repair/
  upgrade over a previous K2 install) it is left untouched, so a returning
  user's own Settings-tab choices are never clobbered by wizard defaults. }
procedure WriteAppSettingsJson;
var
  Dir, Path, FolderEscaped: String;
  Lines: TStringList;
  Json: String;
  I: Integer;
begin
  Path := ExpandConstant('{localappdata}\K2\app_settings.json');
  if FileExists(Path) then Exit;

  Dir := ExpandConstant('{localappdata}\K2');
  ForceDirectories(Dir);

  Lines := TStringList.Create;
  try
    if RbBcManual.Checked and (Trim(EdBcFolder.Text) <> '') then
    begin
      FolderEscaped := EdBcFolder.Text;
      StringChange(FolderEscaped, '\', '\\');
      Lines.Add('  "BaseCampDllFolder": "' + FolderEscaped + '"');
    end;

    if BehaviorPage.Values[0] then
      Lines.Add('  "AutoStopBaseCamp": true')
    else
      Lines.Add('  "AutoStopBaseCamp": false');

    if BehaviorPage.Values[1] then
      Lines.Add('  "RestartBaseCampOnClose": true')
    else
      Lines.Add('  "RestartBaseCampOnClose": false');

    Json := '{' + #13#10;
    for I := 0 to Lines.Count - 1 do
    begin
      Json := Json + Lines[I];
      if I < Lines.Count - 1 then
        Json := Json + ',';
      Json := Json + #13#10;
    end;
    Json := Json + '}' + #13#10;

    SaveStringToFile(Path, Json, False);
  finally
    Lines.Free;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    WriteAppSettingsJson;
end;
