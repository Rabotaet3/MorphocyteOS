var
  UninstallHelperPath: String;
  UninstallHelperHash: String;

function PrepareVerifiedUninstallHelper: Boolean;
var
  ExePath: String;
begin
  Result := False;
  try
    ExePath := ExpandConstant('{app}\.installer\MorphocyteOS-maintenance.exe');
    UninstallHelperHash := '{#MaintenanceHelperHash}';
    if CompareText(GetSHA256OfFile(ExePath), UninstallHelperHash) <> 0 then Exit;
    UninstallHelperPath := ExpandConstant('{tmp}\MorphocyteOS-uninstall-helper.exe');
    if not FileCopy(ExePath, UninstallHelperPath, True) then Exit;
    if CompareText(GetSHA256OfFile(UninstallHelperPath), UninstallHelperHash) <> 0 then Exit;
    Result := True;
  except
    Log('Uninstaller bootstrap validation failed.');
  end;
end;

function InitializeUninstall: Boolean;
begin
  Result := PrepareVerifiedUninstallHelper;
  if not Result then MsgBox(CustomMessage('UninstallRefused'), mbError, MB_OK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  // Only after confirmation and AppMutex check; InitializeUninstall never deletes.
  if CurUninstallStep = usUninstall then begin
    if (UninstallHelperPath = '') or
       (CompareText(GetSHA256OfFile(UninstallHelperPath), UninstallHelperHash) <> 0) then
      RaiseException(CustomMessage('UninstallRefused'));
    if not Exec(UninstallHelperPath,
      '--installer-cleanup ' + QuotedArgument(ExpandConstant('{app}')) + ' {#AppGuid}',
      ExpandConstant('{tmp}'), SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      RaiseException(CustomMessage('UninstallRefused'));
    if ResultCode <> 0 then RaiseException(CustomMessage('UninstallRefused'));
  end;
end;

procedure DeinitializeUninstall;
begin
  if UninstallHelperPath <> '' then DeleteFile(UninstallHelperPath);
end;
