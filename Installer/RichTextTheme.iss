// The license viewer is RichEdit: Font.Color alone does not recolor loaded runs.
// CHARFORMATW has no pointers and is 92 bytes on both native architectures.
type
  TBrandCharFormat = record
    StructSize, Mask, Effects: LongWord;
    Height, Offset: Integer;
    TextColor: LongWord;
    Charset, PitchAndFamily: Byte;
    FaceName: array[0..31] of Word;
    TailPadding: Word;
  end;

function BrandSetCharacterFormat(Window: NativeUInt; Message: LongWord; Scope: NativeUInt;
  var Format: TBrandCharFormat): NativeInt;
  external 'SendMessageW@user32.dll stdcall';

function BrandGetDeviceContext(Window: NativeUInt): NativeUInt;
  external 'GetDC@user32.dll stdcall';
function BrandReleaseDeviceContext(Window, DC: NativeUInt): Integer;
  external 'ReleaseDC@user32.dll stdcall';

procedure StyleBrandReadyMemo;
var
  DC: NativeUInt;
  SavedDC, LineHeight, LineCount: Integer;
  NeedsScrollBar: Boolean;
  TextRectangle, ClientRectangle: TRect;
begin
  if not BrandColorsEnabled then Exit;
  // Keep Inno's native, selectable summary. No alternate copy of its text.
  WizardForm.ReadyMemo.BorderStyle := bsNone;
  WizardForm.ReadyMemo.WordWrap := True;
  WizardForm.ReadyMemo.ScrollBars := ssNone;
  StyleBrandMemo(WizardForm.ReadyMemo);
  // Only show a vertical scrollbar if the actual wrapped native text needs it.
  // Normal short summaries do not need stock scrollbars or a horizontal rail.
  DC := BrandGetDeviceContext(WizardForm.ReadyMemo.Handle);
  if DC = 0 then begin
    WizardForm.ReadyMemo.ScrollBars := ssVertical;
    StyleBrandMemo(WizardForm.ReadyMemo);
    Exit;
  end;
  SavedDC := ChromeSaveDC(DC);
  NeedsScrollBar := True;
  try
    ChromeSelectObject(DC, WizardForm.ReadyMemo.Font.Handle);
    TextRectangle.Left := 0;
    TextRectangle.Top := 0;
    TextRectangle.Right := WizardForm.ReadyMemo.ClientWidth;
    TextRectangle.Bottom := 0;
    LineHeight := ChromeDrawText(DC, 'Hg', -1, TextRectangle, $00000420);
    LineCount := ChromeSendMessage(WizardForm.ReadyMemo.Handle, $00BA, 0, 0); // EM_GETLINECOUNT
    if ChromeGetClientRect(WizardForm.ReadyMemo.Handle, ClientRectangle) then
      NeedsScrollBar := (LineHeight <= 0) or
        (LineCount * LineHeight + ScaleY(8) > ClientRectangle.Bottom - ClientRectangle.Top);
  finally
    ChromeRestoreDC(DC, SavedDC);
    BrandReleaseDeviceContext(WizardForm.ReadyMemo.Handle, DC);
  end;
  if NeedsScrollBar then WizardForm.ReadyMemo.ScrollBars := ssVertical;
  // ScrollBars can recreate the HWND: restore its theme after the final handle.
  StyleBrandMemo(WizardForm.ReadyMemo);
end;

procedure StyleBrandRichMemo(MemoControl: TRichEditViewer);
var
  Format: TBrandCharFormat;
  I: Integer;
begin
  if not BrandColorsEnabled then Exit;
  StyleBrandMemo(MemoControl);
  if not MemoControl.UseRichEdit then Exit;
  Format.StructSize := SizeOf(Format);
  Format.Mask := $40000000; // CFM_COLOR only: preserve links and all other formatting.
  Format.Effects := 0; // Clear CFE_AUTOCOLOR, which otherwise selects system black.
  Format.Height := 0;
  Format.Offset := 0;
  Format.TextColor := BrandTextColor;
  Format.Charset := 0;
  Format.PitchAndFamily := 0;
  for I := 0 to 31 do Format.FaceName[I] := 0;
  Format.TailPadding := 0;
  // EM_SETCHARFORMAT: apply to both defaults and the existing read-only text.
  BrandSetCharacterFormat(MemoControl.Handle, $0444, 0, Format);
  if BrandSetCharacterFormat(MemoControl.Handle, $0444, 4, Format) = 0 then
    RaiseException('Не удалось применить читаемый цвет лицензионного соглашения.');
end;
