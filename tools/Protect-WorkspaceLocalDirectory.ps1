[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$directory = Join-Path (Split-Path -Parent $PSScriptRoot) '.local'
New-Item -ItemType Directory -Path $directory -Force | Out-Null
if ((Get-Item -LiteralPath $directory -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
    throw 'The private .local directory must not be a symbolic link or junction.'
}
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    & chmod 700 -- $directory
    if ($LASTEXITCODE -ne 0) { throw 'Could not restrict access to the private .local directory.' }
    return
}
# Git ignore rules do not protect credentials from other local operating system users.
$access = [Security.AccessControl.DirectorySecurity]::new()
# Modify only the DACL; owner / audit sections can require unrelated OS privileges.
$existing = Get-Acl -LiteralPath $directory
$access.SetSecurityDescriptorSddlForm(
    $existing.GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]::Access),
    [Security.AccessControl.AccessControlSections]::Access)
$access.SetAccessRuleProtection($true, $false)
foreach ($rule in @($access.Access)) { [void]$access.RemoveAccessRuleSpecific($rule) }
$currentUser = [Security.Principal.WindowsIdentity]::GetCurrent().User
$principals = @($currentUser,
    [Security.Principal.SecurityIdentifier]::new('S-1-5-18'),
    [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
foreach ($principal in $principals) {
    $rule = [Security.AccessControl.FileSystemAccessRule]::new($principal,
        [Security.AccessControl.FileSystemRights]::FullControl,
        [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit',
        [Security.AccessControl.PropagationFlags]::None,
        [Security.AccessControl.AccessControlType]::Allow)
    [void]$access.AddAccessRule($rule)
}
$item = Get-Item -LiteralPath $directory -Force
if ($PSVersionTable.PSVersion.Major -ge 6) {
    [IO.FileSystemAclExtensions]::SetAccessControl($item, $access)
} else {
    $item.SetAccessControl($access)
}
