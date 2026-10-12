param(
    [Parameter(Mandatory = $true)][string]$PackageDirectory,
    [Parameter(Mandatory = $true)][string]$MaintenanceHelperPath,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)

# Metadata tests only: never launch Setup, alter registration, or start a VPN.
$ErrorActionPreference = 'Stop'
$buildScript = Join-Path $PSScriptRoot 'Build-Installer.ps1'
$output = [System.IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\', '/')
if (Test-Path -LiteralPath $output) { throw 'Choose a new installer test directory.' }
New-Item -ItemType Directory -Path $output | Out-Null
$checkCount = 0
function Assert-Check {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "Installer test failed: $Message" }
    $script:checkCount++
}

$prepared = & $buildScript -PackageDirectory $PackageDirectory -MaintenanceHelperPath $MaintenanceHelperPath -OutputDirectory $output -TestInstaller -PrepareOnly
$generated = [System.IO.File]::ReadAllLines($prepared.GeneratedFiles)
Assert-Check ($prepared.AppId -eq '40B5D24D-0B16-48E6-85C5-6BE9C471D115') 'test installer has isolated AppId'
Assert-Check ($generated.Count -eq ($prepared.FileCount + 2)) 'each managed file, manifest, marker listed explicitly'
Assert-Check (@($generated | Where-Object { $_ -notmatch 'Flags: ignoreversion uninsneveruninstall$' }).Count -eq 0) 'Inno never unconditionally removes payload files'
Assert-Check (@($generated | Where-Object { $_ -match '(?i)(portable\.flag|settings\.json|\.ya?ml"|\.log"|\\\*"|/\*"|recursesubdirs)' }).Count -eq 0) 'no user state or wildcard files'
$marker = Get-Content -LiteralPath (Join-Path $prepared.StageDirectory '.installer-owned.json') -Raw | ConvertFrom-Json
Assert-Check ($marker.SchemaVersion -eq 1 -and $marker.AppId -eq $prepared.AppId -and $marker.TestMode -eq $true) 'test marker matches isolated identity'
$bootstrap = [System.IO.File]::ReadAllText((Join-Path (Split-Path -Parent $PSScriptRoot) 'Installer\UninstallBootstrap.iss'))
Assert-Check ($prepared.MaintenanceHelperHash -eq (Get-FileHash -LiteralPath $MaintenanceHelperPath -Algorithm SHA256).Hash.ToLowerInvariant()) 'maintenance helper pinned to build-time hash'
Assert-Check ($bootstrap.Contains("UninstallHelperHash := '{#MaintenanceHelperHash}'") -and -not $bootstrap.Contains('release-manifest.json')) 'execution trust does not depend on mutable manifest'

$repo = Split-Path -Parent $PSScriptRoot
$setup = [System.IO.File]::ReadAllText((Join-Path $repo 'Installer/MorphocyteOS.iss'))
$branding = [System.IO.File]::ReadAllText((Join-Path $repo 'Installer/Branding.iss'))
$chrome = [System.IO.File]::ReadAllText((Join-Path $repo 'Installer/WindowChrome.iss'))
$richText = [System.IO.File]::ReadAllText((Join-Path $repo 'Installer/RichTextTheme.iss'))
$builder = [System.IO.File]::ReadAllText($buildScript)
$release = [System.IO.File]::ReadAllText((Join-Path $repo 'Scripts/Release.ps1'))
$artworkSource = [System.IO.File]::ReadAllText((Join-Path $repo 'Source/InstallerArtworkBuilder/Program.cs'))
Assert-Check ($artworkSource.Contains('graphics.DrawString("VPN МАРШРУТИЗАЦИЯ"') -and
    -not $artworkSource.Contains('VPN И МАРШРУТЫ')) 'both banner themes use the requested VPN routing caption'
Assert-Check ([regex]::Matches($setup, '#define DefaultInstallDirectory "\{autopf\}\\MorphocyteOS"').Count -eq 2 -and
    $setup -match '(?m)^AppendDefaultDirName=yes\r?$' -and $setup.Contains('#include "InstallDirectory.iss"')) 'production and test installers share the canonical final folder name without changing their separate identities'
Assert-Check ($setup -match '(?s)function NextButtonClick.*?MorphocyteInstallDirectory\(WizardDirValue\).*?RunSetupValidation\(WizardDirValue\)' -and
    $setup -match '(?s)procedure InitializeWizard.*?MorphocyteInstallDirectory\(WizardDirValue\)' -and
    $setup.Contains('Конечная папка установки должна называться MorphocyteOS.')) 'manual paths and silent/default paths are normalized before validation and guarded before copying'
Assert-Check ([regex]::Matches($release, "'Installer/InstallDirectory.iss'").Count -eq 2) 'directory normalizer is present in both the release source allowlist and archive verification'
Assert-Check ($setup -match '(?m)^SetupArchitecture=x64\r?$') 'installer callbacks use the verified native x64 ABI'
Assert-Check ($setup -match '(?m)^WizardStyle=modern (dynamic|\{#WizardAppearance\}) windows11 hidebevels\r?$' -and
    ($setup -match '(?m)^WizardStyle=modern dynamic ' -or $setup -match '(?m)^\s*#define WizardAppearance "dynamic"\r?$')) 'real native styling follows the Windows light or dark theme'
Assert-Check ($setup -match '(?m)^WizardSmallImageBackColor=none\r?$') 'small logo keeps alpha without a square background'
Assert-Check ($setup -match '(?m)^WizardSmallImageBackColorDynamicDark=none\r?$') 'dark logo also keeps alpha without a square background'
Assert-Check ($setup.Contains('#define MainBanner "wizard-banner-light.png"') -and
    $setup.Contains('#define MainBackground "wizard-background-light.png"') -and
    $setup -match '(?m)^WizardImageFileDynamicDark=\{#SourcePath\}\\Assets\\wizard-banner\.png\r?$' -and
    $setup -match '(?m)^WizardBackImageFileDynamicDark=\{#SourcePath\}\\Assets\\wizard-background\.png\r?$') 'light and dark variants use their own matching artwork'
Assert-Check ($branding.Contains("CustomMessage('CloseBeforeInstall')")) 'welcome retains the explicit VPN shutdown warning'
Assert-Check ($setup -match '(?m)^WizardBackColor=window\r?$' -and $branding.Contains('WizardSetBackImage([], True, True, 255)')) 'decorative background has a system-color fallback'
Assert-Check ($branding.Contains('not HighContrastActive') -and $branding.Contains("CompareText(ParamStr(I), '/NOSTYLE')")) 'high contrast and explicit native styling retain readable text'
Assert-Check ($branding -notmatch '(?i)(external\s+|\.dll|Exec\(|ShellExec\(|TNewButton\.Create)') 'branding adds no external skin or replacement navigation buttons'
Assert-Check ($branding.Contains('ScaleX(') -and $branding.Contains('ScaleY(')) 'custom decoration uses scaled coordinates'
Assert-Check ($branding.Contains('#include "RichTextTheme.iss"') -and
    $branding.Contains('StyleBrandRichMemo(WizardForm.LicenseMemo);')) 'license uses its dedicated native text-color adapter'
Assert-Check ($branding.Contains('if CurPageID = wpLicense then StyleBrandRichMemo(WizardForm.LicenseMemo);')) 'license color is restored after native page loading'
Assert-Check ($branding.Contains('if CurPageID = wpReady then StyleBrandReadyMemo;') -and
    $richText.Contains('WizardForm.ReadyMemo.BorderStyle := bsNone;') -and
    $richText.Contains('WizardForm.ReadyMemo.WordWrap := True;') -and
    $richText.Contains('WizardForm.ReadyMemo.ScrollBars := ssNone;')) 'summary theme is restored after native page loading without a stock border or unnecessary horizontal scrollbar'
Assert-Check ($richText.Contains('LineCount := ChromeSendMessage(WizardForm.ReadyMemo.Handle, $00BA, 0, 0);') -and
    $richText.Contains('if NeedsScrollBar then WizardForm.ReadyMemo.ScrollBars := ssVertical;') -and
    $richText.Contains('ChromeRestoreDC(DC, SavedDC);') -and $richText.Contains('BrandReleaseDeviceContext(WizardForm.ReadyMemo.Handle, DC);')) 'wrapped long summaries remain scrollable and temporary font-metric device contexts are released before handle recreation'
Assert-Check ($richText.Contains('var Format: TBrandCharFormat') -and
    $richText.Contains('FaceName: array[0..31] of Word;') -and $richText.Contains('TailPadding: Word;')) 'character format is passed by pointer with the complete native layout'
Assert-Check ($richText.Contains('Format.Mask := $40000000;') -and
    $richText.Contains('Format.Effects := 0;') -and $richText.Contains('Format.TextColor := BrandTextColor;') -and
    $richText.Contains('BrandSetCharacterFormat(MemoControl.Handle, $0444, 4, Format)')) 'all loaded license runs receive an explicit readable theme color'
Assert-Check ($richText -notmatch '(?i)(\.Text\s*:=|\.RTFText\s*:=|\.SelStart\s*:=|\.UseRichEdit\s*:=)') 'license recoloring preserves the document, links and selection'
Assert-Check ($branding.Contains('WizardForm.NextButton.Left := WizardForm.CancelButton.Left -') -and
    $branding.Contains('BrandNavigationGap - WizardForm.NextButton.Width;') -and
    $branding.Contains('WizardForm.BackButton.Left := WizardForm.NextButton.Left -') -and
    $branding.Contains('BrandNavigationGap - WizardForm.BackButton.Width;')) 'both navigation gaps use the same scaled spacing without changing button widths'
Assert-Check ($branding.Contains('BrandNavigationGap < ScaleX(4)') -and
    $branding.Contains('BrandNavigationGap > ScaleX(12)') -and
    $branding -match '(?s)procedure CurPageChanged.*?AlignMorphocyteNavigation;') 'navigation spacing is bounded and reapplied when pages change'
Assert-Check ([regex]::Matches($release, "'Installer/RichTextTheme.iss'").Count -eq 2) 'release source allowlist and archive verification both include the rich text adapter'
Assert-Check ($branding -notmatch 'BrandFooter|тест установщика|· Windows x64|· просмотр') 'the decorative version/test footer is removed in all installer modes'
Assert-Check ($branding.Contains('WizardForm.DirEdit.AutoSelect := False;') -and
    $branding.Contains('WizardForm.DirEdit.SelStart := 0;') -and $branding.Contains('WizardForm.DirEdit.SelLength := 0;')) 'installation path remains editable without automatic full-text highlighting'
Assert-Check ($setup.Contains('#include "WindowChrome.iss"') -or $branding.Contains('#include "WindowChrome.iss"')) 'custom chrome is compiled into the installer'
Assert-Check ($chrome -notmatch '(?i)(Exec\(|ShellExec\(|(?:isskin|vclstyles|skincontrols)\.dll)') 'custom chrome never starts helpers or loads third-party skins'
Assert-Check ($chrome.Contains('if not BrandColorsEnabled or ChromeInitialized then Exit;')) 'system accessibility fallback keeps its normal window decoration'
Assert-Check ($chrome.Contains('ChromeRegisterButton(WizardForm.NextButton);') -and
    $chrome.Contains('ChromeRegisterButton(WizardForm.BackButton);') -and
    $chrome.Contains('ChromeRegisterButton(WizardForm.CancelButton);')) 'branded navigation keeps the actual native Next Back and Cancel buttons'
Assert-Check ($chrome.Contains('if WizardForm.CancelButton.Enabled then') -and
    $chrome.Contains('ChromeSendMessage(WizardForm.CancelButton.Handle, ChromeBMClick, 0, 0);')) 'custom close retains native cancel confirmation and safety'
Assert-Check ($chrome -match '(?m)^function ChromeFillRect\(DC: NativeUInt; var Rectangle: TRect; Brush: NativeUInt\): Integer;\r?$' -and
    $chrome -notmatch '(?m)^function ChromeFillRect\([^\r\n]*const\s') 'FillRect receives a rectangle pointer through var rather than a by-value record'
Assert-Check ($chrome -match '(?m)^function ChromeEndPaint\(Window: NativeUInt; var PaintBuffer: TChromePaintBuffer\): Boolean;\r?$' -and
    $chrome -notmatch '(?m)^function ChromeEndPaint\([^\r\n]*const\s') 'EndPaint receives its paint buffer pointer through var rather than a by-value record'
$tasksProcedureMatch = [regex]::Match($chrome, '(?s)function ChromeTasksProc\([^;]*;.*?\)\s*:\s*NativeInt;\s*(?:var\s+.*?)?begin\s*(?<Body>.*?)\r?\nend;')
$tasksProcedure = $tasksProcedureMatch.Groups['Body'].Value
Assert-Check ($tasksProcedureMatch.Success -and
    $tasksProcedure -match '(?s)Result := ChromeDefSubclassProc\(Window, Message, WParam, LParam\);.*?ChromeInvalidateRect\(Window, 0, False\);') 'checklist invalidates its full client area after native checked-state changes'
Assert-Check ($tasksProcedure.Contains('(Message = $0201)') -and $tasksProcedure.Contains('(Message = $0202)') -and
    $tasksProcedure.Contains('(Message = $0100)') -and $tasksProcedure.Contains('(Message = $0101)') -and
    $tasksProcedure.Contains('(Message = ChromeWMSetFocus)') -and $tasksProcedure.Contains('(Message = ChromeWMKillFocus)')) 'checklist repaint covers mouse toggles Space key events and focus changes'
Assert-Check ($chrome.Contains('ChromeTasksCallback := CreateCallback(@ChromeTasksProc);') -and
    $chrome.Contains('ChromeSetWindowSubclass(WizardForm.TasksList.Handle, ChromeTasksCallback, $4D434854, 0)')) 'native checklist actually attaches its full repaint callback'
Assert-Check ($chrome.Contains('ChromeBeginBufferedPaint') -and $chrome.Contains('ChromeEndBufferedPaint') -and
    $chrome.Contains('ChromeBitBlt(Buffer.TargetDC, Buffer.TargetRectangle.Left, Buffer.TargetRectangle.Top,') -and
    $chrome.Contains('ChromeReleasePaintBuffer(Buffer);')) 'custom painting copies a completed offscreen frame and releases the temporary buffer'
Assert-Check ($chrome.Contains('ChromeCreateDIBSection(TargetDC, Info, 0, Bits, 0, 0)') -and
    $chrome.Contains('Info.Size := 40;') -and $chrome.Contains('Info.Height := -Height;') -and
    $chrome.Contains('var Info: TChromeBitmapInfo') -and $chrome.Contains('var Bits: NativeUInt')) 'the top-down paint buffer has pointer-correct native bitmap parameters'
Assert-Check ($tasksProcedure.Contains('if Message = ChromeWMEraseBkgnd then begin Result := 1; Exit; end;') -and
    $tasksProcedure.Contains('if DC <> 0 then ChromeDrawTasks(WizardForm.TasksList, DC);') -and
    $chrome.Contains('ChromeGetListItemRectangle(Control.Handle, $0198, ItemID, ItemRectangle)')) 'the entire checklist including its empty tail is painted in one frame using native row positions'
Assert-Check ($chrome.Contains('if Hovered then begin Target := 1; Duration := 130; end') -and
    $chrome.Contains('else begin Target := 0; Duration := 150; end;') -and
    $chrome.Contains('0.12 * ChromeButtons[Index].Hover.Value')) 'button hover timing and accent tint match the main app'
Assert-Check ($chrome.Contains('0.28 * ChromeButtons[Index].Press.Value') -and
    $chrome.Contains('ChromeBMGetState, 0, 0) and $0004')) 'pressed feedback dims the real native button according to its actual input state'
Assert-Check ($chrome.Contains('ChromeGetAnimationSetting($1042, 0, Enabled, 0)') -and
    $chrome.Contains('if not ChromeAnimationsEnabled or (Duration = 0) then begin')) 'Windows reduced-motion preference disables button tweens'
Assert-Check ($chrome.Contains('ChromeKillTimer(ChromeButtons[Index].Handle, ChromeAnimationTimerID)') -and
    $chrome.Contains('if ChromeButtons[Index].AnimationTimerActive then begin') -and
    $chrome.Contains('ChromeButtons[Index].Handle := 0;')) 'button timers stop safely when animations finish or native handles are destroyed'
Assert-Check ($chrome.Contains('ChromeGdipSetSmoothingMode(Result, 4)') -and
    $chrome.Contains('ChromeGdipSetPixelOffsetMode(Result, 4)')) 'all custom geometry uses antialiasing and pixel-aligned rendering'
Assert-Check ($chrome.Contains('var Input: TChromeGdiPlusInput32') -and
    $chrome.Contains('var Input: TChromeGdiPlusInput64') -and $chrome.Contains('PointerPadding: LongWord;')) 'GDI+ startup uses pointer parameters and explicit native x64 alignment'
Assert-Check ($chrome.Contains('ChromeWMQueryUIState = $0129;') -and
    $chrome.Contains('ChromeUISFHideFocus = $0001;') -and
    $chrome.Contains('ChromeShowGlyphFocus(Control.Handle, Focused)') -and
    $chrome.Contains('ChromeShowGlyphFocus(Control.Handle, Control.Focused)')) 'task and license focus cues respect native keyboard-cue visibility and remain on their glyphs'
Assert-Check ($chrome.Contains('AvailableDiameter := ClientHeight - (2 * Inset) - MaximumStroke;') -and
    $chrome.Contains('if Diameter > AvailableDiameter then Diameter := AvailableDiameter;') -and
    $chrome.Contains('Top := (ClientHeight - Diameter) / 2.0;')) 'license circles fit their actual native height with antialiasing margins and exact vertical centering'
Assert-Check ($chrome.Contains('ChromeWMPrintClient = $0318;') -and
    [regex]::Matches($chrome, 'if Message = ChromeWMPrintClient then begin').Count -ge 3 -and
    $chrome.Contains('ChromePaintButton(Index, DC, Rectangle, ChromeCurrentButtonState(Window));') -and
    $chrome.Contains('ChromePaintButton(Index, WParam, Rectangle, ChromeCurrentButtonState(Window));')) 'buttons, tasks and radio controls paint complete custom frames for both normal and composited painting'
Assert-Check ($chrome.Contains('if (NativeState and $0004) <> 0 then Result := Result or ChromeODSSelected;') -and
    $chrome.Contains('if (NativeState and $0008) <> 0 then Result := Result or ChromeODSFocus;') -and
    $chrome.Contains('if not ChromeIsWindowEnabled(Window) then Result := Result or ChromeODSDisabled;')) 'direct button painting retains real native pressed, focus and disabled state'
Assert-Check ($chrome -notmatch 'ChromeEnableWizardCompositing|ChromeSetWindowStyle\([^\r\n]*\$02000000' -and
    $chrome -notmatch 'ChromeSendMessage\([^\r\n]*(?:\$000B|ChromeWMSetRedraw)') 'native text and logo painting are not routed through wizard-wide compositing or a global redraw freeze'
$buttonPainter = [regex]::Match($chrome, '(?s)procedure ChromePaintButton\b(?<Body>.*?)function ChromeCurrentButtonState').Groups['Body'].Value
Assert-Check ([regex]::Matches($buttonPainter, 'ChromeAARoundRect\(').Count -eq 1 -and
    $buttonPainter.Contains('ChromeShowGlyphFocus(ChromeButtons[Index].Handle, Focused)') -and
    $buttonPainter.Contains('BorderColor := BrandAccentColor;')) 'buttons match the app with an existing-border keyboard cue and no additional inner focus contour'
Assert-Check ($chrome.Contains("<event('DeinitializeSetup')>") -and
    $chrome.Contains('Token := ChromeGdiPlusToken;') -and $chrome.Contains('ChromeGdiPlusToken := 0;') -and
    $chrome.Contains('if Token <> 0 then ChromeGdiPlusShutdown(Token);')) 'GDI+ startup token is released once when Setup closes'
Assert-Check ($chrome.Contains('ChromeGdipDeleteGraphics(Graphics);') -and
    $chrome.Contains('ChromeGdipDeletePath(Path);') -and $chrome.Contains('ChromeGdipDeletePen(Pen);') -and
    $chrome.Contains('ChromeGdipDeleteBrush(Brush);')) 'temporary antialiased painting resources are released'
Assert-Check ($branding.Contains('WizardForm.TasksList.Height := ScaleY(80);') -and
    $branding.Contains('NavigationHeight := ScaleY(32);')) 'ungrouped tasks stay compact and navigation uses comfortable scaled buttons'
Assert-Check ($chrome.Contains('CenterX := (Rectangle.Left + Rectangle.Right) / 2.0;') -and
    $chrome.Contains('CenterY := (Rectangle.Top + Rectangle.Bottom) / 2.0;') -and
    $chrome.Contains('ChromeButtons[Index].Control <> ChromeCloseButton') -and
    $chrome.Contains('ChromeButtons[Index].Control <> ChromeMinimizeButton')) 'header glyphs use geometry centered in their real button bounds instead of font metrics'
Assert-Check ($setup -match '(?s)#if Int\(PreviewInstaller\) != 0\s*;[^\r\n]*\s*PrivilegesRequired=lowest\s*Uninstallable=no\s*CreateUninstallRegKey=no\s*AppMutex=\s*#else\s*PrivilegesRequired=admin\s*Uninstallable=yes') 'preview is unprivileged while the real installer keeps its privileges'
Assert-Check ($setup -match '(?s)\[Files\]\s*#if Int\(PreviewInstaller\) == 0.*?#include GeneratedFiles\s*#endif') 'preview excludes all installation payload files'
Assert-Check ($setup -match '(?s)\[Icons\]\s*#if Int\(PreviewInstaller\) == 0.*?#endif') 'preview excludes shell shortcuts'
Assert-Check ($setup.Contains('Result := CurPageID <> wpReady;') -and $setup.Contains("Result := 'Это только просмотр оформления. Установка отключена.'")) 'both interactive and silent preview installation are blocked'
Assert-Check ($setup -notmatch '(?im)^\[(Registry|UninstallRun)\]\r?$') 'Setup adds no registry actions or uninstall launches'
Assert-Check ($setup -match '(?s)\[Run\]\s*#if Int\(PreviewInstaller\) == 0\s*(?:;[^\r\n]*\s*)*Filename: "\{app\}\\MorphocyteOS\.exe";[^\r\n]*Verb: "runas";[^\r\n]*Tasks: launchapp[^\r\n]*Flags: nowait skipifsilent shellexec runasoriginaluser\s*#endif' -and
    $setup -notmatch '(?m)^Filename:[^\r\n]*Flags:[^\r\n]*runascurrentuser') 'preview and silent mode exclude launch while selected launch uses the original user shell and native elevation instead of CreateProcess 740'
Assert-Check ($setup -match '(?m)^Name: "launchapp";[^\r\n]*Flags: unchecked\r?$') 'launch is an additional task that starts unchecked'
Assert-Check ($setup -match '(?m)^UsePreviousTasks=no\r?$') 'repair does not silently restore a previous launch choice'
Assert-Check ($setup -match '(?m)^Name: "desktopicon";[^\r\n]*Flags: unchecked\r?$') 'desktop shortcut remains optional and initially unchecked'
Assert-Check ($setup -notmatch '(?m)^Name: "(launchapp|desktopicon)";[^\r\n]*GroupDescription:') 'additional tasks use a clean checkbox list without decorative group rows'
Assert-Check ($setup -notmatch '(?i)(postinstall|--startup|--autostart|--connect|--start-vpn|--tun)') 'installer never passes an automatic VPN or autostart command'
Assert-Check ($setup -match '(?s)#if Int\(PreviewInstaller\) == 0\s*#include "UninstallBootstrap\.iss"\s*#endif') 'preview excludes the uninstall cleanup hook'
Assert-Check ($builder.Contains('[switch]$PreviewOnly') -and $builder.Contains('PreviewInstaller=')) 'build script explicitly selects preview mode'
Assert-Check ($builder.Contains("[ValidateSet('system', 'light', 'dark')][string]`$PreviewTheme = 'system'") -and
    $builder.Contains("if (-not `$PreviewOnly -and `$PreviewTheme -ne 'system')")) 'forced light or dark themes cannot change a real installer'
Assert-Check ($builder.Contains("[version]'7.1'")) 'compiler minimum supports the native branded presentation'
Assert-Check ($release.Contains("'Source/InstallerArtworkBuilder'") -and $release.Contains("'Installer/Branding.iss'") -and $release.Contains("'Installer/WindowChrome.iss'")) 'source packaging explicitly includes branding, custom chrome and its generator'

function Assert-PngArtwork {
    param([string]$Name, [int]$Width, [int]$Height)
    $imagePath = Join-Path $repo "Installer/Assets/$Name"
    $imageFile = Get-Item -LiteralPath $imagePath
    Assert-Check (($imageFile.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0 -and $imageFile.Length -ge 24 -and $imageFile.Length -le 32MB) "$Name is a bounded ordinary image"
    $bytes = [System.IO.File]::ReadAllBytes($imagePath)
    Assert-Check ([BitConverter]::ToString($bytes, 0, 8) -eq '89-50-4E-47-0D-0A-1A-0A' -and [System.Text.Encoding]::ASCII.GetString($bytes, 12, 4) -eq 'IHDR') "$Name is a PNG"
    $actualWidth = ([int]$bytes[16] -shl 24) -bor ([int]$bytes[17] -shl 16) -bor ([int]$bytes[18] -shl 8) -bor [int]$bytes[19]
    $actualHeight = ([int]$bytes[20] -shl 24) -bor ([int]$bytes[21] -shl 16) -bor ([int]$bytes[22] -shl 8) -bor [int]$bytes[23]
    Assert-Check ($actualWidth -eq $Width -and $actualHeight -eq $Height) "$Name keeps its native artwork aspect ratio"
    Assert-Check ($setup.Contains($Name) -and $release.Contains("'Installer/Assets/$Name'")) "$Name is used by Setup and explicitly included in source packaging"
}
Assert-PngArtwork 'wizard-banner.png' 656 1256
Assert-PngArtwork 'wizard-banner-light.png' 656 1256
Assert-PngArtwork 'wizard-mark.png' 128 128
Assert-PngArtwork 'wizard-background.png' 1988 1440
Assert-PngArtwork 'wizard-background-light.png' 1988 1440

function Assert-RejectedManifest {
    param([string]$Name, [object]$Manifest)
    $fixture = Join-Path $output $Name
    New-Item -ItemType Directory -Path $fixture | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $fixture 'release-manifest.json'), ($Manifest | ConvertTo-Json -Depth 8), [System.Text.UTF8Encoding]::new($false))
    $rejected = $false
    try { & $buildScript -PackageDirectory $fixture -MaintenanceHelperPath $MaintenanceHelperPath -OutputDirectory (Join-Path $fixture 'prepared') -PrepareOnly | Out-Null }
    catch { $rejected = $true }
    Assert-Check $rejected $Name
}

foreach ($path in @('../outside.exe', 'Licenses/../outside.exe', 'portable.flag', 'settings.json', 'profile.yaml',
    'Licenses/private.log', 'Licenses/NUL.txt', 'Licenses/trailing. ', 'ThirdPartySource/a:b', 'other.exe')) {
    $fixtureName = 'invalid-' + [Guid]::NewGuid().ToString('N')
    Assert-RejectedManifest $fixtureName @{ SchemaVersion = 1; Version = '1.2.7'; Files = @(@{ Path = $path; Size = 1; Sha256 = ('a' * 64) }) }
}
Assert-RejectedManifest 'invalid-schema' @{ SchemaVersion = 2; Version = '1.2.7'; Files = @(@{ Path = 'MorphocyteOS.exe'; Size = 1; Sha256 = ('a' * 64) }) }
Assert-RejectedManifest 'invalid-version' @{ SchemaVersion = 1; Version = '../1.2.7'; Files = @(@{ Path = 'MorphocyteOS.exe'; Size = 1; Sha256 = ('a' * 64) }) }
Assert-RejectedManifest 'invalid-hash' @{ SchemaVersion = 1; Version = '1.2.7'; Files = @(@{ Path = 'MorphocyteOS.exe'; Size = 1; Sha256 = 'not-a-hash' }) }
Assert-RejectedManifest 'negative-size' @{ SchemaVersion = 1; Version = '1.2.7'; Files = @(@{ Path = 'MorphocyteOS.exe'; Size = -1; Sha256 = ('a' * 64) }) }
Write-Output "Installer metadata checks passed: $checkCount. No installation was started."
