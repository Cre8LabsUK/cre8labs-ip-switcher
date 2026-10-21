# Rebuilds src\Core\Oui\oui.csv (MAC prefix to vendor) from the public IEEE registry.
#
#   powershell -ExecutionPolicy Bypass -File tools\update-oui.ps1           # curated: security and network vendors
#   powershell -ExecutionPolicy Bypass -File tools\update-oui.ps1 -All      # every registered vendor (about 1 MB)
#
# The output is checked in, so normal builds never need the internet.
param([switch]$All)

$ErrorActionPreference = 'Stop'
$source = 'https://standards-oui.ieee.org/oui/oui.csv'
$out = Join-Path $PSScriptRoot '..\src\Core\Oui\oui.csv'

# Vendors an engineer meets on CCTV, access control and network jobs. Matched case-insensitively against the
# registered organisation name, so all of a vendor's prefixes are included.
$keep = @(
    'hikvision', 'dahua', 'axis communications', 'hanwha', 'samsung techwin', 'bosch security', 'uniview', 'vivotek',
    'mobotix', 'avigilon', 'pelco', 'honeywell', 'i-pro', 'panasonic', 'sony', 'canon', 'milestone', 'genetec',
    'ezviz', 'reolink', 'amcrest', 'lorex', 'flir', 'arecont', 'iduco', 'tiandy', 'tvt', 'kedacom', 'huawei',
    'ubiquiti', 'cisco', 'meraki', 'netgear', 'tp-link', 'd-link', 'aruba', 'hewlett packard', 'juniper',
    'mikrotik', 'routerboard', 'zyxel', 'draytek', 'fortinet', 'sonicwall', 'ruckus', 'extreme networks',
    'allied telesis', 'planet technology', 'edimax', 'teltonika', 'peplink', 'cambium', 'mimosa',
    'synology', 'qnap', 'western digital', 'seagate', 'raspberry pi', 'espressif', 'apple', 'microsoft',
    'dell', 'lenovo', 'hp inc', 'intel corporate', 'realtek', 'siemens', 'schneider', 'rockwell', 'phoenix contact',
    'paxton', 'gallagher', 'lenel', 'hid global', '2n telekomunikace', 'akuvox', 'grandstream', 'yealink',
    'polycom', 'avaya', 'commend', 'aiphone', 'texecom', 'pyronix', 'ajax systems'
)

$tmp = Join-Path ([IO.Path]::GetTempPath()) 'ieee-oui.csv'
Write-Host "Downloading $source"
Invoke-WebRequest -Uri $source -OutFile $tmp -UseBasicParsing

$rows = Import-Csv $tmp   # columns: Registry, Assignment, Organization Name, Organization Address
$lines = foreach ($r in $rows) {
    $name = ($r.'Organization Name' -replace '\s+', ' ').Trim()
    if (-not $All -and -not ($keep | Where-Object { $name -like "*$_*" })) { continue }
    if ($r.Assignment -notmatch '^[0-9A-Fa-f]{6}$') { continue }
    '{0},{1}' -f $r.Assignment.ToUpperInvariant(), $name
}
$lines = $lines | Sort-Object -Unique
Set-Content -Path $out -Value $lines -Encoding UTF8
Write-Host ("Wrote {0:N0} prefixes to {1}" -f $lines.Count, (Resolve-Path $out))
