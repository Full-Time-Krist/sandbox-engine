<#
Помощники для проверки игры вживую: запуск, имитация ввода, скриншоты, лог.
Подключение (dot-source) в начале одного вызова PowerShell — каждый вызов идёт в новом процессе:

    . D:\Projects\test\tools\playtest.ps1
    $game = Start-Game                       # Debug-сборка; -Configuration Release для замеров; -Arguments '--seed 2'
                                             # мир 'playtest', каждый раз новый; -KeepWorld — загрузить сохранённый
    Enter-Game $game                         # клик в центр окна: захват мыши
    Hold-Key $game 0x57 1000                 # W одну секунду; Hold-Keys $game @(0x57, 0x20) 1500 — W и Space вместе (коды: https://learn.microsoft.com/windows/win32/inputdev/virtual-key-codes)
    Move-Mouse $game 0 10 30                 # 30 шагов по 10 px вниз
    Scroll-Mouse $game -1                    # колесо на одно деление: +1 — от себя, -1 — на себя
    Resize-Game $game 800 600                # размер кадра (без рамки и заголовка), например для масштаба интерфейса
    Save-Screenshot $game "$env:TEMP\shot.png" -Half
    Save-ClientScreenshot $game "$env:TEMP\frame.png"  # только кадр, пиксель в пиксель: проверять чёткость интерфейса
    Get-GameTitle $game                      # заголовок: FPS, TPS, строка состояния
    Stop-Game $game                          # закрыть окно и вывести лог (stdout/stderr, UTF-8)

Скриншот снимает экран, поэтому окно игры должно быть видно и не перекрыто.
#>

Add-Type -AssemblyName System.Drawing
if (-not ('PlaytestNative' -as [type])) {
    Add-Type @'
using System; using System.Runtime.InteropServices;
public static class PlaytestNative {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out Rect r);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out Rect r);
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref Point p);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int height, bool repaint);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
}
'@
}

$PlaytestRoot = Split-Path -Parent $PSScriptRoot

# Мир: по умолчанию отдельный 'playtest', а не мир игрока. Перед запуском он удаляется (чистый мир с сидом из
# -Arguments или 1), с -KeepWorld — загружается сохранённый (проверка сохранений).
function Start-Game([string]$Configuration = 'Debug', [int]$WaitSeconds = 3, [string]$Arguments = '',
        [string]$World = 'playtest', [switch]$KeepWorld) {
    $exe = Join-Path $PlaytestRoot "src\Client\bin\$Configuration\net10.0\Sandbox.exe"
    if (-not (Test-Path $exe)) {
        # Имя exe берётся из GameName в Directory.Build.props; если его переименовали — ищем любой exe клиента.
        $exe = Get-ChildItem (Join-Path $PlaytestRoot "src\Client\bin\$Configuration\net10.0") -Filter *.exe | Select-Object -First 1 -ExpandProperty FullName
    }
    # Сохранения лежат в %LOCALAPPDATA%\<название игры>\Saves\<мир>; название игры — имя exe.
    $worldDir = Join-Path $env:LOCALAPPDATA "$([IO.Path]::GetFileNameWithoutExtension($exe))\Saves\$World"
    if (-not $KeepWorld -and (Test-Path $worldDir)) { Remove-Item -Recurse -Force $worldDir }
    if ($Arguments -notmatch '--seed') { $Arguments = "--seed 1 $Arguments" }
    $Arguments = "--world $World $Arguments"
    $log = Join-Path $env:TEMP "playtest-$PID"
    New-Item -ItemType Directory -Force $log | Out-Null
    $extra = @{}
    if ($Arguments) { $extra.ArgumentList = $Arguments }
    $process = Start-Process -FilePath $exe -WorkingDirectory $env:TEMP -PassThru @extra `
        -RedirectStandardOutput "$log\out.txt" -RedirectStandardError "$log\err.txt"
    Start-Sleep -Seconds $WaitSeconds
    $process.Refresh()
    if ($process.HasExited) { throw "Игра завершилась при запуске, код $($process.ExitCode). $(Get-Content "$log\err.txt" -Raw)" }
    [PlaytestNative]::SetForegroundWindow($process.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 300
    [pscustomobject]@{ Process = $process; Log = $log }
}

function Get-WindowRect($game) {
    $rect = New-Object PlaytestNative+Rect
    [PlaytestNative]::GetWindowRect($game.Process.MainWindowHandle, [ref]$rect) | Out-Null
    $rect
}

function Enter-Game($game) {
    $r = Get-WindowRect $game
    [PlaytestNative]::SetCursorPos(($r.Left + $r.Right) / 2, ($r.Top + $r.Bottom) / 2) | Out-Null
    Start-Sleep -Milliseconds 200
    Click-Mouse $game Left
}

function Click-Mouse($game, [ValidateSet('Left', 'Right')][string]$Button = 'Left') {
    $down, $up = if ($Button -eq 'Left') { 0x2, 0x4 } else { 0x8, 0x10 }
    [PlaytestNative]::mouse_event($down, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 30
    [PlaytestNative]::mouse_event($up, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 300
}

function Hold-Key($game, [byte]$VirtualKey, [int]$Milliseconds = 50) {
    [PlaytestNative]::keybd_event($VirtualKey, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds $Milliseconds
    [PlaytestNative]::keybd_event($VirtualKey, 0, 2, [IntPtr]::Zero); Start-Sleep -Milliseconds 200
}

# Несколько клавиш одновременно, например W + Space (разбег с прыжком): Hold-Keys $game @(0x57, 0x20) 1500
function Hold-Keys($game, [byte[]]$VirtualKeys, [int]$Milliseconds = 50) {
    foreach ($k in $VirtualKeys) { [PlaytestNative]::keybd_event($k, 0, 0, [IntPtr]::Zero) }
    Start-Sleep -Milliseconds $Milliseconds
    foreach ($k in $VirtualKeys) { [PlaytestNative]::keybd_event($k, 0, 2, [IntPtr]::Zero) }
    Start-Sleep -Milliseconds 200
}

# Относительное движение мыши: в захваченном режиме игра получает его как сырой ввод.
function Move-Mouse($game, [int]$Dx, [int]$Dy, [int]$Steps = 1) {
    for ($i = 0; $i -lt $Steps; $i++) { [PlaytestNative]::mouse_event(0x1, $Dx, $Dy, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 15 }
    Start-Sleep -Milliseconds 300
}

function Scroll-Mouse($game, [int]$Notches) {
    # Деление колеса — 120 единиц; mouse_event принимает их как uint, поэтому отрицательное число передаётся его битами.
    $data = [BitConverter]::ToUInt32([BitConverter]::GetBytes([int](120 * $Notches)), 0)
    [PlaytestNative]::mouse_event(0x800, 0, 0, $data, [IntPtr]::Zero); Start-Sleep -Milliseconds 300
}

# Задаёт размер кадра: рамку и заголовок окна добавляет сама, левый верхний угол окна остаётся на месте.
function Resize-Game($game, [int]$Width, [int]$Height) {
    $handle = $game.Process.MainWindowHandle
    $outer = Get-WindowRect $game
    $client = New-Object PlaytestNative+Rect
    [PlaytestNative]::GetClientRect($handle, [ref]$client) | Out-Null
    $frameWidth = ($outer.Right - $outer.Left) - $client.Right
    $frameHeight = ($outer.Bottom - $outer.Top) - $client.Bottom
    [PlaytestNative]::MoveWindow($handle, $outer.Left, $outer.Top, $Width + $frameWidth, $Height + $frameHeight, $true) | Out-Null
    Start-Sleep -Milliseconds 700
}

# Кадр без рамки окна: клиентская область в координатах экрана (в рамку Windows 10 входят невидимые края).
function Save-ClientScreenshot($game, [string]$Path) {
    $handle = $game.Process.MainWindowHandle
    $client = New-Object PlaytestNative+Rect
    [PlaytestNative]::GetClientRect($handle, [ref]$client) | Out-Null
    $origin = New-Object PlaytestNative+Point
    [PlaytestNative]::ClientToScreen($handle, [ref]$origin) | Out-Null
    $bitmap = New-Object System.Drawing.Bitmap $client.Right, $client.Bottom
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($origin.X, $origin.Y, 0, 0, $bitmap.Size)
    $bitmap.Save($Path)
}

function Save-Screenshot($game, [string]$Path, [switch]$Half) {
    $r = Get-WindowRect $game
    $bitmap = New-Object System.Drawing.Bitmap ($r.Right - $r.Left), ($r.Bottom - $r.Top)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($r.Left, $r.Top, 0, 0, $bitmap.Size)
    if ($Half) { $bitmap = New-Object System.Drawing.Bitmap $bitmap, ([int]($bitmap.Width / 2)), ([int]($bitmap.Height / 2)) }
    $bitmap.Save($Path)
}

function Get-GameTitle($game) {
    $game.Process.Refresh()
    $game.Process.MainWindowTitle
}

function Stop-Game($game) {
    $alive = -not $game.Process.HasExited
    # При выходе игра дописывает сохранение: ждём завершения, а не фиксированную секунду.
    if ($alive) { $game.Process.CloseMainWindow() | Out-Null; $game.Process.WaitForExit(15000) | Out-Null }
    "alive до закрытия: $alive; завершилась: $($game.Process.HasExited)"
    '--- stdout ---'; Get-Content "$($game.Log)\out.txt" -Encoding UTF8
    '--- stderr ---'; Get-Content "$($game.Log)\err.txt" -Encoding UTF8
}
