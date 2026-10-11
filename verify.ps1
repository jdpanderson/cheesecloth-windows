# Checks that each file has a valid Authenticode signature made with the
# certificate in signing.cer. That certificate is self-signed, so Windows does
# not trust it. The script trusts it for the check only: it adds it to the
# machine's trusted root certificates, then removes it again. This needs an
# administrator.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '',
    Justification = 'It reports progress to the console and the CI log.')]
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string[]] $File
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$cert = [Security.Cryptography.X509Certificates.X509Certificate2]::new((Join-Path $PSScriptRoot 'signing.cer'))
$root = [Security.Cryptography.X509Certificates.X509Store]::new('Root', 'LocalMachine')
$root.Open('ReadWrite')
# A certificate that was trusted before stays trusted.
$added = -not $root.Certificates.Contains($cert)
try {
    if ($added) { $root.Add($cert) }
    foreach ($f in $File) {
        $signature = Get-AuthenticodeSignature $f
        if ($signature.Status -ne 'Valid') {
            throw "the signature of $f is $($signature.Status): $($signature.StatusMessage)"
        }
        $signer = $signature.SignerCertificate
        if ($signer.Thumbprint -ne $cert.Thumbprint) {
            throw "$f is signed by $($signer.Subject) ($($signer.Thumbprint)), not by signing.cer ($($cert.Thumbprint))"
        }
        Write-Host "ok: $f is signed with signing.cer"
    }
} finally {
    if ($added) { $root.Remove($cert) }
    $root.Close()
}
