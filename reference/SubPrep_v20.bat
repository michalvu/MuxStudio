<# :
@echo off
chcp 65001 >nul
cls
set "WORK_DIR=%~dp0"
set "SCRIPT_PATH=%~f0"
cd /d "%WORK_DIR%"

powershell -NoProfile -ExecutionPolicy Bypass -Command "$sp=$env:SCRIPT_PATH; $src=Get-Content -LiteralPath $sp -Raw -Encoding UTF8; Invoke-Command -ScriptBlock ([scriptblock]::Create($src)) -ArgumentList $env:WORK_DIR"

echo.
pause
exit /b
#>

param([string]$targetDir)

$ErrorActionPreference = 'Continue'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

# ==========================================================
#  CAU HINH
# ==========================================================
$SubFolderName  = 'srt_can_sync'    # phu de tai tren mang ve, can doi ten / sync
$RefFolderName  = ''         # file moc (VIDEO .mkv hoac phu de). De '' = dung chinh thu muc chua .bat
$OutFolderName  = ''                # '' = ghi thang canh file moc (khuyen dung). Hoac 'srt_hoan_thanh'
$AlassDir       = 'C:\Users\fenha\Desktop\alass-windows64\bin\alass'                # '' = tu do. Hoac tro thang: 'C:\Users\fenha\Desktop\alass'
$MkvToolNixDir  = 'C:\Users\fenha\Desktop\mkvtoolnix'   # de trich phu de goc tu file .mkv

# --- Trich phu de goc tu file phim de lam moc thoi gian ---
$ExtractFolder    = 'srt_goc'          # noi chua phu de vua trich (KHONG de trong)

# Khi $RefFolderName = '' thi thu muc goc duoc quet DE QUY de tim moc.
# Cac thu muc lam viec duoi day PHAI bi loai tru, neu khong:
#  - phu de trong srt_pool se bi coi la "moc" -> doi ten sai theo ten file trong kho
#  - kho co sub cua nhieu tap -> sinh nhap nhang -> bo qua hang loat file
$RefExcludeFolders = @('srt_pool', 'srt_can_sync', 'srt_hoan_thanh',
                       '_subprep_logs', '_subfetch_logs', '_subsource_probe', 'Output')
$RefSubLangs      = @('en','vi','cs')  # thu tu uu tien ngon ngu khi chon track de trich
$SkipExistingRef  = $true              # da trich roi thi bo qua
# MKV chi co phu de dang anh (PGS .sup)? Doc thang moc thoi gian tu do, KHONG can OCR.
$PgsTimingFallback = $true
$PgsTimingText     = '*'               # noi dung moi dong (chi la cho giu cho, khong ai doc)
# Ket qua sync LUON ghi canh file phim (dat $OutFolderName neu muon doi cho khac)

$VideoExts      = @('.mkv','.mp4','.avi','.m4v','.ts','.mov')
$SubExts        = @('.srt','.ass','.ssa','.vtt')
$KnownLangTags  = @('vi','cs','cz','en','ja','ko','zh','fr','de','es','th')

$ScanSubFolders = $true             # quet ca thu muc con (nhieu season mot luot)
$PreviewFirst   = $true             # xem truoc va xac nhan roi moi doi ten
$SkipExistingOut= $true             # bo qua file da sync roi
$AlassExtraArgs = @()               # vd: @('--split-penalty','7') hoac @('--encoding-inc','UTF-8')

# --- Chon audio track lam moc thoi gian ---
# 'ask'   : hoi moi lan chay (mac dinh)
# 'auto'  : de ffmpeg/alass tu lay audio mac dinh cua file
# 'index' : dung audio thu $SyncAudioIndex (dem tu 0)
# 'lang'  : dung audio co ngon ngu $SyncAudioLang
$SyncAudioMode  = 'ask'
# --- An toan khi dong bo ---
# $MaxShiftSeconds: neu alass dich phu de qua nguong nay -> coi la SAI, giu nguyen file goc.
#                   Dat 0 de tat kiem tra. 30 giay la nguong hop ly cho phu de tai tren mang.
$MaxShiftSeconds = 130
# Cac tham so giup alass than trong hon voi phu de VON DA DUNG.
# Neu alass bao "unknown option", xoa bot phan tu trong mang nay.
# Khi moc la PHU DE (sub-vs-sub): de TRONG = dung mac dinh cua alass, giong het script cu.
# alass duoc phep cat doan va sua framerate - do la diem manh cua no khi doi chieu sub voi sub.
$AlassArgsSubMode   = @()
# Khi moc la AUDIO/VIDEO: VAD de doan nham nhac thanh giong noi, nen siet lai.
$AlassArgsAudioMode = @('--no-split','--disable-fps-guessing') 
$SyncAudioIndex = 0
$SyncAudioLang  = 'en'
$LogDirName     = '_subprep_logs'   # noi luu nhat ky tung lan chay (dung cho hoan tac nhieu cap)
$NormalizeEncoding = $true          # tu chuyen phu de ve UTF-8 truoc khi xu ly
$KeepUndoRuns   = 20                # giu lai toi da bao nhieu nhat ky
$TitleMinScore  = 0.5               # diem giong nhau toi thieu de nhan la cung phim
$TitleMinMargin = 0.15              # khoang cach toi thieu voi ung vien nhi (chong nham)

# --- Kiem tra va sua loi sau khi dong bo (moi o v15) ---
$MinVerifyVotes   = 20              # so moc khop toi thieu de tin ket qua do
$TrimLeadingExtra = $true           # cat doan recap dau file khong co trong phim
$MaxZeroStartCues = 2               # tren muc nay coi la khoi bi don ve 00:00:00
$HealthReport     = $true           # xuat CSV bao cao suc khoe
$UseColors        = $true           # mau 24-bit + khung Unicode
# --- Phan tich FPS / do lech truoc khi dong bo ---
$AnalyzeTiming  = $true             # bat phan tich (tat neu thay cham)
$FpsSampleTgt   = 100               # so moc lay mau ben phu de can sua
$FpsSampleRef   = 400               # so moc lay mau ben phu de goc
$FpsAutoAllowGuess = $true          # phat hien lech FPS -> tu bo --disable-fps-guessing cho file do
$AskCleanup     = $true             # hoi don dep file nguon sau khi xong
$DeleteToRecycleBin = $true         # xoa vao Thung rac (khoi phuc duoc) thay vi xoa han

# ==========================================================
#  LOP GIAO DIEN  -  mau 24-bit + khung Unicode
# ==========================================================
$script:UseAnsi = $false

# Windows 10+ ho tro chuoi VT (mau RGB, chu dam/mo) nhung phai bat co.
# Cung API voi viec tat QuickEdit.
function Initialize-Console {
    try {
        if (-not ('Win32.ConHelper' -as [type])) {
            Add-Type -Name ConHelper -Namespace Win32 -MemberDefinition @'
[DllImport("kernel32.dll", SetLastError = true)]
public static extern IntPtr GetStdHandle(int nStdHandle);
[DllImport("kernel32.dll", SetLastError = true)]
public static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);
[DllImport("kernel32.dll", SetLastError = true)]
public static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
'@
        }
        # Bat mau 24-bit tren luong RA
        $hOut = [Win32.ConHelper]::GetStdHandle(-11)
        $m = 0
        if ([Win32.ConHelper]::GetConsoleMode($hOut, [ref]$m)) {
            if ([Win32.ConHelper]::SetConsoleMode($hOut, ($m -bor 0x0004))) { $script:UseAnsi = $true }
        }
        # Tat QuickEdit tren luong VAO: nhap chuot se khong lam treo tien trinh
        $hIn = [Win32.ConHelper]::GetStdHandle(-10)
        $mi = 0
        if ([Win32.ConHelper]::GetConsoleMode($hIn, [ref]$mi)) {
            [void][Win32.ConHelper]::SetConsoleMode($hIn, (($mi -band -bnot 0x0040) -bor 0x0080))
        }
    } catch { }
    if (-not $UseColors) { $script:UseAnsi = $false }
    return $script:UseAnsi
}

# Bang mau: ten cu (16 mau console) -> RGB diu mat hon
$script:Pal = @{
    'Black'       = '30;30;35'
    'DarkGray'    = '125;130;140'
    'Gray'        = '200;203;210'
    'White'       = '245;246;250'
    'Cyan'        = '86;204;242'
    'DarkCyan'    = '58;140;170'
    'Green'       = '106;214;130'
    'DarkGreen'   = '72;160;100'
    'Yellow'      = '244;203;92'
    'DarkYellow'  = '206;154;62'
    'Red'         = '242;108;108'
    'DarkRed'     = '190;70;70'
    'Magenta'     = '214;130;222'
    'DarkMagenta' = '160;96;170'
    'Blue'        = '114;155;245'
    'DarkBlue'    = '78;110;190'
}

$script:ESC = [char]27

function W($m, $c = 'Gray') {
    if ($script:UseAnsi -and $script:Pal.ContainsKey([string]$c)) {
        Write-Host ("$($script:ESC)[38;2;$($script:Pal[$c])m$m$($script:ESC)[0m")
    } else {
        Write-Host $m -ForegroundColor $c
    }
}

# Ky tu ke khung. Console khong doc duoc Unicode thi lui ve ASCII.
$script:Bx = @{
    TL='+'; TR='+'; BL='+'; BR='+'; H='-'; V='|'; LT='+'; RT='+'
}
function Initialize-BoxChars {
    try {
        if ([Console]::OutputEncoding.CodePage -eq 65001) {
            $script:Bx = @{
                TL=[char]0x250C; TR=[char]0x2510; BL=[char]0x2514; BR=[char]0x2518
                H =[char]0x2500; V =[char]0x2502; LT=[char]0x251C; RT=[char]0x2524
            }
        }
    } catch { }
}

function Get-BoxLine([int]$w, [string]$kind = 'mid') {
    $h = [string]$script:Bx.H * $w
    switch ($kind) {
        'top' { return "$($script:Bx.TL)$h$($script:Bx.TR)" }
        'bot' { return "$($script:Bx.BL)$h$($script:Bx.BR)" }
        default { return "$($script:Bx.LT)$h$($script:Bx.RT)" }
    }
}

# Windows bat san "QuickEdit Mode": nhap chuot vao cua so den la TAM DUNG
# tien trinh cho den khi nhan Enter/Esc. Rat de nham va trong nhu bi treo.
function Disable-QuickEdit {
    try {
        if (-not ('ConHelper' -as [type])) {
            Add-Type -Name ConHelper -Namespace Win32 -MemberDefinition @'
[DllImport("kernel32.dll", SetLastError = true)]
public static extern IntPtr GetStdHandle(int nStdHandle);
[DllImport("kernel32.dll", SetLastError = true)]
public static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);
[DllImport("kernel32.dll", SetLastError = true)]
public static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
'@
        }
        $h = [Win32.ConHelper]::GetStdHandle(-10)      # STD_INPUT_HANDLE
        $m = 0
        if ([Win32.ConHelper]::GetConsoleMode($h, [ref]$m)) {
            $new = ($m -band -bnot 0x0040) -bor 0x0080  # bo QUICK_EDIT, giu EXTENDED_FLAGS
            [void][Win32.ConHelper]::SetConsoleMode($h, $new)
            return $true
        }
    } catch { }
    return $false
}

# ---------- NHAT KY (lo hong 2, 3, 4) ----------
$script:RunLog = @()
$script:SubFileCount = 0
$script:Processed = @()      # cac cap da dong bo thanh cong, dung cho buoc don dep
$script:Health = @()        # ket qua kiem tra suc khoe tung file
$script:SyncIdx = 0
$script:TagConflict = 0
function Add-RunLog([string]$action, [string]$from, [string]$to, [string]$detail) {
    $script:RunLog += [pscustomobject]@{
        Time = (Get-Date -Format 'HH:mm:ss'); Action = $action
        From = $from; To = $to; Detail = $detail
    }
}
function Save-RunLog([string]$root) {
    if ($script:RunLog.Count -eq 0) { return $null }
    $dir = Join-Path $root $LogDirName
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $path = Join-Path $dir ("run_" + (Get-Date -Format 'yyyyMMdd_HHmmss') + ".csv")
    $script:RunLog | Export-Csv -LiteralPath $path -NoTypeInformation -Encoding UTF8
    # Don bot nhat ky cu
    $olds = @(Get-ChildItem -LiteralPath $dir -File -Filter 'run_*.csv' | Sort-Object Name -Descending)
    if ($olds.Count -gt $KeepUndoRuns) {
        $olds[$KeepUndoRuns..($olds.Count - 1)] | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue }
    }
    return $path
}

# ==========================================================
#  CONG CU DUNG CHUNG  (bo sung o v15)
# ==========================================================
# Doc so thap phan KHONG phu thuoc vung mien cua Windows.
# Windows dat vung VN/Sec dung dau PHAY -> "0.500" parse that bai, tra ve 0.
function ConvertTo-Double($v) {
    if ($null -eq $v) { return 0.0 }
    $s = ([string]$v).Trim()
    if (-not $s) { return 0.0 }
    $d = 0.0
    if ([double]::TryParse($s, [System.Globalization.NumberStyles]::Float,
                           [System.Globalization.CultureInfo]::InvariantCulture, [ref]$d)) { return $d }
    if ([double]::TryParse(($s -replace ',', '.'), [System.Globalization.NumberStyles]::Float,
                           [System.Globalization.CultureInfo]::InvariantCulture, [ref]$d)) { return $d }
    return 0.0
}

# ---------- DOC / GHI KHOI SRT ----------
$script:SrtBlockPat = [regex]'(?m)^\s*(\d+)\s*\r?\n(\d{1,2}:\d{2}:\d{2},\d{3})\s*-->\s*(\d{1,2}:\d{2}:\d{2},\d{3})([^\r\n]*)\r?\n([\s\S]*?)(?=\r?\n\s*\r?\n|\Z)'

function ConvertFrom-Stamp([string]$t) {
    $p = $t.Split(':'); $x = $p[2].Split(',')
    return [int]$p[0] * 3600 + [int]$p[1] * 60 + [int]$x[0] + (ConvertTo-Double ("0." + $x[1]))
}

function Read-SrtBlocks([string]$path) {
    try { $txt = [System.IO.File]::ReadAllText($path) } catch { return $null }
    if (-not $txt) { return $null }
    $out = New-Object 'System.Collections.Generic.List[object]'
    foreach ($m in $script:SrtBlockPat.Matches($txt)) {
        $out.Add([pscustomobject]@{
            Start = ConvertFrom-Stamp $m.Groups[2].Value
            End   = ConvertFrom-Stamp $m.Groups[3].Value
            Tail  = $m.Groups[4].Value
            Text  = $m.Groups[5].Value.TrimEnd()
        })
    }
    if ($out.Count -eq 0) { return $null }
    return $out
}

function Write-SrtBlocks([string]$path, $blocks) {
    $sb = New-Object System.Text.StringBuilder
    $i = 0
    foreach ($b in $blocks) {
        $i++
        [void]$sb.AppendLine([string]$i)
        [void]$sb.AppendLine("$(Format-SrtStamp $b.Start) --> $(Format-SrtStamp $b.End)$($b.Tail)")
        [void]$sb.AppendLine($b.Text)
        [void]$sb.AppendLine('')
    }
    try {
        [System.IO.File]::WriteAllText($path, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
        return $true
    } catch { return $false }
}

# ---------- CAT KHOI THUA O DAU FILE (recap tap truoc) ----------
# Phu de lam cho ban phat song thuong co doan "tom tat tap truoc" ma file phim
# khong co. Khong phep dich nao lam no dung duoc -> phai CAT truoc khi sync,
# neu khong alass se lay ca khoi do lam moc va can sai toan bo file.
function Remove-LeadingExtra([string]$path, $refTimes, [ref]$cutCount) {
    $cutCount.Value = 0
    if (-not $TrimLeadingExtra -or -not $refTimes) { return $false }
    $blocks = Read-SrtBlocks $path
    if (-not $blocks -or $blocks.Count -lt 40) { return $false }

    # Tinh quan he thoi gian tren 70% CUOI (phan nay khong dinh recap)
    $skip = [int]($blocks.Count * 0.30)
    $tail = @($blocks | Select-Object -Skip $skip | ForEach-Object { $_.Start })
    $rel  = Get-TimingRelation $refTimes $tail
    if (-not $rel -or $rel.Votes -lt $MinVerifyVotes) { return $false }

    # Bang bam theo o 1 giay: tra cuu O(1) thay vi duyet ca mang moc goc.
    # (Ban cu duyet tuyen tinh -> hang trieu phep so sanh, nhin nhu treo may.)
    $bins = New-Object 'System.Collections.Generic.HashSet[int]'
    foreach ($r in $refTimes) { [void]$bins.Add([int][math]::Floor($r)) }

    $firstGood = -1
    $limit = [math]::Min($blocks.Count - 5, [int]($blocks.Count * 0.30))
    for ($i = 0; $i -le $limit; $i++) {
        $run = 0
        for ($j = $i; $j -lt [math]::Min($i + 5, $blocks.Count); $j++) {
            $x = $blocks[$j].Start * $rel.Scale + $rel.Offset
            $c = [int][math]::Floor($x)
            # dung sai +-1.5s -> chi can kiem 4 o lien ke
            if ($bins.Contains($c) -or $bins.Contains($c-1) -or
                $bins.Contains($c+1) -or $bins.Contains($c-2)) { $run++ } else { break }
        }
        if ($run -ge 5) { $firstGood = $i; break }
    }
    if ($firstGood -le 0) { return $false }

    $keep = @($blocks | Select-Object -Skip $firstGood)
    if ($keep.Count -lt ($blocks.Count * 0.6)) { return $false }   # cat qua nhieu -> nghi ngo, thoi

    if (Write-SrtBlocks $path $keep) { $cutCount.Value = $firstGood; return $true }
    return $false
}

# ---------- SUA KHOI BI DON VE 00:00:00 ----------
# Khi alass cat doan roi day mot khoi ve truoc moc 0, thoi gian am bi kep ve 0
# -> hang chuc dong cung hien mot luc o dau phim.
function Repair-CollapsedCues([string]$path, [ref]$fixCount) {
    $fixCount.Value = 0
    $blocks = Read-SrtBlocks $path
    if (-not $blocks) { return $false }
    $zero = @($blocks | Where-Object { $_.Start -lt 0.05 })
    if ($zero.Count -le $MaxZeroStartCues) { return $false }
    $keep = @($blocks | Where-Object { $_.Start -ge 0.05 })
    if ($keep.Count -lt 10) { return $false }
    if (Write-SrtBlocks $path $keep) { $fixCount.Value = $zero.Count; return $true }
    return $false
}

# ---------- THOI LUONG PHIM ----------
function Get-VideoDuration([string]$video) {
    if (-not $mkvmerge -or -not $video) { return 0 }
    try {
        $raw = & $mkvmerge -J "$video" 2>&1
        if ($LASTEXITCODE -gt 1) { return 0 }
        $j = ($raw -join "`n") | ConvertFrom-Json
        if ($j.container -and $j.container.properties -and $j.container.properties.duration) {
            return (ConvertTo-Double $j.container.properties.duration) / 1000000000.0
        }
    } catch {}
    return 0
}

# ---------- KIEM TRA SUC KHOE PHU DE SAU KHI SYNC ----------
function Test-SubHealth([string]$path, $refTimes, [double]$videoDur, [int]$cuesBefore, $srcTimes) {
    $flags = @(); $level = 0
    $shiftFirst = $null; $shiftLast = $null
    $resOffset = $null; $resVotes = -1; $resFps = ''

    $blocks = Read-SrtBlocks $path
    if (-not $blocks) {
        return [pscustomobject]@{ Level=2; Flags=@('khong doc duoc noi dung'); Cues=0
                                  ShiftFirst=$null; ShiftLast=$null; ResOffset=$null; ResVotes=-1; ResFps='' }
    }
    $n = $blocks.Count
    $outTimes = @($blocks | ForEach-Object { $_.Start })

    if ($srcTimes -and $srcTimes.Count -gt 1 -and $n -gt 1) {
        $m = [math]::Min($srcTimes.Count, $n)
        $shiftFirst = $outTimes[0] - $srcTimes[0]
        $shiftLast  = $outTimes[$m-1] - $srcTimes[$m-1]
    }

    # 1. Dong bi don ve 00:00:00
    $zero = @($blocks | Where-Object { $_.Start -lt 0.05 }).Count
    if ($zero -gt $MaxZeroStartCues) { $flags += "$zero dong dinh o 00:00:00"; $level = 2 }

    # 2. Chong lan
    $ov = 0
    for ($i = 1; $i -lt $n; $i++) { if ($blocks[$i].Start -lt $blocks[$i-1].End - 0.05) { $ov++ } }
    if ($ov -gt [math]::Max(5, $n * 0.05)) { $flags += "$ov cho chong lan"; if ($level -lt 1) { $level = 1 } }

    # 3. Mat dong so voi truoc khi sync
    if ($cuesBefore -gt 0 -and $n -lt ($cuesBefore * 0.95)) {
        $flags += "mat $($cuesBefore - $n)/$cuesBefore dong"; if ($level -lt 1) { $level = 1 }
    }

    # 4. Vuot thoi luong phim
    if ($videoDur -gt 0 -and $n -gt 0 -and $blocks[$n-1].End -gt $videoDur + 60) {
        $flags += "dong cuoi $([math]::Round($blocks[$n-1].End/60,1))p > phim $([math]::Round($videoDur/60,1))p"
        $level = 2
    }

    # 5. Doi chieu voi moc goc
    if ($refTimes) {
        $rel = Get-TimingRelation $refTimes $outTimes
        if ($rel) {
            $resOffset = $rel.Offset; $resVotes = $rel.Votes
            if ($rel.FpsMismatch) { $resFps = $rel.Name }
            if ($rel.Votes -lt $MinVerifyVotes) { $flags += "chi $($rel.Votes) moc khop"; $level = 2 }
            elseif ($rel.FpsMismatch) { $flags += "con lech FPS ($($rel.Name))"; if ($level -lt 1) { $level = 1 } }
            elseif ([math]::Abs($rel.Offset) -gt 1.0) {
                $flags += "van lech $([math]::Round($rel.Offset,2))s"; if ($level -lt 1) { $level = 1 }
            }
        }
    }

    # 6. Co gian bat thuong
    if ($null -ne $shiftFirst -and [math]::Abs($shiftLast - $shiftFirst) -gt 5.0) {
        $flags += "co gian $([math]::Round($shiftLast - $shiftFirst,1))s giua dau va cuoi"
        if ($level -lt 1) { $level = 1 }
    }

    # 7. Hong ma hoa / qua it dong
    try {
        $txt = [System.IO.File]::ReadAllText($path)
        if ($txt -match "\uFFFD") { $flags += 'ky tu hong (sai bang ma)'; if ($level -lt 1) { $level = 1 } }
    } catch {}
    if ($n -lt 50) { $flags += "chi co $n dong"; if ($level -lt 1) { $level = 1 } }

    return [pscustomobject]@{
        Level=$level; Flags=$flags; Cues=$n
        ShiftFirst=$shiftFirst; ShiftLast=$shiftLast
        ResOffset=$resOffset; ResVotes=$resVotes; ResFps=$resFps
    }
}

# ==========================================================
#  NHAN DIEN SO TAP
# ==========================================================
function Get-EpisodeInfo([string]$name) {
    $t = $name -replace '\[[0-9A-Fa-f]{8}\]', ' '
    $t = $t -replace '^\[[^\]]+\]', ' '
    $t = $t -replace '[\._]+', ' '
    $t = $t -replace '\s+', ' '

    $m = [regex]::Match($t, '(?i)\bS(\d{1,2})\s?E(\d{1,4})\b')
    if ($m.Success) { return [pscustomobject]@{ Season = [int]$m.Groups[1].Value; Episode = [int]$m.Groups[2].Value; HasSeason = $true; Confident = $true } }

    $m = [regex]::Match($t, '(?<!\d)(\d{1,2})x(\d{2})(?!\d)')
    if ($m.Success) { return [pscustomobject]@{ Season = [int]$m.Groups[1].Value; Episode = [int]$m.Groups[2].Value; HasSeason = $true; Confident = $true } }

    $m = [regex]::Match($t, '(?i)\bSeason\s?(\d{1,2})\b.*?\b(?:Episode|Ep|Tap)\s?(\d{1,4})\b')
    if ($m.Success) { return [pscustomobject]@{ Season = [int]$m.Groups[1].Value; Episode = [int]$m.Groups[2].Value; HasSeason = $true; Confident = $true } }

    $m = [regex]::Match($t, '(?i)\b(?:Episode|Ep|E|Tap)\s?(\d{1,4})\b')
    if ($m.Success) { return [pscustomobject]@{ Season = 0; Episode = [int]$m.Groups[1].Value; HasSeason = $false; Confident = $true } }

    $m = [regex]::Match($t, '\s-\s(\d{1,4})(?:v\d)?(?:\s|$)')
    if ($m.Success) { return [pscustomobject]@{ Season = 0; Episode = [int]$m.Groups[1].Value; HasSeason = $false; Confident = $true } }

    $c = $t
    $c = $c -replace '(?i)\b(19|20)\d{2}\b', ' '
    $c = $c -replace '(?i)\b(4320|2160|1440|1080|720|576|480|360)p?\b', ' '
    $c = $c -replace '(?i)\b(x|h)\s?26[45]\b', ' '
    $c = $c -replace '(?i)\b(10|8)\s?bit\b', ' '
    $c = $c -replace '\b\d\s?\.\s?\d\b', ' '
    $c = $c -replace '(?i)\b(aac|ac3|eac3|dts|flac|opus|ddp?\d?|truehd|atmos|mp3)\d*\b', ' '
    $c = $c -replace '(?i)\b(bluray|blu ray|bdrip|brrip|webrip|web dl|web|hdtv|dvdrip|remux|hevc|avc|hdr\d*|dovi|sdr|imax)\b', ' '
    $c = $c -replace '(?i)\bv\d\b', ' '
    $nums = [regex]::Matches($c, '(?<!\d)(\d{1,4})(?!\d)')
    # Tang cuoi chi la PHONG DOAN tu con so con sot lai -> khong duoc dung de ghep cap
    if ($nums.Count -gt 0) {
        return [pscustomobject]@{ Season = 0; Episode = [int]$nums[$nums.Count - 1].Value; HasSeason = $false; Confident = $false }
    }
    return $null
}

# Rut goc ten phim: bo tag ky thuat, bo nam, bo dau cau -> chuoi de so sanh
# "28.Years.Later.2025.1080p.AMZN.WEB-DL..." -> "28yearslater"
# "28 Years Later (2025) NF WEB"             -> "28yearslater"
# Tu ky thuat / ngon ngu / the loai - khong mang thong tin nhan dang phim
$script:NoiseWords = @'
bluray blu ray bdrip brrip bdremux remux webdl web dl webrip web hdtv dvdrip hdrip hddvd
x264 x265 h264 h265 hevc avc xvid aac ac3 eac3 dd ddp dts dtshd truehd atmos flac opus mp3 mp4 mkv avi
hdr dovi sdr imax proper repack extended uncut unrated remastered criterion multi dual vietsub subs sub
subtitles bit amzn nf dsnp hmax atvp hulu pcok itunes internal uhd 4k dolby vision ma
eng rus vie viet vietnamese cze czech ces cz cs en fra ger spa ita jpn kor chi
action drama comedy horror thriller adventure animation
'@ -split '\s+' | Where-Object { $_ }

$script:StopWords = @('the','of','a','an','and','in','on','at','to','for')

# Tach ten file thanh tap tu khoa nhan dang phim
function Get-TitleTokens([string]$name) {
    $t = $name -replace '\([^\)]*\)', ' '
    $t = $t -replace '\[[^\]]*\]', ' '
    $out = @()
    foreach ($w in ($t -split '[^A-Za-z0-9]+')) {
        if (-not $w) { continue }
        $lw = $w.ToLower()
        if ($lw.Length -lt 2) { continue }
        if ($lw -match '^\d+$') { continue }          # so thu tu, nam, ...
        if ($lw -match '^\d+p$') { continue }         # 1080p, 720p
        if ($script:StopWords -contains $lw) { continue }
        if ($script:NoiseWords -contains $lw) { continue }
        $out += $lw
    }
    return @($out | Select-Object -Unique)
}

function Get-TitleYear([string]$name) {
    $m = [regex]::Match($name, '\b((?:19|20)\d{2})\b')
    if ($m.Success) { return [int]$m.Groups[1].Value }
    return $null
}

# Diem giong nhau giua hai tap tu khoa: so tu chung / so tu cua ben nhieu hon
function Get-TitleScore($a, $b) {
    if ($a.Count -eq 0 -or $b.Count -eq 0) { return 0.0 }
    $common = 0
    foreach ($x in $a) { if ($b -contains $x) { $common++ } }
    return [double]$common / [math]::Max($a.Count, $b.Count)
}

function Get-TitleKey([string]$name) {
    $t = $name -replace '\[[^\]]*\]', ' '
    $t = $t -replace '\([^\)]*\)', ' '
    $t = $t -replace '[\._]+', ' '
    $t = $t -replace '\s+', ' '
    $junk = '(?i)\b(4320p|2160p|1440p|1080p|720p|576p|480p|360p|4k|uhd|bluray|blu ray|bdrip|brrip|bdremux|remux|web-?dl|webrip|web|hdtv|dvdrip|hdrip|x ?26[45]|h ?26[45]|hevc|avc|xvid|aac\d*|ac3|eac3|ddp?\d?|dts(-hd)?|truehd|atmos|flac|opus|mp3|hdr\d*|dovi|sdr|imax|proper|repack|extended|uncut|unrated|remastered|criterion|multi|dual|vietsub|thuyet minh|10 ?bit|8 ?bit|amzn|nf|dsnp|hmax|atvp|hulu|pcok|itunes|internal|repack)\b'
    $m = [regex]::Match($t, $junk)
    if ($m.Success) { $t = $t.Substring(0, $m.Index) }
    $t = $t -replace '(?i)\b(19|20)\d{2}\b', ' '
    return (($t -replace '[^A-Za-z0-9]', '').ToLower())
}

function Get-EpLabel($info) {
    if (-not $info) { return '?' }
    if ($info.HasSeason) { return ("S{0:D2}E{1:D2}" -f $info.Season, $info.Episode) }
    return ("E{0:D2}" -f $info.Episode)
}

# Tach hau to ngon ngu: "Phim.S01E01.vi" -> Base="Phim.S01E01", Tag="vi"
function Split-LangTag([string]$base) {
    $m = [regex]::Match($base, '\.([A-Za-z]{2,3})$')
    if ($m.Success -and ($KnownLangTags -contains $m.Groups[1].Value.ToLower())) {
        return [pscustomobject]@{ Base = $base.Substring(0, $m.Index); Tag = $m.Groups[1].Value.ToLower() }
    }
    return [pscustomobject]@{ Base = $base; Tag = '' }
}

# ==========================================================
#  MA HOA KY TU  (lo hong 1)
# ==========================================================
# Nhan dien kieu ma hoa cua mot file van ban
function Get-SubEncodingKind([string]$path) {
    try { $bytes = [System.IO.File]::ReadAllBytes($path) } catch { return 'error' }
    if ($bytes.Length -eq 0) { return 'empty' }
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { return 'utf8bom' }
    if ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) { return 'utf16le' }
    if ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFE -and $bytes[1] -eq 0xFF) { return 'utf16be' }
    try {
        $strict = New-Object System.Text.UTF8Encoding($false, $true)
        [void]$strict.GetString($bytes)
        return 'utf8'
    } catch { return 'legacy' }
}

# Bang ma cu theo tung ngon ngu (dung khi file khong phai UTF-8)
function Get-LegacyCodePage([string]$tag) {
    switch (($tag -replace '^\.', '').ToLower()) {
        'vi' { 1258 } 'cs' { 1250 } 'ja' { 932 } 'ko' { 949 }
        'zh' { 936 }  'th' { 874 }  'ru' { 1251 } default { 1252 }
    }
}

# Chuyen file ve UTF-8 khong BOM. Tra ve kieu ma hoa ban dau.
function ConvertTo-Utf8Sub([string]$path, [string]$tag) {
    $kind = Get-SubEncodingKind $path
    if ($kind -eq 'utf8' -or $kind -eq 'empty' -or $kind -eq 'error') { return $kind }

    try { $bytes = [System.IO.File]::ReadAllBytes($path) } catch { return 'error' }
    $text = $null
    if ($kind -eq 'utf8bom') {
        $text = (New-Object System.Text.UTF8Encoding($false)).GetString($bytes, 3, $bytes.Length - 3)
    } elseif ($kind -eq 'utf16le') {
        $text = [System.Text.Encoding]::Unicode.GetString($bytes, 2, $bytes.Length - 2)
    } elseif ($kind -eq 'utf16be') {
        $text = [System.Text.Encoding]::BigEndianUnicode.GetString($bytes, 2, $bytes.Length - 2)
    } else {
        $cp = Get-LegacyCodePage $tag
        try { $text = [System.Text.Encoding]::GetEncoding($cp).GetString($bytes) }
        catch { $text = [System.Text.Encoding]::Default.GetString($bytes) }
        $kind = "cp$cp"
    }
    if ($null -eq $text) { return 'error' }
    try {
        [System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($false)))
    } catch { return 'error' }
    return $kind
}

# Chuan hoa toan bo phu de trong mot danh sach file
function Invoke-NormalizeEncoding($files) {
    if (-not $NormalizeEncoding) { return }
    $n = 0
    foreach ($f in $files) {
        $tag = (Split-LangTag $f.BaseName).Tag
        $was = ConvertTo-Utf8Sub $f.FullName $tag
        if ($was -eq 'utf8' -or $was -eq 'empty') { continue }
        if ($was -eq 'error') { W "  [!] Khong doc duoc de chuyen ma: $($f.Name)" 'Yellow'; continue }
        W "  [enc] $($f.Name): $was -> UTF-8" 'DarkCyan'
        Add-RunLog 'Encoding' $f.FullName '' "$was -> utf8"
        $n++
    }
    if ($n -gt 0) { W "  [i] Da chuyen $n file sang UTF-8." 'DarkCyan' }
}

# Chuan hoa mot tu thanh ma ngon ngu 2 ky tu, '' neu khong nhan ra
function ConvertTo-LangCode([string]$w) {
    if (-not $w) { return '' }
    $x = $w.ToLower().Trim()
    if ($x -match '^(vi|vie|viet|vietnamese|tiengviet)$') { return 'vi' }
    if ($x -match '^(cs|cz|cze|ces|czech|sec)$')          { return 'cs' }
    if ($x -match '^(en|eng|english|anh)$')               { return 'en' }
    if ($x -match '^(ja|jpn|jp|japanese)$')               { return 'ja' }
    if ($x -match '^(ko|kor|korean)$')                    { return 'ko' }
    if ($x -match '^(zh|chi|zho|chinese)$')               { return 'zh' }
    if ($x -match '^(fr|fra|fre|french)$')                { return 'fr' }
    if ($x -match '^(de|ger|deu|german)$')                { return 'de' }
    if ($x -match '^(es|spa|spanish)$')                   { return 'es' }
    if ($x -match '^(th|tha|thai)$')                      { return 'th' }
    return ''
}

# Doan ngon ngu cua mot file phu de:
#   1) ten thu muc con ngay duoi srt_can_sync  (vd: srt_can_sync\vi\...)
#   2) hau to trong ten file                   (vd: Phim.S01E01.vi.srt)
# Tra ve '.vi' / '.cs' ... hoac '' neu chiu.
function Get-FileLangTag($file, [string]$rootDir) {
    $rel = $file.FullName
    if ($rel.StartsWith($rootDir, 'OrdinalIgnoreCase')) { $rel = $rel.Substring($rootDir.Length) }
    # CHI xet thu muc con cap 1 ngay duoi srt_can_sync, tranh nhan nham thu muc phim ten 'en'
    $segs = @(($rel -split '[\\/]') | Where-Object { $_ })
    if ($segs.Count -gt 1) {
        $c = ConvertTo-LangCode $segs[0]
        if ($c) { return '.' + $c }
    }
    foreach ($m in [regex]::Matches($file.BaseName, '[\._\-\[\(]([A-Za-z]{2,12})$')) {
        $c = ConvertTo-LangCode $m.Groups[1].Value
        if ($c) { return '.' + $c }
    }
    $parts = $file.BaseName -split '[\._\-]'
    if ($parts.Count -gt 1) {
        $c = ConvertTo-LangCode $parts[$parts.Count - 1]
        if ($c) { return '.' + $c }
    }
    return ''
}

# ==========================================================
#  PHAN TICH QUAN HE THOI GIAN GIUA HAI PHU DE
#  Suy ra: co gian (ty le FPS) + do lech co dinh
# ==========================================================
# Cac ty le FPS thuong gap. Phu de khong luu FPS nen phai suy nguoc tu moc thoi gian.
$script:FpsRatios = @(
    @{ R = 1.0;            Name = 'khong co gian' },
    @{ R = (24/23.976);    Name = '24 -> 23.976 (hoac 30 -> 29.97)' },
    @{ R = (23.976/24);    Name = '23.976 -> 24 (hoac 29.97 -> 30)' },
    @{ R = (25/23.976);    Name = 'PAL 25 -> NTSC 23.976' },
    @{ R = (23.976/25);    Name = 'NTSC 23.976 -> PAL 25' },
    @{ R = (25/24);        Name = '25 -> 24' },
    @{ R = (24/25);        Name = '24 -> 25' }
)

function Get-EvenSample($arr, [int]$n) {
    if ($arr.Count -le $n) { return @($arr) }
    $step = [double]$arr.Count / $n
    $out = New-Object 'System.Collections.Generic.List[double]'
    for ($i = 0; $i -lt $n; $i++) { $out.Add($arr[[int]($i * $step)]) }
    return @($out)
}

# Bo phieu tim do lech co dinh tot nhat giua hai bo moc
function Get-OffsetVote($refArr, $tgtArr, [double]$maxOff = 400.0, [double]$bin = 0.15) {
    $votes = New-Object 'System.Collections.Generic.Dictionary[int,int]'
    foreach ($t in $tgtArr) {
        foreach ($r in $refArr) {
            $d = $r - $t
            if ($d -lt -$maxOff -or $d -gt $maxOff) { continue }
            $k = [int][math]::Round($d / $bin)
            if ($votes.ContainsKey($k)) { $votes[$k]++ } else { $votes[$k] = 1 }
        }
    }
    if ($votes.Count -eq 0) { return $null }
    $bk = 0; $bv = -1
    foreach ($k in $votes.Keys) { if ($votes[$k] -gt $bv) { $bv = $votes[$k]; $bk = $k } }
    $center = $bk * $bin
    $sum = 0.0; $cnt = 0
    foreach ($t in $tgtArr) {
        foreach ($r in $refArr) {
            $d = $r - $t
            if ([math]::Abs($d - $center) -le 0.35) { $sum += $d; $cnt++ }
        }
    }
    $off = if ($cnt -gt 0) { $sum / $cnt } else { $center }
    return [pscustomobject]@{ Offset = $off; Votes = $bv }
}

# Quet qua tung ty le FPS, chon ty le nao cho nhieu phieu nhat
function Get-TimingRelation($refTimes, $tgtTimes) {
    if (-not $refTimes -or -not $tgtTimes) { return $null }
    if ($refTimes.Count -lt 30 -or $tgtTimes.Count -lt 30) { return $null }

    $R = Get-EvenSample $refTimes $FpsSampleRef
    $T = Get-EvenSample $tgtTimes $FpsSampleTgt

    $best = $null
    foreach ($fr in $script:FpsRatios) {
        $scaled = @($T | ForEach-Object { $_ * $fr.R })
        $v = Get-OffsetVote $R $scaled
        if ($null -eq $v) { continue }
        if ($null -eq $best -or $v.Votes -gt $best.Votes) {
            $best = [pscustomobject]@{ Scale = $fr.R; Name = $fr.Name; Offset = $v.Offset; Votes = $v.Votes }
        }
    }
    if ($null -eq $best) { return $null }

    # Do troi tich luy o cuoi phim neu bo qua co gian
    $lastT = $tgtTimes[$tgtTimes.Count - 1]
    $drift = [math]::Abs(($best.Scale - 1.0) * $lastT)
    return [pscustomobject]@{
        Scale = $best.Scale; Name = $best.Name; Offset = $best.Offset
        Votes = $best.Votes; Drift = $drift
        FpsMismatch = ([math]::Abs($best.Scale - 1.0) -gt 0.0002)
    }
}

# ==========================================================
#  TIM CONG CU / THU MUC
# ==========================================================
function Join-Safe($a, $b) {
    if ([string]::IsNullOrWhiteSpace([string]$a)) { return $null }
    return (Join-Path ([string]$a) $b)
}

function Find-Alass {
    $desk = $null
    try { $desk = [Environment]::GetFolderPath('Desktop') } catch {}
    $hints = @(
        (Join-Safe $AlassDir 'alass.exe'),
        (Join-Safe $targetDir 'alass.exe'),
        (Join-Safe $targetDir 'alass\alass.exe'),
        (Join-Safe $desk 'alass\alass.exe'),
        (Join-Safe $env:USERPROFILE 'Desktop\alass\alass.exe')
    ) | Where-Object { $_ }
    foreach ($h in $hints) { if (Test-Path -LiteralPath $h) { return (Resolve-Path -LiteralPath $h).Path } }
    $c = Get-Command 'alass.exe' -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }
    return $null
}

function Find-Near([string]$exe, [string]$alassPath) {
    $desk = $null
    try { $desk = [Environment]::GetFolderPath('Desktop') } catch {}
    $dirs = @()
    if ($alassPath) { $dirs += (Split-Path $alassPath -Parent) }
    $dirs += @($AlassDir, $targetDir, (Join-Safe $targetDir 'alass'), (Join-Safe $desk 'alass'))
    foreach ($d in ($dirs | Where-Object { $_ })) {
        $p = Join-Path $d $exe
        if (Test-Path -LiteralPath $p) { return (Resolve-Path -LiteralPath $p).Path }
    }
    $c = Get-Command $exe -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }
    return $null
}

# Doc danh sach audio track cua mot file video (thu tu tuong doi, dem tu 0)
function Get-AudioStreams([string]$ffprobe, [string]$video) {
    if (-not $ffprobe) { return @() }
    try {
        $raw = & $ffprobe -v quiet -print_format json -show_streams -select_streams a "$video" 2>$null
        if (-not $raw) { return @() }
        $obj = ($raw -join "`n") | ConvertFrom-Json
        return @($obj.streams)
    } catch { return @() }
}

function Show-AudioStreams($streams) {
    if ($streams.Count -eq 0) { W "      (khong doc duoc danh sach audio)" 'DarkGray'; return }
    for ($i = 0; $i -lt $streams.Count; $i++) {
        $s = $streams[$i]
        $lg = if ($s.tags -and $s.tags.language) { $s.tags.language } else { 'und' }
        $ti = if ($s.tags -and $s.tags.title) { " - $($s.tags.title)" } else { '' }
        $ch = if ($s.channels) { "$($s.channels)ch" } else { '' }
        W ("      [{0}] {1,-4} {2,-10} {3}{4}" -f $i, $lg, $s.codec_name, $ch, $ti) 'White'
    }
}

function Find-MkvTool([string]$exe) {
    $desk = $null
    try { $desk = [Environment]::GetFolderPath('Desktop') } catch {}
    $dirs = @($MkvToolNixDir, (Join-Safe $desk 'mkvtoolnix'), (Join-Safe $env:USERPROFILE 'Desktop\mkvtoolnix'),
              (Join-Safe $env:ProgramFiles 'MKVToolNix'), (Join-Safe ${env:ProgramFiles(x86)} 'MKVToolNix'),
              (Join-Safe $env:LOCALAPPDATA 'Programs\MKVToolNix'), $targetDir)
    foreach ($d in ($dirs | Where-Object { $_ })) {
        $pp = Join-Path $d $exe
        if (Test-Path -LiteralPath $pp) { return (Resolve-Path -LiteralPath $pp).Path }
    }
    $c = Get-Command $exe -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }
    return $null
}

# Phu de van ban dung truc tiep duoc; PGS thi phai doc moc thoi gian ra sau
function Get-SubExtByCodec([string]$codecId) {
    if ($codecId -match 'S_TEXT/UTF8')   { return '.srt' }
    if ($codecId -match 'S_TEXT/ASS')    { return '.ass' }
    if ($codecId -match 'S_TEXT/SSA')    { return '.ssa' }
    if ($codecId -match 'S_TEXT/WEBVTT') { return '.vtt' }
    if ($PgsTimingFallback -and $codecId -match 'S_HDMV/PGS') { return '.sup' }
    return $null
}

# ==========================================================
#  DOC MOC THOI GIAN TU PHU DE PGS (.sup) - KHONG CAN OCR
# ==========================================================
# PGS luu san thoi diem hien/an tung dong, do phan giai 1/90000 giay.
# Ta chi can moc thoi gian de lam tham chieu nen khong phai nhan dang chu.
#
# Mot segment:  'PG' | PTS(4) | DTS(4) | type(1) | size(2) | payload
# Loai 0x16 = PCS:  payload[8] = co doi bang mau
#                   payload[10] = so doi tuong do hoa (>0 hien, =0 xoa)
function Format-SrtStamp([double]$sec) {
    if ($sec -lt 0) { $sec = 0 }
    $ts = [TimeSpan]::FromSeconds($sec)
    return ("{0:D2}:{1:D2}:{2:D2},{3:D3}" -f [int]$ts.TotalHours, $ts.Minutes, $ts.Seconds, $ts.Milliseconds)
}

function Convert-PgsToTimingSrt([string]$supPath, [string]$srtPath) {
    try { $b = [System.IO.File]::ReadAllBytes($supPath) } catch { return 0 }
    if ($b.Length -lt 16) { return 0 }

    $cues = New-Object 'System.Collections.Generic.List[object]'
    $start = -1.0
    $i = 0
    $n = $b.Length

    while ($i + 13 -le $n) {
        if ($b[$i] -ne 0x50 -or $b[$i+1] -ne 0x47) { break }
        $pts = ((([int]$b[$i+2]) -shl 24) -bor (([int]$b[$i+3]) -shl 16) -bor
                (([int]$b[$i+4]) -shl 8)  -bor  ([int]$b[$i+5])) / 90000.0
        $stype = $b[$i+10]
        $size  = ((([int]$b[$i+11]) -shl 8) -bor ([int]$b[$i+12]))
        $body  = $i + 13

        if ($stype -eq 0x16 -and $size -ge 11 -and ($body + 10) -lt $n) {
            if ($b[$body+8] -eq 0) {                     # bo qua PCS chi doi bang mau
                $nobj = $b[$body+10]
                if ($nobj -gt 0) {
                    if ($start -ge 0 -and ($pts - $start) -gt 0.1) { $cues.Add([pscustomobject]@{ S = $start; E = $pts }) }
                    $start = $pts
                } else {
                    if ($start -ge 0 -and ($pts - $start) -gt 0.1) { $cues.Add([pscustomobject]@{ S = $start; E = $pts }) }
                    $start = -1.0
                }
            }
        }
        $i = $body + $size
    }
    if ($cues.Count -eq 0) { return 0 }

    $sb = New-Object System.Text.StringBuilder
    $k = 0
    foreach ($c in $cues) {
        $k++
        [void]$sb.AppendLine([string]$k)
        [void]$sb.AppendLine("$(Format-SrtStamp $c.S) --> $(Format-SrtStamp $c.E)")
        [void]$sb.AppendLine($PgsTimingText)
        [void]$sb.AppendLine('')
    }
    try { [System.IO.File]::WriteAllText($srtPath, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false))) }
    catch { return 0 }
    return $cues.Count
}

# Doc toan bo moc bat dau cua phu de (giay), ho tro SRT va ASS/SSA
function Get-CueTimes([string]$path) {
    try { $txt = Get-Content -LiteralPath $path -Raw -ErrorAction Stop } catch { return $null }
    if (-not $txt) { return $null }
    $list = @()
    foreach ($m in [regex]::Matches($txt, '(\d{1,2}):(\d{2}):(\d{2})[,.](\d{2,3})\s*-->')) {
        $list += ([int]$m.Groups[1].Value) * 3600 + ([int]$m.Groups[2].Value) * 60 +
                 [int]$m.Groups[3].Value + (ConvertTo-Double ("0." + $m.Groups[4].Value))
    }
    if ($list.Count -eq 0) {
        foreach ($m in [regex]::Matches($txt, '(?im)^Dialogue:\s*\d+,(\d{1,2}):(\d{2}):(\d{2})\.(\d{2}),')) {
            $list += ([int]$m.Groups[1].Value) * 3600 + ([int]$m.Groups[2].Value) * 60 +
                     [int]$m.Groups[3].Value + (ConvertTo-Double ("0." + $m.Groups[4].Value))
        }
    }
    if ($list.Count -eq 0) { return $null }
    return @($list | Sort-Object)
}

# Do lech lon nhat giua phu de truoc va sau khi sync (giay)
function Get-MaxShift($before, $after) {
    if (-not $before -or -not $after) { return $null }
    $n = [math]::Min($before.Count, $after.Count)
    if ($n -lt 1) { return $null }
    $d1 = [math]::Abs($after[0] - $before[0])
    $d2 = [math]::Abs($after[$n - 1] - $before[$n - 1])
    if ($d2 -gt $d1) { return $d2 }
    return $d1
}

# So khop ma ngon ngu 2 ky tu voi 3 ky tu
function Test-LangMatch([string]$tag, [string]$want) {
    if (-not $tag) { return $false }
    $t = $tag.ToLower(); $w = $want.ToLower()
    if ($t -eq $w) { return $true }
    $map = @{
        'vi'='vie'; 'cs'='cze'; 'en'='eng'; 'ja'='jpn'; 'ko'='kor'
        'zh'='chi'; 'fr'='fre'; 'de'='ger'; 'es'='spa'; 'th'='tha'
    }
    if ($map.ContainsKey($w) -and ($t -eq $map[$w] -or $t -eq 'ces' -and $w -eq 'cs')) { return $true }
    if ($map.ContainsKey($t) -and $map[$t] -eq $w) { return $true }
    if ($t.Length -ge 2 -and $w.Length -ge 2 -and $t.Substring(0,2) -eq $w.Substring(0,2)) { return $true }
    return $false
}

$subDir = Join-Path $targetDir $SubFolderName
$refDir = if ($RefFolderName) { Join-Path $targetDir $RefFolderName } else { $targetDir }

# ==========================================================
#  DOC DANH SACH FILE MOC
# ==========================================================
# Nguoi dung tu bo file .sup vao thu muc moc -> tu chuyen sang .srt moc thoi gian
function Convert-LooseSupFiles([string]$dir) {
    if (-not $PgsTimingFallback -or -not (Test-Path -LiteralPath $dir)) { return }
    $sups = @(Get-ChildItem -LiteralPath $dir -File -Recurse -ErrorAction SilentlyContinue |
              Where-Object { $_.Extension.ToLower() -eq '.sup' })
    foreach ($sp in $sups) {
        $target = Join-Path $sp.DirectoryName ($sp.BaseName + '.srt')
        if (Test-Path -LiteralPath $target) { continue }
        $cnt = Convert-PgsToTimingSrt $sp.FullName $target
        if ($cnt -gt 0) { W "  [pgs] $($sp.Name) -> $($sp.BaseName).srt ($cnt moc thoi gian)" 'DarkCyan' }
        else            { W "  [!] Khong doc duoc PGS: $($sp.Name)" 'Yellow' }
    }
}

# Duong dan co nam trong mot thu muc lam viec bi loai tru khong?
function Test-ExcludedPath([string]$full) {
    foreach ($n in $RefExcludeFolders) {
        if (-not $n) { continue }
        $d = Join-Path $targetDir $n
        if ($full.StartsWith($d + '\', 'OrdinalIgnoreCase')) { return $true }
        if ($full.Equals($d, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

function Get-RefIndex([string]$onlyType) {
    # Quet ca thu muc moc lan thu muc chua phu de goc
    $scanDirs = @($refDir)
    if ($ExtractFolder) { $scanDirs += (Join-Path $targetDir $ExtractFolder) }
    foreach ($d in $scanDirs) { Convert-LooseSupFiles $d }
    $scanDirs = @($scanDirs | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -Unique)
    if ($scanDirs.Count -eq 0) {
        W "[LOI] Khong tim thay thu muc moc: $refDir" 'Red'; return $null
    }

    $all = @(); $seenPath = @{}
    foreach ($d in $scanDirs) {
        $gci = @{ LiteralPath = $d; File = $true }
        if ($ScanSubFolders) { $gci['Recurse'] = $true }
        foreach ($x in (Get-ChildItem @gci | Where-Object { ($VideoExts + $SubExts) -contains $_.Extension.ToLower() })) {
            if (-not $seenPath.ContainsKey($x.FullName.ToLower())) {
                $seenPath[$x.FullName.ToLower()] = $true
                $all += $x
            }
        }
    }

    $list = @(); $videos = @()
    foreach ($g in $all) {
        if ($g.FullName.StartsWith($subDir, 'OrdinalIgnoreCase')) { continue }   # bo qua thu muc can sync
        if (Test-ExcludedPath $g.FullName) { continue }                          # bo qua srt_pool, Output...
        $isVid = ($VideoExts -contains $g.Extension.ToLower())
        $info  = Get-EpisodeInfo $g.BaseName
        # Canon = ten da bo hau to ngon ngu, de '.en'/'.vi'/'' cua cung 1 phim gom lam mot
        $canon = if ($isVid) { $g.BaseName } else { (Split-LangTag $g.BaseName).Base }
        $item  = [pscustomobject]@{ File = $g; Info = $info; IsVideo = $isVid
                                    Key = (Get-EpLabel $info); Canon = $canon
                                    HasTag = ((Split-LangTag $g.BaseName).Tag -ne '')
                                    Tokens = (Get-TitleTokens $canon); Year = (Get-TitleYear $canon) }
        if ($isVid) { $videos += $item }
        if ($onlyType -eq 'video' -and -not $isVid) { continue }
        if ($onlyType -eq 'sub'   -and $isVid)      { continue }
        $list += $item
    }

    if ($list.Count -eq 0) {
        if ($onlyType -eq 'sub') { W "[LOI] Khong co phu de goc nao trong '$ExtractFolder'." 'Red' }
        else { W "[LOI] Khong tim thay file phim nao." 'Red' }
        return $null
    }

    $byKey = @{}
    foreach ($r in $list | Where-Object { $_.Info }) {
        if (-not $byKey.ContainsKey($r.Key)) { $byKey[$r.Key] = @() }
        $byKey[$r.Key] += $r
    }
    $byEp = @{}
    foreach ($k in $byKey.Keys) {
        foreach ($r in $byKey[$k]) {
            if (-not $byEp.ContainsKey($r.Info.Episode)) { $byEp[$r.Info.Episode] = @() }
            $byEp[$r.Info.Episode] += $r
        }
    }
    $byName = @{}
    foreach ($r in $list) {
        foreach ($n in @($r.File.BaseName.ToLower(), $r.Canon.ToLower())) {
            if (-not $byName.ContainsKey($n)) { $byName[$n] = $r }
            elseif ((-not $r.HasTag) -and $byName[$n].HasTag) { $byName[$n] = $r }
        }
    }
    # Chi muc theo ten phim - dung cho PHIM LE (khong co so tap)
    $byTitle = @{}
    foreach ($r in $list) {
        $tk = Get-TitleKey $r.File.BaseName
        if ($tk.Length -ge 4) {
            if (-not $byTitle.ContainsKey($tk)) { $byTitle[$tk] = @() }
            $byTitle[$tk] += $r
        }
    }
    # Ban do ten file phim -> dung de biet ghi ket qua vao thu muc nao
    $vidByName = @{}
    foreach ($v in $videos) {
        $n = $v.Canon.ToLower()
        if (-not $vidByName.ContainsKey($n)) { $vidByName[$n] = $v.File }
    }

    $kind = if ($onlyType -eq 'sub') { 'phu de goc' } else { 'file phim' }
    W "[i] Moc: $($list.Count) $kind - $($byKey.Keys.Count) tap." 'DarkCyan'

    # Cho biet moc lay tu nhung thu muc nao - de phat hien quet nham
    $srcDirs = @($list | ForEach-Object {
        $d = $_.File.DirectoryName
        if ($d.StartsWith($targetDir, 'OrdinalIgnoreCase')) {
            $d = $d.Substring($targetDir.Length).Trim('\')
        }
        if (-not $d) { '(thu muc goc)' } else { $d }
    } | Group-Object | Sort-Object Count -Descending)
    $txt = @($srcDirs | Select-Object -First 4 | ForEach-Object { "$($_.Name) ($($_.Count))" })
    W "    Lay tu: $($txt -join ', ')" 'DarkGray'
    return [pscustomobject]@{ List = $list; ByKey = $byKey; ByEp = $byEp; ByName = $byName
                              ByTitle = $byTitle; VideoByName = $vidByName }
}
# Nhieu file cung Canon = cung mot phim -> chon mot ban dai dien
# Uu tien: video > phu de khong hau to (ban trich ra) > con lai
function Select-RefVariant($cands) {
    if ($cands.Count -le 1) { return @($cands) }
    $v = @($cands | Where-Object { $_.IsVideo })
    if ($v.Count -gt 0) { return @($v[0]) }
    $plain = @($cands | Where-Object { -not $_.HasTag })
    if ($plain.Count -gt 0) { return @($plain[0]) }
    return @($cands[0])
}

function Resolve-Ref($idx, [string]$subBaseName, [switch]$Quiet) {
    $split = Split-LangTag $subBaseName

    # 1. Khop tuyet doi theo ten (sau khi da doi ten o buoc 1)
    if ($idx.ByName.ContainsKey($split.Base.ToLower())) { return $idx.ByName[$split.Base.ToLower()] }

    # 2. Khop theo TEN PHIM: cham diem tu khoa, loc theo nam (danh cho phim le)
    $tk     = Get-TitleKey $split.Base
    $myTok  = Get-TitleTokens $split.Base
    $myYear = Get-TitleYear $split.Base

    if ($myTok.Count -gt 0) {
        $scored = @()
        foreach ($r in $idx.List) {
            # Nam khac nhau ro rang -> chac chan khong phai cung phim
            if ($myYear -and $r.Year -and $myYear -ne $r.Year) { continue }
            $sc = Get-TitleScore $myTok $r.Tokens
            if ($sc -gt 0) { $scored += [pscustomobject]@{ Ref = $r; Score = $sc } }
        }
        if ($scored.Count -gt 0) {
            $scored = @($scored | Sort-Object Score -Descending)
            $best   = $scored[0]
            # Gom cac ung vien cung diem cao nhat lai (co the la .en/.vi cua cung phim)
            $top    = @($scored | Where-Object { $_.Score -eq $best.Score })
            $topCanon = @($top | ForEach-Object { $_.Ref.Canon } | Select-Object -Unique)
            $secondScore = 0.0
            $lower = @($scored | Where-Object { $_.Score -lt $best.Score })
            if ($lower.Count -gt 0) { $secondScore = $lower[0].Score }

            if ($best.Score -ge $TitleMinScore -and $topCanon.Count -eq 1 -and
                ($best.Score - $secondScore) -ge $TitleMinMargin) {
                if (-not $Quiet) {
                    W "  [i] Khop theo ten phim (diem $([math]::Round($best.Score,2))): $($best.Ref.Canon)" 'DarkGray'
                }
                return (Select-RefVariant @($top | ForEach-Object { $_.Ref }))[0]
            }
            if (-not $Quiet -and $best.Score -ge $TitleMinScore -and $topCanon.Count -gt 1) {
                W "  [-] NHAP NHANG theo ten phim ($($topCanon.Count) moc cung diem $([math]::Round($best.Score,2))): $subBaseName" 'Yellow'
                foreach ($c in ($topCanon | Select-Object -First 4)) { W "        > $c" 'DarkGray' }
                return $null
            }
        }
    }

    # 3. Khop theo season + so tap - CHI khi so tap la that, khong phai phong doan
    $info = Get-EpisodeInfo $split.Base
    if (-not $info) {
        if (-not $Quiet) { W "  [-] Khong nhan dien duoc so tap: $subBaseName" 'DarkGray' }
        return $null
    }
    $cands = @()
    if (-not $info.Confident) {
        # Ten file khong co ma tap that (SxxExx / Ep05 / - 05 -).
        # Con so tim duoc chi la rac ky thuat (vd 'DDP5.1' -> 1) nen TUYET DOI khong ghep theo no.
        if (-not $Quiet) {
            W "  [-] Khong tim duoc phim tuong ung: $subBaseName" 'Yellow'
            W "      Tu khoa doc ra: [$($myTok -join ', ')]$(if($myYear){" nam $myYear"})" 'DarkGray'
            $near = @()
            foreach ($r in $idx.List) { $near += [pscustomobject]@{ C = $r.Canon; S = (Get-TitleScore $myTok $r.Tokens); Y = $r.Year } }
            foreach ($n in (@($near | Sort-Object S -Descending | Select-Object -First 3))) {
                W "      gan nhat: $([math]::Round($n.S,2))  $($n.C)$(if($n.Y){" ($($n.Y))"})" 'DarkGray'
            }
            W "      -> Can diem >= $TitleMinScore. Doi ten phu de cho giong ten phim hon." 'DarkGray'
        }
        return $null
    }
    if ($info.HasSeason) {
        $k = Get-EpLabel $info
        if ($idx.ByKey.ContainsKey($k)) { $cands = $idx.ByKey[$k] }
        if ($cands.Count -eq 0 -and $idx.ByEp.ContainsKey($info.Episode)) {
            $cands = @($idx.ByEp[$info.Episode] | Where-Object { -not $_.Info.HasSeason })
        }
    } elseif ($idx.ByEp.ContainsKey($info.Episode)) {
        $cands = $idx.ByEp[$info.Episode]
    }
    # Chi giu cac moc cung co ma tap that
    $cands = @($cands | Where-Object { $_.Info -and $_.Info.Confident })

    if ($cands.Count -eq 0) {
        # 4. Chi ghep thang khi CA HAI ben deu chi co dung mot file -> khong the nham
        $uniqRef = @($idx.List | ForEach-Object { $_.Canon } | Select-Object -Unique)
        if ($uniqRef.Count -eq 1 -and $script:SubFileCount -eq 1) {
            if (-not $Quiet) { W "  [i] Chi co 1 phu de va 1 file moc -> ghep truc tiep." 'DarkGray' }
            return (Select-RefVariant $idx.List)[0]
        }
        if (-not $Quiet) {
            W "  [-] Khong tim thay file moc cho: $subBaseName" 'Yellow'
            W "      (doc ra: $(Get-EpLabel $info), ten phim: '$tk')" 'DarkGray'
            W "      Cac moc dang co: $((@($idx.List | ForEach-Object { $_.Canon } | Select-Object -Unique) | Select-Object -First 4) -join ' | ')" 'DarkGray'
        }
        return $null
    }
    $cands = @(Select-RefVariant $cands)
    $distinct = @($cands | ForEach-Object { $_.Canon } | Select-Object -Unique)
    if ($distinct.Count -gt 1) {
        if (-not $Quiet) {
            W "  [-] NHAP NHANG tap $(Get-EpLabel $info) - $($distinct.Count) file moc khop: $subBaseName" 'Yellow'
            foreach ($d in $distinct) { W "        > $d" 'DarkGray' }
            W "        -> Dat ten phu de kem season (vd S02E05) roi chay lai." 'DarkGray'
        }
        return $null
    }
    return $cands[0]
}

# ==========================================================
#  BUOC 0: TRICH PHU DE GOC (NON-SDH) TU FILE PHIM
# ==========================================================
function Invoke-ExtractRefSubs {
    $mkvmerge   = Find-MkvTool 'mkvmerge.exe'
    $mkvextract = Find-MkvTool 'mkvextract.exe'
    if (-not $mkvmerge -or -not $mkvextract) {
        W "`n[LOI] Khong tim thay mkvmerge.exe / mkvextract.exe." 'Red'
        W "      Sua bien `$MkvToolNixDir o phan CAU HINH." 'Yellow'
        return 0
    }
    W "[i] mkvextract: $mkvextract" 'DarkCyan'

    if (-not $ExtractFolder) { W "[LOI] `$ExtractFolder khong duoc de trong." 'Red'; return 0 }
    $exDir = Join-Path $targetDir $ExtractFolder
    if (-not (Test-Path -LiteralPath $exDir)) { New-Item -ItemType Directory -Path $exDir -Force | Out-Null }

    # Tim file phim: quet targetDir, bo qua thu muc phu de / thu muc trich / Output
    $gci = @{ LiteralPath = $targetDir; File = $true }
    if ($ScanSubFolders) { $gci['Recurse'] = $true }
    $skipDirs = @($subDir, $exDir)
    foreach ($n in $RefExcludeFolders) { if ($n) { $skipDirs += (Join-Path $targetDir $n) } }
    $videos = @(Get-ChildItem @gci | Where-Object {
        ($VideoExts -contains $_.Extension.ToLower()) -and
        ($_.Extension.ToLower() -eq '.mkv')
    } | Where-Object {
        $keep = $true
        foreach ($d in $skipDirs) { if ($_.FullName.StartsWith($d, 'OrdinalIgnoreCase')) { $keep = $false } }
        $keep
    } | Sort-Object FullName)

    if ($videos.Count -eq 0) {
        W "[!] Khong tim thay file .mkv nao de trich (chi file MKV moi trich duoc)." 'Yellow'
        return 0
    }
    W "[i] Tim thay $($videos.Count) file MKV. Bat dau trich phu de goc...`n" 'DarkCyan'

    $ok = 0; $skip = 0; $err = 0
    foreach ($v in $videos) {
        $json = $null
        try {
            $raw = & $mkvmerge -J "$($v.FullName)" 2>&1
            if ($LASTEXITCODE -gt 1) { throw "exit $LASTEXITCODE" }
            $json = ($raw -join "`n") | ConvertFrom-Json
        } catch {
            W "  [!] Khong doc duoc: $($v.Name)" 'Red'; $err++; continue
        }

        # Chon track phu de tot nhat: van ban, khong forced, khong SDH, uu tien ngon ngu
        $best = $null; $bestScore = 9999
        foreach ($t in @($json.tracks | Where-Object { $_.type -eq 'subtitles' })) {
            $p   = $t.properties
            $ext = Get-SubExtByCodec ([string]$p.codec_id)
            if (-not $ext) { continue }                       # VobSub / DVB -> khong ho tro
            $isPgs = ($ext -eq '.sup')

            $nm  = [string]$p.track_name
            $frc = ($nm -match '(?i)forced') -or ($p.forced_track -eq $true) -or ($p.flag_forced -eq $true)
            if ($frc) { continue }                            # forced chi vai dong -> vo dung lam moc

            $sdh = ($nm -match '(?i)\bSDH\b|hearing') -or ($p.flag_hearing_impaired -eq $true)
            $lang = ("$($p.language) $($p.language_ietf)").ToLower()

            $li = 99
            for ($i = 0; $i -lt $RefSubLangs.Count; $i++) {
                if (Test-LangMatch $lang $RefSubLangs[$i]) { $li = $i; break }
            }
            # Diem cang thap cang tot: van ban truoc PGS, roi den ngon ngu, roi non-SDH
            $score = $li * 10
            if ($sdh)   { $score += 5 }
            if ($isPgs) { $score += 1000 }        # chi dung khi khong con lua chon nao khac
            if ($score -lt $bestScore) {
                $bestScore = $score
                $best = [pscustomobject]@{ Id = [int]$t.id; Ext = $ext; Sdh = $sdh; Lang = $lang; Name = $nm; IsPgs = $isPgs }
            }
        }

        if (-not $best) {
            W "  [-] $($v.Name): khong co phu de van ban dung duoc (co the chi co PGS/VobSub)." 'DarkGray'
            $skip++; continue
        }

        # Voi PGS: file cuoi cung van la .srt (chi chua moc thoi gian)
        $finalPath = Join-Path $exDir ($v.BaseName + $(if ($best.IsPgs) { '.srt' } else { $best.Ext }))
        if ($SkipExistingRef -and (Test-Path -LiteralPath $finalPath)) {
            W "  [=] Da co moc: $($v.BaseName)$(Split-Path $finalPath -Extension)" 'DarkGray'; $skip++; continue
        }
        $outPath = Join-Path $exDir ($v.BaseName + $best.Ext)

        $tag = if ($best.Sdh) { ' [SDH - khong co ban thuong]' } else { '' }
        $logOut = & $mkvextract tracks "$($v.FullName)" "$($best.Id):$outPath" 2>&1
        if ($LASTEXITCODE -le 1 -and (Test-Path -LiteralPath $outPath) -and (Get-Item -LiteralPath $outPath).Length -gt 32) {
            if ($best.IsPgs) {
                # Phu de dang anh -> doc thang moc thoi gian, khong OCR
                $cnt = Convert-PgsToTimingSrt $outPath $finalPath
                Remove-Item -LiteralPath $outPath -Force -ErrorAction SilentlyContinue
                if ($cnt -gt 0) {
                    W "  [+] $($v.BaseName).srt  <- track #$($best.Id) PGS ($($best.Lang.Trim())), doc $cnt moc thoi gian$tag" 'Green'
                    $ok++
                } else {
                    W "  [!] Khong doc duoc moc thoi gian tu PGS: $($v.Name)" 'Red'
                    $err++
                }
                continue
            }
            W "  [+] $($v.BaseName)$($best.Ext)  <- track #$($best.Id) ($($best.Lang.Trim()))$tag" 'Green'
            $ok++
        } else {
            W "  [!] Trich that bai: $($v.Name)" 'Red'
            $logOut | Select-Object -Last 3 | ForEach-Object { W "      $_" 'DarkGray' }
            if (Test-Path -LiteralPath $outPath) { Remove-Item -LiteralPath $outPath -Force -ErrorAction SilentlyContinue }
            $err++
        }
    }
    W "`nTRICH: $ok thanh cong | $skip bo qua | $err loi" 'White'
    return $ok
}

# ==========================================================
#  BUOC 1: DOI TEN
# ==========================================================
function Invoke-Rename($idx, [string]$langTag) {
    $gci = @{ LiteralPath = $subDir; File = $true }
    if ($ScanSubFolders) { $gci['Recurse'] = $true }
    $files = @(Get-ChildItem @gci |
               Where-Object { $SubExts -contains $_.Extension.ToLower() } |
               Sort-Object FullName)

    if ($files.Count -eq 0) { W "[!] Khong co phu de nao trong '$SubFolderName'." 'Yellow'; return 0 }
    $script:SubFileCount = $files.Count

    $plan = @(); $skip = 0; $seen = @{}
    foreach ($f in $files) {
        # --- Xac dinh hau to ngon ngu cho RIENG file nay ---
        $detected = Get-FileLangTag $f $subDir
        $tagForFile = $langTag
        $warnTag = ''
        if ($langTag -eq 'auto') {
            if (-not $detected) {
                W "  [-] Khong doan duoc ngon ngu: $($f.Name)" 'Yellow'
                W "      -> Dat vao thu muc con '$SubFolderName\vi\' (hoac cs/en), hoac them hau to .vi vao ten file." 'DarkGray'
                $skip++; continue
            }
            $tagForFile = $detected
        }
        elseif ($detected -and ($detected -ne $langTag)) {
            $warnTag = "  [!] file nam trong thu muc '$($detected.TrimStart('.'))' nhung ban chon '$($langTag.TrimStart('.'))'"
            $script:TagConflict++
        }

        $ref = Resolve-Ref $idx $f.BaseName
        if (-not $ref) {
            W "  [-] KHONG DOI TEN: $($f.Name)" 'Yellow'
            W "      ngon ngu: $(if($detected){$detected.TrimStart('.')}else{'(khong ro)'}) | khong tim duoc moc tuong ung" 'DarkGray'
            $skip++; continue
        }

        $newName = $ref.Canon + $tagForFile + $f.Extension.ToLower()
        $newPath = Join-Path $f.DirectoryName $newName

        if ($f.Name -ceq $newName) { W "  [=] Da dung ten: $($f.Name)" 'DarkCyan'; $skip++; continue }
        if ($seen.ContainsKey($newPath.ToLower())) {
            W "  [-] TRUNG DICH voi '$($seen[$newPath.ToLower()])': $($f.Name)" 'Red'; $skip++; continue
        }
        if ((Test-Path -LiteralPath $newPath) -and ($newPath -ne $f.FullName)) {
            W "  [-] File dich da ton tai: $newName" 'Red'; $skip++; continue
        }
        $seen[$newPath.ToLower()] = $f.Name
        $plan += [pscustomobject]@{
            Old = $f.FullName; New = $newPath; OldName = $f.Name; NewName = $newName
            Ep = (Get-EpLabel (Get-EpisodeInfo $ref.File.BaseName)); Warn = $warnTag
        }
    }

    if ($script:TagConflict -gt 0) {
        W "" 'Gray'
        W "  [!] $($script:TagConflict) file nam trong thu muc ngon ngu KHAC voi lua chon cua ban." 'Red'
        W "      Chung se bi gan nhan '$($langTag.TrimStart('.'))' theo lua chon, KHONG theo thu muc." 'Yellow'
        W "      Muon gan theo thu muc: go N o buoc xac nhan, chay lai va chon muc 5." 'Yellow'
    }

    if ($plan.Count -eq 0) { W "`nKhong co file nao can doi ten. (Bo qua: $skip)" 'Yellow'; return 0 }

    W "`n--- KE HOACH DOI TEN ($($plan.Count) file) ---" 'Cyan'
    foreach ($p in $plan) {
        W "  [$($p.Ep)] $($p.OldName)" 'Gray'
        W "        -> $($p.NewName)" 'Green'
        if ($p.Warn) { W "       $($p.Warn) - kiem tra ky truoc khi dong y!" 'Red' }
    }
    W "  (Bo qua: $skip file)" 'DarkGray'

    if ($PreviewFirst -and ((Read-Host "`nTien hanh doi ten? (Y/N)") -notmatch '^(?i)y')) {
        W "Da huy, khong file nao bi thay doi." 'Yellow'; return 0
    }

    $ok = 0
    foreach ($p in $plan) {
        try {
            Rename-Item -LiteralPath $p.Old -NewName $p.NewName -ErrorAction Stop
            Add-RunLog 'Rename' $p.Old $p.New $p.Ep
            W "  [OK] $($p.Ep): $($p.NewName)" 'Green'; $ok++
        } catch {
            Add-RunLog 'RenameError' $p.Old $p.New $_.Exception.Message
            W "  [!] Loi: $($_.Exception.Message)" 'Red'
        }
    }
    return $ok
}

# ==========================================================
#  BUOC 2: DONG BO THOI GIAN BANG ALASS
# ==========================================================
# Do xem ban alass hien tai ho tro nhung co nao (lo hong 5)
function Get-SupportedAlassFlags([string]$alass, $wanted) {
    if (-not $wanted -or $wanted.Count -eq 0) { return @() }
    $help = ''
    try { $help = (& $alass --help 2>&1) -join "`n" } catch {}
    if (-not $help) {
        W "  [!] Khong doc duoc 'alass --help' -> cu thu dung day du tham so." 'Yellow'
        return $wanted
    }
    $ok = @(); $no = @()
    foreach ($f in $wanted) {
        if ($f -like '--*') {
            if ($help -match [regex]::Escape($f)) { $ok += $f } else { $no += $f }
        } else { $ok += $f }   # gia tri di kem tham so truoc do
    }
    if ($no.Count -gt 0) {
        W "  [!] Ban alass nay KHONG ho tro: $($no -join ', ')" 'Yellow'
        W "      -> Lop chong cat doan bi giam hieu luc, hay tin vao nguong `$MaxShiftSeconds." 'DarkYellow'
    }
    if ($ok.Count -gt 0) { W "  [i] Tham so cho che do audio: $($ok -join ' ')" 'DarkCyan' }
    return $ok
}

function Invoke-Sync($idx, $audioSel, [bool]$confirmPairs) {
    $alass = Find-Alass
    if (-not $alass) {
        W "`n[LOI] Khong tim thay alass.exe. Sua bien `$AlassDir o phan CAU HINH." 'Red'
        return 0
    }
    $alignSub   = Get-SupportedAlassFlags $alass $AlassArgsSubMode
    $alignAudio = Get-SupportedAlassFlags $alass $AlassArgsAudioMode

    # Luon trich audio ra WAV truoc khi dua cho alass, KE CA che do 'auto'.
    # Ly do: dua thang file MKV thi alass goi ffprobe va doc JSON cua MOI stream.
    # MKV co file dinh kem (font .ttf/.otf cua anime) hoac stream la -> ffprobe khong tra
    # truong 'codec_long_name' -> alass vo voi loi "missing field codec_long_name".
    # File WAV chi co dung 1 stream audio nen khong bao gio gap loi nay.
    $ffmpeg  = Find-Near 'ffmpeg.exe'  $alass
    $ffprobe = Find-Near 'ffprobe.exe' $alass
    if (-not $ffmpeg -or -not $ffprobe) {
        W "[!] Thieu ffmpeg.exe/ffprobe.exe canh alass.exe -> phai dua thang file phim cho alass." 'Yellow'
        W "    MKV co font dinh kem (thuong gap o anime) se loi 'missing field codec_long_name'." 'DarkYellow'
        $ffmpeg = $null; $ffprobe = $null
        if ($audioSel.Mode -ne 'auto') { $audioSel = [pscustomobject]@{ Mode = 'auto'; Value = $null } }
    }

    $gci = @{ LiteralPath = $subDir; File = $true }
    if ($ScanSubFolders) { $gci['Recurse'] = $true }
    $files = @(Get-ChildItem @gci |
               Where-Object { $SubExts -contains $_.Extension.ToLower() } |
               Sort-Object FullName)
    if ($files.Count -eq 0) { W "[!] Khong co phu de nao trong '$SubFolderName'." 'Yellow'; return 0 }
    $script:SubFileCount = $files.Count

    # ---------- LAP CAP ----------
    $pairs = @(); $skip = 0
    foreach ($f in $files) {
        $ref = Resolve-Ref $idx $f.BaseName
        if (-not $ref) { $skip++; continue }

        $split   = Split-LangTag $f.BaseName
        $tag     = if ($split.Tag) { '.' + $split.Tag } else { '' }
        $outName = $ref.Canon + $tag + $f.Extension.ToLower()

        # Ket qua LUON nam canh file phim
        $vid = $null
        $kn  = $ref.Canon.ToLower()
        if ($idx.VideoByName.ContainsKey($kn)) { $vid = $idx.VideoByName[$kn] }
        $outDir = if ($OutFolderName) { Join-Path $targetDir $OutFolderName }
                  elseif ($vid)       { $vid.DirectoryName }
                  else                { $ref.File.DirectoryName }
        $outPath = Join-Path $outDir $outName

        if ($outPath -eq $f.FullName) { W "  [-] Nguon trung dich: $outName" 'Yellow'; $skip++; continue }
        if ($SkipExistingOut -and (Test-Path -LiteralPath $outPath)) {
            W "  [=] Da co ket qua: $outName" 'DarkGray'; $skip++; continue
        }
        # Phan tich quan he thoi gian (chi lam duoc khi moc la PHU DE)
        $rel = $null
        if ($AnalyzeTiming -and -not $ref.IsVideo) {
            $rt = Get-CueTimes $ref.File.FullName
            $tt = Get-CueTimes $f.FullName
            $rel = Get-TimingRelation $rt $tt
        }
        $pairs += [pscustomobject]@{ Sub = $f; Ref = $ref; OutDir = $outDir; OutPath = $outPath
                                     OutName = $outName; Rel = $rel }
    }

    if ($pairs.Count -eq 0) { W "`nKhong co cap nao de dong bo. (Bo qua: $skip)" 'Yellow'; return 0 }

    # Chuan hoa ma hoa TRUOC khi dua cho alass (lo hong 1)
    if ($NormalizeEncoding) {
        W "`n[i] Kiem tra ma hoa ky tu..." 'DarkCyan'
        Invoke-NormalizeEncoding @($pairs | ForEach-Object { $_.Sub })
    }

    # ---------- XAC NHAN ----------
    if ($confirmPairs) {
        W "`n--- CAC CAP SE DONG BO ($($pairs.Count)) ---" 'Cyan'
        foreach ($p in $pairs) {
            $kind = if ($p.Ref.IsVideo) { 'video' } else { 'sub goc' }
            W "  $($p.Sub.Name)" 'White'
            W "     moc ($kind): $($p.Ref.File.Name)" 'DarkCyan'
            W "     ket qua    : $($p.OutPath)" 'Green'
            if ($p.Rel) {
                $sg = if ($p.Rel.Offset -ge 0) { '+' } else { '' }
                W "     du bao     : lech $sg$([math]::Round($p.Rel.Offset,2))s, he so $([math]::Round($p.Rel.Scale,4))" 'DarkGray'
                if ($p.Rel.FpsMismatch) {
                    W "     [!] LECH FPS: $($p.Rel.Name)" 'Red'
                    W "         Neu chi dich thoi gian, cuoi phim se sai khoang $([math]::Round($p.Rel.Drift,1))s." 'Yellow'
                }
            }
        }
        W "  (Bo qua: $skip file)" 'DarkGray'
        if ((Read-Host "`nCac cap tren da dung chua? (Y/N)") -notmatch '^(?i)y') {
            W "Da huy, khong file nao bi thay doi." 'Yellow'; return 0
        }
    }

    # ---------- THUC THI ----------
    $tempDir  = Join-Path $env:TEMP ("subprep_" + [guid]::NewGuid().ToString('N').Substring(0,8))
    $wavCache = @{}
    $ok = 0; $err = 0

    foreach ($p in $pairs) {
        $f = $p.Sub; $ref = $p.Ref; $outPath = $p.OutPath
        if (-not (Test-Path -LiteralPath $p.OutDir)) { New-Item -ItemType Directory -Path $p.OutDir -Force | Out-Null }

        # --- Chon audio track (chi khi moc la video) ---
        $refPath = $ref.File.FullName
        $refKind = if ($ref.IsVideo) { 'video' } else { 'sub goc' }

        if ($ref.IsVideo -and $ffmpeg) {
            $vid = $ref.File.FullName
            if ($wavCache.ContainsKey($vid)) {
                if ($wavCache[$vid]) { $refPath = $wavCache[$vid]; $refKind = 'audio#' + $wavCache["$vid|rel"] }
            } else {
                $streams = Get-AudioStreams $ffprobe $vid
                $rel = -1
                if ($streams.Count -eq 0) {
                    W "  [!] Khong doc duoc audio: $($ref.File.Name) -> dung mac dinh." 'Yellow'
                } elseif ($audioSel.Mode -eq 'auto') {
                    # Audio co co Default, khong co thi lay audio dau tien
                    $rel = 0
                    for ($i = 0; $i -lt $streams.Count; $i++) {
                        if ($streams[$i].disposition -and [int]$streams[$i].disposition.default -eq 1) { $rel = $i; break }
                    }
                } elseif ($audioSel.Mode -eq 'index') {
                    if ([int]$audioSel.Value -lt $streams.Count) { $rel = [int]$audioSel.Value }
                    else { W "  [!] File chi co $($streams.Count) audio -> dung mac dinh." 'Yellow' }
                } else {
                    for ($i = 0; $i -lt $streams.Count; $i++) {
                        $lg = if ($streams[$i].tags) { [string]$streams[$i].tags.language } else { '' }
                        if (Test-LangMatch $lg $audioSel.Value) { $rel = $i; break }
                    }
                    if ($rel -lt 0) { W "  [!] Khong co audio '$($audioSel.Value)' -> dung mac dinh." 'Yellow' }
                }
                $wav = $null
                if ($rel -ge 0) {
                    if (-not (Test-Path -LiteralPath $tempDir)) { New-Item -ItemType Directory -Path $tempDir -Force | Out-Null }
                    $wav = Join-Path $tempDir ("ref_" + [guid]::NewGuid().ToString('N').Substring(0,8) + ".wav")
                    & $ffmpeg -y -v error -i "$vid" -map "0:a:$rel" -vn -ac 1 -ar 16000 -c:a pcm_s16le "$wav" 2>&1 | Out-Null
                    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $wav)) {
                        W "  [!] ffmpeg khong trich duoc audio $rel -> dung mac dinh." 'Yellow'; $wav = $null
                    }
                }
                $wavCache[$vid] = $wav
                $wavCache["$vid|rel"] = $rel
                if ($wav) { $refPath = $wav; $refKind = "audio#$rel" }
            }
        }

        $script:SyncIdx++
        W "  [$($script:SyncIdx)/$($pairs.Count)] Sync ($refKind): $($f.Name)" 'Cyan'

        # Cat doan recap dau file khong co trong phim (phai lam TRUOC alass)
        Write-Host "      dang phan tich..." -NoNewline -ForegroundColor DarkGray
        $cuesBefore = 0
        $bb0 = Read-SrtBlocks $f.FullName
        if ($bb0) { $cuesBefore = $bb0.Count }
        $cutN = 0
        $refTimesForFile = $null
        if ($refKind -eq 'sub goc') { $refTimesForFile = Get-CueTimes $ref.File.FullName }
        if (Remove-LeadingExtra $f.FullName $refTimesForFile ([ref]$cutN)) {
            W "      [cut] Da cat $cutN dong dau khong co trong phim (recap/gioi thieu)" 'Yellow'
            $cuesBefore = $cuesBefore - $cutN
        }
        $vidDur = 0
        if ($ref.IsVideo) { $vidDur = Get-VideoDuration $ref.File.FullName }
        elseif ($idx.VideoByName.ContainsKey($ref.Canon.ToLower())) {
            $vidDur = Get-VideoDuration $idx.VideoByName[$ref.Canon.ToLower()].FullName
        }

        Write-Host "`r                          `r" -NoNewline
        $srcTimes = Get-CueTimes $f.FullName

        # Moc la phu de -> alass chay mac dinh (manh nhat). Moc la video -> siet lai vi VAD de nham.
        $useAlign = if ($refKind -eq 'sub goc') { $alignSub } else { $alignAudio }
        if ($p.Rel -and $p.Rel.FpsMismatch -and $FpsAutoAllowGuess) {
            $useAlign = @($useAlign | Where-Object { $_ -ne '--disable-fps-guessing' })
            W "      [i] Lech FPS -> cho phep alass tu sua framerate cho file nay." 'Yellow'
        }

        $attempts = @()
        $attempts += ,@{ Ref = $refPath; Align = $true; Note = '' }
        if ($refPath -ne $ref.File.FullName) {
            $attempts += ,@{ Ref = $ref.File.FullName; Align = $true; Note = 'thu lai voi file goc' }
        }
        if ($useAlign.Count -gt 0) {
            $attempts += ,@{ Ref = $ref.File.FullName; Align = $false; Note = 'thu lai khong kem tham so' }
        }

        $out = $null; $code = 1
        foreach ($at in $attempts) {
            if ($at.Note) { W "      [!] $($at.Note)..." 'Yellow' }
            $aArgs = @()
            if ($at.Align -and $useAlign.Count -gt 0) { $aArgs += $useAlign }
            if ($AlassExtraArgs.Count -gt 0) { $aArgs += $AlassExtraArgs }
            $aArgs += @($at.Ref, $f.FullName, $outPath)
            $out = & $alass @aArgs 2>&1
            $code = $LASTEXITCODE
            if ($code -eq 0) { break }
        }

        $good = ($code -eq 0) -and (Test-Path -LiteralPath $outPath)
        if ($good -and (Get-Item -LiteralPath $outPath).Length -lt 32) { $good = $false }

        if ($good -and $MaxShiftSeconds -gt 0) {
            $newTimes = Get-CueTimes $outPath
            $shift = Get-MaxShift $srcTimes $newTimes
            # Bao cao chi tiet: dich bao nhieu o dau phim va o cuoi phim
            $dFirst = $null; $dLast = $null
            if ($srcTimes -and $newTimes) {
                $nn = [math]::Min($srcTimes.Count, $newTimes.Count)
                if ($nn -ge 2) {
                    $dFirst = $newTimes[0] - $srcTimes[0]
                    $dLast  = $newTimes[$nn - 1] - $srcTimes[$nn - 1]
                }
            }
            if ($null -ne $shift) {
                if ($shift -gt $MaxShiftSeconds) {
                    if ($refKind -eq 'sub goc') {
                        # Moc la phu de -> dang tin cay, do lech lon van co the dung
                        # (ban dung khac nhau, intro dai ngan khac nhau). Chi canh bao.
                        W "      [!] Luu y: dich toi $([math]::Round($shift,1))s - lon hon binh thuong." 'Yellow'
                        W "         Van giu ket qua vi moc la phu de goc. Kiem tra lai khi xem." 'DarkGray'
                    } else {
                        W "      [!] TU CHOI: alass dich $([math]::Round($shift,1))s (nguong $MaxShiftSeconds s)." 'Red'
                        W "         Moc la audio nen do tin cay thap. Giu nguyen file goc." 'Yellow'
                        Remove-Item -LiteralPath $outPath -Force -ErrorAction SilentlyContinue
                        $skip++; continue
                    }
                }
                # Sua khoi bi don ve 00:00:00 (alass day recap ve truoc moc 0)
                $fixN = 0
                if (Repair-CollapsedCues $outPath ([ref]$fixN)) {
                    W "      [fix] Da bo $fixN dong bi don ve 00:00:00" 'Yellow'
                }
                if ($NormalizeEncoding) { [void](ConvertTo-Utf8Sub $outPath (Split-LangTag $f.BaseName).Tag) }

                # Kiem tra suc khoe
                $h = Test-SubHealth $outPath $refTimesForFile $vidDur $cuesBefore $srcTimes
                $hcol = switch ($h.Level) { 0 { 'Green' } 1 { 'Yellow' } default { 'Red' } }
                $htxt = switch ($h.Level) { 0 { 'TOT' } 1 { 'CAN XEM' } default { 'CO VAN DE' } }
                W "      -> $htxt : $($p.OutName) ($($h.Cues) dong)" $hcol

                if ($null -ne $h.ShiftFirst) {
                    $s1 = if ($h.ShiftFirst -ge 0) { '+' } else { '' }
                    $s2 = if ($h.ShiftLast  -ge 0) { '+' } else { '' }
                    $spread = $h.ShiftLast - $h.ShiftFirst
                    $sp = if ($spread -ge 0) { '+' } else { '' }
                    W ("         alass dich: dau phim $s1{0:N2}s | cuoi phim $s2{1:N2}s" -f $h.ShiftFirst, $h.ShiftLast) 'DarkCyan'
                    $spcol = if ([math]::Abs($spread) -le 0.5) { 'DarkGray' } elseif ([math]::Abs($spread) -le 5) { 'Yellow' } else { 'Red' }
                    $sptxt = if ([math]::Abs($spread) -le 0.5) { '(dich deu, khong co gian)' } elseif ([math]::Abs($spread) -le 5) { '(co gian nhe)' } else { '(CO GIAN MANH)' }
                    W ("         chenh dau-cuoi: $sp{0:N2}s {1}" -f $spread, $sptxt) $spcol
                }
                if ($h.ResVotes -ge 0) {
                    $s3 = if ($h.ResOffset -ge 0) { '+' } else { '' }
                    $q = [math]::Abs($h.ResOffset)
                    $qcol = if ($q -le 0.3) { 'Green' } elseif ($q -le 1.0) { 'DarkGreen' } else { 'Yellow' }
                    $qtxt = if ($q -le 0.3) { 'KHOP' } elseif ($q -le 1.0) { 'gan khop' } else { 'CON LECH' }
                    $ex = if ($h.ResFps) { " | LECH FPS: $($h.ResFps)" } else { '' }
                    W "         so voi sub goc: $qtxt $s3$([math]::Round($h.ResOffset,2))s ($($h.ResVotes) moc)$ex" $qcol
                }
                foreach ($fl in $h.Flags) { W "         - $fl" $hcol }

                $detail = "dau=$([math]::Round($h.ShiftFirst,2))s cuoi=$([math]::Round($h.ShiftLast,2))s"
                if ($h.ResVotes -ge 0) { $detail += " conlech=$([math]::Round($h.ResOffset,2))s moc=$($h.ResVotes)" }
                if ($cutN -gt 0) { $detail += " cat=$cutN dong" }
                if ($h.Flags.Count -gt 0) { $detail += " | " + ($h.Flags -join '; ') }
                Add-RunLog 'Create' $f.FullName $outPath $detail

                $script:Health += [pscustomobject]@{
                    File = $p.OutName; Level = $h.Level; Cues = $h.Cues
                    DichDau = $(if ($null -ne $h.ShiftFirst) { [math]::Round($h.ShiftFirst,2) } else { '' })
                    DichCuoi = $(if ($null -ne $h.ShiftLast) { [math]::Round($h.ShiftLast,2) } else { '' })
                    ConLech = $(if ($h.ResVotes -ge 0) { [math]::Round($h.ResOffset,2) } else { '' })
                    MocKhop = $(if ($h.ResVotes -ge 0) { $h.ResVotes } else { '' })
                    CatDau = $cutN; Flags = ($h.Flags -join ' | ')
                }
                $script:Processed += [pscustomobject]@{ Sub = $f.FullName; Ref = $ref.File.FullName; Out = $outPath }
                if ($h.Level -eq 0) { $ok++ } else { $ok++ }
                continue
            }
        }
        if ($good) {
            W "      -> OK: $($p.OutName)" 'Green'
            if ($NormalizeEncoding) { [void](ConvertTo-Utf8Sub $outPath (Split-LangTag $f.BaseName).Tag) }
            Add-RunLog 'Create' $f.FullName $outPath ''
            $script:Processed += [pscustomobject]@{ Sub = $f.FullName; Ref = $ref.File.FullName; Out = $outPath }
            $ok++
        }
        else {
            W "      [!] THAT BAI (exit $code)" 'Red'
            $out | Select-Object -Last 4 | ForEach-Object { W "         $_" 'DarkGray' }
            if (Test-Path -LiteralPath $outPath) { Remove-Item -LiteralPath $outPath -Force -ErrorAction SilentlyContinue }
            $err++
        }
    }

    if (Test-Path -LiteralPath $tempDir) { Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue }
    W "`nSYNC: $ok thanh cong | $skip bo qua | $err loi" 'White'

    # ---- Bao cao suc khoe ----
    if ($script:Health.Count -gt 0) {
        $g = @($script:Health | Where-Object { $_.Level -eq 0 }).Count
        $y = @($script:Health | Where-Object { $_.Level -eq 1 }).Count
        $r = @($script:Health | Where-Object { $_.Level -eq 2 }).Count
        W "KIEM TRA: $g tot | $y can xem | $r co van de" 'White'

        $bad = @($script:Health | Where-Object { $_.Level -ge 1 } |
                 Sort-Object -Property @{Expression='Level';Descending=$true})
        if ($bad.Count -gt 0) {
            W "`nCAC FILE CAN KIEM TRA TAY:" 'Yellow'
            foreach ($b in $bad) {
                $c = if ($b.Level -eq 2) { 'Red' } else { 'Yellow' }
                W "   $($b.File)" $c
                $d = "      $($b.Cues) dong"
                if ($b.DichDau -ne '') { $d += " | alass dich $($b.DichDau)s -> $($b.DichCuoi)s" }
                if ($b.ConLech -ne '') { $d += " | con lech $($b.ConLech)s ($($b.MocKhop) moc)" }
                if ($b.CatDau -gt 0)   { $d += " | da cat $($b.CatDau) dong dau" }
                W $d $c
                if ($b.Flags) { W "      $($b.Flags)" $c }
            }
        }

        if ($HealthReport) {
            $hd = Join-Path $targetDir $LogDirName
            if (-not (Test-Path -LiteralPath $hd)) { New-Item -ItemType Directory -Path $hd -Force | Out-Null }
            $hp = Join-Path $hd ("health_" + (Get-Date -Format 'yyyyMMdd_HHmmss') + ".csv")
            $script:Health | Export-Csv -LiteralPath $hp -NoTypeInformation -Encoding UTF8
            W "`nBao cao chi tiet: $LogDirName\$(Split-Path $hp -Leaf)" 'DarkCyan'
        }
    }
    return $ok
}
function Invoke-Undo {
    $dir = Join-Path $targetDir $LogDirName
    if (-not (Test-Path -LiteralPath $dir)) { W "[!] Chua co nhat ky nao de hoan tac." 'Red'; return }
    $runs = @(Get-ChildItem -LiteralPath $dir -File -Filter 'run_*.csv' | Sort-Object Name -Descending)
    if ($runs.Count -eq 0) { W "[!] Chua co nhat ky nao de hoan tac." 'Red'; return }

    W "`nCac lan chay gan day (moi lan hoan tac se lui lai 1 buoc):" 'Cyan'
    for ($i = 0; $i -lt [math]::Min($runs.Count, 10); $i++) {
        $rows = @(Import-Csv -LiteralPath $runs[$i].FullName)
        $nRen = @($rows | Where-Object { $_.Action -eq 'Rename' }).Count
        $nNew = @($rows | Where-Object { $_.Action -eq 'Create' }).Count
        $mark = if ($i -eq 0) { ' <- moi nhat' } else { '' }
        W ("   [{0}] {1}  ({2} doi ten, {3} file tao moi){4}" -f ($i+1), $runs[$i].BaseName, $nRen, $nNew, $mark) 'White'
    }
    $pick = Read-Host "`nHoan tac lan nao? (Enter = moi nhat, 0 = huy)"
    if ($pick -eq '0') { W "Da huy." 'Yellow'; return }
    $sel = if ($pick -match '^[1-9][0-9]*$' -and [int]$pick -le $runs.Count) { $runs[[int]$pick - 1] } else { $runs[0] }

    $rows = @(Import-Csv -LiteralPath $sel.FullName)
    $rens = @($rows | Where-Object { $_.Action -eq 'Rename' })
    $news = @($rows | Where-Object { $_.Action -eq 'Create' })

    W "`nSe hoan tac: $($sel.BaseName)" 'Cyan'
    W "   - Tra lai ten cu cho $($rens.Count) file" 'Gray'
    W "   - XOA $($news.Count) file da tao ra (ket qua dong bo)" 'Gray'
    if ((Read-Host "`nXac nhan? (Y/N)") -notmatch '^(?i)y') { W "Da huy." 'Yellow'; return }

    $del = 0; $ren = 0; $miss = 0
    foreach ($r in $news) {
        if (Test-Path -LiteralPath $r.To) {
            try { Remove-Item -LiteralPath $r.To -Force -ErrorAction Stop; $del++ } catch { $miss++ }
        } else { $miss++ }
    }
    for ($i = $rens.Count - 1; $i -ge 0; $i--) {
        $r = $rens[$i]
        if (-not (Test-Path -LiteralPath $r.To)) { $miss++; continue }
        try { Rename-Item -LiteralPath $r.To -NewName (Split-Path $r.From -Leaf) -ErrorAction Stop; $ren++ }
        catch { W "  [!] $($_.Exception.Message)" 'Red'; $miss++ }
    }
    W "`nHoan tac xong: $ren doi ten lai, $del file bi xoa, $miss bo qua." 'Green'
    Remove-Item -LiteralPath $sel.FullName -Force -ErrorAction SilentlyContinue
}


# ==========================================================
#  DON DEP SAU KHI HOAN TAT
# ==========================================================
# Xoa mot file, uu tien dua vao Thung rac de con khoi phuc duoc
function Remove-FileSafe([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $false }
    if ($DeleteToRecycleBin) {
        try {
            Add-Type -AssemblyName Microsoft.VisualBasic -ErrorAction Stop
            [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile(
                $path,
                [Microsoft.VisualBasic.FileIO.UIOption]::OnlyErrorDialogs,
                [Microsoft.VisualBasic.FileIO.RecycleOption]::SendToRecycleBin)
            return $true
        } catch { }
    }
    try { Remove-Item -LiteralPath $path -Force -ErrorAction Stop; return $true }
    catch { return $false }
}

function Invoke-Cleanup {
    $done = @($script:Processed)
    if ($done.Count -eq 0) { return }

    # CHI don nhung file da co ket qua that su nam canh phim
    $safe = @($done | Where-Object {
        (Test-Path -LiteralPath $_.Out) -and ((Get-Item -LiteralPath $_.Out).Length -gt 32)
    })
    if ($safe.Count -eq 0) { return }

    $exDir = Join-Path $targetDir $ExtractFolder
    $subFiles = @($safe | ForEach-Object { $_.Sub } | Select-Object -Unique |
                  Where-Object { Test-Path -LiteralPath $_ })
    $refFiles = @($safe | ForEach-Object { $_.Ref } | Select-Object -Unique |
                  Where-Object { (Test-Path -LiteralPath $_) -and $_.StartsWith($exDir, 'OrdinalIgnoreCase') })

    if ($subFiles.Count -eq 0 -and $refFiles.Count -eq 0) { return }

    W "`n---------------------------------------------------" 'Cyan'
    W " DON DEP FILE NGUON" 'Cyan'
    W "---------------------------------------------------" 'Cyan'
    W "$($safe.Count) phu de da dong bo xong va nam canh file phim." 'Green'
    W "Cac file nguon sau day khong con can nua:" 'Gray'
    W "   - $($subFiles.Count) file trong '$SubFolderName'" 'DarkGray'
    W "   - $($refFiles.Count) phu de goc trong '$ExtractFolder'" 'DarkGray'
    $where = if ($DeleteToRecycleBin) { 'Thung rac (khoi phuc duoc)' } else { 'XOA HAN (khong khoi phuc duoc)' }
    W "Noi den: $where" 'Yellow'

    W "`n   1. Xoa CA HAI ('$SubFolderName' + '$ExtractFolder')"
    W "   2. Chi xoa '$SubFolderName'"
    W "   3. Chi xoa '$ExtractFolder'"
    W "   0. Khong xoa gi" 'Green'
    $c = Read-Host "`nNhap lua chon"
    if ($c -eq '0' -or -not $c) { W "Khong xoa gi ca." 'DarkGray'; return }
    if (@('1','2','3') -notcontains $c) { W "[!] Lua chon khong hop le, khong xoa gi." 'Yellow'; return }

    $targets = @()
    if ($c -eq '1' -or $c -eq '2') { $targets += $subFiles }
    if ($c -eq '1' -or $c -eq '3') { $targets += $refFiles }

    W "`nSe xoa $($targets.Count) file:" 'Yellow'
    foreach ($t in ($targets | Select-Object -First 12)) { W "   $(Split-Path $t -Leaf)" 'DarkGray' }
    if ($targets.Count -gt 12) { W "   ... va $($targets.Count - 12) file nua" 'DarkGray' }
    if ((Read-Host "`nXac nhan xoa? (Y/N)") -notmatch '^(?i)y') { W "Da huy, khong xoa gi." 'Yellow'; return }

    $n = 0; $bad = 0
    foreach ($t in $targets) {
        if (Remove-FileSafe $t) { Add-RunLog 'Delete' $t '' $where; $n++ }
        else { W "  [!] Khong xoa duoc: $(Split-Path $t -Leaf)" 'Red'; $bad++ }
    }
    W "`nDa xoa $n file." 'Green'
    if ($bad -gt 0) { W "$bad file khong xoa duoc." 'Yellow' }

    foreach ($d in @((Join-Path $targetDir $SubFolderName), $exDir)) {
        if (Test-Path -LiteralPath $d) {
            $left = @(Get-ChildItem -LiteralPath $d -File -Recurse -ErrorAction SilentlyContinue).Count
            if ($left -eq 0) { W "   ('$(Split-Path $d -Leaf)' gio da trong)" 'DarkGray' }
            else { W "   ('$(Split-Path $d -Leaf)' con $left file chua xu ly)" 'DarkGray' }
        }
    }
}


# ==========================================================
#  MENU CHINH
# ==========================================================
[void](Initialize-Console)
Initialize-BoxChars
$__qe = Disable-QuickEdit

W "===================================================" 'Cyan'
W "   SUBPREP - CHUAN BI PHU DE CHO MUXTOOL" 'Cyan'
W "===================================================" 'Cyan'
if ($__qe) { W "[i] Da tat QuickEdit - nhap chuot vao cua so khong lam treo nua." 'DarkGray' }
else { W "[!] Khong tat duoc QuickEdit. Neu script dung im: nhan ESC de chay tiep." 'Yellow' }
W "Phu de can xu ly : $subDir" 'DarkGray'
W "Phu de goc       : $(Join-Path $targetDir $ExtractFolder)" 'DarkGray'

if (-not (Test-Path -LiteralPath $subDir)) {
    W "`n[LOI] Khong tim thay thu muc '$SubFolderName' tai: $targetDir" 'Red'
    W "      Hay tao thu muc do va bo phu de tai ve vao trong." 'Yellow'
    return
}

W "`nDong bo thoi gian bang cach nao?"
W "   1. Theo AUDIO cua file phim                (dung khi khong co phu de goc)"
W "   2. Theo PHU DE GOC trong '$ExtractFolder'  (chinh xac hon)" 'Green'
if (Test-Path -LiteralPath (Join-Path $targetDir $LogDirName)) { W "   0. Hoan tac (chon lan chay de lui lai)" 'Yellow' }
$syncMode = Read-Host "`nNhap lua chon"

if ($syncMode -eq '0') { Invoke-Undo; return }
if (@('1','2') -notcontains $syncMode) { W "[!] Lua chon khong hop le." 'Red'; return }

# ---------- Hau to ngon ngu cho phu de ----------
W "`nPhu de trong '$SubFolderName' la ngon ngu gi?"
W "   1. Tieng Viet  (.vi)"
W "   2. Tieng Sec   (.cs)"
W "   3. Tieng Anh   (.en)"
W "   4. Tu nhap"
W "   5. TU NHAN DIEN tung file  (doc ten thu muc con vi\ cs\ en\)  <- NEN DUNG" 'Green'
switch (Read-Host "`nNhap lua chon (Enter = 5)") {
    '1' { $langTag = '.vi' }
    '2' { $langTag = '.cs' }
    '3' { $langTag = '.en' }
    '4' {
        $raw = Read-Host "Nhap hau to (vd: ja)"
        if (-not $raw) { W "[!] Khong nhap gi." 'Red'; return }
        $langTag = '.' + $raw.Trim().TrimStart('.')
    }
    '5' {
        $langTag = 'auto'
        W "[i] Se doc ngon ngu tu ten thu muc con hoac hau to ten file." 'DarkCyan'
    }
    '' {
        $langTag = 'auto'
        W "[i] Mac dinh: doc ngon ngu tu ten thu muc con hoac hau to ten file." 'DarkCyan'
    }
    default { W "[!] Lua chon khong hop le." 'Red'; return }
}

$audioSel = [pscustomobject]@{ Mode = 'auto'; Value = $null }
$refType  = 'video'

if ($syncMode -eq '2') {
    # ---------- Doi chieu voi phu de goc ----------
    $refType = 'sub'
    $exDir = Join-Path $targetDir $ExtractFolder
    $haveRef = 0
    if (Test-Path -LiteralPath $exDir) {
        $haveRef = @(Get-ChildItem -LiteralPath $exDir -File -Recurse |
                     Where-Object { $SubExts -contains $_.Extension.ToLower() }).Count
    }
    if ($haveRef -eq 0) {
        W "`n[!] Thu muc '$ExtractFolder' chua co phu de goc nao." 'Yellow'
        if ((Read-Host "    Trich tu dong phu de non-SDH tu cac file MKV? (Y/N)") -match '^(?i)y') {
            Invoke-ExtractRefSubs | Out-Null
        } else { W "Da huy." 'Yellow'; return }
    } else {
        W "[i] Da co $haveRef phu de goc trong '$ExtractFolder'." 'DarkCyan'
        if ((Read-Host "    Trich bo sung cho cac tap con thieu? (Y/N)") -match '^(?i)y') {
            Invoke-ExtractRefSubs | Out-Null
        }
    }
} else {
    # ---------- Dong bo theo audio ----------
    if ($SyncAudioMode -eq 'index')    { $audioSel = [pscustomobject]@{ Mode = 'index'; Value = $SyncAudioIndex } }
    elseif ($SyncAudioMode -eq 'lang') { $audioSel = [pscustomobject]@{ Mode = 'lang';  Value = $SyncAudioLang } }
    else {
        $tmpIdx = Get-RefIndex 'video'
        if (-not $tmpIdx) { return }
        $sample = @($tmpIdx.List | Select-Object -First 1)
        if ($sample.Count -gt 0) {
            $fp = Find-Near 'ffprobe.exe' (Find-Alass)
            W "`nAudio track cua file mau: $($sample[0].File.Name)" 'DarkCyan'
            Show-AudioStreams (Get-AudioStreams $fp $sample[0].File.FullName)
        }
        W "`nDung audio track nao lam moc?"
        W "   1. Tu dong (audio mac dinh cua file)"
        W "   2. Chon theo SO THU TU  (0, 1, 2...)"
        W "   3. Chon theo NGON NGU   (en, vi, cs, ja...)"
        switch (Read-Host "`nNhap lua chon") {
            '1' { }
            '2' {
                $n = Read-Host "Nhap so thu tu audio track (dem tu 0)"
                if ($n -match '^\d+$') { $audioSel = [pscustomobject]@{ Mode = 'index'; Value = [int]$n } }
                else { W "[!] Khong hop le -> dung che do tu dong." 'Yellow' }
            }
            '3' {
                $l = Read-Host "Nhap ma ngon ngu (vd: en)"
                if ($l) { $audioSel = [pscustomobject]@{ Mode = 'lang'; Value = $l.Trim().ToLower() } }
                else { W "[!] Khong nhap gi -> dung che do tu dong." 'Yellow' }
            }
            default { W "[!] Khong hop le -> dung che do tu dong." 'Yellow' }
        }
        if ($audioSel.Mode -ne 'auto') { W "[i] Se dung audio: $($audioSel.Mode) = $($audioSel.Value)" 'DarkCyan' }
    }
}

# ---------- BUOC 1: DOI TEN ----------
W "`n=== BUOC 1/2: DOI TEN PHU DE ===" 'Cyan'
$idx = Get-RefIndex $refType
if (-not $idx) { return }
Invoke-Rename $idx $langTag | Out-Null

# ---------- BUOC 2: DONG BO ----------
W "`n=== BUOC 2/2: DONG BO THOI GIAN ===" 'Cyan'
$idx = Get-RefIndex $refType
if (-not $idx) { return }
Invoke-Sync $idx $audioSel ($syncMode -eq '2') | Out-Null

if ($AskCleanup) { Invoke-Cleanup }

$logPath = Save-RunLog $targetDir
if ($logPath) { W "`n[i] Nhat ky lan chay: $LogDirName\$(Split-Path $logPath -Leaf)" 'DarkCyan' }
W "XONG. Phu de da nam canh file phim, chay MuxTool de ghep vao." 'White'