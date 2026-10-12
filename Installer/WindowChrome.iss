// Native controls keep their original click handlers, tab order, and accessibility.
// Only their painting and the wizard's non-client decoration are replaced.
const
  ChromeWMDrawItem = $002B;
  ChromeWMMouseMove = $0200;
  ChromeWMMouseLeave = $02A3;
  ChromeWMLButtonDown = $0201;
  ChromeWMTimer = $0113;
  ChromeWMSettingChange = $001A;
  ChromeWMNCLButtonDown = $00A1;
  ChromeWMSetFocus = $0007;
  ChromeWMKillFocus = $0008;
  ChromeWMNCDestroy = $0082;
  ChromeWMPaint = $000F;
  ChromeWMPrintClient = $0318;
  ChromeWMEraseBkgnd = $0014;
  ChromeWMEnable = $000A;
  ChromeWMSize = $0005;
  ChromeWMUpdateUIState = $0128;
  ChromeWMQueryUIState = $0129;
  ChromeUISFHideFocus = $0001;
  ChromeBMSetStyle = $00F4;
  ChromeBMGetState = $00F2;
  ChromeBMClick = $00F5;
  ChromeBSOwnerDraw = $000B;
  ChromeODSSelected = $0001;
  ChromeODSDisabled = $0004;
  ChromeODSFocus = $0010;
  ChromeODTButton = 4;
  ChromeODTListBox = 2;
  ChromeAnimationTimerID = $4D434841;

type
  TChromeTween = record
    Value, StartValue, TargetValue: Single;
    StartedAt, Duration: LongWord;
  end;
  TChromeBitmapInfo = record
    Size: LongWord;
    Width, Height: Integer;
    Planes, BitCount: Word;
    Compression, ImageSize: LongWord;
    XPelsPerMeter, YPelsPerMeter: Integer;
    ColorsUsed, ColorsImportant: LongWord;
  end;
  TChromeBufferedPaint = record
    TargetDC, DC, Bitmap, PreviousBitmap: NativeUInt;
    TargetRectangle: TRect;
  end;
  TChromeButton = record
    Control: TNewButton;
    Handle: NativeUInt;
    Hovered: Boolean;
    Hover, Press: TChromeTween;
    AnimationTimerActive: Boolean;
    RegionWidth, RegionHeight, RegionRadiusX, RegionRadiusY: Integer;
  end;
  TChromeParent = record
    Handle: NativeUInt;
  end;
  TChromeTrackMouse = record
    StructSize: LongWord;
    Flags: LongWord;
    Window: NativeUInt;
    HoverTime: LongWord;
    // Windows x64 adds tail padding. Windows x86 uses only the first 16 bytes.
    TailPadding: LongWord;
  end;
  TChromeControlBounds = record
    Left, Top, Width, Height: Integer;
  end;
  // Large enough for PAINTSTRUCT on both x86 (64 bytes) and x64 (72 bytes).
  TChromePaintBuffer = array[0..79] of Byte;
  TChromeGdiPlusInput32 = record
    Version: LongWord;
    DebugCallback: LongWord;
    SuppressBackgroundThread: LongWord;
    SuppressExternalCodecs: LongWord;
  end;
  TChromeGdiPlusInput64 = record
    Version: LongWord;
    // Native C structure aligns its callback pointer at offset 8 on x64.
    PointerPadding: LongWord;
    DebugCallback: NativeUInt;
    SuppressBackgroundThread: LongWord;
    SuppressExternalCodecs: LongWord;
  end;

function ChromeSetWindowSubclass(Window: NativeUInt; Callback: NativeInt; ID, ReferenceData: NativeUInt): Boolean;
  external 'SetWindowSubclass@comctl32.dll stdcall';
function ChromeRemoveWindowSubclass(Window: NativeUInt; Callback: NativeInt; ID: NativeUInt): Boolean;
  external 'RemoveWindowSubclass@comctl32.dll stdcall';
function ChromeDefSubclassProc(Window: NativeUInt; Message: LongWord; WParam: NativeUInt; LParam: NativeInt): NativeInt;
  external 'DefSubclassProc@comctl32.dll stdcall';
function ChromeSendMessage(Window: NativeUInt; Message: LongWord; WParam: NativeUInt; LParam: NativeInt): NativeInt;
  external 'SendMessageW@user32.dll stdcall';
function ChromeGetListItemRectangle(Window: NativeUInt; Message: LongWord; ItemIndex: NativeUInt;
  var Rectangle: TRect): NativeInt;
  external 'SendMessageW@user32.dll stdcall';
function ChromeReleaseCapture: Boolean;
  external 'ReleaseCapture@user32.dll stdcall';
function ChromeShowWindow(Window: NativeUInt; Command: Integer): Boolean;
  external 'ShowWindow@user32.dll stdcall';
function ChromeInvalidateRect(Window: NativeUInt; Rectangle: NativeUInt; Erase: Boolean): Boolean;
  external 'InvalidateRect@user32.dll stdcall';
function ChromeTrackMouseEvent(var Event: TChromeTrackMouse): Boolean;
  external 'TrackMouseEvent@user32.dll stdcall';
function ChromeSetTimer(Window, TimerID: NativeUInt; Interval: LongWord; Callback: NativeUInt): NativeUInt;
  external 'SetTimer@user32.dll stdcall';
function ChromeKillTimer(Window, TimerID: NativeUInt): Boolean;
  external 'KillTimer@user32.dll stdcall';
function ChromeGetTickCount: LongWord;
  external 'GetTickCount@kernel32.dll stdcall';
function ChromeGetAnimationSetting(Action, Parameter: LongWord; var Value: LongWord; Flags: LongWord): Boolean;
  external 'SystemParametersInfoW@user32.dll stdcall';
procedure ChromeReadLong(var Destination: LongWord; Source: NativeUInt; Length: NativeUInt);
  external 'RtlMoveMemory@kernel32.dll stdcall';
procedure ChromeReadNative(var Destination: NativeUInt; Source: NativeUInt; Length: NativeUInt);
  external 'RtlMoveMemory@kernel32.dll stdcall';
function ChromeSaveDC(DC: NativeUInt): Integer;
  external 'SaveDC@gdi32.dll stdcall';
function ChromeRestoreDC(DC: NativeUInt; Saved: Integer): Boolean;
  external 'RestoreDC@gdi32.dll stdcall';
function ChromeCreateBrush(Color: LongWord): NativeUInt;
  external 'CreateSolidBrush@gdi32.dll stdcall';
function ChromeSelectObject(DC, ObjectHandle: NativeUInt): NativeUInt;
  external 'SelectObject@gdi32.dll stdcall';
function ChromeDeleteObject(ObjectHandle: NativeUInt): Boolean;
  external 'DeleteObject@gdi32.dll stdcall';
function ChromeCreateCompatibleDC(DC: NativeUInt): NativeUInt;
  external 'CreateCompatibleDC@gdi32.dll stdcall';
function ChromeDeleteDC(DC: NativeUInt): Boolean;
  external 'DeleteDC@gdi32.dll stdcall';
function ChromeCreateDIBSection(DC: NativeUInt; var Info: TChromeBitmapInfo; Usage: LongWord;
  var Bits: NativeUInt; Section: NativeUInt; Offset: LongWord): NativeUInt;
  external 'CreateDIBSection@gdi32.dll stdcall';
function ChromeBitBlt(DestinationDC: NativeUInt; X, Y, Width, Height: Integer;
  SourceDC: NativeUInt; SourceX, SourceY: Integer; RasterOperation: LongWord): Boolean;
  external 'BitBlt@gdi32.dll stdcall';
// Pascal Script's DLL bridge passes `const` records by value. These Windows
// APIs require pointers: explicit `var` is essential, especially on x86.
function ChromeFillRect(DC: NativeUInt; var Rectangle: TRect; Brush: NativeUInt): Integer;
  external 'FillRect@user32.dll stdcall';
function ChromeBeginPaint(Window: NativeUInt; var PaintBuffer: TChromePaintBuffer): NativeUInt;
  external 'BeginPaint@user32.dll stdcall';
function ChromeEndPaint(Window: NativeUInt; var PaintBuffer: TChromePaintBuffer): Boolean;
  external 'EndPaint@user32.dll stdcall';
function ChromeGetClientRect(Window: NativeUInt; var Rectangle: TRect): Boolean;
  external 'GetClientRect@user32.dll stdcall';
function ChromeIsWindowEnabled(Window: NativeUInt): Boolean;
  external 'IsWindowEnabled@user32.dll stdcall';
function ChromeSetTextColor(DC: NativeUInt; Color: LongWord): LongWord;
  external 'SetTextColor@gdi32.dll stdcall';
function ChromeSetBkMode(DC: NativeUInt; Mode: Integer): Integer;
  external 'SetBkMode@gdi32.dll stdcall';
function ChromeDrawText(DC: NativeUInt; Text: String; Count: Integer; var Rectangle: TRect; Format: LongWord): Integer;
  external 'DrawTextW@user32.dll stdcall';
function ChromeCreateRoundRectRgn(Left, Top, Right, Bottom, EllipseWidth, EllipseHeight: Integer): NativeUInt;
  external 'CreateRoundRectRgn@gdi32.dll stdcall';
function ChromeSetWindowRgn(Window, Region: NativeUInt; Redraw: Boolean): Integer;
  external 'SetWindowRgn@user32.dll stdcall';

// GDI+ is the Windows component, not a bundled skin DLL. All output pointers
// and native structures explicitly use `var` in the Pascal Script DLL bridge.
function ChromeGdiPlusStartup32(var Token: NativeUInt; var Input: TChromeGdiPlusInput32; Output: NativeUInt): Integer;
  external 'GdiplusStartup@gdiplus.dll stdcall';
function ChromeGdiPlusStartup64(var Token: NativeUInt; var Input: TChromeGdiPlusInput64; Output: NativeUInt): Integer;
  external 'GdiplusStartup@gdiplus.dll stdcall';
procedure ChromeGdiPlusShutdown(Token: NativeUInt);
  external 'GdiplusShutdown@gdiplus.dll stdcall';
function ChromeGdipCreateFromHDC(DC: NativeUInt; var Graphics: NativeUInt): Integer;
  external 'GdipCreateFromHDC@gdiplus.dll stdcall';
function ChromeGdipDeleteGraphics(Graphics: NativeUInt): Integer;
  external 'GdipDeleteGraphics@gdiplus.dll stdcall';
function ChromeGdipSetSmoothingMode(Graphics: NativeUInt; Mode: Integer): Integer;
  external 'GdipSetSmoothingMode@gdiplus.dll stdcall';
function ChromeGdipSetPixelOffsetMode(Graphics: NativeUInt; Mode: Integer): Integer;
  external 'GdipSetPixelOffsetMode@gdiplus.dll stdcall';
function ChromeGdipSetCompositingQuality(Graphics: NativeUInt; Quality: Integer): Integer;
  external 'GdipSetCompositingQuality@gdiplus.dll stdcall';
function ChromeGdipSetPageUnit(Graphics: NativeUInt; UnitValue: Integer): Integer;
  external 'GdipSetPageUnit@gdiplus.dll stdcall';
function ChromeGdipCreateSolidFill(Color: LongWord; var Brush: NativeUInt): Integer;
  external 'GdipCreateSolidFill@gdiplus.dll stdcall';
function ChromeGdipDeleteBrush(Brush: NativeUInt): Integer;
  external 'GdipDeleteBrush@gdiplus.dll stdcall';
function ChromeGdipCreatePen1(Color: LongWord; Width: Single; UnitValue: Integer; var Pen: NativeUInt): Integer;
  external 'GdipCreatePen1@gdiplus.dll stdcall';
function ChromeGdipDeletePen(Pen: NativeUInt): Integer;
  external 'GdipDeletePen@gdiplus.dll stdcall';
function ChromeGdipSetPenLineCap(Pen: NativeUInt; StartCap, EndCap, DashCap: Integer): Integer;
  external 'GdipSetPenLineCap197819@gdiplus.dll stdcall';
function ChromeGdipSetPenLineJoin(Pen: NativeUInt; JoinValue: Integer): Integer;
  external 'GdipSetPenLineJoin@gdiplus.dll stdcall';
function ChromeGdipCreatePath(FillMode: Integer; var Path: NativeUInt): Integer;
  external 'GdipCreatePath@gdiplus.dll stdcall';
function ChromeGdipDeletePath(Path: NativeUInt): Integer;
  external 'GdipDeletePath@gdiplus.dll stdcall';
function ChromeGdipAddPathArc(Path: NativeUInt; X, Y, Width, Height, StartAngle, SweepAngle: Single): Integer;
  external 'GdipAddPathArc@gdiplus.dll stdcall';
function ChromeGdipClosePathFigure(Path: NativeUInt): Integer;
  external 'GdipClosePathFigure@gdiplus.dll stdcall';
function ChromeGdipFillPath(Graphics, Brush, Path: NativeUInt): Integer;
  external 'GdipFillPath@gdiplus.dll stdcall';
function ChromeGdipDrawPath(Graphics, Pen, Path: NativeUInt): Integer;
  external 'GdipDrawPath@gdiplus.dll stdcall';
function ChromeGdipDrawLine(Graphics, Pen: NativeUInt; X1, Y1, X2, Y2: Single): Integer;
  external 'GdipDrawLine@gdiplus.dll stdcall';
function ChromeGdipFillEllipse(Graphics, Brush: NativeUInt; X, Y, Width, Height: Single): Integer;
  external 'GdipFillEllipse@gdiplus.dll stdcall';
function ChromeGdipDrawEllipse(Graphics, Pen: NativeUInt; X, Y, Width, Height: Single): Integer;
  external 'GdipDrawEllipse@gdiplus.dll stdcall';

var
  ChromeButtons: array of TChromeButton;
  ChromeParents: array of TChromeParent;
  ChromeButtonCallback, ChromeParentCallback: NativeInt;
  ChromeHeaderCallback: NativeInt;
  ChromeRadioCallback: NativeInt;
  ChromeTasksCallback: NativeInt;
  ChromeTasksRegionWidth, ChromeTasksRegionHeight, ChromeTasksRegionRadiusX, ChromeTasksRegionRadiusY: Integer;
  ChromeRadioControls: array of TNewRadioButton;
  ChromeHeader: TPanel;
  ChromeHeaderTitle: TNewStaticText;
  ChromeCloseButton, ChromeMinimizeButton: TNewButton;
  ChromeInitialized: Boolean;
  ChromeAnimationsEnabled: Boolean;
  ChromeGdiPlusToken: NativeUInt;

function ChromePointerSize: Integer;
var
  Value: NativeUInt;
begin
  Result := SizeOf(Value);
end;

procedure ChromeReleasePaintBuffer(var Buffer: TChromeBufferedPaint);
begin
  if (Buffer.DC <> 0) and (Buffer.PreviousBitmap <> 0) then
    ChromeSelectObject(Buffer.DC, Buffer.PreviousBitmap);
  if Buffer.Bitmap <> 0 then ChromeDeleteObject(Buffer.Bitmap);
  if Buffer.DC <> 0 then ChromeDeleteDC(Buffer.DC);
  Buffer.DC := 0;
  Buffer.Bitmap := 0;
  Buffer.PreviousBitmap := 0;
end;

function ChromeBeginBufferedPaint(TargetDC: NativeUInt; var Rectangle: TRect;
  var Buffer: TChromeBufferedPaint): NativeUInt;
var
  Info: TChromeBitmapInfo;
  Bits: NativeUInt;
  Width, Height: Integer;
begin
  Buffer.TargetDC := TargetDC;
  Buffer.TargetRectangle := Rectangle;
  Buffer.DC := 0;
  Buffer.Bitmap := 0;
  Buffer.PreviousBitmap := 0;
  // Fall back to the original DC if Windows cannot allocate a small buffer;
  // input/installation must not depend on optional visual resources.
  Result := TargetDC;
  Width := Rectangle.Right - Rectangle.Left;
  Height := Rectangle.Bottom - Rectangle.Top;
  if (TargetDC = 0) or (Width <= 0) or (Height <= 0) or
    (Width > 8192) or (Height > 8192) then Exit;
  Buffer.DC := ChromeCreateCompatibleDC(TargetDC);
  if Buffer.DC = 0 then Exit;
  Info.Size := 40; // BITMAPINFOHEADER: no native pointer padding on x86/x64.
  Info.Width := Width;
  Info.Height := -Height; // Top-down, opaque 32-bit pixels for GDI + GDI+.
  Info.Planes := 1;
  Info.BitCount := 32;
  Info.Compression := 0;
  Info.ImageSize := Width * Height * 4;
  Info.XPelsPerMeter := 0;
  Info.YPelsPerMeter := 0;
  Info.ColorsUsed := 0;
  Info.ColorsImportant := 0;
  Bits := 0;
  Buffer.Bitmap := ChromeCreateDIBSection(TargetDC, Info, 0, Bits, 0, 0);
  if Buffer.Bitmap <> 0 then
    Buffer.PreviousBitmap := ChromeSelectObject(Buffer.DC, Buffer.Bitmap);
  if NativeInt(Buffer.PreviousBitmap) = -1 then Buffer.PreviousBitmap := 0;
  if (Buffer.Bitmap = 0) or (Buffer.PreviousBitmap = 0) then begin
    ChromeReleasePaintBuffer(Buffer);
    Exit;
  end;
  // Each item is painted at the buffer origin. The destination's original
  // clip region is applied only to the final blit, never to intermediate text.
  Rectangle.Left := 0;
  Rectangle.Top := 0;
  Rectangle.Right := Width;
  Rectangle.Bottom := Height;
  Result := Buffer.DC;
end;

procedure ChromeEndBufferedPaint(var Buffer: TChromeBufferedPaint);
begin
  try
    if Buffer.DC <> 0 then
      // One completed frame: no visible clear -> border -> caption sequence.
      ChromeBitBlt(Buffer.TargetDC, Buffer.TargetRectangle.Left, Buffer.TargetRectangle.Top,
        Buffer.TargetRectangle.Right - Buffer.TargetRectangle.Left,
        Buffer.TargetRectangle.Bottom - Buffer.TargetRectangle.Top,
        Buffer.DC, 0, 0, $00CC0020); // SRCCOPY
  finally
    ChromeReleasePaintBuffer(Buffer);
  end;
end;

function ChromeBlendColor(FromColor, ToColor: LongWord; Amount: Single): LongWord;
var
  Red, Green, Blue, FromRed, FromGreen, FromBlue, ToRed, ToGreen, ToBlue: Integer;
begin
  if Amount <= 0 then begin Result := FromColor; Exit; end;
  if Amount >= 1 then begin Result := ToColor; Exit; end;
  // Extract into signed integers before subtraction (LongWord would wrap
  // when blending a bright channel toward a darker pressed-state color).
  FromRed := FromColor and $FF;
  FromGreen := (FromColor shr 8) and $FF;
  FromBlue := (FromColor shr 16) and $FF;
  ToRed := ToColor and $FF;
  ToGreen := (ToColor shr 8) and $FF;
  ToBlue := (ToColor shr 16) and $FF;
  Red := Round(FromRed + (ToRed - FromRed) * Amount);
  Green := Round(FromGreen + (ToGreen - FromGreen) * Amount);
  Blue := Round(FromBlue + (ToBlue - FromBlue) * Amount);
  Result := Red or (Green shl 8) or (Blue shl 16);
end;

function ChromeAdvanceTween(var Tween: TChromeTween; Tick: LongWord): Boolean;
var
  Elapsed: LongWord;
  Progress: Single;
begin
  Result := False;
  if Tween.Duration = 0 then Exit;
  // DWORD uptime wraps after 49.7 days; a short animation must not jump back.
  if Tick >= Tween.StartedAt then Elapsed := Tick - Tween.StartedAt
  else Elapsed := ($FFFFFFFF - Tween.StartedAt) + Tick + 1;
  if Elapsed >= Tween.Duration then begin
    Tween.Value := Tween.TargetValue;
    Tween.Duration := 0;
    Exit;
  end;
  Progress := Elapsed / (Tween.Duration * 1.0);
  Tween.Value := Tween.StartValue + (Tween.TargetValue - Tween.StartValue) * Progress;
  Result := True;
end;

function ChromeSetTweenTarget(var Tween: TChromeTween; Target: Single;
  Duration, Tick: LongWord): Boolean;
begin
  Result := False;
  if Tween.TargetValue = Target then Exit;
  // Reversing halfway through a hover starts from the current frame, not zero.
  ChromeAdvanceTween(Tween, Tick);
  Tween.StartValue := Tween.Value;
  Tween.TargetValue := Target;
  Tween.StartedAt := Tick;
  Tween.Duration := Duration;
  if not ChromeAnimationsEnabled or (Duration = 0) then begin
    Tween.Value := Target;
    Tween.Duration := 0;
  end;
  Result := True;
end;

procedure ChromeStopButtonTimer(Index: Integer);
begin
  if ChromeButtons[Index].AnimationTimerActive then begin
    ChromeKillTimer(ChromeButtons[Index].Handle, ChromeAnimationTimerID);
    ChromeButtons[Index].AnimationTimerActive := False;
  end;
end;

procedure ChromeQueueButtonFrame(Index: Integer);
begin
  if (ChromeButtons[Index].Hover.Duration <> 0) or (ChromeButtons[Index].Press.Duration <> 0) then begin
    if not ChromeButtons[Index].AnimationTimerActive then begin
      // WM_TIMER is delivered through the existing HWND subclass. No separate
      // script callback can survive destruction of the real native button.
      ChromeButtons[Index].AnimationTimerActive :=
        ChromeSetTimer(ChromeButtons[Index].Handle, ChromeAnimationTimerID, 15, 0) <> 0;
      if not ChromeButtons[Index].AnimationTimerActive then begin
        ChromeButtons[Index].Hover.Value := ChromeButtons[Index].Hover.TargetValue;
        ChromeButtons[Index].Hover.Duration := 0;
        ChromeButtons[Index].Press.Value := ChromeButtons[Index].Press.TargetValue;
        ChromeButtons[Index].Press.Duration := 0;
      end;
    end;
  end else ChromeStopButtonTimer(Index);
  ChromeInvalidateRect(ChromeButtons[Index].Handle, 0, False);
end;

procedure ChromeSetButtonHovered(Index: Integer; Hovered: Boolean);
var
  Target: Single;
  Duration: LongWord;
begin
  if not ChromeButtons[Index].Control.Enabled then Hovered := False;
  ChromeButtons[Index].Hovered := Hovered;
  if Hovered then begin Target := 1; Duration := 130; end
  else begin Target := 0; Duration := 150; end;
  if ChromeSetTweenTarget(ChromeButtons[Index].Hover, Target, Duration, ChromeGetTickCount) then
    ChromeQueueButtonFrame(Index);
end;

procedure ChromeSetButtonPressed(Index: Integer; Pressed: Boolean);
var
  Target: Single;
  Duration: LongWord;
begin
  if not ChromeButtons[Index].Control.Enabled then Pressed := False;
  // App.xaml dims the full border to 0.72 while pressed; a short color blend
  // gives that feedback without moving/scaling the native HWND or its text.
  if Pressed then begin Target := 1; Duration := 55; end
  else begin Target := 0; Duration := 90; end;
  if ChromeSetTweenTarget(ChromeButtons[Index].Press, Target, Duration, ChromeGetTickCount) then
    ChromeQueueButtonFrame(Index);
end;

procedure ChromeRefreshAnimationPreference;
var
  Enabled: LongWord;
  I: Integer;
begin
  Enabled := 1;
  // Read-only SPI_GETCLIENTAREAANIMATION: respect reduced motion in Windows.
  if ChromeGetAnimationSetting($1042, 0, Enabled, 0) then
    ChromeAnimationsEnabled := Enabled <> 0;
  if ChromeAnimationsEnabled then Exit;
  for I := 0 to GetArrayLength(ChromeButtons) - 1 do
    if ChromeButtons[I].Handle <> 0 then begin
      ChromeStopButtonTimer(I);
      ChromeButtons[I].Hover.Value := ChromeButtons[I].Hover.TargetValue;
      ChromeButtons[I].Hover.Duration := 0;
      ChromeButtons[I].Press.Value := ChromeButtons[I].Press.TargetValue;
      ChromeButtons[I].Press.Duration := 0;
      // InvalidateRect(NULL, ...) means every window on the desktop, not a
      // destroyed child; late settings broadcasts must stay within setup.
      ChromeInvalidateRect(ChromeButtons[I].Handle, 0, False);
    end;
end;

procedure ChromeInitializeAA;
var
  Input32: TChromeGdiPlusInput32;
  Input64: TChromeGdiPlusInput64;
  Status: Integer;
begin
  if ChromeGdiPlusToken <> 0 then Exit;
  if ChromePointerSize = 8 then begin
    Input64.Version := 1;
    Input64.PointerPadding := 0;
    Input64.DebugCallback := 0;
    Input64.SuppressBackgroundThread := 0;
    Input64.SuppressExternalCodecs := 1;
    Status := ChromeGdiPlusStartup64(ChromeGdiPlusToken, Input64, 0);
  end else begin
    Input32.Version := 1;
    Input32.DebugCallback := 0;
    Input32.SuppressBackgroundThread := 0;
    Input32.SuppressExternalCodecs := 1;
    Status := ChromeGdiPlusStartup32(ChromeGdiPlusToken, Input32, 0);
  end;
  if (Status <> 0) or (ChromeGdiPlusToken = 0) then
    RaiseException('Не удалось подготовить сглаживание установщика.');
end;

function ChromeARGB(Color: LongWord): LongWord;
begin
  // TColor/COLORREF is 00BBGGRR; GDI+ receives opaque AARRGGBB.
  Result := $FF000000 or ((Color and $FF) shl 16) or
    (Color and $FF00) or ((Color shr 16) and $FF);
end;

function ChromeBeginAA(DC: NativeUInt): NativeUInt;
begin
  Result := 0;
  if (ChromeGdiPlusToken = 0) or (DC = 0) then Exit;
  if ChromeGdipCreateFromHDC(DC, Result) <> 0 then begin Result := 0; Exit; end;
  ChromeGdipSetPageUnit(Result, 2); // UnitPixel: dimensions are already DPI-scaled.
  ChromeGdipSetSmoothingMode(Result, 4); // SmoothingModeAntiAlias
  ChromeGdipSetPixelOffsetMode(Result, 4); // PixelOffsetModeHalf
  ChromeGdipSetCompositingQuality(Result, 2); // CompositingQualityHighQuality
end;

procedure ChromeAARoundRect(Graphics: NativeUInt; Left, Top, Right, Bottom, DiameterX, DiameterY: Single;
  FaceColor, BorderColor: LongWord; StrokeWidth: Single; Fill: Boolean);
var
  Path, Brush, Pen: NativeUInt;
begin
  if (Graphics = 0) or (Right <= Left) or (Bottom <= Top) then Exit;
  if DiameterX > Right - Left then DiameterX := Right - Left;
  if DiameterY > Bottom - Top then DiameterY := Bottom - Top;
  Path := 0;
  Brush := 0;
  Pen := 0;
  if ChromeGdipCreatePath(0, Path) <> 0 then Exit;
  try
    ChromeGdipAddPathArc(Path, Left, Top, DiameterX, DiameterY, 180, 90);
    ChromeGdipAddPathArc(Path, Right - DiameterX, Top, DiameterX, DiameterY, 270, 90);
    ChromeGdipAddPathArc(Path, Right - DiameterX, Bottom - DiameterY, DiameterX, DiameterY, 0, 90);
    ChromeGdipAddPathArc(Path, Left, Bottom - DiameterY, DiameterX, DiameterY, 90, 90);
    ChromeGdipClosePathFigure(Path);
    if Fill and (ChromeGdipCreateSolidFill(ChromeARGB(FaceColor), Brush) = 0) then
      ChromeGdipFillPath(Graphics, Brush, Path);
    if (StrokeWidth > 0) and (ChromeGdipCreatePen1(ChromeARGB(BorderColor), StrokeWidth, 2, Pen) = 0) then
      ChromeGdipDrawPath(Graphics, Pen, Path);
  finally
    if Pen <> 0 then ChromeGdipDeletePen(Pen);
    if Brush <> 0 then ChromeGdipDeleteBrush(Brush);
    ChromeGdipDeletePath(Path);
  end;
end;

procedure ChromeAAEllipse(Graphics: NativeUInt; Left, Top, Width, Height: Single;
  FaceColor, BorderColor: LongWord; StrokeWidth: Single; Fill: Boolean);
var
  Brush, Pen: NativeUInt;
begin
  if (Graphics = 0) or (Width <= 0) or (Height <= 0) then Exit;
  Brush := 0;
  Pen := 0;
  try
    if Fill and (ChromeGdipCreateSolidFill(ChromeARGB(FaceColor), Brush) = 0) then
      ChromeGdipFillEllipse(Graphics, Brush, Left, Top, Width, Height);
    if (StrokeWidth > 0) and (ChromeGdipCreatePen1(ChromeARGB(BorderColor), StrokeWidth, 2, Pen) = 0) then
      ChromeGdipDrawEllipse(Graphics, Pen, Left, Top, Width, Height);
  finally
    if Pen <> 0 then ChromeGdipDeletePen(Pen);
    if Brush <> 0 then ChromeGdipDeleteBrush(Brush);
  end;
end;

procedure ChromeAALine(Graphics: NativeUInt; Color: LongWord; Width, X1, Y1, X2, Y2: Single);
var
  Pen: NativeUInt;
begin
  if Graphics = 0 then Exit;
  Pen := 0;
  if ChromeGdipCreatePen1(ChromeARGB(Color), Width, 2, Pen) <> 0 then Exit;
  try
    ChromeGdipSetPenLineCap(Pen, 2, 2, 2); // Rounded endpoints, including short glyphs.
    ChromeGdipSetPenLineJoin(Pen, 2);
    ChromeGdipDrawLine(Graphics, Pen, X1, Y1, X2, Y2);
  finally
    ChromeGdipDeletePen(Pen);
  end;
end;

function ChromeLongAt(Address: NativeUInt; Offset: Integer): LongWord;
begin
  ChromeReadLong(Result, Address + Offset, 4);
end;

function ChromeNativeAt(Address: NativeUInt; Offset: Integer): NativeUInt;
begin
  ChromeReadNative(Result, Address + Offset, ChromePointerSize);
end;

function ChromeButtonIndex(Window: NativeUInt): Integer;
var
  I: Integer;
begin
  Result := -1;
  for I := 0 to GetArrayLength(ChromeButtons) - 1 do
    if ChromeButtons[I].Handle = Window then begin Result := I; Exit; end;
end;

function ChromeParentIndex(Window: NativeUInt): Integer;
var
  I: Integer;
begin
  Result := -1;
  for I := 0 to GetArrayLength(ChromeParents) - 1 do
    if ChromeParents[I].Handle = Window then begin Result := I; Exit; end;
end;

procedure ChromeApplyRoundedRegion(Window: NativeUInt;
  var CachedWidth, CachedHeight, CachedRadiusX, CachedRadiusY: Integer);
var
  Rectangle: TRect;
  Width, Height, RadiusX, RadiusY: Integer;
  Region: NativeUInt;
begin
  if not ChromeGetClientRect(Window, Rectangle) then Exit;
  Width := Rectangle.Right - Rectangle.Left;
  Height := Rectangle.Bottom - Rectangle.Top;
  RadiusX := ScaleX(16);
  RadiusY := ScaleY(16);
  if (Width <= 0) or (Height <= 0) then Exit;
  if (CachedWidth = Width) and (CachedHeight = Height) and
    (CachedRadiusX = RadiusX) and (CachedRadiusY = RadiusY) then Exit;
  Region := ChromeCreateRoundRectRgn(0, 0, Width + 1, Height + 1, RadiusX, RadiusY);
  if Region = 0 then Exit;
  // Cache before the API call: SetWindowRgn can synchronously send window-
  // position messages. An unchanged shape must not recursively create regions.
  CachedWidth := Width;
  CachedHeight := Height;
  CachedRadiusX := RadiusX;
  CachedRadiusY := RadiusY;
  if ChromeSetWindowRgn(Window, Region, True) = 0 then begin
    // Ownership transfers to Windows only on success, including old-region
    // cleanup. Never delete a region that the window now owns.
    ChromeDeleteObject(Region);
    CachedWidth := 0;
    CachedHeight := 0;
    CachedRadiusX := 0;
    CachedRadiusY := 0;
  end;
end;

procedure ChromeRoundButton(Index: Integer);
begin
  ChromeApplyRoundedRegion(ChromeButtons[Index].Handle,
    ChromeButtons[Index].RegionWidth, ChromeButtons[Index].RegionHeight,
    ChromeButtons[Index].RegionRadiusX, ChromeButtons[Index].RegionRadiusY);
end;

procedure ChromeRoundTasks;
begin
  ChromeApplyRoundedRegion(WizardForm.TasksList.Handle,
    ChromeTasksRegionWidth, ChromeTasksRegionHeight,
    ChromeTasksRegionRadiusX, ChromeTasksRegionRadiusY);
end;

function ChromeBackground: LongWord;
begin
  Result := BrandSurfaceColor;
end;

function ChromeHeaderColor: LongWord;
begin
  if BrandDarkMode then Result := $00172316 else Result := $00DFEEDB;
end;

function ChromeTextColor: LongWord;
begin
  Result := BrandTextColor;
end;

function ChromeGlyphFocusColor: LongWord;
begin
  if BrandDarkMode then Result := $00E5FFF0 else Result := $00405D1B;
end;

function ChromeShowGlyphFocus(Window: NativeUInt; Focused: Boolean): Boolean;
begin
  // Windows hides keyboard-focus cues after mouse input. Respect that state
  // instead of drawing a long focus rectangle around the whole caption.
  Result := Focused and
    ((ChromeSendMessage(Window, ChromeWMQueryUIState, 0, 0) and ChromeUISFHideFocus) = 0);
end;

procedure ChromeRadioGeometryAtSize(ClientHeight, NominalSize: Integer;
  MaximumStroke, Inset: Single; var Diameter, Top: Single);
var
  AvailableDiameter: Single;
begin
  // The original native radio can be shorter than our nominal 18-DIP glyph.
  // Fit both the AA fringe and the thicker keyboard-focus stroke inside its
  // actual client height. Use a centered path, not a downward +0.5 offset.
  if Inset < 1 then Inset := 1;
  Diameter := NominalSize - MaximumStroke;
  AvailableDiameter := ClientHeight - (2 * Inset) - MaximumStroke;
  if Diameter > AvailableDiameter then Diameter := AvailableDiameter;
  if Diameter < 0 then Diameter := 0;
  Top := (ClientHeight - Diameter) / 2.0;
end;

procedure ChromeRadioGeometry(ClientHeight: Integer;
  var Diameter, Top, MaximumStroke: Single);
begin
  MaximumStroke := ScaleX(16) / 10.0;
  ChromeRadioGeometryAtSize(ClientHeight, ScaleY(18), MaximumStroke,
    ScaleY(10) / 10.0, Diameter, Top);
end;

procedure ChromeReadDrawRectangle(DrawItem: NativeUInt; var Rectangle: TRect);
var
  Offset: Integer;
begin
  if ChromePointerSize = 8 then Offset := 40 else Offset := 28;
  Rectangle.Left := ChromeLongAt(DrawItem, Offset);
  Rectangle.Top := ChromeLongAt(DrawItem, Offset + 4);
  Rectangle.Right := ChromeLongAt(DrawItem, Offset + 8);
  Rectangle.Bottom := ChromeLongAt(DrawItem, Offset + 12);
end;

procedure ChromePaintTaskItem(Control: TNewCheckListBox; ItemID: Integer; DC: NativeUInt;
  Rectangle: TRect; Focused: Boolean);
var
  SavedDC, GlyphLeft, GlyphTop, GlyphSize: Integer;
  TextRectangle: TRect;
  BackgroundBrush, Graphics: NativeUInt;
  TextColor, FaceColor, BorderColor, TickColor: LongWord;
  StrokeWidth: Single;
  Checked: Boolean;
begin
  if (ItemID < 0) or (ItemID >= Control.Items.Count) then Exit;
  Checked := Control.Checked[ItemID];
  TextColor := BrandTextColor;
  FaceColor := BrandSurfaceColor;
  BorderColor := BrandMutedColor;
  if Checked then begin FaceColor := BrandAccentColor; BorderColor := BrandAccentColor; end;
  if not Control.ItemEnabled[ItemID] then TextColor := BrandMutedColor;
  StrokeWidth := ScaleX(10) / 10.0;
  if Control.ItemEnabled[ItemID] and ChromeShowGlyphFocus(Control.Handle, Focused) then begin
    // A compact cue on the real checkbox keeps keyboard navigation visible
    // without making the entire option look like a text field.
    BorderColor := ChromeGlyphFocusColor;
    StrokeWidth := ScaleX(16) / 10.0;
  end;
  SavedDC := ChromeSaveDC(DC);
  BackgroundBrush := ChromeCreateBrush(BrandSurfaceColor);
  try
    ChromeFillRect(DC, Rectangle, BackgroundBrush);
    GlyphSize := ScaleY(18);
    GlyphLeft := Rectangle.Left + ScaleX(6);
    GlyphTop := Rectangle.Top + (Rectangle.Bottom - Rectangle.Top - GlyphSize) div 2;
    TextRectangle := Rectangle;
    TextRectangle.Left := GlyphLeft + GlyphSize + ScaleX(12);
    TextRectangle.Right := TextRectangle.Right - ScaleX(6);
    Graphics := ChromeBeginAA(DC);
    try
      ChromeAARoundRect(Graphics, GlyphLeft + 0.5, GlyphTop + 0.5,
        GlyphLeft + GlyphSize - 0.5, GlyphTop + GlyphSize - 0.5,
        ScaleX(7), ScaleY(7), FaceColor, BorderColor, StrokeWidth, True);
      if Checked then begin
        if BrandDarkMode then TickColor := $00172710 else TickColor := $00F2F7EA;
        ChromeAALine(Graphics, TickColor, ScaleX(20) / 10.0,
          GlyphLeft + ScaleX(4), GlyphTop + ScaleY(9), GlyphLeft + ScaleX(8), GlyphTop + ScaleY(13));
        ChromeAALine(Graphics, TickColor, ScaleX(20) / 10.0,
          GlyphLeft + ScaleX(8), GlyphTop + ScaleY(13), GlyphLeft + ScaleX(14), GlyphTop + ScaleY(5));
      end;
    finally
      if Graphics <> 0 then ChromeGdipDeleteGraphics(Graphics);
    end;
    ChromeSelectObject(DC, Control.Font.Handle);
    ChromeSetTextColor(DC, TextColor);
    ChromeSetBkMode(DC, 1);
    // Real checklist owns toggles, Space, focus, and accessibility; paint only.
    ChromeDrawText(DC, Control.ItemCaption[ItemID], -1, TextRectangle, $00100024);
  finally
    ChromeRestoreDC(DC, SavedDC);
    ChromeDeleteObject(BackgroundBrush);
  end;
end;

procedure ChromeDrawTask(DrawItem: NativeUInt);
var
  ItemID: LongWord;
  HandleOffset: Integer;
  DC: NativeUInt;
  Rectangle: TRect;
  Buffer: TChromeBufferedPaint;
begin
  ItemID := ChromeLongAt(DrawItem, 8);
  if ItemID >= WizardForm.TasksList.Items.Count then Exit;
  if ChromePointerSize = 8 then HandleOffset := 24 else HandleOffset := 20;
  DC := ChromeNativeAt(DrawItem, HandleOffset + ChromePointerSize);
  ChromeReadDrawRectangle(DrawItem, Rectangle);
  DC := ChromeBeginBufferedPaint(DC, Rectangle, Buffer);
  try
    ChromePaintTaskItem(WizardForm.TasksList, ItemID, DC, Rectangle,
      WizardForm.TasksList.Focused and ((ChromeLongAt(DrawItem, 16) and ChromeODSFocus) <> 0));
  finally
    ChromeEndBufferedPaint(Buffer);
  end;
end;

procedure ChromeDrawTasks(Control: TNewCheckListBox; DC: NativeUInt);
var
  Rectangle, ItemRectangle: TRect;
  Buffer: TChromeBufferedPaint;
  Brush: NativeUInt;
  ItemID, FocusedItem: Integer;
begin
  if not ChromeGetClientRect(Control.Handle, Rectangle) then Exit;
  DC := ChromeBeginBufferedPaint(DC, Rectangle, Buffer);
  Brush := ChromeCreateBrush(BrandSurfaceColor);
  try
    // Include the empty tail in the same frame as every visible row. Native
    // WM_ERASEBKGND must not clear all captions while only one row repaints.
    ChromeFillRect(DC, Rectangle, Brush);
    FocusedItem := ChromeSendMessage(Control.Handle, $0188, 0, 0); // LB_GETCURSEL
    for ItemID := 0 to Control.Items.Count - 1 do
      if ChromeGetListItemRectangle(Control.Handle, $0198, ItemID, ItemRectangle) <> -1 then
        if (ItemRectangle.Bottom > Rectangle.Top) and (ItemRectangle.Top < Rectangle.Bottom) then
          ChromePaintTaskItem(Control, ItemID, DC, ItemRectangle,
            Control.Focused and (ItemID = FocusedItem));
  finally
    ChromeDeleteObject(Brush);
    ChromeEndBufferedPaint(Buffer);
  end;
end;

procedure ChromeDrawRadio(Control: TNewRadioButton; DC: NativeUInt);
var
  Rectangle, TextRectangle: TRect;
  SavedDC, NominalGlyphSize: Integer;
  GlyphLeft, GlyphTop, GlyphDiameter, MaximumStroke, StrokeWidth, DotDiameter: Single;
  BorderColor: LongWord;
  BackgroundBrush, Graphics: NativeUInt;
  Buffer: TChromeBufferedPaint;
begin
  ChromeGetClientRect(Control.Handle, Rectangle);
  DC := ChromeBeginBufferedPaint(DC, Rectangle, Buffer);
  SavedDC := ChromeSaveDC(DC);
  BackgroundBrush := ChromeCreateBrush(BrandSurfaceColor);
  try
    ChromeFillRect(DC, Rectangle, BackgroundBrush);
    NominalGlyphSize := ScaleY(18);
    ChromeRadioGeometry(Rectangle.Bottom - Rectangle.Top,
      GlyphDiameter, GlyphTop, MaximumStroke);
    GlyphLeft := ScaleX(2) + (NominalGlyphSize - GlyphDiameter) / 2.0;
    BorderColor := BrandMutedColor;
    StrokeWidth := ScaleX(10) / 10.0;
    if Control.Enabled and ChromeShowGlyphFocus(Control.Handle, Control.Focused) then begin
      BorderColor := ChromeGlyphFocusColor;
      StrokeWidth := MaximumStroke;
    end;
    TextRectangle := Rectangle;
    TextRectangle.Left := ScaleX(2) + NominalGlyphSize + ScaleX(10);
    Graphics := ChromeBeginAA(DC);
    try
      ChromeAAEllipse(Graphics, GlyphLeft, GlyphTop, GlyphDiameter, GlyphDiameter,
        BrandSurfaceColor, BorderColor, StrokeWidth, True);
      if Control.Checked and (GlyphDiameter > 0) then begin
        DotDiameter := GlyphDiameter * 0.5;
        ChromeAAEllipse(Graphics, GlyphLeft + (GlyphDiameter - DotDiameter) / 2.0,
          GlyphTop + (GlyphDiameter - DotDiameter) / 2.0, DotDiameter, DotDiameter,
          BrandAccentColor, BrandAccentColor, 0, True);
      end;
    finally
      if Graphics <> 0 then ChromeGdipDeleteGraphics(Graphics);
    end;
    ChromeSelectObject(DC, Control.Font.Handle);
    if Control.Enabled then ChromeSetTextColor(DC, BrandTextColor)
    else ChromeSetTextColor(DC, BrandMutedColor);
    ChromeSetBkMode(DC, 1);
    ChromeDrawText(DC, Control.Caption, -1, TextRectangle, $00100024);
  finally
    ChromeRestoreDC(DC, SavedDC);
    ChromeDeleteObject(BackgroundBrush);
    ChromeEndBufferedPaint(Buffer);
  end;
end;

function ChromeRadioProc(Window: NativeUInt; Message: LongWord; WParam: NativeUInt; LParam: NativeInt;
  SubclassID, ReferenceData: NativeUInt): NativeInt;
var
  Index: Integer;
  PaintBuffer: TChromePaintBuffer;
  DC: NativeUInt;
begin
  if Message = ChromeWMNCDestroy then
    ChromeRemoveWindowSubclass(Window, ChromeRadioCallback, SubclassID);
  Index := ReferenceData - 1;
  if (Index >= 0) and (Index < GetArrayLength(ChromeRadioControls)) then begin
    if Message = ChromeWMEraseBkgnd then begin Result := 1; Exit; end;
    if Message = ChromeWMPrintClient then begin
      if WParam <> 0 then ChromeDrawRadio(ChromeRadioControls[Index], WParam);
      Result := 0;
      Exit;
    end;
    if Message = ChromeWMPaint then begin
      DC := ChromeBeginPaint(Window, PaintBuffer);
      try
        if DC <> 0 then ChromeDrawRadio(ChromeRadioControls[Index], DC);
      finally
        ChromeEndPaint(Window, PaintBuffer);
      end;
      Result := 0;
      Exit;
    end;
    if (Message = ChromeWMSetFocus) or (Message = ChromeWMKillFocus) then
      ChromeInvalidateRect(Window, 0, False);
  end;
  // Keep BS_AUTORADIOBUTTON: Windows, not a fake widget, changes checked state.
  Result := ChromeDefSubclassProc(Window, Message, WParam, LParam);
  // Native controls may invalidate just their smaller stock glyph. Refresh
  // our full, completed frame after native state/UI-cue changes as well.
  if (Message = $00F1) or (Message = ChromeWMEnable) or
    (Message = ChromeWMUpdateUIState) then ChromeInvalidateRect(Window, 0, False);
end;

function ChromeTasksProc(Window: NativeUInt; Message: LongWord; WParam: NativeUInt; LParam: NativeInt;
  SubclassID, ReferenceData: NativeUInt): NativeInt;
var
  PaintBuffer: TChromePaintBuffer;
  DC: NativeUInt;
begin
  if Message = ChromeWMNCDestroy then
    ChromeRemoveWindowSubclass(Window, ChromeTasksCallback, SubclassID);
  if Message = ChromeWMEraseBkgnd then begin Result := 1; Exit; end;
  if Message = ChromeWMPrintClient then begin
    if WParam <> 0 then ChromeDrawTasks(WizardForm.TasksList, WParam);
    Result := 0;
    Exit;
  end;
  if Message = ChromeWMPaint then begin
    DC := ChromeBeginPaint(Window, PaintBuffer);
    try
      if DC <> 0 then ChromeDrawTasks(WizardForm.TasksList, DC);
    finally
      ChromeEndPaint(Window, PaintBuffer);
    end;
    Result := 0;
    Exit;
  end;
  Result := ChromeDefSubclassProc(Window, Message, WParam, LParam);
  if Message = ChromeWMSize then ChromeRoundTasks;
  // Native checklist code invalidates only its original, smaller check glyph.
  // Our larger themed glyph must repaint in full after actual native toggles.
  if (Message = $0201) or (Message = $0202) or (Message = $0100) or
    (Message = $0101) or (Message = ChromeWMSetFocus) or (Message = ChromeWMKillFocus) then
    ChromeInvalidateRect(Window, 0, False);
  if Message = ChromeWMUpdateUIState then ChromeInvalidateRect(Window, 0, False);
end;

procedure ChromePaintButton(Index: Integer; DC: NativeUInt; Rectangle: TRect; State: LongWord);
var
  BackgroundBrush, Graphics: NativeUInt;
  SavedDC: Integer;
  TextRectangle: TRect;
  FaceColor, BorderColor, TextColor, BackgroundColor: LongWord;
  CenterX, CenterY, GlyphHalfX, GlyphHalfY: Single;
  IsPrimary, Disabled, Focused: Boolean;
  Tick: LongWord;
  Buffer: TChromeBufferedPaint;
begin
  IsPrimary := False;
  if WizardForm <> nil then IsPrimary := ChromeButtons[Index].Control = WizardForm.NextButton;
  Disabled := (State and ChromeODSDisabled) <> 0;
  // ODS_SELECTED comes from Windows' actual mouse/keyboard/capture state,
  // including Space and the default button. We never emulate native input.
  ChromeSetButtonPressed(Index, (State and ChromeODSSelected) <> 0);
  Tick := ChromeGetTickCount;
  ChromeAdvanceTween(ChromeButtons[Index].Hover, Tick);
  ChromeAdvanceTween(ChromeButtons[Index].Press, Tick);
  Focused := (State and ChromeODSFocus) <> 0;
  TextColor := ChromeTextColor;
  BackgroundColor := ChromeBackground;
  if ChromeButtons[Index].Control.Parent = ChromeHeader then BackgroundColor := ChromeHeaderColor;
  if BrandDarkMode then begin
    FaceColor := $002A3725;
    BorderColor := $005B7552;
    if IsPrimary then begin
      FaceColor := $00BFFF73;
      BorderColor := $00BFFF73;
      TextColor := $00172710;
    end;
    if Disabled then begin FaceColor := $00232E20; BorderColor := $0035452D; TextColor := $00697E61; end;
  end else begin
    FaceColor := $00E0EDDC;
    BorderColor := $009DB995;
    if IsPrimary then begin
      FaceColor := BrandAccentColor;
      BorderColor := BrandAccentColor;
      TextColor := $00F2F7EA;
    end;
    if Disabled then begin FaceColor := $00DDE8D9; BorderColor := $00B9CBB3; TextColor := $008CA382; end;
  end;
  if not Disabled then begin
    // App.xaml uses a 12% accent overlay and 130/150 ms hover transitions.
    // Primary faces are already accent-colored: use the same subtle tint
    // strength with a light highlight, keeping light-theme captions readable.
    if IsPrimary then FaceColor := ChromeBlendColor(FaceColor, $00FFFFFF, 0.12 * ChromeButtons[Index].Hover.Value)
    else FaceColor := ChromeBlendColor(FaceColor, BrandAccentColor, 0.12 * ChromeButtons[Index].Hover.Value);
    BorderColor := ChromeBlendColor(BorderColor, BrandAccentColor, ChromeButtons[Index].Hover.Value);
    // Match App.xaml: keyboard focus recolors only the existing outer border.
    // Mouse focus keeps its normal hover/press appearance, never an inner ring.
    if ChromeShowGlyphFocus(ChromeButtons[Index].Handle, Focused) then
      BorderColor := BrandAccentColor;
    FaceColor := ChromeBlendColor(FaceColor, BackgroundColor, 0.28 * ChromeButtons[Index].Press.Value);
    BorderColor := ChromeBlendColor(BorderColor, BackgroundColor, 0.28 * ChromeButtons[Index].Press.Value);
    TextColor := ChromeBlendColor(TextColor, BackgroundColor, 0.28 * ChromeButtons[Index].Press.Value);
  end;
  DC := ChromeBeginBufferedPaint(DC, Rectangle, Buffer);
  SavedDC := ChromeSaveDC(DC);
  BackgroundBrush := ChromeCreateBrush(BackgroundColor);
  try
    ChromeFillRect(DC, Rectangle, BackgroundBrush);
    Graphics := ChromeBeginAA(DC);
    try
      // Leave two physical pixels around the anti-aliased path. Its coverage
      // must not be cut by the HWND region's inherently binary clipping edge.
      ChromeAARoundRect(Graphics, Rectangle.Left + 2, Rectangle.Top + 2,
        Rectangle.Right - 2, Rectangle.Bottom - 2, ScaleX(16), ScaleY(16),
        FaceColor, BorderColor, ScaleX(10) / 10.0, True);
      // These are geometry, not font characters: the visual center is exactly
      // the button center in light/dark themes and at every supported DPI.
      CenterX := (Rectangle.Left + Rectangle.Right) / 2.0;
      CenterY := (Rectangle.Top + Rectangle.Bottom) / 2.0;
      if ChromeButtons[Index].Control = ChromeCloseButton then begin
        GlyphHalfX := ScaleX(4);
        GlyphHalfY := ScaleY(4);
        ChromeAALine(Graphics, TextColor, ScaleX(16) / 10.0,
          CenterX - GlyphHalfX, CenterY - GlyphHalfY, CenterX + GlyphHalfX, CenterY + GlyphHalfY);
        ChromeAALine(Graphics, TextColor, ScaleX(16) / 10.0,
          CenterX - GlyphHalfX, CenterY + GlyphHalfY, CenterX + GlyphHalfX, CenterY - GlyphHalfY);
      end else if ChromeButtons[Index].Control = ChromeMinimizeButton then
        ChromeAALine(Graphics, TextColor, ScaleX(16) / 10.0,
          CenterX - ScaleX(5), CenterY, CenterX + ScaleX(5), CenterY);
    finally
      if Graphics <> 0 then ChromeGdipDeleteGraphics(Graphics);
    end;
    if (ChromeButtons[Index].Control <> ChromeCloseButton) and
      (ChromeButtons[Index].Control <> ChromeMinimizeButton) then begin
      ChromeSelectObject(DC, ChromeButtons[Index].Control.Font.Handle);
      ChromeSetTextColor(DC, TextColor);
      ChromeSetBkMode(DC, 1);
      TextRectangle := Rectangle;
      TextRectangle.Left := TextRectangle.Left + ScaleX(6);
      TextRectangle.Right := TextRectangle.Right - ScaleX(6);
      // DT_HIDEPREFIX hides stock underlines without removing native accelerators.
      ChromeDrawText(DC, ChromeButtons[Index].Control.Caption, -1, TextRectangle, $00100025);
    end;
  finally
    ChromeRestoreDC(DC, SavedDC);
    ChromeDeleteObject(BackgroundBrush);
    ChromeEndBufferedPaint(Buffer);
  end;
end;

function ChromeCurrentButtonState(Window: NativeUInt): LongWord;
var
  NativeState: NativeInt;
begin
  // Query Windows' real capture/keyboard state rather than synthesizing input.
  NativeState := ChromeSendMessage(Window, ChromeBMGetState, 0, 0);
  Result := 0;
  if (NativeState and $0004) <> 0 then Result := Result or ChromeODSSelected;
  if (NativeState and $0008) <> 0 then Result := Result or ChromeODSFocus;
  if not ChromeIsWindowEnabled(Window) then Result := Result or ChromeODSDisabled;
end;

procedure ChromeDrawButton(Index: Integer; DrawItem: NativeUInt);
var
  HandleOffset: Integer;
  DC: NativeUInt;
  Rectangle: TRect;
begin
  if ChromePointerSize = 8 then HandleOffset := 24 else HandleOffset := 20;
  DC := ChromeNativeAt(DrawItem, HandleOffset + ChromePointerSize);
  ChromeReadDrawRectangle(DrawItem, Rectangle);
  ChromePaintButton(Index, DC, Rectangle, ChromeLongAt(DrawItem, 16));
end;

function ChromeParentProc(Window: NativeUInt; Message: LongWord; WParam: NativeUInt; LParam: NativeInt;
  SubclassID, ReferenceData: NativeUInt): NativeInt;
var
  ParentIndex, ButtonIndex, HandleOffset: Integer;
begin
  ParentIndex := ChromeParentIndex(Window);
  if Message = ChromeWMNCDestroy then
    ChromeRemoveWindowSubclass(Window, ChromeParentCallback, SubclassID);
  if ParentIndex < 0 then begin Result := ChromeDefSubclassProc(Window, Message, WParam, LParam); Exit; end;
  if Message = ChromeWMSettingChange then ChromeRefreshAnimationPreference;
  if (Message = ChromeWMDrawItem) and (LParam <> 0) then begin
    if ChromeLongAt(LParam, 0) = ChromeODTListBox then begin
      if ChromePointerSize = 8 then HandleOffset := 24 else HandleOffset := 20;
      if ChromeNativeAt(LParam, HandleOffset) = WizardForm.TasksList.Handle then begin
        ChromeDrawTask(LParam);
        Result := 1;
        Exit;
      end;
    end;
    if ChromeLongAt(LParam, 0) = ChromeODTButton then begin
      if ChromePointerSize = 8 then HandleOffset := 24 else HandleOffset := 20;
      ButtonIndex := ChromeButtonIndex(ChromeNativeAt(LParam, HandleOffset));
      if ButtonIndex >= 0 then begin
        ChromeDrawButton(ButtonIndex, LParam);
        Result := 1;
        Exit;
      end;
    end;
  end;
  Result := ChromeDefSubclassProc(Window, Message, WParam, LParam);
end;

function ChromeButtonProc(Window: NativeUInt; Message: LongWord; WParam: NativeUInt; LParam: NativeInt;
  SubclassID, ReferenceData: NativeUInt): NativeInt;
var
  Index: Integer;
  Track: TChromeTrackMouse;
  HoverRunning, PressRunning: Boolean;
  Tick: LongWord;
  PaintBuffer: TChromePaintBuffer;
  DC: NativeUInt;
  Rectangle: TRect;
begin
  Index := ChromeButtonIndex(Window);
  if Message = ChromeWMNCDestroy then begin
    if Index >= 0 then begin
      ChromeStopButtonTimer(Index);
      // A click handler may close the wizard while DefSubclassProc runs.
      // Mark dead HWNDs before forwarding final destruction notifications.
      ChromeButtons[Index].Handle := 0;
    end;
    ChromeRemoveWindowSubclass(Window, ChromeButtonCallback, SubclassID);
    Result := ChromeDefSubclassProc(Window, Message, WParam, LParam);
    Exit;
  end;
  if Index < 0 then begin Result := ChromeDefSubclassProc(Window, Message, WParam, LParam); Exit; end;
  // We paint the complete background inside the same offscreen frame as its
  // caption. A separate native erase would expose a blank intermediate frame.
  if Message = ChromeWMEraseBkgnd then begin Result := 1; Exit; end;
  // Some native/theme paint routes do not send parent WM_DRAWITEM. Own both
  // normal client paint and composited/print-client paint so none can expose
  // a stock blank button between our completed frames.
  if Message = ChromeWMPaint then begin
    DC := ChromeBeginPaint(Window, PaintBuffer);
    try
      if (DC <> 0) and ChromeGetClientRect(Window, Rectangle) then
        ChromePaintButton(Index, DC, Rectangle, ChromeCurrentButtonState(Window));
    finally
      ChromeEndPaint(Window, PaintBuffer);
    end;
    Result := 0;
    Exit;
  end;
  if Message = ChromeWMPrintClient then begin
    if (WParam <> 0) and ChromeGetClientRect(Window, Rectangle) then
      ChromePaintButton(Index, WParam, Rectangle, ChromeCurrentButtonState(Window));
    Result := 0;
    Exit;
  end;
  if (Message = ChromeWMTimer) and (WParam = ChromeAnimationTimerID) then begin
    // KillTimer does not remove a WM_TIMER already queued for this HWND.
    if ChromeButtons[Index].AnimationTimerActive then begin
      Tick := ChromeGetTickCount;
      HoverRunning := ChromeAdvanceTween(ChromeButtons[Index].Hover, Tick);
      PressRunning := ChromeAdvanceTween(ChromeButtons[Index].Press, Tick);
      if not HoverRunning and not PressRunning then ChromeStopButtonTimer(Index);
      ChromeInvalidateRect(Window, 0, False);
    end;
    Result := 0;
    Exit;
  end;
  if Message = ChromeBMSetStyle then
    WParam := (WParam and not $000F) or ChromeBSOwnerDraw;
  if Message = ChromeWMMouseMove then begin
    if not ChromeButtons[Index].Hovered then begin
      ChromeSetButtonHovered(Index, True);
      if ChromePointerSize = 8 then Track.StructSize := 24 else Track.StructSize := 16;
      Track.Flags := 2; // TME_LEAVE
      Track.Window := Window;
      Track.HoverTime := 0;
      Track.TailPadding := 0;
      ChromeTrackMouseEvent(Track);
    end;
  end;
  if Message = ChromeWMMouseLeave then begin
    ChromeSetButtonHovered(Index, False);
  end;
  if (Message = ChromeWMSetFocus) or (Message = ChromeWMKillFocus) or
    (Message = ChromeWMUpdateUIState) then
    ChromeInvalidateRect(Window, 0, False);
  Result := ChromeDefSubclassProc(Window, Message, WParam, LParam);
  if ChromeButtons[Index].Handle <> Window then Exit;
  // Native mouse/keyboard processing runs first; query its real pressed state
  // after capture changes too. No synthetic click, focus, or hit-test changes.
  if (Message = ChromeWMLButtonDown) or (Message = $0202) or
    (Message = ChromeWMMouseMove) or (Message = $0100) or (Message = $0101) or
    (Message = $0215) or (Message = $001F) or (Message = $00F3) then
    ChromeSetButtonPressed(Index, (ChromeSendMessage(Window, ChromeBMGetState, 0, 0) and $0004) <> 0);
  if (Message = ChromeWMEnable) and (WParam = 0) then begin
    ChromeButtons[Index].Hovered := False;
    ChromeButtons[Index].Hover.Value := 0;
    ChromeButtons[Index].Hover.TargetValue := 0;
    ChromeButtons[Index].Hover.Duration := 0;
    ChromeButtons[Index].Press.Value := 0;
    ChromeButtons[Index].Press.TargetValue := 0;
    ChromeButtons[Index].Press.Duration := 0;
    ChromeStopButtonTimer(Index);
    ChromeInvalidateRect(Window, 0, False);
  end;
  if (Message = ChromeWMEnable) and (WizardForm <> nil) then
    if ChromeButtons[Index].Control = WizardForm.CancelButton then
      ChromeCloseButton.Enabled := (WParam <> 0) or (WizardForm.CurPageID = wpFinished);
  if Message = ChromeWMSize then ChromeRoundButton(Index);
end;

procedure ChromeRegisterParent(Window: NativeUInt);
var
  Index: Integer;
begin
  if ChromeParentIndex(Window) >= 0 then Exit;
  Index := GetArrayLength(ChromeParents);
  SetArrayLength(ChromeParents, Index + 1);
  ChromeParents[Index].Handle := Window;
  if not ChromeSetWindowSubclass(Window, ChromeParentCallback, $4D434850, Index + 1) then
    RaiseException('Could not initialize installer panel styling.');
end;

procedure ChromeRegisterButton(Button: TNewButton);
var
  Index: Integer;
begin
  Button.StyleElements := [];
  Index := GetArrayLength(ChromeButtons);
  SetArrayLength(ChromeButtons, Index + 1);
  ChromeButtons[Index].Control := Button;
  ChromeButtons[Index].Handle := Button.Handle;
  ChromeButtons[Index].Hovered := False;
  ChromeButtons[Index].AnimationTimerActive := False;
  ChromeRegisterParent(Button.Parent.Handle);
  if not ChromeSetWindowSubclass(Button.Handle, ChromeButtonCallback, $4D434842, Index + 1) then
    RaiseException('Could not initialize installer button styling.');
  ChromeSendMessage(Button.Handle, ChromeBMSetStyle, ChromeBSOwnerDraw, 1);
  // Physically clip native HWND corners so the parent's gradient remains
  // visible there; a rectangular solid fill must not surround the round face.
  ChromeRoundButton(Index);
end;

procedure ChromeStartDrag;
begin
  ChromeReleaseCapture;
  ChromeSendMessage(WizardForm.Handle, ChromeWMNCLButtonDown, 2, 0); // HTCAPTION
end;

procedure ChromeDrawHeaderTitle(Control: TNewStaticText; DC: NativeUInt);
var
  Rectangle: TRect;
  Buffer: TChromeBufferedPaint;
  BackgroundBrush: NativeUInt;
  SavedDC: Integer;
begin
  if not ChromeGetClientRect(Control.Handle, Rectangle) then Exit;
  DC := ChromeBeginBufferedPaint(DC, Rectangle, Buffer);
  SavedDC := ChromeSaveDC(DC);
  BackgroundBrush := ChromeCreateBrush(ChromeHeaderColor);
  try
    // TNewStaticText's Delphi Transparent flag defaults to True and is not
    // exposed in Pascal Script. Own both paint routes rather than asking its
    // style hook to reconstruct an uncertain parent background.
    ChromeFillRect(DC, Rectangle, BackgroundBrush);
    ChromeSelectObject(DC, Control.Font.Handle);
    ChromeSetTextColor(DC, Control.Font.Color);
    ChromeSetBkMode(DC, 1);
    // Keep the native static text's left/top alignment and caption semantics.
    ChromeDrawText(DC, Control.Caption, -1, Rectangle, $00000820);
  finally
    ChromeRestoreDC(DC, SavedDC);
    ChromeDeleteObject(BackgroundBrush);
    ChromeEndBufferedPaint(Buffer);
  end;
end;

function ChromeHeaderProc(Window: NativeUInt; Message: LongWord; WParam: NativeUInt; LParam: NativeInt;
  SubclassID, ReferenceData: NativeUInt): NativeInt;
var
  PaintBuffer: TChromePaintBuffer;
  DC: NativeUInt;
begin
  if Message = ChromeWMNCDestroy then begin
    ChromeRemoveWindowSubclass(Window, ChromeHeaderCallback, SubclassID);
    Result := ChromeDefSubclassProc(Window, Message, WParam, LParam);
    Exit;
  end;
  if ReferenceData = 1 then begin
    if Message = ChromeWMEraseBkgnd then begin Result := 1; Exit; end;
    if Message = ChromeWMPaint then begin
      DC := ChromeBeginPaint(Window, PaintBuffer);
      try
        if DC <> 0 then ChromeDrawHeaderTitle(ChromeHeaderTitle, DC);
      finally
        ChromeEndPaint(Window, PaintBuffer);
      end;
      Result := 0;
      Exit;
    end;
    if Message = ChromeWMPrintClient then begin
      if WParam <> 0 then ChromeDrawHeaderTitle(ChromeHeaderTitle, WParam);
      Result := 0;
      Exit;
    end;
  end;
  if Message = ChromeWMLButtonDown then begin ChromeStartDrag; Result := 0; Exit; end;
  Result := ChromeDefSubclassProc(Window, Message, WParam, LParam);
end;

procedure ChromeCloseClick(Sender: TObject);
begin
  // Native Cancel handler retains Inno's confirmation and installation-safety rules.
  if WizardForm.CurPageID = wpFinished then WizardForm.Close
  else if WizardForm.CancelButton.Enabled then
    ChromeSendMessage(WizardForm.CancelButton.Handle, ChromeBMClick, 0, 0);
end;

procedure ChromeMinimizeClick(Sender: TObject);
begin
  ChromeShowWindow(WizardForm.Handle, 6); // SW_MINIMIZE
end;

procedure ChromeRoundWindow;
var
  Region: NativeUInt;
begin
  Region := ChromeCreateRoundRectRgn(0, 0, WizardForm.Width + 1, WizardForm.Height + 1, ScaleX(24), ScaleY(24));
  if Region <> 0 then begin
    // Windows owns a successfully assigned region; only free it on failure.
    if ChromeSetWindowRgn(WizardForm.Handle, Region, True) = 0 then ChromeDeleteObject(Region);
  end;
end;

procedure RefreshMorphocyteChrome;
var
  I: Integer;
begin
  if not ChromeInitialized then Exit;
  ChromeRefreshAnimationPreference;
  ChromeCloseButton.Enabled := WizardForm.CancelButton.Enabled or (WizardForm.CurPageID = wpFinished);
  ChromeRoundWindow;
  for I := 0 to GetArrayLength(ChromeButtons) - 1 do ChromeRoundButton(I);
  ChromeRoundTasks;
end;

procedure InitializeMorphocyteChrome;
var
  I, HeaderHeight, SavedWidth, SavedHeight: Integer;
  OriginalBounds: array of TChromeControlBounds;
begin
  if not BrandColorsEnabled or ChromeInitialized then Exit;
  ChromeAnimationsEnabled := True;
  ChromeRefreshAnimationPreference;
  ChromeInitializeAA;
  ChromeInitialized := True;
  SavedWidth := WizardForm.ClientWidth;
  SavedHeight := WizardForm.ClientHeight;
  HeaderHeight := ScaleY(46);
  // Do this before HWND subclassing: changing BorderStyle can recreate the form.
  WizardForm.BorderStyle := bsNone;
  WizardForm.ClientWidth := SavedWidth;
  WizardForm.ClientHeight := SavedHeight;
  SetArrayLength(OriginalBounds, WizardForm.ControlCount);
  for I := 0 to WizardForm.ControlCount - 1 do begin
    OriginalBounds[I].Left := WizardForm.Controls[I].Left;
    OriginalBounds[I].Top := WizardForm.Controls[I].Top;
    OriginalBounds[I].Width := WizardForm.Controls[I].Width;
    OriginalBounds[I].Height := WizardForm.Controls[I].Height;
  end;
  WizardForm.ClientHeight := SavedHeight + HeaderHeight;
  // Restore explicit original bounds after resizing; bottom anchors must not
  // shift buttons twice, and stretching notebooks must not gain a hidden tail.
  for I := 0 to WizardForm.ControlCount - 1 do
    WizardForm.Controls[I].SetBounds(OriginalBounds[I].Left, OriginalBounds[I].Top + HeaderHeight,
      OriginalBounds[I].Width, OriginalBounds[I].Height);
  ChromeHeader := TPanel.Create(WizardForm);
  ChromeHeader.Parent := WizardForm;
  ChromeHeader.SetBounds(0, 0, WizardForm.ClientWidth, HeaderHeight);
  ChromeHeader.Anchors := [akLeft, akTop, akRight];
  ChromeHeader.Caption := '';
  ChromeHeader.BevelOuter := bvNone;
  ChromeHeader.BevelInner := bvNone;
  ChromeHeader.StyleElements := [];
  ChromeHeader.Color := ChromeHeaderColor;
  ChromeHeaderTitle := TNewStaticText.Create(WizardForm);
  ChromeHeaderTitle.Parent := ChromeHeader;
  ChromeHeaderTitle.SetBounds(ScaleX(22), ScaleY(14), WizardForm.ClientWidth - ScaleX(134), ScaleY(22));
  ChromeHeaderTitle.AutoSize := False;
  ChromeHeaderTitle.Caption := 'MorphocyteOS · Установка';
  ChromeHeaderTitle.Font.Name := 'Segoe UI';
  ChromeHeaderTitle.Font.Size := 10;
  ChromeHeaderTitle.Font.Style := [fsBold];
  ChromeHeaderTitle.StyleElements := [];
  ChromeHeaderTitle.Color := ChromeHeaderColor;
  ChromeHeaderTitle.Font.Color := ChromeTextColor;
  ChromeCloseButton := TNewButton.Create(WizardForm);
  ChromeCloseButton.Parent := ChromeHeader;
  ChromeCloseButton.SetBounds(WizardForm.ClientWidth - ScaleX(54), ScaleY(7), ScaleX(36), ScaleY(32));
  ChromeCloseButton.Anchors := [akTop, akRight];
  ChromeCloseButton.Caption := '×';
  ChromeCloseButton.Font.Size := 15;
  ChromeCloseButton.Hint := 'Закрыть';
  ChromeCloseButton.ShowHint := False;
  ChromeCloseButton.OnClick := @ChromeCloseClick;
  ChromeMinimizeButton := TNewButton.Create(WizardForm);
  ChromeMinimizeButton.Parent := ChromeHeader;
  ChromeMinimizeButton.SetBounds(WizardForm.ClientWidth - ScaleX(96), ScaleY(7), ScaleX(36), ScaleY(32));
  ChromeMinimizeButton.Anchors := [akTop, akRight];
  ChromeMinimizeButton.Caption := '−';
  ChromeMinimizeButton.Font.Size := 15;
  ChromeMinimizeButton.Hint := 'Свернуть';
  ChromeMinimizeButton.ShowHint := False;
  ChromeMinimizeButton.OnClick := @ChromeMinimizeClick;
  ChromeButtonCallback := CreateCallback(@ChromeButtonProc);
  ChromeParentCallback := CreateCallback(@ChromeParentProc);
  ChromeHeaderCallback := CreateCallback(@ChromeHeaderProc);
  ChromeRadioCallback := CreateCallback(@ChromeRadioProc);
  ChromeTasksCallback := CreateCallback(@ChromeTasksProc);
  if not ChromeSetWindowSubclass(ChromeHeader.Handle, ChromeHeaderCallback, $4D434848, 0) then
    RaiseException('Could not initialize installer header styling.');
  if not ChromeSetWindowSubclass(ChromeHeaderTitle.Handle, ChromeHeaderCallback, $4D434849, 1) then
    RaiseException('Could not initialize installer title dragging.');
  ChromeRegisterButton(WizardForm.NextButton);
  ChromeRegisterButton(WizardForm.BackButton);
  ChromeRegisterButton(WizardForm.CancelButton);
  ChromeRegisterButton(WizardForm.DirBrowseButton);
  ChromeRegisterButton(WizardForm.GroupBrowseButton);
  ChromeRegisterButton(ChromeCloseButton);
  ChromeRegisterButton(ChromeMinimizeButton);
  ChromeRegisterParent(WizardForm.TasksList.Parent.Handle);
  if not ChromeSetWindowSubclass(WizardForm.TasksList.Handle, ChromeTasksCallback, $4D434854, 0) then
    RaiseException('Could not initialize installer task styling.');
  ChromeRoundTasks;
  SetArrayLength(ChromeRadioControls, 2);
  ChromeRadioControls[0] := WizardForm.LicenseAcceptedRadio;
  ChromeRadioControls[1] := WizardForm.LicenseNotAcceptedRadio;
  for I := 0 to GetArrayLength(ChromeRadioControls) - 1 do begin
    ChromeRadioControls[I].StyleElements := [];
    if not ChromeSetWindowSubclass(ChromeRadioControls[I].Handle, ChromeRadioCallback, $4D434852, I + 1) then
      RaiseException('Could not initialize installer radio-button styling.');
  end;
  ChromeRoundWindow;
end;

<event('DeinitializeSetup')>
procedure ShutdownMorphocyteAA;
var
  Token: NativeUInt;
  I: Integer;
begin
  for I := 0 to GetArrayLength(ChromeButtons) - 1 do
    if ChromeButtons[I].Handle <> 0 then ChromeStopButtonTimer(I);
  // Every graphics/path/brush/pen is local to one synchronous paint callback
  // and has already been freed before setup's final deinitialization event.
  Token := ChromeGdiPlusToken;
  ChromeGdiPlusToken := 0;
  if Token <> 0 then ChromeGdiPlusShutdown(Token);
end;
