; nocat.farm setup - the screen and what it does. Included from nocatfarm.iss.

[Code]
const
  UrlGitHub = 'https://github.com/VisaHolder/nocatfarm';
  UrlSite = 'https://nocat.lol/nocatfarm';
  UrlMaker = 'https://nocat.lol';
  DefaultPort = 7242;

  // The screen, in pixels at 100%. Every language's words are measured against these (Consolas: 6px a character at
  // 8pt, 7px at 9 and 10pt) - a longer translation needs checking against the space it gets.
  FormW = 640;
  FormH = 512;
  Col2 = 320;         // the second column of toggles, and the port
  MainBtnW = 200;     // "auf 1.6.3 aktualisieren", "mettre à jour vers 1.6.3"
  SecBtnW = 130;      // "deinstallieren"

  // nocat.farm's palette, as $BBGGRR
  CBg = $0A0A0A;
  CPanel = $111111;
  CLine = $333333;
  CLine2 = $666666;
  CText = $E8E8E8;
  CBody = $C8C8C8;
  CMuted = $8A8A8A;
  CPurple = $F65C8B;

  WM_NCLBUTTONDOWN = $00A1;
  HTCAPTION = 2;

  // Where the accounts come from ("switching from")
  SrcFresh = 0;
  SrcAsf = 1;
  SrcIdleMaster = 2;
  SrcOther = 3;
  SrcLast = 3;

type
  TToggle = record
    Box: TLabel;      // "[x]" / "[ ]", or "(*)" / "( )" for a choice
    Caption: TLabel;
    On: Boolean;
  end;

var
  Upgrading: Boolean;   // nocat.farm is installed here already: update / reinstall / uninstall
  InstalledNewer: Boolean;   // ...and it's newer than this setup: no downgrade offered
  InstalledVersion: String;
  Portable: String;     // a portable nocat.farm found on this PC, '' if none
  PortableHere: Boolean;   // the install folder picked is that portable copy's own folder: nothing to move
  MovePick: array[0..1] of TToggle;   // 0 move it here, 1 start fresh
  MoveHave, MoveFrom: TLabel;
  RunsRule: TBitmapImage;
  InstallDir: String;
  ShowingAdvanced: Boolean;
  CloseOnly: Boolean;   // the last screen's "close" (or its cross): finish without opening nocat.farm
  HoverTimer: LongWord;

  LogoImg: TBitmapImage;
  Title, Tagline, Note, PathLabel, ChangeLink, AdvLink, FreeLine, PortLabel, Percent, CloseX: TLabel;
  ComingFrom, SourceFrom: TLabel;
  LinkGit, LinkSite, LinkMaker: TLabel;
  Toggles: array[0..2] of TToggle;   // 0 start with Windows, 1 desktop shortcut, 2 start hidden
  Sources: array[0..3] of TToggle;   // SrcFresh .. SrcOther
  SourcePath: array[0..3] of String; // the folder found for each, '' if none
  Source: Integer;
  PortEdit: TNewEdit;
  PortTouched: Boolean;
  BarBack, BarFill: TBitmapImage;
  MainBtnEdge, MainBtnFace, SecBtnEdge, SecBtnFace: TBitmapImage;
  MainBtnText, SecBtnText: TLabel;

function ReleaseCapture: Boolean; external 'ReleaseCapture@user32.dll stdcall';

type
  TPointRec = record
    X, Y: Longint;
  end;

function GetCursorPos(var P: TPointRec): Boolean; external 'GetCursorPos@user32.dll stdcall';
function ScreenToClient(Wnd: HWND; var P: TPointRec): Boolean; external 'ScreenToClient@user32.dll stdcall';
function SetTimer(Wnd: HWND; Id, Elapse, Func: LongWord): LongWord; external 'SetTimer@user32.dll stdcall';
function KillTimer(Wnd: HWND; Id: LongWord): Boolean; external 'KillTimer@user32.dll stdcall';

function IsPreview: Boolean;
begin
#ifdef Preview
  Result := True;
#else
  Result := False;
#endif
end;

function IsCjk: Boolean;
begin
  Result := (ActiveLanguage = 'japanese') or (ActiveLanguage = 'korean') or (ActiveLanguage = 'chinesesimplified');
end;

// Monospace like the app; Chinese, Japanese and Korean need a face that has their characters.
function UiFont: String;
begin
  if IsCjk then Result := 'Segoe UI' else Result := 'Consolas';
end;

// Command-line answers for unattended installs (and uninstalls); anything not given keeps the screen's default.
// /NAME=yes|no, or just /NAME on its own for yes.
function ParamYes(const Name: String; Default: Boolean): Boolean;
var
  P, V: String;
  I: Integer;
begin
  Result := Default;
  for I := 1 to ParamCount do begin
    P := ParamStr(I);
    if CompareText(P, '/' + Name) = 0 then Result := True
    else if CompareText(Copy(P, 1, Length(Name) + 2), '/' + Name + '=') = 0 then begin
      V := Lowercase(Copy(P, Length(Name) + 3, Length(P)));
      Result := (V = 'yes') or (V = '1') or (V = 'true') or (V = 'on');
    end;
  end;
end;

function ParamGiven(const Name: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if (CompareText(ParamStr(I), '/' + Name) = 0) or (CompareText(Copy(ParamStr(I), 1, Length(Name) + 2), '/' + Name + '=') = 0) then
      Result := True;
end;

// /SHOTS=yes on the preview: the real wording, no PREVIEW badge - for the README's screenshots. Still installs nothing.
function ShotMode: Boolean;
begin
  Result := IsPreview and ParamYes('SHOTS', False);
end;

function SameDir(const A, B: String): Boolean;
begin
  Result := (A <> '') and (B <> '') and (CompareText(RemoveBackslash(A), RemoveBackslash(B)) = 0);
end;

// ── building blocks ─────────────────────────────────────────────────────────

function Box(L, T, W, H: Integer; Color: TColor): TBitmapImage;
begin
  Result := TBitmapImage.Create(WizardForm);
  Result.Parent := WizardForm;
  Result.SetBounds(ScaleX(L), ScaleY(T), ScaleX(W), ScaleY(H));
  Result.BackColor := Color;
end;

function Words(const S: String; L, T, Size: Integer; Color: TColor; Bold: Boolean): TLabel;
begin
  Result := TLabel.Create(WizardForm);
  Result.Parent := WizardForm;
  Result.Transparent := True;
  Result.Caption := S;
  Result.Font.Name := UiFont;
  Result.Font.Size := Size;
  Result.Font.Color := Color;
  if Bold then Result.Font.Style := [fsBold];
  Result.Left := ScaleX(L);
  Result.Top := ScaleY(T);
end;

// A label that wraps inside the box it's given, instead of running on under whatever is to its right.
procedure Wrap(L: TLabel; Left, Top, W, H: Integer);
begin
  L.AutoSize := False;
  L.WordWrap := True;
  L.SetBounds(ScaleX(Left), ScaleY(Top), ScaleX(W), ScaleY(H));
end;

procedure OpenUrl(Sender: TObject);
var
  Code: Integer;
  Url: String;
begin
  if Sender = LinkGit then Url := UrlGitHub
  else if Sender = LinkSite then Url := UrlSite
  else Url := UrlMaker;
  ShellExec('open', Url, '', '', SW_SHOWNORMAL, ewNoWait, Code);
end;

function Link(const S: String; L, T: Integer): TLabel;
begin
  Result := Words(S, L, T, 8, CMuted, False);
  Result.Cursor := crHand;
  Result.Font.Style := [fsUnderline];
  Result.OnClick := @OpenUrl;
end;

procedure DragWindow(Sender: TObject; Button: TMouseButton; Shift: TShiftState; X, Y: Integer);
begin
  ReleaseCapture;
  SendMessage(WizardForm.Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
end;

procedure Paint(var T: TToggle; Radio: Boolean);
begin
  if T.On then begin
    if Radio then T.Box.Caption := '(*)' else T.Box.Caption := '[x]';
    T.Box.Font.Color := CText;
    T.Caption.Font.Color := CText;
  end else begin
    if Radio then T.Box.Caption := '( )' else T.Box.Caption := '[ ]';
    T.Box.Font.Color := CMuted;
    T.Caption.Font.Color := CMuted;
  end;
end;

procedure MakeToggle(var T: TToggle; const S: String; L, Top: Integer; On: Boolean; Click: TNotifyEvent);
begin
  T.Box := Words('[x]', L, Top, 10, CText, True);
  T.Caption := Words(S, L + 36, Top, 10, CBody, False);
  T.Box.Cursor := crHand;
  T.Caption.Cursor := crHand;
  T.Box.OnClick := Click;
  T.Caption.OnClick := Click;
  T.On := On;
end;

procedure ShowToggle(var T: TToggle; Show: Boolean);
begin
  T.Box.Visible := Show;
  T.Caption.Visible := Show;
end;

procedure ToggleClick(Sender: TObject);
var
  I: Integer;
begin
  for I := 0 to 2 do
    if (Sender = Toggles[I].Box) or (Sender = Toggles[I].Caption) then begin
      Toggles[I].On := not Toggles[I].On;
      Paint(Toggles[I], False);
    end;
end;

// A flat bordered button like the app's "start all": a 1px edge, a dark face, the caption centred.
procedure MakeButton(var Edge, Face: TBitmapImage; var Caption: TLabel; L, T, W: Integer; Primary: Boolean; Click: TNotifyEvent);
begin
  if Primary then Edge := Box(L, T, W, 34, CBody) else Edge := Box(L, T, W, 34, CLine2);
  Face := Box(L + 1, T + 1, W - 2, 32, CPanel);
  Caption := Words('', L, T + 8, 10, CText, Primary);
  Caption.AutoSize := False;
  Caption.Alignment := taCenter;
  Caption.SetBounds(ScaleX(L), ScaleY(T + 8), ScaleX(W), ScaleY(20));
  Edge.OnClick := Click;
  Face.OnClick := Click;
  Caption.OnClick := Click;
  Edge.Cursor := crHand;
  Face.Cursor := crHand;
  Caption.Cursor := crHand;
end;

procedure ShowButton(Edge, Face: TBitmapImage; Caption: TLabel; Show: Boolean);
begin
  Edge.Visible := Show;
  Face.Visible := Show;
  Caption.Visible := Show;
end;

// ── finding what was used before ────────────────────────────────────────────

function DirOfExe(const Command: String): String;
var
  S: String;
  P: Integer;
begin
  S := Trim(Command);
  if (Length(S) > 0) and (S[1] = '"') then begin
    Delete(S, 1, 1);
    P := Pos('"', S);
    if P > 0 then S := Copy(S, 1, P - 1);
  end else begin
    P := Pos('.exe', Lowercase(S));
    if P > 0 then S := Copy(S, 1, P + 3);
  end;
  Result := ExtractFileDir(S);
end;

// 64-bit PowerShell. The setup itself is 32-bit, so plain powershell.exe would be the 32-bit one - and that can't read
// the path of a 64-bit program: a running nocat.farm or ArchiSteamFarm was never found, and never stopped.
function PowerShell: String;
begin
  Result := ExpandConstant('{sysnative}\WindowsPowerShell\v1.0\powershell.exe');
end;

function RunningDir(const Process: String): String;
var
  OutFile, Quoted: String;
  Code: Integer;
  Lines: TArrayOfString;
begin
  Result := '';
  OutFile := ExpandConstant('{tmp}\running-' + Process + '.txt');
  Quoted := OutFile;
  StringChangeEx(Quoted, '''', '''''', True);   // PowerShell single quotes: an apostrophe is written twice
  if Exec(PowerShell, '-NoProfile -NonInteractive -Command "(Get-Process ' + Process + ' -ErrorAction SilentlyContinue | Select-Object -First 1).Path | Out-File -Encoding utf8 ''' + Quoted + '''"',
    '', SW_HIDE, ewWaitUntilTerminated, Code) and LoadStringsFromFile(OutFile, Lines) and (GetArrayLength(Lines) > 0) then
    Result := ExtractFileDir(Trim(Lines[0]));
end;

// A folder holding Exe and Marker (relative), up to Depth levels under Base - where people unzip things.
function ScanFor(const Base, Exe, Marker: String; Depth: Integer): String;
var
  F: TFindRec;
begin
  Result := '';
  if FileExists(Base + '\' + Exe) and FileExists(Base + '\' + Marker) then begin
    Result := Base;
    Exit;
  end;
  if (Depth = 0) or not FindFirst(Base + '\*', F) then Exit;
  try
    repeat
      if ((F.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and (F.Name <> '.') and (F.Name <> '..') then
        Result := ScanFor(Base + '\' + F.Name, Exe, Marker, Depth - 1);
    until (Result <> '') or not FindNext(F);
  finally
    FindClose(F);
  end;
end;

function ScanUsual(const Exe, Marker: String): String;
begin
  Result := ScanFor(ExpandConstant('{userdesktop}'), Exe, Marker, 2);
  if Result = '' then Result := ScanFor(ExpandConstant('{userdocs}'), Exe, Marker, 2);
  if Result = '' then Result := ScanFor(AddBackslash(GetEnv('USERPROFILE')) + 'Downloads', Exe, Marker, 2);
end;

function FindNocatFarm: String;
var
  Run: String;
begin
  Result := '';
  if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'nocatFarm', Run) then
    Result := DirOfExe(Run);
  if (Result = '') or not FileExists(Result + '\config\nocatFarm.json') then Result := RunningDir('nocatFarm');
  if (Result = '') or not FileExists(Result + '\config\nocatFarm.json') then Result := ScanUsual('{#AppExe}', 'config\nocatFarm.json');
  if SameDir(Result, InstallDir) then Result := '';
  Result := RemoveBackslash(Result);
end;

function FindAsf: String;
begin
  Log('looking for ArchiSteamFarm under ' + ExpandConstant('{userdesktop}'));
  Result := RunningDir('ArchiSteamFarm');
  Log('running ArchiSteamFarm: [' + Result + ']');
  if (Result = '') or not FileExists(Result + '\config\ASF.json') then Result := ScanUsual('ArchiSteamFarm.exe', 'config\ASF.json');
  Result := RemoveBackslash(Result);
end;

function FindIdleMaster: String;
begin
  Result := RunningDir('IdleMasterExtended');
  if Result = '' then Result := RunningDir('IdleMaster');
  if Result = '' then Result := ScanUsual('IdleMasterExtended.exe', 'IdleMasterExtended.exe');
  if Result = '' then Result := ScanUsual('IdleMaster.exe', 'IdleMaster.exe');
  Result := RemoveBackslash(Result);
end;

// Whether an exe understands --quit (1.4.8 and later).
function KnowsQuit(const Exe: String): Boolean;
var
  MS, LS: Cardinal;
begin
  Result := GetVersionNumbers(Exe, MS, LS) and ((MS > $00010004) or ((MS = $00010004) and ((LS shr 16) >= 8)));
end;

// Close the nocat.farm in Dir cleanly and wait for it. One that can't be asked (older than --quit), or that didn't
// close in time (--quit gives up after 120s and says so), is stopped - rather than left for Windows' "file in use".
procedure CloseCopy(const Dir: String);
var
  Code: Integer;
  Exe, Quoted: String;
begin
  Exe := Dir + '\{#AppExe}';
  Quoted := Exe;
  StringChangeEx(Quoted, '''', '''''', True);   // PowerShell single quotes: an apostrophe is written twice
  if not FileExists(Exe) then Exit;
  if KnowsQuit(Exe) then begin
    if Exec(Exe, '--quit', Dir, SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0) then Exit;
    Log('--quit didn''t close ' + Exe + ' (exit ' + IntToStr(Code) + ') - stopping it');
  end;
  Exec(PowerShell, '-NoProfile -NonInteractive -Command "Get-Process nocatFarm -ErrorAction SilentlyContinue | Where-Object Path -eq ''' + Quoted + ''' | Stop-Process -Force; Start-Sleep -Seconds 2"',
    '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

function WantsDesktopIcon: Boolean;
begin
  Result := (not Upgrading) and Toggles[1].On;
end;

// The version installed is newer than this setup - "update to" would be a downgrade.
function IsNewer(const Installed: String): Boolean;
var
  Have, Mine: Int64;
begin
  Result := StrToVersion(Installed, Have) and StrToVersion('{#AppVersion}', Mine) and (ComparePackedVersion(Have, Mine) > 0);
end;

function IsSameVersion(const Installed: String): Boolean;
var
  Have, Mine: Int64;
begin
  Result := StrToVersion(Installed, Have) and StrToVersion('{#AppVersion}', Mine) and (ComparePackedVersion(Have, Mine) = 0);
end;

// ── the screen ──────────────────────────────────────────────────────────────

function ShortPath(const S: String; Max: Integer): String;
begin
  if Length(S) > Max then Result := Copy(S, 1, Max div 2 - 2) + '...' + Copy(S, Length(S) - (Max div 2) + 2, Max) else Result := S;
end;

procedure ShowSourceFrom;
begin
  if Source = SrcFresh then SourceFrom.Caption := CustomMessage('FromFresh')
  else if SourcePath[Source] <> '' then SourceFrom.Caption := FmtMessage(CustomMessage('FromFound'), [ShortPath(SourcePath[Source], 50)])
  else SourceFrom.Caption := CustomMessage('FromPickLater');
end;

procedure PickSource(I: Integer);
var
  J: Integer;
begin
  Source := I;
  for J := 0 to SrcLast do begin
    Sources[J].On := J = I;
    Paint(Sources[J], True);
  end;
  ShowSourceFrom;
end;

procedure SourceClick(Sender: TObject);
var
  I: Integer;
begin
  for I := 0 to SrcLast do
    if (Sender = Sources[I].Box) or (Sender = Sources[I].Caption) then PickSource(I);
end;

procedure PickMove(I: Integer);
begin
  MovePick[0].On := I = 0;
  MovePick[1].On := I = 1;
  Paint(MovePick[0], True);
  Paint(MovePick[1], True);
end;

procedure MoveClick(Sender: TObject);
begin
  if (Sender = MovePick[0].Box) or (Sender = MovePick[0].Caption) then PickMove(0) else PickMove(1);
end;

function MovingPortable: Boolean;
begin
  Result := (not Upgrading) and (Portable <> '') and (not PortableHere) and MovePick[0].On;
end;

// The portable copy's accounts end up in the install: moved in, or already in the folder being installed to.
function KeepingPortable: Boolean;
begin
  Result := (not Upgrading) and (Portable <> '') and (PortableHere or MovePick[0].On);
end;

procedure ShowPath;
begin
  if ShotMode then PathLabel.Caption := CustomMessage('InstallsTo') + '  C:\Users\you\AppData\Local\Programs\nocat.farm'
  else PathLabel.Caption := CustomMessage('InstallsTo') + '  ' + ShortPath(InstallDir, 58);
  ChangeLink.Left := PathLabel.Left + PathLabel.Width + ScaleX(12);
end;

// "move it here", or - when the folder picked is the portable copy's own - just that it's there already.
procedure ShowMove(Show: Boolean);
begin
  MoveHave.Visible := Show;
  MoveFrom.Visible := Show;
  ShowToggle(MovePick[0], Show and not PortableHere);
  ShowToggle(MovePick[1], Show and not PortableHere);
  if PortableHere then begin
    MoveFrom.Caption := CustomMessage('MoveAlready');
    MoveFrom.SetBounds(ScaleX(24), ScaleY(204), ScaleX(FormW - 48), ScaleY(32));
  end else begin
    MoveFrom.Caption := FmtMessage(CustomMessage('MoveDetail'), [ShortPath(Portable, 60)]);
    MoveFrom.SetBounds(ScaleX(60), ScaleY(226), ScaleX(FormW - 84), ScaleY(32));
  end;
end;

procedure SetState(const State: String); forward;

procedure ChangeClick(Sender: TObject);
var
  Dir: String;
begin
  Dir := InstallDir;
  if BrowseForFolder(CustomMessage('PickFolder'), Dir, True) then begin
    // Its own folder, unless it's the portable copy's: installing there keeps that copy's accounts where they are.
    if not SameDir(Dir, Portable) and (CompareText(ExtractFileName(RemoveBackslash(Dir)), '{#AppName}') <> 0) then
      Dir := AddBackslash(Dir) + '{#AppName}';
    InstallDir := Dir;
    PortableHere := SameDir(InstallDir, Portable);
    WizardForm.DirEdit.Text := Dir;
    ShowPath;
    SetState('main');
  end;
end;

procedure PaintAdvanced;
begin
  ShowToggle(Toggles[2], ShowingAdvanced);
  PortLabel.Visible := ShowingAdvanced;
  PortEdit.Visible := ShowingAdvanced;
  if ShowingAdvanced then AdvLink.Caption := '- ' + CustomMessage('Advanced') else AdvLink.Caption := '+ ' + CustomMessage('Advanced');
end;

procedure AdvancedClick(Sender: TObject);
begin
  ShowingAdvanced := not ShowingAdvanced;
  PaintAdvanced;
end;

// Setup's own buttons stay on the form - Windows won't close a setup whose Cancel button is gone - but out of sight.
// Enter still presses the hidden Next button, so everything the screen's own buttons do is done in NextButtonClick.
procedure HideNativeButtons;
begin
  WizardForm.NextButton.Left := -ScaleX(2000);
  WizardForm.BackButton.Left := -ScaleX(2000);
  WizardForm.CancelButton.Left := -ScaleX(2000);
  WizardForm.BackButton.Visible := False;
end;

// No hover events here, so a timer watches the mouse: the cross turns red, the buttons light up - like the app.
function Over(C: TControl; X, Y: Integer): Boolean;
begin
  Result := C.Visible and (X >= C.Left - ScaleX(6)) and (X < C.Left + C.Width + ScaleX(6)) and (Y >= C.Top - ScaleY(4)) and (Y < C.Top + C.Height + ScaleY(4));
end;

procedure HoverTick(Wnd: HWND; Msg, Id, Time: LongWord);
var
  P: TPointRec;
begin
  if not GetCursorPos(P) or not ScreenToClient(WizardForm.Handle, P) then Exit;
  if Over(CloseX, P.X, P.Y) then CloseX.Font.Color := $3131FF else CloseX.Font.Color := CMuted;
  if Over(MainBtnEdge, P.X, P.Y) then MainBtnEdge.BackColor := $FFFFFF else MainBtnEdge.BackColor := CBody;
  if Over(SecBtnEdge, P.X, P.Y) then SecBtnEdge.BackColor := CBody else SecBtnEdge.BackColor := CLine2;
end;

procedure GoNext;
begin
  WizardForm.NextButton.OnClick(WizardForm.NextButton);
end;

// The cross: on the last screen it's "close" (nothing opened); anywhere else it leaves setup.
procedure CloseClick(Sender: TObject);
begin
  if WizardForm.CurPageID = wpFinished then begin
    CloseOnly := True;
    GoNext;
  end else
    WizardForm.CancelButton.OnClick(WizardForm.CancelButton);
end;

function Port: Integer;
begin
  Result := StrToIntDef(Trim(PortEdit.Text), -1);
end;

// Typed into the box, or given as /PORT - otherwise the port is left as it is (7242, or a moved copy's own).
procedure PortChange(Sender: TObject);
begin
  PortTouched := True;
end;

function PortChosen: Boolean;
begin
  Result := ParamGiven('PORT') or PortTouched;
end;

// The main button is Next, exactly like Enter - see NextButtonClick.
procedure MainClick(Sender: TObject);
begin
  GoNext;
end;

procedure SecondaryClick(Sender: TObject);
var
  Code: Integer;
  Uninstaller: String;
begin
  if (WizardForm.CurPageID <> wpFinished) and Upgrading then begin
    // "uninstall": Windows' own uninstaller for this copy, then this setup steps aside.
    Uninstaller := AddBackslash(InstallDir) + 'unins000.exe';
    if IsPreview then MsgBox(CustomMessage('PreviewUninstall'), mbInformation, MB_OK)
    else if FileExists(Uninstaller) then begin
      ShellExecAsOriginalUser('open', Uninstaller, '', '', SW_SHOWNORMAL, ewNoWait, Code);
      WizardForm.Close;
    end;
    Exit;
  end;
  // "close" on the last screen
  CloseOnly := True;
  GoNext;
end;

procedure OpenCopy(const Dir: String);
var
  Code: Integer;
begin
  if not IsPreview then
    ShellExecAsOriginalUser('open', AddBackslash(Dir) + '{#AppExe}', '', Dir, SW_SHOWNORMAL, ewNoWait, Code);
end;

// Next - the main button, Enter, or a silent install's simulated click.
function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = wpReady then begin
    if not WizardSilent then begin
      // A newer nocat.farm is installed: no downgrade from here - the button opens the one that's there.
      if Upgrading and InstalledNewer then begin
        OpenCopy(InstallDir);
        Result := False;
        WizardForm.Close;
        Exit;
      end;
      if ShowingAdvanced and ((Port < 1024) or (Port > 65535)) then begin
        MsgBox(CustomMessage('AdvPortBad'), mbError, MB_OK);
        Result := False;
        Exit;
      end;
    end;
    WizardForm.DirEdit.Text := InstallDir;
  end else if CurPageID = wpFinished then begin
    if not WizardSilent and not CloseOnly then OpenCopy(ExpandConstant('{app}'));
  end;
end;

procedure SetState(const State: String);
var
  I: Integer;
  Main, Choosing: Boolean;
begin
  Main := State = 'main';
  Choosing := Main and not Upgrading;
  RunsRule.Visible := Choosing;
  ShowToggle(Toggles[0], Choosing);
  ShowToggle(Toggles[1], Choosing);
  // Other idlers only when there's no nocat.farm already; a portable one gets "move it here / start fresh".
  ComingFrom.Visible := Choosing and (Portable = '');
  SourceFrom.Visible := Choosing and (Portable = '');
  for I := 0 to SrcLast do ShowToggle(Sources[I], Choosing and (Portable = ''));
  ShowMove(Choosing and (Portable <> ''));
  PathLabel.Visible := Choosing;
  ChangeLink.Visible := Choosing;
  AdvLink.Visible := Choosing;
  if Choosing then PaintAdvanced else begin
    ShowToggle(Toggles[2], False);
    PortLabel.Visible := False;
    PortEdit.Visible := False;
  end;
  BarBack.Visible := State = 'installing';
  BarFill.Visible := State = 'installing';
  Percent.Visible := State = 'installing';
  CloseX.Visible := State <> 'installing';
  ShowButton(MainBtnEdge, MainBtnFace, MainBtnText, State <> 'installing');
  ShowButton(SecBtnEdge, SecBtnFace, SecBtnText, (State = 'done') or (Main and Upgrading));

  if State = 'main' then begin
    if Upgrading then begin
      Title.Caption := CustomMessage('InstalledTitle');
      if InstalledNewer then begin
        Note.Caption := FmtMessage(CustomMessage('NewerText'), [InstalledVersion]);
        MainBtnText.Caption := CustomMessage('OpenButton');
      end else begin
        Note.Caption := FmtMessage(CustomMessage('InstalledText'), [InstalledVersion]);
        if IsSameVersion(InstalledVersion) then MainBtnText.Caption := CustomMessage('ReinstallButton')
        else MainBtnText.Caption := FmtMessage(CustomMessage('UpdateToButton'), ['{#AppVersion}']);
      end;
      SecBtnText.Caption := CustomMessage('UninstallButton');
    end else begin
      Title.Caption := CustomMessage('InstallTitle');
      Note.Caption := '';
      MainBtnText.Caption := CustomMessage('InstallButton');
    end;
    if IsPreview and not ShotMode then Note.Caption := CustomMessage('PreviewBadge') + #13#10 + Note.Caption;
  end else if State = 'installing' then begin
    Title.Caption := CustomMessage('Installing');
    Note.Caption := '';
  end else begin
    if IsPreview and not ShotMode then begin
      Title.Caption := CustomMessage('PreviewDoneTitle');
      Note.Caption := CustomMessage('PreviewDone');
    end else if Upgrading then begin
      Title.Caption := CustomMessage('UpdatedTitle');
      Note.Caption := CustomMessage('UpdatedText');
    end else begin
      Title.Caption := CustomMessage('DoneTitle');
      Note.Caption := CustomMessage('DoneText');
    end;
    MainBtnText.Caption := CustomMessage('OpenButton');
    SecBtnText.Caption := CustomMessage('CloseButton');
  end;
end;

procedure InitializeWizard;
var
  W, H, I, Y: Integer;
  Farm, Drag: TLabel;
  Surface: TBitmapImage;
  Names: array[0..3] of String;
begin
  InstallDir := WizardForm.DirEdit.Text;

  // Installed already (Windows' installed-apps entry, the same one the uninstaller uses)? /UPGRADE shows that
  // screen in the preview, /NEWER the one for a newer version already installed. The app keeps DisplayVersion up
  // to date as it updates itself, so an older setup can find a newer version there.
  if IsPreview then begin
    Upgrading := ParamYes('UPGRADE', False) or ParamYes('NEWER', False);
    if ParamYes('NEWER', False) then InstalledVersion := '9.9.9' else InstalledVersion := '1.4.7';
  end else
    Upgrading := RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{89595F01-E60C-4593-9BA7-51B5A3A2F7C5}_is1', 'DisplayVersion', InstalledVersion)
      and FileExists(AddBackslash(InstallDir) + '{#AppExe}');
  InstalledNewer := Upgrading and IsNewer(InstalledVersion);
  if InstalledNewer then Log('version ' + InstalledVersion + ' is installed, newer than this setup ({#AppVersion})');

  // /FRESH shows what a PC with nothing on it gets (for the preview).
  if not Upgrading and not ParamYes('FRESH', False) then begin
    Portable := FindNocatFarm;
    SourcePath[SrcAsf] := FindAsf;
    SourcePath[SrcIdleMaster] := FindIdleMaster;
  end;

  // Screenshots never show whose PC they were taken on.
  if ShotMode and (Portable <> '') then Portable := 'C:\Users\you\Downloads\nocat.farm';

  // Borderless, a 1px grey edge (the form's own colour) around the black face - like the app's window.
  W := FormW;
  H := FormH;
  WizardForm.BorderStyle := bsNone;
  WizardForm.ClientWidth := ScaleX(W);
  WizardForm.ClientHeight := ScaleY(H);
  WizardForm.Position := poScreenCenter;
  WizardForm.Color := CLine;
  WizardForm.OuterNotebook.Visible := False;
  WizardForm.Bevel.Visible := False;
  HideNativeButtons;

  Surface := Box(0, 0, W, H, CBg);
  Surface.SetBounds(1, 1, WizardForm.ClientWidth - 2, WizardForm.ClientHeight - 2);

  // header: the wordmark (it drags the window, like a title bar), the version, a close cross
  Box(1, 52, W - 2, 1, CLine);
  Farm := Words('nocat.', 24, 16, 13, CText, True);
  Farm := Words('farm', 24, 16, 13, CPurple, True);
  Farm.Left := ScaleX(24) + Words('nocat.', -500, 16, 13, CText, True).Width;
  with Words('setup {#AppVersion}', 0, 21, 9, CMuted, False) do Left := ScaleX(W - 150);
  Drag := Words('', 0, 0, 9, CMuted, False);
  Drag.AutoSize := False;
  Drag.SetBounds(1, 1, WizardForm.ClientWidth - ScaleX(170), ScaleY(51));
  Drag.OnMouseDown := @DragWindow;
  CloseX := Words('x', W - 36, 14, 14, CMuted, False);
  CloseX.Cursor := crHand;
  CloseX.OnClick := @CloseClick;

  // who, what - and that it's free, right under the name where every language has room for it
  LogoImg := TBitmapImage.Create(WizardForm);
  LogoImg.Parent := WizardForm;
  LogoImg.SetBounds(ScaleX(24), ScaleY(76), ScaleX(64), ScaleY(64));
  LogoImg.Stretch := True;
  ExtractTemporaryFile('logo-128.bmp');
  LogoImg.Bitmap.LoadFromFile(ExpandConstant('{tmp}\logo-128.bmp'));
  Title := Words('', 104, 78, 15, CText, True);
  Tagline := Words(CustomMessage('Tagline'), 104, 106, 9, CMuted, False);
  FreeLine := Words(CustomMessage('FreeLine'), 104, 124, 8, CMuted, False);
  Wrap(FreeLine, 104, 124, W - 128, 16);
  Note := Words('', 24, 150, 10, CBody, False);
  Wrap(Note, 24, 150, W - 48, 70);

  // switching from - what the accounts were on before
  ComingFrom := Words(CustomMessage('ComingFrom'), 24, 182, 9, CMuted, False);
  Names[SrcFresh] := CustomMessage('SrcFresh');
  Names[SrcAsf] := 'ArchiSteamFarm';
  Names[SrcIdleMaster] := 'Idle Master (or Extended)';
  Names[SrcOther] := CustomMessage('SrcOther');
  Y := 204;
  for I := 0 to SrcLast do begin
    MakeToggle(Sources[I], Names[I], 24, Y, False, @SourceClick);
    Y := Y + 24;
  end;
  SourceFrom := Words('', 24, Y + 4, 9, CMuted, False);
  Wrap(SourceFrom, 24, Y + 4, W - 48, 32);
  // /FROM=fresh|asf|idlemaster|other answers "switching from" for unattended installs
  case Lowercase(ExpandConstant('{param:FROM|}')) of
    'fresh': PickSource(SrcFresh);
    'asf': PickSource(SrcAsf);
    'idlemaster': PickSource(SrcIdleMaster);
    'other': PickSource(SrcOther);
  else
    if SourcePath[SrcAsf] <> '' then PickSource(SrcAsf) else PickSource(SrcFresh);
  end;

  // a portable nocat.farm: move it into the install, or start fresh
  MoveHave := Words(CustomMessage('MoveHave'), 24, 182, 9, CMuted, False);
  MakeToggle(MovePick[0], CustomMessage('MoveHere'), 24, 204, False, @MoveClick);
  MoveFrom := Words('', 60, 226, 9, CMuted, False);
  Wrap(MoveFrom, 60, 226, W - 84, 32);
  MakeToggle(MovePick[1], CustomMessage('StartFresh'), 24, 266, False, @MoveClick);
  if ParamYes('MOVE', True) then PickMove(0) else PickMove(1);

  // how it runs - the port gets the second column of its own row, and its box sits right after its label
  RunsRule := Box(24, 336, W - 48, 1, CPanel);
  MakeToggle(Toggles[0], CustomMessage('OptStartup'), 24, 348, ParamYes('STARTUP', True), @ToggleClick);
  MakeToggle(Toggles[1], CustomMessage('OptDesktop'), Col2, 348, ParamYes('DESKTOP', True), @ToggleClick);
  MakeToggle(Toggles[2], CustomMessage('OptHidden'), 24, 374, ParamYes('HIDDEN', False), @ToggleClick);
  for I := 0 to 2 do Paint(Toggles[I], False);
  PortLabel := Words(CustomMessage('PortLabel'), Col2, 374, 10, CMuted, False);
  PortEdit := TNewEdit.Create(WizardForm);
  PortEdit.Parent := WizardForm;
  PortEdit.SetBounds(PortLabel.Left + PortLabel.Width + ScaleX(12), ScaleY(371), ScaleX(80), ScaleY(22));
  PortEdit.Font.Name := UiFont;
  PortEdit.Font.Color := CBody;
  PortEdit.Color := CPanel;
  PortEdit.Text := ExpandConstant('{param:PORT|' + IntToStr(DefaultPort) + '}');
  PortEdit.OnChange := @PortChange;
  ShowingAdvanced := ParamGiven('PORT') or ParamYes('HIDDEN', False);

  PathLabel := Words('', 24, 404, 9, CMuted, False);
  ChangeLink := Words(CustomMessage('Change'), 0, 404, 9, CBody, False);
  ChangeLink.Font.Style := [fsUnderline];
  ChangeLink.Cursor := crHand;
  ChangeLink.OnClick := @ChangeClick;
  ShowPath;
  AdvLink := Words('', 24, 426, 9, CBody, False);
  AdvLink.Cursor := crHand;
  AdvLink.OnClick := @AdvancedClick;

  // progress
  BarBack := Box(24, 204, W - 48, 4, CLine);
  BarFill := Box(24, 204, 0, 4, CPurple);
  Percent := Words('', 24, 218, 9, CMuted, False);

  // bottom: the links, the buttons
  Box(1, H - 68, W - 2, 1, CLine);
  LinkGit := Link('github', 24, H - 40);
  LinkSite := Link('nocat.farm', 0, H - 40);
  LinkSite.Left := LinkGit.Left + LinkGit.Width + ScaleX(18);
  LinkMaker := Link(CustomMessage('MadeBy'), 0, H - 40);
  LinkMaker.Left := LinkSite.Left + LinkSite.Width + ScaleX(18);
  MakeButton(MainBtnEdge, MainBtnFace, MainBtnText, W - 24 - MainBtnW, H - 51, MainBtnW, True, @MainClick);
  MakeButton(SecBtnEdge, SecBtnFace, SecBtnText, W - 24 - MainBtnW - 12 - SecBtnW, H - 51, SecBtnW, False, @SecondaryClick);

  SetState('main');
  HoverTimer := SetTimer(0, 0, 50, CreateCallback(@HoverTick));
end;

procedure DeinitializeSetup;
begin
  if HoverTimer <> 0 then KillTimer(0, HoverTimer);
  HoverTimer := 0;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  HideNativeButtons;
  if CurPageID = wpInstalling then SetState('installing')
  else if CurPageID = wpFinished then SetState('done');
end;

procedure CancelButtonClick(CurPageID: Integer; var Cancel, Confirm: Boolean);
begin
  Confirm := False;   // the cross means close - no "are you sure" box
end;

procedure CurInstallProgressChanged(CurProgress, MaxProgress: Integer);
begin
  if MaxProgress > 0 then begin
    BarFill.Width := (BarBack.Width * CurProgress) div MaxProgress;
    Percent.Caption := IntToStr((CurProgress * 100) div MaxProgress) + '%';
  end;
end;

// ── installing ──────────────────────────────────────────────────────────────

function TrueFalse(B: Boolean): String;
begin
  if B then Result := 'true' else Result := 'false';
end;

function LanguageCode: String;
begin
  case ActiveLanguage of
    'german': Result := 'de';
    'spanish': Result := 'es';
    'french': Result := 'fr';
    'japanese': Result := 'ja';
    'korean': Result := 'ko';
    'polish': Result := 'pl';
    'brazilianportuguese': Result := 'pt-BR';
    'russian': Result := 'ru';
    'turkish': Result := 'tr';
    'chinesesimplified': Result := 'zh-CN';
  else
    Result := 'en';
  end;
end;

function SourceKey: String;
begin
  case Source of
    SrcAsf: Result := 'asf';
    SrcIdleMaster: Result := 'idlemaster';
    SrcOther: Result := 'other';
    SrcFresh: Result := 'none';
  else
    Result := '';
  end;
end;

// Why --setup didn't save, from the line it wrote to logs\setup.log - only when that line is about this very exit code,
// so an old failure in the same file is never shown as today's. The code alone told nobody what to fix.
function SetupProblem(Code: Integer): String;
var
  Lines: TArrayOfString;
  Last, Mark: String;
  P: Integer;
begin
  Result := '';
  if not LoadStringsFromFile(ExpandConstant('{app}\logs\setup.log'), Lines) or (GetArrayLength(Lines) = 0) then Exit;
  Last := Trim(Lines[GetArrayLength(Lines) - 1]);
  Mark := '--setup exit ' + IntToStr(Code) + ': ';
  P := Pos(Mark, Last);
  if P > 0 then Result := Copy(Last, P + Length(Mark), Length(Last));
end;

// Keeping: a portable copy's accounts are in the install (moved in, or it was installed over) - its own language,
// port and "switching from" stay as they were.
procedure SaveChoices(Keeping: Boolean);
var
  Args, Why: String;
  Code: Integer;
begin
  Args := '--setup StartWithWindows=' + TrueFalse(Toggles[0].On);
  if not Keeping then Args := Args + ' Language=' + LanguageCode;
  if ShowingAdvanced then
    Args := Args + ' StartMinimized=' + TrueFalse(Toggles[2].On);
  // The port only when it was chosen - a moved copy keeps its own, rather than the box's 7242. One that isn't a port
  // (a silent /PORT=abc skips the screen's check) is left out, instead of failing every other choice with it.
  if PortChosen and (Port >= 1024) and (Port <= 65535) then
    Args := Args + ' WebPort=' + IntToStr(Port);
  // Another idler: the dashboard's first-run setup opens on importing from it.
  // (Starting fresh instead of moving a portable copy over counts as coming from nothing.)
  if Portable <> '' then begin
    if not Keeping then Args := Args + ' ImportFrom=none';
  end else if SourceKey <> '' then begin
    Args := Args + ' ImportFrom=' + SourceKey;
    if SourcePath[Source] <> '' then Args := Args + ' "ImportPath=' + SourcePath[Source] + '"';
  end;
  if not Exec(ExpandConstant('{app}\{#AppExe}'), Args, ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then begin
    // The reason is in English, as nocat.farm wrote it - still more use than a bare number.
    Why := SetupProblem(Code);
    if Why <> '' then Why := #13#10#13#10 + Why;
    Log('--setup failed with exit code ' + IntToStr(Code) + Why);
    SuppressibleMsgBox(FmtMessage(CustomMessage('SetupFailed'), [IntToStr(Code)]) + Why, mbInformation, MB_OK, IDOK);
  end;
end;

// robocopy: 0-7 is copied (or nothing needed copying), 8 and up is a failure.
function CopyTree(const From, Dest: String): Boolean;
var
  Code: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\robocopy.exe'), '"' + From + '" "' + Dest + '" /E /R:2 /W:1 /NFL /NDL /NJH /NJS', '', SW_HIDE, ewWaitUntilTerminated, Code)
    and (Code < 8);
  Log('robocopy ' + From + ' -> ' + Dest + ': exit ' + IntToStr(Code));
end;

// The portable copy's accounts into the install. False when they couldn't be copied: whatever part arrived is taken
// back out, and the portable copy - accounts, startup entry and all - is left exactly as it was.
function MoveIn: Boolean;
var
  Run, Aside: String;
begin
  // Data kept by an earlier uninstall: set aside rather than used instead of the copy being moved in. If it can't be
  // (Explorer, a terminal or a virus scan has something in it open), nothing is copied at all: merged into the kept
  // folder, a copy that failed half way could only be undone by deleting that folder - the kept accounts with it.
  Aside := '';
  if DirExists(ExpandConstant('{app}\config')) then begin
    Aside := ExpandConstant('{app}\config.old-') + GetDateTimeString('yyyymmdd-hhnnss', #0, #0);
    if not RenameFile(ExpandConstant('{app}\config'), Aside) then begin
      Log('MoveIn: couldn''t set the kept ' + ExpandConstant('{app}\config') + ' aside - not moving ' + Portable + ' in');
      Result := False;
      SuppressibleMsgBox(FmtMessage(CustomMessage('CopyFailed'), [Portable]), mbError, MB_OK, IDOK);
      Exit;
    end;
  end;
  // From here {app}\config is this run's own (nothing was there, or it was set aside): taking a failed copy back out
  // deletes only what this run put there.
  Result := CopyTree(Portable + '\config', ExpandConstant('{app}\config'));
  if not Result then begin
    DelTree(ExpandConstant('{app}\config'), True, True, True);
    if Aside <> '' then RenameFile(Aside, ExpandConstant('{app}\config'));
    SuppressibleMsgBox(FmtMessage(CustomMessage('CopyFailed'), [Portable]), mbError, MB_OK, IDOK);
    Exit;
  end;
  // The history comes along too; if it can't, the accounts are still moved.
  if DirExists(Portable + '\logs') then CopyTree(Portable + '\logs', ExpandConstant('{app}\logs'));
  // The portable's startup entry would start the old copy at every sign-in next to this one. With start with
  // Windows on, the choices below point it here; with it off, nothing should start at all.
  if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'nocatFarm', Run)
    and SameDir(DirOfExe(Run), Portable) then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'nocatFarm');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Keeping: Boolean;
begin
  if IsPreview then begin
    if CurStep = ssDone then RemoveDir(ExpandConstant('{app}'));   // nothing was put there
    Exit;
  end;

  if CurStep = ssInstall then begin
    CloseCopy(RemoveBackslash(ExpandConstant('{app}')));
    if MovingPortable then CloseCopy(Portable);
  end else if (CurStep = ssPostInstall) and not Upgrading then begin
    Keeping := KeepingPortable;
    if MovingPortable then Keeping := MoveIn;
    SaveChoices(Keeping);
  end;
end;

// ── uninstalling ────────────────────────────────────────────────────────────

// Accounts, settings, backups (zips with every login in them) and logs - and config set aside by a move.
procedure DeleteData;
var
  App: String;
  F: TFindRec;
begin
  App := RemoveBackslash(ExpandConstant('{app}'));
  if CompareText(ExtractFileName(App), '{#AppName}') = 0 then begin
    DelTree(App, True, True, True);   // updates add files the uninstall log never saw
    Exit;
  end;
  // A folder of the person's own choosing: only what nocat.farm put there.
  DelTree(App + '\config', True, True, True);
  DelTree(App + '\logs', True, True, True);
  DelTree(App + '\backups', True, True, True);
  if FindFirst(App + '\config.old-*', F) then
    try
      repeat
        if (F.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then DelTree(App + '\' + F.Name, True, True, True);
      until not FindNext(F);
    finally
      FindClose(F);
    end;
  RemoveDir(App);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Run: String;
  Code: Integer;
  Wipe: Boolean;
begin
  // After "are you sure" - answering no must leave nocat.farm running.
  if CurUninstallStep = usUninstall then CloseCopy(RemoveBackslash(ExpandConstant('{app}')));
  if CurUninstallStep <> usPostUninstall then Exit;

  if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'nocatFarm', Run)
    and SameDir(DirOfExe(Run), ExpandConstant('{app}')) then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'nocatFarm');

  // The firewall rule the dashboard's button makes, only if there is one - removing it needs Windows' prompt, so it's
  // said first, and it's netsh itself that asks, not a command prompt. Any copy's button makes the same rule, so it
  // stays while another copy is set to start with Windows - and a silent uninstall never stops on Windows' prompt.
  if not UninstallSilent
    and not (RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'nocatFarm', Run) and FileExists(DirOfExe(Run) + '\{#AppExe}'))
    and Exec(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall show rule name="nocat.farm dashboard"', '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0) then begin
    MsgBox(CustomMessage('FirewallAsk'), mbInformation, MB_OK);
    ShellExec('runas', ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall delete rule name="nocat.farm dashboard"', '', SW_HIDE, ewWaitUntilTerminated, Code);
  end;

  // /DELETEDATA=yes|no answers this for an unattended uninstall; otherwise it's asked (a silent one keeps them).
  if DirExists(ExpandConstant('{app}\config')) then begin
    if ParamGiven('DELETEDATA') then Wipe := ParamYes('DELETEDATA', False)
    else Wipe := SuppressibleMsgBox(CustomMessage('UninstallKeep'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES;
    if Wipe then DeleteData;
  end;
end;
