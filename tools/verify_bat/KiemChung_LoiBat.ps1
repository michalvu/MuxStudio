# ============================================================================
#  KiemChung_LoiBat.ps1  -  Chung minh cac loi tim thay khi doc 3 file .bat
#  (dung trong docs/SPEC.md, muc "Thay doi so voi .bat").
#
#  Script CHI nap dinh nghia ham tu file .bat (bang bo phan tich cu phap cua
#  PowerShell), KHONG chay phan chinh, KHONG goi mang, KHONG doc key.
#  File tam ghi vao thu muc TEMP roi xoa.
#
#  Cach chay tren Windows (PowerShell 5.1 co san):
#    powershell -NoProfile -ExecutionPolicy Bypass -File KiemChung_LoiBat.ps1 -BatDir "C:\duong\dan\toi\thu muc .bat"
# ============================================================================
param([Parameter(Mandatory = $true)][string]$BatDir)

$ErrorActionPreference = 'Stop'

function Find-Bat([string]$prefix) {
    $f = @(Get-ChildItem -LiteralPath $BatDir -File | Where-Object { $_.Name -like "*$prefix*.bat" } | Sort-Object Name)
    if ($f.Count -eq 0) { throw "Khong tim thay *$prefix*.bat trong $BatDir" }
    return $f[0].FullName
}
$SF = Find-Bat 'SubFetch'
$SP = Find-Bat 'SubPrep'
$MT = Find-Bat 'MuxTool'

# Nap dinh nghia ham tu phan PowerShell (sau dong '#>') bang AST.
function Import-BatFunctions([string]$bat, [string[]]$names) {
    $src = [System.IO.File]::ReadAllText($bat)
    $ps  = $src.Substring($src.IndexOf('#>') + 2)
    $tok = $null; $err = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseInput($ps, [ref]$tok, [ref]$err)
    $fns = $ast.FindAll({ param($a) $a -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $false)
    $out = @()
    foreach ($f in $fns) { if ($names -contains $f.Name) { $out += $f.Extent.Text } }
    return ($out -join "`n`n")
}
function Invoke-In([string]$code, [string]$call) { return & ([scriptblock]::Create($code + "`n" + $call)) }
function Show-Obj($o) {
    if ($null -eq $o) { return '(null)' }
    return (($o.PSObject.Properties | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join '; ')
}
function Q([string]$s) { return "'" + ($s -replace "'", "''") + "'" }

$tmp = Join-Path $env:TEMP ('kiemchung_' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $tmp -Force | Out-Null

try {
    # ------------------------------------------------------------------
    "=== C1. Format-SrtStamp: [int]TotalHours LAM TRON -> sai gio (ca 3 file) ==="
    $code = Import-BatFunctions $SP @('Format-SrtStamp')
    foreach ($s in @(1799.0, 2700.0, 3599.9, 5400.0, 6000.25)) {
        "  {0,8} giay -> {1}" -f $s, (Invoke-In $code "Format-SrtStamp $s")
    }
    "  (dung phai la 00:29:59 / 00:45:00 / 00:59:59 / 01:30:00 / 01:40:00)"

    "`n=== C1b. Hau qua: Convert-PgsToTimingSrt (moc goc tu PGS) ==="
    $code = Import-BatFunctions $SF @('Format-SrtStamp', 'Convert-PgsToTimingSrt')
    $all = New-Object System.Collections.Generic.List[byte]
    foreach ($t in @(100, 1900, 2700, 4000)) {
        foreach ($pair in @(@($t, 1), @(($t + 2), 0))) {
            $pts = [uint32][math]::Round($pair[0] * 90000)
            $b = New-Object byte[] 24
            $b[0] = 0x50; $b[1] = 0x47
            $b[2] = ($pts -shr 24) -band 0xFF; $b[3] = ($pts -shr 16) -band 0xFF
            $b[4] = ($pts -shr 8) -band 0xFF;  $b[5] = $pts -band 0xFF
            $b[10] = 0x16; $b[12] = 11; $b[21] = 0; $b[23] = [byte]$pair[1]
            $all.AddRange($b)
        }
    }
    $sup = Join-Path $tmp 'x.sup'; $srt = Join-Path $tmp 'x.srt'
    [System.IO.File]::WriteAllBytes($sup, $all.ToArray())
    $n = Invoke-In $code "Convert-PgsToTimingSrt $(Q $sup) $(Q $srt)"
    "  doc duoc $n dong, PGS hien o 100s / 1900s / 2700s / 4000s:"
    Get-Content -LiteralPath $srt | Where-Object { $_ -match '-->' } | ForEach-Object { "    $_" }
    "  (dung phai la 00:01:40 / 00:31:40 / 00:45:00 / 01:06:40)"

    # ------------------------------------------------------------------
    "`n=== C2. Nhan dien so tap: ba ban khac nhau ==="
    $sfc = Import-BatFunctions $SF @('Get-EpisodeInfo', 'Test-EpisodeConflict')
    $spc = Import-BatFunctions $SP @('Get-EpisodeInfo')
    $mtc = Import-BatFunctions $MT @('Split-EpisodeName', 'Split-ReleaseName')
    foreach ($nm in @('One Piece - 0643', '[Judas] One Piece - 1085 [1080p]', 'Naruto EP 055',
                      '1917 - 2019', 'Show.S01E05E06', 'Show Season 2 Episode 5', 'One Piece - 1085[1080p]')) {
        "  $nm"
        "     SubFetch : $(Show-Obj (Invoke-In $sfc "Get-EpisodeInfo $(Q $nm)"))"
        "     SubPrep  : $(Show-Obj (Invoke-In $spc "Get-EpisodeInfo $(Q $nm)"))"
        "     MuxTool  : $(Show-Obj (Invoke-In $mtc "Split-EpisodeName $(Q $nm)"))"
    }
    "  SubFetch Test-EpisodeConflict('One Piece - 0643','One Piece - 0644') = " +
        (Invoke-In $sfc "Test-EpisodeConflict 'One Piece - 0643' 'One Piece - 0644'") + "  (dung phai la True)"

    "`n=== C3. Tach ten phim + nam ==="
    $sfc2 = Import-BatFunctions $SF @('Get-CleanTitle', 'Get-Year')
    foreach ($nm in @('Blade Runner 2049', '2001 A Space Odyssey', '2001.A.Space.Odyssey.1968.1080p', 'Uncut.Gems.2019.1080p', "Charlotte's.Web.2006.720p")) {
        $a = Invoke-In $mtc "Split-ReleaseName $(Q $nm)"
        "  {0,-34} MuxTool: '{1}' ({2})   SubFetch: '{3}' ({4})" -f $nm, $a.Title, $a.Year,
            (Invoke-In $sfc2 "Get-CleanTitle $(Q $nm)"), (Invoke-In $sfc2 "Get-Year $(Q $nm)")
    }

    # ------------------------------------------------------------------
    "`n=== C4. SubPrep Test-LangMatch: thu tu -and/-or ==="
    $code = Import-BatFunctions $SP @('Test-LangMatch')
    foreach ($c in @(@('cze cs', 'cs'), @('cze ', 'cs'), @('ger', 'de'), @('jpn', 'ja'), @('chi', 'zh'), @('spa', 'es'), @('eng en', 'en'))) {
        "  tag={0,-8} muon={1} -> {2}" -f (Q $c[0]), $c[1], (Invoke-In $code "Test-LangMatch $(Q $c[0]) $(Q $c[1])")
    }
    "  (Invoke-ExtractRefSubs truyen vao chuoi '`$language `$language_ietf', vd 'cze cs')"

    # ------------------------------------------------------------------
    "`n=== C5. Lam tron [int] = lam tron ve so chan (can port dung) ==="
    "  [int]2.5=$([int]2.5)  [int]3.5=$([int]3.5)  [int](45*0.30)=$([int](45*0.30))  [math]::Round(0.225/0.15)=$([math]::Round(0.225/0.15))"
}
finally {
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
