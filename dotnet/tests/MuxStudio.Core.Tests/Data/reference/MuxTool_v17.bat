<# :
@echo off
chcp 65001 >nul
cls
set "WORK_DIR=%~dp0"
set "SCRIPT_PATH=%~f0"
cd /d "%WORK_DIR%"

echo ==============================================================
echo    MKV MUX TOOL v17 - Don dep Audio/Sub + Tu dong dat ten
echo ==============================================================
echo.

if not exist "Output" mkdir "Output"

powershell -NoProfile -ExecutionPolicy Bypass -Command "$sp=$env:SCRIPT_PATH; $src=Get-Content -LiteralPath $sp -Raw -Encoding UTF8; Invoke-Command -ScriptBlock ([scriptblock]::Create($src)) -ArgumentList $env:WORK_DIR"

echo.
echo ==============================================================
echo   KET THUC.
pause
exit /b
#>

param([string]$targetDir)

$ErrorActionPreference = 'Continue'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}
# PowerShell 5.1 mac dinh dung TLS 1.0 -> moi ket noi HTTPS toi TMDB deu bi tu choi
try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch {}

# ==========================================================
#  CAU HINH  (sua o day, khong can dong vao code ben duoi)
# ==========================================================
$UserMkvToolNix     = "C:\Users\fenha\Desktop\mkvtoolnix"  # duong dan uu tien cua ban
$SkipOnMultipleEnAudio = $true             # >=2 audio English -> bao cao va bo qua file
$RemoveCommentaryAudio = $true             # xoa audio binh luan (commentary/director/cast...)
$RemoveDescriptiveAudio= $true           # xoa audio thuyet minh cho nguoi khiem thi (audio description)
$RemoveCommentarySubs  = $true             # xoa phu de binh luan
# Phu de dang ANH (PGS/VobSub) khong hien duoc tren nhieu client iOS/trinh duyet.
# $true = xoa het, chi giu phu de VAN BAN (SRT/ASS). Phu de van ban co san van duoc giu nguyen.
$RemoveImageSubs       = $true
$AskWhichAudio         = $true             # con nhieu hon 1 audio -> hoi muon xoa track nao
$AskAudioMinTracks     = 2                 # tu bao nhieu track tro len thi hoi
# 'ask'    = hoi mot lan roi ap dung cho cac file sau (Enter = CO)
# 'always' = tu ghi nho, khong hoi
# 'never'  = moi file deu hoi lai tu dau
$RememberAudioChoice   = 'ask'
$UseColors             = $true            # mau 24-bit + khung Unicode
# $true = mo CUA SO BANG that de chon audio (Ctrl/Shift chon nhieu dong)
$UseGridView           = $true
# Bang chon phu de cho TUNG TAP: doc truoc moi tap, hien mot cua so bang de tick.
# O duoc tick san theo quy tac tu dong; bam Huy la chay dung nhu cu.
$UseSubPicker          = $true
$SubPickerMinFiles     = 1                 # tu bao nhieu file tro len thi hien bang
# Kich thuoc cua so. Bo dem giu nhieu dong de con CUON LEN xem lai.
$ConsoleCols           = 150
$ConsoleRows           = 45
$ConsoleScrollback     = 9000

# --- The codec trong TEN FILE (khong gan vao ten thu muc) ---
# Jellyfin dung phan sau " - " lam nhan phien ban, nen nhieu ban encode cua cung
# mot phim se gom thanh mot muc, chon phien ban bang menu.
$AddCodecTag           = $true
$CodecTagForTv         = $false           # phim bo: mac dinh TAT (Jellyfin xu ly kem on dinh hon)
$CodecTagParts         = @('res','hdr','vcodec','acodec')
$DryRun                = $false            # $true = chi in lenh mkvmerge ra, KHONG ghi file nao (lo hong 7)
$WriteRunLog           = $true             # xuat nhat ky CSV sau moi lan chay (lo hong 2)
$LogDirName            = '_muxtool_logs'
$TmdbRetry             = 3                 # so lan thu lai khi loi mang (lo hong 4)
$ExtraToolDirs         = @('C:\Users\fenha\Desktop\alass-windows64\bin\alass')  # noi tim them ffprobe (lo hong 6)
$SubKeepLangs       = @('vi','cs','en')    # sub giu lai
$SubDefaultPriority = @('vi','en','cs')    # thu tu uu tien dat Default cho sub
$RemoveForcedSubs   = $true                # $false = giu forced sub lai
$SkipOnAmbiguousUnd  = $true               # >=2 audio ma co track 'und' -> bo qua file
$DefaultExternalLang= 'vi'                 # sub ngoai khong co hau to ngon ngu -> coi la tieng gi
$ExternalSubOverridesInternal = $true      # co file sub ngoai ngon ngu X -> xoa het sub internal ngon ngu X
$SkipIfOutputExists = $false               # $true = bo qua file da mux roi
$UseTrackOrder      = $true                # sap xep lai thu tu track trong file dich
$SetTitleFromName   = $true                # doi title file thanh ten file (xoa title rac)
$SubExtPriority     = @('.srt','.ass','.ssa','.sup','.vtt')

# --- DAT TEN FILE THEO CO SO DU LIEU ---
# Moi lan chay, script LUON hoi chon TMDB / TVDB / giu ten goc (ca phim le lan phim bo).
# Khong co lua chon mac dinh: phai go 1, 2 hoac 0.
# TMDB -> "Ten phim (Nam) [tmdbid-xxx]"   |   TVDB -> "Ten phim (Nam) [tvdbid-xxx]"
$TmdbApiKey         = ''         # BAT BUOC: lay mien phi tai themoviedb.org > Settings > API
$TmdbLang           = 'en-US'    # 'vi-VN' neu muon lay ten tieng Viet
$TmdbConfirm        = $true      # hoi lai khi ket qua khong chac chan (ap dung cho ca TMDB va TVDB)
$TmdbCreateFolder   = $true     # $true = moi phim le mot thu muc rieng (chuan Jellyfin/Emby)
# --- Phim bo: tu nhan dien SxxExx / so tuyet doi va dat ten chuan Jellyfin ---
$TvFetchEpisodeTitle= $true      # lay them ten tap (tra them 1 request/tap)
$TvSeasonFolder     = $true      # tao cau truc Series\Season 01\ (Jellyfin rat nen bat)
# Anime danh so tuyet doi (vd One Piece S16E643): doi sang ma season/tap cua TMDB
# de Jellyfin nhan dung tap. $false = giu ma tap goc, chi lay ten tap.
$TvAbsoluteToSeason = $true

# Database ban chon khong tim thay mot phim -> co thu database con lai khong?
# $false (mac dinh) = giu ten goc va bao ra, de thu vien khong tron lan tmdbid/tvdbid.
$NameFallbackOther  = $false
# TVDB v4: lay key tai thetvdb.com > Dashboard > API Keys
$TvdbApiKey         = ''
$TvdbPin            = ''         # chi can neu key cua ban la loai "user-supported"
$TvdbLang           = 'eng'      # ma 3 chu: 'eng', 'vie', 'jpn'...
$TvdbSeasonType     = 'default'  # 'default' (theo phat song) | 'dvd' | 'official'

# --- GHEP AUDIO TU FILE PHIM KHAC (vd lay tieng Sec tu mot ban phim khac) ---
# Dat file nguon (.mkv/.mka/.mp4) vao thu muc $DonorFolder canh file .bat.
# Phim bo: ten file nguon chi can co dung so tap (S01E05 hoac "- 0643").
$UseDonorAudio      = $true
$DonorFolder        = 'audio_nguon'
$DonorLangs         = @('cs')          # ngon ngu audio can lay tu file nguon
$DonorSkipIfPresent = $true            # phim cua ban da co audio ngon ngu do -> khong lay them
$DonorSetDefault    = $false           # $true = dat audio ghep vao lam Default
$DonorMinVotes      = 20               # so moc phu de khop toi thieu de tin do lech
$DonorAcceptDurationMatch = $true      # khong co phu de chung nhung thoi luong khop -> van ghep, lech 0
$DonorMaxDurationDiff     = 1.0        # (giay) muc chenh thoi luong toi da coi la "khop"
$DonorOffsetMs      = @{}              # chinh tay (ms), ghi de ket qua do: @{ 'S01E03' = 1200; 'tap #643' = -500 }

# --- Quet thu muc con (de o thu muc cha chua nhieu Season) ---
$ScanSubFolders     = $true      # $true = quet ca thu muc con; $false = chi thu muc hien tai

# ==========================================================
#  HAM TIEN ICH
# ==========================================================
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
        # Luong VAO:
        #  - tat QUICK_EDIT (0x40): nhap chuot khong con lam TAM DUNG tien trinh
        #  - tat MOUSE_INPUT (0x10): tra lai con lan chuot cho console de CUON duoc
        #    (giu co nay thi ung dung nuot su kien chuot -> cua so khong cuon)
        $hIn = [Win32.ConHelper]::GetStdHandle(-10)
        $mi = 0
        if ([Win32.ConHelper]::GetConsoleMode($hIn, [ref]$mi)) {
            $newIn = ($mi -band -bnot (0x0040 -bor 0x0010)) -bor 0x0080
            [void][Win32.ConHelper]::SetConsoleMode($hIn, $newIn)
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

function Write-Step($msg, $color = 'Gray') { W $msg $color }

# ---------- HOI NGUOI DUNG CHON AUDIO GIU LAI ----------
# Tra ve danh sach track con lai sau khi loai bo theo y nguoi dung
function Select-AudioTracks($aInfo, $ffMap) {
    # ---------- Ap dung quy tac da ghi nho tu file truoc ----------
    if ($null -ne $script:AudioKeepLangs -or $null -ne $script:AudioKeepSig) {
        $keep = @(); $drop = @()
        foreach ($a in $aInfo) {
            $chan = Get-ChannelName $a.Track.properties.audio_channels
            $ok = $true
            # Quy tac 1: chi giu cac ngon ngu da chon (ngon ngu la cung bi loai)
            if ($null -ne $script:AudioKeepLangs -and ($script:AudioKeepLangs -notcontains $a.Code)) { $ok = $false }
            # Quy tac 2: trong mot ngon ngu, chi giu cac he thong kenh da chon
            if ($ok -and $null -ne $script:AudioKeepSig -and $script:AudioKeepSig.ContainsKey($a.Code)) {
                if ($script:AudioKeepSig[$a.Code] -notcontains $chan) { $ok = $false }
            }
            if ($ok) { $keep += $a } else { $drop += $a }
        }
        if ($keep.Count -eq 0) {
            Write-Step "  [!] Quy tac da nho se xoa het audio cua file nay -> bo qua quy tac, giu tat ca." 'Yellow'
            return $aInfo
        }
        Write-Step "  [i] Ap dung quy tac da nho: $(Get-AudioRuleText)" 'DarkCyan'
        foreach ($d in $drop) {
            $chan = Get-ChannelName $d.Track.properties.audio_channels
            Write-Step "  [-] Audio  #$($d.Id): XOA - theo quy tac ($(Get-LangName $d.Code) $chan)" 'DarkGray'
        }
        return $keep
    }

    if (-not $AskWhichAudio -or $aInfo.Count -lt $AskAudioMinTracks) { return $aInfo }

    # ---------- Hoi nguoi dung ----------
    Write-Step ""
    Write-Step "  File nay con $($aInfo.Count) audio track:" 'Cyan'
    foreach ($a in $aInfo) {
        $p    = $a.Track.properties
        $nm   = [string]$p.track_name
        $bps  = Get-AudioBitrate $a.Track $ffMap
        $desc = ((( Get-LangName $a.Code), (Get-CodecName $p), (Get-ChannelName $p.audio_channels)) -join ' ').Trim() -replace '\s+',' '
        $extra = if ($nm) { "  [$nm]" } else { '' }
        Write-Step ("     #{0}  {1,-34} {2,12}{3}" -f $a.Id, $desc, (Format-Kbps $bps), $extra) 'White'
    }
    $ids = @()

    # ---- Cua so bang that: giu Ctrl/Shift de chon nhieu dong ----
    if ($UseGridView -and (Get-Command Out-GridView -ErrorAction SilentlyContinue)) {
        $rows = @()
        foreach ($a in $aInfo) {
            $p = $a.Track.properties
            $rows += [pscustomobject]@{
                ID        = $a.Id
                'Ngon ngu'= (Get-LangName $a.Code)
                Codec     = (Get-CodecName $p)
                Kenh      = (Get-ChannelName $p.audio_channels)
                Bitrate   = (Format-Kbps (Get-AudioBitrate $a.Track $ffMap))
                'Ten track' = [string]$p.track_name
            }
        }
        $sel = $rows | Out-GridView -OutputMode Multiple `
                 -Title "CHON CAC AUDIO MUON XOA (Ctrl/Shift de chon nhieu dong) - Cancel = giu tat ca"
        if (-not $sel) { Write-Step "  [i] Khong chon gi -> giu tat ca." 'DarkGray'; return $aInfo }
        $ids = @($sel | ForEach-Object { [int]$_.ID })
    } else {
        Write-Step "  Nhap ID cac audio muon XOA, cach nhau bang dau phay (vd: 1,3)." 'Gray'
        $ans = Read-Host "  Enter = giu tat ca"
        if (-not $ans -or -not $ans.Trim()) { return $aInfo }
        foreach ($x in ($ans -split '[,;\s]+')) { if ($x -match '^\d+$') { $ids += [int]$x } }
    }

    if ($ids.Count -eq 0) { Write-Step "  [!] Khong doc duoc ID nao -> giu tat ca." 'Yellow'; return $aInfo }

    $keep = @($aInfo | Where-Object { $ids -notcontains $_.Id })
    $drop = @($aInfo | Where-Object { $ids -contains $_.Id })
    if ($keep.Count -eq 0) {
        Write-Step "  [!] Khong the xoa HET audio -> giu nguyen tat ca." 'Yellow'
        return $aInfo
    }
    foreach ($d in $drop) { Write-Step "  [-] Audio  #$($d.Id): XOA - theo lua chon cua ban" 'DarkGray' }

    # ---------- Dung quy tac de dung lai cho cac file sau ----------
    if ($RememberAudioChoice -eq 'never') { return $keep }

    $keepLangs = @($keep | ForEach-Object { $_.Code } | Select-Object -Unique)
    $dropLangs = @($drop | ForEach-Object { $_.Code } | Select-Object -Unique)
    # Ngon ngu vua giu vua xoa -> phai phan biet them bang so kenh
    $mixed = @($dropLangs | Where-Object { $keepLangs -contains $_ })

    $sig = $null
    if ($mixed.Count -gt 0) {
        $sig = @{}
        foreach ($m in $mixed) {
            $sig[$m] = @($keep | Where-Object { $_.Code -eq $m } |
                         ForEach-Object { Get-ChannelName $_.Track.properties.audio_channels } |
                         Select-Object -Unique)
        }
    }

    # Mo ta quy tac cho nguoi dung xem truoc khi dong y
    $parts = @()
    foreach ($lg in $keepLangs) {
        $n = Get-LangName $lg
        if ($sig -and $sig.ContainsKey($lg)) { $parts += "$n ($($sig[$lg] -join '/'))" } else { $parts += $n }
    }
    $ruleTxt = "chi giu " + ($parts -join ', ')

    $doIt = $true
    if ($RememberAudioChoice -eq 'ask') {
        $rep = Read-Host "  Ap dung cho MOI file con lai ($ruleTxt)? (Y/n)"
        $doIt = ($rep -notmatch '^(?i)n')        # Enter = CO
    }
    if ($doIt) {
        $script:AudioKeepLangs = $keepLangs
        $script:AudioKeepSig   = $sig
        Write-Step "  [i] Da nho: $ruleTxt" 'DarkCyan'
    }
    return $keep
}

# Mo ta quy tac dang ap dung, de in ra o moi file
function Get-AudioRuleText {
    if ($null -eq $script:AudioKeepLangs) { return '(khong co)' }
    $parts = @()
    foreach ($lg in $script:AudioKeepLangs) {
        $n = Get-LangName $lg
        if ($script:AudioKeepSig -and $script:AudioKeepSig.ContainsKey($lg)) {
            $parts += "$n ($($script:AudioKeepSig[$lg] -join '/'))"
        } else { $parts += $n }
    }
    return "chi giu " + ($parts -join ', ')
}

# ---------- NHAT KY (lo hong 2) ----------
$script:RunLog = @()
$script:AudioKeepLangs = $null   # ngon ngu audio duoc giu (ngon ngu khac se bi xoa)
$script:AudioKeepSig   = $null   # trong mot ngon ngu, he thong kenh nao duoc giu
function Add-MuxLog([string]$file, [string]$status, [string]$outName, [string]$detail) {
    $script:RunLog += [pscustomobject]@{
        Time = (Get-Date -Format 'HH:mm:ss'); File = $file
        Status = $status; Output = $outName; Detail = $detail
    }
}
function Save-MuxLog([string]$root) {
    if (-not $WriteRunLog -or $script:RunLog.Count -eq 0) { return $null }
    $dir = Join-Path $root $LogDirName
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $path = Join-Path $dir ("mux_" + (Get-Date -Format 'yyyyMMdd_HHmmss') + ".csv")
    $script:RunLog | Export-Csv -LiteralPath $path -NoTypeInformation -Encoding UTF8
    return $path
}

function Find-Exe([string]$name, [string[]]$hints) {
    foreach ($h in $hints) {
        if ($h -and (Test-Path -LiteralPath $h)) { return (Resolve-Path -LiteralPath $h).Path }
    }
    $c = Get-Command $name -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }
    return $null
}

# Noi 2 duong dan nhung khong no neu ve trai la null/rong
function Join-Safe($base, $child) {
    if ([string]::IsNullOrWhiteSpace([string]$base)) { return $null }
    return (Join-Path ([string]$base) $child)
}

# Chuan hoa moi kieu ma ngon ngu ve dang 2 ky tu
function Get-LangCode($lang, $ietf) {
    $s = ("$lang $ietf").ToLower()
    if ($s -match '\b(vie|vi|vn|vietnamese)\b')      { return 'vi' }
    if ($s -match '\b(cze|ces|cs|cz|czech)\b')       { return 'cs' }
    if ($s -match '\b(eng|en|english)\b')            { return 'en' }
    if ($s -match '\b(jpn|ja|jp|japanese)\b')        { return 'ja' }
    if ($s -match '\b(kor|ko|korean)\b')             { return 'ko' }
    if ($s -match '\b(chi|zho|zh|cmn|yue)\b')        { return 'zh' }
    if ($s -match '\b(fre|fra|fr)\b')                { return 'fr' }
    if ($s -match '\b(ger|deu|de)\b')                { return 'de' }
    if ($s -match '\b(spa|es)\b')                    { return 'es' }
    if ($s -match '\b(tha|th)\b')                    { return 'th' }
    if ($lang -and $lang -ne 'und' -and $lang -ne '') { return ([string]$lang).ToLower() }
    return 'und'
}

function Get-LangName([string]$code) {
    switch ($code) {
        'vi' { 'Vietnamese' } 'cs' { 'Czech' }  'en' { 'English' }
        'ja' { 'Japanese' }   'ko' { 'Korean' } 'zh' { 'Chinese' }
        'fr' { 'French' }     'de' { 'German' } 'es' { 'Spanish' }
        'th' { 'Thai' }       'und' { '' }
        default { $code.ToUpper() }
    }
}

# Ten codec dep: uu tien field 'codec' cua mkvmerge, fallback sang codec_id
# mkvmerge >= v50 hieu tag IETF 2 ky tu (vi/cs/en). Ban cu hon chi nhan ISO 639-2.
function Get-LangForMux([string]$code) {
    if ($script:mkvVer -ge 50) { return $code }
    switch ($code) {
        'vi' { 'vie' } 'cs' { 'cze' } 'en' { 'eng' } 'ja' { 'jpn' } 'ko' { 'kor' }
        'zh' { 'chi' } 'fr' { 'fre' } 'de' { 'ger' } 'es' { 'spa' } 'th' { 'tha' }
        default { $code }
    }
}

function Get-CodecName($props) {
    $c  = [string]$props.codec
    $id = [string]$props.codec_id
    if ($c) {
        if ($c -match '(?i)TrueHD')                 { return 'TrueHD' }
        if ($c -match '(?i)DTS-HD Master')          { return 'DTS-HD MA' }
        if ($c -match '(?i)DTS-HD High')            { return 'DTS-HD HRA' }
        if ($c -match '(?i)DTS-ES')                 { return 'DTS-ES' }
        if ($c -match '(?i)^DTS')                   { return 'DTS' }
        if ($c -match '(?i)E-AC-3|EAC3')            { return 'E-AC-3' }
        if ($c -match '(?i)AC-3|AC3')               { return 'Dolby Digital' }
        if ($c -match '(?i)AAC')                    { return 'AAC' }
        if ($c -match '(?i)FLAC')                   { return 'FLAC' }
        if ($c -match '(?i)Opus')                   { return 'Opus' }
        if ($c -match '(?i)Vorbis')                 { return 'Vorbis' }
        if ($c -match '(?i)MP3|MPEG-1 Audio layer') { return 'MP3' }
        if ($c -match '(?i)PCM')                    { return 'PCM' }
        return $c
    }
    return ($id -replace '^A_', '')
}

# Nhan dien track binh luan / thuyet minh cho nguoi khiem thi
function Test-CommentaryTrack($props) {
    if ($props.flag_commentary -eq $true) { return $true }
    $nm = [string]$props.track_name
    if (-not $nm) { return $false }
    return ($nm -match '(?i)\b(commentary|comments?|director''?s? track|filmmaker|isolated score|binh luan)\b')
}

function Test-DescriptiveTrack($props) {
    if ($props.flag_visual_impaired -eq $true) { return $true }
    $nm = [string]$props.track_name
    if (-not $nm) { return $false }
    return ($nm -match '(?i)\b(audio description|descriptive|narration for the blind|visually impaired)\b')
}

# ---------- MA HOA PHU DE NGOAI (lo hong 1) ----------
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

# Tra ve ten bang ma de truyen cho mkvmerge qua --sub-charset, '' neu khong can
function Get-SubCharsetArg([string]$path, [string]$langCode) {
    $kind = Get-SubEncodingKind $path
    if ($kind -eq 'utf8' -or $kind -eq 'utf8bom' -or $kind -eq 'empty' -or $kind -eq 'error') { return '' }
    if ($kind -eq 'utf16le') { return 'UTF-16LE' }
    if ($kind -eq 'utf16be') { return 'UTF-16BE' }
    switch ($langCode) {
        'vi' { 'windows-1258' } 'cs' { 'windows-1250' } 'ja' { 'shift_jis' }
        'ko' { 'euc-kr' }       'zh' { 'gbk' }          'th' { 'windows-874' }
        default { 'windows-1252' }
    }
}

# Phu de dang anh: PGS (Blu-ray), VobSub (DVD), DVB
function Test-ImageSub([string]$codecId) {
    return ($codecId -match '(?i)S_HDMV/PGS|S_VOBSUB|S_DVBSUB|PGS|VOBSUB')
}

function Get-ChannelName($n) {
    if (-not $n) { return '' }
    switch ([int]$n) {
        1 { '1.0' } 2 { '2.0' } 3 { '2.1' } 6 { '5.1' } 7 { '6.1' } 8 { '7.1' }
        default { "$n.0" }
    }
}

# Uoc luong bitrate: 1) tag BPS  2) ffprobe  3) heuristic theo codec + so kenh
function Get-AudioBitrate($track, $ffprobeMap) {
    $p = $track.properties
    foreach ($k in @('tag_bps','tag_bps_eng')) {
        if ($p.PSObject.Properties.Name -contains $k) {
            $v = $p.$k
            if ($v -and ($v -as [double])) { return [double]$v }
        }
    }
    if ($ffprobeMap -and $ffprobeMap.ContainsKey([int]$track.id)) {
        $v = $ffprobeMap[[int]$track.id]
        if ($v -gt 0) { return [double]$v }
    }
    # Heuristic (chi de so sanh tuong doi giua cac track English)
    $base = switch (Get-CodecName $p) {
        'PCM'           { 4608; break } 'TrueHD'        { 4000; break }
        'DTS-HD MA'     { 3500; break } 'DTS-HD HRA'    { 2000; break }
        'FLAC'          { 1400; break } 'DTS-ES'        { 1509; break }
        'DTS'           { 1509; break } 'E-AC-3'        {  768; break }
        'Dolby Digital' {  640; break } 'AAC'           {  256; break }
        'Opus'          {  256; break } 'Vorbis'        {  224; break }
        'MP3'           {  192; break } default         {  300 }
    }
    $ch = if ($p.audio_channels) { [int]$p.audio_channels } else { 2 }
    return ($base * 1000.0) * ([math]::Max($ch, 1) / 6.0)
}

function Get-FfprobeBitrates($ffprobe, $file) {
    $map = @{}
    if (-not $ffprobe) { return $map }
    try {
        $raw = & $ffprobe -v quiet -print_format json -show_streams -select_streams a "$file" 2>$null
        if (-not $raw) { return $map }
        $obj = ($raw -join "`n") | ConvertFrom-Json
        foreach ($s in $obj.streams) {
            $br = 0
            if ($s.bit_rate -and ($s.bit_rate -as [double])) { $br = [double]$s.bit_rate }
            elseif ($s.tags) {
                foreach ($tn in @('BPS','BPS-eng','BPS-en')) {
                    if ($s.tags.PSObject.Properties.Name -contains $tn -and ($s.tags.$tn -as [double])) {
                        $br = [double]$s.tags.$tn; break
                    }
                }
            }
            if ($br -gt 0) { $map[[int]$s.index] = $br }
        }
    } catch {}
    return $map
}

function Format-Kbps($bps) {
    if (-not $bps -or $bps -le 0) { return '' }
    return ("{0:N0} kbps" -f ($bps / 1000))
}

# ==========================================================
#  THE CODEC TRONG TEN FILE
# ==========================================================
# Jellyfin nhan nhieu phien ban cua mot phim bang cach: cac file nam CUNG THU MUC,
# ten file bat dau DUNG BANG ten thu muc, phan sau " - " la nhan phien ban.
#   Avengers (2019) [tmdbid-299534]\
#       Avengers (2019) [tmdbid-299534] - 2160p DV HEVC TrueHD7.1.mkv
#       Avengers (2019) [tmdbid-299534] - 1080p HEVC EAC3 5.1.mkv
# Vi vay THE CHI GAN VAO TEN FILE, ten thu muc phai giu sach.

function Get-VideoCodecName([string]$codecId, [string]$codec) {
    $s = "$codecId $codec"
    if ($s -match '(?i)AV1')                 { return 'AV1' }
    if ($s -match '(?i)HEVC|H\.?265|MPEGH')  { return 'HEVC' }
    if ($s -match '(?i)AVC|H\.?264|MPEG4')   { return 'H264' }
    if ($s -match '(?i)VP9')                 { return 'VP9' }
    if ($s -match '(?i)MPEG-?2')             { return 'MPEG2' }
    return ''
}

function Get-ResolutionName([string]$dim) {
    if (-not $dim -or $dim -notmatch '^(\d+)x(\d+)$') { return '' }
    $h = [int]$Matches[2]
    if ($h -ge 2000) { return '2160p' }
    if ($h -ge 1400) { return '1440p' }
    if ($h -ge 900)  { return '1080p' }
    if ($h -ge 600)  { return '720p' }
    if ($h -ge 500)  { return '576p' }
    return '480p'
}

# Dai dong (HDR/DV): mkvmerge khong bao, phai hoi ffprobe.
# Khong co ffprobe thi doc tu ten file goc - ban release hau het deu ghi.
function Get-DynamicRange([string]$ffprobe, [string]$file, [string]$origName) {
    $dv = $false; $hdr = ''
    if ($ffprobe) {
        try {
            $raw = & $ffprobe -v quiet -print_format json -show_streams -select_streams v:0 "$file" 2>$null
            if ($raw) {
                $j = ($raw -join "`n") | ConvertFrom-Json
                $v = @($j.streams)[0]
                if ($v) {
                    $ct = [string]$v.color_transfer
                    if ($ct -match '(?i)smpte2084') { $hdr = 'HDR' }
                    elseif ($ct -match '(?i)arib-std-b67') { $hdr = 'HLG' }
                    foreach ($sd in @($v.side_data_list)) {
                        $t = [string]$sd.side_data_type
                        if ($t -match '(?i)dolby vision|DOVI') { $dv = $true }
                        if ($t -match '(?i)HDR Dynamic Metadata') { $hdr = 'HDR10+' }
                    }
                }
            }
        } catch {}
    }
    # Khong doc duoc thi lay tu ten file
    if (-not $dv -and $origName -match '(?i)\b(DV|DoVi|Dolby[\. ]?Vision)\b') { $dv = $true }
    if (-not $hdr) {
        if ($origName -match '(?i)\bHDR10\+|\bHDR10P\b') { $hdr = 'HDR10+' }
        elseif ($origName -match '(?i)\bHDR\b')          { $hdr = 'HDR' }
        elseif ($origName -match '(?i)\bHLG\b')          { $hdr = 'HLG' }
    }
    $out = @()
    if ($dv)  { $out += 'DV' }
    if ($hdr) { $out += $hdr }
    return ($out -join ' ')
}

# Dung the: "2160p DV HEVC TrueHD7.1"
function Get-CodecTag($json, [string]$file, [string]$ffprobe, [string]$origName, $defAudioTrack) {
    if (-not $AddCodecTag) { return '' }
    $parts = @()

    $vt = @($json.tracks | Where-Object { $_.type -eq 'video' })[0]
    if ($vt) {
        if ($CodecTagParts -contains 'res') {
            $r = Get-ResolutionName ([string]$vt.properties.pixel_dimensions)
            if ($r) { $parts += $r }
        }
        if ($CodecTagParts -contains 'hdr') {
            $d = Get-DynamicRange $ffprobe $file $origName
            if ($d) { $parts += $d }
        }
        if ($CodecTagParts -contains 'vcodec') {
            $vc = Get-VideoCodecName ([string]$vt.properties.codec_id) ([string]$vt.properties.codec)
            if ($vc) { $parts += $vc }
        }
    }

    if ($CodecTagParts -contains 'acodec' -and $defAudioTrack) {
        $ap = $defAudioTrack.properties
        $ac = Get-CodecName $ap
        # Rut gon cho ten file khoi dai
        $ac = switch ($ac) {
            'Dolby Digital' { 'AC3' }
            'E-AC-3'        { 'EAC3' }
            'DTS-HD MA'     { 'DTS-HD' }
            'DTS-HD HRA'    { 'DTS-HD' }
            default         { $ac }
        }
        $ch = Get-ChannelName $ap.audio_channels
        $atmos = ''
        if (("$($ap.track_name) $($ap.codec)" -match '(?i)atmos') -or ($origName -match '(?i)\batmos\b')) { $atmos = ' Atmos' }
        if ($ac) { $parts += ("$ac$ch$atmos".Trim()) }
    }

    if ($parts.Count -eq 0) { return '' }
    return (Get-SafeFileName ($parts -join ' '))
}

# ==========================================================
#  HAM TMDB
# ==========================================================
$script:TmdbCache = @{}

# Loai bo ky tu Windows cam dat ten file
function Get-SafeFileName([string]$n) {
    $n = $n -replace ':', ' -'
    $n = $n -replace '[\\/:*?"<>|]', ''
    $n = $n -replace '\s+', ' '
    return $n.Trim().TrimEnd('.')
}

function Get-NormTitle([string]$t) { return (($t -replace '[^a-zA-Z0-9]', '').ToLower()) }

# Tach ten release ("Avengers.Endgame.2019.2160p.BluRay.x265-GRP") -> Title + Year
function Split-ReleaseName([string]$name) {
    $t = $name -replace '^\[[^\]]+\]\s*', ''
    $t = $t -replace '[\._]+', ' '
    $t = $t -replace '\s+', ' '
    $junk = '(?i)\b(2160p|1080p|1440p|720p|480p|4k|uhd|bluray|blu-ray|bdrip|brrip|bdremux|remux|web-?dl|webrip|web|hdtv|dvdrip|hdrip|x ?264|x ?265|h ?264|h ?265|hevc|avc|xvid|aac\d*|ac3|eac3|ddp?\d?|dts(-hd)?|truehd|atmos|flac|opus|hdr\d*|dovi|sdr|imax|proper|repack|extended|uncut|unrated|remastered|criterion|multi|dual|vietsub|thuyet minh|long tieng|10bit|8bit)\b'
    $m = [regex]::Match($t, $junk)
    $head = if ($m.Success) { $t.Substring(0, $m.Index) } else { $t }
    $year = $null; $title = $head
    $ym = [regex]::Matches($head, '\b(19|20)\d{2}\b')
    if ($ym.Count -gt 0) {
        $last  = $ym[$ym.Count - 1]
        $year  = [int]$last.Value
        $title = $head.Substring(0, $last.Index)
    }
    $title = ($title -replace '[\(\)\[\]\{\}]', ' ') -replace '\s+', ' '
    return [pscustomobject]@{ Title = $title.Trim(" -_".ToCharArray()); Year = $year }
}

# Goi TMDB co thu lai khi loi mang (lo hong 4)
function Invoke-TmdbApi([string]$url, [switch]$Quiet404) {
    if ($script:TmdbAuthFailed) { return $null }
    # Key v4 "API Read Access Token" (chuoi dai bat dau bang eyJ) phai gui qua header,
    # khong duoc dat vao ?api_key= nhu key v3 (32 ky tu).
    $hdr = @{}
    if (([string]$TmdbApiKey).StartsWith('eyJ')) {
        $url = $url -replace 'api_key=[^&]*&?', ''
        $hdr['Authorization'] = "Bearer $TmdbApiKey"
    }
    for ($i = 1; $i -le $TmdbRetry; $i++) {
        try {
            return (Invoke-RestMethod -Uri $url -Headers $hdr -TimeoutSec 20 -ErrorAction Stop)
        } catch {
            $msg = $_.Exception.Message
            $code = 0
            try { $code = [int]$_.Exception.Response.StatusCode } catch {}
            # 404 khi tra THU mot tap/season la chuyen binh thuong -> im lang
            if ($Quiet404 -and ($code -eq 404 -or $msg -match '404')) { return $null }
            # Loi 401/404 la loi that su, thu lai vo ich
            if ($code -eq 401 -or $msg -match '\b401\b|Unauthorized') {
                Write-Step "  [!] TMDB tu choi API key (401) -> tat TMDB cho lan chay nay." 'Red'
                Write-Step "      Kiem tra `$TmdbApiKey: dan 'API Key' (32 ky tu) hoac 'API Read Access Token' (bat dau eyJ)." 'Yellow'
                $script:TmdbAuthFailed = $true
                return $null
            }
            if ($code -eq 404 -or $msg -match '404') {
                Write-Step "  [!] TMDB khong co du lieu (404): $msg" 'Yellow'
                return $null
            }
            if ($i -lt $TmdbRetry) {
                $wait = [math]::Pow(2, $i)
                Write-Step "  [!] TMDB loi mang (lan $i/$TmdbRetry), thu lai sau $wait giay..." 'Yellow'
                Start-Sleep -Seconds $wait
            } else {
                Write-Step "  [!] TMDB that bai sau $TmdbRetry lan: $msg" 'Red'
            }
        }
    }
    return $null
}

function Find-TmdbMovie([string]$title, $year) {
    if (-not $title) { return $null }
    $url = "https://api.themoviedb.org/3/search/movie?api_key=$TmdbApiKey&language=$TmdbLang" +
           "&include_adult=false&query=$([uri]::EscapeDataString($title))"
    if ($year) { $url += "&year=$year" }
    $r = Invoke-TmdbApi $url
    if ($null -eq $r) { return $null }
    if (-not $r.results -or @($r.results).Count -eq 0) {
        if ($year) { return (Find-TmdbMovie $title $null) }   # thu lai, bo dieu kien nam
        return $null
    }
    return @($r.results)
}

# Chon ket qua tu danh sach TMDB tra ve (dung chung cho ca movie va tv)
function Select-TmdbCandidate($res, [string]$wantTitle, $wantYear, [string]$nameProp, [string]$origProp, [string]$dateProp) {
    $wantN = Get-NormTitle $wantTitle
    $exact = @($res | Where-Object {
        ((Get-NormTitle $_.$nameProp) -eq $wantN -or (Get-NormTitle $_.$origProp) -eq $wantN) -and
        ((-not $wantYear) -or ($_.$dateProp -and $_.$dateProp.Length -ge 4 -and
          [math]::Abs([int]$_.$dateProp.Substring(0,4) - $wantYear) -le 1))
    })
    if ($exact.Count -ge 1) { return $exact[0] }
    if (-not $TmdbConfirm) { return $res[0] }

    Write-Step "  [?] Khong khop chinh xac. Cac ket qua gan dung nhat:" 'Yellow'
    $top = @($res | Select-Object -First 5)
    for ($i = 0; $i -lt $top.Count; $i++) {
        $yy = if ($top[$i].$dateProp) { $top[$i].$dateProp.Substring(0,4) } else { '????' }
        Write-Step "      [$($i+1)] $($top[$i].$nameProp) ($yy)  tmdbid-$($top[$i].id)" 'White'
    }
    $ans = Read-Host "      Chon 1-$($top.Count), hoac Enter de giu ten file goc"
    if ($ans -match '^[1-9]$' -and [int]$ans -le $top.Count) { return $top[[int]$ans - 1] }
    return $null
}

# Tach ten tap phim bo: tra ve Series / Year / Season / EpStart / EpEnd
function Split-EpisodeName([string]$name) {
    $t = $name -replace '^\[[^\]]+\]\s*', ''
    $t = $t -replace '[\._]+', ' '
    $t = $t -replace '\s+', ' '

    $se = $null; $e1 = $null; $e2 = $null; $idx = -1; $abs = $false
    $m = [regex]::Match($t, '(?i)\bS(\d{1,2})\s?E(\d{1,4})(?:\s?-\s?|\s?)?(?:E(\d{1,4}))?\b')
    if ($m.Success) {
        $se = [int]$m.Groups[1].Value; $e1 = [int]$m.Groups[2].Value
        if ($m.Groups[3].Success) { $e2 = [int]$m.Groups[3].Value }
        $idx = $m.Index
    } else {
        $m2 = [regex]::Match($t, '(?<!\d)(\d{1,2})x(\d{2})(?!\d)')
        if ($m2.Success) { $se = [int]$m2.Groups[1].Value; $e1 = [int]$m2.Groups[2].Value; $idx = $m2.Index }
    }
    if ($idx -lt 0) {
        # Kieu anime danh so TUYET DOI, khong co season:
        #   "One Piece - 0643"   "[Judas] One Piece - 1085 [1080p]"   "Naruto EP 055"
        $m3 = [regex]::Match($t, '(?i)\s-\s(?:E|EP|Episode\s?)?(\d{1,4})(?:v\d)?(?=\s|$|\[|\()')
        if (-not $m3.Success) { $m3 = [regex]::Match($t, '(?i)\b(?:EP|Episode)\s?(\d{1,4})(?:v\d)?\b') }
        if ($m3.Success) {
            $n = [int]$m3.Groups[1].Value
            # "Phim - 1917" la NAM, khong phai so tap
            $isYear = ($m3.Groups[1].Value.Length -eq 4 -and $n -ge 1900 -and $n -le 2099)
            if (-not $isYear -and $n -gt 0) { $se = 0; $e1 = $n; $idx = $m3.Index; $abs = $true }
        }
    }
    if ($idx -lt 0) { return $null }

    $head = $t.Substring(0, $idx)
    $year = $null; $series = $head
    $ym = [regex]::Matches($head, '\b(19|20)\d{2}\b')
    if ($ym.Count -gt 0) {
        $last = $ym[$ym.Count - 1]
        $year = [int]$last.Value
        $series = $head.Substring(0, $last.Index)
    }
    $series = ($series -replace '[\(\)\[\]\{\}]', ' ') -replace '\s+', ' '
    return [pscustomobject]@{
        Series = $series.Trim(" -_".ToCharArray()); Year = $year
        Season = $se; EpStart = $e1; EpEnd = $e2; Absolute = $abs
    }
}

# Nhan de in ra man hinh: "S16E643" hoac "tap #643" (so tuyet doi)
function Format-EpRef($ep) {
    if ($ep.Absolute) { return "tap #$($ep.EpStart)" }
    return ("S{0:D2}E{1:D2}" -f $ep.Season, $ep.EpStart)
}

function Find-TmdbTv([string]$title, $year) {
    if (-not $title) { return $null }
    $url = "https://api.themoviedb.org/3/search/tv?api_key=$TmdbApiKey&language=$TmdbLang" +
           "&include_adult=false&query=$([uri]::EscapeDataString($title))"
    if ($year) { $url += "&first_air_date_year=$year" }
    $r = Invoke-TmdbApi $url
    if ($null -eq $r) { return $null }
    if (-not $r.results -or @($r.results).Count -eq 0) {
        if ($year) { return (Find-TmdbTv $title $null) }
        return $null
    }
    return @($r.results)
}

function Get-TmdbEpisodeTitle([int]$tvId, [int]$se, [int]$ep) {
    $url = "https://api.themoviedb.org/3/tv/$tvId/season/$se/episode/$ep" +
           "?api_key=$TmdbApiKey&language=$TmdbLang"
    $r = Invoke-TmdbApi $url -Quiet404
    if ($r -and $r.name) { return [string]$r.name }
    return $null
}

# Tra ve "Ten phim (Nam) [tmdbid-xxxxx]" hoac $null neu khong xac dinh duoc
# Dat ten PHIM LE theo database da chon ($db = tmdb | tvdb)
# (luu y: PowerShell khong phan biet hoa thuong ten bien -> dung $dbU, khong dung $DB)
function Get-MovieFileName([string]$origBase, [string]$db) {
    $dbU = $db.ToUpper()

    $parsed = Split-ReleaseName $origBase
    if (-not $parsed.Title) { return $null }
    $key = "MV|$db|$($parsed.Title)|$($parsed.Year)"
    if ($script:TmdbCache.ContainsKey($key)) { return $script:TmdbCache[$key] }

    Write-Step "  [~] $dbU dang tra phim le: '$($parsed.Title)'$(if($parsed.Year){" ($($parsed.Year))"})" 'DarkCyan'
    $res = if ($db -eq 'tvdb') { @(Find-TvdbMovie $parsed.Title $parsed.Year) } else { Find-TmdbMovie $parsed.Title $parsed.Year }
    if ($res -and @($res).Count -eq 0) { $res = $null }
    if (-not $res) {
        Write-Step "  [!] $dbU khong tim thay phim nay -> giu ten file goc." 'Yellow'
        $script:TmdbCache[$key] = $null
        return $null
    }

    $wantN = Get-NormTitle $parsed.Title
    $exact = @($res | Where-Object {
        ((Get-NormTitle $_.title) -eq $wantN -or (Get-NormTitle $_.original_title) -eq $wantN) -and
        ((-not $parsed.Year) -or ($_.release_date -and $_.release_date.Length -ge 4 -and
          [math]::Abs([int]$_.release_date.Substring(0,4) - $parsed.Year) -le 1))
    })

    $pick = $null
    if ($exact.Count -ge 1) { $pick = $exact[0] }
    elseif ($TmdbConfirm) {
        Write-Step "  [?] Khong khop chinh xac. Cac ket qua gan dung nhat:" 'Yellow'
        $top = @($res | Select-Object -First 5)
        for ($i = 0; $i -lt $top.Count; $i++) {
            $yy = if ($top[$i].release_date) { $top[$i].release_date.Substring(0,4) } else { '????' }
            Write-Step "      [$($i+1)] $($top[$i].title) ($yy)  $($db)id-$($top[$i].id)" 'White'
        }
        $ans = Read-Host "      Chon 1-$($top.Count), hoac Enter de giu ten file goc"
        if ($ans -match '^[1-9]$' -and [int]$ans -le $top.Count) { $pick = $top[[int]$ans - 1] }
    }
    else { $pick = $res[0] }

    if (-not $pick) { $script:TmdbCache[$key] = $null; return $null }

    $yr = if ($pick.release_date -and $pick.release_date.Length -ge 4) { $pick.release_date.Substring(0,4) }
          else { $parsed.Year }
    if (-not $yr) {
        Write-Step "  [!] $dbU khong co nam phat hanh -> giu ten file goc." 'Yellow'
        $script:TmdbCache[$key] = $null; return $null
    }
    # Ten phim phai sach ngoac truoc, neu khong Jellyfin se doc nham phan trong ngoac
    $cleanTitle = (Get-SafeFileName $pick.title) -replace '[\[\]\(\)\{\}]', ' '
    $cleanTitle = ($cleanTitle -replace '\s+', ' ').Trim()
    $final = "$cleanTitle ($yr) [$($db)id-$($pick.id)]"
    $script:TmdbCache[$key] = $final
    return $final
}

# Tra ve ten tap + duong dan day du theo chuan Jellyfin cho phim bo
# Chi tiet series: danh sach season + so tap moi season (co bo dem)
function Get-TmdbTvDetails([int]$tvId) {
    $k = "TVD|$tvId"
    if ($script:TmdbCache.ContainsKey($k)) { return $script:TmdbCache[$k] }
    $r = Invoke-TmdbApi ("https://api.themoviedb.org/3/tv/$tvId" + "?api_key=$TmdbApiKey&language=$TmdbLang") -Quiet404
    $script:TmdbCache[$k] = $r
    return $r
}

# Nhieu series TRUNG TEN (vd 'One Piece' anime 1999 va ban live-action 2023).
# Ten file khong ghi nam -> chon series thuc su CO season/tap do, thay vi lay ket qua dau.
function Select-TmdbTvShow($res, $ep) {
    $wantN = Get-NormTitle $ep.Series
    $exact = @($res | Where-Object {
        (Get-NormTitle $_.name) -eq $wantN -or (Get-NormTitle $_.original_name) -eq $wantN })
    if ($ep.Year -or $exact.Count -le 1) {
        return (Select-TmdbCandidate $res $ep.Series $ep.Year 'name' 'original_name' 'first_air_date')
    }

    Write-Step "  [?] Co $($exact.Count) series cung ten '$($ep.Series)' - kiem tra series nao co $(Format-EpRef $ep)..." 'Yellow'
    $best = $null; $bestScore = -1
    foreach ($c in ($exact | Select-Object -First 6)) {
        $d = Get-TmdbTvDetails ([int]$c.id)
        $ss = @(); if ($d -and $d.seasons) { $ss = @($d.seasons | Where-Object { [int]$_.season_number -ge 1 }) }
        $hasSeason = @($ss | Where-Object { [int]$_.season_number -eq $ep.Season }).Count -gt 0
        $total = 0; foreach ($x in $ss) { $total += [int]$x.episode_count }
        $score = 0
        if ($hasSeason) { $score += 2 }
        if ($total -ge $ep.EpStart) { $score += 1 }
        $yy = if ($c.first_air_date -and $c.first_air_date.Length -ge 4) { $c.first_air_date.Substring(0,4) } else { '????' }
        Write-Step ("      - {0} ({1})  tmdbid-{2}: {3} season, {4} tap" -f $c.name, $yy, $c.id, $ss.Count, $total) 'DarkGray'
        if ($score -gt $bestScore) { $bestScore = $score; $best = $c }
    }
    if ($bestScore -le 0) {
        Write-Step "  [!] Khong series nao co $(Format-EpRef $ep)." 'Yellow'
        return (Select-TmdbCandidate $res $ep.Series $ep.Year 'name' 'original_name' 'first_air_date')
    }
    $yy = if ($best.first_air_date -and $best.first_air_date.Length -ge 4) { $best.first_air_date.Substring(0,4) } else { '????' }
    Write-Step "  [i] -> Chon: $($best.name) ($yy)" 'Green'
    return $best
}

# Tim dung tap tren TMDB. Xu ly so tap TUYET DOI cua anime (vd One Piece S16E643):
# TMDB danh so lai tu dau moi season nen /season/16/episode/643 khong ton tai.
function Resolve-TmdbEpisode([int]$tvId, [int]$se, [int]$ep) {
    $out = [pscustomobject]@{ Season = $se; Episode = $ep; Title = $null; Mapped = $false }

    # 1. Tra thang (bo qua khi ten file chi co so tuyet doi - season 0 la tap dac biet)
    if ($se -ge 1) {
        $t = Get-TmdbEpisodeTitle $tvId $se $ep
        if ($t) { $out.Title = $t; return $out }
    }

    $d = Get-TmdbTvDetails $tvId
    if (-not $d -or -not $d.seasons) { return $out }
    $ss = @($d.seasons | Where-Object { [int]$_.season_number -ge 1 } | Sort-Object { [int]$_.season_number })

    # Tap nay nam trong pham vi season -> khong phai so tuyet doi, chi la TMDB chua co ten
    $cur = @($ss | Where-Object { [int]$_.season_number -eq $se })[0]
    if ($cur -and $ep -le [int]$cur.episode_count) { return $out }

    # 2. Coi la so tuyet doi: cong don so tap cac season de tim season/tap tuong ung
    $sum = 0; $S = -1; $rel = -1
    foreach ($x in $ss) {
        $n = [int]$x.episode_count
        if ($ep -le $sum + $n) { $S = [int]$x.season_number; $rel = $ep - $sum; break }
        $sum += $n
    }
    if ($S -lt 0) { return $out }

    # TMDB danh so lai trong season
    $t = Get-TmdbEpisodeTitle $tvId $S $rel
    if ($t) {
        $out.Season = $S; $out.Episode = $rel; $out.Title = $t; $out.Mapped = $true
        return $out
    }
    # Mot so anime TMDB giu so tuyet doi ngay trong season
    $t = Get-TmdbEpisodeTitle $tvId $S $ep
    if ($t) { $out.Season = $S; $out.Title = $t; $out.Mapped = ($S -ne $se); return $out }
    return $out
}

function Get-TmdbTvName([string]$origBase, [string]$rootDir) {
    $ep = Split-EpisodeName $origBase
    if (-not $ep -or -not $ep.Series) { return $null }

    $key = "TV|$($ep.Series)|$($ep.Year)"
    if ($script:TmdbCache.ContainsKey($key)) { $show = $script:TmdbCache[$key] }
    else {
        Write-Step "  [~] TMDB (TV) dang tra: '$($ep.Series)'$(if($ep.Year){" ($($ep.Year))"})" 'DarkCyan'
        $res = Find-TmdbTv $ep.Series $ep.Year
        if (-not $res) {
            Write-Step "  [!] TMDB khong tim thay series -> giu ten file goc." 'Yellow'
            $script:TmdbCache[$key] = $null; return $null
        }
        $pick = Select-TmdbTvShow $res $ep
        if (-not $pick) { $script:TmdbCache[$key] = $null; return $null }

        $yr = if ($pick.first_air_date -and $pick.first_air_date.Length -ge 4) { $pick.first_air_date.Substring(0,4) }
              else { $ep.Year }
        $nm = (Get-SafeFileName $pick.name) -replace '[\[\]\(\)\{\}]', ' '
        $nm = ($nm -replace '\s+', ' ').Trim()
        $folder = if ($yr) { "$nm ($yr) [tmdbid-$($pick.id)]" } else { "$nm [tmdbid-$($pick.id)]" }
        $show = [pscustomobject]@{ Id = $pick.id; Name = $nm; Year = $yr; Folder = $folder }
        $script:TmdbCache[$key] = $show
    }
    if (-not $show) { return $null }

    $seNum = $ep.Season; $epNum = $ep.EpStart
    $epTitle = $null
    if (($TvFetchEpisodeTitle -or $TvAbsoluteToSeason) -and -not $ep.EpEnd) {
        $rt = Resolve-TmdbEpisode $show.Id $ep.Season $ep.EpStart
        if ($rt.Mapped) {
            Write-Step ("  [i] So tap tuyet doi: {0} -> TMDB S{1:D2}E{2:D2}" -f `
                        (Format-EpRef $ep), $rt.Season, $rt.Episode) 'Cyan'
            if ($TvAbsoluteToSeason) { $seNum = $rt.Season; $epNum = $rt.Episode }
        } elseif (-not $rt.Title) {
            Write-Step "  [!] TMDB khong co $(Format-EpRef $ep) -> giu ma tap goc, khong co ten tap." 'Yellow'
        }
        if ($TvFetchEpisodeTitle) { $epTitle = $rt.Title }
    }

    if ($seNum -le 0) { $seNum = 1 }
    # Ma tap: S01E05 hoac S01E05-E06 (tap doi)
    $code = "S{0:D2}E{1:D2}" -f $seNum, $epNum
    if ($ep.EpEnd) { $code += "-E{0:D2}" -f $ep.EpEnd }

    if ($epTitle) {
        $epTitle = (Get-SafeFileName $epTitle) -replace '[\[\]\{\}]', ' '
        $epTitle = ($epTitle -replace '\s+', ' ').Trim()
    }

    $prefix = if ($show.Year) { "$($show.Name) ($($show.Year))" } else { $show.Name }
    $base   = if ($epTitle) { "$prefix - $code - $epTitle" } else { "$prefix - $code" }
    $base   = Get-SafeFileName $base

    $dir = Join-Path $rootDir $show.Folder
    if ($TvSeasonFolder) { $dir = Join-Path $dir ("Season {0:D2}" -f $seNum) }
    return [pscustomobject]@{ Base = $base; Dir = $dir }
}

# Dieu phoi: phim bo hay phim le, tra ve ten + duong dan file dich
# Database nay dung duoc khong (co key + key khong bi tu choi)
function Test-DbUsable([string]$db) {
    if ($db -eq 'tmdb') { return ([bool]$TmdbApiKey -and -not $script:TmdbAuthFailed) }
    if ($db -eq 'tvdb') { return ([bool]$TvdbApiKey -and -not $script:TvdbLoginFailed) }
    return $false
}

function Get-TvNameFrom([string]$db, [string]$origBase, [string]$rootDir) {
    if ($db -eq 'tvdb') { return (Get-TvdbTvName $origBase $rootDir) }
    return (Get-TmdbTvName $origBase $rootDir)
}

# Dieu phoi: phim bo hay phim le, tra ve ten + duong dan file dich theo database da chon
function Resolve-OutputName([string]$origBase, [string]$rootDir) {
    $keep = [pscustomobject]@{ Base = $origBase; Path = (Join-Path $rootDir "$origBase.mkv") }
    $db = $script:NameDb
    if ($db -ne 'tmdb' -and $db -ne 'tvdb') { return $keep }
    $other = if ($db -eq 'tvdb') { 'tmdb' } else { 'tvdb' }
    $isEpisode = $null -ne (Split-EpisodeName $origBase)

    if ($isEpisode) {
        $used = $db
        $tv = Get-TvNameFrom $db $origBase $rootDir
        if (-not $tv -and $NameFallbackOther -and (Test-DbUsable $other)) {
            Write-Step "  [~] $($db.ToUpper()) khong xac dinh duoc -> thu $($other.ToUpper())..." 'Yellow'
            $tv = Get-TvNameFrom $other $origBase $rootDir
            if ($tv) { $used = $other }
        }
        if ($tv) {
            Write-Step "  [T] TV Show ($($used.ToUpper())): $($tv.Base)" 'Green'
            if (-not (Test-Path -LiteralPath $tv.Dir)) { New-Item -ItemType Directory -Path $tv.Dir -Force | Out-Null }
            return [pscustomobject]@{ Base = $tv.Base; Path = (Join-Path $tv.Dir "$($tv.Base).mkv") }
        }
        Write-Step "  [!] $($db.ToUpper()) khong xac dinh duoc phim bo nay -> giu ten file goc." 'Yellow'
        return $keep
    }

    $used = $db
    $mv = Get-MovieFileName $origBase $db
    if (-not $mv -and $NameFallbackOther -and (Test-DbUsable $other)) {
        Write-Step "  [~] $($db.ToUpper()) khong xac dinh duoc -> thu $($other.ToUpper())..." 'Yellow'
        $mv = Get-MovieFileName $origBase $other
        if ($mv) { $used = $other }
    }
    if ($mv) {
        Write-Step "  [T] Phim le ($($used.ToUpper())): $mv" 'Green'
        $dir = $rootDir
        if ($TmdbCreateFolder) {
            $dir = Join-Path $rootDir $mv
            if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        }
        return [pscustomobject]@{ Base = $mv; Path = (Join-Path $dir "$mv.mkv") }
    }
    return $keep
}

function Set-ConsoleSize([int]$cols, [int]$rows, [int]$scrollback) {
    try {
        $raw = $Host.UI.RawUI
        $buf = $raw.BufferSize; $win = $raw.WindowSize
        $maxW = $raw.MaxPhysicalWindowSize.Width; $maxH = $raw.MaxPhysicalWindowSize.Height
        if ($cols -gt $maxW) { $cols = $maxW }
        if ($rows -gt $maxH) { $rows = $maxH }
        $buf.Width = [math]::Max($cols, $buf.Width)
        $buf.Height = [math]::Max($scrollback, 3000)
        $raw.BufferSize = $buf
        $win.Width = $cols; $win.Height = $rows
        $raw.WindowSize = $win
        $buf = $raw.BufferSize; $buf.Width = $cols; $raw.BufferSize = $buf
    } catch { }
}

# ==========================================================
#  BANG CHON PHU DE CHO TUNG TAP  (moi o v12)
# ==========================================================
# Doc truoc TAT CA cac tap, liet ke moi phu de (sub ngoai canh file + sub trong MKV)
# thanh mot bang: moi dong la mot tap, moi cot la mot loai phu de.
# O duoc tick san theo dung quy tac tu dong cua ban v11; ban chi can sua o nao can sua.

# Tra ve danh sach phu de cua MOT file, kem quyet dinh tu dong (giong het v11)
function Get-SubAutoPlan($f, $json) {
    $items = @()
    $base  = $f.BaseName

    # ---- Sub ngoai (cung logic BUOC 3) ----
    $cands = @(Get-ChildItem -LiteralPath $f.DirectoryName -File -ErrorAction SilentlyContinue | Where-Object {
        ($SubExtPriority -contains $_.Extension.ToLower()) -and
        ($_.BaseName.Length -ge $base.Length) -and
        ([string]::Equals($_.BaseName.Substring(0, $base.Length), $base, 'OrdinalIgnoreCase'))
    })
    $byLang = @{}
    foreach ($c in $cands) {
        $suffix = $c.BaseName.Substring($base.Length)
        if ($suffix -match '(?i)forced' -and $RemoveForcedSubs) { continue }
        $code = 'und'
        if     ($suffix -match '(?i)(^|[._\- ])(vi|vie|viet|vietnamese)([._\- ]|$)') { $code = 'vi' }
        elseif ($suffix -match '(?i)(^|[._\- ])(cs|cz|cze|ces|czech)([._\- ]|$)')    { $code = 'cs' }
        elseif ($suffix -match '(?i)(^|[._\- ])(en|eng|english)([._\- ]|$)')         { $code = 'en' }
        elseif ($suffix.Trim() -eq '')                                              { $code = $DefaultExternalLang }
        else { continue }
        if ($SubKeepLangs -notcontains $code) { continue }
        $rank = $SubExtPriority.IndexOf($c.Extension.ToLower())
        if (-not $byLang.ContainsKey($code) -or $rank -lt $byLang[$code].Rank) {
            $byLang[$code] = [pscustomobject]@{ File = $c; Rank = $rank; Code = $code }
        }
    }
    $extLangs = @()
    foreach ($code in @($byLang.Keys)) {
        $e = $byLang[$code]
        $nm = Get-LangName $code; if (-not $nm) { $nm = 'Khong ro' }
        $items += [pscustomobject]@{
            Key = "E|$code"; Kind = 'ext'; Id = -1; Code = $code; AutoKeep = $true
            Header = "NGOAI`n$nm ($($e.File.Extension.TrimStart('.').ToUpper()))"
            Tip = "File ngoai: $($e.File.Name)"
            Order = $(if ($SubDefaultPriority -contains $code) { $SubDefaultPriority.IndexOf($code) } else { 50 })
        }
        if ($ExternalSubOverridesInternal) { $extLangs += $code }
    }

    # ---- Sub trong MKV (cung logic BUOC 4) ----
    $subTracks = @($json.tracks | Where-Object { $_.type -eq 'subtitles' })
    $hasCleanEng = $false
    foreach ($t in $subTracks) {
        $code = Get-LangCode $t.properties.language $t.properties.language_ietf
        $nm   = [string]$t.properties.track_name
        $sdh  = ($nm -match '(?i)\bSDH\b|hearing') -or ($t.properties.flag_hearing_impaired -eq $true)
        $frc  = ($nm -match '(?i)forced') -or ($t.properties.forced_track -eq $true) -or ($t.properties.flag_forced -eq $true)
        if ($code -eq 'en' -and -not $sdh -and -not $frc) { $hasCleanEng = $true; break }
    }
    $seen = @{}
    foreach ($t in $subTracks) {
        $id   = [int]$t.id
        $p    = $t.properties
        $code = Get-LangCode $p.language $p.language_ietf
        $nm   = [string]$p.track_name
        $sdh  = ($nm -match '(?i)\bSDH\b|hearing') -or ($p.flag_hearing_impaired -eq $true)
        $frc  = ($nm -match '(?i)forced') -or ($p.forced_track -eq $true) -or ($p.flag_forced -eq $true)
        $img  = Test-ImageSub ([string]$p.codec_id)
        $cmt  = Test-CommentaryTrack $p

        $keep = $true; $why = 'giu'
        if     ($SubKeepLangs -notcontains $code)              { $keep = $false; $why = 'ngoai danh sach ngon ngu' }
        elseif ($extLangs -contains $code)                     { $keep = $false; $why = 'da co sub ngoai thay the' }
        elseif ($frc -and $RemoveForcedSubs)                   { $keep = $false; $why = 'Forced' }
        elseif ($RemoveImageSubs -and $img)                    { $keep = $false; $why = 'phu de dang anh' }
        elseif ($RemoveCommentarySubs -and $cmt)               { $keep = $false; $why = 'Commentary' }
        elseif ($code -eq 'en' -and $sdh -and $hasCleanEng)    { $keep = $false; $why = 'SDH (da co English thuong)' }

        $label = Get-LangName $code; if (-not $label) { $label = 'Khong ro' }
        if ($sdh) { $label += ' SDH' }
        if ($frc) { $label += ' Forced' }
        if ($img) { $label += ' [anh]' }
        if ($cmt) { $label += ' [binh luan]' }

        # Hai track cung nhan trong mot file -> tach thanh cot rieng
        $k = "I|$label"
        if ($seen.ContainsKey($k)) {
            $seen[$k]++; $n = $seen[$k]
            $k = "$k #$n"; $label = "$label #$n"
        } else { $seen[$k] = 1 }

        $items += [pscustomobject]@{
            Key = $k; Kind = 'int'; Id = $id; Code = $code; AutoKeep = $keep
            Header = "TRONG MKV`n$label"
            Tip = "Track #$id  $($p.codec_id)$(if($nm){"  '$nm'"})  -> tu dong: $why"
            Order = 100
        }
    }
    return $items
}

# Mo cua so bang. Tra ve hashtable: duong dan file -> @{ Ext = ma ngon ngu; Int = id track }
# Tra ve $null neu nguoi dung bam Huy (khi do dung quy tac tu dong nhu v11).
function Show-SubPickerGrid($plans) {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    [System.Windows.Forms.Application]::EnableVisualStyles()

    # Gom cot: sub ngoai truoc (theo thu tu uu tien), sub trong sau (theo thu tu gap)
    $colDefs = [ordered]@{}
    foreach ($pl in $plans) {
        foreach ($it in ($pl.Items | Sort-Object Order)) {
            if (-not $colDefs.Contains($it.Key)) { $colDefs[$it.Key] = $it.Header }
        }
    }
    $extKeys = @($colDefs.Keys | Where-Object { $_ -like 'E|*' })
    $intKeys = @($colDefs.Keys | Where-Object { $_ -like 'I|*' })
    $keys    = @($extKeys) + @($intKeys)

    $cOn    = [System.Drawing.Color]::FromArgb(214, 245, 221)
    $cOff   = [System.Drawing.Color]::White
    $cNa    = [System.Drawing.Color]::FromArgb(225, 225, 228)
    $cDiff  = [System.Drawing.Color]::FromArgb(255, 236, 179)

    $form = New-Object System.Windows.Forms.Form
    $form.Text = "MuxTool - Chon phu de giu lai cho tung tap ($($plans.Count) file)"
    $form.Size = New-Object System.Drawing.Size(1280, 720)
    $form.StartPosition = 'CenterScreen'
    $form.TopMost = $true
    $form.Font = New-Object System.Drawing.Font('Segoe UI', 9)

    $lbl = New-Object System.Windows.Forms.Label
    $lbl.Dock = 'Top'; $lbl.Height = 54
    $lbl.Padding = New-Object System.Windows.Forms.Padding(8, 6, 8, 0)
    $lbl.Text = "Tick = GIU phu de do. O xam = tap do khong co loai phu de nay. O vang = khac voi quy tac tu dong.`n" +
                "Bam TIEU DE COT de tick/bo tick ca cot cho moi tap. Bam TEN TAP (cot dau) de tick/bo tick ca dong.`n" +
                "Re chuot len o de xem chi tiet track."

    $grid = New-Object System.Windows.Forms.DataGridView
    $grid.Dock = 'Fill'
    $grid.AllowUserToAddRows = $false
    $grid.AllowUserToDeleteRows = $false
    $grid.AllowUserToResizeRows = $false
    $grid.RowHeadersVisible = $false
    $grid.SelectionMode = 'CellSelect'
    $grid.MultiSelect = $false
    $grid.ColumnHeadersHeightSizeMode = 'AutoSize'
    $grid.ColumnHeadersDefaultCellStyle.WrapMode = 'True'
    $grid.ColumnHeadersDefaultCellStyle.Alignment = 'MiddleCenter'
    $grid.EnableHeadersVisualStyles = $false
    $grid.BackgroundColor = [System.Drawing.Color]::White
    $grid.ShowCellToolTips = $true

    $cEp = New-Object System.Windows.Forms.DataGridViewTextBoxColumn
    $cEp.HeaderText = 'Tap'; $cEp.ReadOnly = $true; $cEp.Width = 70; $cEp.Frozen = $true
    $cEp.SortMode = 'NotSortable'
    [void]$grid.Columns.Add($cEp)
    $cNm = New-Object System.Windows.Forms.DataGridViewTextBoxColumn
    $cNm.HeaderText = 'File'; $cNm.ReadOnly = $true; $cNm.Width = 330; $cNm.Frozen = $true
    $cNm.SortMode = 'NotSortable'
    [void]$grid.Columns.Add($cNm)

    foreach ($k in $keys) {
        $col = New-Object System.Windows.Forms.DataGridViewCheckBoxColumn
        $col.Name = $k
        $col.HeaderText = $colDefs[$k]
        $col.Width = 105
        $col.SortMode = 'NotSortable'
        if ($k -like 'E|*') {
            $col.HeaderCell.Style.BackColor = [System.Drawing.Color]::FromArgb(222, 235, 255)
        }
        [void]$grid.Columns.Add($col)
    }

    foreach ($pl in $plans) {
        $ri = $grid.Rows.Add()
        $row = $grid.Rows[$ri]
        $ep = Split-EpisodeName $pl.File.BaseName
        $row.Cells[0].Value = $(if ($ep) { Format-EpRef $ep } else { '-' })
        $row.Cells[1].Value = $pl.File.Name
        $row.Cells[1].ToolTipText = $pl.File.FullName
        for ($c = 0; $c -lt $keys.Count; $c++) {
            $cell = $row.Cells[$c + 2]
            $it = @($pl.Items | Where-Object { $_.Key -eq $keys[$c] })[0]
            if ($it) {
                $cell.Tag = $it
                $cell.Value = [bool]$it.AutoKeep
                $cell.ToolTipText = $it.Tip
                $cell.Style.BackColor = $(if ($it.AutoKeep) { $cOn } else { $cOff })
            } else {
                $cell.Tag = $null
                $cell.Value = $false
                $cell.ReadOnly = $true
                $cell.Style.BackColor = $cNa
                $cell.ToolTipText = 'Tap nay khong co phu de loai nay'
            }
        }
    }

    # Luu vao bien script de cac su kien dung duoc
    $script:SPGrid = $grid
    $script:SPColors = @{ On = $cOn; Off = $cOff; Diff = $cDiff }

    $paint = {
        param($cell)
        if (-not $cell.Tag) { return }
        $v = [bool]$cell.Value
        if ($v -ne [bool]$cell.Tag.AutoKeep) { $cell.Style.BackColor = $script:SPColors.Diff }
        elseif ($v) { $cell.Style.BackColor = $script:SPColors.On }
        else { $cell.Style.BackColor = $script:SPColors.Off }
    }
    $script:SPPaint = $paint

    # Tick la ghi nhan ngay, khong can roi o
    $grid.Add_CurrentCellDirtyStateChanged({
        if ($script:SPGrid.IsCurrentCellDirty) { [void]$script:SPGrid.CommitEdit('CurrentCellChange') }
    })
    $grid.Add_CellValueChanged({
        param($s, $e)
        if ($e.RowIndex -lt 0 -or $e.ColumnIndex -lt 2) { return }
        & $script:SPPaint $script:SPGrid.Rows[$e.RowIndex].Cells[$e.ColumnIndex]
    })

    # Bam tieu de cot -> dao ca cot
    $grid.Add_ColumnHeaderMouseClick({
        param($s, $e)
        if ($e.ColumnIndex -lt 2) { return }
        $g = $script:SPGrid
        [void]$g.EndEdit()
        $anyOff = $false
        foreach ($r in $g.Rows) {
            $cl = $r.Cells[$e.ColumnIndex]
            if ($cl.Tag -and -not [bool]$cl.Value) { $anyOff = $true; break }
        }
        foreach ($r in $g.Rows) {
            $cl = $r.Cells[$e.ColumnIndex]
            if ($cl.Tag) { $cl.Value = $anyOff; & $script:SPPaint $cl }
        }
    })

    # Bam o ten tap -> dao ca dong
    $grid.Add_CellClick({
        param($s, $e)
        if ($e.RowIndex -lt 0 -or $e.ColumnIndex -gt 1) { return }
        $g = $script:SPGrid
        [void]$g.EndEdit()
        $r = $g.Rows[$e.RowIndex]
        $anyOff = $false
        for ($c = 2; $c -lt $g.Columns.Count; $c++) {
            if ($r.Cells[$c].Tag -and -not [bool]$r.Cells[$c].Value) { $anyOff = $true; break }
        }
        for ($c = 2; $c -lt $g.Columns.Count; $c++) {
            $cl = $r.Cells[$c]
            if ($cl.Tag) { $cl.Value = $anyOff; & $script:SPPaint $cl }
        }
    })

    # ---- Nut bam ----
    $pnl = New-Object System.Windows.Forms.FlowLayoutPanel
    $pnl.Dock = 'Bottom'; $pnl.Height = 46
    $pnl.FlowDirection = 'RightToLeft'
    $pnl.Padding = New-Object System.Windows.Forms.Padding(6)

    $mk = {
        param([string]$text, [int]$w)
        $b = New-Object System.Windows.Forms.Button
        $b.Text = $text; $b.Width = $w; $b.Height = 30
        return $b
    }
    $bOk     = & $mk 'OK - Bat dau mux' 150
    $bCancel = & $mk 'Huy (dung quy tac tu dong)' 190
    $bAuto   = & $mk 'Tra ve mac dinh' 130
    $bNone   = & $mk 'Bo tick tat ca' 120
    $bAll    = & $mk 'Tick tat ca' 110
    $bOk.DialogResult = [System.Windows.Forms.DialogResult]::OK
    $bCancel.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    $bOk.BackColor = [System.Drawing.Color]::FromArgb(106, 214, 130)

    $script:SPSetAll = {
        param($mode)   # 'all' | 'none' | 'auto'
        $g = $script:SPGrid
        [void]$g.EndEdit()
        foreach ($r in $g.Rows) {
            for ($c = 2; $c -lt $g.Columns.Count; $c++) {
                $cl = $r.Cells[$c]
                if (-not $cl.Tag) { continue }
                $cl.Value = $(switch ($mode) { 'all' { $true } 'none' { $false } default { [bool]$cl.Tag.AutoKeep } })
                & $script:SPPaint $cl
            }
        }
    }
    $bAll.Add_Click({  & $script:SPSetAll 'all' })
    $bNone.Add_Click({ & $script:SPSetAll 'none' })
    $bAuto.Add_Click({ & $script:SPSetAll 'auto' })

    foreach ($b in @($bOk, $bCancel, $bAuto, $bNone, $bAll)) { [void]$pnl.Controls.Add($b) }

    $form.Controls.Add($grid)
    $form.Controls.Add($lbl)
    $form.Controls.Add($pnl)
    $form.AcceptButton = $bOk
    $form.CancelButton = $bCancel

    $res = $form.ShowDialog()
    [void]$grid.EndEdit()

    if ($res -ne [System.Windows.Forms.DialogResult]::OK) { $form.Dispose(); return $null }

    $choice = @{}
    for ($ri = 0; $ri -lt $plans.Count; $ri++) {
        $row = $grid.Rows[$ri]
        $ext = @(); $int = @()
        for ($c = 2; $c -lt $grid.Columns.Count; $c++) {
            $cl = $row.Cells[$c]
            if (-not $cl.Tag -or -not [bool]$cl.Value) { continue }
            if ($cl.Tag.Kind -eq 'ext') { $ext += $cl.Tag.Code } else { $int += [int]$cl.Tag.Id }
        }
        $choice[$plans[$ri].File.FullName] = @{ Ext = $ext; Int = $int }
    }
    $form.Dispose()
    return $choice
}

# ==========================================================
#  TVDB v4  (dat ten phim bo theo TheTVDB)   - moi o v14
# ==========================================================
$script:TvdbToken       = $null
$script:TvdbLoginFailed = $false
$script:NameDb          = 'none'     # 'tmdb' | 'tvdb' | 'none' - chon luc khoi dong

# Goi API TVDB. Doc than phan hoi bang UTF-8 (TVDB khong luon ghi charset,
# PowerShell 5.1 se doc sai ten co dau / tieng Nhat neu de mac dinh).
function Invoke-TvdbApi([string]$path, [switch]$Quiet404, [switch]$NoAuth, [hashtable]$Body) {
    if (-not $NoAuth -and -not $script:TvdbToken) { if (-not (Connect-Tvdb)) { return $null } }
    $url = "https://api4.thetvdb.com/v4$path"
    for ($i = 1; $i -le [math]::Max(2, $TmdbRetry); $i++) {
        try {
            $h = @{ Accept = 'application/json' }
            if (-not $NoAuth) { $h['Authorization'] = "Bearer $($script:TvdbToken)" }
            if ($Body) {
                $r = Invoke-WebRequest -Uri $url -Method Post -Headers $h -ContentType 'application/json' `
                        -Body ($Body | ConvertTo-Json -Compress) -UseBasicParsing -TimeoutSec 25 -ErrorAction Stop
            } else {
                $r = Invoke-WebRequest -Uri $url -Headers $h -UseBasicParsing -TimeoutSec 25 -ErrorAction Stop
            }
            $ms = $r.RawContentStream; $ms.Position = 0
            $sr = New-Object System.IO.StreamReader($ms, [System.Text.Encoding]::UTF8)
            $txt = $sr.ReadToEnd(); $sr.Close()
            return ($txt | ConvertFrom-Json)
        } catch {
            $msg = $_.Exception.Message
            $code = 0
            try { $code = [int]$_.Exception.Response.StatusCode } catch {}
            if ($code -eq 404) {
                if (-not $Quiet404) { Write-Step "  [!] TVDB khong co: $path" 'Yellow' }
                return $null
            }
            # Token het han -> dang nhap lai mot lan
            if ($code -eq 401 -and -not $NoAuth -and $i -eq 1) {
                $script:TvdbToken = $null
                if (Connect-Tvdb) { continue }
                return $null
            }
            if ($code -ge 400 -and $code -lt 500) {
                Write-Step "  [!] TVDB tu choi yeu cau ($code): $msg" 'Red'
                return $null
            }
            if ($i -lt $TmdbRetry) {
                $wait = [math]::Pow(2, $i)
                Write-Step "  [!] TVDB loi mang (lan $i/$TmdbRetry), thu lai sau $wait giay..." 'Yellow'
                Start-Sleep -Seconds $wait
            } else {
                Write-Step "  [!] TVDB that bai: $msg" 'Red'
            }
        }
    }
    return $null
}

function Connect-Tvdb {
    if ($script:TvdbToken) { return $true }
    if ($script:TvdbLoginFailed -or -not $TvdbApiKey) { return $false }
    $b = @{ apikey = $TvdbApiKey }
    if ($TvdbPin) { $b['pin'] = $TvdbPin }
    $r = Invoke-TvdbApi '/login' -NoAuth -Body $b
    if ($r -and $r.data -and $r.data.token) { $script:TvdbToken = [string]$r.data.token; return $true }
    $script:TvdbLoginFailed = $true
    Write-Step "[!] Dang nhap TVDB that bai - kiem tra `$TvdbApiKey (va `$TvdbPin neu key cua ban can PIN)." 'Red'
    return $false
}

# Ten theo ngon ngu uu tien: $TvdbLang -> eng -> ten goc
function Get-TvdbLocalName($obj) {
    foreach ($lg in @($TvdbLang, 'eng')) {
        if ($obj.translations -and $obj.translations.PSObject.Properties.Name -contains $lg) {
            $v = [string]$obj.translations.$lg
            if ($v) { return $v }
        }
    }
    return [string]$obj.name
}

# Tim PHIM LE tren TVDB. Tra ve cung dang voi ket qua TMDB (title / original_title / release_date)
function Find-TvdbMovie([string]$title, $year) {
    if (-not $title) { return $null }
    $q = "/search?query=$([uri]::EscapeDataString($title))&type=movie"
    if ($year) { $q += "&year=$year" }
    $r = Invoke-TvdbApi $q
    if ((-not $r -or -not $r.data -or @($r.data).Count -eq 0) -and $year) { return (Find-TvdbMovie $title $null) }
    if (-not $r -or -not $r.data) { return $null }
    $out = @()
    foreach ($x in @($r.data)) {
        if (-not $x.tvdb_id) { continue }
        $yy = [string]$x.year
        $fa = [string]$x.first_air_time
        if (-not $yy -and $fa.Length -ge 4) { $yy = $fa.Substring(0, 4) }
        $out += [pscustomobject]@{
            id             = [int]$x.tvdb_id
            title          = (Get-TvdbLocalName $x)
            original_title = [string]$x.name
            release_date   = $(if ($yy) { "$yy-01-01" } else { '' })
        }
    }
    return $out
}

function Find-TvdbSeries([string]$title, $year) {
    if (-not $title) { return $null }
    $q = "/search?query=$([uri]::EscapeDataString($title))&type=series"
    if ($year) { $q += "&year=$year" }
    $r = Invoke-TvdbApi $q
    if ((-not $r -or -not $r.data -or @($r.data).Count -eq 0) -and $year) { return (Find-TvdbSeries $title $null) }
    if (-not $r -or -not $r.data) { return $null }
    # Dua ve cung dang voi ket qua TMDB de dung chung ham chon ung vien
    $out = @()
    foreach ($x in @($r.data)) {
        if (-not $x.tvdb_id) { continue }
        $yy = [string]$x.year
        if (-not $yy -and $x.first_air_time) { $yy = ([string]$x.first_air_time).Substring(0, 4) }
        $out += [pscustomobject]@{
            id             = [int]$x.tvdb_id
            name           = (Get-TvdbLocalName $x)
            original_name  = [string]$x.name
            first_air_date = $(if ($yy) { "$yy-01-01" } else { '' })
        }
    }
    return $out
}

# Toan bo danh sach tap cua series theo thu tu $TvdbSeasonType (co bo dem)
function Get-TvdbEpisodes([int]$id) {
    $k = "TVDBEP|$id|$TvdbSeasonType"
    if ($script:TmdbCache.ContainsKey($k)) { return $script:TmdbCache[$k] }
    $all = @()
    for ($p = 0; $p -lt 30; $p++) {
        $r = Invoke-TvdbApi "/series/$id/episodes/$($TvdbSeasonType)?page=$p" -Quiet404
        if (-not $r -or -not $r.data) { break }
        $all += @($r.data.episodes)
        if (-not $r.links -or -not $r.links.next) { break }
    }
    $script:TmdbCache[$k] = $all
    return $all
}

function Get-TvdbEpisodeName($e) {
    $have = @($e.nameTranslations)
    foreach ($lg in (@($TvdbLang, 'eng') | Select-Object -Unique)) {
        if ($have.Count -gt 0 -and $have -notcontains $lg) { continue }
        $t = Invoke-TvdbApi "/episodes/$($e.id)/translations/$lg" -Quiet404
        if ($t -and $t.data -and $t.data.name) { return [string]$t.data.name }
    }
    return $null
}

# Tim tap: khop truc tiep SxxExx, khong co thi coi la so tuyet doi (TVDB co san absoluteNumber)
function Resolve-TvdbEpisode([int]$id, [int]$se, [int]$ep, [bool]$wantTitle) {
    $out = [pscustomobject]@{ Season = $se; Episode = $ep; Title = $null; Mapped = $false; Found = $false }
    $list = @(Get-TvdbEpisodes $id)
    if ($list.Count -eq 0) { return $out }

    $hit = $null
    if ($se -ge 1) { $hit = @($list | Where-Object { [int]$_.seasonNumber -eq $se -and [int]$_.number -eq $ep })[0] }
    if (-not $hit) {
        $hit = @($list | Where-Object { [int]$_.seasonNumber -ge 1 -and [int]$_.absoluteNumber -eq $ep })[0]
        if ($hit) { $out.Mapped = $true }
    }
    if (-not $hit) { return $out }

    $out.Found   = $true
    $out.Season  = [int]$hit.seasonNumber
    $out.Episode = [int]$hit.number
    if ($wantTitle) { $out.Title = Get-TvdbEpisodeName $hit }
    return $out
}

# Nhieu series trung ten, file khong ghi nam -> chon series thuc su co tap nay
function Select-TvdbShow($res, $ep) {
    $wantN = Get-NormTitle $ep.Series
    $exact = @($res | Where-Object {
        (Get-NormTitle $_.name) -eq $wantN -or (Get-NormTitle $_.original_name) -eq $wantN })
    if ($ep.Year -or $exact.Count -le 1) {
        return (Select-TmdbCandidate $res $ep.Series $ep.Year 'name' 'original_name' 'first_air_date')
    }
    Write-Step "  [?] Co $($exact.Count) series cung ten '$($ep.Series)' - kiem tra series nao co $(Format-EpRef $ep)..." 'Yellow'
    foreach ($c in ($exact | Select-Object -First 6)) {
        $n  = @(Get-TvdbEpisodes $c.id).Count
        $rt = Resolve-TvdbEpisode $c.id $ep.Season $ep.EpStart $false
        $yy = if ($c.first_air_date) { $c.first_air_date.Substring(0, 4) } else { '????' }
        Write-Step ("      - {0} ({1})  tvdbid-{2}: {3} tap{4}" -f $c.name, $yy, $c.id, $n, $(if ($rt.Found) { '  <- co tap nay' } else { '' })) 'DarkGray'
        if ($rt.Found) { Write-Step "  [i] -> Chon: $($c.name) ($yy)" 'Green'; return $c }
    }
    Write-Step "  [!] Khong series nao co $(Format-EpRef $ep)." 'Yellow'
    return (Select-TmdbCandidate $res $ep.Series $ep.Year 'name' 'original_name' 'first_air_date')
}

# Giong Get-TmdbTvName nhung lay du lieu tu TVDB. Thu muc dung [tvdbid-xxxx].
function Get-TvdbTvName([string]$origBase, [string]$rootDir) {
    $ep = Split-EpisodeName $origBase
    if (-not $ep -or -not $ep.Series) { return $null }

    $key = "TVDB|$($ep.Series)|$($ep.Year)"
    if ($script:TmdbCache.ContainsKey($key)) { $show = $script:TmdbCache[$key] }
    else {
        Write-Step "  [~] TVDB dang tra: '$($ep.Series)'$(if($ep.Year){" ($($ep.Year))"})" 'DarkCyan'
        $res = @(Find-TvdbSeries $ep.Series $ep.Year)
        if ($res.Count -eq 0) {
            Write-Step "  [!] TVDB khong tim thay series." 'Yellow'
            $script:TmdbCache[$key] = $null; return $null
        }
        $pick = Select-TvdbShow $res $ep
        if (-not $pick) { $script:TmdbCache[$key] = $null; return $null }
        $yr = if ($pick.first_air_date) { $pick.first_air_date.Substring(0, 4) } else { $ep.Year }
        $nm = (Get-SafeFileName $pick.name) -replace '[\[\]\(\)\{\}]', ' '
        $nm = ($nm -replace '\s+', ' ').Trim()
        $folder = if ($yr) { "$nm ($yr) [tvdbid-$($pick.id)]" } else { "$nm [tvdbid-$($pick.id)]" }
        $show = [pscustomobject]@{ Id = $pick.id; Name = $nm; Year = $yr; Folder = $folder }
        $script:TmdbCache[$key] = $show
    }
    if (-not $show) { return $null }

    $seNum = $ep.Season; $epNum = $ep.EpStart; $epTitle = $null
    if (-not $ep.EpEnd) {
        $rt = Resolve-TvdbEpisode $show.Id $ep.Season $ep.EpStart $TvFetchEpisodeTitle
        if ($rt.Mapped) {
            Write-Step ("  [i] So tap tuyet doi: {0} -> TVDB S{1:D2}E{2:D2}" -f `
                        (Format-EpRef $ep), $rt.Season, $rt.Episode) 'Cyan'
            if ($TvAbsoluteToSeason) { $seNum = $rt.Season; $epNum = $rt.Episode }
        } elseif (-not $rt.Found) {
            Write-Step "  [!] TVDB khong co $(Format-EpRef $ep) -> giu ma tap goc, khong co ten tap." 'Yellow'
        }
        $epTitle = $rt.Title
    }

    if ($seNum -le 0) { $seNum = 1 }
    $code = "S{0:D2}E{1:D2}" -f $seNum, $epNum
    if ($ep.EpEnd) { $code += "-E{0:D2}" -f $ep.EpEnd }
    if ($epTitle) {
        $epTitle = (Get-SafeFileName $epTitle) -replace '[\[\]\{\}]', ' '
        $epTitle = ($epTitle -replace '\s+', ' ').Trim()
    }
    $prefix = if ($show.Year) { "$($show.Name) ($($show.Year))" } else { $show.Name }
    $base   = if ($epTitle) { "$prefix - $code - $epTitle" } else { "$prefix - $code" }
    $base   = Get-SafeFileName $base

    $dir = Join-Path $rootDir $show.Folder
    if ($TvSeasonFolder) { $dir = Join-Path $dir ("Season {0:D2}" -f $seNum) }
    return [pscustomobject]@{ Base = $base; Dir = $dir }
}

# Chon co so du lieu dat ten luc khoi dong. LUON hoi, khong co lua chon mac dinh.
function Select-NameDatabase($files) {
    $all = @($files)
    if ($all.Count -eq 0) { return 'none' }
    $nEp = @($all | Where-Object { $null -ne (Split-EpisodeName $_.BaseName) }).Count
    $nMv = $all.Count - $nEp

    # Thu ca hai key TRUOC khi hoi, de menu bao dung tinh trang tung database
    $stTmdb = 'chua dien $TmdbApiKey'
    if ($TmdbApiKey) {
        [void](Invoke-TmdbApi ("https://api.themoviedb.org/3/configuration?api_key=$TmdbApiKey") -Quiet404)
        $stTmdb = if ($script:TmdbAuthFailed) { 'KEY BI TU CHOI' } else { 'san sang' }
    }
    $stTvdb = 'chua dien $TvdbApiKey'
    if ($TvdbApiKey) { $stTvdb = if (Connect-Tvdb) { 'san sang' } else { 'DANG NHAP LOI' } }
    $okTmdb = ($stTmdb -eq 'san sang'); $okTvdb = ($stTvdb -eq 'san sang')

    Write-Step "`nDat ten theo co so du lieu nao? ($nMv phim le, $nEp tap phim bo)" 'Cyan'
    Write-Step ("   1. TMDB  (themoviedb.org)  -> [tmdbid-xxx]   ({0})" -f $stTmdb) $(if ($okTmdb) { 'White' } else { 'DarkGray' })
    Write-Step ("   2. TVDB  (thetvdb.com)     -> [tvdbid-xxx]   ({0})" -f $stTvdb) $(if ($okTvdb) { 'White' } else { 'DarkGray' })
    Write-Step  "   0. Khong doi ten - giu ten file goc" 'White'

    while ($true) {
        $a = Read-Host "Nhap 1, 2 hoac 0"
        if ($null -eq $a) {
            Write-Step "[!] Khong doc duoc ban phim -> giu ten file goc." 'Yellow'
            return 'none'
        }
        switch (([string]$a).Trim()) {
            '1' {
                if ($okTmdb) { Write-Step "[i] Dat ten theo: TMDB" 'Green'; return 'tmdb' }
                Write-Step "    TMDB chua dung duoc ($stTmdb). Sua `$TmdbApiKey, hoac chon so khac." 'Yellow'
            }
            '2' {
                if ($okTvdb) { Write-Step "[i] Dat ten theo: TVDB" 'Green'; return 'tvdb' }
                Write-Step "    TVDB chua dung duoc ($stTvdb). Sua `$TvdbApiKey / `$TvdbPin, hoac chon so khac." 'Yellow'
            }
            '0' { Write-Step "[i] Giu nguyen ten file goc." 'Green'; return 'none' }
            default { Write-Step "    Chua chon. Go 1, 2 hoac 0 roi nhan Enter." 'Yellow' }
        }
    }
}

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


# ==========================================================
#  GHEP AUDIO TU FILE PHIM KHAC  (moi o v16)
# ==========================================================
# Vd: lay audio tieng Sec tu mot ban phim khac dat trong thu muc 'audio_nguon'.
# Cach lam:
#   1. Ghep tap voi tap (theo SxxExx / so tuyet doi, phim le theo ten).
#   2. Do do lech + chenh FPS bang cach so PHU DE cua hai ban phim voi nhau
#      (cung thoai -> phu de lech bao nhieu thi audio lech bay nhieu).
#   3. Cung FPS  -> mkvmerge ghep thang, khong encode lai, chi them --sync.
#      Khac FPS  -> ffmpeg doi toc do (asetrate) roi moi ghep.
$script:DonorTmp   = $null
$script:DonorFiles = $null
$script:SubTimeCache = @{}
$FpsSampleRef  = 400
$FpsSampleTgt  = 100
$PgsTimingText = '*'

function Get-DonorTmpDir {
    if (-not $script:DonorTmp) {
        $script:DonorTmp = Join-Path $env:TEMP ("muxtool_" + [guid]::NewGuid().ToString('N').Substring(0, 8))
        New-Item -ItemType Directory -Path $script:DonorTmp -Force | Out-Null
    }
    return $script:DonorTmp
}

# So thap phan luon dung dau CHAM (Windows vung VN/Sec in dau phay -> ffmpeg hieu sai)
function Format-Inv([double]$x, [string]$fmt = '0.######') {
    return $x.ToString($fmt, [System.Globalization.CultureInfo]::InvariantCulture)
}

function Get-DonorFiles {
    if ($null -ne $script:DonorFiles) { return $script:DonorFiles }
    $script:DonorFiles = @()
    $dir = Join-Path $targetDir $DonorFolder
    if (Test-Path -LiteralPath $dir) {
        $script:DonorFiles = @(Get-ChildItem -LiteralPath $dir -File -Recurse -ErrorAction SilentlyContinue |
                               Where-Object { $_.Extension -match '(?i)^\.(mkv|mka|mp4|m4v|ts)$' })
    }
    return $script:DonorFiles
}

# Tim file nguon ung voi tap/phim dang xu ly
function Find-DonorFile($f) {
    $pool = @(Get-DonorFiles)
    if ($pool.Count -eq 0) { return $null }
    $ep = Split-EpisodeName $f.BaseName
    if ($ep) {
        foreach ($d in $pool) {
            $de = Split-EpisodeName $d.BaseName
            if (-not $de -or $de.EpStart -ne $ep.EpStart) { continue }
            if ($ep.Absolute -eq $de.Absolute) {
                if ($ep.Absolute -or $de.Season -eq $ep.Season) { return $d }
            } else {
                # Mot ben S16E643, mot ben "- 643": chi tin khi so tap du lon (khong the nham voi tap le cua season)
                $nonAbs = if ($ep.Absolute) { $de } else { $ep }
                if ($nonAbs.Season -eq 1 -or $ep.EpStart -ge 100) { return $d }
            }
        }
        return $null
    }
    # Phim le: khop theo ten (+ nam neu ca hai co)
    $me = Split-ReleaseName $f.BaseName
    foreach ($d in $pool) {
        if ($null -ne (Split-EpisodeName $d.BaseName)) { continue }
        $dn = Split-ReleaseName $d.BaseName
        if ((Get-NormTitle $dn.Title) -ne (Get-NormTitle $me.Title)) { continue }
        if ($me.Year -and $dn.Year -and $me.Year -ne $dn.Year) { continue }
        return $d
    }
    if ($pool.Count -eq 1 -and $null -eq (Split-EpisodeName $pool[0].BaseName)) { return $pool[0] }
    return $null
}

# Cac track phu de dung lam moc thoi gian (bo Forced vi qua it dong)
function Get-RefSubTracks($json) {
    $out = @()
    foreach ($t in @($json.tracks | Where-Object { $_.type -eq 'subtitles' })) {
        $p = $t.properties; $cid = [string]$p.codec_id
        $ext = $null
        if     ($cid -match 'S_TEXT/UTF8')  { $ext = '.srt' }
        elseif ($cid -match 'S_TEXT/ASS')   { $ext = '.ass' }
        elseif ($cid -match 'S_TEXT/SSA')   { $ext = '.ssa' }
        elseif ($cid -match 'S_HDMV/PGS')   { $ext = '.sup' }
        else { continue }
        $nm  = [string]$p.track_name
        $frc = ($nm -match '(?i)forced') -or ($p.forced_track -eq $true) -or ($p.flag_forced -eq $true)
        if ($frc) { continue }
        $sdh = ($nm -match '(?i)\bSDH\b|hearing') -or ($p.flag_hearing_impaired -eq $true)
        $out += [pscustomobject]@{ Id = [int]$t.id; Code = (Get-LangCode $p.language $p.language_ietf)
                                   Ext = $ext; Rank = $(if ($ext -eq '.sup') { 2 } elseif ($sdh) { 1 } else { 0 }) }
    }
    return @($out | Sort-Object Rank)
}

# Trich moc thoi gian cua mot track phu de (co bo dem)
function Get-TrackCueTimes([string]$file, $trk) {
    $k = "$file|$($trk.Id)"
    if ($script:SubTimeCache.ContainsKey($k)) { return $script:SubTimeCache[$k] }
    $script:SubTimeCache[$k] = $null
    if (-not $script:mkvextract) { return $null }
    $tmp = Get-DonorTmpDir
    $raw = Join-Path $tmp ("s_" + [guid]::NewGuid().ToString('N').Substring(0, 8) + $trk.Ext)
    & $script:mkvextract tracks "$file" "$($trk.Id):$raw" 2>&1 | Out-Null
    if (-not (Test-Path -LiteralPath $raw)) { return $null }
    $srt = $raw
    if ($trk.Ext -eq '.sup') {
        $srt = [System.IO.Path]::ChangeExtension($raw, '.srt')
        if ((Convert-PgsToTimingSrt $raw $srt) -eq 0) { return $null }
    }
    $t = Get-CueTimes $srt
    $script:SubTimeCache[$k] = $t
    return $t
}

function Get-VideoFpsOf($json) {
    $v = @($json.tracks | Where-Object { $_.type -eq 'video' })[0]
    if (-not $v -or -not $v.properties.default_duration) { return 0 }
    $dd = ConvertTo-Double $v.properties.default_duration
    if ($dd -le 0) { return 0 }
    return 1000000000.0 / $dd
}

function Get-DurationOf($json) {
    if ($json.container -and $json.container.properties -and $json.container.properties.duration) {
        return (ConvertTo-Double $json.container.properties.duration) / 1000000000.0
    }
    return 0
}

# Do quan he thoi gian: phim_cua_ban = nguon * Scale + Offset
function Measure-DonorTiming($f, $json, $d, $dj) {
    $mine  = @(Get-RefSubTracks $json)
    $donor = @(Get-RefSubTracks $dj)

    # Sub ngoai canh phim cua ban cung la moc tot (da dong bo dung voi phim)
    $extRefs = @()
    foreach ($x in @(Get-ChildItem -LiteralPath $f.DirectoryName -File -ErrorAction SilentlyContinue | Where-Object {
            $_.Extension -match '(?i)^\.(srt|ass|ssa)$' -and
            $_.BaseName.StartsWith($f.BaseName + '.', [System.StringComparison]::OrdinalIgnoreCase) })) {
        $suffix = $x.BaseName.Substring($f.BaseName.Length)
        if ($suffix -match '(?i)forced') { continue }
        $code = ''
        if     ($suffix -match '(?i)(^|[._\- ])(en|eng|english)([._\- ]|$)')         { $code = 'en' }
        elseif ($suffix -match '(?i)(^|[._\- ])(cs|cz|cze|ces|czech)([._\- ]|$)')    { $code = 'cs' }
        elseif ($suffix -match '(?i)(^|[._\- ])(vi|vie|viet|vietnamese)([._\- ]|$)') { $code = 'vi' }
        if ($code) { $extRefs += [pscustomobject]@{ Code = $code; Path = $x.FullName } }
    }

    # Ngon ngu chung, uu tien tieng Anh
    $langs = @('en') + @($donor | ForEach-Object { $_.Code }) | Where-Object { $_ -and $_ -ne 'und' } | Select-Object -Unique
    foreach ($lg in $langs) {
        $dt = @($donor | Where-Object { $_.Code -eq $lg })[0]
        if (-not $dt) { continue }
        $mineTimes = $null; $src = ''
        $mt = @($mine | Where-Object { $_.Code -eq $lg })[0]
        if ($mt) { $mineTimes = Get-TrackCueTimes $f.FullName $mt; $src = "sub $(Get-LangName $lg) trong MKV" }
        if (-not $mineTimes) {
            $er = @($extRefs | Where-Object { $_.Code -eq $lg })[0]
            if ($er) { $mineTimes = Get-CueTimes $er.Path; $src = "sub $(Get-LangName $lg) ngoai" }
        }
        if (-not $mineTimes) { continue }
        $donorTimes = Get-TrackCueTimes $d.FullName $dt
        if (-not $donorTimes) { continue }
        $rel = Get-TimingRelation $mineTimes $donorTimes
        if ($rel -and $rel.Votes -ge $DonorMinVotes) {
            return [pscustomobject]@{ Scale = $rel.Scale; Offset = $rel.Offset; Votes = $rel.Votes
                                      Name = $rel.Name; How = "do bang $src ($($rel.Votes) moc khop)"; Sure = $true }
        }
    }

    # Khong do duoc: doan theo FPS + thoi luong
    $fm = Get-VideoFpsOf $json; $fd = Get-VideoFpsOf $dj
    $scale = 1.0
    if ($fm -gt 0 -and $fd -gt 0 -and [math]::Abs($fm / $fd - 1.0) -gt 0.001) { $scale = $fd / $fm }
    $dm = Get-DurationOf $json; $dd = (Get-DurationOf $dj) * $scale
    $close = ($dm -gt 0 -and $dd -gt 0 -and [math]::Abs($dm - $dd) -le $DonorMaxDurationDiff)
    return [pscustomobject]@{ Scale = $scale; Offset = 0.0; Votes = 0; Name = ''
                              How = $(if ($close) { "UOC TINH: khong co phu de chung, thoi luong khop (chenh $(Format-Inv ([math]::Abs($dm - $dd)) '0.0')s)" }
                                      else { "khong do duoc (thoi luong chenh $(Format-Inv ([math]::Abs($dm - $dd)) '0.0')s)" })
                              Sure = $false; Close = $close }
}

# Lap ke hoach ghep audio cho MOT file. Tra ve danh sach track can them.
function Get-DonorPlan($f, $json, [string[]]$haveLangs) {
    if (-not $UseDonorAudio) { return @() }
    $want = @($DonorLangs | Where-Object { -not ($DonorSkipIfPresent -and $haveLangs -contains $_) })
    if ($want.Count -eq 0) {
        if ((Get-DonorFiles).Count -gt 0) { Write-Step "  [=] Ghep audio: file da co $(($DonorLangs | ForEach-Object { Get-LangName $_ }) -join ', ') -> bo qua." 'DarkGray' }
        return @()
    }
    $d = Find-DonorFile $f
    if (-not $d) {
        if ((Get-DonorFiles).Count -gt 0) { Write-Step "  [-] Ghep audio: khong co file nguon khop voi file nay trong '$DonorFolder'." 'DarkGray' }
        return @()
    }

    $dj = $null
    try {
        $raw = & $mkvmerge -J "$($d.FullName)" 2>&1
        if ($LASTEXITCODE -le 1) { $dj = ($raw -join "`n") | ConvertFrom-Json }
    } catch {}
    if (-not $dj) { Write-Step "  [!] Ghep audio: khong doc duoc $($d.Name)" 'Red'; return @() }

    $dAudio = @($dj.tracks | Where-Object { $_.type -eq 'audio' })
    $picks = @()
    foreach ($lg in $want) {
        $c = @($dAudio | Where-Object {
                (Get-LangCode $_.properties.language $_.properties.language_ietf) -eq $lg -and
                -not (Test-CommentaryTrack $_.properties) -and -not (Test-DescriptiveTrack $_.properties) })
        if ($c.Count -eq 0) { continue }
        # Nhieu track cung ngon ngu -> lay track nhieu kenh / bitrate cao nhat
        $best = $c | Sort-Object @{Expression={ [int]$_.properties.audio_channels }; Descending=$true},
                                 @{Expression={ Get-AudioBitrate $_ @{} }; Descending=$true} | Select-Object -First 1
        $picks += [pscustomobject]@{ Track = $best; Code = $lg }
    }
    if ($picks.Count -eq 0) {
        Write-Step "  [-] Ghep audio: $($d.Name) khong co audio $(($want | ForEach-Object { Get-LangName $_ }) -join '/')." 'Yellow'
        return @()
    }

    Write-Step "  [~] Ghep audio tu: $($d.Name)" 'Cyan'
    $tm = Measure-DonorTiming $f $json $d $dj

    # Chinh tay ghi de: $DonorOffsetMs = @{ 'S01E03' = 1200; 'Ten file' = -500 }
    $epRef = $null; $epInfo = Split-EpisodeName $f.BaseName
    if ($epInfo) { $epRef = Format-EpRef $epInfo }
    $manual = $null
    foreach ($k in @($DonorOffsetMs.Keys)) {
        if ($k -eq $f.BaseName -or ($epRef -and $k -eq $epRef) -or ($epInfo -and $k -eq [string]$epInfo.EpStart)) { $manual = [double]$DonorOffsetMs[$k]; break }
    }
    if ($null -ne $manual) {
        $tm.Offset = $manual / 1000.0; $tm.Sure = $true; $tm.How = "chinh tay trong `$DonorOffsetMs"
    }

    if (-not $tm.Sure -and -not ($tm.Close -and $DonorAcceptDurationMatch)) {
        Write-Step "      [!] $($tm.How) -> KHONG ghep de tranh lech tieng." 'Yellow'
        Write-Step "          Them phu de (vd tieng Anh) cho ca hai ban, hoac dat do lech tay trong `$DonorOffsetMs." 'DarkGray'
        return @()
    }

    $offMs  = [int][math]::Round($tm.Offset * 1000)
    $stretch = [math]::Abs($tm.Scale - 1.0) -gt 0.0002
    $sg = if ($offMs -ge 0) { '+' } else { '' }
    Write-Step ("      Lech: {0}{1} ms | {2}" -f $sg, $offMs, $tm.How) $(if ($tm.Sure) { 'DarkCyan' } else { 'Yellow' })
    if ($stretch) { Write-Step "      Khac FPS: $($tm.Name) -> se doi toc do audio (encode lai)" 'Yellow' }

    $plans = @()
    foreach ($pk in $picks) {
        $t = $pk.Track; $p = $t.properties
        $label = ((Get-LangName $pk.Code), (Get-CodecName $p), (Get-ChannelName $p.audio_channels)) -join ' '
        $plan = [pscustomobject]@{ Path = $d.FullName; Tid = [int]$t.id; Code = $pk.Code
                                   Name = ($label -replace '\s+', ' ').Trim(); OffsetMs = $offMs; Temp = $null }

        if ($stretch) {
            if (-not $script:ffmpeg) {
                Write-Step "      [!] Can ffmpeg.exe de doi toc do audio nhung khong tim thay -> bo qua $(Get-LangName $pk.Code)." 'Red'
                continue
            }
            if ($DryRun) {
                Write-Step "      [DRY-RUN] Se encode lai $(Get-LangName $pk.Code) bang ffmpeg." 'Magenta'
            } else {
                $aIdx = 0
                for ($i = 0; $i -lt $dAudio.Count; $i++) { if ([int]$dAudio[$i].id -eq [int]$t.id) { $aIdx = $i; break } }
                $sr = [int]$p.audio_sampling_frequency; if ($sr -le 0) { $sr = 48000 }
                $ch = [int]$p.audio_channels; if ($ch -le 0) { $ch = 2 }
                $outCh = [math]::Min($ch, 6)
                $br = if ($outCh -ge 6) { '640k' } else { '256k' }
                # asetrate chi nhan so nguyen -> phan le con lai bu bang atempo (chinh xac tuyet doi)
                $nRate = [int][math]::Round($sr / $tm.Scale)
                $flt = "asetrate=$nRate,aresample=$sr"
                $fix = $sr / ($nRate * $tm.Scale)
                if ([math]::Abs($fix - 1.0) -gt 0.0000001) { $flt += ",atempo=$(Format-Inv $fix '0.##########')" }
                $mka = Join-Path (Get-DonorTmpDir) ("a_" + [guid]::NewGuid().ToString('N').Substring(0, 8) + ".mka")
                Write-Step "      [~] ffmpeg dang doi toc do $(Get-LangName $pk.Code) ($flt)..." 'DarkCyan'
                & $script:ffmpeg -y -v error -i "$($d.FullName)" -map "0:a:$aIdx" -vn -sn -dn `
                    -af $flt -ac $outCh -c:a eac3 -b:a $br "$mka" 2>&1 | Out-Null
                if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $mka)) {
                    Write-Step "      [!] ffmpeg that bai -> bo qua $(Get-LangName $pk.Code)." 'Red'
                    continue
                }
                $plan.Path = $mka; $plan.Tid = 0; $plan.Temp = $mka
                $plan.Name = ((Get-LangName $pk.Code), 'E-AC-3', (Get-ChannelName $outCh)) -join ' '
            }
        }
        Write-Step "  [+] Audio ghep: $($plan.Name)  (lech $sg$offMs ms)" 'Magenta'
        $plans += $plan
    }
    return $plans
}

function Remove-DonorTemp {
    if ($script:DonorTmp -and (Test-Path -LiteralPath $script:DonorTmp)) {
        Remove-Item -LiteralPath $script:DonorTmp -Recurse -Force -ErrorAction SilentlyContinue
    }
    $script:DonorTmp = $null
}

[void](Initialize-Console)
Initialize-BoxChars
Set-ConsoleSize $ConsoleCols $ConsoleRows $ConsoleScrollback

# ==========================================================
#  1. TIM CONG CU
# ==========================================================
# Desktop THAT SU (neu OneDrive dang backup Desktop thi duong dan la
# C:\Users\<ten>\OneDrive\Desktop chu khong phai C:\Users\<ten>\Desktop)
$desktopReal = $null
try { $desktopReal = [Environment]::GetFolderPath('Desktop') } catch {}

$mkvFolders = @(
    $UserMkvToolNix,                                        # 1. duong dan ban khai bao o CAU HINH
    (Join-Safe $desktopReal 'mkvtoolnix'),                  # 2. Desktop that (chong OneDrive)
    (Join-Safe $env:USERPROFILE 'Desktop\mkvtoolnix'),      # 3. Desktop kieu co dien
    (Join-Safe $targetDir 'mkvtoolnix'),                    # 4. thu muc con canh video
    $targetDir,                                             # 5. ngay canh file video
    (Join-Safe $env:ProgramFiles 'MKVToolNix'),             # 6. ban cai dat 64-bit
    (Join-Safe ${env:ProgramFiles(x86)} 'MKVToolNix'),      # 7. ban cai dat 32-bit
    (Join-Safe $env:LOCALAPPDATA 'Programs\MKVToolNix')     # 8. cai cho rieng user
)
$mkvPaths = @($mkvFolders | Where-Object { $_ } | ForEach-Object { Join-Path $_ 'mkvmerge.exe' })
$mkvmerge = Find-Exe 'mkvmerge.exe' $mkvPaths
if (-not $mkvmerge) {
    try {
        $rk = Get-ItemProperty 'HKLM:\SOFTWARE\*\MKVToolNix*' -ErrorAction SilentlyContinue
        foreach ($r in $rk) {
            if ($r.InstallLocation) {
                $p = Join-Path $r.InstallLocation 'mkvmerge.exe'
                if (Test-Path -LiteralPath $p) { $mkvmerge = $p; break }
            }
        }
    } catch {}
}
if (-not $mkvmerge) {
    Write-Step "[LOI] Khong tim thay mkvmerge.exe. Da do cac vi tri sau:" 'Red'
    $mkvPaths | ForEach-Object { Write-Step "      $_" 'DarkGray' }
    Write-Step "      -> Sua bien `$UserMkvToolNix o phan CAU HINH dau script." 'Yellow'
    return
}

$ffprobe = Find-Exe 'ffprobe.exe' @(
    (@($targetDir, $UserMkvToolNix, $desktopReal) + $ExtraToolDirs +
     @((Join-Safe $desktopReal 'alass'), (Join-Safe $targetDir 'alass'))) |
        Where-Object { $_ } | ForEach-Object { Join-Path $_ 'ffprobe.exe' }
)

# Cong cu cho phan ghep audio: mkvextract (trich phu de lam moc), ffmpeg (doi toc do khi khac FPS)
$script:mkvextract = $null
$__mx = Join-Path (Split-Path $mkvmerge -Parent) 'mkvextract.exe'
if (Test-Path -LiteralPath $__mx) { $script:mkvextract = $__mx }
$script:ffmpeg = Find-Exe 'ffmpeg.exe' @(
    (@($(if ($ffprobe) { Split-Path $ffprobe -Parent }), $targetDir, $UserMkvToolNix, $desktopReal) + $ExtraToolDirs +
     @((Join-Safe $desktopReal 'alass'), (Join-Safe $targetDir 'alass'))) |
        Where-Object { $_ } | ForEach-Object { Join-Path $_ 'ffmpeg.exe' }
)

# Doi ten option theo phien ban mkvmerge (v74+ dat lai ten cac flag)
$script:mkvVer = 0
$mkvOk = $false
try {
    $vTxt = (& $mkvmerge --version 2>&1) -join ' '
    if ($LASTEXITCODE -eq 0 -and $vTxt -match '(?i)mkvmerge') { $mkvOk = $true }
    if ($vTxt -match 'v(\d+)\.') { $script:mkvVer = [int]$Matches[1] }
} catch {}
if (-not $mkvOk) {
    Write-Step "[LOI] Tim thay file nhung KHONG chay duoc: $mkvmerge" 'Red'
    Write-Step "      Thuong do ban giai nen thieu file DLL di kem, hoac Windows dang" 'Yellow'
    Write-Step "      chan file tai ve tu Internet (chuot phai > Properties > Unblock)." 'Yellow'
    return
}
if ($mkvVer -ge 74) {
    $F_DEF = '--default-track-flag'; $F_FOR = '--forced-display-flag'
} else {
    $F_DEF = '--default-track';      $F_FOR = '--forced-track'
}

Write-Step "mkvmerge : $mkvmerge  (v$mkvVer)" 'DarkGray'
if ($ffprobe) { Write-Step "ffprobe  : $ffprobe (do bitrate that)" 'DarkGray' }
else {
    Write-Step "ffprobe  : KHONG TIM THAY" 'Yellow'
    Write-Step "           -> Bitrate se chi la UOC LUONG theo codec/so kenh, khong phai so do that." 'Yellow'
    Write-Step "           -> Chep ffprobe.exe (co san trong thu muc alass) vao: $UserMkvToolNix" 'DarkYellow'
}

# ==========================================================
#  2. QUET FILE
# ==========================================================
$outDir = Join-Path $targetDir 'Output'
if (-not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

$gciArgs = @{ LiteralPath = $targetDir; File = $true }
if ($ScanSubFolders) { $gciArgs['Recurse'] = $true }
$outDirFull = (Resolve-Path -LiteralPath $outDir).Path.TrimEnd('\') + '\'

$files = @(Get-ChildItem @gciArgs |
           Where-Object { $_.Extension -match '(?i)^\.(mkv|mp4|m4v|avi|ts)$' } |
           Where-Object { -not $_.FullName.StartsWith($outDirFull, 'OrdinalIgnoreCase') } |
           Where-Object { -not ($UseDonorAudio -and $_.FullName.StartsWith((Join-Path $targetDir $DonorFolder) + '\', 'OrdinalIgnoreCase')) } |
           Sort-Object FullName)
if ($UseDonorAudio -and (Get-DonorFiles).Count -gt 0) {
    Write-Step "Ghep audio: $((Get-DonorFiles).Count) file nguon trong '$DonorFolder' -> lay $(($DonorLangs | ForEach-Object { Get-LangName $_ }) -join ', ')" 'Cyan'
    if (-not $script:mkvextract) { Write-Step "  [!] Thieu mkvextract.exe -> khong do duoc do lech bang phu de." 'Yellow' }
    if (-not $script:ffmpeg)     { Write-Step "  [!] Thieu ffmpeg.exe -> file nguon khac FPS se bi bo qua." 'Yellow' }
}

if ($files.Count -eq 0) {
    Write-Step "[!] Khong tim thay file video nao trong: $targetDir" 'Red'
    return
}
$scope = if ($ScanSubFolders) { 'ke ca thu muc con' } else { 'chi thu muc hien tai' }
Write-Step "`nTim thay $($files.Count) file ($scope)." 'White'
$script:NameDb = Select-NameDatabase $files
Write-Step "Bat dau xu ly...`n" 'White'

$okCount = 0; $skipCount = 0; $failCount = 0

# ==========================================================
#  2b. DOC TRUOC CAC TAP + BANG CHON PHU DE
# ==========================================================
$script:SubChoice = $null
$script:JsonCache = @{}
if ($UseSubPicker -and $files.Count -ge $SubPickerMinFiles) {
    $plans = @(); $n = 0
    foreach ($pf in $files) {
        $n++
        Write-Progress -Activity 'Doc truoc phu de cac tap' -Status "$n/$($files.Count)  $($pf.Name)" `
                       -PercentComplete ([int](100 * $n / $files.Count))
        $pj = $null
        try {
            $praw = & $mkvmerge -J "$($pf.FullName)" 2>&1
            if ($LASTEXITCODE -le 1) { $pj = ($praw -join "`n") | ConvertFrom-Json }
        } catch {}
        if (-not $pj) { continue }
        $script:JsonCache[$pf.FullName] = $pj
        $items = @(Get-SubAutoPlan $pf $pj)
        $plans += [pscustomobject]@{ File = $pf; Items = $items }
    }
    Write-Progress -Activity 'Doc truoc phu de cac tap' -Completed

    $withSubs = @($plans | Where-Object { $_.Items.Count -gt 0 })
    if ($withSubs.Count -eq 0) {
        Write-Step "[i] Khong tap nao co phu de -> bo qua bang chon." 'DarkGray'
    } else {
        Write-Step "[i] Mo bang chon phu de ($($plans.Count) tap)... (cua so co the nam sau cua so nay)" 'Cyan'
        try { $script:SubChoice = Show-SubPickerGrid $plans }
        catch {
            Write-Step "[!] Khong mo duoc bang chon: $($_.Exception.Message)" 'Yellow'
            $script:SubChoice = $null
        }
        if ($script:SubChoice) { Write-Step "[i] Da nhan lua chon phu de cho $($script:SubChoice.Count) tap." 'Green' }
        else { Write-Step "[i] Khong dung bang chon -> ap dung quy tac tu dong nhu cu." 'DarkGray' }
    }
    Write-Host ''
}

foreach ($f in $files) {
    $file     = $f.FullName
    $baseName = $f.BaseName
    $outName  = Join-Path $outDir "$baseName.mkv"

    $relName = $f.FullName
    if ($relName.StartsWith($targetDir, 'OrdinalIgnoreCase')) {
        $relName = $relName.Substring($targetDir.Length).TrimStart('\')
    }
    Write-Step ("=" * 62) 'DarkCyan'
    Write-Step "=== $relName" 'Cyan'

    # --- Dat ten chuan theo TMDB (phim le hoac phim bo; that bai thi lui ve ten goc) ---
    $naming  = Resolve-OutputName $baseName $outDir
    $outBase = $naming.Base
    $outName = $naming.Path

    if ($SkipIfOutputExists) {
        $already = $null
        if (Test-Path -LiteralPath $outName) { $already = $outName }
        else {
            # Lan truoc TMDB co the that bai -> file da mux nam duoi TEN GOC
            $legacy = Join-Path $outDir "$baseName.mkv"
            if (Test-Path -LiteralPath $legacy) { $already = $legacy }
            # Hoac da mux ra voi the codec: "<ten> - 2160p DV HEVC ....mkv"
            if (-not $already -and $AddCodecTag) {
                $dirOf = Split-Path $outName -Parent
                if (Test-Path -LiteralPath $dirOf) {
                    $hit = @(Get-ChildItem -LiteralPath $dirOf -File -Filter '*.mkv' -ErrorAction SilentlyContinue |
                             Where-Object { $_.BaseName.StartsWith("$outBase - ", 'OrdinalIgnoreCase') })
                    if ($hit.Count -gt 0) { $already = $hit[0].FullName }
                }
            }
        }
        if ($already) {
            Write-Step "  [>] Da co trong Output: $(Split-Path $already -Leaf) -> bo qua." 'DarkGray'
            Add-MuxLog $f.Name 'Skip-Exists' (Split-Path $already -Leaf) ''
            $skipCount++; continue
        }
    }

    # ---- Doc cau truc file ----
    $json = $null
    try {
        if ($script:JsonCache.ContainsKey($file)) { $json = $script:JsonCache[$file] }
        else {
            $raw = & $mkvmerge -J "$file" 2>&1
            if ($LASTEXITCODE -gt 1) { throw "mkvmerge exit $LASTEXITCODE" }
            $json = ($raw -join "`n") | ConvertFrom-Json
        }
    } catch {
        Write-Step "  [!] Khong doc duoc cau truc file (hong hoac khong ho tro)." 'Red'
        Add-MuxLog $f.Name 'FAIL' '' 'khong doc duoc cau truc file'
        $failCount++; continue
    }
    $tracks = @($json.tracks)

    $trackName = @{}   # id -> ten hien thi
    $trackLang = @{}   # id -> ma ngon ngu can ghi de (chi khi can doi)

    # ------------------------------------------------------
    #  BUOC 1: VIDEO
    # ------------------------------------------------------
    $videoIds = @()
    foreach ($t in $tracks | Where-Object { $_.type -eq 'video' }) {
        $videoIds += [int]$t.id
        $code = Get-LangCode $t.properties.language $t.properties.language_ietf
        $nm   = Get-LangName $code
        if ($nm) {
            $trackName[[int]$t.id] = $nm
            Write-Step "  [*] Video  #$($t.id): ten -> '$nm'" 'DarkCyan'
        }
    }

    # ------------------------------------------------------
    #  BUOC 2: AUDIO  (loc commentary, giu tieng goc, dat ten, chon default)
    # ------------------------------------------------------
    $audioTracks = @($tracks | Where-Object { $_.type -eq 'audio' })
    $keepAudio   = @()
    $audioBitrate= @{}
    $defAudioId  = -1
    $skipThisFile= $false

    if ($audioTracks.Count -eq 0) {
        Write-Step "  [!] File khong co audio track nao." 'Yellow'
    }
    else {
        $ffMap = Get-FfprobeBitrates $ffprobe $file

        # Gan ma ngon ngu + nhan dien commentary cho tung track audio
        $aAll = @()
        foreach ($t in $audioTracks) {
            $code = Get-LangCode $t.properties.language $t.properties.language_ietf
            $isCm = ($RemoveCommentaryAudio  -and (Test-CommentaryTrack $t.properties))
            $isDs = ($RemoveDescriptiveAudio -and (Test-DescriptiveTrack $t.properties))
            $why = ''
            if ($isCm) { $why = 'Commentary' } elseif ($isDs) { $why = 'Audio Description' }
            $aAll += [pscustomobject]@{ Track = $t; Id = [int]$t.id; Code = $code; Drop = ($isCm -or $isDs); Why = $why }
        }

        # --- Loai bo commentary / audio description TRUOC moi quy tac khac ---
        $dropList = @($aAll | Where-Object { $_.Drop })
        $aInfo    = @($aAll | Where-Object { -not $_.Drop })

        # An toan: neu loc het sach thi giu nguyen tat ca
        if ($aInfo.Count -eq 0 -and $dropList.Count -gt 0) {
            Write-Step "  [!] Tat ca audio deu bi nhan dien la binh luan -> giu nguyen toan bo cho an toan." 'Yellow'
            $aInfo = $aAll; $dropList = @()
        }
        foreach ($d in $dropList) {
            $nm = [string]$d.Track.properties.track_name
            Write-Step "  [-] Audio  #$($d.Id): XOA - $($d.Why)$(if($nm){" ('$nm')"})" 'DarkGray'
        }

        # --- Hoi nguoi dung muon xoa audio nao (commentary da bi xoa thang o tren) ---
        $aInfo = @(Select-AudioTracks $aInfo $ffMap)

        $undList = @($aInfo | Where-Object { $_.Code -eq 'und' })

        # --- Quy tac 'und' ---
        if ($undList.Count -gt 0) {
            if ($aInfo.Count -eq 1) {
                $only = $aInfo[0]
                $only.Code = 'en'
                $trackLang[$only.Id] = 'en'
                Write-Step "  [*] Audio #$($only.Id): ngon ngu 'und' + chi co 1 track -> dat thanh 'en'" 'Yellow'
            }
            elseif ($SkipOnAmbiguousUnd) {
                Write-Step "  [!] CANH BAO: file co $($aInfo.Count) audio track (sau khi loc), trong do $($undList.Count) track khong ro ngon ngu ('und')." 'Yellow'
                Write-Step "      Khong the doan tu dong -> BO QUA file nay, khong sua doi gi." 'Yellow'
                Write-Step "      -> Hay tu gan ngon ngu bang MKVToolNix GUI roi chay lai." 'DarkYellow'
                $skipThisFile = $true
            }
        }

        # --- Quy tac: nhieu hon 1 audio English -> khong tu quyet dinh duoc ---
        $enList = @($aInfo | Where-Object { $_.Code -eq 'en' })
        $userPicked = ($AskWhichAudio -and $audioTracks.Count -ge $AskAudioMinTracks)
        if (-not $skipThisFile -and $enList.Count -gt 1 -and $SkipOnMultipleEnAudio -and -not $userPicked) {
            Write-Step "  [!] CANH BAO: file co $($enList.Count) audio track English (#$(($enList.Id) -join ', #'))." 'Yellow'
            Write-Step "      Khong ro nen dat track nao lam Default -> BO QUA file nay, khong sua doi gi." 'Yellow'
            Write-Step "      -> Hay tu chon Default bang MKVToolNix GUI, hoac dat" 'DarkYellow'
            Write-Step "         `$SkipOnMultipleEnAudio = `$false de script tu lay track bitrate cao nhat." 'DarkYellow'
            $skipThisFile = $true
        }

        if (-not $skipThisFile) {
            # Giu toan bo audio goc CON LAI (phim Han/Nhat/Phap... khong mat tieng goc)
            foreach ($a in $aInfo) {
                $p        = $a.Track.properties
                $codecNm  = Get-CodecName $p
                $chanNm   = Get-ChannelName $p.audio_channels
                $langNm   = Get-LangName $a.Code
                $fullName = (($langNm, $codecNm, $chanNm) -join ' ').Trim() -replace '\s+', ' '
                $bps      = Get-AudioBitrate $a.Track $ffMap
                $audioBitrate[$a.Id] = $bps

                $keepAudio += $a.Id
                if ($fullName) { $trackName[$a.Id] = $fullName }
                Write-Step "  [+] Audio  #$($a.Id): GIU - $fullName  $(Format-Kbps $bps)" 'Green'
            }

            # Default audio: uu tien English; neu khong co thi lay track dau tien (tieng goc)
            if ($enList.Count -eq 1) {
                $defAudioId = $enList[0].Id
                Write-Step "  [*] Default Audio -> #$defAudioId (English, $(Format-Kbps $audioBitrate[$defAudioId]))" 'Cyan'
            }
            elseif ($enList.Count -gt 1) {
                $best = $enList | Sort-Object @{Expression={ $audioBitrate[$_.Id] }} -Descending | Select-Object -First 1
                $defAudioId = $best.Id
                Write-Step "  [*] Default Audio -> #$defAudioId (English bitrate cao nhat, $(Format-Kbps $audioBitrate[$defAudioId]))" 'Cyan'
            }
            elseif ($keepAudio.Count -gt 0) {
                $defAudioId = $keepAudio[0]
                Write-Step "  [*] Default Audio -> #$defAudioId (khong co English, giu tieng goc: $($trackName[$defAudioId]))" 'Cyan'
            }
        }
    }

    if ($skipThisFile) {
        Add-MuxLog $f.Name 'Skip-Audio' '' 'audio und hoac nhieu track English - can xu ly tay'
        $skipCount++; Write-Host ''; continue
    }

    # ------------------------------------------------------
    #  BUOC 3: SUBTITLE EXTERNAL  (quet TRUOC de biet ngon ngu nao bi thay the)
    # ------------------------------------------------------
    $extSubs = @()
    $candidates = @(Get-ChildItem -LiteralPath $f.DirectoryName -File | Where-Object {
        ($SubExtPriority -contains $_.Extension.ToLower()) -and
        ($_.BaseName.Length -ge $baseName.Length) -and
        ([string]::Equals($_.BaseName.Substring(0, $baseName.Length), $baseName, 'OrdinalIgnoreCase'))
    })

    $byLang = @{}
    foreach ($c in $candidates) {
        $suffix = $c.BaseName.Substring($baseName.Length)
        if ($suffix -match '(?i)forced' -and $RemoveForcedSubs) { continue }

        $code = 'und'
        if     ($suffix -match '(?i)(^|[._\- ])(vi|vie|viet|vietnamese)([._\- ]|$)') { $code = 'vi' }
        elseif ($suffix -match '(?i)(^|[._\- ])(cs|cz|cze|ces|czech)([._\- ]|$)')    { $code = 'cs' }
        elseif ($suffix -match '(?i)(^|[._\- ])(en|eng|english)([._\- ]|$)')         { $code = 'en' }
        elseif ($suffix.Trim() -eq '')                                              { $code = $DefaultExternalLang }
        else { continue }

        if ($SubKeepLangs -notcontains $code) { continue }
        $rank = $SubExtPriority.IndexOf($c.Extension.ToLower())
        if (-not $byLang.ContainsKey($code) -or $rank -lt $byLang[$code].Rank) {
            $byLang[$code] = [pscustomobject]@{ File = $c; Rank = $rank; Code = $code }
        }
    }
    foreach ($code in $SubDefaultPriority) {
        if ($byLang.ContainsKey($code)) { $extSubs += $byLang[$code] }
    }
    foreach ($k in $byLang.Keys) {
        if ($SubDefaultPriority -notcontains $k) { $extSubs += $byLang[$k] }
    }

    # Ngon ngu co file sub ngoai -> sub internal cung ngon ngu se bi loai bo
    $extLangs = @()
    if ($ExternalSubOverridesInternal) { $extLangs = @($extSubs | ForEach-Object { $_.Code }) }
    if ($extLangs.Count -gt 0) {
        Write-Step "  [~] Co sub ngoai ($(($extLangs | ForEach-Object { Get-LangName $_ }) -join ', ')) -> se thay the toan bo sub internal cung ngon ngu." 'DarkCyan'
    }

    # ------------------------------------------------------
    #  BUOC 4: SUBTITLE INTERNAL
    # ------------------------------------------------------
    $subTracks = @($tracks | Where-Object { $_.type -eq 'subtitles' })
    $keepSubs  = @()
    $subInfo   = @()

    # Co ban English "sach" nao khong (de quyet dinh xoa SDH)
    $hasCleanEng = $false
    foreach ($t in $subTracks) {
        $code = Get-LangCode $t.properties.language $t.properties.language_ietf
        $nm   = [string]$t.properties.track_name
        $sdh  = ($nm -match '(?i)\bSDH\b|hearing') -or ($t.properties.flag_hearing_impaired -eq $true)
        $frc  = ($nm -match '(?i)forced') -or ($t.properties.forced_track -eq $true) -or ($t.properties.flag_forced -eq $true)
        if ($code -eq 'en' -and -not $sdh -and -not $frc) { $hasCleanEng = $true; break }
    }

    foreach ($t in $subTracks) {
        $id   = [int]$t.id
        $p    = $t.properties
        $code = Get-LangCode $p.language $p.language_ietf
        $nm   = [string]$p.track_name
        $sdh  = ($nm -match '(?i)\bSDH\b|hearing') -or ($p.flag_hearing_impaired -eq $true)
        $frc  = ($nm -match '(?i)forced') -or ($p.forced_track -eq $true) -or ($p.flag_forced -eq $true)

        if ($SubKeepLangs -notcontains $code) {
            Write-Step "  [-] Sub    #$id : XOA - $(Get-LangName $code) (ngoai danh sach)" 'DarkGray'
            continue
        }
        if ($extLangs -contains $code) {
            Write-Step "  [-] Sub    #$id : XOA - $(Get-LangName $code) (da co file sub ngoai thay the)" 'Red'
            continue
        }
        if ($frc -and $RemoveForcedSubs) {
            Write-Step "  [-] Sub    #$id : XOA - Forced" 'Red'
            continue
        }
        if ($RemoveImageSubs -and (Test-ImageSub ([string]$p.codec_id))) {
            Write-Step "  [-] Sub    #$id : XOA - phu de dang anh ($($p.codec_id))" 'DarkGray'
            continue
        }
        if ($RemoveCommentarySubs -and (Test-CommentaryTrack $p)) {
            Write-Step "  [-] Sub    #$id : XOA - Commentary$(if($nm){" ('$nm')"})" 'DarkGray'
            continue
        }
        if ($code -eq 'en' -and $sdh -and $hasCleanEng) {
            Write-Step "  [-] Sub    #$id : XOA - English SDH (da co ban English thuong)" 'Red'
            continue
        }

        $label = Get-LangName $code
        if ($sdh) { $label = "$label SDH" }
        if ($frc) { $label = "$label Forced" }

        $keepSubs += $id
        $trackName[$id] = $label
        $subInfo += [pscustomobject]@{ Id = $id; Code = $code; Sdh = $sdh; Forced = $frc }
        $tag = if ($sdh -and -not $hasCleanEng) { 'Ve vot' } else { 'GIU' }
        Write-Step "  [+] Sub    #$id : $tag - $label" 'Green'
    }

    # ------------------------------------------------------
    #  BUOC 4b: AP DUNG LUA CHON TU BANG CHON PHU DE
    # ------------------------------------------------------
    if ($script:SubChoice -and $script:SubChoice.ContainsKey($file)) {
        $ch = $script:SubChoice[$file]
        $extSubs = @($extSubs | Where-Object { $ch.Ext -contains $_.Code })
        foreach ($sid in @($keepSubs)) { if ($trackName.ContainsKey($sid)) { $trackName.Remove($sid) } }
        $keepSubs = @(); $subInfo = @()
        foreach ($t in $subTracks) {
            $id = [int]$t.id
            if ($ch.Int -notcontains $id) { continue }
            $p    = $t.properties
            $code = Get-LangCode $p.language $p.language_ietf
            $nm   = [string]$p.track_name
            $sdh  = ($nm -match '(?i)\bSDH\b|hearing') -or ($p.flag_hearing_impaired -eq $true)
            $frc  = ($nm -match '(?i)forced') -or ($p.forced_track -eq $true) -or ($p.flag_forced -eq $true)
            $label = Get-LangName $code
            if ($sdh) { $label = "$label SDH" }
            if ($frc) { $label = "$label Forced" }
            $keepSubs += $id
            if ($label) { $trackName[$id] = $label.Trim() }
            $subInfo += [pscustomobject]@{ Id = $id; Code = $code; Sdh = $sdh; Forced = $frc }
        }
        $txt = @()
        foreach ($e in $extSubs) { $txt += "$(Get-LangName $e.Code) (ngoai)" }
        foreach ($si in $subInfo) { $txt += "$($trackName[$si.Id]) (#$($si.Id))" }
        $shown = if ($txt.Count -gt 0) { $txt -join ', ' } else { 'khong giu phu de nao' }
        Write-Step "  [#] THEO BANG CHON -> giu: $shown" 'Magenta'
    }

    # ------------------------------------------------------
    #  BUOC 5: CHON DEFAULT SUBTITLE (uu tien vi > cs > en)
    # ------------------------------------------------------
    $defSubKind = ''; $defSubId = -1; $defSubExtIdx = -1
    foreach ($code in $SubDefaultPriority) {
        $extIdx = -1
        for ($i = 0; $i -lt $extSubs.Count; $i++) { if ($extSubs[$i].Code -eq $code) { $extIdx = $i; break } }
        if ($extIdx -ge 0) { $defSubKind = 'ext'; $defSubExtIdx = $extIdx; break }

        $cand = @($subInfo | Where-Object { $_.Code -eq $code -and -not $_.Sdh })
        if ($cand.Count -eq 0) { $cand = @($subInfo | Where-Object { $_.Code -eq $code }) }
        if ($cand.Count -gt 0) { $defSubKind = 'int'; $defSubId = $cand[0].Id; break }
    }

    # ------------------------------------------------------
    #  BUOC 5b: GAN THE CODEC VAO TEN FILE
    # ------------------------------------------------------
    # Chi gan vao TEN FILE. Ten thu muc giu sach de Jellyfin gom cac ban encode
    # khac nhau cua cung mot phim thanh mot muc (chon phien ban bang menu).
    if ($AddCodecTag) {
        $isEp = $null -ne (Split-EpisodeName $baseName)
        if ($isEp -and -not $CodecTagForTv) {
            Write-Step "  [i] Phim bo -> khong gan the codec (`$CodecTagForTv = `$false)." 'DarkGray'
        } else {
            $defTrack = $null
            if ($defAudioId -ge 0) {
                $defTrack = @($tracks | Where-Object { [int]$_.id -eq $defAudioId })[0]
            }
            $tag = Get-CodecTag $json $file $ffprobe $baseName $defTrack
            if ($tag) {
                $outBase = "$outBase - $tag"
                $outName = Join-Path (Split-Path $outName -Parent) "$outBase.mkv"
                Write-Step "  [#] The codec: $tag" 'Magenta'
            }
        }
    }

    # ------------------------------------------------------
    #  BUOC 5c: GHEP AUDIO TU FILE PHIM KHAC
    # ------------------------------------------------------
    $haveLangs = @()
    foreach ($a in @($tracks | Where-Object { $_.type -eq 'audio' -and $keepAudio -contains [int]$_.id })) {
        $lc = if ($trackLang.ContainsKey([int]$a.id)) { $trackLang[[int]$a.id] } else { Get-LangCode $a.properties.language $a.properties.language_ietf }
        $haveLangs += $lc
    }
    $donorPlans = @(Get-DonorPlan $f $json $haveLangs)
    $donorDefault = ($DonorSetDefault -and $donorPlans.Count -gt 0)

    # ------------------------------------------------------
    #  BUOC 6: RAP LENH MKVMERGE
    # ------------------------------------------------------
    $cmd = @('-o', $outName)
    if ($SetTitleFromName) { $cmd += @('--title', $outBase) }

    # -- Audio: chi liet ke khi thuc su co track bi loai bo (commentary...) --
    if ($audioTracks.Count -gt 0 -and $keepAudio.Count -gt 0 -and $keepAudio.Count -lt $audioTracks.Count) {
        $cmd += @('--audio-tracks', ($keepAudio -join ','))
    }
    # -- Sub internal: chon track --
    if ($subTracks.Count -gt 0) {
        if ($keepSubs.Count -eq 0) { $cmd += '--no-subtitles' }
        elseif ($keepSubs.Count -lt $subTracks.Count) { $cmd += @('--subtitle-tracks', ($keepSubs -join ',')) }
    }

    # -- Ghi de ngon ngu (truong hop und -> en) --
    foreach ($id in $trackLang.Keys) { $cmd += @('--language', "$($id):$(Get-LangForMux $trackLang[$id])") }

    # -- Dat ten cho cac track ĐƯỢC GIỮ --
    $keptAll = @($videoIds) + @($keepAudio) + @($keepSubs)
    foreach ($id in $keptAll) {
        if ($trackName.ContainsKey($id) -and $trackName[$id]) {
            $cmd += @('--track-name', "$($id):$($trackName[$id])")
        }
    }

    # -- Xoa/dat co Default & Forced tuong minh cho MOI track giu lai --
    foreach ($id in $keepAudio) {
        $v = if ($id -eq $defAudioId -and -not $donorDefault) { 1 } else { 0 }
        $cmd += @($F_DEF, "$($id):$v")
    }
    foreach ($s in $subInfo) {
        $v = if ($defSubKind -eq 'int' -and $s.Id -eq $defSubId) { 1 } else { 0 }
        $cmd += @($F_DEF, "$($s.Id):$v")
        $fv = if ($s.Forced) { 1 } else { 0 }
        $cmd += @($F_FOR, "$($s.Id):$fv")
    }

    $cmd += $file

    # -- Them cac file sub ngoai --
    for ($i = 0; $i -lt $extSubs.Count; $i++) {
        $e   = $extSubs[$i]
        $lbl = Get-LangName $e.Code
        $isD = if ($defSubKind -eq 'ext' -and $i -eq $defSubExtIdx) { 1 } else { 0 }

        # Bao dung bang ma neu file khong phai UTF-8, tranh chu co dau bi vo (lo hong 1)
        $cs = Get-SubCharsetArg $e.File.FullName $e.Code
        if ($cs) {
            $cmd += @('--sub-charset', "0:$cs")
            Write-Step "  [enc] $($e.File.Name): khong phai UTF-8 -> doc bang $cs" 'Yellow'
        }

        $cmd += @('--language', "0:$(Get-LangForMux $e.Code)", '--track-name', "0:$lbl",
                  $F_DEF, "0:$isD", $F_FOR, '0:0', $e.File.FullName)
        $mark = if ($isD -eq 1) { ' [*Default]' } else { '' }
        Write-Step "  [+] Sub ngoai: $lbl ($($e.File.Extension.TrimStart('.').ToUpper()))$mark" 'Magenta'
    }

    # -- Them audio ghep tu file phim khac (moi file nguon la mot input rieng) --
    $donorBase = 1 + $extSubs.Count
    for ($k = 0; $k -lt $donorPlans.Count; $k++) {
        $dp = $donorPlans[$k]; $tid = $dp.Tid
        $isD = if ($donorDefault -and $k -eq 0) { 1 } else { 0 }
        $cmd += @('--no-video', '--no-subtitles', '--no-chapters', '--no-attachments', '--no-global-tags',
                  '--audio-tracks', "$tid",
                  '--language', "$($tid):$(Get-LangForMux $dp.Code)", '--track-name', "$($tid):$($dp.Name)",
                  $F_DEF, "$($tid):$isD", $F_FOR, "$($tid):0")
        if ($dp.OffsetMs -ne 0) { $cmd += @('--sync', "$($tid):$($dp.OffsetMs)") }
        $cmd += $dp.Path
    }

    if ($defSubKind -eq 'int') {
        $dsc = @($subInfo | Where-Object { $_.Id -eq $defSubId })[0].Code
        Write-Step "  [*] Default Sub -> track internal #$defSubId ($(Get-LangName $dsc))" 'Cyan'
    } elseif ($defSubKind -eq '') {
        Write-Step "  [*] Default Sub -> khong co sub nao" 'DarkGray'
    }

    # -- Thu tu track trong file dich --
    if ($UseTrackOrder) {
        $order = @()
        foreach ($v in $videoIds) { $order += "0:$v" }
        $donorOrder = @()
        for ($k = 0; $k -lt $donorPlans.Count; $k++) { $donorOrder += "$($donorBase + $k):$($donorPlans[$k].Tid)" }
        if ($donorDefault) { $order += $donorOrder }
        if ($defAudioId -ge 0) { $order += "0:$defAudioId" }
        foreach ($a in $keepAudio) { if ($a -ne $defAudioId) { $order += "0:$a" } }
        if (-not $donorDefault) { $order += $donorOrder }
        if ($defSubKind -eq 'ext') { $order += "$($defSubExtIdx + 1):0" }
        elseif ($defSubKind -eq 'int') { $order += "0:$defSubId" }
        foreach ($code in $SubDefaultPriority) {
            for ($i = 0; $i -lt $extSubs.Count; $i++) {
                if ($extSubs[$i].Code -eq $code -and -not ($defSubKind -eq 'ext' -and $i -eq $defSubExtIdx)) {
                    $order += "$($i + 1):0"
                }
            }
            foreach ($s in $subInfo) {
                if ($s.Code -eq $code -and -not ($defSubKind -eq 'int' -and $s.Id -eq $defSubId)) {
                    $order += "0:$($s.Id)"
                }
            }
        }
        foreach ($s in $subInfo) {
            if ($SubDefaultPriority -notcontains $s.Code -and $order -notcontains "0:$($s.Id)") { $order += "0:$($s.Id)" }
        }
        if ($order.Count -gt 0) { $cmd = @($cmd[0], $cmd[1], '--track-order', ($order -join ',')) + $cmd[2..($cmd.Count - 1)] }
    }

    # ------------------------------------------------------
    #  BUOC 7: THUC THI + KIEM TRA KET QUA THAT SU
    # ------------------------------------------------------
    if ($DryRun) {
        Write-Step "  [DRY-RUN] Khong ghi file. Lenh se chay:" 'Magenta'
        Write-Step "     mkvmerge $([string]::Join(' ', ($cmd | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } })))" 'DarkGray'
        Add-MuxLog $f.Name 'DryRun' $outBase ''
        Remove-DonorTemp
        $skipCount++
        Write-Host ''
        continue
    }

    $log = & $mkvmerge @cmd 2>&1
    $code = $LASTEXITCODE
    Remove-DonorTemp

    if ($code -eq 0) {
        Write-Step "  -> THANH CONG. Da luu: $outName" 'DarkYellow'
        $dInfo = if ($donorPlans.Count -gt 0) { " ghep=" + (($donorPlans | ForEach-Object { "$($_.Code)@$($_.OffsetMs)ms" }) -join ',') } else { '' }
        Add-MuxLog $f.Name 'OK' $outBase "audio giu=$($keepAudio -join ',') sub giu=$($keepSubs -join ',')$dInfo"
        $okCount++
    }
    elseif ($code -eq 1) {
        Write-Step "  -> XONG (co canh bao):" 'Yellow'
        $warns = @($log | Where-Object { $_ -match '(?i)warning' })
        $warns | Select-Object -First 5 | ForEach-Object { Write-Step "     $_" 'DarkYellow' }
        Add-MuxLog $f.Name 'OK-Warning' $outBase (($warns | Select-Object -First 3) -join ' | ')
        $okCount++
    }
    else {
        Write-Step "  [!] THAT BAI (exit code $code):" 'Red'
        $errs = @($log | Where-Object { $_ -match '(?i)error' })
        $errs | Select-Object -First 8 | ForEach-Object { Write-Step "     $_" 'Red' }
        Add-MuxLog $f.Name 'FAIL' $outBase (($errs | Select-Object -First 3) -join ' | ')
        if (Test-Path -LiteralPath $outName) { Remove-Item -LiteralPath $outName -Force -ErrorAction SilentlyContinue }
        $failCount++
    }
    Write-Host ''
}

Write-Step ("=" * 62) 'DarkCyan'
Write-Step "TONG KET:  Thanh cong: $okCount  |  Bo qua: $skipCount  |  Loi: $failCount" 'White'
$mlog = Save-MuxLog $targetDir
if ($mlog) { Write-Step "Nhat ky day du: $LogDirName\$(Split-Path $mlog -Leaf)" 'DarkCyan' }
if ($DryRun) { Write-Step "*** DANG O CHE DO DRY-RUN - khong file nao duoc ghi. Dat `$DryRun = `$false de chay that. ***" 'Magenta' }
