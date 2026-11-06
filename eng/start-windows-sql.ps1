$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_OS -ne 'Windows') {
    throw 'This setup is only for the disposable Windows Actions runner.'
}
# Use the runner's installed SQL instance. Never touch a developer/customer instance.
$instances = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL'
$instance = $instances.PSObject.Properties | Where-Object { $_.Name -notlike 'PS*' } | Select-Object -First 1
if (!$instance) { throw 'No SQL Server instance available on this runner.' }
$name = $instance.Name
$id = $instance.Value
$serviceName = if ($name -eq 'MSSQLSERVER') { 'MSSQLSERVER' } else { 'MSSQL$' + $name }
$service = Get-CimInstance Win32_Service | Where-Object Name -eq $serviceName
$cert = New-SelfSignedCertificate -DnsName 'localhost', $env:COMPUTERNAME -CertStoreLocation 'Cert:\LocalMachine\My' -KeySpec KeyExchange -Provider 'Microsoft RSA SChannel Cryptographic Provider' -NotAfter (Get-Date).AddDays(3) -Type SSLServerAuthentication
$public = Join-Path $env:RUNNER_TEMP 'hisabtak-sql-test.cer'
Export-Certificate -Cert $cert -FilePath $public | Out-Null
Import-Certificate -FilePath $public -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null
$rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
$keyPath = Join-Path $env:ProgramData ('Microsoft\Crypto\RSA\MachineKeys\' + $rsa.CspKeyContainerInfo.UniqueKeyContainerName)
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
Restart-Service $serviceName -Force
# Windows integrated authentication; no password or bypass of certificate verification.
"HISABTAK_UI_SQL_CONNECTION=Server=tcp:localhost,14339;Database=master;Integrated Security=true;Encrypt=true;TrustServerCertificate=false;Connect Timeout=30" | Out-File -FilePath $env:GITHUB_ENV -Encoding utf8 -Append
Write-Host 'Disposable Windows SQL configured with certificate verification.'
