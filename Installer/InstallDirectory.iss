// Choose a parent freely, but keep one canonical application folder at its end.
// This only computes a path; it never renames or moves existing directories.
function MorphocyteInstallDirectory(const Value: String): String;
var
  Candidate, Leaf: String;
begin
  Candidate := Trim(Value);
  StringChangeEx(Candidate, '/', '\', True);
  Candidate := RemoveBackslashUnlessRoot(Candidate);
  if Candidate = '' then begin Result := ''; Exit; end;
  Leaf := ExtractFileName(Candidate);
  if (CompareText(Leaf, 'MorphocyteOS') = 0) or
    (CompareText(Leaf, 'MorphocyteOS-test-install') = 0) then
    Candidate := ExtractFileDir(Candidate);
  Result := AddBackslash(Candidate) + 'MorphocyteOS';
end;
