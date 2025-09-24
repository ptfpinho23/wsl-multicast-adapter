<#
.SYNOPSIS
    Registers a Hyper-V Guest Communication Service for a given VSOCK port.
.DESCRIPTION
    Linux uses AF_VSOCK (cid, port).
    Windows uses AF_HYPERV (VmId, Service GUID).
    Microsoft defined a template GUID:
        00000000-facb-11e6-bd58-64006a7986d3
    The "Data1" field (first 32 bits) is replaced by the port number in hex.
    Example: port 5000 (0x1388) => 00001388-facb-11e6-bd58-64006a7986d3
    This script creates the registry entry automatically.
.PARAMETER Port
    The VSOCK port number (e.g. 5000).
.PARAMETER Name
    Friendly name for the service (optional).
.EXAMPLE
    .\Register-VsockPort.ps1 -Port 5000 -Name "WSL Multicast Bridge"
#>

param(
    [Parameter(Mandatory=$true)]
    [int]$Port,

    [string]$Name = "VSOCK Service"
)

function Get-FacbGuidFromPort([int]$p) {
    $hex = '{0:x8}' -f ($p -band 0xFFFFFFFF)
    return "$hex-facb-11e6-bd58-64006a7986d3"
}

$guid = Get-FacbGuidFromPort -p $Port
$base = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization\GuestCommunicationServices'

Write-Host "Registering mapping for port $Port"
Write-Host " -> Service GUID: $guid"

# Create the key if missing
if (-not (Test-Path "$base\$guid")) {
    New-Item -Path "$base\$guid" -Force | Out-Null
}

# Add friendly name
New-ItemProperty -Path "$base\$guid" -Name 'ElementName' -Value $Name -PropertyType String -Force | Out-Null

Write-Host "Registered VSOCK port $Port with GUID $guid"
