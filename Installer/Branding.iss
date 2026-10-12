// Presentation only. Real native controls, focus order and installation checks stay intact.
var
  BrandColorsEnabled: Boolean;
  BrandDarkMode: Boolean;
  BrandTextColor: TColor;
  BrandAccentColor: TColor;
  BrandMutedColor: TColor;
  BrandSurfaceColor: TColor;
  BrandNavigationGap: Integer;

function ShouldUseBrandColors: Boolean;
var
  I: Integer;
begin
  Result := not HighContrastActive;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/NOSTYLE') = 0 then Result := False;
end;

procedure ApplyReadableBackground;
begin
  // 'window' is the base color, so clearing the artwork restores system contrast.
  if not ShouldUseBrandColors then WizardSetBackImage([], True, True, 255);
end;

procedure StyleBrandLabel(LabelControl: TNewStaticText; TextColor: TColor);
begin
  LabelControl.StyleElements := LabelControl.StyleElements - [seFont];
  LabelControl.Font.Color := TextColor;
end;

procedure StyleBrandMemo(MemoControl: TMemo);
begin
  MemoControl.StyleElements := MemoControl.StyleElements - [seFont, seClient];
  MemoControl.Color := BrandSurfaceColor;
  MemoControl.Font.Color := BrandTextColor;
end;

procedure InitializeBrandPalette;
begin
  BrandDarkMode := IsDarkInstallMode;
  if BrandDarkMode then begin
    BrandTextColor := $00F2F7EA;
    BrandAccentColor := $00BFFF73;
    BrandMutedColor := $00949B7F;
    BrandSurfaceColor := $001A1C10;
  end else begin
    BrandTextColor := $002B3418;
    BrandAccentColor := $004A6918;
    BrandMutedColor := $005F714B;
    BrandSurfaceColor := $00EEF5E7;
  end;
end;

#include "WindowChrome.iss"
#include "RichTextTheme.iss"

procedure AlignMorphocyteNavigation;
begin
  if not BrandColorsEnabled then Exit;
  WizardForm.NextButton.Left := WizardForm.CancelButton.Left -
    BrandNavigationGap - WizardForm.NextButton.Width;
  WizardForm.BackButton.Left := WizardForm.NextButton.Left -
    BrandNavigationGap - WizardForm.BackButton.Width;
end;

procedure InitializeMorphocyteBranding;
var
  NavigationButtons: array of TNewButton;
  I, NavigationHeight: Integer;
begin
  BrandColorsEnabled := ShouldUseBrandColors;
  InitializeBrandPalette;
  ApplyReadableBackground;
  WizardForm.WelcomeLabel1.Caption := 'MorphocyteOS';
  WizardForm.WelcomeLabel1.Font.Style := [fsBold];
  WizardForm.WelcomeLabel1.Height := ScaleY(48);
  WizardForm.WelcomeLabel2.Top := WizardForm.WelcomeLabel1.Top + WizardForm.WelcomeLabel1.Height + ScaleY(16);
  WizardForm.WelcomeLabel2.Height := WizardForm.WelcomePage.ClientHeight - WizardForm.WelcomeLabel2.Top - ScaleY(24);
  WizardForm.WelcomeLabel2.Caption :=
    'Твой трафик. Твои маршруты.' + #13#10#13#10 +
    'VPN и правила маршрутизации в одном приложении.' + #13#10#13#10 +
    'Мастер установит MorphocyteOS и создаст ярлык в «Пуске».' + #13#10#13#10 +
    CustomMessage('CloseBeforeInstall');
  WizardForm.DirEdit.AutoSelect := False;
  if BrandColorsEnabled then begin
    StyleBrandLabel(WizardForm.PageNameLabel, BrandAccentColor);
    StyleBrandLabel(WizardForm.PageDescriptionLabel, BrandMutedColor);
    StyleBrandLabel(WizardForm.WelcomeLabel1, BrandTextColor);
    StyleBrandLabel(WizardForm.WelcomeLabel2, BrandTextColor);
    StyleBrandLabel(WizardForm.FinishedHeadingLabel, BrandTextColor);
    StyleBrandLabel(WizardForm.FinishedLabel, BrandTextColor);
    StyleBrandRichMemo(WizardForm.LicenseMemo);
    StyleBrandReadyMemo;
    WizardForm.DirEdit.StyleElements := WizardForm.DirEdit.StyleElements - [seFont, seClient];
    WizardForm.DirEdit.Color := BrandSurfaceColor;
    WizardForm.DirEdit.Font.Color := BrandTextColor;
    WizardForm.TasksList.StyleElements := WizardForm.TasksList.StyleElements - [seFont, seClient];
    WizardForm.TasksList.Color := BrandSurfaceColor;
    WizardForm.TasksList.Font.Color := BrandTextColor;
    WizardForm.TasksList.MinItemHeight := ScaleY(32);
    // Two ungrouped tasks: avoid a full-page empty listbox behind them.
    WizardForm.TasksList.Anchors := [akLeft, akTop, akRight];
    WizardForm.TasksList.Height := ScaleY(80);
    WizardForm.TasksList.BorderStyle := bsNone;
    WizardForm.TasksList.ShowLines := False;
    WizardForm.TasksList.Flat := True;
    // Match the app's button proportions without changing the native handlers.
    SetArrayLength(NavigationButtons, 3);
    NavigationButtons[0] := WizardForm.BackButton;
    NavigationButtons[1] := WizardForm.NextButton;
    NavigationButtons[2] := WizardForm.CancelButton;
    NavigationHeight := ScaleY(32);
    BrandNavigationGap := WizardForm.NextButton.Left -
      WizardForm.BackButton.Left - WizardForm.BackButton.Width;
    if BrandNavigationGap < ScaleX(4) then BrandNavigationGap := ScaleX(4);
    if BrandNavigationGap > ScaleX(12) then BrandNavigationGap := ScaleX(12);
    for I := 0 to GetArrayLength(NavigationButtons) - 1 do begin
      NavigationButtons[I].Top := NavigationButtons[I].Top -
        (NavigationHeight - NavigationButtons[I].Height) div 2;
      NavigationButtons[I].Height := NavigationHeight;
    end;
    AlignMorphocyteNavigation;
  end;
  InitializeMorphocyteChrome;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  // Inno can reload license runs and resize/reposition navigation per page.
  if CurPageID = wpLicense then StyleBrandRichMemo(WizardForm.LicenseMemo);
  if CurPageID = wpReady then StyleBrandReadyMemo;
  if CurPageID = wpSelectDir then begin
    // Keep the editable path visible without an automatic full-text selection.
    WizardForm.DirEdit.SelStart := 0;
    WizardForm.DirEdit.SelLength := 0;
  end;
  AlignMorphocyteNavigation;
#if Int(PreviewInstaller) != 0
  if CurPageID = wpReady then begin
    WizardForm.NextButton.Enabled := False;
    WizardForm.NextButton.Caption := 'Просмотр';
  end;
#endif
  RefreshMorphocyteChrome;
end;

<event('InitializeUninstallProgressForm')>
procedure InitializeMorphocyteUninstallBranding;
begin
  ApplyReadableBackground;
end;
