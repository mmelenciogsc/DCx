[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("InspectPath")]
    [string]$Operation,

    [Parameter(Mandatory = $true)]
    [string]$TargetPath
)

$ErrorActionPreference = "Stop"

switch ($Operation) {
    "InspectPath" {
        $item = Get-Item -LiteralPath $TargetPath -Force
        [pscustomobject]@{
            Name = $item.Name
            Kind = if ($item.PSIsContainer) { "Directory" } else { "File" }
            Length = if ($item.PSIsContainer) { $null } else { $item.Length }
            LastWriteTimeUtc = $item.LastWriteTimeUtc.ToString("O")
        } | ConvertTo-Json -Compress
    }
}
