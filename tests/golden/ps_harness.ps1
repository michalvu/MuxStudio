# ============================================================================
#  Chay HAM GOC trong reference/*.bat voi du lieu tu file JSON, ghi ket qua ra JSON.
#  Dung cho tests/test_golden_powershell.py (doi chieu ban Python voi ban .bat).
#  Chi nap dinh nghia ham bang AST - KHONG chay phan chinh cua .bat.
# ============================================================================
param([string]$BatDir, [string]$InFile, [string]$OutFile)
$ErrorActionPreference = 'Stop'

function Import-Bat([string]$bat, [string[]]$vars) {
    $src = [System.IO.File]::ReadAllText($bat)
    $ps  = $src.Substring($src.IndexOf('#>') + 2)
    $tok = $null; $err = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseInput($ps, [ref]$tok, [ref]$err)
    $out = @()
    foreach ($st in $ast.EndBlock.Statements) {
        if ($st -is [System.Management.Automation.Language.FunctionDefinitionAst]) { $out += $st.Extent.Text; continue }
        if ($st -is [System.Management.Automation.Language.AssignmentStatementAst]) {
            $name = $st.Left.Extent.Text.TrimStart('$')
            if ($vars -contains $name) { $out += $st.Extent.Text }
        }
    }
    return ($out -join "`n`n")
}

function D($a) { return @($a | ForEach-Object { [double]$_ }) }

$cases = Get-Content -LiteralPath $InFile -Raw | ConvertFrom-Json
$results = @{}
$byBat = $cases | Group-Object -Property bat

foreach ($grp in $byBat) {
    $batPath = Join-Path $BatDir $grp.Name
    $defs = Import-Bat $batPath @('script:FpsRatios', 'script:SrtBlockPat', 'NoiseWords', 'StopWords', 'script:NoiseWords', 'script:StopWords')
    $res = & {
        . ([scriptblock]::Create($defs))
        $FpsSampleRef = 400; $FpsSampleTgt = 100; $PgsTimingText = '*'; $MaxZeroStartCues = 2
        # Bien cau hinh SubPrep (gia tri mac dinh cua .bat) cho Remove-LeadingExtra / Test-SubHealth / Resolve-Ref
        $TrimLeadingExtra = $true; $MinVerifyVotes = 20; $TitleMinScore = 0.5; $TitleMinMargin = 0.15
        $KnownLangTags = @('vi','cs','cz','en','ja','ko','zh','fr','de','es','th')
        $ScanSubFolders = $true; $VideoExts = @('.mkv','.mp4','.avi','.m4v','.ts','.mov'); $SubExts = @('.srt','.ass','.ssa','.vtt')
        $ExtractFolder = 'srt_goc'; $SubFolderName = 'srt_can_sync'; $RefExcludeFolders = @(); $PgsTimingFallback = $true
        function W { }   # tat log console cua .bat
        $local = @{}
        foreach ($c in $grp.Group) {
            $a = $c.args
            $r = switch ($c.fn) {
                'Get-EvenSample'      { ,@(Get-EvenSample (D $a[0]) ([int]$a[1])) }
                'Get-OffsetVote'      { Get-OffsetVote (D $a[0]) (D $a[1]) }
                'Get-TimingRelation'  { Get-TimingRelation (D $a[0]) (D $a[1]) }
                'Get-QuickOffset'     { Get-QuickOffset (D $a[0]) (D $a[1]) }
                'ConvertTo-Double'    { ConvertTo-Double $a[0] }
                'Get-TitleTokens'     { ,@(Get-TitleTokens ([string]$a[0])) }
                'Get-Tokens'          { ,@(Get-Tokens ([string]$a[0])) }
                'Get-TitleScore'      { Get-TitleScore @(Get-TitleTokens ([string]$a[0])) @(Get-TitleTokens ([string]$a[1])) }
                'Get-CueTimes'        { $t = Get-CueTimes ([string]$a[0]); if ($t) { ,@($t) } else { $null } }
                'Convert-PgsToTimingSrt' { $n = Convert-PgsToTimingSrt ([string]$a[0]) ([string]$a[1]); $n }
                'Format-SrtStamp'     { Format-SrtStamp ([double]$a[0]) }
                'Get-EpisodeInfo'     { Get-EpisodeInfo ([string]$a[0]) }
                'Split-EpisodeName'   { Split-EpisodeName ([string]$a[0]) }
                'Split-ReleaseName'   { Split-ReleaseName ([string]$a[0]) }
                'Get-SafeFileName'    { Get-SafeFileName ([string]$a[0]) }
                'Get-NormTitle'       { Get-NormTitle ([string]$a[0]) }
                'Get-LangCode'        { Get-LangCode ([string]$a[0]) ([string]$a[1]) }
                'Get-SubEncodingKind' { Get-SubEncodingKind ([string]$a[0]) }
                'Get-ChannelName'     { Get-ChannelName $a[0] }
                'Test-EpisodeConflict' { Test-EpisodeConflict ([string]$a[0]) ([string]$a[1]) }
                'Read-SrtBlocks'      { $b = Read-SrtBlocks ([string]$a[0]); if ($b) { ,@($b | ForEach-Object { @{ S = $_.Start; E = $_.End; T = $_.Text } }) } else { $null } }
                'Remove-LeadingExtra' { $cut = 0; $ok = Remove-LeadingExtra ([string]$a[0]) (D $a[1]) ([ref]$cut); @{ Ok = [bool]$ok; Cut = $cut } }
                'Repair-CollapsedCues' { $fix = 0; $ok = Repair-CollapsedCues ([string]$a[0]) ([ref]$fix); @{ Ok = [bool]$ok; Fix = $fix } }
                'Test-SubHealth'      {
                    $rt = if ($null -ne $a[1]) { D $a[1] } else { $null }
                    $st = if ($null -ne $a[4]) { D $a[4] } else { $null }
                    $h = Test-SubHealth ([string]$a[0]) $rt ([double]$a[2]) ([int]$a[3]) $st
                    @{ Level = $h.Level; Cues = $h.Cues; ShiftFirst = $h.ShiftFirst; ShiftLast = $h.ShiftLast
                       ResOffset = $h.ResOffset; ResVotes = $h.ResVotes; ResFps = $h.ResFps; Flags = @($h.Flags) }
                }
                'Resolve-Ref'         {
                    # a: [thu muc phim, 'video'|'sub', so phu de, [ten phu de...]]
                    $targetDir = [string]$a[0]; $refDir = $targetDir; $subDir = Join-Path $targetDir $SubFolderName
                    $script:SubFileCount = [int]$a[2]
                    $idx = Get-RefIndex ([string]$a[1])
                    ,@(@($a[3]) | ForEach-Object { $r = Resolve-Ref $idx ([string]$_); if ($r) { [string]$r.Canon } else { '' } })
                }
                default { throw "khong biet ham $($c.fn)" }
            }
            $local[[string]$c.id] = $r
        }
        $local
    }
    foreach ($k in $res.Keys) { $results[$k] = $res[$k] }
}
$results | ConvertTo-Json -Depth 6 -Compress | Set-Content -LiteralPath $OutFile -Encoding UTF8
