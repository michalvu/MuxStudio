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
try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch {}

# Noi cua so nhung giu bo dem that lon de con cuon len xem lai.
# ('mode con' cua CMD dat bo dem BANG cua so -> mat sach lich su cuon)
function Set-ConsoleSize([int]$cols, [int]$rows, [int]$scrollback) {
    try {
        $raw = $Host.UI.RawUI
        $buf = $raw.BufferSize
        $win = $raw.WindowSize
        $maxW = $raw.MaxPhysicalWindowSize.Width
        $maxH = $raw.MaxPhysicalWindowSize.Height
        if ($cols -gt $maxW) { $cols = $maxW }
        if ($rows -gt $maxH) { $rows = $maxH }

        # Phai NOI bo dem truoc khi noi cua so
        $buf.Width  = [math]::Max($cols, $buf.Width)
        $buf.Height = [math]::Max($scrollback, 3000)
        $raw.BufferSize = $buf

        $win.Width  = $cols
        $win.Height = $rows
        $raw.WindowSize = $win

        # Thu hep be ngang bo dem ve dung be ngang cua so (tranh thanh cuon ngang)
        $buf = $raw.BufferSize
        $buf.Width = $cols
        $raw.BufferSize = $buf
    } catch { }
}

# ==========================================================
#  CAU HINH
# ==========================================================
$SubSourceApiKey = ''            # subsource.net > My Profile > API Key
$TmdbApiKey      = ''            # de trong = bo qua buoc tra IMDb ID, dung tim theo ten

# =====  NGON NGU CAN TAI  =====
# Doi $true / $false de bat tat. Thu tu o day cung la thu tu xu ly.
$Lang_vi = $true      # Tieng Viet
$Lang_en = $true      # Tieng Anh
$Lang_cs = $true      # Tieng Sec
$Lang_ja = $false     # Tieng Nhat
$Lang_ko = $false     # Tieng Han
$Lang_zh = $false     # Tieng Trung (gian the)
$Lang_fr = $false     # Tieng Phap
$Lang_de = $false     # Tieng Duc
$Lang_es = $false     # Tieng Tay Ban Nha
$Lang_th = $false     # Tieng Thai
$Lang_id = $false     # Tieng Indonesia
$Lang_ar = $false     # Tieng A Rap
# Ngon ngu khac khong co trong danh sach tren, vd: @('pt','ru')
$LangExtra = @()
$SubFolderName   = 'srt_can_sync'        # noi ha phu de tai ve (de SubPrep xu ly tiep)
# $true = tach thanh srt_can_sync\vi\, \cs\, \en\  (SubPrep che do 5 doc ten thu muc de biet ngon ngu)
# $false = do chung mot cho, phan biet bang hau to trong ten file
$UseLangSubFolders = $true
$MkvToolNixDir   = 'C:\Users\fenha\Desktop\mkvtoolnix'

$ScanSubFolders  = $true         # quet ca thu muc con (nhieu season mot luot)
$MaxShowCandidates = 12          # so dong toi da trong bang chon
# Be rong bang. 0 = tu do theo cua so console (khuyen dung).
# Dat so cu the neu muon ep cung, vd 120.
$TableWidth        = 0
$TableMinWidth     = 110         # khong bao gio hep hon muc nay
# Kich thuoc cua so console. Bo dem giu nhieu dong de con CUON LEN xem lai.
$ConsoleCols       = 150
$ConsoleRows       = 45
$ConsoleScrollback = 9000

# --- Kiem chung bang chinh phu de co san trong MKV ---
$VerifyTiming    = $true         # doi chieu moc thoi gian truoc khi chap nhan
# SubFetch KHONG sua thoi gian phu de. Nan thoi gian la viec cua SubPrep.
# (Sua o hai noi de gay keo dan sai va lam MKV bao sai thoi luong.)

# --- Giao dien chon phu de ---
$Interactive     = $true         # hien bang de tu chon; $false = tu lay ban dau bang
$RememberChoice  = $true         # da chon cho season+ngon ngu nao thi dung lai cho cac tap sau
$AutoPickWhenSingle = $true      # chi co dung 1 ung vien -> lay luon, khoi hoi
# Dat $true roi chay lai script de HOAN TAC lan chay truoc (xoa file da tao).
# Binh thuong luon de $false - script se khong hoi gi ca.
$UndoLastRun     = $false
$UseColors       = $true        # mau 24-bit + khung Unicode
# $true = mo CUA SO BANG that de chon (sap xep, loc duoc). Khong co thi lui ve bang console.
$UseGridView     = $true

# --- Phu de ban tu nem san vao srt_can_sync ---
$UseExistingSubs   = $true       # co file san -> doi ten cho khop MKV va bo qua tai
$ExistingMinScore  = 0.35        # do giong ten toi thieu khi khong do duoc thoi gian
# 'copy' = giu nguyen file goc trong srt_can_sync (an toan, khuyen dung)
# 'move' = doi ten han file goc
$ExistingRenameMode = 'copy'

# --- Phu de goc trich tu MKV ---
# Giu lai trong thu muc rieng thay vi xoa cuoi phien:
#  - lan chay sau khoi phai trich lai (nhanh hon nhieu)
#  - ban mo ra xem duoc de doi chieu tay
$KeepExtractedRef = $true
$ExtractFolder    = 'srt_goc'

# Khop DUNG thuong cho 25-35 phieu; khop SAI chi 5-15 phieu.
# Dat 8 la bien nhieu thanh 'dat yeu cau' -> ghep nham tap. Toi thieu nen la 20.
$MinVerifyVotes  = 20            # so moc khop toi thieu de tin ket qua

# --- Rang buoc FRAMERATE ---
# Phu de PAL 25fps ghep vao phim 23.976 se troi dan toi ~5 phut o cuoi.
# $StrictFpsMatch: ban NAO KHAI BAO fps khac video se bi LOAI THANG, khong chi tru diem.
$StrictFpsMatch  = $true
# Ban khong khai bao fps: van cho phep (se do bang moc thoi gian sau khi tai).
# Dat $false neu chi muon lay ban khai bao dung fps.
$AllowUnknownFps = $true
# Nguon TON QUOTA (OpenSubtitles): da tai roi thi giu lai du kiem chung yeu,
# chi canh bao de SubPrep xu ly tiep. Vut bo la vua mat quota vua khong duoc gi.
$KeepPaidDownloads = $true

# --- Kho phu de tu tai thu cong (nem file vao day, script tu chon) ---
$UsePool         = $true
$PoolFolder      = 'srt_pool'    # dat canh file .bat; nen chia thu muc con vi\ cs\ en\
$PoolTryLimit    = 8             # do toi da bao nhieu file moi ngon ngu
# Duoi muc nay coi la phim khac han -> bo luon, khong hien ra.
# Tu muc nay den $MinVerifyVotes thi van hien kem ghi chu 'can kiem tra'.
$PoolMinVotes    = 8
# $true = kho noi bo co ban DO DUOC LA KHOP thi lay luon, khong hoi SubSource/OpenSubtitles.
# Tiet kiem thoi gian va quota. Dat $false neu van muon xem het moi nguon de so sanh.
$PoolFirst       = $true
$PoolDefaultLang = ''            # '' = bo qua file khong ro ngon ngu
$SkipIfExists    = $true         # da co phu de ngon ngu do thi bo qua
$ExcludeSdh      = $true         # KHONG tai ban SDH / Hearing Impaired
$ExcludeForced   = $true         # KHONG tai ban Forced / foreignParts
$ApiPauseMs      = 1100          # nghi giua cac request (tran 60/phut)
$MaxPages        = 10            # so trang toi da (mot muc co the co 150+ ban, dung dat qua thap)

# --- OpenSubtitles.com: nguon du phong khi SubSource khong co ---
$UseOpenSubtitles = $true
$OsApiKey         = ''            # opensubtitles.com > Profile > API Consumers
$OsUsername       = ''            # bat buoc neu muon TAI (tim thi khong can)
$OsPassword       = ''
# BAT BUOC dung dang "TenUngDung vX.Y.Z". OpenSubtitles chan cac User-Agent la
# hoac User-Agent mac dinh cua PowerShell (loi 403 kong-user-agent-block).
$OsUserAgent      = 'MyApp v1.0.0'
$OsWarnRemaining  = 50            # con duoi muc nay thi canh bao
$OsStopRemaining  = 5             # con duoi muc nay thi ngung tai
# Kho tieng Viet tren OpenSubtitles co rat nhieu ban dich may. Loai het thi
# thuong khong con gi. Mac dinh chi TRU DIEM, van cho phep tai neu khong co lua chon khac.
$OsExcludeMachine = $true        # $true = loai han ban dich may / AI

$ApiBase   = 'https://api.subsource.net/api/v1'
$VideoExts = @('.mkv','.mp4','.avi','.m4v','.ts')

# Ma 2 ky tu -> ten ngon ngu ma SubSource dung
$LangMap = @{
    'en' = 'english';  'vi' = 'vietnamese'; 'cs' = 'czech'
    'ja' = 'japanese'; 'ko' = 'korean';     'zh' = 'chinese_simplified'
    'fr' = 'french';   'de' = 'german';     'es' = 'spanish'
    'th' = 'thai';     'id' = 'indonesian'; 'ar' = 'arabic'
    'pt' = 'portuguese'; 'ru' = 'russian';  'it' = 'italian'
    'pl' = 'polish';   'nl' = 'dutch';      'tr' = 'turkish'
}

# Gom cac cong tac o tren thanh danh sach lam viec
$Languages = @()
foreach ($sw in @(
    @{ C = 'vi'; On = $Lang_vi }, @{ C = 'en'; On = $Lang_en }, @{ C = 'cs'; On = $Lang_cs },
    @{ C = 'ja'; On = $Lang_ja }, @{ C = 'ko'; On = $Lang_ko }, @{ C = 'zh'; On = $Lang_zh },
    @{ C = 'fr'; On = $Lang_fr }, @{ C = 'de'; On = $Lang_de }, @{ C = 'es'; On = $Lang_es },
    @{ C = 'th'; On = $Lang_th }, @{ C = 'id'; On = $Lang_id }, @{ C = 'ar'; On = $Lang_ar }
)) {
    if ($sw.On) { $Languages += $sw.C }
}
foreach ($x in $LangExtra) {
    $c = ([string]$x).Trim().ToLower()
    if ($c) { $Languages += $c }
}
$Languages = @($Languages | Select-Object -Unique)

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

# ==========================================================
#  TU KHOA / CHAM DIEM
# ==========================================================
$NoiseWords = @'
bluray blu ray bdrip brrip bdremux remux webdl web dl webrip web hdtv dvdrip hdrip hddvd
x264 x265 h264 h265 hevc avc xvid aac ac3 eac3 dd ddp dts dtshd truehd atmos flac opus mp3 mp4 mkv
hdr dovi sdr imax proper repack extended uncut unrated remastered criterion multi dual vietsub subs sub
subtitles bit amzn nf dsnp hmax atvp hulu pcok itunes internal uhd 4k dolby vision ma
eng rus vie viet vietnamese cze czech ces cz cs en fra ger spa ita jpn kor chi
'@ -split '\s+' | Where-Object { $_ }
$StopWords = @('the','of','a','an','and','in','on','at','to','for')

function Get-Tokens([string]$name) {
    $t = $name -replace '\([^\)]*\)', ' '
    $t = $t -replace '\[[^\]]*\]', ' '
    $out = @()
    foreach ($w in ($t -split '[^A-Za-z0-9]+')) {
        if (-not $w) { continue }
        $lw = $w.ToLower()
        if ($lw.Length -lt 2) { continue }
        if ($lw -match '^\d+$' -or $lw -match '^\d+p$') { continue }
        if ($StopWords -contains $lw -or $NoiseWords -contains $lw) { continue }
        $out += $lw
    }
    return @($out | Select-Object -Unique)
}

function Get-Score($a, $b) {
    if ($a.Count -eq 0 -or $b.Count -eq 0) { return 0.0 }
    $c = 0
    foreach ($x in $a) { if ($b -contains $x) { $c++ } }
    return [double]$c / [math]::Max($a.Count, $b.Count)
}

function Get-Year([string]$n) {
    $m = [regex]::Match($n, '\b((?:19|20)\d{2})\b')
    if ($m.Success) { return [int]$m.Groups[1].Value }
    return $null
}

function Get-EpisodeInfo([string]$name) {
    $t = ($name -replace '[\._]+', ' ') -replace '\s+', ' '
    $m = [regex]::Match($t, '(?i)\bS(\d{1,2})\s?E(\d{1,4})\b')
    if ($m.Success) { return [pscustomobject]@{ Season = [int]$m.Groups[1].Value; Episode = [int]$m.Groups[2].Value } }
    $m = [regex]::Match($t, '(?<!\d)(\d{1,2})x(\d{2})(?!\d)')
    if ($m.Success) { return [pscustomobject]@{ Season = [int]$m.Groups[1].Value; Episode = [int]$m.Groups[2].Value } }
    return $null
}

# Cat ten release lay phan ten phim (bo tu ky thuat tro di)
function Get-CleanTitle([string]$name) {
    $t = $name -replace '\[[^\]]*\]', ' '
    $t = ($t -replace '[\._]+', ' ') -replace '\s+', ' '
    $junk = '(?i)\b(2160p|1080p|720p|480p|4k|uhd|bluray|blu-ray|bdrip|brrip|remux|web-?dl|webrip|web|hdtv|dvdrip|x ?26[45]|h ?26[45]|hevc|avc|aac\d*|ac3|eac3|ddp?\d?|dts|truehd|atmos|hdr\d*|imax|proper|repack|extended|unrated|multi|dual|vietsub|amzn|nf|dsnp|hmax|atvp|hulu|S\d{1,2}E\d{1,4})\b'
    $m = [regex]::Match($t, $junk)
    if ($m.Success) { $t = $t.Substring(0, $m.Index) }
    $t = $t -replace '(?i)\b(19|20)\d{2}\b', ' '
    $t = ($t -replace '[\(\)\[\]\{\}]', ' ') -replace '\s+', ' '
    return $t.Trim(" -_".ToCharArray())
}

# ==========================================================
#  CONG CU
# ==========================================================
function Join-Safe($a, $b) {
    if ([string]::IsNullOrWhiteSpace([string]$a)) { return $null }
    return (Join-Path ([string]$a) $b)
}

$mkvmerge = $null; $mkvextract = $null
foreach ($exe in @('mkvmerge.exe','mkvextract.exe')) {
    foreach ($p in @((Join-Safe $MkvToolNixDir $exe), (Join-Safe $targetDir $exe),
                     (Join-Safe $env:ProgramFiles "MKVToolNix\$exe"))) {
        if ($p -and (Test-Path -LiteralPath $p)) {
            if ($exe -eq 'mkvmerge.exe') { $mkvmerge = $p } else { $mkvextract = $p }
            break
        }
    }
}

# Doc framerate cua video tu mkvmerge (default_duration = nano giay/khung hinh)
function Get-VideoFps([string]$file) {
    if (-not $mkvmerge) { return $null }
    try {
        $raw = & $mkvmerge -J "$file" 2>&1
        if ($LASTEXITCODE -gt 1) { return $null }
        $j = ($raw -join "`n") | ConvertFrom-Json
        foreach ($t in $j.tracks) {
            if ($t.type -eq 'video' -and $t.properties.default_duration) {
                $dd = ConvertTo-Double $t.properties.default_duration
                if ($dd -gt 0) { return [math]::Round(1000000000.0 / $dd, 3) }
            }
        }
    } catch {}
    return $null
}

# ==========================================================
#  PHAN TICH THOI GIAN  (doi chieu voi phu de goc trong MKV)
# ==========================================================
# Doc so thap phan KHONG phu thuoc vung mien cua Windows.
# "23.976" phai luon ra 23.976, ke ca khi Windows dung dau phay.
function ConvertTo-Double($v) {
    if ($null -eq $v) { return 0.0 }
    $s = ([string]$v).Trim()
    if (-not $s) { return 0.0 }
    $d = 0.0
    if ([double]::TryParse($s, [System.Globalization.NumberStyles]::Float,
                           [System.Globalization.CultureInfo]::InvariantCulture, [ref]$d)) { return $d }
    # phong khi nguon ghi bang dau phay: "23,976"
    if ([double]::TryParse(($s -replace ',', '.'), [System.Globalization.NumberStyles]::Float,
                           [System.Globalization.CultureInfo]::InvariantCulture, [ref]$d)) { return $d }
    return 0.0
}

function Get-CueTimes([string]$path) {
    try { $txt = [System.IO.File]::ReadAllText($path) } catch { return $null }
    if (-not $txt) { return $null }
    $list = @()
    foreach ($m in [regex]::Matches($txt, '(\d{1,2}):(\d{2}):(\d{2})[,.](\d{2,3})\s*-->')) {
        $list += ([int]$m.Groups[1].Value) * 3600 + ([int]$m.Groups[2].Value) * 60 +
                 [int]$m.Groups[3].Value + (ConvertTo-Double ("0." + $m.Groups[4].Value))
    }
    if ($list.Count -eq 0) {
        foreach ($m in [regex]::Matches($txt, '(?im)^Dialogue:\s*[^,]*,(\d{1,2}):(\d{2}):(\d{2})\.(\d{2}),')) {
            $list += ([int]$m.Groups[1].Value) * 3600 + ([int]$m.Groups[2].Value) * 60 +
                     [int]$m.Groups[3].Value + (ConvertTo-Double ("0." + $m.Groups[4].Value))
        }
    }
    if ($list.Count -eq 0) { return $null }
    return @($list | Sort-Object)
}

$script:FpsRatios = @(
    @{ R = 1.0;         Name = 'khong co gian' },
    @{ R = (24/23.976); Name = '24 -> 23.976' },
    @{ R = (23.976/24); Name = '23.976 -> 24' },
    @{ R = (25/23.976); Name = 'PAL 25 -> NTSC 23.976' },
    @{ R = (23.976/25); Name = 'NTSC 23.976 -> PAL 25' },
    @{ R = (25/24);     Name = '25 -> 24' },
    @{ R = (24/25);     Name = '24 -> 25' }
)

function Get-EvenSample($arr, [int]$n) {
    if ($arr.Count -le $n) { return @($arr) }
    $step = [double]$arr.Count / $n
    $out = New-Object 'System.Collections.Generic.List[double]'
    for ($i = 0; $i -lt $n; $i++) { $out.Add($arr[[int]($i * $step)]) }
    return @($out)
}

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

# Quet qua tung ty le FPS, chon ty le cho nhieu phieu nhat
function Get-TimingRelation($refTimes, $tgtTimes) {
    if (-not $refTimes -or -not $tgtTimes) { return $null }
    if ($refTimes.Count -lt 30 -or $tgtTimes.Count -lt 30) { return $null }
    $R = Get-EvenSample $refTimes 400
    $T = Get-EvenSample $tgtTimes 100

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
    $lastT = $tgtTimes[$tgtTimes.Count - 1]
    return [pscustomobject]@{
        Scale = $best.Scale; Name = $best.Name; Offset = $best.Offset; Votes = $best.Votes
        Drift = [math]::Abs(($best.Scale - 1.0) * $lastT)
        FpsMismatch = ([math]::Abs($best.Scale - 1.0) -gt 0.0002)
    }
}

function Format-SrtStamp([double]$sec) {
    if ($sec -lt 0) { $sec = 0 }
    $ts = [TimeSpan]::FromSeconds($sec)
    return ("{0:D2}:{1:D2}:{2:D2},{3:D3}" -f [int]$ts.TotalHours, $ts.Minutes, $ts.Seconds, $ts.Milliseconds)
}

# ---------- DOC MOC THOI GIAN TU PGS (.sup) ----------
function Convert-PgsToTimingSrt([string]$supPath, [string]$srtPath) {
    try { $b = [System.IO.File]::ReadAllBytes($supPath) } catch { return 0 }
    if ($b.Length -lt 16) { return 0 }
    $cues = New-Object 'System.Collections.Generic.List[object]'
    $start = -1.0; $i = 0; $n = $b.Length
    while ($i + 13 -le $n) {
        if ($b[$i] -ne 0x50 -or $b[$i+1] -ne 0x47) { break }
        $pts = ((([int]$b[$i+2]) -shl 24) -bor (([int]$b[$i+3]) -shl 16) -bor
                (([int]$b[$i+4]) -shl 8)  -bor  ([int]$b[$i+5])) / 90000.0
        $stype = $b[$i+10]
        $size  = ((([int]$b[$i+11]) -shl 8) -bor ([int]$b[$i+12]))
        $body  = $i + 13
        if ($stype -eq 0x16 -and $size -ge 11 -and ($body + 10) -lt $n) {
            if ($b[$body+8] -eq 0) {
                $nobj = $b[$body+10]
                if ($nobj -gt 0) {
                    if ($start -ge 0 -and ($pts - $start) -gt 0.1) { $cues.Add([pscustomobject]@{ S=$start; E=$pts }) }
                    $start = $pts
                } else {
                    if ($start -ge 0 -and ($pts - $start) -gt 0.1) { $cues.Add([pscustomobject]@{ S=$start; E=$pts }) }
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
        [void]$sb.AppendLine('*')
        [void]$sb.AppendLine('')
    }
    try { [System.IO.File]::WriteAllText($srtPath, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false))) }
    catch { return 0 }
    return $cues.Count
}

# ---------- LAY MOC THOI GIAN GOC TU CHINH FILE MKV ----------
$script:RefCache = @{}

# Tim file moc trong srt_goc khi ten KHONG trung ten phim.
# Uu tien dung so tap; neu khong co so tap thi so do giong ten.
function Find-RefInFolder($video, [string]$exDir) {
    if (-not (Test-Path -LiteralPath $exDir)) { return $null }
    $cands = @(Get-ChildItem -LiteralPath $exDir -File -ErrorAction SilentlyContinue |
               Where-Object { @('.srt','.ass','.ssa','.sup') -contains $_.Extension.ToLower() })
    if ($cands.Count -eq 0) { return $null }

    # Bo nhung file ghi ro la tap khac
    $cands = @($cands | Where-Object { -not (Test-EpisodeConflict $video.BaseName $_.BaseName) })
    if ($cands.Count -eq 0) { return $null }

    # Co file ghi DUNG so tap thi chi xet nhung file do
    $myEp = Get-EpisodeInfo $video.BaseName
    if ($myEp) {
        $exact = @($cands | Where-Object {
            $e = Get-EpisodeInfo $_.BaseName
            $e -and $e.Season -eq $myEp.Season -and $e.Episode -eq $myEp.Episode
        })
        if ($exact.Count -gt 0) { $cands = $exact }
    }

    $vTok = Get-Tokens $video.BaseName
    $best = $null; $bs = -1
    foreach ($c in $cands) {
        $sc = Get-Score $vTok (Get-Tokens $c.BaseName)
        if ($myEp) { $sc += 1.0 }        # da dung tap thi ten khong con quan trong
        if ($sc -gt $bs) { $bs = $sc; $best = $c }
    }
    if (-not $best -or $bs -lt 0.35) { return $null }

    # File PGS -> doc moc thoi gian ra .srt truoc khi dung
    if ($best.Extension.ToLower() -eq '.sup') {
        $conv = [System.IO.Path]::ChangeExtension($best.FullName, '.srt')
        if (-not [System.IO.File]::Exists($conv)) {
            if ((Convert-PgsToTimingSrt $best.FullName $conv) -eq 0) { return $null }
            W "    [pgs] $($best.Name) -> $(Split-Path $conv -Leaf)" 'DarkCyan'
        }
        return $conv
    }
    return $best.FullName
}

function Get-RefTimes($video) {
    $key = $video.FullName.ToLower()
    if ($script:RefCache.ContainsKey($key)) { return $script:RefCache[$key] }
    $script:RefCache[$key] = $null
    if (-not $mkvmerge -or -not $mkvextract) { return $null }
    if ($video.Extension.ToLower() -ne '.mkv') { return $null }

    try {
        $raw = & $mkvmerge -J "$($video.FullName)" 2>&1
        if ($LASTEXITCODE -gt 1) { return $null }
        $j = ($raw -join "`n") | ConvertFrom-Json
    } catch { return $null }

    $best = $null; $bestScore = 9999
    foreach ($t in @($j.tracks | Where-Object { $_.type -eq 'subtitles' })) {
        $p  = $t.properties
        $cid = [string]$p.codec_id
        $ext = $null; $isPgs = $false
        if     ($cid -match 'S_TEXT/UTF8')   { $ext = '.srt' }
        elseif ($cid -match 'S_TEXT/ASS')    { $ext = '.ass' }
        elseif ($cid -match 'S_TEXT/SSA')    { $ext = '.ssa' }
        elseif ($cid -match 'S_HDMV/PGS')    { $ext = '.sup'; $isPgs = $true }
        else { continue }

        $nm  = [string]$p.track_name
        $frc = ($nm -match '(?i)forced') -or ($p.forced_track -eq $true) -or ($p.flag_forced -eq $true)
        if ($frc) { continue }                      # forced chi vai dong -> vo dung lam moc

        $sc = 0
        if ($isPgs) { $sc += 100 }                  # uu tien phu de van ban
        if (($nm -match '(?i)\bSDH\b|hearing') -or ($p.flag_hearing_impaired -eq $true)) { $sc += 5 }
        if ($sc -lt $bestScore) {
            $bestScore = $sc
            $best = [pscustomobject]@{ Id = [int]$t.id; Ext = $ext; IsPgs = $isPgs }
        }
    }
    if (-not $best) { return $null }

    # Noi luu: srt_goc (giu lai) hoac thu muc tam (xoa cuoi phien)
    if ($KeepExtractedRef -and $ExtractFolder) {
        $exDir = Join-Path $targetDir $ExtractFolder
        if (-not (Test-Path -LiteralPath $exDir)) { New-Item -ItemType Directory -Path $exDir -Force | Out-Null }
        $finalSrt = Join-Path $exDir ($video.BaseName + '.srt')

        # 1. Trung ten tuyet doi -> dung lai luon
        if ([System.IO.File]::Exists($finalSrt)) {
            $t0 = Get-CueTimes $finalSrt
            if ($t0 -and $t0.Count -gt 10) {
                $script:RefCache[$key] = $t0
                $script:RefPathCache[$key] = $finalSrt
                W "    Moc goc: dung lai $ExtractFolder\$($video.BaseName).srt ($($t0.Count) dong)" 'DarkGray'
                return $t0
            }
        }

        # 2. Ban tu bo file moc vao srt_goc voi ten khac -> van tim ra
        $found = Find-RefInFolder $video $exDir
        if ($found) {
            $t0 = Get-CueTimes $found
            if ($t0 -and $t0.Count -gt 10) {
                $script:RefCache[$key] = $t0
                $script:RefPathCache[$key] = $found
                W "    Moc goc: $ExtractFolder\$(Split-Path $found -Leaf) ($($t0.Count) dong)" 'DarkGray'
                return $t0
            }
        }
        $raw1 = Join-Path $exDir ($video.BaseName + $best.Ext)
        $srtPath = $finalSrt
    } else {
        if (-not (Test-Path -LiteralPath $tempRoot)) { New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null }
        $raw1 = Join-Path $tempRoot ("ref_" + [guid]::NewGuid().ToString('N').Substring(0,8) + $best.Ext)
        $srtPath = $raw1
    }

    & $mkvextract tracks "$($video.FullName)" "$($best.Id):$raw1" 2>&1 | Out-Null
    if (-not (Test-Path -LiteralPath $raw1)) { return $null }

    if ($best.IsPgs) {
        # PGS: doc moc thoi gian ra .srt roi bo file .sup
        if ($srtPath -eq $raw1) { $srtPath = [System.IO.Path]::ChangeExtension($raw1, '.srt') }
        $cnt = Convert-PgsToTimingSrt $raw1 $srtPath
        Remove-Item -LiteralPath $raw1 -Force -ErrorAction SilentlyContinue
        if ($cnt -eq 0) { return $null }
    } elseif ($srtPath -ne $raw1) {
        # Track van ban co the la .ass/.ssa - doi ten cho thong nhat
        try { [System.IO.File]::Copy($raw1, $srtPath, $true); [System.IO.File]::Delete($raw1) } catch { $srtPath = $raw1 }
    }
    $times = Get-CueTimes $srtPath
    $script:RefCache[$key] = $times
    $script:RefPathCache[$key] = $srtPath
    if ($times) {
        $kind = if ($best.IsPgs) { 'PGS' } else { 'van ban' }
        W "    Moc goc: track #$($best.Id) ($kind), $($times.Count) dong" 'DarkGray'
    }
    return $times
}

# ==========================================================
#  KHO PHU DE TU TAI THU CONG  (nguon du phong cuoi cung)
# ==========================================================
# Khac voi nguon online: file da nam san tren dia nen khong ton quota.
# Vi vay ta DO THANG tung ung vien thay vi doan theo ten.
$script:PoolIndex = $null
$script:ChoiceCache = @{}
$script:UsedExisting = 0

function Get-PoolLang([System.IO.FileInfo]$f, [string]$poolRoot) {
    # 1. Thu muc con cap 1 ngay duoi kho: srt_pool\vi\...
    $rel = $f.FullName
    if ($rel.StartsWith($poolRoot, 'OrdinalIgnoreCase')) { $rel = $rel.Substring($poolRoot.Length) }
    $segs = @(($rel -split '[\\/]') | Where-Object { $_ })
    if ($segs.Count -gt 1) {
        $c = ConvertTo-PoolLangCode $segs[0]
        if ($c) { return $c }
    }
    # 2. Hau to trong ten file: Phim.vi.srt / Phim.vietnamese.srt
    $m = [regex]::Match($f.BaseName, '[\._\-\[\(]([A-Za-z]{2,12})$')
    if ($m.Success) {
        $c = ConvertTo-PoolLangCode $m.Groups[1].Value
        if ($c) { return $c }
    }
    return ''
}

function ConvertTo-PoolLangCode([string]$w) {
    if (-not $w) { return '' }
    $x = $w.ToLower().Trim()
    if ($x -match '^(vi|vie|viet|vietnamese|tiengviet)$') { return 'vi' }
    if ($x -match '^(cs|cz|cze|ces|czech|sec)$')          { return 'cs' }
    if ($x -match '^(en|eng|english|anh)$')               { return 'en' }
    if ($x -match '^(ja|jpn|jp|japanese)$')               { return 'ja' }
    if ($x -match '^(ko|kor|korean)$')                    { return 'ko' }
    if ($x -match '^(zh|chi|zho|chinese)$')               { return 'zh' }
    if ($x -match '^(fr|fre|fra|french)$')                { return 'fr' }
    if ($x -match '^(de|ger|deu|german)$')                { return 'de' }
    if ($x -match '^(es|spa|spanish)$')                   { return 'es' }
    if ($x -match '^(th|tha|thai)$')                      { return 'th' }
    return ''
}

# Quet kho mot lan, phan loai theo ngon ngu
function Initialize-Pool([string]$poolRoot) {
    if ($null -ne $script:PoolIndex) { return $script:PoolIndex }
    $script:PoolIndex = @{}
    if (-not (Test-Path -LiteralPath $poolRoot)) { return $script:PoolIndex }

    $files = @(Get-ChildItem -LiteralPath $poolRoot -File -Recurse -ErrorAction SilentlyContinue |
               Where-Object { @('.srt','.ass','.ssa') -contains $_.Extension.ToLower() })

    $unknown = 0
    foreach ($f in $files) {
        # bo ban Forced / SDH theo ten file
        if ($ExcludeForced -and $f.BaseName -match '(?i)\bforced\b') { continue }
        if ($ExcludeSdh    -and $f.BaseName -match '(?i)\b(sdh|hearing|hi)\b') { continue }

        $lg = Get-PoolLang $f $poolRoot
        if (-not $lg) {
            if ($PoolDefaultLang) { $lg = $PoolDefaultLang } else { $unknown++; continue }
        }
        if (-not $script:PoolIndex.ContainsKey($lg)) { $script:PoolIndex[$lg] = @() }
        $script:PoolIndex[$lg] += $f
    }

    if ($files.Count -gt 0) {
        $parts = @($script:PoolIndex.Keys | Sort-Object | ForEach-Object { "$_($($script:PoolIndex[$_].Count))" })
        W "[i] Kho phu de '$PoolFolder': $($files.Count) file - $($parts -join ', ')" 'DarkCyan'
        if ($unknown -gt 0) {
            W "    ($unknown file khong ro ngon ngu, da bo qua - dat vao thu muc con vi\\ cs\\ en\\ hoac them hau to)" 'DarkGray'
        }
    }
    return $script:PoolIndex
}

# ==========================================================
#  GOI API
# ==========================================================
$script:ApiCalls = 0

function Invoke-Sub([string]$path, [hashtable]$params) {
    $qs = @()
    if ($params) {
        foreach ($k in $params.Keys) {
            $v = [string]$params[$k]
            if ($v -ne '') { $qs += "$k=$([uri]::EscapeDataString($v))" }
        }
    }
    $url = "$ApiBase$path"
    if ($qs.Count -gt 0) { $url += '?' + ($qs -join '&') }

    if ($script:ApiCalls -gt 0) { Start-Sleep -Milliseconds $ApiPauseMs }
    $script:ApiCalls++

    for ($try = 1; $try -le 3; $try++) {
        try {
            $r = Invoke-WebRequest -Uri $url -Headers @{ 'X-API-Key' = $SubSourceApiKey } `
                                   -UseBasicParsing -TimeoutSec 25 -ErrorAction Stop
            return ($r.Content | ConvertFrom-Json)
        } catch {
            $code = -1
            try { $code = [int]$_.Exception.Response.StatusCode } catch {}
            if ($code -eq 429) {
                W "    [!] Cham tran rate limit, cho 20 giay..." 'Yellow'
                Start-Sleep -Seconds 20
                continue
            }
            $msg = ''
            if ($_.ErrorDetails -and $_.ErrorDetails.Message) { $msg = $_.ErrorDetails.Message }
            if ($code -ge 400 -and $code -lt 500) {
                W "    [!] API $code : $($msg -replace '\s+',' ')" 'DarkYellow'
                return $null
            }
            if ($try -lt 3) { Start-Sleep -Seconds (2 * $try) } else { W "    [!] Loi mang: $($_.Exception.Message)" 'Red' }
        }
    }
    return $null
}

# Lay IMDb ID tu TMDB (chinh xac hon tim theo ten)
function Get-ImdbId([string]$title, $year, $isTv) {
    if (-not $TmdbApiKey -or -not $title) { return $null }
    $kind = if ($isTv) { 'tv' } else { 'movie' }
    $u = "https://api.themoviedb.org/3/search/${kind}?api_key=$TmdbApiKey&query=$([uri]::EscapeDataString($title))"
    if ($year) { $u += if ($isTv) { "&first_air_date_year=$year" } else { "&year=$year" } }
    try {
        $r = Invoke-RestMethod -Uri $u -TimeoutSec 20 -ErrorAction Stop
        if (-not $r.results -or @($r.results).Count -eq 0) { return $null }
        $id = $r.results[0].id
        $e = Invoke-RestMethod -Uri "https://api.themoviedb.org/3/$kind/$id/external_ids?api_key=$TmdbApiKey" -TimeoutSec 20 -ErrorAction Stop
        if ($e.imdb_id) { return [string]$e.imdb_id }
    } catch {}
    return $null
}

# ==========================================================
#  TIM MUC PHIM TREN SUBSOURCE
# ==========================================================
$script:EntryCache = @{}
$script:ImdbCache  = @{}

# Tra IMDb ID mot lan roi dung lai cho ca hai nguon
function Get-ImdbIdCached([string]$title, $year, [bool]$isTv) {
    $k = "$title|$year|$isTv"
    if ($script:ImdbCache.ContainsKey($k)) { return $script:ImdbCache[$k] }
    $v = Get-ImdbId $title $year $isTv
    $script:ImdbCache[$k] = $v
    if ($v) { W "    IMDb: $v" 'DarkGray' }
    return $v
}

function Find-Entry([string]$cleanTitle, $year, $season) {
    $key = "$cleanTitle|$year|$season"
    if ($script:EntryCache.ContainsKey($key)) { return $script:EntryCache[$key] }

    $cands = @()

    # 1. Uu tien tra bang IMDb ID (da co san trong bo dem)
    $imdb = Get-ImdbIdCached $cleanTitle $year ($null -ne $season)
    if ($imdb) {
        $r = Invoke-Sub '/movies/search' @{ searchType = 'imdb'; imdb = $imdb }
        if ($r -and $r.data) { $cands = @($r.data) }
    }
    # 2. Khong duoc thi tim theo ten
    if ($cands.Count -eq 0) {
        $r = Invoke-Sub '/movies/search' @{ searchType = 'text'; q = $cleanTitle }
        if ($r -and $r.data) { $cands = @($r.data) }
    }
    if ($cands.Count -eq 0) { $script:EntryCache[$key] = $null; return $null }

    # Loc theo season (phim bo) hoac theo nam (phim le)
    if ($null -ne $season) {
        $bySeason = @($cands | Where-Object { $_.season -eq $season })
        if ($bySeason.Count -gt 0) { $cands = $bySeason }
        else {
            W "    [!] Khong co season $season tren SubSource (co: $((@($cands | ForEach-Object { $_.season }) | Sort-Object -Unique) -join ', '))" 'Yellow'
            $script:EntryCache[$key] = $null; return $null
        }
    } elseif ($year) {
        $byYear = @($cands | Where-Object { $_.releaseYear -and [math]::Abs([int]$_.releaseYear - $year) -le 1 })
        if ($byYear.Count -gt 0) { $cands = $byYear }
    }

    # Con nhieu thi lay cai giong ten nhat
    $want = Get-Tokens $cleanTitle
    $best = $null; $bestScore = -1
    foreach ($c in $cands) {
        $s = [math]::Max((Get-Score $want (Get-Tokens ([string]$c.title))),
                         (Get-Score $want (Get-Tokens ([string]$c.alternateTitle))))
        if ($s -gt $bestScore) { $bestScore = $s; $best = $c }
    }
    $script:EntryCache[$key] = $best
    return $best
}

# ==========================================================
#  LIET KE PHU DE
# ==========================================================
$script:ListCache = @{}

# Lay TOAN BO phu de cua mot movieId, khong loc gi o phia may chu.
# Ly do: cac ban do nguoi dung tu dang thuong de trong hearingImpaired/foreignParts,
# neu loc bang tham so thi chung bi loai oan (tieng Viet hay dinh loi nay).
function Get-AllSubs([int]$movieId) {
    $key = "all|$movieId"
    if ($script:ListCache.ContainsKey($key)) { return $script:ListCache[$key] }

    $all = @()
    $limit = 100
    for ($page = 1; $page -le $MaxPages; $page++) {
        $r = Invoke-Sub '/subtitles' @{ movieId = $movieId; page = $page; limit = "$limit" }
        if (-not $r -or -not $r.data) {
            if ($page -eq 1 -and $limit -eq 100) {
                # May chu co the khong chap nhan limit=100 -> thu lai voi 20
                $limit = 20
                $r = Invoke-Sub '/subtitles' @{ movieId = $movieId; page = 1; limit = '20' }
                if (-not $r -or -not $r.data) { break }
            } else { break }
        }
        $all += @($r.data)
        if (-not $r.pagination) { break }
        if ($page -ge [int]$r.pagination.pages) { break }
    }
    $script:ListCache[$key] = $all
    return $all
}

# So khop ten ngon ngu cua SubSource voi ma 2 ky tu cua ta
function Test-SubLang($sub, [string]$code, [string]$fullName) {
    $v = ([string]$sub.language).ToLower().Trim()
    if (-not $v) { return $false }
    if ($v -eq $fullName) { return $true }
    if ($v -eq $code) { return $true }
    # phong truong hop ten co bien the: vietnamese / viet / vi-vn ...
    switch ($code) {
        'vi' { return ($v -like 'viet*' -or $v -like 'vi[-_]*') }
        'cs' { return ($v -like 'czech*' -or $v -like 'cze*' -or $v -like 'cs[-_]*') }
        'en' { return ($v -like 'english*' -or $v -like 'en[-_]*') }
    }
    return $false
}

function Get-SubList([int]$movieId, [string]$langName, [string]$langCode) {
    $all = Get-AllSubs $movieId
    if ($all.Count -eq 0) { return @() }
    return @($all | Where-Object { Test-SubLang $_ $langCode $langName })
}

# Liet ke cac ngon ngu that su co - dung de bao cho nguoi dung biet
function Get-LangSummary($all) {
    $t = @{}
    foreach ($x in $all) {
        $v = ([string]$x.language).ToLower().Trim()
        if (-not $v) { $v = '(trong)' }
        if ($t.ContainsKey($v)) { $t[$v]++ } else { $t[$v] = 1 }
    }
    return @($t.GetEnumerator() | Sort-Object -Property @{Expression={$_.Value};Descending=$true} |
             ForEach-Object { "$($_.Name)($($_.Value))" })
}

# ==========================================================
#  CHAM DIEM UNG VIEN
# ==========================================================
# Hang 1 = fps trung khop | Hang 2 = khong khai bao | Hang 3 = fps khac video
function Get-FpsTier($declaredFps, $videoFps) {
    if (-not $videoFps) { return 2 }
    $f = ConvertTo-Double $declaredFps
    if ($f -le 0) { return 2 }
    if ([math]::Abs($f - $videoFps) -lt 0.05) { return 1 }
    return 3
}

function Get-FpsTierText([int]$tier, $declaredFps) {
    switch ($tier) {
        1 { "fps $declaredFps KHOP" }
        2 { 'fps khong khai bao' }
        default { "fps $declaredFps LECH" }
    }
}

function Get-CandidateScore($sub, $videoTokens, $videoFps, [string]$videoName) {
    $best = 0.0
    foreach ($ri in @($sub.releaseInfo)) {
        if (-not $ri) { continue }
        $s = Get-Score $videoTokens (Get-Tokens ([string]$ri))
        if ($s -gt $best) { $best = $s }
    }
    # Chi xet do giong TEN PHIM. Framerate duoc xu ly rieng bang xep hang.
    return $best
}

# ==========================================================
#  OPENSUBTITLES.COM  (nguon du phong)
# ==========================================================
$script:OsToken     = $null
$script:OsBase      = 'api.opensubtitles.com'
$script:OsRemaining = -1
$script:OsAllowed   = -1
$script:OsDisabled  = $false
$script:OsResetMsg  = ''
$script:OsRelogins  = 0

# PowerShell doi khi de trong ErrorDetails -> phai doc thang tu luong phan hoi
function Get-ErrBody($errRec) {
    $b = ''
    try { if ($errRec.ErrorDetails -and $errRec.ErrorDetails.Message) { $b = [string]$errRec.ErrorDetails.Message } } catch {}
    if (-not $b) {
        try {
            $resp = $errRec.Exception.Response
            if ($resp) {
                $st = $resp.GetResponseStream()
                $st.Position = 0
                $sr = New-Object System.IO.StreamReader($st, [System.Text.Encoding]::UTF8)
                $b = $sr.ReadToEnd(); $sr.Close()
            }
        } catch {}
    }
    if (-not $b) { try { $b = [string]$errRec.Exception.Message } catch {} }
    return ($b -replace '\s+', ' ')
}

function Invoke-Os([string]$method, [string]$path, [hashtable]$params, $bodyObj) {
    $qs = @()
    if ($params) {
        foreach ($k in $params.Keys) {
            $v = [string]$params[$k]
            if ($v -ne '') { $qs += "$k=$([uri]::EscapeDataString($v))" }
        }
    }
    $suffix = ''
    if ($qs.Count -gt 0) { $suffix = '?' + ($qs -join '&') }

    for ($try = 1; $try -le 3; $try++) {
        # Dung lai o MOI lan thu: sau khi dang nhap lai, may chu va token co the da doi
        $url = "https://$($script:OsBase)/api/v1$path$suffix"
        # KHONG dat User-Agent trong -Headers: PowerShell coi la header han che va bo qua,
        # roi gui User-Agent mac dinh -> bi chan. Phai dung tham so -UserAgent.
        $h = @{
            'Api-Key' = $OsApiKey
            'Accept'  = 'application/json'
        }
        if ($script:OsToken) { $h['Authorization'] = "Bearer $($script:OsToken)" }

        try {
            if ($bodyObj) {
                $json = $bodyObj | ConvertTo-Json -Compress
                $r = Invoke-WebRequest -Uri $url -Method $method -Headers $h -Body $json `
                                       -ContentType 'application/json' -UserAgent $OsUserAgent `
                                       -UseBasicParsing -TimeoutSec 30 -ErrorAction Stop
            } else {
                $r = Invoke-WebRequest -Uri $url -Method $method -Headers $h `
                                       -UserAgent $OsUserAgent `
                                       -UseBasicParsing -TimeoutSec 30 -ErrorAction Stop
            }
            return ($r.Content | ConvertFrom-Json)
        } catch {
            $code = -1
            try { $code = [int]$_.Exception.Response.StatusCode } catch {}
            $msg = Get-ErrBody $_

            if ($code -eq 403 -and $msg -match 'user-agent') {
                W "    [!] OpenSubtitles chan User-Agent." 'Red'
                W "        Dat `$OsUserAgent theo dang 'TenApp v1.0.0' (co du 3 so)." 'Yellow'
                W "        Neu van bi chan, dang ky ten app tai opensubtitles.com > API Consumers." 'Yellow'
                $script:OsDisabled = $true
                return $null
            }
            if ($code -eq 401) {
                W "    [!] OpenSubtitles tu choi xac thuc (401) tai $path" 'Yellow'
                if ($msg) { W "        May chu noi: $msg" 'DarkGray' }

                # Token co the het han hoac bi vo hieu -> dang nhap lai mot lan roi thu tiep
                if ($path -ne '/login' -and $script:OsRelogins -lt 2) {
                    $script:OsRelogins++
                    W "        Dang dang nhap lai (lan $($script:OsRelogins))..." 'Yellow'
                    $script:OsToken = $null
                    Start-Sleep -Seconds 2
                    if (Connect-OpenSubtitles -Quiet) {
                        W "        Da lay token moi, thu lai yeu cau." 'DarkGray'
                        continue
                    }
                }
                W "        Khong khac phuc duoc -> tat nguon OpenSubtitles." 'Red'
                $script:OsDisabled = $true
                return $null
            }
            if ($code -eq 429) {
                W "    [!] OpenSubtitles gioi han tan suat, cho 5 giay..." 'Yellow'
                Start-Sleep -Seconds 5
                continue
            }
            if ($code -ge 400 -and $code -lt 500) {
                if ($msg) { W "    [!] OpenSubtitles $code : $msg" 'DarkYellow' }
                return $null
            }
            if ($try -lt 3) { Start-Sleep -Seconds (2 * $try) }
        }
    }
    return $null
}

function Connect-OpenSubtitles([switch]$Quiet) {
    if (-not $UseOpenSubtitles) { return $false }
    if (-not $OsApiKey) { W "[i] Chua co `$OsApiKey -> bo qua OpenSubtitles." 'DarkGray'; return $false }
    if (-not $OsUsername -or -not $OsPassword) {
        W "[!] Thieu tai khoan OpenSubtitles -> chi tim duoc, KHONG tai duoc." 'Yellow'
        return $false
    }

    $r = Invoke-Os 'POST' '/login' $null @{ username = $OsUsername; password = $OsPassword }
    if (-not $r -or -not $r.token) { W "[!] Dang nhap OpenSubtitles that bai." 'Red'; $script:OsDisabled = $true; return $false }

    $script:OsToken = $r.token
    if ($r.base_url) { $script:OsBase = $r.base_url }   # VIP duoc chuyen sang vip-api...
    if ($r.user) {
        $script:OsAllowed = [int]$r.user.allowed_downloads
        $vipText = if ($r.user.vip) { 'VIP' } else { 'thuong' }
        if (-not $Quiet) {
            W "[i] OpenSubtitles: da dang nhap ($vipText, muc '$($r.user.level)', $($script:OsAllowed) luot tai/ngay)" 'Green'
            W "    May chu: $($script:OsBase)" 'DarkGray'
        }
    }

    # Hoi so du hien tai
    if (-not $Quiet) {
        $u = Invoke-Os 'GET' '/infos/user' $null $null
        if ($u -and $u.data) {
            if ($null -ne $u.data.remaining_downloads) { $script:OsRemaining = [int]$u.data.remaining_downloads }
            if ($null -ne $u.data.allowed_downloads)   { $script:OsAllowed   = [int]$u.data.allowed_downloads }
            W "    Con lai hom nay: $($script:OsRemaining)/$($script:OsAllowed) luot tai" 'Cyan'
        }
    }
    return $true
}

# Canh bao khi sap het quota
function Test-OsQuota {
    if ($script:OsRemaining -lt 0) { return $true }        # chua biet -> cu thu
    if ($script:OsRemaining -le $OsStopRemaining) {
        if (-not $script:OsDisabled) {
            W "`n[!!] OpenSubtitles chi con $($script:OsRemaining) luot tai - DUNG dung nguon nay." 'Red'
            if ($script:OsResetMsg) { W "     $($script:OsResetMsg)" 'Yellow' }
            $script:OsDisabled = $true
        }
        return $false
    }
    if ($script:OsRemaining -le $OsWarnRemaining) {
        W "    [!] Chu y: OpenSubtitles chi con $($script:OsRemaining)/$($script:OsAllowed) luot tai hom nay." 'Yellow'
    }
    return $true
}

# Tim phu de tren OpenSubtitles
function Get-OsCandidates([string]$imdb, $ep, [string]$langCode, [string]$title, $year) {
    $p = @{ languages = $langCode }
    if ($imdb) {
        $p['imdb_id'] = ($imdb -replace '^tt', '')
    } elseif ($title) {
        $p['query'] = $title
        if ($year) { $p['year'] = "$year" }
    } else { return @() }

    if ($ep) {
        $p['season_number']  = "$($ep.Season)"
        $p['episode_number'] = "$($ep.Episode)"
    }
    # Lan 1: co sap xep theo luot tai
    $p1 = $p.Clone()
    $p1['order_by'] = 'download_count'
    $p1['order_direction'] = 'desc'
    $r = Invoke-Os 'GET' '/subtitles' $p1 $null
    if ($r -and $r.data -and @($r.data).Count -gt 0) { return @($r.data) }

    # Lan 2: bo tham so sap xep (co the may chu khong nhan)
    Start-Sleep -Milliseconds $ApiPauseMs
    $r = Invoke-Os 'GET' '/subtitles' $p $null
    if ($r -and $r.data -and @($r.data).Count -gt 0) { return @($r.data) }

    # Lan 3: doi tu tra theo IMDb sang tim theo ten
    if ($p.ContainsKey('imdb_id') -and $title) {
        Start-Sleep -Milliseconds $ApiPauseMs
        $p3 = @{ languages = $langCode; query = $title }
        if ($year) { $p3['year'] = "$year" }
        if ($ep) {
            $p3['season_number']  = "$($ep.Season)"
            $p3['episode_number'] = "$($ep.Episode)"
        }
        $r = Invoke-Os 'GET' '/subtitles' $p3 $null
        if ($r -and $r.data) {
            if (@($r.data).Count -gt 0) { W "        (tim theo ten thay vi IMDb)" 'DarkGray' }
            return @($r.data)
        }
    }
    return @()
}

# Cham diem ung vien OpenSubtitles (thang diem giong SubSource de so sanh duoc)
function Get-OsScore($item, $videoTokens, $videoFps, [string]$videoName) {
    $a = $item.attributes
    if (-not $a) { return -999 }

    $rel = [string]$a.release
    $score = (Get-Score $videoTokens (Get-Tokens $rel)) * 100

    if ($videoFps) {
        $f = ConvertTo-Double $a.fps
        if ($f -gt 0) {
            if ([math]::Abs($f - $videoFps) -lt 0.05) { $score += 25 } else { $score -= 25 }
        } else {
            $score -= 5
        }
    }
    if ($a.download_count) { $score += [math]::Min(10, [math]::Log10([math]::Max(1, [int]$a.download_count)) * 3) }
    if ($a.from_trusted -eq $true) { $score += 5 }
    # Dich may van dung duoc nhung kem hon han -> tru diem thay vi loai
    if ($a.machine_translated -eq $true -or $a.ai_translated -eq $true) { $score -= 15 }
    $rt = ConvertTo-Double $a.ratings
    if ($rt -gt 0) { $score += $rt / 2.0 }
    return $score
}

# Loai ban SDH / Forced / dich may
function Test-OsUsable($item) {
    $a = $item.attributes
    if (-not $a) { return $false }
    if ($ExcludeSdh    -and $a.hearing_impaired     -eq $true) { return $false }
    if ($ExcludeForced -and $a.foreign_parts_only   -eq $true) { return $false }
    if ($OsExcludeMachine) {
        if ($a.machine_translated -eq $true) { return $false }
        if ($a.ai_translated      -eq $true) { return $false }
    }
    if (-not $a.files -or @($a.files).Count -eq 0) { return $false }
    return $true
}

# Tai mot phu de: POST /download lay link tam, roi GET link do
function Save-OsSubtitle($item, [string]$dest) {
    if ($script:OsDisabled) { return $false }
    if (-not (Test-OsQuota)) { return $false }
    if (-not $script:OsToken) { W "        [!] Chua dang nhap -> khong tai duoc." 'Yellow'; return $false }

    $fileId = [int](@($item.attributes.files)[0].file_id)
    if (-not $fileId) { return $false }

    Start-Sleep -Milliseconds $ApiPauseMs
    $tk = if ($script:OsToken) { "co token (" + $script:OsToken.Length + " ky tu)" } else { 'KHONG co token' }
    W "        POST /download file_id=$fileId | may chu $($script:OsBase) | $tk" 'DarkGray'
    $d = Invoke-Os 'POST' '/download' $null @{ file_id = $fileId }
    if (-not $d -or -not $d.link) {
        if ($d -and $d.message) { W "        [!] $($d.message)" 'Yellow' }
        return $false
    }

    # Cap nhat so du ngay tu phan hoi
    if ($null -ne $d.remaining) { $script:OsRemaining = [int]$d.remaining }
    if ($d.message)    { $script:OsResetMsg = [string]$d.message }
    elseif ($d.reset_time) { $script:OsResetMsg = "Quota se hoi lai sau $($d.reset_time)" }

    # Tai vao thu muc tam (ten sach, khong co ky tu dai dien) roi moi chep sang dich
    if (-not (Test-Path -LiteralPath $tempRoot)) { New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null }
    $tmpDl = Join-Path $tempRoot ("os_" + [guid]::NewGuid().ToString('N').Substring(0,10) + ".srt")
    try {
        Invoke-WebRequest -Uri $d.link -UserAgent $OsUserAgent -UseBasicParsing `
                          -TimeoutSec 60 -OutFile $tmpDl -ErrorAction Stop | Out-Null
    } catch {
        W "        [!] Tai file that bai: $($_.Exception.Message)" 'Red'
        return $false
    }
    if (-not (Test-Path -LiteralPath $tmpDl) -or (Get-Item -LiteralPath $tmpDl).Length -lt 64) {
        Remove-Item -LiteralPath $tmpDl -Force -ErrorAction SilentlyContinue
        W "        [!] File tai ve rong." 'Yellow'
        return $false
    }
    if (-not (Save-ToDest $tmpDl $dest)) { return $false }
    W "        (con $($script:OsRemaining)/$($script:OsAllowed) luot tai hom nay)" 'DarkGray'
    [void](Test-OsQuota)
    return $true
}

# ==========================================================
#  TAI VA GIAI NEN
# ==========================================================
$script:ZipCache = @{}
$tempRoot = Join-Path $env:TEMP ("subfetch_" + [guid]::NewGuid().ToString('N').Substring(0,8))

function Get-SubtitlePack([int]$subId) {
    if ($script:ZipCache.ContainsKey($subId)) { return $script:ZipCache[$subId] }
    if (-not (Test-Path -LiteralPath $tempRoot)) { New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null }

    $zip = Join-Path $tempRoot "$subId.zip"
    $dir = Join-Path $tempRoot "$subId"
    try {
        if ($script:ApiCalls -gt 0) { Start-Sleep -Milliseconds $ApiPauseMs }
        $script:ApiCalls++
        Invoke-WebRequest -Uri "$ApiBase/subtitles/$subId/download" `
                          -Headers @{ 'X-API-Key' = $SubSourceApiKey } `
                          -UseBasicParsing -TimeoutSec 90 -OutFile $zip -ErrorAction Stop | Out-Null
    } catch {
        W "      [!] Tai that bai (id=$subId): $($_.Exception.Message)" 'Red'
        $script:ZipCache[$subId] = $null; return $null
    }

    $bytes = [System.IO.File]::ReadAllBytes($zip)
    if ($bytes.Length -lt 4) { $script:ZipCache[$subId] = $null; return $null }
    if (-not ($bytes[0] -eq 0x50 -and $bytes[1] -eq 0x4B)) {
        $magic = if ($bytes[0] -eq 0x52) { 'RAR' } elseif ($bytes[0] -eq 0x37) { '7Z' } else { 'khong ro' }
        W "      [!] File tai ve khong phai ZIP ($magic) - can 7-Zip, bo qua." 'Yellow'
        $script:ZipCache[$subId] = $null; return $null
    }
    try {
        if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
        Expand-Archive -LiteralPath $zip -DestinationPath $dir -Force -ErrorAction Stop
    } catch {
        W "      [!] Giai nen loi: $($_.Exception.Message)" 'Red'
        $script:ZipCache[$subId] = $null; return $null
    }
    $script:ZipCache[$subId] = $dir
    return $dir
}

# Chon dung file trong goi (season pack co the co hang chuc file)
function Select-FromPack([string]$dir, $ep, $videoTokens) {
    $files = @(Get-ChildItem -LiteralPath $dir -File -Recurse |
               Where-Object { @('.srt','.ass','.ssa') -contains $_.Extension.ToLower() })
    if ($files.Count -eq 0) { return $null }

    # Trong goi thuong lan ca ban Forced va SDH - loai theo ten file
    $pat = @()
    if ($ExcludeForced) { $pat += 'forced' }
    if ($ExcludeSdh)    { $pat += 'sdh|hi\b|hearing|cc\b' }
    if ($pat.Count -gt 0) {
        $rx = '(?i)\b(' + ($pat -join '|') + ')'
        $normal = @($files | Where-Object { $_.BaseName -notmatch $rx })
        if ($normal.Count -gt 0) { $files = $normal }
        else { return $null }      # ca goi chi co Forced/SDH -> bo qua ung vien nay
    }

    if ($ep) {
        $hit = @($files | Where-Object {
            $e = Get-EpisodeInfo $_.BaseName
            $e -and $e.Season -eq $ep.Season -and $e.Episode -eq $ep.Episode
        })
        if ($hit.Count -eq 0) { return $null }
        $files = $hit
    }
    if ($files.Count -eq 1) { return $files[0] }

    $best = $null; $bs = -1
    foreach ($f in $files) {
        $s = Get-Score $videoTokens (Get-Tokens $f.BaseName)
        if ($s -gt $bs) { $bs = $s; $best = $f }
    }
    return $best
}

# ==========================================================
#  GOM UNG VIEN TU MOI NGUON  -  chi xet FPS + TEN PHIM
# ==========================================================
# Ten phim hay chua [ ] (vd "[YTS.MX]") ma PowerShell coi la ky tu dai dien.
# Cac tham so -OutFile / -Destination deu bi anh huong, nen phai dung .NET truc tiep.
function Save-ToDest([string]$srcPath, [string]$dest) {
    try {
        $dir = [System.IO.Path]::GetDirectoryName($dest)
        if ($dir -and -not [System.IO.Directory]::Exists($dir)) {
            [void][System.IO.Directory]::CreateDirectory($dir)
        }
        [System.IO.File]::Copy($srcPath, $dest, $true)
        return $true
    } catch {
        W "      [!] Khong ghi duoc file dich: $($_.Exception.Message)" 'Red'
        return $false
    }
}

# SubSource tra ve truong 'preview' chua vai dong dau kem MOC THOI GIAN THAT.
# Nho do uoc tinh duoc do lech TRUOC KHI TAI, khong ton quota, khong ton thoi gian.
function Get-PreviewTimes([string]$txt) {
    if (-not $txt) { return @() }
    $out = @()
    foreach ($m in [regex]::Matches($txt, '(\d{1,2}):(\d{2}):(\d{2})[,.](\d{2,3})\s*-->')) {
        $out += ([int]$m.Groups[1].Value) * 3600 + ([int]$m.Groups[2].Value) * 60 +
                [int]$m.Groups[3].Value + (ConvertTo-Double ("0." + $m.Groups[4].Value))
    }
    return @($out | Sort-Object)
}

# Chi ket luan khi TAT CA moc trong preview cung dong y mot do lech.
# Voi o 0.25s va pham vi +-180s: ban dung nhan ra 60/60, ban sai lot 0/60.
function Get-QuickOffset($refTimes, $pvTimes) {
    if (-not $refTimes -or $pvTimes.Count -lt 3) { return $null }
    $votes = New-Object 'System.Collections.Generic.Dictionary[int,int]'
    foreach ($t in $pvTimes) {
        foreach ($r in $refTimes) {
            $d = $r - $t
            if ($d -lt -180 -or $d -gt 180) { continue }
            $k = [int][math]::Round($d / 0.25)
            if ($votes.ContainsKey($k)) { $votes[$k]++ } else { $votes[$k] = 1 }
        }
    }
    if ($votes.Count -eq 0) { return $null }
    $bk = 0; $bv = -1
    foreach ($k in $votes.Keys) { if ($votes[$k] -gt $bv) { $bv = $votes[$k]; $bk = $k } }
    if ($bv -lt $pvTimes.Count) { return $null }      # phai du TAT CA moc dong y
    return [pscustomobject]@{ Offset = $bk * 0.25; Votes = $bv; Total = $pvTimes.Count }
}

function New-Cand($src, $tier, $fps, $score, $release, $info, $payload, $off) {
    return [pscustomobject]@{
        Src = $src; Tier = $tier; Fps = $fps; Score = $score
        Release = $release; Info = $info; Payload = $payload; Off = $off
    }
}

# Chi gom tu KHO NOI BO - mien phi, khong cham mang, do duoc do lech that
function Get-PoolCandidates([string]$lang, $v, $vTok, $refTimes, [string]$poolRoot) {
    $out = @()
    if (-not $UsePool) { return $out }
    $idx = Initialize-Pool $poolRoot
    if (-not $idx.ContainsKey($lang)) {
        W "    [kho] $lang : khong co file nao trong kho." 'DarkGray'
        return $out
    }
    $nAll = $idx[$lang].Count

    # Loc theo SO TAP TRUOC khi cat bot danh sach.
    # (Cac tap cung series co ten gan nhu y het nhau nen diem giong ten khong phan biet duoc,
    #  cat truoc thi dung tap can tim rat de bi loai oan.)
    $pool = @($idx[$lang] | Where-Object { -not (Test-EpisodeConflict $v.BaseName $_.BaseName) })
    if ($pool.Count -eq 0) {
        W "    [kho] $lang : $nAll file, nhung deu thuoc tap khac." 'DarkGray'
        return $out
    }

    # Neu co file ghi DUNG so tap thi chi xet nhung file do
    $myEp = Get-EpisodeInfo $v.BaseName
    if ($myEp) {
        $exact = @($pool | Where-Object {
            $e = Get-EpisodeInfo $_.BaseName
            $e -and $e.Season -eq $myEp.Season -and $e.Episode -eq $myEp.Episode
        })
        if ($exact.Count -gt 0) { $pool = $exact }
    }

    $ranked = @()
    foreach ($f in $pool) {
        $ranked += [pscustomobject]@{ File = $f; S = (Get-Score $vTok (Get-Tokens $f.BaseName)) }
    }
    $ranked = @($ranked | Sort-Object S -Descending | Select-Object -First $PoolTryLimit)
    W "    [kho] $lang : $nAll file, $($pool.Count) file hop tap, do thu $($ranked.Count) file" 'DarkGray'

    $dropLow = 0
    foreach ($r in $ranked) {
        $tier = 2; $fpsTxt = '?'; $poolOff = $null; $note = 'file noi bo'
        if ($refTimes) {
            $t = Get-CueTimes $r.File.FullName
            if (-not $t -or $t.Count -lt 30) {
                $note = 'file qua ngan, khong do duoc'
            } else {
                $rl = Get-TimingRelation $refTimes $t
                if ($rl) {
                    # Duoi muc nay gan nhu chac chan la phim khac -> bo han
                    if ($rl.Votes -lt $PoolMinVotes) {
                        W "       bo: $($r.File.Name) (chi $($rl.Votes) moc khop)" 'DarkGray'
                        $dropLow++
                        continue
                    }
                    $poolOff = [pscustomobject]@{ Offset = $rl.Offset; Votes = $rl.Votes; Total = -1 }
                    if ($rl.Votes -lt $MinVerifyVotes) {
                        # Do duoc nhung chua chac chan -> van cho ban thay de tu quyet
                        $tier = 2; $fpsTxt = 'do: nghi ngo'
                        $note = "chi $($rl.Votes) moc khop - can kiem tra"
                    } elseif ($rl.FpsMismatch) {
                        $tier = 3; $fpsTxt = 'lech'
                        $note = "$($rl.Votes) moc khop, LECH FPS"
                    } else {
                        $tier = 1; $fpsTxt = 'do: khop'
                        $note = "$($rl.Votes) moc khop"
                    }
                }
            }
        }
        $out += New-Cand 'Kho' $tier $fpsTxt $r.S $r.File.Name $note $r.File $poolOff
    }
    if ($out.Count -eq 0 -and $dropLow -gt 0) {
        W "    [kho] $lang : khong file nao khop voi phim nay." 'DarkGray'
    }
    return $out
}

# Gom tu cac nguon TRUC TUYEN (cham mang, OpenSubtitles con ton quota)
function Get-OnlineCandidates($entry, [string]$lang, [string]$langName, $v, $vTok, $fps, $refTimes) {
    $out = @()

    # ---- SubSource ----
    if ($entry -and $langName) {
        $list = Get-SubList ([int]$entry.movieId) $langName $lang
        foreach ($s in $list) {
            if ($ExcludeSdh    -and $s.hearingImpaired -eq $true) { continue }
            if ($ExcludeForced -and $s.foreignParts    -eq $true) { continue }
            $rel = (@($s.releaseInfo) | Select-Object -First 1)
            $sc  = Get-Score $vTok (Get-Tokens ([string]$rel))
            $qo = $null
            $isPack = ($s.files -and [int]$s.files -gt 1)

            # Goi nay da tai ve tu tap truoc -> do THANG file cua dung tap nay.
            # Khong ton them lan tai nao, va cho so lieu chinh xac thay vi uoc tinh.
            if ($refTimes -and $script:ZipCache.ContainsKey([int]$s.subtitleId) -and
                $script:ZipCache[[int]$s.subtitleId]) {
                $epFile = Select-FromPack $script:ZipCache[[int]$s.subtitleId] (Get-EpisodeInfo $v.BaseName) $vTok
                if ($epFile) {
                    $tt = Get-CueTimes $epFile.FullName
                    if ($tt -and $tt.Count -ge 30) {
                        $rr = Get-TimingRelation $refTimes $tt
                        if ($rr -and $rr.Votes -ge $MinVerifyVotes) {
                            $qo = [pscustomobject]@{ Offset = $rr.Offset; Votes = $rr.Votes; Total = -1 }
                        }
                    }
                }
            }
            # Chua tai -> uoc tinh tu preview (chi dung cho tap ma preview trich ra)
            if (-not $qo -and $refTimes -and $s.preview) {
                $qo = Get-QuickOffset $refTimes (Get-PreviewTimes ([string]$s.preview))
            }
            $note = "$($s.files) file, $($s.downloads) tai"
            if ($isPack) { $note += ' (goi season)' }
            $out += New-Cand 'SubSource' (Get-FpsTier $s.framerate $fps) ([string]$s.framerate) $sc `
                             ([string]$rel) $note $s $qo
        }
    }

    # ---- OpenSubtitles ----
    if ($UseOpenSubtitles -and -not $script:OsDisabled) {
        $title = Get-CleanTitle $v.BaseName
        $year  = Get-Year $v.BaseName
        $ep2   = Get-EpisodeInfo $v.BaseName
        $osAll = Get-OsCandidates $script:CurImdb $ep2 $lang $title $year
        foreach ($o in $osAll) {
            if (-not (Test-OsUsable $o)) { continue }
            $a  = $o.attributes
            $sc = Get-Score $vTok (Get-Tokens ([string]$a.release))
            $mt = if ($a.machine_translated -eq $true -or $a.ai_translated -eq $true) { 'dich may, ' } else { '' }
            $out += New-Cand 'OpenSub' (Get-FpsTier $a.fps $fps) ([string]$a.fps) $sc `
                             ([string]$a.release) ("$mt$($a.download_count) tai") $o $null
        }
    }
    return $out
}

# ==========================================================
#  NHAT KY THAO TAC + HOAN TAC
# ==========================================================
$script:ActionLog = New-Object 'System.Collections.Generic.List[object]'

function Add-Action([string]$act, [string]$from, [string]$to) {
    $script:ActionLog.Add([pscustomobject]@{
        Time = (Get-Date -Format 'HH:mm:ss'); Action = $act; From = $from; To = $to
    })
}

function Save-ActionLog([string]$root) {
    if ($script:ActionLog.Count -eq 0) { return $null }
    $dir = Join-Path $root '_subfetch_logs'
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $path = Join-Path $dir ("actions_" + (Get-Date -Format 'yyyyMMdd_HHmmss') + ".csv")
    $script:ActionLog | Export-Csv -LiteralPath $path -NoTypeInformation -Encoding UTF8
    # Chi giu 20 nhat ky gan nhat
    $olds = @(Get-ChildItem -LiteralPath $dir -File -Filter 'actions_*.csv' | Sort-Object Name -Descending)
    if ($olds.Count -gt 20) {
        $olds[20..($olds.Count-1)] | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue }
    }
    return $path
}

# Hoan tac mot lan chay: xoa file da tao, tra lai file da di chuyen
function Invoke-UndoRun([string]$root) {
    $dir = Join-Path $root '_subfetch_logs'
    if (-not (Test-Path -LiteralPath $dir)) { W "[!] Chua co nhat ky nao." 'Yellow'; return }
    $runs = @(Get-ChildItem -LiteralPath $dir -File -Filter 'actions_*.csv' | Sort-Object Name -Descending)
    if ($runs.Count -eq 0) { W "[!] Chua co nhat ky nao." 'Yellow'; return }

    W "`nCac lan chay gan day:" 'Cyan'
    for ($i = 0; $i -lt [math]::Min($runs.Count, 10); $i++) {
        $rows = @(Import-Csv -LiteralPath $runs[$i].FullName)
        $nNew = @($rows | Where-Object { $_.Action -eq 'Create' -or $_.Action -eq 'Copy' }).Count
        $nMov = @($rows | Where-Object { $_.Action -eq 'Move' }).Count
        $mark = if ($i -eq 0) { '  <- moi nhat' } else { '' }
        W ("   [{0}] {1}  ({2} file tao/chep, {3} file di chuyen){4}" -f ($i+1), $runs[$i].BaseName, $nNew, $nMov, $mark) 'White'
    }
    $pick = Read-Host "`nHoan tac lan nao? (Enter = moi nhat, 0 = huy)"
    if ($pick -eq '0') { W "Da huy." 'Yellow'; return }
    $sel = if ($pick -match '^[1-9][0-9]*$' -and [int]$pick -le $runs.Count) { $runs[[int]$pick - 1] } else { $runs[0] }

    $rows = @(Import-Csv -LiteralPath $sel.FullName)
    $news = @($rows | Where-Object { $_.Action -eq 'Create' -or $_.Action -eq 'Copy' })
    $movs = @($rows | Where-Object { $_.Action -eq 'Move' })

    W "`nSe hoan tac: $($sel.BaseName)" 'Cyan'
    W "   - XOA $($news.Count) file da tao ra" 'Gray'
    W "   - Tra lai cho cu cho $($movs.Count) file da di chuyen" 'Gray'
    if ((Read-Host "`nXac nhan? (Y/N)") -notmatch '^(?i)y') { W "Da huy." 'Yellow'; return }

    $del = 0; $back = 0; $miss = 0
    foreach ($r in $news) {
        if ([System.IO.File]::Exists($r.To)) {
            try { [System.IO.File]::Delete($r.To); $del++ } catch { $miss++ }
        } else { $miss++ }
    }
    for ($i = $movs.Count - 1; $i -ge 0; $i--) {
        $r = $movs[$i]
        if (-not [System.IO.File]::Exists($r.To)) { $miss++; continue }
        try {
            if ([System.IO.File]::Exists($r.From)) { [System.IO.File]::Delete($r.From) }
            [System.IO.File]::Move($r.To, $r.From); $back++
        } catch { $miss++ }
    }
    W "`nHoan tac xong: $del file bi xoa, $back file tra ve cho cu, $miss bo qua." 'Green'
    Remove-Item -LiteralPath $sel.FullName -Force -ErrorAction SilentlyContinue
}

# ==========================================================
#  PHU DE DA CO SAN TRONG srt_can_sync
# ==========================================================
# Ban tu tai tay va nem vao srt_can_sync\vi\ (hoac thang srt_can_sync\).
# Script se DOI TEN cho khop file MKV roi BO QUA viec tai ngon ngu do.
# Hai ten deu ghi so tap ma KHAC nhau -> chac chan khong phai mot tap.
# Biet duoc ngay tu ten file, khong can do thoi gian.
function Test-EpisodeConflict([string]$nameA, [string]$nameB) {
    $a = Get-EpisodeInfo $nameA
    $b = Get-EpisodeInfo $nameB
    if (-not $a -or -not $b) { return $false }
    return (($a.Season -ne $b.Season) -or ($a.Episode -ne $b.Episode))
}

function Use-ExistingSub([string]$langDir, $v, $vTok, $refTimes, [string]$dest, [string]$lang) {
    if (-not $UseExistingSubs) { return $false }

    # Da dung ten roi
    if (Test-Path -LiteralPath $dest) {
        W "    [=] $lang : da co san, bo qua tai." 'DarkGray'
        return $true
    }

    # Gom file ung vien: trong thu muc ngon ngu, va o goc srt_can_sync neu ten co hau to dung
    $cands = @()
    if (Test-Path -LiteralPath $langDir) {
        $cands += @(Get-ChildItem -LiteralPath $langDir -File -ErrorAction SilentlyContinue |
                    Where-Object { @('.srt','.ass','.ssa') -contains $_.Extension.ToLower() })
    }
    $rootDir = Join-Path $targetDir $SubFolderName
    if ((Test-Path -LiteralPath $rootDir) -and ($rootDir -ne $langDir)) {
        $cands += @(Get-ChildItem -LiteralPath $rootDir -File -ErrorAction SilentlyContinue |
                    Where-Object {
                        (@('.srt','.ass','.ssa') -contains $_.Extension.ToLower()) -and
                        ($_.BaseName -match "(?i)[\._\-]$lang$")
                    })
    }
    if ($cands.Count -eq 0) { return $false }

    # Loai ngay nhung file ghi ro la TAP KHAC
    $before = $cands.Count
    $cands = @($cands | Where-Object { -not (Test-EpisodeConflict $v.BaseName $_.BaseName) })
    $dropped = $before - $cands.Count
    if ($dropped -gt 0) { W "    [i] $lang : bo $dropped file thuoc tap khac." 'DarkGray' }
    if ($cands.Count -eq 0) { return $false }

    # Cham diem: uu tien do THOI GIAN neu co moc goc, khong thi so ten
    $best = $null
    foreach ($f in $cands) {
        $ns = Get-Score $vTok (Get-Tokens $f.BaseName)
        $votes = -1; $fpsBad = $false
        if ($refTimes) {
            $t = Get-CueTimes $f.FullName
            if ($t -and $t.Count -ge 30) {
                $rl = Get-TimingRelation $refTimes $t
                if ($rl) { $votes = $rl.Votes; $fpsBad = $rl.FpsMismatch }
            }
        }
        $rank = if ($votes -ge 0) { $votes * 100 + $ns } else { $ns }
        if ($null -eq $best -or $rank -gt $best.Rank) {
            $best = [pscustomobject]@{ File = $f; Name = $ns; Votes = $votes; FpsBad = $fpsBad; Rank = $rank }
        }
    }
    if (-not $best) { return $false }

    # DO DUOC thoi gian thi tin phep do, KHONG duoc lay ten cuu van.
    # (File tap khac co the trung ten toi 0.44 diem nhung moc thoi gian bac bo han.)
    $measured = ($best.Votes -ge 0)
    $okByTime = $measured -and ($best.Votes -ge $MinVerifyVotes)
    $okByName = (-not $measured) -and ($best.Name -ge $ExistingMinScore)

    if (-not $okByTime -and -not $okByName) {
        W "    [i] $lang : co $($cands.Count) file san nhung khong file nao khop phim nay." 'DarkGray'
        if ($measured) {
            W "        gan nhat: $($best.File.Name) - chi $($best.Votes) moc khop (can >= $MinVerifyVotes)" 'DarkGray'
        } else {
            W "        gan nhat: $($best.File.Name) - ten giong $([math]::Round($best.Name,2)) (can >= $ExistingMinScore)" 'DarkGray'
        }
        return $false
    }

    # Dua ve dung ten file MKV
    if ($best.File.FullName -eq $dest) { return $true }
    try {
        $dir = [System.IO.Path]::GetDirectoryName($dest)
        if ($dir -and -not [System.IO.Directory]::Exists($dir)) { [void][System.IO.Directory]::CreateDirectory($dir) }
        if ($ExistingRenameMode -eq 'move') {
            if ([System.IO.File]::Exists($dest)) { [System.IO.File]::Delete($dest) }
            [System.IO.File]::Move($best.File.FullName, $dest)
            Add-Action 'Move' $best.File.FullName $dest
        } else {
            [System.IO.File]::Copy($best.File.FullName, $dest, $true)
            Add-Action 'Copy' $best.File.FullName $dest
        }
    } catch {
        W "    [!] Khong dua ve duoc: $($_.Exception.Message)" 'Red'
        return $false
    }

    $ly = if ($okByTime) { "$($best.Votes) moc khop" } else { "ten giong $([math]::Round($best.Name,2))" }
    W "    [=] $lang : DUNG FILE CO SAN ($ly)" 'Green'
    W "        $($best.File.Name)" 'DarkGray'
    W "        -> doi ten thanh $(Split-Path $dest -Leaf)" 'Green'
    if ($best.FpsBad) { W "        [!] Do duoc LECH FPS - buoc dong bo se can xu ly." 'Yellow' }

    $script:UsedExisting++
    return $true
}

# ==========================================================
#  BANG CHON
# ==========================================================
function Get-TableWidth {
    if ($TableWidth -gt 0) { return $TableWidth }
    $cw = 78
    try { $cw = [Console]::WindowWidth - 6 } catch {}
    if ($cw -lt $TableMinWidth) { $cw = $TableMinWidth }
    if ($cw -gt 200) { $cw = 200 }
    return $cw
}

# Cat chuoi cho vua o, giu phan DUOI vi ten nhom release nam o cuoi
function Limit-Text([string]$t, [int]$n) {
    if (-not $t) { return '' }
    if ($t.Length -le $n) { return $t }
    if ($n -lt 12) { return $t.Substring(0, $n) }
    $head = [int](($n - 3) * 0.55)
    $tail = $n - 3 - $head
    return $t.Substring(0, $head) + '...' + $t.Substring($t.Length - $tail)
}

function Show-CandidateTable($cands, $v, [string]$lang, $videoFps) {
    $w    = Get-TableWidth
    $relW = $w - 52          # chua them cot Lech
    if ($relW -lt 20) { $relW = 20 }

    $V   = [string]$script:Bx.V
    $top = "  " + (Get-BoxLine $w 'top')
    $mid = "  " + (Get-BoxLine $w 'mid')
    $bot = "  " + (Get-BoxLine $w 'bot')
    W $top 'DarkCyan'
    W ("  $V{0,-$w}$V" -f (" $lang  <-  " + (Limit-Text $v.Name ($w - 12)))) 'Cyan'
    W ("  $V{0,-$w}$V" -f " video $videoFps fps") 'DarkGray'
    W $mid 'DarkCyan'
    W ("  $V{0,4} {1,-9} {2,-14} {3,6} {4,-12}  {5,-$relW} $V" -f '#', 'Nguon', 'FPS', 'Ten', 'Lech', 'Ban release') 'DarkGray'

    $i = 0
    foreach ($c in $cands) {
        $i++
        $col = switch ($c.Tier) { 1 { 'Green' } 2 { 'Gray' } default { 'DarkYellow' } }
        $fpsTxt = switch ($c.Tier) {
            1 { if ($c.Fps -eq 'do: khop') { 'do duoc: KHOP' } else { "$($c.Fps) KHOP" } }
            2 { 'khong khai bao' }
            default { "$($c.Fps) LECH" }
        }
        $rel = Limit-Text ([string]$c.Release) $relW

        # Cot do lech: do truoc khi tai (kho noi bo do day du, SubSource uoc tinh tu preview)
        $offTxt = '?'
        if ($c.Off) {
            $sg = if ($c.Off.Offset -ge 0) { '+' } else { '' }
            $offTxt = "$sg$([math]::Round($c.Off.Offset,2))s"
            if ($c.Off.Total -gt 0) { $offTxt += '~' }     # dau ~ = uoc tinh tu preview
        }
        W ("  $V{0,4} {1,-9} {2,-14} {3,6:N2} {4,-12}  {5,-$relW} $V" -f `
            $i, $c.Src, $fpsTxt, $c.Score, $offTxt, $rel) $col
        if ($c.Info) { W ("  $V{0,-$w}$V" -f ("      " + (Limit-Text $c.Info ($w - 7)))) 'DarkGray' }
    }
    W $bot 'DarkCyan'
    W "   Cot 'Lech': do lech so voi phu de goc trong MKV. Dau ~ = uoc tinh tu preview." 'DarkGray'
    W "   '?' = chua do duoc. Voi goi season, preview chi thuoc mot tap nen cac tap khac" 'DarkGray'
    W "         se hien '?' cho den khi goi duoc tai ve - sau do do duoc chinh xac." 'DarkGray'
}

function Select-Candidate($cands, [string]$key, [string]$videoName) {
    if ($cands.Count -eq 0) { return $null }

    # Da chon cho season + ngon ngu nay roi -> dung lai
    if ($RememberChoice -and $script:ChoiceCache.ContainsKey($key)) {
        $prev = $script:ChoiceCache[$key]
        # Ban da chon la ban CHI CO MOT TAP KHAC -> khong the dung cho tap nay.
        # (Chi goi ca season moi dung lai duoc cho cac tap sau.)
        if ($videoName -and (Test-EpisodeConflict $videoName ([string]$prev.Release))) {
            W "  [i] Lua chon truoc la ban rieng cua tap khac -> chon lai cho tap nay." 'DarkGray'
            $script:ChoiceCache.Remove($key)
        } else {
            $again = @($cands | Where-Object { $_.Src -eq $prev.Src -and $_.Release -eq $prev.Release })
            if ($again.Count -gt 0) {
                W "  [i] Dung lai lua chon truoc: $($prev.Src) - $($prev.Release)" 'DarkCyan'
                return $again[0]
            }
        }
    }

    if (-not $Interactive) { return $cands[0] }

    # Khong co gi de can nhac -> khoi hoi
    if ($AutoPickWhenSingle -and $cands.Count -eq 1) {
        W "  [i] Chi co 1 ung vien -> lay luon." 'DarkCyan'
        if ($RememberChoice -and -not ($videoName -and (Test-EpisodeConflict $videoName ([string]$cands[0].Release)))) {
            $script:ChoiceCache[$key] = $cands[0]
        }
        return $cands[0]
    }

    # ---- Cua so bang that: sap xep va loc duoc, bam chuot de chon ----
    if ($UseGridView -and (Get-Command Out-GridView -ErrorAction SilentlyContinue)) {
        $lg = ($key -split '\|')[-1]
        $rows = @(); $n = 0
        foreach ($c in $cands) {
            $n++
            $fpsTxt = switch ($c.Tier) {
                1 { if ($c.Fps -eq 'do: khop') { 'do duoc: KHOP' } else { "$($c.Fps) KHOP" } }
                2 { 'khong khai bao' }
                default { "$($c.Fps) LECH" }
            }
            $offTxt = '?'
            if ($c.Off) {
                $sg = if ($c.Off.Offset -ge 0) { '+' } else { '' }
                $offTxt = "$sg$([math]::Round($c.Off.Offset,2))s"
                if ($c.Off.Total -gt 0) { $offTxt += ' (uoc tinh)' }
            }
            $rows += [pscustomobject]@{
                STT = $n
                Nguon = $c.Src
                FPS = $fpsTxt
                Lech = $offTxt
                'Giong ten' = [math]::Round($c.Score, 2)
                'Ban release' = [string]$c.Release
                'Thong tin' = [string]$c.Info
            }
        }
        $sel = $rows | Out-GridView -Title "[$lg] Chon phu de cho: $videoName   (Cancel = bo qua ngon ngu nay)" -OutputMode Single
        if (-not $sel) { return $null }
        $pick = $cands[[int]$sel.STT - 1]
        if ($RememberChoice -and -not ($videoName -and (Test-EpisodeConflict $videoName ([string]$pick.Release)))) {
            $script:ChoiceCache[$key] = $pick
        }
        return $pick
    }

    $ans = Read-Host "  Chon so (Enter = so 1, s = bo qua ngon ngu nay)"
    if ($ans -match '^(?i)s$') { return $null }
    $pick = $cands[0]
    if ($ans -match '^\d+$' -and [int]$ans -ge 1 -and [int]$ans -le $cands.Count) {
        $pick = $cands[[int]$ans - 1]
    }
    # Chi ghi nho khi ban nay KHONG phai ban rieng cua mot tap
    if ($RememberChoice -and -not ($videoName -and (Test-EpisodeConflict $videoName ([string]$pick.Release)))) {
        $script:ChoiceCache[$key] = $pick
    }
    return $pick
}

# ==========================================================
#  LAY VE UNG VIEN DA CHON
# ==========================================================
function Get-ChosenSubtitle($c, [string]$dest, $ep, $vTok, $refTimes) {
    switch ($c.Src) {
        'SubSource' {
            $dir = Get-SubtitlePack ([int]$c.Payload.subtitleId)
            if (-not $dir) { return $false }
            $pick = Select-FromPack $dir $ep $vTok
            if (-not $pick) { W "      (goi nay khong co tap can tim)" 'DarkYellow'; return $false }
            return (Save-ToDest $pick.FullName $dest)
        }
        'Kho' {
            return (Save-ToDest $c.Payload.FullName $dest)
        }
        'OpenSub' {
            return (Save-OsSubtitle $c.Payload $dest)
        }
    }
    return $false
}

# Chi BAO CAO, khong sua gi. Viec nan thoi gian de SubPrep lo.
function Report-Timing([string]$file, $refTimes) {
    if (-not $VerifyTiming -or -not $refTimes) { return }
    $t = Get-CueTimes $file
    if (-not $t -or $t.Count -lt 30) { return }
    $rel = Get-TimingRelation $refTimes $t
    if (-not $rel) { return }

    $sg = if ($rel.Offset -ge 0) { '+' } else { '' }
    if ($rel.Votes -lt $MinVerifyVotes) {
        W "      [!] Kiem chung yeu: chi $($rel.Votes) moc khop - kiem tra ky khi xem." 'Yellow'
        return
    }
    if ($rel.FpsMismatch) {
        W "      [!] Do duoc LECH FPS ($($rel.Name)) - SubPrep se can sua khi dong bo." 'Yellow'
    } else {
        W "      [v] Khop thoi gian, lech $sg$([math]::Round($rel.Offset,2))s (SubPrep se dong bo)" 'DarkGreen'
    }
}

$script:RefPathCache = @{}

# ---------- DOC / GHI KHOI SRT ----------

# Trich audio track da chon ra WAV de alass dung lam moc

# ==========================================================
#  BAT DAU
# ==========================================================
[void](Initialize-Console)
Initialize-BoxChars
Set-ConsoleSize $ConsoleCols $ConsoleRows $ConsoleScrollback

W "===================================================" 'Cyan'
W "   SUBFETCH - TAI PHU DE TU SUBSOURCE" 'Cyan'
W "===================================================" 'Cyan'

if (-not $SubSourceApiKey) {
    W "`n[!] Chua dien `$SubSourceApiKey o phan CAU HINH." 'Red'
    W "    Lay tai: subsource.net > dang nhap > My Profile > API Key" 'Yellow'
    return
}
# Hoan tac: dat $UndoLastRun = $true o phan CAU HINH roi chay lai script
if ($UndoLastRun) { Invoke-UndoRun $targetDir; return }

if ($Languages.Count -eq 0) {
    W "`n[!] Chua bat ngon ngu nao." 'Red'
    W "    Mo file .bat, tim phan 'NGON NGU CAN TAI' va dat it nhat mot dong thanh `$true." 'Yellow'
    return
}
$unknownLang = @($Languages | Where-Object { -not $LangMap.ContainsKey($_) })
if ($unknownLang.Count -gt 0) {
    W "[!] Chua biet ten SubSource cho: $($unknownLang -join ', ') - se chi tim tren OpenSubtitles." 'Yellow'
}
if (-not $mkvmerge) { W "[!] Khong tim thay mkvmerge.exe -> khong doc duoc framerate video." 'Yellow' }
if ($Interactive) { W "[i] Che do CHON TAY: se hien bang de ban tu chon." 'DarkCyan' }
else { W "[i] Che do TU DONG: lay ban dau bang." 'DarkGray' }
if ($StrictFpsMatch) {
    $uu = if ($AllowUnknownFps) { 'cho phep ban khong khai bao fps' } else { 'chi lay ban khai bao dung fps' }
    W "[i] Rang buoc framerate: BAT ($uu)." 'DarkCyan'
}
if ($VerifyTiming) {
    if ($mkvextract) { W "[i] Bat kiem chung thoi gian: doi chieu voi phu de co san trong MKV." 'DarkCyan' }
    else { W "[!] Thieu mkvextract.exe -> KHONG kiem chung duoc, chi cham diem theo mo ta." 'Yellow' }
}
if (-not $TmdbApiKey) { W "[i] Chua co TMDB key -> tim theo ten thay vi IMDb ID." 'DarkGray' }

if ($UseOpenSubtitles) { [void](Connect-OpenSubtitles) }

$poolRoot = Join-Path $targetDir $PoolFolder
if ($UsePool) {
    if (Test-Path -LiteralPath $poolRoot) { [void](Initialize-Pool $poolRoot) }
    else {
        New-Item -ItemType Directory -Path $poolRoot -Force | Out-Null
        W "[i] Da tao thu muc kho '$PoolFolder' - nem phu de tai thu cong vao day." 'DarkGray'
    }
}

$subDir = Join-Path $targetDir $SubFolderName
if (-not (Test-Path -LiteralPath $subDir)) { New-Item -ItemType Directory -Path $subDir -Force | Out-Null }

# Moi ngon ngu mot thu muc con
$LangDir = @{}
foreach ($lg in $Languages) {
    $d = if ($UseLangSubFolders) { Join-Path $subDir $lg } else { $subDir }
    $LangDir[$lg] = $d
    if (-not (Test-Path -LiteralPath $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
}
if ($UseLangSubFolders) {
    W "Phu de se ha vao: $SubFolderName\{$($Languages -join ', ')}\" 'DarkGray'
}

$gci = @{ LiteralPath = $targetDir; File = $true }
if ($ScanSubFolders) { $gci['Recurse'] = $true }
$videos = @(Get-ChildItem @gci |
            Where-Object { $VideoExts -contains $_.Extension.ToLower() } |
            Where-Object { -not $_.FullName.StartsWith($subDir, 'OrdinalIgnoreCase') } |
            Sort-Object FullName)

if ($videos.Count -eq 0) { W "`n[!] Khong tim thay file video nao." 'Red'; return }
W "`nTim thay $($videos.Count) file video. Ngon ngu can tai: $($Languages -join ', ')`n" 'White'

$okCount = 0; $missCount = 0
$report = @()

foreach ($v in $videos) {
    W ("=" * 55) 'DarkCyan'
    W "=== $($v.Name)" 'Cyan'

    $ep    = Get-EpisodeInfo $v.BaseName
    $title = Get-CleanTitle $v.BaseName
    $year  = Get-Year $v.BaseName
    $fps   = Get-VideoFps $v.FullName
    $vTok  = Get-Tokens $v.BaseName

    $refTimes = $null
    if ($VerifyTiming) { $refTimes = Get-RefTimes $v }
    $what = if ($ep) { "phim bo S$($ep.Season)E$($ep.Episode)" } else { 'phim le' }
    W "    Nhan dien: '$title'$(if($year){" ($year)"}) - $what$(if($fps){" - $fps fps"})" 'DarkGray'

    $script:LangReported = $false
    $imdb  = Get-ImdbIdCached $title $year ($null -ne $ep)
    $script:CurImdb = $imdb
    $entry = Find-Entry $title $year $(if ($ep) { $ep.Season } else { $null })
    if ($entry) {
        W "    SubSource: $($entry.title)$(if($entry.season){" - season $($entry.season)"}) (movieId=$($entry.movieId), $($entry.subtitleCount) phu de)" 'DarkGray'
    } else {
        W "    [-] Khong tim thay tren SubSource." 'Yellow'
        if (-not $UseOpenSubtitles -or $script:OsDisabled) { $missCount++; continue }
    }

    foreach ($lang in $Languages) {
        $langName = $LangMap[$lang]        # $null thi bo qua SubSource, van thu OpenSubtitles duoc

        $dest = Join-Path $LangDir[$lang] "$($v.BaseName).$lang.srt"

        # Uu tien file ban da tu nem vao srt_can_sync
        if (Use-ExistingSub $LangDir[$lang] $v $vTok $refTimes $dest $lang) { continue }

        # ---- Gom ung vien tu MOI nguon roi moi quyet dinh ----
        # --- BUOC 1: kho noi bo truoc (mien phi, khong cham mang) ---
        $cands = @(Get-PoolCandidates $lang $v $vTok $refTimes $poolRoot)

        # Da co ban DO DUOC la khop -> khong can hoi mang nua
        $poolHit = @($cands | Where-Object { $_.Tier -eq 1 -and $_.Off })
        $skipOnline = ($PoolFirst -and $poolHit.Count -gt 0)

        if ($skipOnline) {
            W "    [i] $lang : kho noi bo da co ban khop -> bo qua tim tren mang." 'DarkCyan'
            $cands = $poolHit
        } else {
            if ($cands.Count -gt 0) {
                W "    [i] $lang : kho noi bo co $($cands.Count) ung vien, tim them tren mang..." 'DarkGray'
            }
            # --- BUOC 2: cac nguon truc tuyen ---
            $cands += @(Get-OnlineCandidates $entry $lang $langName $v $vTok $fps $refTimes)
        }

        if ($cands.Count -eq 0) {
            W "    [-] $lang : khong nguon nao co phu de." 'DarkYellow'
            if ($entry -and -not $script:LangReported) {
                $summary = Get-LangSummary (Get-AllSubs ([int]$entry.movieId))
                W "        (SubSource co: $($summary -join ', '))" 'DarkGray'
                $script:LangReported = $true
            }
            $missCount++; continue
        }

        # ---- Rang buoc framerate ----
        $before = $cands.Count
        if ($StrictFpsMatch)       { $cands = @($cands | Where-Object { $_.Tier -ne 3 }) }
        if (-not $AllowUnknownFps) { $cands = @($cands | Where-Object { $_.Tier -ne 2 }) }
        $cut = $before - $cands.Count
        if ($cut -gt 0) { W "    [i] $lang : loai $cut ban lech framerate." 'DarkGray' }

        if ($cands.Count -eq 0) {
            W "    [-] $lang : co $before ban nhung deu lech framerate." 'DarkYellow'
            $missCount++; continue
        }

        # Hang FPS truoc, trong cung hang moi so do giong ten
        # Thu tu: hang FPS -> da do duoc do lech -> do lech nho -> giong ten
        $cands = @($cands | Sort-Object `
                     @{Expression='Tier'}, `
                     @{Expression={ if ($_.Off) { 0 } else { 1 } }}, `
                     @{Expression={ if ($_.Off) { [math]::Abs($_.Off.Offset) } else { 999 } }}, `
                     @{Expression='Score';Descending=$true} |
                   Select-Object -First $MaxShowCandidates)

        Show-CandidateTable $cands $v $lang $fps

        $key    = "$(if($entry){$entry.movieId}else{$v.BaseName})|$lang"
        $chosen = Select-Candidate $cands $key $v.BaseName
        if (-not $chosen) { W "    [>] Bo qua $lang." 'DarkGray'; continue }

        # Ban da chon co the khong chua tap can tim -> thu tiep cac ban con lai
        $tryList = @($chosen) + @($cands | Where-Object {
            -not ($_.Src -eq $chosen.Src -and $_.Release -eq $chosen.Release) })

        $chosenOk = $null
        foreach ($cd in $tryList) {
            if ($cd -ne $chosen) {
                W "    [~] Thu ban khac: [$($cd.Src)] $($cd.Release)" 'Yellow'
            } else {
                W "    [~] Dang lay: [$($cd.Src)] $($cd.Release)" 'Cyan'
            }
            if (Get-ChosenSubtitle $cd $dest $ep $vTok $refTimes) { $chosenOk = $cd; break }
            # Ban ghi nho khong dung duoc -> xoa khoi bo nho de lan sau khong lap lai
            if ($RememberChoice -and $script:ChoiceCache.ContainsKey($key) -and
                $script:ChoiceCache[$key].Release -eq $cd.Release) {
                $script:ChoiceCache.Remove($key)
            }
        }
        if ($chosenOk) { $chosen = $chosenOk }

        if ($chosenOk) {
            $relDest = $dest
            if ($relDest.StartsWith($targetDir, 'OrdinalIgnoreCase')) {
                $relDest = $relDest.Substring($targetDir.Length).TrimStart('\')
            }
            W "      -> DA LUU: $relDest" 'Green'
            Add-Action 'Create' $chosen.Release $dest
            Report-Timing $dest $refTimes
            $report += [pscustomobject]@{
                Video = $v.Name; Lang = $lang; Nguon = $chosen.Src
                Release = $chosen.Release; Score = [math]::Round($chosen.Score, 2)
                Fps = $chosen.Fps
            }
            $okCount++
        } else {
            W "    [-] $lang : lay that bai." 'Yellow'
            $missCount++
        }
    }
    Write-Host ''
}

$__alog = Save-ActionLog $targetDir
if ($__alog) { W "`n  Nhat ky thao tac: _subfetch_logs\$(Split-Path $__alog -Leaf)" 'DarkCyan'
               W "  Chay lai script va chon 'y' o dau de hoan tac." 'DarkGray' }

# Don thu muc tam
if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue }

W ("=" * 55) 'DarkCyan'
W "TONG KET: $okCount phu de da tai | $($script:UsedExisting) dung file co san | $missCount khong tim duoc" 'White'
if ($UseOpenSubtitles -and $script:OsRemaining -ge 0) {
    $col = if ($script:OsRemaining -le $OsWarnRemaining) { 'Yellow' } else { 'Cyan' }
    W "OpenSubtitles: con $($script:OsRemaining)/$($script:OsAllowed) luot tai hom nay" $col
    if ($script:OsResetMsg) { W "  $($script:OsResetMsg)" 'DarkGray' }
}
if ($report.Count -gt 0) {
    $logDir = Join-Path $targetDir '_subfetch_logs'
    if (-not (Test-Path -LiteralPath $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
    $lp = Join-Path $logDir ("fetch_" + (Get-Date -Format 'yyyyMMdd_HHmmss') + ".csv")
    $report | Export-Csv -LiteralPath $lp -NoTypeInformation -Encoding UTF8
    W "Nhat ky: _subfetch_logs\$(Split-Path $lp -Leaf)" 'DarkCyan'
}
if ($UseLangSubFolders) {
    W "`nBuoc tiep theo: chay SubPrep, chon che do 2 (theo phu de goc)," 'White'
    W "roi chon ngon ngu muc 5 (TU NHAN DIEN) - no se doc ten thu muc con." 'White'
} else {
    W "`nBuoc tiep theo: chay SubPrep de dong bo thoi gian." 'White'
}
