$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_OS -ne 'Windows') {
    throw 'This setup is only for the disposable Windows Actions runner.'
}
# Install an isolated Express instance on this ephemeral CI machine.
# Official download page: https://www.microsoft.com/en-us/download/details.aspx?id=108838
$media = Join-Path $env:RUNNER_TEMP 'hisabtak-sql-media'
New-Item -ItemType Directory -Path $media -Force | Out-Null
$bootstrap = Join-Path $media 'SQL2022-SSEI-Expr.exe'
Invoke-WebRequest 'https://download.microsoft.com/download/29654887-7cde-4397-bba3-d7f087970845/SQL2022-SSEI-Expr.exe' -OutFile $bootstrap
function Assert-MicrosoftSignature($path) {
    $signature = Get-AuthenticodeSignature $path
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
        throw "Invalid Microsoft installer signature: $path"
    }
}
function Run-Installer($path, $arguments) {
    $process = Start-Process $path -ArgumentList $arguments -PassThru -Wait -NoNewWindow
    if ($process.ExitCode -notin @(0,3010)) { throw "SQL installer exited $($process.ExitCode)." }
}
Assert-MicrosoftSignature $bootstrap
Run-Installer $bootstrap ('/Action=Download /MediaType=Core /Quiet /MediaPath="' + $media + '"')
$core = Join-Path $media 'SQLEXPR_x64_ENU.exe'
Assert-MicrosoftSignature $core
$expanded = Join-Path $media 'expanded'
Run-Installer $core ('/q /x:"' + $expanded + '"')
$setup = Join-Path $expanded 'setup.exe'
Assert-MicrosoftSignature $setup
$admin = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
Run-Installer $setup ('/Q /ACTION=Install /FEATURES=SQLEngine /INSTANCENAME=HISABTAKCI /IACCEPTSQLSERVERLICENSETERMS /SQLSYSADMINACCOUNTS="' + $admin + '" /TCPENABLED=1 /NPENABLED=0 /UPDATEENABLED=False')
$instances = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL'
$id = $instances.HISABTAKCI
if (!$id) { throw 'SQL instance installation did not complete.' }
$serviceName = 'MSSQL$HISABTAKCI'
$service = Get-CimInstance Win32_Service | Where-Object Name -eq $serviceName
$cert = New-SelfSignedCertificate -Subject "CN=$env:COMPUTERNAME" -DnsName $env:COMPUTERNAME, 'localhost' -KeyUsage DigitalSignature,KeyEncipherment -KeyLength 2048 -HashAlgorithm SHA256 -CertStoreLocation 'Cert:\LocalMachine\My' -KeySpec KeyExchange -Provider 'Microsoft RSA SChannel Cryptographic Provider' -NotAfter (Get-Date).AddDays(3) -Type SSLServerAuthentication
$public = Join-Path $env:RUNNER_TEMP 'hisabtak-sql-test.cer'
Export-Certificate -Cert $cert -FilePath $public | Out-Null
Import-Certificate -FilePath $public -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null
# X509Certificate2.PrivateKey exposes the legacy CSP container required by SQL Server.
# GetRSAPrivateKey may return a wrapper without CspKeyContainerInfo; never apply an ACL to the parent directory.
$privateKey = $cert.PrivateKey
$container = $privateKey.CspKeyContainerInfo.UniqueKeyContainerName
if ([string]::IsNullOrWhiteSpace($container)) { throw 'The test certificate has no legacy CSP container.' }
$keyPath = Join-Path $env:ProgramData ('Microsoft\Crypto\RSA\MachineKeys\' + $container)
if (!(Test-Path $keyPath -PathType Leaf)) { throw 'Certificate private-key file was not found.' }
$acl = Get-Acl $keyPath
$identity = $service.StartName
if ($identity -eq 'LocalSystem') { $identity = 'NT AUTHORITY\SYSTEM' }
$acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($identity, 'Read', 'Allow')))
Set-Acl $keyPath $acl
$network = "HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\$id\MSSQLServer\SuperSocketNetLib"
Set-ItemProperty $network Certificate $cert.Thumbprint.ToLowerInvariant()
Set-ItemProperty $network ForceEncryption 1
Set-ItemProperty "$network\Tcp" Enabled 1
Set-ItemProperty "$network\Tcp\IPAll" TcpDynamicPorts ''
Set-ItemProperty "$network\Tcp\IPAll" TcpPort '14339'
Set-Service $serviceName -StartupType Manual
try { Restart-Service $serviceName -Force }
catch {
    $parameters = Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\$id\MSSQLServer\Parameters"
    $log = $parameters.SQLArg1 -replace '^-e',''
    if (Test-Path $log) { Get-Content $log -Tail 70 | Write-Host }
    throw
}
# Windows integrated authentication; no password or bypass of certificate verification.
"HISABTAK_UI_SQL_CONNECTION=Server=tcp:localhost,14339;Database=master;Integrated Security=true;Encrypt=true;TrustServerCertificate=false;Connect Timeout=30" | Out-File -FilePath $env:GITHUB_ENV -Encoding utf8 -Append
Write-Host 'Disposable Windows SQL configured with certificate verification.'
